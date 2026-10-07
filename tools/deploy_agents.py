"""Deploy the DealOS agents to Dataverse (idempotent).

Steps:
  1. gc_secret table (org-owned, no privileges for normal roles) + 'gemini.api_key' / 'openai.api_key' rows from .env
  2. 'Document Intelligence' option on the global choice gc_agent
  3. Plug-in assembly DealOS.Agents + plug-in type DealOS.Agents.AgentPlugin
  4. One Custom API per agent (gc_Agent_<Name>) with request parameters and response properties
  5. Agent settings in gc_platformsetting (created only when missing)
  6. Deterministic operations (plug-in type DealOS.Agents.Operations.OperationsPlugin):
     gc_AcceptOffer, gc_ReleaseDeal, plus the gc_deal.gc_requirement lookup
  7. Retires removed pieces from Dev if still there (website chat agents and plug-ins, marketplace agents, escrow operations; 7 Oct 2026)
  8. Email Desk operations (plug-in type DealOS.Agents.Mail.MailPlugin): gc_IngestEmail, gc_AttachEmailFile
Everything is created inside the DealOS solution.

Usage:
  dotnet build -c Release src/DealOS.Agents
  dotnet run --project tests/DealOS.Agents.Harness -- build/agents
  python3 tools/deploy_agents.py
"""
import base64, json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOLUTION = "DealOS"
DLL = os.path.join(ROOT, "src", "DealOS.Agents", "bin", "Release", "net462", "DealOS.Agents.dll")
MANIFEST = os.path.join(ROOT, "build", "agents", "agents.manifest.json")
SOL = {"MSCRM.SolutionUniqueName": SOLUTION}

SETTINGS = [  # key, value, value type label, description
    ("agents.provider", "openai", "Text", "AI provider of the agents: openai (gc_secret openai.api_key, models agents.openai.model.*) or gemini (gemini.api_key, agents.model.*). "
                                          "Switch with: python3 tools/deploy_agents.py --provider openai|gemini"),
    ("agents.openai.model.default", "gpt-5.4-mini", "Text", "Default OpenAI model for agents (Responses API)."),
    ("agents.openai.model.document", "gpt-5.4-mini", "Text", "OpenAI model for Document Intelligence (PDF/image extraction)."),
    ("agents.openai.model.discovery", "gpt-5.4-mini", "Text", "OpenAI model for web discovery of sellers and buyers (web_search tool)."),
    ("agents.openai.model.fallbacks", "gpt-4.1-mini", "Text", "OpenAI models tried in order on rate limit (429), overload (503), unknown model (404) or timeout."),
    ("agents.openai.reasoning_effort", "low", "Text", "Reasoning effort for gpt-5 / o-series models: none, low, medium or high."),
    ("agents.model.default", "gemini-3.5-flash", "Text", "Default Gemini model for agents."),
    ("agents.model.document", "gemini-3.5-flash", "Text", "Gemini model for Document Intelligence (PDF/image extraction)."),
    ("agents.model.fallbacks", "gemini-3.1-flash-lite,gemini-3.8-flash", "Text", "Models tried in order on quota (429), overload (503), retired model (404) or timeout."),
    ("agents.time_budget_seconds", "100", "Number", "Max seconds per agent run (plug-in limit is 120)."),
    ("agents.call_timeout_seconds", "45", "Number", "Max seconds for one model call before trying the next model."),
    ("agents.pricing", "{}", "Json", "USD per 1M tokens per model prefix, e.g. {\"gemini-3.5-flash\":[in,out]}; empty = cost not recorded."),
    ("agents.generation_config", "", "Json", "Optional extra Gemini generationConfig merged into every call (e.g. thinkingConfig)."),
    ("admin.digest.recipients", "", "Text", "Semicolon-separated emails for the daily AdminSupervisor digest; empty = the flow owner's mailbox."),
    ("email.enabled", "false", "Bool", "true = the Mailbox sync flow reads Gmail and triages new email; false = it does nothing."),
    ("email.sync.query", "to:{user}+dealos@{domain} newer_than:7d", "Text",
     "Gmail search for mail the desk reads; {user} and {domain} are filled from the connected mailbox (labels DealOS/Processed and "
     "DealOS/Error are always excluded). Testing: only mail to the +dealos alias. Live: 'in:inbox newer_than:7d'."),
    ("email.sync.max_per_run", "5", "Number", "Emails the Mailbox sync flow handles per run (Gemini free tier: keep it small)."),
    ("email.self_addresses", "", "Text", "Extra addresses of ours (aliases), separated by semicolons; the connected mailbox is detected automatically. "
     "Mail from them is recorded as Outbound."),
    ("email.triage.proceed", "65", "Number", "Triage score from which a trade email is Genuine."),
    ("email.triage.ignore", "30", "Number", "Triage score below which a trade email is Ignored (between the two: Review)."),
    ("email.attachments.max_mb", "10", "Number", "Largest attachment the desk stores (PDF, images, Office, text)."),
    ("email.reply_to", "{user}+dealos@{domain}", "Text", "Reply-To on desk emails ({user}/{domain} from the connected mailbox), so answers reach the address the "
     "desk reads while testing; empty = no Reply-To (live, when email.sync.query reads the whole inbox)."),
    ("email.signature", "Best regards,\\nTrade Desk\\nGigacore Energy Pvt Ltd", "Text", "Signature added under every desk email (\\n = new line)."),
    ("email.sourcing.max_sellers", "5", "Number", "Sellers the desk contacts per requirement and sourcing round."),
    ("email.autosend", "off", "Text", "off = every desk email waits in Gmail Drafts for a person; routine = enquiries to sellers and replies without "
     "prices go out by themselves; all = everything except contracts goes out by itself. Briefings to us are always sent."),
    ("desk.owner_email", "{mailbox}", "Text", "Who gets the desk briefings ({mailbox} = the connected mailbox; 'off' = none)."),
    ("email.discovery.enabled", "true", "Bool", "Web discovery of sellers (per requirement) and buyers (per lot); the end-to-end tests switch it off so real companies get no test drafts."),
    ("email.discovery.max", "8", "Number", "Companies the web seller discovery looks for per requirement."),
    ("trade.margin_percent", "3", "Number", "Our margin (back to back): price to buyer = seller price × (1 + margin/100). Decision pending; 3 is a placeholder."),
    ("trade.company_name", "Gigacore Energy Pvt Ltd", "Text", "Our legal name on contracts."),
    ("trade.governing_law", "laws of India; disputes by arbitration in Mumbai under the Arbitration and Conciliation Act, 1996", "Text",
     "Governing law and disputes clause of the contract template (confirm with counsel)."),
    ("trade.bid_window_hours", "24", "Number", "Seller lots: default hours buyers can bid when the seller's email does not say (0 = open-ended: bids go to the seller, who decides). Never past the seller's own validity."),
    ("email.marketing.max_buyers", "5", "Number", "Seller lots: buyers a new lot is offered to (open requirements first, then buyer leads)."),
    ("desk.chase_after_hours", "48", "Number", "Hours without an answer before one chaser draft to a seller (our enquiry) or a buyer (our offer)."),
]
VALUE_TYPES = {"Bool": 303300000, "Number": 303300001, "Text": 303300002, "Json": 303300003}


def ok(status, body, what):
    if status >= 300:
        sys.exit(f"FAILED {what}: {status} {json.dumps(body)[:1500]}")
    return body


def label(text):
    return {"@odata.type": "Microsoft.Dynamics.CRM.Label",
            "LocalizedLabels": [{"@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel", "Label": text, "LanguageCode": 1033}]}


def env_key(name="GEMINI_API_KEY"):
    """A key from .env, or None. Team members without the key deploy everything else and leave the stored secret as it is."""
    path = os.path.join(ROOT, ".env")
    if os.path.exists(path):
        with open(path) as f:
            for line in f:
                if line.startswith(name + "="):
                    return line.split("=", 1)[1].strip() or None
    return None


def set_provider(provider):
    """agents.provider = openai | gemini (the stored value; ensure_settings never overwrites it)."""
    rows = ok(*dv.get("gc_platformsettings?$select=gc_platformsettingid&$filter=gc_key eq 'agents.provider'"), "read setting").get("value", [])
    if rows:
        ok(*dv.patch(f"gc_platformsettings({rows[0]['gc_platformsettingid']})", {"gc_value": provider}), "update agents.provider")
    print(f"* agents.provider = {provider}")


def ensure_secret_table():
    s, _ = dv.get("EntityDefinitions(LogicalName='gc_secret')?$select=LogicalName")
    if s == 200:
        print("= gc_secret table exists")
        return
    body = {
        "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
        "SchemaName": "gc_Secret",
        "DisplayName": label("Secret"),
        "DisplayCollectionName": label("Secrets"),
        "Description": label("Platform secrets read only by DealOS plug-ins as SYSTEM. Grant no privileges to user roles."),
        "OwnershipType": "OrganizationOwned",
        "HasActivities": False, "HasNotes": False, "IsActivity": False,
        "IsAuditEnabled": {"Value": True, "CanBeChanged": True},
        "Attributes": [{
            "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
            "SchemaName": "gc_Name", "IsPrimaryName": True, "MaxLength": 100,
            "RequiredLevel": {"Value": "ApplicationRequired", "CanBeChanged": True},
            "DisplayName": label("Name"), "Description": label("Secret name, e.g. gemini.api_key")}],
    }
    ok(*dv.request("POST", "EntityDefinitions", body, SOL), "create gc_secret")
    body = {"@odata.type": "Microsoft.Dynamics.CRM.MemoAttributeMetadata", "SchemaName": "gc_Value", "MaxLength": 4000,
            "Format": "TextArea", "RequiredLevel": {"Value": "None", "CanBeChanged": True},
            "DisplayName": label("Value"), "Description": label("Secret value")}
    ok(*dv.request("POST", "EntityDefinitions(LogicalName='gc_secret')/Attributes", body, SOL), "create gc_secret.gc_value")
    print("+ gc_secret table created")


def upsert_secret(name, value):
    rows = ok(*dv.get(f"gc_secrets?$select=gc_secretid&$filter=gc_name eq '{name}'"), "read secret").get("value", [])
    if rows:
        ok(*dv.patch(f"gc_secrets({rows[0]['gc_secretid']})", {"gc_value": value}), "update secret")
        print(f"= secret {name} updated")
    else:
        ok(*dv.post("gc_secrets", {"gc_name": name, "gc_value": value}), "create secret")
        print(f"+ secret {name} stored")


def ensure_agent_option():
    b = ok(*dv.get("GlobalOptionSetDefinitions(Name='gc_agent')"), "read gc_agent")
    if any(o["Value"] == 303300011 for o in b["Options"]):
        print("= gc_agent option Document Intelligence exists")
        return
    ok(*dv.request("POST", "InsertOptionValue", {"OptionSetName": "gc_agent", "Value": 303300011, "Label": label("Document Intelligence"),
                                                 "SolutionUniqueName": SOLUTION}), "insert option")
    ok(*dv.request("POST", "PublishXml", {"ParameterXml": "<importexportxml><optionsets><optionset>gc_agent</optionset></optionsets></importexportxml>"}), "publish option")
    print("+ gc_agent option Document Intelligence added")


def upsert_assembly():
    """Returns {type name: plugintypeid} for the agent and operations plug-in types."""
    with open(DLL, "rb") as f:
        content = base64.b64encode(f.read()).decode()
    s = ok(*dv.get("pluginassemblies?$select=pluginassemblyid&$filter=name eq 'DealOS.Agents'"), "read assembly")
    rows = s.get("value", [])
    if rows:
        aid = rows[0]["pluginassemblyid"]
        ok(*dv.patch(f"pluginassemblies({aid})", {"content": content, "version": "1.0.0.0"}), "update assembly")
        print("= plug-in assembly updated")
    else:
        b = ok(*dv.request("POST", "pluginassemblies", {
            "name": "DealOS.Agents", "content": content, "isolationmode": 2, "sourcetype": 0, "version": "1.0.0.0",
            "culture": "neutral", "description": "DealOS AI agents exposed as gc_Agent_* Custom APIs"}, SOL), "create assembly")
        aid = b["pluginassemblyid"]
        print("+ plug-in assembly registered")
    types = {}
    for typename, friendly, desc in [("DealOS.Agents.AgentPlugin", "AgentPlugin", "Runs a DealOS agent"),
                                     ("DealOS.Agents.Operations.OperationsPlugin", "OperationsPlugin", "Deterministic deal operations (accept offer, open escrow)"),
                                     ("DealOS.Agents.Mail.MailPlugin", "MailPlugin", "Email Desk: stores Gmail messages and attachments")]:
        s = ok(*dv.get(f"plugintypes?$select=plugintypeid&$filter=typename eq '{typename}' and _pluginassemblyid_value eq {aid}"), "read type")
        rows = s.get("value", [])
        if rows:
            types[typename] = rows[0]["plugintypeid"]
            continue
        b = ok(*dv.request("POST", "plugintypes", {
            "typename": typename, "friendlyname": friendly, "name": typename,
            "description": desc, "pluginassemblyid@odata.bind": f"/pluginassemblies({aid})"}, SOL), "create type " + typename)
        print(f"+ plug-in type {typename} registered")
        types[typename] = b["plugintypeid"]
    return types


REQUEST = [  # name, type (0 Boolean, 10 String), description, optional
    ("SubjectId", 10, "GUID of the record the agent works on.", None),
    ("Input", 10, "Optional JSON object with extra instructions or data, e.g. {\"perspective\":\"buyer\",\"limits\":{\"max_price\":9800}}.", True),
    ("DryRun", 0, "True: run the agent but do not write records (actions are reported only).", True),
]
RESPONSE = [
    ("AgentRunId", 10, "gc_agentrun id for this run."),
    ("Status", 10, "Succeeded or Failed."),
    ("Summary", 10, "Short human-readable summary."),
    ("NeedsHuman", 0, "True when a person must act before the process continues."),
    ("Result", 10, "Full JSON result: agent output, actions taken, model, tokens, timing."),
]


def upsert_api(agent, type_id):
    api = agent["api"]
    s = ok(*dv.get(f"customapis?$select=customapiid&$filter=uniquename eq '{api}'"), "read api")
    rows = s.get("value", [])
    desc = agent["description"][:300]
    if rows:
        cid = rows[0]["customapiid"]
        ok(*dv.patch(f"customapis({cid})", {"description": desc, "displayname": "Agent: " + agent["display"],
                                             "PluginTypeId@odata.bind": f"/plugintypes({type_id})"}), "update api")
        print(f"= {api}")
    else:
        b = ok(*dv.request("POST", "customapis", {
            "uniquename": api, "name": api, "displayname": "Agent: " + agent["display"], "description": desc,
            "bindingtype": 0, "isfunction": False, "isprivate": False, "allowedcustomprocessingsteptype": 0,
            "PluginTypeId@odata.bind": f"/plugintypes({type_id})"}, SOL), "create api " + api)
        cid = b["customapiid"]
        print(f"+ {api}")
    s = ok(*dv.get(f"customapirequestparameters?$select=uniquename&$filter=_customapiid_value eq {cid}"), "read params")
    have = {r["uniquename"] for r in s.get("value", [])}
    for name, typ, d, optional in REQUEST:
        if name in have:
            continue
        opt = optional if optional is not None else agent["subject_table"] is None
        sub = f" ({agent['subject_table']})" if name == "SubjectId" and agent["subject_table"] else ""
        ok(*dv.request("POST", "customapirequestparameters", {
            "uniquename": name, "name": f"{api}.{name}", "displayname": name, "description": d + sub, "type": typ,
            "isoptional": opt, "CustomAPIId@odata.bind": f"/customapis({cid})"}, SOL), f"param {api}.{name}")
    s = ok(*dv.get(f"customapiresponseproperties?$select=uniquename&$filter=_customapiid_value eq {cid}"), "read props")
    have = {r["uniquename"] for r in s.get("value", [])}
    for name, typ, d in RESPONSE:
        if name in have:
            continue
        ok(*dv.request("POST", "customapiresponseproperties", {
            "uniquename": name, "name": f"{api}.{name}", "displayname": name, "description": d, "type": typ,
            "CustomAPIId@odata.bind": f"/customapis({cid})"}, SOL), f"prop {api}.{name}")


# Custom API types: 0 Boolean, 7 Integer, 10 String, 12 Guid
OPERATIONS = [
    {"api": "gc_AcceptOffer", "display": "Accept offer",
     "description": "Accept an offer: copy its terms to the deal, reserve (or split) the lot, reject the deal's other offers, "
                    "close competing deals for the same lot or RFQ, and move the deal to Terms Agreed. Idempotent; all or nothing.",
     "request": [("OfferId", 12, "The gc_offer to accept.", False)],
     "response": [("Status", 10, "Accepted or AlreadyAccepted."), ("DealId", 10, "The deal."), ("Stage", 7, "Deal stage after the call."),
                  ("ClosedDeals", 7, "Competing deals cancelled."), ("Summary", 10, "What happened, for a human.")]},
    {"api": "gc_ReleaseDeal", "display": "Release cancelled deal",
     "description": "For a Cancelled deal: make the lots reserved for it Available again and reject its open offers.",
     "request": [("DealId", 12, "The cancelled gc_deal.", False)],
     "response": [("Status", 10, "Released."), ("Lots", 7, "Lots freed."), ("Offers", 7, "Offers rejected."), ("Summary", 10, "What happened, for a human.")]},
]



MAIL_OPERATIONS = [
    {"api": "gc_IngestEmail", "display": "Ingest email",
     "description": "Store one Gmail message (users.messages.get, format=full) as a gc_message in the conversation of its Gmail thread. "
                    "Idempotent on the Gmail id. Returns the attachments still to fetch.",
     "request": [("MessageJson", 10, "The Gmail message JSON (format=full).", False),
                 ("MailboxAddress", 10, "Address of the connected mailbox (from Gmail's profile); its mail is recorded as Outbound.", True)],
     "response": [("Status", 10, "Created or Exists."), ("MessageId", 10, "The gc_message."), ("ConversationId", 10, "The gc_conversation."),
                  ("Direction", 10, "Inbound or Outbound."), ("NeedsTriage", 0, "True for a received email not yet triaged."),
                  ("Attachments", 10, "JSON array of {attachmentId, fileName, mimeType, size} to fetch and store."),
                  ("Summary", 10, "What happened, for a human.")]},
    {"api": "gc_AttachEmailFile", "display": "Attach email file",
     "description": "Store one email attachment (base64url from Gmail) as a Quarantined gc_document of the email. Idempotent per file name.",
     "request": [("MessageId", 12, "The gc_message the file belongs to.", False), ("FileName", 10, "File name.", False),
                 ("MimeType", 10, "MIME type.", True), ("Data", 10, "File content, base64url (as Gmail returns it).", False)],
     "response": [("Status", 10, "Created or Exists."), ("DocumentId", 10, "The gc_document.")]},
    {"api": "gc_BuildEmailRaw", "display": "Build email",
     "description": "A pending Email Desk draft (gc_message) as the base64url RFC 2822 message for Gmail drafts.create, threaded as a reply, with its attachments.",
     "request": [("MessageId", 12, "The draft gc_message.", False), ("MailboxAddress", 10, "Connected mailbox (for the Reply-To alias).", True)],
     "response": [("Raw", 10, "base64url RFC 2822 message."), ("ThreadId", 10, "Gmail thread to draft in (empty = new thread)."), ("To", 10, "Recipient.")]},
    {"api": "gc_SourceRequirement", "display": "Source requirement",
     "description": "Shortlist seller leads for a buyer requirement and open masked source requests (deal, invite, seller thread, enquiry draft) per seller with an email.",
     "request": [("RequirementId", 12, "The gc_buyerrequirement.", False)],
     "response": [("Invited", 7, "Source requests opened."), ("Result", 10, "JSON: matched leads, requests, leads without email.")]},
    {"api": "gc_DiscoverSellers", "display": "Discover sellers",
     "description": "Web seller discovery for a buyer requirement: Gemini with Google Search finds producers/exporters (public business contacts only) "
                    "and saves them as leads (source Web Search). Once per requirement unless Force.",
     "request": [("RequirementId", 12, "The gc_buyerrequirement.", False), ("Force", 0, "Run again even if it ran before.", True)],
     "response": [("Found", 7, "Leads found or updated."), ("Result", 10, "JSON: leads, skipped, model.")]},
    {"api": "gc_DiscoverBuyers", "display": "Discover buyers",
     "description": "Web buyer discovery for a seller lot: Gemini with Google Search finds companies that use, import or distribute the material "
                    "(public business contacts only) and saves them as buyer leads (source Web Search). Once per lot unless Force.",
     "request": [("LotId", 12, "The gc_sellerlot.", False), ("Force", 0, "Run again even if it ran before.", True)],
     "response": [("Found", 7, "Leads found or updated."), ("Result", 10, "JSON: leads, skipped, model.")]},
    {"api": "gc_MarketLot", "display": "Market seller lot",
     "description": "Offer an open seller lot (masked, our price) to matching buyers that have not been offered it yet: open email requirements, then buyer leads.",
     "request": [("LotId", 12, "The gc_sellerlot.", False)],
     "response": [("Offered", 7, "Buyers offered the lot in this run."), ("Result", 10, "JSON: who was offered.")]},
    {"api": "gc_DeskBrief", "display": "Desk briefing",
     "description": "A briefing email to the desk owner (desk.owner_email) in the 'DealOS desk briefing' thread; sent automatically by the Desk drafts flow.",
     "request": [("Subject", 10, "Short subject.", False), ("Text", 10, "Body.", False)],
     "response": [("MessageId", 10, "The briefing gc_message (empty when briefings are off).")]},
    {"api": "gc_DeskContract", "display": "Desk contract out",
     "description": "For an approved contract of an Email Desk deal: back-to-back contract PDFs and drafts with them to the buyer and seller threads.",
     "request": [("ContractId", 12, "The gc_contract (Sent For Signature).", False)],
     "response": [("Result", 10, "JSON: status, documents, drafts.")]},
    {"api": "gc_CloseLots", "display": "Close seller lots",
     "description": "Timed seller lots whose bid deadline passed: rank bids (highest buyer price, then earliest), Confirm deal tasks for the winners while quantity lasts, "
                    "confirmation and 'not this time' drafts, briefing. No bid at the seller's price: the best bid goes to the seller and the lot goes open-ended.",
     "request": [],
     "response": [("Result", 10, "JSON: closed lots with winners and bids.")]},
    {"api": "gc_DeskFollowUps", "display": "Desk follow-ups",
     "description": "One chaser to sellers and buyers who have not answered after desk.chase_after_hours; reminders to lot buyers before offers close.",
     "request": [],
     "response": [("Result", 10, "JSON: chasers and reminders drafted.")]},
]


def upsert_operation(op, type_id):
    api = op["api"]
    rows = ok(*dv.get(f"customapis?$select=customapiid&$filter=uniquename eq '{api}'"), "read api").get("value", [])
    if rows:
        cid = rows[0]["customapiid"]
        ok(*dv.patch(f"customapis({cid})", {"description": op["description"][:300], "displayname": op["display"],
                                             "PluginTypeId@odata.bind": f"/plugintypes({type_id})"}), "update api")
        print(f"= {api}")
    else:
        cid = ok(*dv.request("POST", "customapis", {
            "uniquename": api, "name": api, "displayname": op["display"], "description": op["description"][:300],
            "bindingtype": 0, "isfunction": False, "isprivate": False, "allowedcustomprocessingsteptype": 0,
            "PluginTypeId@odata.bind": f"/plugintypes({type_id})"}, SOL), "create api " + api)["customapiid"]
        print(f"+ {api}")
    have = {r["uniquename"] for r in ok(*dv.get(f"customapirequestparameters?$select=uniquename&$filter=_customapiid_value eq {cid}"), "read params").get("value", [])}
    for name, typ, d, optional in op["request"]:
        if name not in have:
            ok(*dv.request("POST", "customapirequestparameters", {
                "uniquename": name, "name": f"{api}.{name}", "displayname": name, "description": d, "type": typ,
                "isoptional": optional, "CustomAPIId@odata.bind": f"/customapis({cid})"}, SOL), f"param {api}.{name}")
    have = {r["uniquename"] for r in ok(*dv.get(f"customapiresponseproperties?$select=uniquename&$filter=_customapiid_value eq {cid}"), "read props").get("value", [])}
    for name, typ, d in op["response"]:
        if name not in have:
            ok(*dv.request("POST", "customapiresponseproperties", {
                "uniquename": name, "name": f"{api}.{name}", "displayname": name, "description": d, "type": typ,
                "CustomAPIId@odata.bind": f"/customapis({cid})"}, SOL), f"prop {api}.{name}")


def ensure_requirement_lookup():
    """gc_deal.gc_requirement: the buyer RFQ a deal came from, so accepting one seller's offer can close the others."""
    s, _ = dv.get("EntityDefinitions(LogicalName='gc_deal')/Attributes(LogicalName='gc_requirement')?$select=LogicalName")
    if s == 200:
        print("= gc_deal.gc_requirement exists")
        return
    ok(*dv.request("POST", "RelationshipDefinitions", {
        "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata",
        "SchemaName": "gc_buyerrequirement_gc_deal_requirement",
        "ReferencedEntity": "gc_buyerrequirement", "ReferencedAttribute": "gc_buyerrequirementid", "ReferencingEntity": "gc_deal",
        "CascadeConfiguration": {"Assign": "NoCascade", "Delete": "RemoveLink", "Merge": "NoCascade", "Reparent": "NoCascade",
                                 "Share": "NoCascade", "Unshare": "NoCascade"},
        "Lookup": {"@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata", "SchemaName": "gc_Requirement",
                   "DisplayName": label("Buyer requirement"), "Description": label("The RFQ this deal came from (one deal per invited seller)."),
                   "RequiredLevel": {"Value": "None", "CanBeChanged": True}}}, SOL), "create gc_deal.gc_requirement")
    ok(*dv.request("POST", "PublishXml", {"ParameterXml": "<importexportxml><entities><entity>gc_deal</entity><entity>gc_buyerrequirement</entity></entities></importexportxml>"}), "publish gc_deal")
    print("+ gc_deal.gc_requirement lookup created")


def ensure_step(type_id, typename, message, entity, stage, mode, description, filtering=None):
    """One plug-in step (stage 20 pre-operation / 40 post-operation; mode 0 sync / 1 async), created when missing."""
    name = f"{typename}: {message} of {entity}"
    if ok(*dv.get(f"sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid&$filter=name eq '{name}'"), "read step").get("value"):
        print(f"= step {name}")
        return
    msg = ok(*dv.get(f"sdkmessages?$select=sdkmessageid&$filter=name eq '{message}'"), f"read {message} message")["value"][0]["sdkmessageid"]
    flt = ok(*dv.get(f"sdkmessagefilters?$select=sdkmessagefilterid&$filter=primaryobjecttypecode eq '{entity}' and _sdkmessageid_value eq {msg}"),
             f"read {entity} filter")["value"][0]["sdkmessagefilterid"]
    body = {"name": name, "description": description, "mode": mode, "stage": stage, "rank": 1, "supporteddeployment": 0,
            "eventhandler_plugintype@odata.bind": f"/plugintypes({type_id})",
            "sdkmessageid@odata.bind": f"/sdkmessages({msg})", "sdkmessagefilterid@odata.bind": f"/sdkmessagefilters({flt})"}
    if mode == 1:
        body["asyncautodelete"] = True
    if filtering:
        body["filteringattributes"] = filtering
    ok(*dv.request("POST", "sdkmessageprocessingsteps", body, SOL), "create step " + name)
    print(f"+ step {name} ({'async' if mode else 'sync'})")


# Removed from the build on 7 Oct 2026, so that only the email desk and what it uses remain: the website (Power Pages site, its chat
# agents and plug-ins) and the marketplace-only agents and escrow operations. The email desk is the front door.
RETIRED_TYPES = ["DealOS.Agents.ChatPlugin", "DealOS.Agents.Portal.CatalogPlugin", "DealOS.Agents.Portal.PortalGuardPlugin"]
RETIRED_APIS = ["gc_Agent_BuyerConcierge", "gc_Agent_SellerAssistant", "gc_RefreshCatalog",
                "gc_Agent_ListingVerification", "gc_Agent_Matching", "gc_Agent_Pricing", "gc_Agent_Negotiation", "gc_Agent_Payment", "gc_Agent_Logistics",
                "gc_OpenEscrow", "gc_InstructRelease"]


def retire():
    """Removes the retired custom APIs and plug-in types (with their steps) from Dev, so the assembly without them can be uploaded.
    Tables and data (listings, catalog, payments, chat conversations) are left as they are."""
    for api in RETIRED_APIS:
        for r in ok(*dv.get(f"customapis?$select=customapiid&$filter=uniquename eq '{api}'"), "read api").get("value", []):
            for kind, key in (("customapirequestparameters", "customapirequestparameterid"), ("customapiresponseproperties", "customapiresponsepropertyid")):
                for x in ok(*dv.get(f"{kind}?$select={key}&$filter=_customapiid_value eq {r['customapiid']}"), "read " + kind).get("value", []):
                    ok(*dv.request("DELETE", f"{kind}({x[key]})"), "delete " + kind)
            ok(*dv.request("DELETE", f"customapis({r['customapiid']})"), "delete api " + api)
            print(f"- custom API {api} removed")
    for typename in RETIRED_TYPES:
        for t in ok(*dv.get(f"plugintypes?$select=plugintypeid&$filter=typename eq '{typename}'"), "read type").get("value", []):
            steps = ok(*dv.get(f"sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid&$filter=_eventhandler_value eq {t['plugintypeid']}"), "read steps").get("value", [])
            for st in steps:
                ok(*dv.request("DELETE", f"sdkmessageprocessingsteps({st['sdkmessageprocessingstepid']})"), "delete step")
            ok(*dv.request("DELETE", f"plugintypes({t['plugintypeid']})"), "delete type " + typename)
            print(f"- plug-in type {typename} removed ({len(steps)} step(s))")


def ensure_settings():
    for key, value, vtype, desc in SETTINGS:
        s = ok(*dv.get(f"gc_platformsettings?$select=gc_platformsettingid&$filter=gc_key eq '{key}'"), "read setting")
        if s.get("value"):
            print(f"= setting {key} (kept)")
            continue
        ok(*dv.post("gc_platformsettings", {"gc_name": key, "gc_key": key, "gc_value": value, "gc_valuetype": VALUE_TYPES[vtype],
                                             "gc_description": desc}), "create setting " + key)
        print(f"+ setting {key} = {value}")


def main():
    if not os.path.exists(DLL):
        sys.exit("Build first: dotnet build -c Release src/DealOS.Agents")
    if not os.path.exists(MANIFEST):
        sys.exit("Run the harness first: dotnet run --project tests/DealOS.Agents.Harness -- build/agents")
    agents = json.load(open(MANIFEST))
    ensure_secret_table()
    for env, secret in (("GEMINI_API_KEY", "gemini.api_key"), ("OPENAI_API_KEY", "openai.api_key")):
        key = env_key(env)
        if key:
            upsert_secret(secret, key)
        else:
            print(f"= no {env} in .env; the stored {secret} is left unchanged")
    ensure_agent_option()
    retire()
    types = upsert_assembly()
    for a in agents:
        upsert_api(a, types["DealOS.Agents.AgentPlugin"])
    ensure_requirement_lookup()
    for op in OPERATIONS:
        upsert_operation(op, types["DealOS.Agents.Operations.OperationsPlugin"])
    for op in MAIL_OPERATIONS:
        upsert_operation(op, types["DealOS.Agents.Mail.MailPlugin"])
    ensure_settings()
    print(f"Deployed {len(agents)} agents and {len(OPERATIONS)} operations.")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--provider" and sys.argv[2] in ("openai", "gemini"):
        set_provider(sys.argv[2])
    else:
        main()

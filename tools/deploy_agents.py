"""Deploy the DealOS agents to Dataverse (idempotent).

Steps:
  1. gc_secret table (org-owned, no privileges for normal roles) + 'gemini.api_key' row from .env
  2. 'Document Intelligence' option on the global choice gc_agent
  3. Plug-in assembly DealOS.Agents + plug-in type DealOS.Agents.AgentPlugin
  4. One Custom API per agent (gc_Agent_<Name>) with request parameters and response properties
  5. Agent settings in gc_platformsetting (created only when missing)
  6. Deterministic operations (plug-in type DealOS.Agents.Operations.OperationsPlugin):
     gc_AcceptOffer, gc_OpenEscrow, gc_ReleaseDeal, gc_InstructRelease, plus the gc_deal.gc_requirement lookup
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
    ("agents.provider", "Gemini", "Text", "AI provider used by the gc_Agent_* custom APIs."),
    ("agents.model.default", "gemini-3.5-flash", "Text", "Default Gemini model for agents."),
    ("agents.model.document", "gemini-3.5-flash", "Text", "Gemini model for Document Intelligence (PDF/image extraction)."),
    ("agents.model.fallbacks", "gemini-3.1-flash-lite,gemini-3.8-flash", "Text", "Models tried in order on quota (429), overload (503), retired model (404) or timeout."),
    ("agents.time_budget_seconds", "100", "Number", "Max seconds per agent run (plug-in limit is 120)."),
    ("agents.call_timeout_seconds", "45", "Number", "Max seconds for one Gemini call before trying the next model."),
    ("agents.pricing", "{}", "Json", "USD per 1M tokens per model prefix, e.g. {\"gemini-3.5-flash\":[in,out]}; empty = cost not recorded."),
    ("agents.generation_config", "", "Json", "Optional extra Gemini generationConfig merged into every call (e.g. thinkingConfig)."),
    ("escrow.default_schedule", '[{"pct":30,"condition":"Inspection passed and BL issued"},{"pct":70,"condition":"Delivered and discharge inspection accepted"}]',
     "Json", "Release tranches gc_OpenEscrow creates: pct must sum to 100; conditions name the milestones."),
    ("escrow.partner", "Not configured", "Text", "Name of the licensed escrow partner written on new payments."),
    ("escrow.funding_days", "7", "Number", "Days the buyer has to fund escrow after signing."),
    ("notifications.email.enabled", "false", "Bool", "true = the Notify party flow emails parties; false = it only records what it would have sent."),
    ("notifications.email.redirect", "", "Text", "If set, every party email goes to this address instead (testing)."),
    ("admin.digest.recipients", "", "Text", "Semicolon-separated emails for the daily AdminSupervisor digest; empty = the flow owner's mailbox."),
]
VALUE_TYPES = {"Bool": 303300000, "Number": 303300001, "Text": 303300002, "Json": 303300003}


def ok(status, body, what):
    if status >= 300:
        sys.exit(f"FAILED {what}: {status} {json.dumps(body)[:1500]}")
    return body


def label(text):
    return {"@odata.type": "Microsoft.Dynamics.CRM.Label",
            "LocalizedLabels": [{"@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel", "Label": text, "LanguageCode": 1033}]}


def env_key():
    with open(os.path.join(ROOT, ".env")) as f:
        for line in f:
            if line.startswith("GEMINI_API_KEY="):
                return line.split("=", 1)[1].strip()
    sys.exit("GEMINI_API_KEY missing in .env")


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
            "culture": "neutral", "description": "DealOS AI agents (Gemini) exposed as gc_Agent_* Custom APIs"}, SOL), "create assembly")
        aid = b["pluginassemblyid"]
        print("+ plug-in assembly registered")
    types = {}
    for typename, friendly, desc in [("DealOS.Agents.AgentPlugin", "AgentPlugin", "Runs a DealOS agent"),
                                     ("DealOS.Agents.Operations.OperationsPlugin", "OperationsPlugin", "Deterministic deal operations (accept offer, open escrow)")]:
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
    {"api": "gc_OpenEscrow", "display": "Open escrow",
     "description": "Open escrow for a signed deal: one payment (Awaiting Funding) and release tranches with commission from the "
                    "deal's commission plan and the escrow.default_schedule setting. Idempotent.",
     "request": [("DealId", 12, "The gc_deal (Signed or Awaiting Funding).", False)],
     "response": [("Status", 10, "Opened or Exists."), ("PaymentId", 10, "The gc_payment."), ("Releases", 7, "Number of release tranches."),
                  ("Summary", 10, "What happened, for a human.")]},
    {"api": "gc_ReleaseDeal", "display": "Release cancelled deal",
     "description": "For a Cancelled deal: make the lots reserved for it Available again and reject its open offers.",
     "request": [("DealId", 12, "The cancelled gc_deal.", False)],
     "response": [("Status", 10, "Released."), ("Lots", 7, "Lots freed."), ("Offers", 7, "Offers rejected."), ("Summary", 10, "What happened, for a human.")]},
    {"api": "gc_InstructRelease", "display": "Instruct fund release",
     "description": "Mark an escrow release as Instructed after Finance approved its Fund Release task. Re-checks in code: approved task, "
                    "funded escrow, no hold on the deal. Idempotent.",
     "request": [("ReleaseId", 12, "The gc_paymentrelease.", False)],
     "response": [("Status", 10, "Instructed or AlreadyInstructed."), ("Summary", 10, "What happened, for a human.")]},
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
    upsert_secret("gemini.api_key", env_key())
    ensure_agent_option()
    types = upsert_assembly()
    for a in agents:
        upsert_api(a, types["DealOS.Agents.AgentPlugin"])
    ensure_requirement_lookup()
    for op in OPERATIONS:
        upsert_operation(op, types["DealOS.Agents.Operations.OperationsPlugin"])
    ensure_settings()
    print(f"Deployed {len(agents)} agents and {len(OPERATIONS)} operations.")


if __name__ == "__main__":
    main()

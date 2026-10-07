"""DealOS cloud flows that compose the gc_Agent_* custom APIs and the deterministic operations.

Each function returns a dict: name, description, id, definition, connections.
Existing flows keep their workflow id; new flows get a stable id from their name.
"""
import copy
import glob
import json
import os

from .lib import (AUTH, B, COMPANIES_HOUSE, CONTRACT, DOCUSIGN, GLEIF, GMAIL, connector, docusign, gmail, http_get, minutes_trigger, scope, upload_file,
                  _custom_connectors, DISPUTE, DV, EMPTY_GUID, FAILURE, INSPECTION, INSPECTION_RESULT, INVITE, INVOICE, INVOICE_KIND, KYB,
                  LISTING, MATCH, MESSAGE, MESSAGE_KIND, MILESTONE, MILESTONE_INSPECTION, MILESTONE_PENDING, MILESTONE_TYPE, NOTIFY_FLOW, OFFER,
                  OUTLOOK, OVERLAY, OVERLAY_DISPUTED, OVERLAY_ON_HOLD, PARSE, PARTY, PAYMENT, PURPOSE, RELEASE, REQUIREMENT, REQUIREMENT_EXPIRED,
                  RESOLUTION, REVIEW_STATUS, ROOT, SCOPE, SHIPMENT, STAGE, TIER, TIER_TRADE_VERIFIED, VERIFICATION_CONFIRMED,
                  VERIFICATION_METHOD_OWN_INSPECTION, agent, agent_ok, agent_result, audit, chain, compose, condition, create, daily_trigger,
                  definition, flow_failure, flow_id, foreach, get_row, hourly_trigger, list_rows, review_task, row_trigger, run_agent, switch,
                  terminate, transition, try_catch, unbound, update)

DESK_SOURCE_EMAIL = B + 1  # gc_buyerrequirement.gc_source = Email
DESK_STAGE = dict(Qualifying=B, Sourcing=B + 1, Quoted=B + 2, Negotiating=B + 3, Agreed=B + 4, ContractSent=B + 5, Signed=B + 6, Closed=B + 7)
DRAFT = dict(Pending=B, Sent=B + 1, Discarded=B + 2)
ESIGN = dict(NotUsed=B, AwaitingApproval=B + 1, Sending=B + 2, Sent=B + 3, Completed=B + 4, Declined=B + 5, Voided=B + 6, Failed=B + 7, Rejected=B + 8)
DIRECTION = dict(Inbound=B, Outbound=B + 1, Draft=B + 2)
TRIAGE = dict(Genuine=B, Review=B + 1, Ignored=B + 2)

EXISTING = {
    "DealOS | Listing verification": "02bdd963-de7f-412e-829d-b86ad5bb05ff",
    "DealOS | Approvals": "54c86828-b789-43ff-a057-9b0e19c37ae2",
    "DealOS | Offer pricing": "e1ef2b29-ffd9-40ad-8640-6dbc3b362d18",
    "DealOS | Daily sweep": "dbe63e93-8844-40c7-a9c8-037b23dfdb6f",
}


def T(col):
    """Trigger column (expression, no @)."""
    return f"triggerOutputs()?['body/{col}']"


def S(expr):
    """String-interpolated expression."""
    return "@{" + expr + "}"


def body(action, col):
    """Column of a Get row / child-flow result (expression, no @)."""
    return f"body('{action}')?['{col}']"


def or_empty_guid(expr):
    """Interpolated id that is never empty, for OData filters on optional lookups."""
    return S(f"coalesce({expr}, '{EMPTY_GUID}')")


FMT_AMOUNT = "formatNumber({}, 'N2')"
FMT_DATE = "formatDateTime({}, 'd MMM yyyy')"
INCOTERMS = ["EXW", "FCA", "FAS", "FOB", "CFR", "CIF", "CPT", "CIP", "DAP", "DPU", "DDP"]


def incoterm_label(expr):
    """Choice value → Incoterm code. Create triggers carry no formatted values."""
    table = json.dumps({str(B + i): code for i, code in enumerate(INCOTERMS)}).replace("'", "''")
    return f"coalesce(json('{table}')?[string({expr})], 'Incoterm not set')"


def first_value(action, col):
    return f"first(outputs('{action}')?['body/value'])?['{col}']"


def allowed(action):
    return f"@not(equals(outputs('{action}')?['body/Allowed'], true))"


def props(**kv):
    """Object expression from key → expression pairs (for audit details and review payloads)."""
    expr = "json('{}')"
    for k, v in kv.items():
        expr = f"addProperty({expr}, '{k}', {v})"
    return expr


def flow(name, description, triggers, steps, connections=(DV,), catch_extra=None):
    return {"name": name, "description": description, "id": EXISTING.get(name) or flow_id(name),
            "definition": definition(triggers, try_catch(name, steps, catch_extra)), "connections": connections}


def stuck_task(title_expr, deal_expr, failures_action, purpose="Other", role="DealManager"):
    return review_task(title_expr, purpose, role, props(failures=f"outputs('{failures_action}')?['body/Failures']"), deal=S(deal_expr), kind="Review")


# ---------------------------------------------------------------- notifications (child flow)

# ---------------------------------------------------------------- supply side

def document_intake():
    name = "DealOS | Document intake"
    did, acc, lst, deal = T("gc_documentid"), T("_gc_account_value"), T("_gc_listing_value"), T("_gc_deal_value")
    seller_role, buyer_role = str(PARTY["Seller"]), str(PARTY["Buyer"])
    roles = "string(coalesce(body('Party')?['gc_partyrole'], ''))"
    steps = [
        # One upload fires several "modified" events; a queued run must not reprocess a document another run already parsed.
        ("Document_now", get_row("gc_documents", S(did), select="gc_parsestatus")),
        ("Already_processed", condition(f"@not(equals({body('Document_now', 'gc_parsestatus')}, {PARSE['Pending']}))", [("Nothing_to_do", terminate("Succeeded"))])),
        ("Run_DocumentIntelligence", agent("DocumentIntelligence", S(did), "document-intake")),
        ("Document_failed", condition(f"@not({agent_ok('Run_DocumentIntelligence')[1:]})", [
            ("Mark_document_failed", update("gc_documents", S(did), gc_parsestatus=PARSE["Failed"])),
            ("Record_document_failure", flow_failure(name, "Run_DocumentIntelligence", S("coalesce(outputs('Run_DocumentIntelligence')?['body/Summary'], 'agent call failed')"))),
            ("Stop_document_failed", terminate("Failed", "Document Intelligence failed; see Flow failures")),
        ]), {"Run_DocumentIntelligence": ["Succeeded", "Failed", "TimedOut"]}),
        ("Party_document", condition(f"@and(not(empty({acc})), empty({lst}), empty({deal}))", [
            ("Party", get_row("accounts", S(acc), select="gc_partyrole")),
            ("Seller_party", condition(f"@contains({roles}, '{seller_role}')", run_agent("OnboardingKYB", S(acc), "document-intake", name, on_fail="record"))),
            ("Buyer_party", condition(f"@contains({roles}, '{buyer_role}')", run_agent("BuyerVerification", S(acc), "document-intake", name, on_fail="record"))),
        ])),
        ("Audit", audit("flow:document-intake", "document.processed", "gc_document", S(did),
                        props(summary="outputs('Run_DocumentIntelligence')?['body/Summary']"))),
    ]
    return flow(name, "A document's file is uploaded (or it is set back to Pending): an email attachment released by the Trade Desk, or a "
                      "party's KYB document → Document Intelligence; a party's own document → Onboarding KYB / Buyer Verification.",
                row_trigger("When_a_document_file_is_ready", "gc_document", 4, attributes="gc_file,gc_parsestatus", concurrency=1,
                            conditions=[f"@and(equals({T('gc_parsestatus')}, {PARSE['Pending']}), not(empty({T('gc_file')})))"]),
                steps)


def party_onboarding():
    name = "DealOS | Party onboarding"
    acc = T("accountid")
    roles = f"string(coalesce({T('gc_partyrole')}, ''))"
    steps = [
        ("Mark_in_progress", update("accounts", S(acc), gc_kybstatus=KYB["InProgress"])),
        # Public register and sanctions lists first, so the KYB agents see the results
        *party_checks(acc, name),
        ("Seller", condition(f"@contains({roles}, '{PARTY['Seller']}')", run_agent("OnboardingKYB", S(acc), "party-onboarding", name, on_fail="record")),
         {"Checks_done": ["Succeeded", "Failed", "Skipped", "TimedOut"]}),
        ("Buyer", condition(f"@contains({roles}, '{PARTY['Buyer']}')", run_agent("BuyerVerification", S(acc), "party-onboarding", name, on_fail="record"))),
        ("Audit", audit("flow:party-onboarding", "party.onboarding", "account", S(acc), props(roles=roles))),
    ]
    return flow(name, "A party becomes a seller or buyer (and KYB has not started) → KYB status In Progress → company registry (GLEIF, Companies House) and "
                      "sanctions screening (OFAC, UN, UK) → Onboarding KYB and/or Buyer Verification. The agents record pending checks and draft the document request.",
                row_trigger("When_a_party_needs_KYB", "account", 4, attributes="gc_partyrole", concurrency=1, conditions=[
                    f"@and(or(contains({roles}, '{PARTY['Seller']}'), contains({roles}, '{PARTY['Buyer']}')), "
                    f"or(empty({T('gc_kybstatus')}), equals({T('gc_kybstatus')}, {KYB['NotStarted']})))"]),
                steps, connections=CHECK_CONNECTIONS)


# ---------------------------------------------------------------- demand and matching

# ---------------------------------------------------------------- offers

def offer_pricing():
    name = "DealOS | Offer pricing"
    oid, did = T("gc_offerid"), T("_gc_deal_value")
    dl = lambda c: body("Deal", c)
    steps = [
        ("Price_offer", unbound("gc_CalculatePriceQuote", OfferId=S(oid))),
        ("Deal", get_row("gc_deals", S(did), select="gc_name,gc_stage,_gc_buyer_value,_gc_seller_value,gc_emaildesk")),
        ("First_offer", condition(f"@equals({dl('gc_stage')}, {STAGE['Inquiry']})", [("To_negotiation", transition(S(did), STAGE["Negotiation"], "First offer received"))])),
        ("Audit", audit("flow:offer-pricing", "offer.priced", "gc_offer", S(oid), props(summary="outputs('Price_offer')?['body/Summary']"))),
    ]
    return flow(name, "Offer created (a seller quote or our bid on an email desk deal) → gc_CalculatePriceQuote (engine numbers, needed to accept it) → "
                      "deal to Negotiation on the first offer.",
                row_trigger("When_an_offer_is_created", "gc_offer", 1), steps)


def offer_accepted():
    name = "DealOS | Offer accepted"
    oid = T("gc_offerid")
    steps = [
        ("Accept", unbound("gc_AcceptOffer", retry_none=True, OfferId=S(oid))),
        ("Audit", audit("flow:offer-accepted", "offer.accept.flow", "gc_offer", S(oid),
                        props(status="outputs('Accept')?['body/Status']", summary="outputs('Accept')?['body/Summary']"))),
    ]
    catch = [
        ("Reopen_offer", update("gc_offers", S(oid), gc_status=OFFER["Open"])),
        ("Ask_deal_manager", review_task(f"concat('Offer acceptance refused: ', coalesce({T('gc_name')}, ''))", "Other", "DealManager",
                                         props(error="take(string(result('Try')), 3000)"), deal=S(T("_gc_deal_value")), kind="Review")),
    ]
    return flow(name, "Offer set to Accepted (staff or portal) → gc_AcceptOffer: terms to deal, lot reserved, competing deals closed, Terms Agreed. "
                      "If refused, the offer goes back to Open and a Deal Manager task explains why.",
                row_trigger("When_an_offer_is_accepted", "gc_offer", 3, filter=f"gc_status eq {OFFER['Accepted']}", attributes="gc_status", concurrency=1),
                steps, catch_extra=catch)


# ---------------------------------------------------------------- deal execution

def deal_trigger(name, stage):
    return row_trigger(name, "gc_deal", 3, filter=f"gc_stage eq {STAGE[stage]}", attributes="gc_stage", concurrency=1)


def terms_agreed():
    name = "DealOS | Terms agreed"
    did = T("gc_dealid")
    steps = [
        ("To_compliance", transition(S(did), STAGE["ComplianceCheck"], "Terms agreed")),
        ("Compliance_refused", condition(allowed("To_compliance"), [("Stuck_task", stuck_task(f"concat('Deal stuck at Terms Agreed: ', {T('gc_name')})", did, "To_compliance"))])),
    ]
    return flow(name, "Deal at Terms Agreed → move to Compliance Check (the desk tells the parties by email).", deal_trigger("When_terms_are_agreed", "TermsAgreed"), steps)


def compliance_check():
    name = "DealOS | Compliance check"
    did = T("gc_dealid")
    decision = agent_result("Compliance", "decision")
    steps = [
        *run_agent("Compliance", S(did), "compliance-check", name),
        ("Clear", condition(f"@equals({decision}, 'Clear')", [
            ("To_contracting", transition(S(did), STAGE["Contracting"], "Compliance clear")),
            ("Contracting_refused", condition(allowed("To_contracting"), [
                ("Stuck_task", stuck_task(f"concat('Compliance clear but contracting refused: ', {T('gc_name')})", did, "To_contracting"))])),
        ], [
            ("Blocked", condition(f"@equals({decision}, 'Block')", [("Compliance_hold", update("gc_deals", S(did), gc_statusoverlay=OVERLAY["ComplianceHold"]))])),
        ])),
        ("Audit", audit("flow:compliance-check", "deal.compliance", "gc_deal", S(did), props(decision=decision, summary="outputs('Run_Compliance')?['body/Summary']"))),
    ]
    return flow(name, "Deal at Compliance Check → Compliance agent. Clear → Contracting. Refer → Compliance Officer decides (Review decisions flow). "
                      "Block → Compliance Hold overlay and officer review.", deal_trigger("When_compliance_starts", "ComplianceCheck"), steps)


def contracting():
    name = "DealOS | Contracting"
    did = T("gc_dealid")
    steps = [
        *run_agent("Contract", S(did), "contracting", name),
        ("Not_drafted", condition(f"@not(equals({agent_result('Contract', 'contract_created')}, true))", [
            ("Missing_terms_task", review_task(f"concat('Contract not drafted: ', {T('gc_name')})", "Other", "DealManager",
                                               props(summary="outputs('Run_Contract')?['body/Summary']", missing=agent_result("Contract", "missing_terms")),
                                               deal=S(did), kind="Review"))])),
        ("Audit", audit("flow:contracting", "deal.contract_drafted", "gc_deal", S(did), props(summary="outputs('Run_Contract')?['body/Summary']"))),
    ]
    return flow(name, "Deal at Contracting → Contract agent drafts the contract from the accepted terms and opens the Contract Issue approval; "
                      "missing terms go to the Deal Manager.", deal_trigger("When_contracting_starts", "Contracting"), steps)


def contract_signed():
    name = "DealOS | Contract signed"
    cid, did = T("gc_contractid"), T("_gc_deal_value")
    steps = [
        ("Stamp_signed_date", condition(f"@empty({T('gc_signedon')})", [("Set_signed_on", update("gc_contracts", S(cid), gc_signedon="@utcNow()"))])),
        ("To_signed", transition(S(did), STAGE["Signed"], "Contract signed")),
        ("Signed_refused", condition(allowed("To_signed"), [
            ("Signed_stuck_task", stuck_task("concat('Contract signed but deal not moved: ', " + T("gc_name") + ")", did, "To_signed")),
            ("Stop_not_signed", terminate("Succeeded"))])),
        ("Desk_deal", get_row("gc_deals", S(did), select="_gc_requirement_value")),
        ("Desk_requirement_signed", condition("@not(empty(body('Desk_deal')?['_gc_requirement_value']))", [
            ("Set_desk_signed", update("gc_buyerrequirements", S("body('Desk_deal')?['_gc_requirement_value']"), gc_deskstage=DESK_STAGE["Signed"]))])),
        ("Audit_desk_signed", audit("flow:contract-signed", "deal.signed", "gc_deal", S(did), props(escrow="'none: payment between the parties per contract'"))),
    ]
    return flow(name, "Contract set to Signed → deal Signed (no escrow: payment is between the parties per contract) → requirement desk stage Signed "
                      "(Inspection booking follows).",
                row_trigger("When_a_contract_is_signed", "gc_contract", 3, filter=f"gc_status eq {CONTRACT['Signed']}", attributes="gc_status", concurrency=1),
                steps)


def deal_cancelled():
    name = "DealOS | Deal cancelled"
    did = T("gc_dealid")
    steps = [
        ("Release_holds", unbound("gc_ReleaseDeal", retry_none=True, DealId=S(did))),
        ("Audit", audit("flow:deal-cancelled", "deal.cancelled", "gc_deal", S(did), props(released="outputs('Release_holds')?['body/Summary']"))),
    ]
    return flow(name, "Deal Cancelled (another buyer won the lot, the deal broke, compliance) → gc_ReleaseDeal rejects its open offers "
                      "(the desk drafts any note to the parties itself).", deal_trigger("When_a_deal_is_cancelled", "Cancelled"), steps)


# ---------------------------------------------------------------- human decisions

def review_decisions():
    name = "DealOS | Review decisions"
    tid, deal, account = T("gc_reviewtaskid"), T("_gc_deal_value"), T("_gc_account_value")
    pl = lambda k: f"json(coalesce({T('gc_payload')}, '{{}}'))?['{k}']"
    approved = f"equals({T('gc_status')}, {REVIEW_STATUS['Approved']})"
    cases = [
        ("Message_send", PURPOSE["MessageSend"], [
            # A party request an agent drafted (e.g. KYB documents): approval marks it; the desk sends requests as Gmail drafts.
            ("Send_decision", condition("@" + approved,
                                        [("Approve_message", update("gc_generatedmessages", S(pl("messageId")), gc_status=MESSAGE["Approved"]))],
                                        [("Reject_message", update("gc_generatedmessages", S(pl("messageId")), gc_status=MESSAGE["Rejected"]))]))]),
        ("Tier_upgrade", PURPOSE["TierUpgrade"], [
            ("Tier_decision", condition("@" + approved, [
                ("Set_tier", update("accounts", S(account), gc_trusttier=f"@int({pl('tierValue')})",
                                    gc_kybstatus=f"@if(greaterOrEquals(int({pl('tierValue')}), {TIER['KYBVerified']}), {KYB['Passed']}, {KYB['InProgress']})"))]))]),
        ("Screening_clearance", PURPOSE["ScreeningClearance"], [
            ("Deal_or_party", condition(f"@not(empty({deal}))", [
                ("Clearance_decision", condition("@" + approved, [
                    ("Screened_deal", get_row("gc_deals", S(deal), select="gc_name,gc_statusoverlay,gc_stage")),
                    ("Lift_compliance_hold", condition(f"@equals(body('Screened_deal')?['gc_statusoverlay'], {OVERLAY['ComplianceHold']})",
                                                       [("Clear_overlay", update("gc_deals", S(deal), gc_statusoverlay=OVERLAY["None_"]))])),
                    # Only a deal still waiting at Compliance Check moves on (a re-check may have cleared it already)
                    ("Still_at_compliance", condition(f"@equals(body('Screened_deal')?['gc_stage'], {STAGE['ComplianceCheck']})", [
                        ("To_contracting", transition(S(deal), STAGE["Contracting"], "Compliance cleared by officer")),
                        ("Contracting_refused", condition(allowed("To_contracting"), [
                            ("Cleared_stuck_task", stuck_task("concat('Cleared but contracting refused: ', coalesce(body('Screened_deal')?['gc_name'], ''))", deal, "To_contracting"))])),
                    ])),
                ], [("Cancel_deal", transition(S(deal), STAGE["Cancelled"], "Compliance not cleared"))])),
            ], [
                ("Party_hold", condition(f"@not(empty({account}))", [
                    ("Set_compliance_hold", update("accounts", S(account), gc_compliancehold=f"@not({approved})")),
                    # Sanctions screening tasks list their screenings: Clear (not the same person / company) or Confirmed Match
                    ("Each_screening", foreach(f"@coalesce({pl('screeningIds')}, json('[]'))", [
                        ("Set_screening", update("gc_screenings", S("items('Each_screening')"), gc_result=f"@if({approved}, {B}, {B + 2})",
                                                 gc_clearancereason=S(f"concat(if({approved}, 'Cleared: ', 'Confirmed match: '), coalesce({T('gc_decisionreason')}, 'decided by a person'))"))),
                    ])),
                ])),
            ]))]),
        ("Contract_issue", PURPOSE["ContractIssue"], [
            ("Issue_decision", condition("@" + approved, [
                ("Send_for_signature", update("gc_contracts", S(pl("contractId")), gc_status=CONTRACT["SentForSignature"])),
            ], [("Void_contract", update("gc_contracts", S(pl("contractId")), gc_status=CONTRACT["Void"]))]))]),
        ("Desk_decisions", PURPOSE["Other"], [
            ("Desk_accept_offer", condition(f"@and({approved}, equals({pl('action')}, 'desk.accept_offer'))", [
                ("Set_buyer_price", update("gc_deals", S(pl("dealId")), gc_buyerprice=f"@float({pl('buyerPrice')})")),
                ("Accept_offer", update("gc_offers", S(pl("offerId")), gc_status=OFFER["Accepted"])),
            ])),
            ("Desk_contract_signed", condition(f"@and({approved}, equals({pl('action')}, 'desk.contract_signed'))", [
                ("Mark_contract_signed", update("gc_contracts", S(pl("contractId")), gc_status=CONTRACT["Signed"], gc_signedon="@utcNow()")),
            ])),
            # Send for e-signature: approved → the Desk e-signature flow sends both envelopes; rejected → nothing goes out
            ("Desk_esign_send", condition(f"@equals({pl('action')}, 'desk.esign_send')", [
                ("Esign_decision", condition("@" + approved, [
                    ("Esign_sending", update("gc_contracts", S(pl("contractId")), gc_esignstatus=ESIGN["Sending"])),
                ], [
                    ("Esign_rejected", update("gc_contracts", S(pl("contractId")), gc_esignstatus=ESIGN["Rejected"])),
                    ("Esign_rejected_brief", unbound("gc_DeskBrief", retry_none=True, Subject=S(f"concat('Not sent for e-signature: ', coalesce({T('gc_name')}, ''))"),
                                                     Text=S(f"concat('Nothing was sent to the parties. ', coalesce({T('gc_decisionreason')}, ''), "
                                                            "decodeUriComponent('%0A%0A'), 'To correct the terms: set the contract to Void, fix the deal and approve a new contract.')"))),
                ])),
            ])),
        ]),
        ("Shipment_booking", PURPOSE["ShipmentBooking"], [
            ("Booking_decision", condition("@" + approved, [
                ("Booking_deal", get_row("gc_deals", S(deal), select="gc_name,_gc_origincountry_value,_gc_destinationcountry_value")),
                ("Existing_shipment", list_rows("gc_shipments", filter=f"_gc_deal_value eq {S(deal)}", select="gc_shipmentid", top=1)),
                ("No_shipment_yet", condition("@equals(length(outputs('Existing_shipment')?['body/value']), 0)", [
                    ("Create_shipment", create("gc_shipments", gc_name=S("take(concat('Shipment – ', body('Booking_deal')?['gc_name']), 100)"),
                                               gc_status=SHIPMENT["Planned"],
                                               gc_scope=f"@if(equals(body('Booking_deal')?['_gc_origincountry_value'], body('Booking_deal')?['_gc_destinationcountry_value']), "
                                                        f"{SCOPE['Domestic']}, {SCOPE['International']})",
                                               **{"gc_deal@odata.bind": S(f"concat('gc_deals(', {deal}, ')')")}))])),
            ]))]),
    ]
    steps = [
        ("Apply_decision", switch(f"@{T('gc_purpose')}", cases)),
        ("Audit", audit("flow:review-decisions", "review.applied", "gc_reviewtask", S(tid),
                        props(purpose=T("gc_purpose"), status=T("gc_status")))),
    ]
    return flow(name, "Review task Approved or Rejected (via the Approvals flow or directly in the admin app) → apply it: mark a drafted party request, "
                      "set trust tier (KYB), clear screening, issue the contract, desk decisions (Confirm deal, signed contract), plan a shipment.",
                row_trigger("When_a_review_is_decided", "gc_reviewtask", 3, attributes="gc_status", concurrency=1, conditions=[
                    f"@or(equals({T('gc_status')}, {REVIEW_STATUS['Approved']}), equals({T('gc_status')}, {REVIEW_STATUS['Rejected']}))"]),
                steps)


def approvals():
    """The existing Approvals flow, now only asking and recording: decisions are applied by Review decisions,
    so a task decided directly in the admin app is applied the same way. Review-kind tasks are routed too."""
    name = "DealOS | Approvals"
    path = glob.glob(os.path.join(ROOT, "solutions", "DealOS", "Workflows", "DealOSApprovals-*.json"))[0]
    with open(path) as f:
        defn = copy.deepcopy(json.load(f)["properties"]["definition"])
    trig = defn["triggers"]["When_a_review_task_is_created"]["inputs"]["parameters"]
    trig["subscriptionRequest/filterexpression"] = "(gc_kind eq 303300000 or gc_kind eq 303300001) and gc_status eq 303300000"
    tried = defn["actions"]["Try"]["actions"]
    tried.pop("Apply_decision", None)
    # The owner may have answered the briefing by email first (approve by reply): a Teams/Outlook answer only counts while the task is still open.
    decided = {k: tried.pop(k) for k in ("Responder", "Record_decision", "Link_decider")}
    decided["Responder"]["runAfter"] = {}
    tried["Task_now"] = dict(get_row("gc_reviewtasks", "@{triggerOutputs()?['body/gc_reviewtaskid']}", select="gc_status"), runAfter={"Ask_staff": ["Succeeded"]})
    tried["Still_open"] = dict(condition(f"@equals(body('Task_now')?['gc_status'], {REVIEW_STATUS['Open']})", []), runAfter={"Task_now": ["Succeeded"]})
    tried["Still_open"]["actions"] = decided
    tried["Audit"]["runAfter"] = {"Still_open": ["Succeeded"]}
    tried["Audit"]["inputs"]["parameters"]["item/gc_hash"] = "set-by-invariants-plugin"
    return {"name": name, "id": EXISTING[name], "connections": (DV, "shared_approvals"), "definition": defn,
            "description": "Review task opened (Approval or Review) → Teams/Outlook approval to the role's assignee → decision recorded on the task, "
                           "unless the owner already decided it by replying to the briefing. The Review decisions flow applies it."}


# ---------------------------------------------------------------- operations

def daily_digest():
    """The AdminSupervisor digest goes to the desk owner as a desk briefing (Gmail), like every other desk report.
    (It was emailed through Outlook, but the tenant user has no Exchange mailbox: Send_digest failed with 404.)"""
    name = "DealOS | Daily digest"
    sections = agent_result("AdminSupervisor", "sections")
    actions = agent_result("AdminSupervisor", "actions")
    nl = "decodeUriComponent('%0A')"
    steps = [
        ("Pipeline", unbound("gc_DeskPipeline", retry_none=True)),
        *run_agent("AdminSupervisor", None, "daily-digest", name, on_fail="record"),
        ("Section_lines", {"type": "Select", "inputs": {"from": f"@coalesce({sections}, json('[]'))",
                                                        "select": f"@concat(item()?['title'], ':', {nl}, '- ', join(coalesce(item()?['items'], json('[]')), concat({nl}, '- ')))"}}),
        ("Action_lines", {"type": "Select", "inputs": {"from": f"@coalesce({actions}, json('[]'))",
                                                       "select": "@concat('- [', item()?['priority'], '] ', item()?['action'], ' (', item()?['owner_role'], ')')"}}),
        ("Send_digest", unbound("gc_DeskBrief", retry_none=True,
                                Subject=S(f"concat('Daily digest: ', coalesce({agent_result('AdminSupervisor', 'headline')}, 'operations'))"),
                                Text=S(f"concat(coalesce(outputs('Run_AdminSupervisor')?['body/Summary'], 'The AI summary is not available today; the pipeline is below.'), {nl}, {nl}, "
                                       f"'PIPELINE', {nl}, coalesce(outputs('Pipeline')?['body/Text'], ''), {nl}, {nl}, 'Actions:', {nl}, join(body('Action_lines'), {nl}), "
                                       f"{nl}, {nl}, join(body('Section_lines'), concat({nl}, {nl})), {nl}, {nl}, 'Reply PIPELINE to any briefing for the latest view.')"))),
    ]
    return flow(name, "Every day 03:00 UTC → AdminSupervisor digest (approvals waiting, stuck deals, failures, AI spend) → briefing email to the desk owner "
                      "(gc_DeskBrief, desk.owner_email).", daily_trigger("Every_day_0300_UTC", 3), steps)


# ---------------------------------------------------------------- RFQ fan-out (plan #8)

def open_task_exists(title_expr, deal_expr=None, account_expr=None):
    """List open review tasks with this exact title (and deal / account), to avoid opening the same task twice."""
    escaped = "replace(take(" + title_expr + ", 100), '''', '''''')"  # ' → '' inside the OData string literal
    flt = f"gc_status eq {REVIEW_STATUS['Open']} and gc_name eq '" + S(escaped) + "'"
    if deal_expr:
        flt += f" and _gc_deal_value eq {or_empty_guid(deal_expr)}"
    if account_expr:
        flt += f" and _gc_account_value eq {or_empty_guid(account_expr)}"
    return list_rows("gc_reviewtasks", filter=flt, select="gc_reviewtaskid", top=1)


def none_found(action):
    return f"@equals(length(outputs('{action}')?['body/value']), 0)"


# ---------------------------------------------------------------- inspection (plan #13)

def inspection_booking():
    name = "DealOS | Inspection booking"
    did = T("gc_dealid")
    insp = "outputs('Create_inspection')?['body/gc_inspectionid']"
    steps = [
        ("Existing_inspection", list_rows("gc_inspections", filter=f"_gc_deal_value eq {S(did)} and gc_status ne {INSPECTION['Cancelled']}", select="gc_inspectionid", top=1)),
        ("Already_booked", condition("@greater(length(outputs('Existing_inspection')?['body/value']), 0)", [("Inspection_exists", terminate("Succeeded"))])),
        ("Create_inspection", create("gc_inspections", gc_name=S(f"take(concat('Inspection – ', {T('gc_name')}), 200)"),
                                     gc_status=INSPECTION["Requested"], gc_result=INSPECTION_RESULT["Pending"],
                                     **{"gc_Deal@odata.bind": S(f"concat('gc_deals(', {did}, ')')")})),
        ("Link_lot", condition(f"@not(empty({T('_gc_lot_value')}))", [
            ("Set_lot", update("gc_inspections", S(insp), **{"gc_Lot@odata.bind": S(f"concat('gc_lots(', {T('_gc_lot_value')}, ')')")}))])),
        ("Booking_task", review_task(f"concat('Book independent inspection: ', {T('gc_name')})", "Other", "LogisticsCoordinator",
                                     props(inspectionId=insp, instructions="'Choose an independent agency (never one proposed by the seller), set the warehouse, date and agency on the inspection, then set it to Booked. The agency uploads the report directly.'"),
                                     deal=S(did), kind="Review")),
        ("Audit", audit("flow:inspection", "inspection.requested", "gc_deal", S(did), props(inspection=insp))),
    ]
    return flow(name, "Deal Signed → one inspection (Requested) → Logistics Coordinator task to book an independent agency.",
                row_trigger("When_a_deal_is_ready_for_inspection", "gc_deal", 3, filter=f"gc_stage eq {STAGE['Signed']}",
                            attributes="gc_stage", concurrency=1), steps)


def inspection_result():
    name = "DealOS | Inspection result"
    iid, did = T("gc_inspectionid"), T("_gc_deal_value")
    dl = lambda c: body("Deal", c)
    verification = "outputs('Create_verification')?['body/gc_verificationid']"
    milestone = "first(outputs('Inspection_milestone')?['body/value'])?['gc_milestoneid']"
    passed = [
        ("Within_spec", update("gc_inspections", S(iid), gc_result=INSPECTION_RESULT["WithinSpec"])),
        ("Create_verification", create("gc_verifications", gc_name=S(f"take(concat('Independent inspection passed – ', {dl('gc_name')}), 100)"),
                                       gc_method=VERIFICATION_METHOD_OWN_INSPECTION, gc_result=VERIFICATION_CONFIRMED,
                                       gc_performedon=f"@coalesce({T('gc_sampledon')}, utcNow())",
                                       gc_notes=S(f"coalesce({T('gc_findings')}, 'Within specification')"))),
        ("Verification_links", condition(f"@not(empty({T('_gc_report_value')}))", [
            ("Link_report", update("gc_verifications", S(verification), **{"gc_evidencedocument@odata.bind": S(f"concat('gc_documents(', {T('_gc_report_value')}, ')')")}))])),
        ("Verification_agency", condition(f"@not(empty({T('_gc_agency_value')}))", [
            ("Link_agency", update("gc_verifications", S(verification), **{"gc_performedbypartner@odata.bind": S(f"concat('accounts(', {T('_gc_agency_value')}, ')')")}))])),
        ("Link_verification", update("gc_inspections", S(iid), **{"gc_Verification@odata.bind": S(f"concat('gc_verifications(', {verification}, ')')")})),
        ("Inspection_milestone", list_rows("gc_milestones", filter=f"_gc_deal_value eq {S(did)} and gc_type eq {MILESTONE_INSPECTION} and gc_status eq {MILESTONE_PENDING}",
                                           select="gc_milestoneid", top=1)),
        ("Complete_milestone", condition("@greater(length(outputs('Inspection_milestone')?['body/value']), 0)", [
            ("Milestone_done", update("gc_milestones", S(milestone), gc_status=MILESTONE["Completed"], gc_completedon="@utcNow()")),
            ("Milestone_evidence", condition(f"@not(empty({T('_gc_report_value')}))", [
                ("Milestone_report", update("gc_milestones", S(milestone), **{"gc_evidencedocument@odata.bind": S(f"concat('gc_documents(', {T('_gc_report_value')}, ')')")}))])),
        ])),
    ]
    failed = [
        ("Off_spec", update("gc_inspections", S(iid), gc_result=INSPECTION_RESULT["OffSpec"])),
        ("Hold_deal", update("gc_deals", S(did), gc_statusoverlay=OVERLAY_ON_HOLD)),
        ("Off_spec_task", review_task(f"concat('Inspection off-spec: ', {dl('gc_name')})", "Other", "DealManager",
                                      props(inspectionId=iid, findings=T("gc_findings"),
                                            options="'Renegotiate the price with both sides by email, or cancel the deal. Lift the On Hold overlay when decided.'"),
                                      deal=S(did), kind="Review")),
    ]
    steps = [
        ("Deal", get_row("gc_deals", S(did), select="gc_name,_gc_buyer_value,_gc_seller_value")),
        ("Passed_or_failed", condition(f"@equals({T('gc_status')}, {INSPECTION['Passed']})", passed, failed)),
        ("Audit", audit("flow:inspection", "inspection.result", "gc_inspection", S(iid), props(status=T("gc_status"), deal=did))),
    ]
    return flow(name, "Inspection set to Passed → verification record (independent inspection, Confirmed) + Inspection milestone Completed with the report. "
                      "Failed → deal On Hold + Deal Manager task (renegotiate or cancel).",
                row_trigger("When_an_inspection_concludes", "gc_inspection", 3, attributes="gc_status", concurrency=1, conditions=[
                    f"@or(equals({T('gc_status')}, {INSPECTION['Passed']}), equals({T('gc_status')}, {INSPECTION['Failed']}))"]),
                steps)


# ---------------------------------------------------------------- disputes (plan #17)

# ---------------------------------------------------------------- settlement, ratings, invoices (plan P5, P7)

# ---------------------------------------------------------------- operations: deadlines and failures (plan #3 additions, #18)

def flow_failure_triage():
    name = "DealOS | Flow failure triage"
    each = "items('Each_failing_flow')"
    steps = [
        ("New_failures", list_rows("gc_flowfailures", select="gc_flowfailureid,gc_flowname,gc_step,gc_error,gc_runurl,createdon", top=100,
                                   orderby="createdon desc", filter=f"gc_status eq {FAILURE['New']}")),
        ("Failure_names", {"type": "Select", "inputs": {"from": "@outputs('New_failures')?['body/value']", "select": "@coalesce(item()?['gc_flowname'], 'unknown')"}}),
        ("Each_failing_flow", foreach("@union(body('Failure_names'), body('Failure_names'))", [
            ("Triage_task_exists", open_task_exists(f"concat('Flow failures: ', {each})")),
            ("Open_triage_task", condition(none_found("Triage_task_exists"), [
                ("Failures_of_flow", {"type": "Query", "inputs": {"from": "@outputs('New_failures')?['body/value']",
                                                                  "where": f"@equals(coalesce(item()?['gc_flowname'], 'unknown'), {each})"}}),
                ("Triage_task", review_task(f"concat('Flow failures: ', {each})", "Other", "Support",
                                            props(flow=each, count="length(body('Failures_of_flow'))",
                                                  how="'Open each run link, fix the cause, then Resubmit the run in Power Automate and set the failure to Replayed (or Ignored).'",
                                                  failures="take(body('Failures_of_flow'), 20)"), kind="Review")),
            ])),
        ])),
    ]
    return flow(name, "Every hour: new gc_flowfailure rows are grouped per flow into one Support review task with the run links (resubmit from the link, "
                      "then mark the failure Replayed). One open task per flow at a time.", hourly_trigger("Every_hour"), steps)


# ---------------------------------------------------------------- Email Desk (docs/EMAIL_DESK.md)

MAIL_LABELS = ["DealOS/Buyer", "DealOS/Seller", "DealOS/Genuine", "DealOS/Review", "DealOS/Ignored", "DealOS/Processed", "DealOS/Error", "DealOS/Seen"]


def mailbox_sync():
    """Every 3 minutes: new Gmail messages → gc_IngestEmail (+ attachments) → Mail Triage agent → Gmail labels.
    A message is labelled DealOS/Processed when done, or DealOS/Error when it failed (remove that label to retry)."""
    name = "DealOS | Mailbox sync"
    setting = lambda key: list_rows("gc_platformsettings", filter=f"gc_key eq '{key}'", select="gc_value", top=1)
    msg = "items('Each_email')?['id']"
    ingest = lambda col: f"outputs('Ingest')?['body/{col}']"
    att = lambda col: f"items('Each_attachment')?['{col}']"
    label_id = lambda expr: f"outputs('Label_map')?[{expr}]"
    triage_label = "coalesce(json(coalesce(outputs('Run_MailTriage')?['body/Result'], '{}'))?['result']?['label'], 'DealOS/Review')"
    add_labels = lambda *names: gmail("ModifyMessage", id=S(msg), body__addLabelIds="@createArray(" + ", ".join(label_id(n) for n in names) + ")")
    sent_id = "items('Each_sent')?['id']"
    label_sent = lambda name_: gmail("ModifyMessage", id=S(sent_id), body__addLabelIds="@createArray(" + label_id(name_) + ")")
    handle = [
        ("Get_email", gmail("GetMessage", id=S(msg), format="full")),
        ("Ingest", unbound("gc_IngestEmail", retry_none=True, MessageJson="@{string(body('Get_email'))}", MailboxAddress="@{outputs('Mailbox')}")),
        ("Each_attachment", foreach(f"@json(coalesce({ingest('Attachments')}, '[]'))", [
            ("Get_attachment", gmail("GetAttachment", messageId=S(msg), id=S(att("attachmentId")))),
            # Idempotent (same email + file name → Exists), so the connector's default retries cover a transient platform error.
            ("Store_attachment", unbound("gc_AttachEmailFile", MessageId=S(ingest("MessageId")), FileName=S(att("fileName")),
                                         MimeType=S(att("mimeType")), Data="@{body('Get_attachment')?['data']}")),
        ])),
        ("Triage_needed", condition(f"@equals({ingest('NeedsTriage')}, true)", [
            ("Run_MailTriage", agent("MailTriage", S(ingest("MessageId")), "mailbox-sync")),
            ("Triage_ok", condition(agent_ok("Run_MailTriage"), [
                ("Label_triaged", add_labels("'DealOS/Processed'", triage_label)),
            ], [
                ("Record_triage_failure", flow_failure(name, "Run_MailTriage", "@{coalesce(outputs('Run_MailTriage')?['body/Summary'], 'triage call failed')} (Gmail " + S(msg) + ")")),
                ("Label_triage_error", add_labels("'DealOS/Error'")),
            ]), {"Run_MailTriage": ["Succeeded", "Failed", "TimedOut"]}),
        ], [
            ("Label_processed", add_labels("'DealOS/Processed'")),
        ])),
    ]
    steps = [
        ("Enabled_setting", setting("email.enabled")),
        ("Stop_if_disabled", condition(f"@not(equals(toLower(coalesce({first_value('Enabled_setting', 'gc_value')}, 'false')), 'true'))",
                                       [("Email_desk_off", terminate("Succeeded"))])),
        ("Query_setting", setting("email.sync.query")),
        ("Max_setting", setting("email.sync.max_per_run")),
        # Whichever Gmail account the connection signed in with is the desk's mailbox; nothing is tied to one address.
        ("Profile", gmail("GetProfile")),
        ("Mailbox", compose("@toLower(body('Profile')?['emailAddress'])")),
        ("Search", compose("@" + f"concat(replace(replace(coalesce({first_value('Query_setting', 'gc_value')}, 'in:inbox newer_than:2d'), "
                                 "'{user}', first(split(outputs('Mailbox'), '@'))), '{domain}', last(split(outputs('Mailbox'), '@'))), "
                                 "' -label:dealos-processed -label:dealos-error -in:drafts')")),
        # Labels: create the DealOS ones that are missing, then map name → id
        ("Labels", gmail("ListLabels")),
        ("Label_names", {"type": "Select", "inputs": {"from": "@coalesce(body('Labels')?['labels'], json('[]'))", "select": "@item()?['name']"}}),
        ("Each_needed_label", foreach("@json('" + json.dumps(MAIL_LABELS) + "')", [
            ("Label_missing", condition("@not(contains(body('Label_names'), item()))", [
                ("Create_label", gmail("CreateLabel", body__name="@item()",
                                       body__labelListVisibility="@if(equals(item(), 'DealOS/Seen'), 'labelHide', 'labelShow')",
                                       body__messageListVisibility="@if(equals(item(), 'DealOS/Seen'), 'hide', 'show')"))])),
        ])),
        ("Labels_now", gmail("ListLabels")),
        ("DealOS_labels", {"type": "Query", "inputs": {"from": "@coalesce(body('Labels_now')?['labels'], json('[]'))",
                                                       "where": "@startsWith(item()?['name'], 'DealOS/')"}}),
        ("Label_pairs", {"type": "Select", "inputs": {"from": "@body('DealOS_labels')",
                                                      "select": "@concat('\"', item()?['name'], '\":\"', item()?['id'], '\"')"}}),
        ("Label_map", compose("@json(concat('{', join(body('Label_pairs'), ','), '}'))")),
        # The owner's corrections: emails moved to another DealOS label in Gmail since the last run (Gmail history, labelAdded)
        ("History_setting", list_rows("gc_platformsettings", filter="gc_key eq 'email.gmail.history_id'", select="gc_platformsettingid,gc_value", top=1)),
        ("Label_corrections", scope([
            ("Has_history_id", condition(f"@not(empty({first_value('History_setting', 'gc_value')}))", [
                ("Label_history", gmail("ListHistory", startHistoryId=S(first_value("History_setting", "gc_value")), historyTypes="labelAdded", maxResults=500)),
                ("History_ok", condition("@equals(outputs('Label_history')?['statusCode'], 200)", [
                    ("Apply_corrections", unbound("gc_TriageCorrections", retry_none=True, HistoryJson="@{string(body('Label_history'))}", LabelMap="@{string(outputs('Label_map'))}")),
                    ("Next_history_id", update("gc_platformsettings", S(first_value("History_setting", "gc_platformsettingid")),
                                               gc_value=S("coalesce(body('Label_history')?['historyId'], body('Profile')?['historyId'])"))),
                ], [
                    # History id too old (Gmail keeps about a week): start again from now
                    ("Restart_history_id", update("gc_platformsettings", S(first_value("History_setting", "gc_platformsettingid")), gc_value=S("body('Profile')?['historyId']"))),
                ]), {"Label_history": ["Succeeded", "Failed"]}),
            ], [
                ("Start_history_id", update("gc_platformsettings", S(first_value("History_setting", "gc_platformsettingid")), gc_value=S("body('Profile')?['historyId']"))),
            ])),
        ])),
        ("On_corrections_failure", scope([
            ("Record_corrections_failure", flow_failure(name, "Label_corrections", "@{take(string(result('Label_corrections')), 3000)}")),
        ]), {"Label_corrections": ["Failed", "TimedOut"]}),
        # New mail: the configured search, minus what is already handled
        ("New_email", gmail("ListMessages", q="@{outputs('Search')}",
                            maxResults=f"@int(coalesce({first_value('Max_setting', 'gc_value')}, '5'))"),
         {"On_corrections_failure": ["Succeeded", "Skipped"]}),
        ("Each_email", foreach("@coalesce(body('New_email')?['messages'], json('[]'))", [
            ("Handle_email", scope(handle)),
            ("On_email_failure", scope([
                ("Record_email_failure", flow_failure(name, "Handle_email", "@{take(string(result('Handle_email')), 3000)} (Gmail " + S(msg) + ")")),
                ("Label_email_error", add_labels("'DealOS/Error'")),
            ]), {"Handle_email": ["Failed", "TimedOut"]}),
        ])),
        # Sent mail: a person sent a desk draft from Gmail → it is recorded in its thread and the draft is closed.
        # Our other sent mail is only marked with the hidden label DealOS/Seen.
        ("Sent_email", gmail("ListMessages", q="in:sent newer_than:3d -label:dealos-processed -label:dealos-seen -label:dealos-error", maxResults=10)),
        ("Each_sent", foreach("@coalesce(body('Sent_email')?['messages'], json('[]'))", [
            ("Handle_sent", scope([
                ("Get_sent", gmail("GetMessage", id=S(sent_id), format="full")),
                ("Ingest_sent", unbound("gc_IngestEmail", retry_none=True, MessageJson="@{string(body('Get_sent'))}", MailboxAddress="@{outputs('Mailbox')}")),
                ("Desk_thread", condition("@equals(outputs('Ingest_sent')?['body/Status'], 'Skipped')",
                                          [("Label_sent_seen", label_sent("'DealOS/Seen'"))],
                                          [("Label_sent_done", label_sent("'DealOS/Processed'"))])),
            ])),
            ("On_sent_failure", scope([
                ("Record_sent_failure", flow_failure(name, "Handle_sent", "@{take(string(result('Handle_sent')), 3000)} (Gmail " + S(sent_id) + ")")),
                ("Label_sent_error", label_sent("'DealOS/Error'")),
            ]), {"Handle_sent": ["Failed", "TimedOut"]}),
        ])),
        ("Any_email", condition("@greater(length(coalesce(body('New_email')?['messages'], json('[]'))), 0)", [
            ("Audit", audit("flow:mailbox-sync", "email.synced", "none", "", props(emails="length(body('New_email')?['messages'])"))),
        ])),
    ]
    return flow(name, "Every 3 minutes (when email.enabled = true): Gmail messages matching email.sync.query that are not yet labelled → gc_IngestEmail "
                      "(+ attachments, Quarantined) → Mail Triage agent → labels DealOS/Buyer, Seller, Genuine, Review or Ignored plus DealOS/Processed. "
                      "Then sent mail: replies sent from desk threads are recorded (their drafts close); other sent mail gets the hidden label DealOS/Seen. "
                      "Before new mail, the owner's label corrections (Gmail history since email.gmail.history_id) are applied. Replies to briefings are commands (approve by reply). "
                      "Failures are recorded and labelled DealOS/Error (remove the label to retry).",
                minutes_trigger("Every_3_minutes", 3), steps, connections=(DV, GMAIL))


def trade_desk():
    """A received email is (or is set to) Genuine → the Trade Desk agent works it in its thread."""
    name = "DealOS | Trade desk"
    mid, conv = T("gc_messageid"), T("_gc_conversation_value")
    run = _dv_unbound_agent("TradeDesk", S(conv), '{"message_id":"' + S(mid) + '","trigger":"flow:trade-desk"}')
    steps = [
        ("Run_TradeDesk", run),
        ("Desk_failed", condition(f"@not({agent_ok('Run_TradeDesk')[1:]})", [
            ("Record_desk_failure", flow_failure(name, "Run_TradeDesk", S("coalesce(outputs('Run_TradeDesk')?['body/Summary'], 'agent call failed')"))),
            ("Desk_failure_task", review_task(f"concat('Trade desk could not handle: ', coalesce({T('gc_subject')}, {T('gc_name')}, 'email'))", "Other", "DealManager",
                                              props(messageId=mid, conversationId=conv, error="outputs('Run_TradeDesk')?['body/Summary']",
                                                    how="'Reply to the email from Gmail yourself, or set the email back to Genuine (gc_triage) to retry once the cause is fixed.'"),
                                              kind="Review")),
        ]), {"Run_TradeDesk": ["Succeeded", "Failed", "TimedOut"]}),
        ("Brief_owner", unbound("gc_DeskBrief", retry_none=True,
                                Subject=S(f"concat(coalesce(json(coalesce(outputs('Run_TradeDesk')?['body/Result'], '{{}}'))?['result']?['intent'], 'Update'), ': ', "
                                          f"coalesce({T('gc_subject')}, {T('gc_name')}, 'email'))"),
                                Text=S("concat('From: ', coalesce(" + T('gc_senderlabel') + ", ''), decodeUriComponent('%0A%0A'), "
                                       "coalesce(outputs('Run_TradeDesk')?['body/Summary'], 'The desk could not handle this email; a task is open.'), "
                                       "decodeUriComponent('%0A%0ANext: '), coalesce(json(coalesce(outputs('Run_TradeDesk')?['body/Result'], '{}'))?['result']?['next_step'], '-'), "
                                       "decodeUriComponent('%0A%0A'), if(equals(json(coalesce(outputs('Run_TradeDesk')?['body/Result'], '{}'))?['result']?['drafted'], true), "
                                       "'Drafts are waiting in Gmail > Drafts (unless auto-send sent them).', 'No email was drafted.'))"),
                                Drafts=S("string(coalesce(json(coalesce(outputs('Run_TradeDesk')?['body/Result'], '{}'))?['result']?['draft_ids'], json('[]')))")),
         {"Desk_failed": ["Succeeded", "Skipped"]}),
        ("Audit", audit("flow:trade-desk", "email.worked", "gc_message", S(mid), props(summary="outputs('Run_TradeDesk')?['body/Summary']"))),
    ]
    return flow(name, "A received email's triage becomes Genuine (by Mail Triage or a person) → Trade Desk agent: records requirement / quote / price, "
                      "sources sellers, opens confirm tasks, drafts the replies. Failures → Deal Manager task.",
                row_trigger("When_an_email_is_genuine", "gc_message", 3, attributes="gc_triage", concurrency=1, conditions=[
                    f"@and(equals({T('gc_triage')}, {TRIAGE['Genuine']}), equals({T('gc_direction')}, {DIRECTION['Inbound']}))"]),
                steps)


def _dv_unbound_agent(name, subject, input_text):
    a = unbound(f"gc_Agent_{name}", retry_none=True, SubjectId=subject, Input=input_text)
    return a


def desk_drafts():
    """Desk drafts → Gmail drafts in the right thread (a person checks and sends them); replaced drafts are deleted from Gmail."""
    name = "DealOS | Desk drafts"
    mid, conv = T("gc_messageid"), T("_gc_conversation_value")
    build = lambda col: f"outputs('Build')?['body/{col}']"
    created = lambda path: f"coalesce(body('Draft_in_thread')?{path}, body('Draft_new_thread')?{path})"
    create = [
        ("Profile", gmail("GetProfile")),
        ("Build", unbound("gc_BuildEmailRaw", retry_none=True, MessageId=S(mid), MailboxAddress="@{body('Profile')?['emailAddress']}")),
        ("Has_thread", condition(f"@not(empty({build('ThreadId')}))",
                                 [("Draft_in_thread", gmail("CreateDraft", body__message__raw=S(build("Raw")), body__message__threadId=S(build("ThreadId"))))],
                                 [("Draft_new_thread", gmail("CreateDraft", body__message__raw=S(build("Raw"))))])),
        ("Save_draft_id", update("gc_messages", S(mid), gc_gmaildraftid=S(created("['id']")))),
        ("New_thread", condition(f"@empty({build('ThreadId')})", [
            ("Save_thread_id", update("gc_conversations", S(conv), gc_gmailthreadid=S(created("['message']?['threadId']"))))])),
        ("Auto_send", condition(f"@equals({T('gc_autosend')}, true)", [
            ("Send_now", gmail("SendDraft", body__id=S(created("['id']")))),
            ("Mark_sent", update("gc_messages", S(mid), gc_draftstatus=DRAFT["Sent"]))])),
        ("Audit_draft", audit("flow:desk-drafts", "email.drafted", "gc_message", S(mid), props(to=build("To"), draft=created("['id']"), auto_send=T("gc_autosend")))),
    ]
    release = [
        # The owner replied SEND to a briefing: the Gmail draft goes out as it stands (with any edits made in Gmail).
        # The flow marks it Sent itself; the sync's sent-mail pass leaves drafts flagged for sending alone.
        ("Send_released", gmail("SendDraft", body__id=S(T("gc_gmaildraftid")))),
        ("Mark_released_sent", update("gc_messages", S(mid), gc_draftstatus=DRAFT["Sent"])),
        ("Audit_released", audit("flow:desk-drafts", "email.sent_on_owner_reply", "gc_message", S(mid), props(draft=T("gc_gmaildraftid")))),
        # Not sent (the draft was sent or deleted by hand in Gmail): back to a normal draft, and the owner is told
        ("Not_released", update("gc_messages", S(mid), gc_autosend=False), {"Send_released": ["Failed", "TimedOut"]}),
        ("Brief_not_sent", unbound("gc_DeskBrief", retry_none=True, Subject="Draft not sent",
                                   Text=S(f"concat('SEND could not send the draft \"', coalesce({T('gc_subject')}, {T('gc_name')}), '\" from Gmail. "
                                          "It may have been sent or deleted there by hand. Check Gmail > Drafts and send it from there if it is still waiting.')")),
         {"Not_released": ["Succeeded", "Failed"]}),
        ("Audit_not_released", audit("flow:desk-drafts", "email.release_failed", "gc_message", S(mid), props(draft=T("gc_gmaildraftid"))),
         {"Brief_not_sent": ["Succeeded", "Failed"]}),
    ]
    pending = [("New_or_released", condition(f"@empty({T('gc_gmaildraftid')})", create, release))]
    discarded = [
        ("Delete_gmail_draft", gmail("DeleteDraft", id=S(T("gc_gmaildraftid")))),
        ("Draft_gone", compose("deleted or already sent"), {"Delete_gmail_draft": ["Succeeded", "Failed"]}),
    ]
    steps = [
        ("Pending_or_discarded", condition(f"@equals({T('gc_draftstatus')}, {DRAFT['Pending']})", pending, discarded)),
    ]
    return flow(name, "A desk draft (gc_message, Draft, Pending) → gc_BuildEmailRaw → Gmail draft in the thread (new thread for a first enquiry; the "
                      "thread id is saved). A draft replaced by a newer one (Discarded) is deleted from Gmail. A draft the owner released by replying SEND is sent.",
                row_trigger("When_a_desk_draft_changes", "gc_message", 4, attributes="gc_draftstatus,gc_autosend", concurrency=1, conditions=[
                    f"@or(and(equals({T('gc_draftstatus')}, {DRAFT['Pending']}), empty({T('gc_gmaildraftid')})), "
                    f"and(equals({T('gc_draftstatus')}, {DRAFT['Discarded']}), not(empty({T('gc_gmaildraftid')}))), "
                    f"and(equals({T('gc_draftstatus')}, {DRAFT['Pending']}), not(empty({T('gc_gmaildraftid')})), equals({T('gc_autosend')}, true)))"]),
                steps, connections=(DV, GMAIL))


def desk_contract():
    name = "DealOS | Desk contract"
    cid = T("gc_contractid")
    steps = [
        ("Deal", get_row("gc_deals", S(T("_gc_deal_value")), select="gc_emaildesk,gc_name")),
        ("Desk_only", condition("@not(equals(body('Deal')?['gc_emaildesk'], true))", [("Marketplace_contract", terminate("Succeeded"))])),
        ("Contract_out", unbound("gc_DeskContract", retry_none=True, ContractId=S(cid))),
        ("Audit", audit("flow:desk-contract", "contract.sent_by_email", "gc_contract", S(cid), props(result="outputs('Contract_out')?['body/Result']"))),
    ]
    return flow(name, "Contract approved (Sent For Signature) on an email desk deal → gc_DeskContract: back-to-back contract PDFs. Scan and return: each attached "
                      "to a draft in its thread and briefed with the PDFs (reply SEND). contract.esign = docusign: nothing is sent; a 'Send for e-signature' "
                      "approval with both PDFs is opened instead.",
                row_trigger("When_a_contract_is_issued", "gc_contract", 3, filter=f"gc_status eq {CONTRACT['SentForSignature']}", attributes="gc_status", concurrency=1),
                steps)


def seller_discovery():
    """Sourcing has started for an email requirement → find more sellers on the web, contact the new ones, brief the owner."""
    name = "DealOS | Seller discovery"
    rid = T("gc_buyerrequirementid")
    found = "json(coalesce(outputs('Discover')?['body/Result'], '{}'))"
    steps = [
        ("Discover", unbound("gc_DiscoverSellers", retry_none=True, RequirementId=S(rid))),
        ("Found_some", condition("@greater(int(coalesce(outputs('Discover')?['body/Found'], 0)), 0)", [
            ("Source_again", unbound("gc_SourceRequirement", retry_none=True, RequirementId=S(rid))),
        ])),
        ("Brief_owner", unbound("gc_DeskBrief", retry_none=True, Subject=S(f"concat('Seller search: ', coalesce({T('gc_commoditytext')}, {T('gc_name')}))"),
                                Text=S(f"concat('Web search: ', coalesce({found}?['status'], ''), '. ', coalesce({found}?['reason'], ''), decodeUriComponent('%0A'), "
                                       f"'Companies found: ', string(coalesce({found}?['found'], 0)), ', with a published email: ', string(coalesce({found}?['with_email'], 0)), "
                                       f"decodeUriComponent('%0A'), 'New enquiries opened: ', string(coalesce(outputs('Source_again')?['body/Invited'], 0)), "
                                       f"decodeUriComponent('%0A%0A'), 'Leads without an email are listed in the task Find contacts for sourcing. Enquiry drafts are in Gmail > Drafts.')")),
         {"Found_some": ["Succeeded", "Failed", "Skipped"]}),
        ("Audit", audit("flow:seller-discovery", "requirement.discovered", "gc_buyerrequirement", S(rid), props(result=f"outputs('Discover')?['body/Result']"))),
    ]
    return flow(name, "Email requirement moves to desk stage Sourcing (first round sent to known leads) → gc_DiscoverSellers (Gemini + Google Search, "
                      "public business contacts) → new leads with an email get enquiries (gc_SourceRequirement) → briefing to the owner.",
                row_trigger("When_sourcing_starts", "gc_buyerrequirement", 3, filter=f"gc_deskstage eq {DESK_STAGE['Sourcing']}", attributes="gc_deskstage", concurrency=1,
                            conditions=[f"@and(equals({T('gc_source')}, {DESK_SOURCE_EMAIL}), empty({T('gc_discoveredon')}))"]),
                steps)


def buyer_discovery():
    """A seller lot is created (a seller looking for buyers) → find more buyers on the web, offer the lot to the new ones, brief the owner."""
    name = "DealOS | Buyer discovery"
    lid = T("gc_sellerlotid")
    found = "json(coalesce(outputs('Discover')?['body/Result'], '{}'))"
    steps = [
        ("Discover", unbound("gc_DiscoverBuyers", retry_none=True, LotId=S(lid))),
        ("Found_some", condition("@greater(int(coalesce(outputs('Discover')?['body/Found'], 0)), 0)", [
            ("Market_again", unbound("gc_MarketLot", retry_none=True, LotId=S(lid))),
        ])),
        ("Brief_owner", unbound("gc_DeskBrief", retry_none=True, Subject=S(f"concat('Buyer search: ', coalesce({T('gc_commoditytext')}, {T('gc_name')}))"),
                                Text=S(f"concat('Web search: ', coalesce({found}?['status'], ''), '. ', coalesce({found}?['reason'], ''), decodeUriComponent('%0A'), "
                                       f"'Companies found: ', string(coalesce({found}?['found'], 0)), ', with a published email: ', string(coalesce({found}?['with_email'], 0)), "
                                       f"decodeUriComponent('%0A'), 'New buyers offered the lot: ', string(coalesce(outputs('Market_again')?['body/Offered'], 0)), "
                                       f"decodeUriComponent('%0A%0A'), 'Offer drafts are in Gmail > Drafts. Buyers found without an email are saved as leads.')")),
         {"Found_some": ["Succeeded", "Failed", "Skipped"]}),
        ("Audit", audit("flow:buyer-discovery", "lot.discovered", "gc_sellerlot", S(lid), props(result=f"outputs('Discover')?['body/Result']"))),
    ]
    return flow(name, "A seller lot is created (a seller's offer with price and quantity, already offered to known buyers) → gc_DiscoverBuyers (Gemini + Google Search, "
                      "public business contacts) → the lot is offered to new buyer leads with an email (gc_MarketLot) → briefing to the owner.",
                row_trigger("When_a_lot_is_created", "gc_sellerlot", 1, concurrency=1), steps)


def desk_timers():
    """Every 15 minutes, day and night: close seller lots whose bid deadline passed (winners → Confirm deal), then the follow-ups
    (one chaser to sellers and buyers who have not answered, reminders before lot offers close). Both only draft; a person sends."""
    name = "DealOS | Desk timers"
    failed = lambda step: condition(f"@not(equals(outputs('{step}')?['statusCode'], 200))", [
        (f"Record_{step}_failure", flow_failure(name, step, S(f"coalesce(string(outputs('{step}')?['body']), 'operation failed')")))])
    steps = [
        ("Close_lots", unbound("gc_CloseLots", retry_none=True)),
        ("Lots_failed", failed("Close_lots"), {"Close_lots": ["Succeeded", "Failed", "TimedOut"]}),
        ("Follow_ups", unbound("gc_DeskFollowUps", retry_none=True), {"Lots_failed": ["Succeeded", "Skipped", "Failed"]}),
        ("Follow_ups_failed", failed("Follow_ups"), {"Follow_ups": ["Succeeded", "Failed", "TimedOut"]}),
    ]
    return flow(name, "Every 15 minutes: gc_CloseLots (timed seller lots past their bid deadline: highest buyer prices win while quantity lasts, Confirm deal tasks, "
                      "confirmation and 'not this time' drafts; no bid at the seller's price → best bid to the seller, lot goes open-ended) and "
                      "gc_DeskFollowUps (chasers after desk.chase_after_hours, lot reminders). Failures are recorded.",
                minutes_trigger("Every_15_minutes", 15), steps)


def approval_briefing():
    """Every decision a person must take is briefed by email with a reply code, so the owner can answer APPROVE / REJECT."""
    name = "DealOS | Approval briefing"
    tid = T("gc_reviewtaskid")
    steps = [
        ("Brief_task", unbound("gc_DeskBriefTask", retry_none=True, TaskId=S(tid))),
    ]
    return flow(name, "A review task is opened (Confirm deal, contract terms, e-signature, KYB tier, compliance, signed copy, ...) → gc_DeskBriefTask: a briefing "
                      "to the desk owner with the details, any documents and a reply code. Replying APPROVE / REJECT applies the decision (Review decisions); "
                      "Desk timers send one reminder after desk.approval_remind_hours.",
                row_trigger("When_a_decision_is_needed", "gc_reviewtask", 1, concurrency=1,
                            conditions=[f"@equals({T('gc_status')}, {REVIEW_STATUS['Open']})"]),
                steps)


def desk_tracking(kind):
    """Inspection or shipment status of an Email Desk deal changed → masked updates drafted to the buyer and the seller."""
    table, statuses = ("gc_inspection", [INSPECTION["Booked"], INSPECTION["Passed"], INSPECTION["Failed"]]) if kind == "inspection" \
        else ("gc_shipment", [B + 2, B + 3, B + 4, B + 6])  # Loading, In Transit, Arrived, Delivered
    name = f"DealOS | Desk tracking: {kind}"
    rid = T(f"{table}id")
    steps = [
        ("Track", unbound("gc_DeskTrack", retry_none=True, Kind=kind, RecordId=S(rid))),
        ("Audit", audit(f"flow:desk-tracking", f"{kind}.tracked", table, S(rid), props(result="outputs('Track')?['body/Result']"))),
    ]
    status_ok = "or(" + ", ".join(f"equals({T('gc_status')}, {v})" for v in statuses) + ")"
    return flow(name, f"A {kind} of a deal changes status → gc_DeskTrack: short masked updates drafted to the buyer and the seller in their threads "
                      "(each status once per side) and a briefing (reply SEND to send them). Other deals are skipped.",
                row_trigger(f"When_a_{kind}_status_changes", table, 4, attributes="gc_status", concurrency=1, conditions=[f"@{status_ok}"]),
                steps)


def desk_tracking_inspection():
    return desk_tracking("inspection")


def desk_tracking_shipment():
    return desk_tracking("shipment")


# ---------------------------------------------------------------- party checks: company registry + sanctions screening

# Registers are used once their connectors are deployed (tools/deploy_connector.py registries); Companies House only with its key bound.
_NAMES = _custom_connectors()
REGISTRY_ON = "gleif" in _NAMES
CH_ON = _NAMES.get("companieshouse_connected") == "yes"
CHECK_CONNECTIONS = (DV,) + ((GLEIF,) if REGISTRY_ON else ()) + ((COMPANIES_HOUSE,) if CH_ON else ())
ANY = ["Succeeded", "Failed", "Skipped", "TimedOut"]


def party_checks(acc, flow_name):
    """Company registry (GLEIF; Companies House for UK companies) → Company Registry KYB checks; then sanctions screening of the company,
    its contacts and the directors / owners the register lists. Never stops the flow: a failure is recorded and the agents still run."""
    rq = lambda k: f"outputs('Registry_terms')?['body/{k}']"
    steps = []
    if REGISTRY_ON:
        steps += [
            ("Registry_terms", unbound("gc_RegistryQuery", retry_none=True, AccountId=S(acc))),
            ("By_lei", condition(f"@not(empty({rq('Lei')}))", [("Gleif_lei", connector(GLEIF, "GetLeiRecord", lei=S(rq("Lei"))))])),
            ("By_number", condition(f"@not(empty({rq('RegistrationNumber')}))", [
                ("Gleif_number", connector(GLEIF, "SearchLeiRecords", **{"filter[entity.registeredAs]": S(rq("RegistrationNumber")), "page[size]": 5}))]), {"By_lei": ANY}),
            ("By_name", condition(f"@not(empty({rq('Search')}))", [
                ("Gleif_name", connector(GLEIF, "SearchLeiRecords", **{"filter[fulltext]": S(rq("Search")), "page[size]": 5}))]), {"By_number": ANY}),
        ]
        last = "By_name"
        if CH_ON:
            number = "first(body('Ch_search')?['items'])?['company_number']"
            steps.append(("Uk_company", condition(f"@equals({rq('Uk')}, true)", [
                ("Ch_search", connector(COMPANIES_HOUSE, "SearchCompanies", q=S(f"if(empty({rq('RegistrationNumber')}), {rq('Search')}, {rq('RegistrationNumber')})"),
                                        items_per_page=5)),
                ("Ch_found", condition("@greater(length(coalesce(body('Ch_search')?['items'], json('[]'))), 0)", [
                    ("Ch_profile", connector(COMPANIES_HOUSE, "GetCompany", company_number=S(number))),
                    ("Ch_officers", connector(COMPANIES_HOUSE, "ListOfficers", company_number=S(number)), {"Ch_profile": ANY}),
                    ("Ch_psc", connector(COMPANIES_HOUSE, "ListPsc", company_number=S(number)), {"Ch_officers": ANY}),
                ])),
            ]), {"By_name": ANY}))
            last = "Uk_company"
        ch = (lambda a: S(f"body('{a}')")) if CH_ON else (lambda a: "")
        steps += [
            ("Record_registry", unbound("gc_RegistryRecord", retry_none=True, AccountId=S(acc), GleifLei=S("body('Gleif_lei')"), GleifSearch=S("body('Gleif_number')"),
                                        GleifNameSearch=S("body('Gleif_name')"), ChProfile=ch("Ch_profile"), ChOfficers=ch("Ch_officers"), ChPsc=ch("Ch_psc")), {last: ANY}),
            ("Registry_failed", condition("@not(equals(outputs('Record_registry')?['statusCode'], 200))", [
                ("Record_registry_failure", flow_failure(flow_name, "Record_registry", S("coalesce(string(outputs('Record_registry')?['body']), 'registry check failed')")))]),
             {"Record_registry": ANY}),
            ("Screen_party", unbound("gc_ScreenParty", retry_none=True, AccountId=S(acc), ExtraNames=S("coalesce(outputs('Record_registry')?['body/Names'], '[]')")),
             {"Registry_failed": ANY}),
        ]
    else:
        steps.append(("Screen_party", unbound("gc_ScreenParty", retry_none=True, AccountId=S(acc))))
    steps.append(("Checks_done", condition("@not(equals(outputs('Screen_party')?['statusCode'], 200))", [
        ("Record_screening_failure", flow_failure(flow_name, "Screen_party", S("coalesce(string(outputs('Screen_party')?['body']), 'screening failed')")))]),
        {"Screen_party": ANY}))
    return steps


def party_recheck():
    """The party's registration number or name changed after onboarding (e.g. KYB documents arrived by email) → registry and screening again,
    then the KYB agents, which see the new results. (Party onboarding does the first round.)"""
    name = "DealOS | Party re-check"
    acc = T("accountid")
    roles = f"string(coalesce({T('gc_partyrole')}, ''))"
    steps = [
        *party_checks(acc, name),
        ("Seller", condition(f"@contains({roles}, '{PARTY['Seller']}')", run_agent("OnboardingKYB", S(acc), "party-recheck", name, on_fail="record")), {"Checks_done": ANY}),
        ("Buyer", condition(f"@contains({roles}, '{PARTY['Buyer']}')", run_agent("BuyerVerification", S(acc), "party-recheck", name, on_fail="record"))),
        ("Audit", audit("flow:party-recheck", "party.rechecked", "account", S(acc), props(registry="outputs('Record_registry')?['body/Summary']"))),
    ]
    return flow(name, "A party's registration number or name changes after onboarding → company registry and sanctions screening again → KYB agents.",
                row_trigger("When_party_details_change", "account", 3, attributes="gc_registrationnumber,name", concurrency=1, conditions=[
                    f"@and(or(contains({roles}, '{PARTY['Seller']}'), contains({roles}, '{PARTY['Buyer']}')), "
                    f"not(empty({T('gc_kybstatus')})), not(equals({T('gc_kybstatus')}, {KYB['NotStarted']})))"]),
                steps, connections=CHECK_CONNECTIONS)


def sanctions_lists():
    """Every day: the official sanctions lists (OFAC SDN + consolidated, UN, UK) are downloaded into their gc_sanctionlist rows and indexed;
    when any changed, every party is screened again (new possible matches → Screening Clearance task + briefing)."""
    name = "DealOS | Sanctions lists"
    each = "items('Each_list')"
    steps = [
        ("Lists", list_rows("gc_sanctionlists", select="gc_sanctionlistid,gc_source,gc_url,gc_aliasurl")),
        ("Each_list", foreach("@outputs('Lists')?['body/value']", [
            ("Get_list", http_get(S(f"{each}?['gc_url']"))),
            ("Store_list", upload_file("gc_sanctionlists", S(f"{each}?['gc_sanctionlistid']"), "gc_raw", "@body('Get_list')", S(f"concat({each}?['gc_source'], '.dat')"))),
            ("Has_aliases", condition(f"@not(empty({each}?['gc_aliasurl']))", [
                ("Get_aliases", http_get(S(f"{each}?['gc_aliasurl']"))),
                ("Store_aliases", upload_file("gc_sanctionlists", S(f"{each}?['gc_sanctionlistid']"), "gc_rawaliases", "@body('Get_aliases')",
                                              S(f"concat({each}?['gc_source'], '-aliases.dat')"))),
            ])),
            ("Load_list", unbound("gc_SanctionsLoad", retry_none=True, ListId=S(f"{each}?['gc_sanctionlistid']"))),
            ("List_failed", condition("@not(equals(outputs('Load_list')?['statusCode'], 200))", [
                ("Record_list_failure", flow_failure(name, S(f"concat('Load ', {each}?['gc_source'])"),
                                                     S("coalesce(string(outputs('Load_list')?['body']), string(outputs('Get_list')?['statusCode']), 'download failed')")))]),
             {"Load_list": ANY}),
        ])),
        # Only does work when a list changed since the last re-screen
        ("Rescreen", unbound("gc_ScreenParties", retry_none=True), {"Each_list": ["Succeeded", "Failed"]}),
        ("Audit", audit("flow:sanctions-lists", "sanctions.lists_loaded", "gc_sanctionlist", EMPTY_GUID, props(rescreen="outputs('Rescreen')?['body/Result']"))),
    ]
    return flow(name, "Every day 02:30 UTC: OFAC SDN and consolidated, UN Security Council and UK sanctions lists downloaded (HTTP) into gc_sanctionlist → "
                      "gc_SanctionsLoad (name index) → gc_ScreenParties when a list changed.", daily_trigger("Every_day_0230_UTC", 2, 30), steps)


def kyb_recheck():
    """A party's KYB is approved (Passed) → its email desk deals waiting at Compliance Check are checked again at once."""
    name = "DealOS | KYB passed: compliance re-check"
    acc = T("accountid")
    each = "items('Each_waiting_deal')"
    decision = "json(coalesce(outputs('Run_Compliance')?['body/Result'], '{}'))?['result']?['decision']"
    steps = [
        ("Waiting_deals", list_rows("gc_deals", select="gc_dealid,gc_name", top=20,
                                    filter=f"gc_emaildesk eq true and gc_stage eq {STAGE['ComplianceCheck']} and (_gc_buyer_value eq {S(acc)} or _gc_seller_value eq {S(acc)})")),
        ("Each_waiting_deal", foreach("@outputs('Waiting_deals')?['body/value']", [
            ("Run_Compliance", unbound("gc_Agent_Compliance", retry_none=True, SubjectId=S(f"{each}?['gc_dealid']"),
                                       Input=json.dumps({"trigger": "flow:kyb-recheck"}))),
            ("Clear_now", condition(f"@and(equals(outputs('Run_Compliance')?['body/Status'], 'Succeeded'), equals({decision}, 'Clear'))", [
                ("To_contracting", transition(S(f"{each}?['gc_dealid']"), STAGE["Contracting"], "Compliance clear after KYB passed")),
                ("Brief_clear", unbound("gc_DeskBrief", retry_none=True, Subject=S(f"concat('Compliance clear: ', {each}?['gc_name'])"),
                                        Text="KYB is approved and the compliance re-check is Clear, so the deal moved to Contracting. The contract terms come to you for approval next. "
                                             "Any older compliance task for this deal can be closed; approving it changes nothing.")),
            ], [
                ("Brief_not_clear", unbound("gc_DeskBrief", retry_none=True, Subject=S(f"concat('Compliance still open: ', {each}?['gc_name'])"),
                                            Text=S("concat('KYB is approved but the compliance re-check says ', coalesce(" + decision + ", 'it could not run'), ': ', "
                                                   "coalesce(outputs('Run_Compliance')?['body/Summary'], ''), decodeUriComponent('%0A%0A'), "
                                                   "'Answer the compliance task (screening, permits) when ready.')"))),
            ]), {"Run_Compliance": ["Succeeded", "Failed", "TimedOut"]}),
        ])),
    ]
    return flow(name, "A party's KYB status becomes Passed (a person approved the tier) → its email desk deals at Compliance Check run the Compliance agent again; "
                      "Clear → Contracting; otherwise the owner is briefed with what is still missing.",
                row_trigger("When_KYB_passes", "account", 3, attributes="gc_kybstatus", concurrency=1, conditions=[f"@equals({T('gc_kybstatus')}, {KYB['Passed']})"]),
                steps)


# ---------------------------------------------------------------- e-signature (DocuSign; deployed with --esign once the connection exists)

ESIGN_RECIPIENT_TYPE = "signers"   # DocuSign recipient type for a signer
ESIGN_TAB_TYPE = "signHereTabs"     # signature tab, placed with an anchor string
ESIGN_COMBINED = "combined"         # all documents of the envelope as one signed PDF


def desk_esign_send():
    """The owner approved 'Send for e-signature' → one DocuSign envelope per side: contract PDF, party signs first, our signatory countersigns."""
    name = "DealOS | Desk e-signature send"
    cid = T("gc_contractid")
    env = "items('Each_envelope')"
    signer = "items('Each_signer')"
    account = S("first(outputs('Account_setting')?['body/value'])?['gc_value']")
    created = "body('Create_envelope')?['envelopeId']"
    steps = [
        ("Account_setting", list_rows("gc_platformsettings", filter="gc_key eq 'esign.docusign.account_id'", select="gc_value", top=1)),
        ("Envelopes", unbound("gc_EsignEnvelopes", retry_none=True, ContractId=S(cid))),
        ("Each_envelope", foreach("@json(outputs('Envelopes')?['body/Result'])", [
            ("Create_envelope", docusign("CreateBlankEnvelopeV2", accountId=account, emailSubject=S(f"{env}?['subject']"), body__emailBlurb=S(f"{env}?['blurb']"))),
            ("Add_contract", docusign("AddDocumentsToEnvelope", accountId=account, envelopeId=S(created),
                                      body__documents=f"@createArray(json(concat('{{\"documentBase64\":\"', {env}?['document_base64'], '\",\"fileExtension\":\"pdf\",\"name\":\"', {env}?['document_name'], '\",\"documentId\":\"1\"}}')))")),
            ("Each_signer", foreach(f"@{env}?['signers']", [
                ("Add_signer", docusign("AddRecipientToEnvelopeV2", accountId=account, envelopeId=S(created), recipientType=ESIGN_RECIPIENT_TYPE,
                                        recipientId=S(f"{signer}?['recipient_id']"), routingOrder=S(f"{signer}?['routing_order']"),
                                        additionalRecipientParams__name=S(f"{signer}?['name']"), additionalRecipientParams__email=S(f"{signer}?['email']"))),
                ("Add_signature_tab", docusign("AddRecipientTabs", accountId=account, envelopeId=S(created), recipientId=S(f"{signer}?['recipient_id']"),
                                               tabType=ESIGN_TAB_TYPE, tabDetails__anchorString=S(f"{signer}?['anchor']"), tabDetails__anchorXOffset="110",
                                               tabDetails__anchorYOffset="-4", tabDetails__anchorUnits="pixels", tabDetails__anchorIgnoreIfNotPresent="false")),
            ])),
            ("Send_envelope", docusign("SendDraftEnvelope", accountId=account, envelopeId=S(created))),
            ("Record_envelope", unbound("gc_EsignRecord", retry_none=True, ContractId=S(cid), Side=S(f"{env}?['side']"), EnvelopeId=S(created))),
        ])),
    ]
    catch = [
        ("Mark_failed", update("gc_contracts", S(cid), gc_esignstatus=ESIGN["Failed"])),
        ("Brief_failed", unbound("gc_DeskBrief", Subject=S(f"concat('E-signature not sent: ', coalesce({T('gc_name')}, ''))"),
                                 Text=S("concat('DocuSign could not send the contracts: ', take(string(result('Try')), 1500), decodeUriComponent('%0A%0A'), "
                                        "'Check the DocuSign connection and esign.docusign.account_id / contract.signatory, then set the contract e-signature status to Sending again.')"))),
    ]
    return flow(name, "Contract e-signature status → Sending (the owner approved 'Send for e-signature') → gc_EsignEnvelopes → per side a DocuSign envelope: the contract PDF, "
                      "the party signs first, our signatory (contract.signatory) countersigns → sent → gc_EsignRecord. Any failure: status Failed and a briefing.",
                row_trigger("When_esign_is_approved", "gc_contract", 3, attributes="gc_esignstatus", concurrency=1, conditions=[f"@equals({T('gc_esignstatus')}, {ESIGN['Sending']})"]),
                steps, connections=(DV, DOCUSIGN), catch_extra=catch)


def desk_esign_status():
    """Every 15 minutes: envelopes out for signature → their signers' status; completed → signed PDF stored, both → contract Signed."""
    name = "DealOS | Desk e-signature status"
    item_ = "items('Each_envelope')"
    account = S("first(outputs('Account_setting')?['body/value'])?['gc_value']")
    signers = "coalesce(body('Signers')?['signers'], json('[]'))"
    steps = [
        ("Account_setting", list_rows("gc_platformsettings", filter="gc_key eq 'esign.docusign.account_id'", select="gc_value", top=1)),
        ("Out_for_signature", unbound("gc_EsignPending", retry_none=True)),
        ("Each_envelope", foreach("@json(coalesce(outputs('Out_for_signature')?['body/Result'], '[]'))", [
            ("Signers", docusign("GetRecipientStatus", accountId=account, envelopeId=S(f"{item_}?['envelope']"))),
            ("Signed_signers", {"type": "Query", "inputs": {"from": "@" + signers, "where": "@equals(toLower(coalesce(item()?['status'], '')), 'completed')"}}),
            ("All_signed", condition(f"@and(greater(length({signers}), 0), equals(length({signers}), length(body('Signed_signers'))))", [
                ("Signed_pdf", docusign("GetDocumentsV2", accountId=account, envelopeId=S(f"{item_}?['envelope']"), documentId=ESIGN_COMBINED)),
                ("Store_signed", unbound("gc_EsignUpdate", retry_none=True, ContractId=S(f"{item_}?['contract']"), Side=S(f"{item_}?['side']"),
                                         RecipientsJson="@{string(body('Signers'))}", SignedPdf="@{base64(body('Signed_pdf'))}")),
            ], [
                ("Update_status", unbound("gc_EsignUpdate", retry_none=True, ContractId=S(f"{item_}?['contract']"), Side=S(f"{item_}?['side']"),
                                          RecipientsJson="@{string(body('Signers'))}")),
            ])),
        ])),
    ]
    return flow(name, "Every 15 minutes: DocuSign envelopes still out (gc_EsignPending) → List recipients → all signed: the signed PDF (combined) is stored and, when both "
                      "sides are done, the contract becomes Signed (Contract signed → inspection); declined → task and briefing (gc_EsignUpdate).",
                minutes_trigger("Every_15_minutes", 15), steps, connections=(DV, DOCUSIGN))


def email_test_kit():
    """Test only (not in ALL; deployed with --tests and switched on only while tools/mail_test.py runs):
    HTTP-triggered access to the DealOS Gmail connector, so tests can put mail into the inbox and send drafts."""
    name = "DealOS | Email Desk test kit"
    inp = lambda k: f"triggerBody()?['{k}']"
    respond = lambda action: {"type": "Response", "kind": "Http", "inputs": {"statusCode": 200, "body": f"@body('{action}')"}}
    trig = {"manual": {"type": "Request", "kind": "Http", "inputs": {"schema": {"type": "object", "properties": {
        "action": {"type": "string"}, "raw": {"type": "string"}, "thread_id": {"type": "string"}, "id": {"type": "string"},
        "q": {"type": "string"}}}}}}
    cases = [
        ("Insert", "insert", [("Insert_message", gmail("InsertMessage", internalDateSource="receivedTime", body__raw=f"@{inp('raw')}",
                                                       body__threadId=f"@{inp('thread_id')}", body__labelIds="@createArray('INBOX', 'UNREAD')")),
                              ("Inserted", respond("Insert_message"))]),
        ("Send", "send_draft", [("Send_draft", gmail("SendDraft", body__id=f"@{inp('id')}")), ("Sent", respond("Send_draft"))]),
        ("Get", "get", [("Get_message", gmail("GetMessage", id=f"@{inp('id')}", format="metadata")), ("Got", respond("Get_message"))]),
        ("List", "list", [("List_messages", gmail("ListMessages", q=f"@{inp('q')}", maxResults=20)), ("Listed", respond("List_messages"))]),
        ("Labels", "labels", [("List_labels", gmail("ListLabels")), ("Labelled", respond("List_labels"))]),
        ("Me", "profile", [("Get_profile", gmail("GetProfile")), ("Profiled", respond("Get_profile"))]),
        ("Draft", "create_draft", [("Create_draft", gmail("CreateDraft", body__message__raw=f"@{inp('raw')}", body__message__threadId=f"@{inp('thread_id')}")),
                                   ("Drafted", respond("Create_draft"))]),
        ("Label", "add_label", [("Add_label", gmail("ModifyMessage", id=f"@{inp('id')}", body__addLabelIds=f"@createArray({inp('q')})")),
                                ("Label_added", respond("Add_label"))]),
        ("Unlabel", "remove_label", [("Remove_label", gmail("ModifyMessage", id=f"@{inp('id')}", body__removeLabelIds=f"@createArray({inp('q')})")),
                                     ("Label_removed", respond("Remove_label"))]),
    ]
    actions = {"Do": dict(switch(f"@{inp('action')}", cases), runAfter={})}
    return {"name": name, "id": flow_id(name), "connections": (GMAIL,),
            "description": "Test only: HTTP access to Gmail for tools/mail_test.py (insert mail, send a draft, read labels). Keep off outside tests.",
            "definition": definition(trig, actions)}


TESTS = [email_test_kit]

# E-signature flows need the DocuSign connection (gc_docusign): python3 tools/deploy_flows.py --esign once it is bound.
ESIGN_FLOWS = [desk_esign_send, desk_esign_status]

ALL = [document_intake, party_onboarding, offer_pricing, offer_accepted, terms_agreed, compliance_check, contracting, contract_signed, deal_cancelled, review_decisions, approvals, daily_digest, inspection_booking, inspection_result, flow_failure_triage, mailbox_sync, trade_desk, desk_drafts, desk_contract, seller_discovery, buyer_discovery, desk_timers,
       approval_briefing, desk_tracking_inspection, desk_tracking_shipment, kyb_recheck, party_recheck, sanctions_lists]

# Flows removed with the website and marketplace (7 Oct 2026); deploy_flows.py turns them off and deletes them in Dev.
RETIRED = ['DealOS | Commission invoice', 'DealOS | Daily deadlines', 'DealOS | Daily sweep', 'DealOS | Deal settled', 'DealOS | Dispute closed', 'DealOS | Dispute opened', 'DealOS | Escrow funded', 'DealOS | Listing verification', 'DealOS | Match accepted', 'DealOS | Match notifications', 'DealOS | Milestone progress', 'DealOS | New listing matching', 'DealOS | Notify party', 'DealOS | RFQ invite answered', 'DealOS | RFQ invite sent', 'DealOS | RFQ matching', 'DealOS | Rating received', 'DealOS | Release settled']

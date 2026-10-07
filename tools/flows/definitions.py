"""DealOS cloud flows that compose the gc_Agent_* custom APIs and the deterministic operations.

Each function returns a dict: name, description, id, definition, connections.
Existing flows keep their workflow id; new flows get a stable id from their name.
"""
import copy
import glob
import json
import os

from .lib import (AUTH, B, CONTRACT, GMAIL, gmail, minutes_trigger, scope, DISPUTE, DV, EMPTY_GUID, FAILURE, INSPECTION, INSPECTION_RESULT, INVITE, INVOICE, INVOICE_KIND, KYB,
                  LISTING, MATCH, MESSAGE, MESSAGE_KIND, MILESTONE, MILESTONE_INSPECTION, MILESTONE_PENDING, MILESTONE_TYPE, NOTIFY_FLOW, OFFER,
                  OUTLOOK, OVERLAY, OVERLAY_DISPUTED, OVERLAY_ON_HOLD, PARSE, PARTY, PAYMENT, PURPOSE, RELEASE, REQUIREMENT, REQUIREMENT_EXPIRED,
                  RESOLUTION, REVIEW_STATUS, ROOT, SCOPE, SHIPMENT, STAGE, TIER, TIER_TRADE_VERIFIED, VERIFICATION_CONFIRMED,
                  VERIFICATION_METHOD_OWN_INSPECTION, agent, agent_ok, agent_result, audit, chain, compose, condition, create, daily_trigger,
                  definition, flow_failure, flow_id, foreach, get_row, hourly_trigger, list_rows, notify, review_task, row_trigger, run_agent, switch,
                  terminate, transition, try_catch, unbound, update)

DESK_SOURCE_EMAIL = B + 1  # gc_buyerrequirement.gc_source = Email
DESK_STAGE = dict(Qualifying=B, Sourcing=B + 1, Quoted=B + 2, Negotiating=B + 3, Agreed=B + 4, ContractSent=B + 5, Signed=B + 6, Closed=B + 7)
DRAFT = dict(Pending=B, Sent=B + 1, Discarded=B + 2)
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

def notify_party():
    name = NOTIFY_FLOW
    field = lambda title, desc: {"title": title, "type": "string", "x-ms-dynamically-added": True, "description": desc, "x-ms-content-hint": "TEXT"}
    trig = {"manual": {"type": "Request", "kind": "Button", "inputs": {"schema": {"type": "object", "properties": {
        "text": field("AccountId", "Party (account) to notify"), "text_1": field("Subject", "Email subject"),
        "text_2": field("Body", "Plain-text body"), "text_3": field("Event", "Event key for the audit trail, e.g. offer.created")},
        "required": ["text", "text_1", "text_2", "text_3"]}}}}
    inp = lambda k: f"triggerBody()?['{k}']"
    setting = lambda key: list_rows("gc_platformsettings", filter=f"gc_key eq '{key}'", select="gc_value", top=1)
    recipient = ("if(empty(" + first_value("Redirect_setting", "gc_value") + "), coalesce(" + first_value("Party", "emailaddress1") +
                 ", first(outputs('Party')?['body/value'])?['primarycontactid']?['emailaddress1'], ''), " + first_value("Redirect_setting", "gc_value") + ")")
    enabled = f"equals(toLower(coalesce({first_value('Enabled_setting', 'gc_value')}, 'false')), 'true')"
    details = props(to="outputs('Recipient')", subject=inp("text_1"), body=inp("text_2"))
    send = {"type": "OpenApiConnection", "inputs": {
        "host": {"connectionName": OUTLOOK, "operationId": "SendEmailV2", "apiId": f"/providers/Microsoft.PowerApps/apis/{OUTLOOK}"},
        "parameters": {"emailMessage/To": S("outputs('Recipient')"), "emailMessage/Subject": S(inp("text_1")),
                       "emailMessage/Body": "<p>" + S(f"replace({inp('text_2')}, decodeUriComponent('%0A'), '<br>')") + "</p>"
                                            "<p style=\"color:#666\">DealOS – verified mineral trade. Please do not reply to this email.</p>",
                       "emailMessage/Importance": "Normal"},
        "authentication": AUTH}}
    steps = [
        ("Enabled_setting", setting("notifications.email.enabled")),
        ("Redirect_setting", setting("notifications.email.redirect")),
        ("Party", list_rows("accounts", filter=f"accountid eq {or_empty_guid(inp('text'))}",
                            select="name,emailaddress1", expand="primarycontactid($select=emailaddress1)", top=1)),
        ("Recipient", compose(S(recipient))),
        ("Can_send", condition(f"@and({enabled}, not(empty(outputs('Recipient'))))",
                               [("Send_email", send),
                                ("Audit_sent", audit("flow:notify", "notify.sent", "account", S(inp("text")), props(event=inp("text_3"), **{"to": "outputs('Recipient')", "subject": inp("text_1")}))),
                                ("Status_sent", compose("sent"))],
                               [("Audit_recorded", audit("flow:notify", "notify.recorded", "account", S(inp("text")), props(event=inp("text_3"), to="outputs('Recipient')",
                                                                                                                            subject=inp("text_1"), body=inp("text_2"))))])),
    ]
    actions = try_catch(name, steps)
    # A child flow must always answer its caller; failures are recorded, never thrown back.
    actions["Catch"]["actions"].pop("Fail_run")
    actions["Respond"] = {"type": "Response", "kind": "PowerApp", "runAfter": {"Catch": ["Succeeded", "Skipped"]},
                          "inputs": {"statusCode": 200,
                                     "body": {"status": "@{if(equals(result('Try')?[0]?['status'], 'Failed'), 'failed', "
                                                        "if(equals(toLower(coalesce(" + first_value('Enabled_setting', 'gc_value') + ", 'false')), 'true'), 'sent', 'recorded'))}"},
                                     "schema": {"type": "object", "properties": {"status": {"title": "status", "x-ms-dynamically-added": True, "type": "string"}}}}}
    return {"name": name, "id": flow_id(name), "connections": (DV, OUTLOOK),
            "description": "Child flow: emails a party (account email, else primary contact) when notifications.email.enabled is true; "
                           "otherwise records the message in the audit trail. notifications.email.redirect sends everything to one test inbox.",
            "definition": definition(trig, actions)}


# ---------------------------------------------------------------- supply side

def listing_verification():
    name = "DealOS | Listing verification"
    lid = T("gc_listingid")
    doc = "items('For_each_document')?['gc_documentid']"
    steps = [
        ("Mark_in_verification", update("gc_listings", S(lid), gc_status=LISTING["InVerification"])),
        ("Pending_documents", list_rows("gc_documents", filter=f"_gc_listing_value eq {S(lid)} and gc_parsestatus eq {PARSE['Pending']}",
                                        select="gc_documentid,gc_name")),
        ("For_each_document", foreach("@outputs('Pending_documents')?['body/value']", [
            ("Run_DocumentIntelligence", agent("DocumentIntelligence", S(doc), "listing-verification")),
            ("Document_failed", condition(f"@not({agent_ok('Run_DocumentIntelligence')[1:]})",
                                          [("Mark_document_failed", update("gc_documents", S(doc), gc_parsestatus=PARSE["Failed"]))]),
             {"Run_DocumentIntelligence": ["Succeeded", "Failed", "TimedOut"]}),
        ])),
        *run_agent("ListingVerification", S(lid), "listing-verification", name),
        ("Audit", audit("flow:listing-verification", "listing.verified", "gc_listing", S(lid),
                        props(recommendation=agent_result("ListingVerification", "recommendation"), documents="length(outputs('Pending_documents')?['body/value'])",
                              summary="outputs('Run_ListingVerification')?['body/Summary']"))),
    ]
    return flow(name, "Listing submitted → Document Intelligence on each pending document → Listing Verification (DD1). "
                      "The agent opens the publish / message approvals; the Review decisions flow applies them.",
                row_trigger("When_a_listing_is_submitted", "gc_listing", 4, filter=f"gc_status eq {LISTING['Submitted']}", attributes="gc_status", concurrency=1),
                steps)


def document_intake():
    name = "DealOS | Document intake"
    did, acc, lst, deal = T("gc_documentid"), T("_gc_account_value"), T("_gc_listing_value"), T("_gc_deal_value")
    seller_role, buyer_role = str(PARTY["Seller"]), str(PARTY["Buyer"])
    roles = "string(coalesce(body('Party')?['gc_partyrole'], ''))"
    steps = [
        # One upload fires several "modified" events; a queued run must not reprocess a document another run already parsed.
        ("Document_now", get_row("gc_documents", S(did), select="gc_parsestatus")),
        ("Already_processed", condition(f"@not(equals({body('Document_now', 'gc_parsestatus')}, {PARSE['Pending']}))", [("Nothing_to_do", terminate("Succeeded"))])),
        ("Listing", list_rows("gc_listings", filter=f"gc_listingid eq {or_empty_guid(lst)}", select="gc_status", top=1)),
        ("Listing_status", compose("@" + first_value("Listing", "gc_status"))),
        ("Left_to_listing_flow", condition(f"@equals(outputs('Listing_status'), {LISTING['Submitted']})", [("Listing_flow_handles_it", terminate("Succeeded"))])),
        ("Run_DocumentIntelligence", agent("DocumentIntelligence", S(did), "document-intake")),
        ("Document_failed", condition(f"@not({agent_ok('Run_DocumentIntelligence')[1:]})", [
            ("Mark_document_failed", update("gc_documents", S(did), gc_parsestatus=PARSE["Failed"])),
            ("Record_document_failure", flow_failure(name, "Run_DocumentIntelligence", S("coalesce(outputs('Run_DocumentIntelligence')?['body/Summary'], 'agent call failed')"))),
            ("Stop_document_failed", terminate("Failed", "Document Intelligence failed; see Flow failures")),
        ]), {"Run_DocumentIntelligence": ["Succeeded", "Failed", "TimedOut"]}),
        ("Seller_answered", condition(f"@equals(outputs('Listing_status'), {LISTING['NeedsInfo']})", [
            ("Back_in_verification", update("gc_listings", S(lst), gc_status=LISTING["InVerification"])),
            *run_agent("ListingVerification", S(lst), "document-intake", name),
        ], [
            ("Party_document", condition(f"@and(not(empty({acc})), empty({lst}), empty({deal}))", [
                ("Party", get_row("accounts", S(acc), select="gc_partyrole")),
                ("Seller_party", condition(f"@contains({roles}, '{seller_role}')", run_agent("OnboardingKYB", S(acc), "document-intake", name, on_fail="record"))),
                ("Buyer_party", condition(f"@contains({roles}, '{buyer_role}')", run_agent("BuyerVerification", S(acc), "document-intake", name, on_fail="record"))),
            ])),
        ])),
        ("Audit", audit("flow:document-intake", "document.processed", "gc_document", S(did),
                        props(summary="outputs('Run_DocumentIntelligence')?['body/Summary']", listing_status="outputs('Listing_status')"))),
    ]
    return flow(name, "A document's file is uploaded (or it is set back to Pending) → Document Intelligence. Then: seller answered an ask "
                      "(listing Needs Info) → Listing Verification; KYB document of a party → Onboarding KYB / Buyer Verification.",
                row_trigger("When_a_document_file_is_ready", "gc_document", 4, attributes="gc_file,gc_parsestatus", concurrency=1,
                            conditions=[f"@and(equals({T('gc_parsestatus')}, {PARSE['Pending']}), not(empty({T('gc_file')})))"]),
                steps)


def party_onboarding():
    name = "DealOS | Party onboarding"
    acc = T("accountid")
    roles = f"string(coalesce({T('gc_partyrole')}, ''))"
    steps = [
        ("Mark_in_progress", update("accounts", S(acc), gc_kybstatus=KYB["InProgress"])),
        ("Seller", condition(f"@contains({roles}, '{PARTY['Seller']}')", run_agent("OnboardingKYB", S(acc), "party-onboarding", name, on_fail="record"))),
        ("Buyer", condition(f"@contains({roles}, '{PARTY['Buyer']}')", run_agent("BuyerVerification", S(acc), "party-onboarding", name, on_fail="record"))),
        ("Audit", audit("flow:party-onboarding", "party.onboarding", "account", S(acc), props(roles=roles))),
    ]
    return flow(name, "A party becomes a seller or buyer (and KYB has not started) → KYB status In Progress → Onboarding KYB and/or Buyer Verification. "
                      "The agents record pending checks and draft the document request for approval.",
                row_trigger("When_a_party_needs_KYB", "account", 4, attributes="gc_partyrole", concurrency=1, conditions=[
                    f"@and(or(contains({roles}, '{PARTY['Seller']}'), contains({roles}, '{PARTY['Buyer']}')), "
                    f"or(empty({T('gc_kybstatus')}), equals({T('gc_kybstatus')}, {KYB['NotStarted']})))"]),
                steps)


# ---------------------------------------------------------------- demand and matching

def rfq_matching():
    name = "DealOS | RFQ matching"
    rid = T("gc_buyerrequirementid")
    steps = [
        *run_agent("Matching", S(rid), "rfq-matching", name),
        ("Audit", audit("flow:rfq-matching", "rfq.matched", "gc_buyerrequirement", S(rid),
                        props(proposed=f"length(coalesce({agent_result('Matching', 'proposed')}, json('[]')))", summary="outputs('Run_Matching')?['body/Summary']"))),
    ]
    return flow(name, "Buyer requirement opened → Matching agent scores published listings and proposes matches (both sides notified by Match notifications).",
                row_trigger("When_an_RFQ_opens", "gc_buyerrequirement", 4, filter=f"gc_status eq {REQUIREMENT['Open']}", attributes="gc_status", concurrency=1,
                            conditions=[f"@not(equals({T('gc_source')}, {DESK_SOURCE_EMAIL}))"]),
                steps)


def listing_published_matching():
    name = "DealOS | New listing matching"
    lid = T("gc_listingid")
    req = "items('Match_each_RFQ')?['gc_buyerrequirementid']"
    steps = [
        ("Open_RFQs", list_rows("gc_buyerrequirements", select="gc_buyerrequirementid", top=5, orderby="createdon desc",
                                filter=f"gc_status eq {REQUIREMENT['Open']} and _gc_commodity_value eq {or_empty_guid(T('_gc_commodity_value'))}")),
        ("Match_each_RFQ", foreach("@outputs('Open_RFQs')?['body/value']", [
            ("Run_Matching", agent("Matching", S(req), "new-listing-matching")),
            ("Matching_failed", condition(f"@not({agent_ok('Run_Matching')[1:]})",
                                          [("Record_matching_failure", flow_failure(name, "Run_Matching", S("coalesce(outputs('Run_Matching')?['body/Summary'], 'agent call failed')")))]),
             {"Run_Matching": ["Succeeded", "Failed", "TimedOut"]}),
        ])),
        ("Audit", audit("flow:new-listing-matching", "listing.matched", "gc_listing", S(lid), props(rfqs="length(outputs('Open_RFQs')?['body/value'])"))),
    ]
    return flow(name, "Listing published → re-run Matching for up to 5 newest open RFQs of the same commodity.",
                row_trigger("When_a_listing_is_published", "gc_listing", 3, filter=f"gc_status eq {LISTING['Published']}", attributes="gc_status", concurrency=1),
                steps)


def match_notifications():
    name = "DealOS | Match notifications"
    mid = T("gc_matchid")
    score = S(f"formatNumber(coalesce({T('gc_score')}, 0), '0')")
    steps = [
        ("Requirement", get_row("gc_buyerrequirements", S(T("_gc_requirement_value")), select="gc_name,_gc_buyer_value")),
        ("Listing", get_row("gc_listings", S(T("_gc_listing_value")), select="gc_name,_gc_seller_value")),
        ("Notify_buyer", notify(S("body('Requirement')?['_gc_buyer_value']"), "New verified match for your requirement",
                                f"A listing matches your requirement '{S(body('Requirement', 'gc_name'))}': "
                                f"{S(body('Listing', 'gc_name'))} (match score {score}).\n"
                                "Open DealOS to review it and opt in. The seller's identity stays hidden until a contract is signed.", "match.proposed")),
        ("Notify_seller", notify(S("body('Listing')?['_gc_seller_value']"), "A buyer requirement matches your listing",
                                 f"A buyer is looking for material like your listing '{S(body('Listing', 'gc_name'))}' "
                                 f"(match score {score}).\nOpen DealOS to review the requirement and opt in. The buyer's identity stays hidden until a contract is signed.",
                                 "match.proposed")),
    ]
    return flow(name, "Match proposed → tell the buyer and the seller (identities stay masked) so both can opt in.",
                row_trigger("When_a_match_is_proposed", "gc_match", 1), steps)


def match_to_deal():
    name = "DealOS | Match accepted"
    mid, req_id, lst_id = T("gc_matchid"), T("_gc_requirement_value"), T("_gc_listing_value")
    rq = lambda c: f"body('Requirement')?['{c}']"
    ls = lambda c: f"body('Listing')?['{c}']"
    deal = "outputs('Create_deal')?['body/gc_dealid']"
    both = f"and(equals({T('gc_buyeroptin')}, true), equals({T('gc_selleroptin')}, true), not(equals({T('gc_status')}, {MATCH['Mutual']})))"
    steps = [
        ("Mark_mutual", update("gc_matchs", S(mid), gc_status=MATCH["Mutual"])),
        ("Existing_deal", list_rows("gc_deals", filter=f"_gc_requirement_value eq {S(req_id)} and _gc_listing_value eq {S(lst_id)}", select="gc_dealid", top=1)),
        ("Already_connected", condition("@greater(length(outputs('Existing_deal')?['body/value']), 0)", [("Deal_exists", terminate("Succeeded"))])),
        ("Requirement", get_row("gc_buyerrequirements", S(req_id),
                                select="gc_name,_gc_buyer_value,gc_quantity,gc_quantityunit,gc_incoterm,gc_currency,_gc_destinationcountry_value")),
        ("Listing", get_row("gc_listings", S(lst_id), select="gc_name,_gc_seller_value,_gc_commodity_value,_gc_origincountry_value,gc_currency,gc_incoterm,gc_namedplace")),
        ("Create_deal", create("gc_deals", gc_name=S(f"take(concat('RFQ: ', {ls('gc_name')}), 100)"), gc_stage=STAGE["Inquiry"],
                               gc_currency=S(f"coalesce({rq('gc_currency')}, {ls('gc_currency')}, 'USD')"),
                               gc_quantity=f"@{rq('gc_quantity')}", gc_quantityunit=f"@{rq('gc_quantityunit')}",
                               gc_incoterm=f"@coalesce({rq('gc_incoterm')}, {ls('gc_incoterm')})", gc_namedplace=S(f"coalesce({ls('gc_namedplace')}, '')"),
                               **{"gc_buyer@odata.bind": S(f"concat('accounts(', {rq('_gc_buyer_value')}, ')')"),
                                  "gc_seller@odata.bind": S(f"concat('accounts(', {ls('_gc_seller_value')}, ')')"),
                                  "gc_listing@odata.bind": S(f"concat('gc_listings(', {lst_id}, ')')"),
                                  "gc_Requirement@odata.bind": S(f"concat('gc_buyerrequirements(', {req_id}, ')')")})),
        ("Set_commodity", condition(f"@not(empty({ls('_gc_commodity_value')}))", [
            ("Link_commodity", update("gc_deals", S(deal), **{"gc_commodity@odata.bind": S(f"concat('gc_commodities(', {ls('_gc_commodity_value')}, ')')")}))])),
        ("Set_origin", condition(f"@not(empty({ls('_gc_origincountry_value')}))", [
            ("Link_origin", update("gc_deals", S(deal), **{"gc_origincountry@odata.bind": S(f"concat('gc_countries(', {ls('_gc_origincountry_value')}, ')')")}))])),
        ("Set_destination", condition(f"@not(empty({rq('_gc_destinationcountry_value')}))", [
            ("Link_destination", update("gc_deals", S(deal), **{"gc_destinationcountry@odata.bind": S(f"concat('gc_countries(', {rq('_gc_destinationcountry_value')}, ')')")}))])),
        ("Notify_buyer", notify(S(rq("_gc_buyer_value")), "You are connected with a verified seller",
                                f"Both sides opted in on '{S(ls('gc_name'))}'. Use DealOS to request and compare offers with landed cost. "
                                "Identities stay masked until a contract is signed.", "match.mutual")),
        ("Notify_seller", notify(S(ls("_gc_seller_value")), "You are connected with a verified buyer",
                                 f"Both sides opted in on your listing '{S(ls('gc_name'))}'. Use DealOS to send your offer. "
                                 "Identities stay masked until a contract is signed.", "match.mutual")),
        ("Audit", audit("flow:match-accepted", "match.mutual", "gc_match", S(mid), props(deal=deal))),
    ]
    return flow(name, "Buyer and seller both opted in on a match → match Mutual → one deal (Inquiry) per seller, linked to the RFQ.",
                row_trigger("When_both_sides_opt_in", "gc_match", 3, attributes="gc_buyeroptin,gc_selleroptin", concurrency=1, conditions=["@" + both]),
                steps)


# ---------------------------------------------------------------- offers

def offer_pricing():
    name = "DealOS | Offer pricing"
    oid, did = T("gc_offerid"), T("_gc_deal_value")
    dl = lambda c: body("Deal", c)
    counterparty = f"if(equals({T('_gc_fromparty_value')}, {dl('_gc_seller_value')}), {dl('_gc_buyer_value')}, {dl('_gc_seller_value')})"
    steps = [
        ("Price_offer", unbound("gc_CalculatePriceQuote", OfferId=S(oid))),
        ("Deal", get_row("gc_deals", S(did), select="gc_name,gc_stage,_gc_buyer_value,_gc_seller_value,gc_emaildesk")),
        ("First_offer", condition(f"@equals({dl('gc_stage')}, {STAGE['Inquiry']})", [("To_negotiation", transition(S(did), STAGE["Negotiation"], "First offer received"))])),
        ("Marketplace_offer", condition(f"@not(equals({dl('gc_emaildesk')}, true))", [
            *run_agent("Pricing", S(oid), "offer-pricing", name, on_fail="record"),
            ("Notify_counterparty", notify(S(counterparty), "New offer on your deal",
                                           f"A new offer was made on '{S(dl('gc_name'))}': {S(T('gc_price'))} {S(T('gc_currency'))} per unit for "
                                           f"{S(T('gc_quantity'))} units, {S(incoterm_label(T('gc_incoterm')))}.\n"
                                           "Open DealOS to see your landed cost or net payout, then accept, counter or decline.", "offer.created")),
        ])),
        ("Audit", audit("flow:offer-pricing", "offer.priced", "gc_offer", S(oid),
                        props(summary="outputs('Price_offer')?['body/Summary']", explanation="coalesce(outputs('Run_Pricing')?['body/Summary'], 'email desk deal')"))),
    ]
    return flow(name, "Offer created → gc_CalculatePriceQuote (engine numbers) → deal to Negotiation on the first offer → Pricing agent explanation → notify the other party.",
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


def notify_both(prefix, subject, body, event, buyer=None, seller=None):
    return [(f"{prefix}_buyer", notify(S(buyer or T("_gc_buyer_value")), subject, body, event)),
            (f"{prefix}_seller", notify(S(seller or T("_gc_seller_value")), subject, body, event))]


def terms_agreed():
    name = "DealOS | Terms agreed"
    did = T("gc_dealid")
    steps = [
        *notify_both("Notify", "Terms agreed", f"Terms are agreed on '{S(T('gc_name'))}'. Compliance checks run next; "
                                               "you will hear from us before the contract is issued.", "deal.terms_agreed"),
        ("To_compliance", transition(S(did), STAGE["ComplianceCheck"], "Terms agreed")),
        ("Compliance_refused", condition(allowed("To_compliance"), [("Stuck_task", stuck_task(f"concat('Deal stuck at Terms Agreed: ', {T('gc_name')})", did, "To_compliance"))])),
    ]
    return flow(name, "Deal at Terms Agreed → tell both parties → move to Compliance Check.", deal_trigger("When_terms_are_agreed", "TermsAgreed"), steps)


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
    dl = lambda c: body("Deal", c)
    steps = [
        ("Stamp_signed_date", condition(f"@empty({T('gc_signedon')})", [("Set_signed_on", update("gc_contracts", S(cid), gc_signedon="@utcNow()"))])),
        ("To_signed", transition(S(did), STAGE["Signed"], "Contract signed")),
        ("Signed_refused", condition(allowed("To_signed"), [
            ("Signed_stuck_task", stuck_task("concat('Contract signed but deal not moved: ', " + T("gc_name") + ")", did, "To_signed")),
            ("Stop_not_signed", terminate("Succeeded"))])),
        ("Desk_deal", get_row("gc_deals", S(did), select="gc_emaildesk,_gc_requirement_value")),
        ("No_escrow_for_desk", condition("@equals(body('Desk_deal')?['gc_emaildesk'], true)", [
            ("Desk_requirement_signed", condition("@not(empty(body('Desk_deal')?['_gc_requirement_value']))", [
                ("Set_desk_signed", update("gc_buyerrequirements", S("body('Desk_deal')?['_gc_requirement_value']"), gc_deskstage=DESK_STAGE["Signed"]))])),
            ("Audit_desk_signed", audit("flow:contract-signed", "deal.signed", "gc_deal", S(did), props(escrow="'none: email desk deal (payment between the parties per contract)'"))),
            ("Stop_desk_signed", terminate("Succeeded")),
        ])),
        ("Open_escrow", unbound("gc_OpenEscrow", retry_none=True, DealId=S(did))),
        ("To_awaiting_funding", transition(S(did), STAGE["AwaitingFunding"], "Escrow opened")),
        ("Funding_refused", condition(allowed("To_awaiting_funding"), [
            ("Funding_stuck_task", stuck_task("concat('Escrow opened but deal not moved: ', " + T("gc_name") + ")", did, "To_awaiting_funding"))])),
        *run_agent("Payment", S(did), "contract-signed", name, on_fail="record"),
        ("Deal", get_row("gc_deals", S(did), select="gc_name,_gc_buyer_value,_gc_seller_value")),
        # The buyer sees only what they must pay; commission and plan details stay internal.
        ("Escrow", get_row("gc_payments", S("outputs('Open_escrow')?['body/PaymentId']"), select="gc_amountdue,gc_currency,gc_fundingdeadline")),
        ("Notify_buyer", notify(S(dl("_gc_buyer_value")), "Contract signed: please fund escrow",
                                f"The contract for '{S(dl('gc_name'))}' is signed. Please fund escrow with {S(body('Escrow', 'gc_currency'))} "
                                f"{S(FMT_AMOUNT.format(body('Escrow', 'gc_amountdue')))} by {S(FMT_DATE.format(body('Escrow', 'gc_fundingdeadline')))}.\n"
                                "Fund the escrow account by the deadline shown in DealOS. Money is released to the seller in stages, "
                                "only after the agreed milestones are verified.", "deal.signed")),
        ("Notify_seller", notify(S(dl("_gc_seller_value")), "Contract signed",
                                 f"The contract for '{S(dl('gc_name'))}' is signed. We will tell you as soon as the buyer has funded escrow.", "deal.signed")),
        ("Audit", audit("flow:contract-signed", "deal.escrow_opened", "gc_deal", S(did), props(escrow="outputs('Open_escrow')?['body/Summary']"))),
    ]
    return flow(name, "Contract set to Signed → deal Signed → gc_OpenEscrow (payment + tranches, deterministic) → Awaiting Funding → "
                      "Payment agent check → buyer asked to fund.",
                row_trigger("When_a_contract_is_signed", "gc_contract", 3, filter=f"gc_status eq {CONTRACT['Signed']}", attributes="gc_status", concurrency=1),
                steps)


def escrow_funded():
    name = "DealOS | Escrow funded"
    did = T("_gc_deal_value")
    dl = lambda c: body("Deal", c)
    steps = [
        ("To_funded", transition(S(did), STAGE["Funded"], "Escrow partner confirmed funding")),
        ("Funded_refused", condition(allowed("To_funded"), [
            ("Funded_stuck_task", stuck_task("concat('Escrow funded but deal not moved: ', " + T("gc_name") + ")", did, "To_funded")),
            ("Stop_not_funded", terminate("Succeeded"))])),
        *run_agent("Logistics", S(did), "escrow-funded", name),
        ("Deal", get_row("gc_deals", S(did), select="gc_name,_gc_buyer_value,_gc_seller_value")),
        ("Notify_buyer", notify(S(dl("_gc_buyer_value")), "Escrow funded",
                                f"Escrow for '{S(dl('gc_name'))}' is funded. Inspection and shipment are being arranged; track the milestones in DealOS.", "deal.funded")),
        ("Notify_seller", notify(S(dl("_gc_seller_value")), "Buyer has funded escrow",
                                 f"The buyer has funded escrow for '{S(dl('gc_name'))}'. Prepare the material for inspection; the document checklist is in DealOS.",
                                 "deal.funded")),
        ("Audit", audit("flow:escrow-funded", "deal.funded", "gc_deal", S(did), props(logistics="outputs('Run_Logistics')?['body/Summary']"))),
    ]
    return flow(name, "Payment set to Funded (Finance, later the partner webhook) → deal Funded → Logistics agent (checklist, milestones, booking approval) → notify.",
                row_trigger("When_escrow_is_funded", "gc_payment", 3, filter=f"gc_state eq {PAYMENT['Funded']}", attributes="gc_state", concurrency=1),
                steps)


def milestone_progress():
    name = "DealOS | Milestone progress"
    did, mtype = T("_gc_deal_value"), T("gc_type")
    stage = "body('Deal')?['gc_stage']"
    steps = [
        ("Deal", get_row("gc_deals", S(did), select="gc_name,gc_stage")),
        ("Shipped", condition(f"@and(or(equals({mtype}, {MILESTONE_TYPE['BLIssued']}), equals({mtype}, {MILESTONE_TYPE['Departure']})), equals({stage}, {STAGE['Funded']}))",
                              [("To_in_transit", transition(S(did), STAGE["InTransit"], "Departure / B/L milestone"))])),
        ("Arrived", condition(f"@and(equals({mtype}, {MILESTONE_TYPE['Delivered']}), equals({stage}, {STAGE['InTransit']}))",
                              [("To_delivered", transition(S(did), STAGE["Delivered"], "Delivery milestone"))])),
        *run_agent("Payment", S(did), "milestone-progress", name, on_fail="record"),
        ("Audit", audit("flow:milestone-progress", "milestone.progress", "gc_milestone", S(T("gc_milestoneid")),
                        props(type=T("gc_type"), status=T("gc_status"),
                              payment="outputs('Run_Payment')?['body/Summary']"))),
    ]
    return flow(name, "Milestone Completed or Verified → move the deal (B/L or departure → In Transit; delivery → Delivered) → Payment agent checks "
                      "which releases are ready and opens Fund Release approvals for Finance.",
                row_trigger("When_a_milestone_progresses", "gc_milestone", 3, attributes="gc_status", concurrency=1, conditions=[
                    f"@or(equals({T('gc_status')}, {MILESTONE['Completed']}), equals({T('gc_status')}, {MILESTONE['Verified']}))"]),
                steps)


def release_settled():
    name = "DealOS | Release settled"
    rid, pid = T("gc_paymentreleaseid"), T("_gc_payment_value")
    deal = "body('Payment')?['_gc_deal_value']"
    steps = [
        ("Payment", get_row("gc_payments", S(pid), select="gc_name,gc_currency,_gc_deal_value")),
        ("Deal", get_row("gc_deals", S(deal), select="gc_name,_gc_seller_value")),
        ("Notify_seller", notify(S("body('Deal')?['_gc_seller_value']"), "Payment released",
                                 f"A payment for '{S(body('Deal', 'gc_name'))}' is settled: "
                                 f"{S(body('Payment', 'gc_currency'))} {S(FMT_AMOUNT.format(T('gc_netamount')))} to you after commission of "
                                 f"{S(FMT_AMOUNT.format(T('gc_commissionamount')))}, from a gross release of {S(FMT_AMOUNT.format(T('gc_amount')))}.", "release.settled")),
        ("Unsettled", list_rows("gc_paymentreleases", select="gc_paymentreleaseid",
                                filter=f"_gc_payment_value eq {S(pid)} and gc_status ne {RELEASE['Settled']} and gc_status ne {B + 5}")),
        ("All_settled", condition("@equals(length(outputs('Unsettled')?['body/value']), 0)", [
            ("Payment_settled", update("gc_payments", S(pid), gc_state=PAYMENT["Settled"])),
            ("To_settled", transition(S(deal), STAGE["Settled"], "All releases settled")),
        ])),
        ("Audit", audit("flow:release-settled", "release.settled", "gc_paymentrelease", S(rid), props(net=T("gc_netamount"), commission=T("gc_commissionamount")))),
    ]
    return flow(name, "Release Settled → tell the seller the net amount → when every release is settled: payment Settled and deal Settled.",
                row_trigger("When_a_release_is_settled", "gc_paymentrelease", 3, filter=f"gc_status eq {RELEASE['Settled']}", attributes="gc_status", concurrency=1),
                steps)


def deal_cancelled():
    name = "DealOS | Deal cancelled"
    did = T("gc_dealid")
    funded = " or ".join(f"gc_state eq {PAYMENT['Funded'] + i}" for i in range(3))
    steps = [
        ("Release_holds", unbound("gc_ReleaseDeal", retry_none=True, DealId=S(did))),
        ("Funded_escrow", list_rows("gc_payments", select="gc_paymentid,gc_amountfunded", filter=f"_gc_deal_value eq {S(did)} and ({funded})")),
        ("Refund_needed", condition("@greater(length(outputs('Funded_escrow')?['body/value']), 0)", [
            ("Refund_task", review_task(f"concat('Refund escrow: ', {T('gc_name')})", "Refund", "Finance", props(payments="outputs('Funded_escrow')?['body/value']"), deal=S(did)))])),
        *notify_both("Notify", "Deal closed", f"The deal '{S(T('gc_name'))}' has been closed and will not proceed. "
                                              "Any material reserved for it has been released. Contact us in DealOS if you have questions.", "deal.cancelled"),
        ("Audit", audit("flow:deal-cancelled", "deal.cancelled", "gc_deal", S(did), props(released="outputs('Release_holds')?['body/Summary']"))),
    ]
    return flow(name, "Deal Cancelled (lot sold elsewhere, compliance, timeout) → gc_ReleaseDeal frees the lot and rejects open offers → "
                      "Finance refund task if escrow was funded → notify both parties.", deal_trigger("When_a_deal_is_cancelled", "Cancelled"), steps)


# ---------------------------------------------------------------- human decisions

def review_decisions():
    name = "DealOS | Review decisions"
    tid, deal, listing, account = T("gc_reviewtaskid"), T("_gc_deal_value"), T("_gc_listing_value"), T("_gc_account_value")
    pl = lambda k: f"json(coalesce({T('gc_payload')}, '{{}}'))?['{k}']"
    approved = f"equals({T('gc_status')}, {REVIEW_STATUS['Approved']})"
    msg = "body('Message')"
    dl = lambda c: body("Contract_deal", c)
    cases = [
        ("Listing_publish", PURPOSE["ListingPublish"], [
            ("Publish_decision", condition("@" + approved,
                                           [("Publish_listing", update("gc_listings", S(listing), gc_status=LISTING["Published"], gc_publishedon="@utcNow()"))],
                                           [("Back_to_seller", update("gc_listings", S(listing), gc_status=LISTING["NeedsInfo"]))]))]),
        ("Message_send", PURPOSE["MessageSend"], [
            ("Send_decision", condition("@" + approved, [
                ("Approve_message", update("gc_generatedmessages", S(pl("messageId")), gc_status=MESSAGE["Approved"])),
                ("Message", get_row("gc_generatedmessages", S(pl("messageId")), select="gc_name,gc_kind,gc_text")),
                ("Request_to_party", condition(f"@and(equals({msg}?['gc_kind'], {MESSAGE_KIND['Source']}), not(empty({account})))", [
                    ("Send_to_party", notify(S(account), S(f"{msg}?['gc_name']"), S(f"{msg}?['gc_text']"), "message.request")),
                    ("Was_sent", condition("@equals(body('Send_to_party')?['status'], 'sent')",
                                           [("Mark_sent", update("gc_generatedmessages", S(pl("messageId")), gc_status=MESSAGE["Sent"]))])),
                    ("Listing_waits", condition(f"@not(empty({listing}))", [("Listing_needs_info", update("gc_listings", S(listing), gc_status=LISTING["NeedsInfo"]))])),
                ])),
            ], [("Reject_message", update("gc_generatedmessages", S(pl("messageId")), gc_status=MESSAGE["Rejected"]))]))]),
        ("Tier_upgrade", PURPOSE["TierUpgrade"], [
            ("Tier_decision", condition("@" + approved, [
                ("Set_tier", update("accounts", S(account), gc_trusttier=f"@int({pl('tierValue')})",
                                    gc_kybstatus=f"@if(greaterOrEquals(int({pl('tierValue')}), {TIER['KYBVerified']}), {KYB['Passed']}, {KYB['InProgress']})"))]))]),
        ("Screening_clearance", PURPOSE["ScreeningClearance"], [
            ("Deal_or_party", condition(f"@not(empty({deal}))", [
                ("Clearance_decision", condition("@" + approved, [
                    ("Screened_deal", get_row("gc_deals", S(deal), select="gc_name,gc_statusoverlay")),
                    ("Lift_compliance_hold", condition(f"@equals(body('Screened_deal')?['gc_statusoverlay'], {OVERLAY['ComplianceHold']})",
                                                       [("Clear_overlay", update("gc_deals", S(deal), gc_statusoverlay=OVERLAY["None_"]))])),
                    ("To_contracting", transition(S(deal), STAGE["Contracting"], "Compliance cleared by officer")),
                    ("Contracting_refused", condition(allowed("To_contracting"), [
                        ("Cleared_stuck_task", stuck_task("concat('Cleared but contracting refused: ', coalesce(body('Screened_deal')?['gc_name'], ''))", deal, "To_contracting"))])),
                ], [("Cancel_deal", transition(S(deal), STAGE["Cancelled"], "Compliance not cleared"))])),
            ], [
                ("Party_hold", condition(f"@not(empty({account}))", [("Set_compliance_hold", update("accounts", S(account), gc_compliancehold=f"@not({approved})"))])),
            ]))]),
        ("Contract_issue", PURPOSE["ContractIssue"], [
            ("Issue_decision", condition("@" + approved, [
                ("Send_for_signature", update("gc_contracts", S(pl("contractId")), gc_status=CONTRACT["SentForSignature"])),
                ("Contract_deal", get_row("gc_deals", S(deal), select="gc_name,_gc_buyer_value,_gc_seller_value")),
                *notify_both("Contract_ready", "Contract ready for signature",
                             f"The contract for '{S(dl('gc_name'))}' is approved and will reach you for e-signature. Please review and sign it in DealOS.",
                             "contract.issued", buyer=dl("_gc_buyer_value"), seller=dl("_gc_seller_value")),
            ], [("Void_contract", update("gc_contracts", S(pl("contractId")), gc_status=CONTRACT["Void"]))]))]),
        ("Fund_release", PURPOSE["FundRelease"], [
            ("Release_decision", condition("@" + approved, [("Instruct_release", unbound("gc_InstructRelease", retry_none=True, ReleaseId=S(pl("releaseId"))))]))]),
        ("Desk_decisions", PURPOSE["Other"], [
            ("Desk_accept_offer", condition(f"@and({approved}, equals({pl('action')}, 'desk.accept_offer'))", [
                ("Set_buyer_price", update("gc_deals", S(pl("dealId")), gc_buyerprice=f"@float({pl('buyerPrice')})")),
                ("Accept_offer", update("gc_offers", S(pl("offerId")), gc_status=OFFER["Accepted"])),
            ])),
            ("Desk_contract_signed", condition(f"@and({approved}, equals({pl('action')}, 'desk.contract_signed'))", [
                ("Mark_contract_signed", update("gc_contracts", S(pl("contractId")), gc_status=CONTRACT["Signed"], gc_signedon="@utcNow()")),
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
    return flow(name, "Review task Approved or Rejected (via the Approvals flow or directly in the admin app) → apply it: publish listing, send the "
                      "approved request to the party, set trust tier, clear screening, issue contract, instruct a fund release (gc_InstructRelease), plan a shipment.",
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
    tried["Audit"]["runAfter"] = {"Link_decider": ["Succeeded"]}
    tried["Audit"]["inputs"]["parameters"]["item/gc_hash"] = "set-by-invariants-plugin"
    return {"name": name, "id": EXISTING[name], "connections": (DV, "shared_approvals"), "definition": defn,
            "description": "Review task opened (Approval or Review) → Teams/Outlook approval to the role's assignee → decision recorded on the task. "
                           "The Review decisions flow applies it."}


# ---------------------------------------------------------------- operations

def daily_sweep():
    """The existing Daily sweep, unchanged; listed here so deploy_flows.py turns it on with the rest."""
    name = "DealOS | Daily sweep"
    path = glob.glob(os.path.join(ROOT, "solutions", "DealOS", "Workflows", "DealOSDailysweep-*.json"))[0]
    with open(path) as f:
        defn = json.load(f)["properties"]["definition"]
    return {"name": name, "id": EXISTING[name], "connections": (DV,), "definition": defn,
            "description": "Every day 02:00 UTC: re-resolve evidence for listings with expired facts, expire stale offers."}


def daily_digest():
    name = "DealOS | Daily digest"
    sections = agent_result("AdminSupervisor", "sections")
    actions = agent_result("AdminSupervisor", "actions")
    recipients = ("if(empty(" + first_value("Recipients_setting", "gc_value") + "), coalesce(json(coalesce(" + first_value("Assignee_setting", "gc_value") +
                  ", '{}'))?['default'], ''), " + first_value("Recipients_setting", "gc_value") + ")")
    send = {"type": "OpenApiConnection", "inputs": {
        "host": {"connectionName": OUTLOOK, "operationId": "SendEmailV2", "apiId": f"/providers/Microsoft.PowerApps/apis/{OUTLOOK}"},
        "parameters": {"emailMessage/To": S("outputs('Recipients')"),
                       "emailMessage/Subject": "DealOS daily digest: " + S(f"coalesce({agent_result('AdminSupervisor', 'headline')}, 'operations')"),
                       "emailMessage/Body": "<p>" + S("outputs('Run_AdminSupervisor')?['body/Summary']") + "</p>" +
                                            "<h3>Actions</h3><ul>" + S("join(body('Action_lines'), '')") + "</ul>" + S("join(body('Section_lines'), '')"),
                       "emailMessage/Importance": "Normal"},
        "authentication": AUTH}}
    steps = [
        *run_agent("AdminSupervisor", None, "daily-digest", name),
        ("Section_lines", {"type": "Select", "inputs": {"from": f"@coalesce({sections}, json('[]'))",
                                                        "select": "@concat('<h3>', item()?['title'], '</h3><ul><li>', join(coalesce(item()?['items'], json('[]')), '</li><li>'), '</li></ul>')"}}),
        ("Action_lines", {"type": "Select", "inputs": {"from": f"@coalesce({actions}, json('[]'))",
                                                       "select": "@concat('<li><b>', item()?['priority'], '</b> – ', item()?['action'], ' (', item()?['owner_role'], ')</li>')"}}),
        ("Recipients_setting", list_rows("gc_platformsettings", filter="gc_key eq 'admin.digest.recipients'", select="gc_value", top=1)),
        ("Assignee_setting", list_rows("gc_platformsettings", filter="gc_key eq 'approvals.assignees'", select="gc_value", top=1)),
        ("Recipients", compose(S(recipients))),
        ("Has_recipients", condition("@not(empty(outputs('Recipients')))", [("Send_digest", send)])),
    ]
    return flow(name, "Every day 03:00 UTC → AdminSupervisor digest (approvals waiting, stuck deals, failures, AI spend) emailed to admin.digest.recipients "
                      "(default: the approvals.assignees default).", daily_trigger("Every_day_0300_UTC", 3), steps, connections=(DV, OUTLOOK))


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


def rfq_invite_sent():
    name = "DealOS | RFQ invite sent"
    iid = T("gc_rfqinviteid")
    rq = lambda c: body("Requirement", c)
    ls = lambda c: body("Listing", c)
    steps = [
        ("Requirement", get_row("gc_buyerrequirements", S(T("_gc_requirement_value")), select="gc_name,gc_quantity,gc_validuntil")),
        ("Listing", get_row("gc_listings", S(T("_gc_listing_value")), select="gc_name,_gc_seller_value")),
        ("Stamp_invite", update("gc_rfqinvites", S(iid), gc_invitedon=f"@coalesce({T('gc_invitedon')}, utcNow())",
                                **{"gc_Seller@odata.bind": S(f"concat('accounts(', coalesce({T('_gc_seller_value')}, {ls('_gc_seller_value')}), ')')")})),
        ("Notify_seller", notify(S(ls("_gc_seller_value")), "A buyer invites you to quote",
                                 f"A verified buyer invites you to quote on '{S(rq('gc_name'))}' for your listing '{S(ls('gc_name'))}'.\n"
                                 "Open the invite in DealOS to accept (a deal opens where you can send your offer) or decline. "
                                 "The buyer's identity stays hidden until a contract is signed.", "rfq.invited")),
        ("Audit", audit("flow:rfq-invite", "rfq.invited", "gc_rfqinvite", S(iid), props(requirement=T("_gc_requirement_value"), listing=T("_gc_listing_value")))),
    ]
    return flow(name, "RFQ invite created (buyer sends one RFQ to N sellers from the portal) → seller and invite date stamped → seller told (buyer masked).",
                row_trigger("When_a_seller_is_invited", "gc_rfqinvite", 1, filter=f"gc_status eq {INVITE['Invited']}",
                            conditions=[f"@not(empty({T('_gc_listing_value')}))"]), steps)


def rfq_invite_answered():
    name = "DealOS | RFQ invite answered"
    iid, req_id, lst_id = T("gc_rfqinviteid"), T("_gc_requirement_value"), T("_gc_listing_value")
    rq = lambda c: body("Requirement", c)
    ls = lambda c: body("Listing", c)
    deal = "outputs('Create_deal')?['body/gc_dealid']"
    accepted = [
        ("Existing_deal", list_rows("gc_deals", filter=f"_gc_requirement_value eq {S(req_id)} and _gc_listing_value eq {S(lst_id)}", select="gc_dealid", top=1)),
        ("Need_deal", condition(none_found("Existing_deal"), [
            ("Create_deal", create("gc_deals", gc_name=S(f"take(concat('RFQ: ', {ls('gc_name')}), 100)"), gc_stage=STAGE["Inquiry"],
                                   gc_currency=S(f"coalesce({rq('gc_currency')}, {ls('gc_currency')}, 'USD')"),
                                   gc_quantity=f"@{rq('gc_quantity')}", gc_quantityunit=f"@{rq('gc_quantityunit')}",
                                   gc_incoterm=f"@coalesce({rq('gc_incoterm')}, {ls('gc_incoterm')})", gc_namedplace=S(f"coalesce({ls('gc_namedplace')}, '')"),
                                   **{"gc_buyer@odata.bind": S(f"concat('accounts(', {rq('_gc_buyer_value')}, ')')"),
                                      "gc_seller@odata.bind": S(f"concat('accounts(', {ls('_gc_seller_value')}, ')')"),
                                      "gc_listing@odata.bind": S(f"concat('gc_listings(', {lst_id}, ')')"),
                                      "gc_Requirement@odata.bind": S(f"concat('gc_buyerrequirements(', {req_id}, ')')")})),
            ("Set_commodity", condition(f"@not(empty({ls('_gc_commodity_value')}))", [
                ("Link_commodity", update("gc_deals", S(deal), **{"gc_commodity@odata.bind": S(f"concat('gc_commodities(', {ls('_gc_commodity_value')}, ')')")}))])),
            ("Link_invite", update("gc_rfqinvites", S(iid), **{"gc_Deal@odata.bind": S(f"concat('gc_deals(', {deal}, ')')")})),
        ])),
        ("Notify_buyer_accepted", notify(S(rq("_gc_buyer_value")), "A seller accepted your RFQ invite",
                                         f"A seller accepted your invite for '{S(rq('gc_name'))}' (listing '{S(ls('gc_name'))}'). Their offer will appear in DealOS, "
                                         "with landed cost, so you can compare it with other sellers. Identities stay masked until a contract is signed.", "rfq.accepted")),
    ]
    declined = [
        ("Notify_buyer_declined", notify(S(rq("_gc_buyer_value")), "A seller declined your RFQ invite",
                                         f"A seller declined your invite for '{S(rq('gc_name'))}' (listing '{S(ls('gc_name'))}'). "
                                         "Your other invites are unaffected; you can invite more sellers from the RFQ in DealOS.", "rfq.declined")),
    ]
    steps = [
        ("Requirement", get_row("gc_buyerrequirements", S(req_id),
                                select="gc_name,_gc_buyer_value,gc_quantity,gc_quantityunit,gc_incoterm,gc_currency")),
        ("Listing", get_row("gc_listings", S(lst_id), select="gc_name,_gc_seller_value,_gc_commodity_value,gc_currency,gc_incoterm,gc_namedplace")),
        ("Stamp_response", update("gc_rfqinvites", S(iid), gc_respondedon="@utcNow()")),
        ("Accepted_or_declined", condition(f"@equals({T('gc_status')}, {INVITE['Accepted']})", accepted, declined)),
        ("Audit", audit("flow:rfq-invite", "rfq.answered", "gc_rfqinvite", S(iid), props(status=T("gc_status")))),
    ]
    return flow(name, "Seller accepts an RFQ invite → one deal (Inquiry) linked to the RFQ and listing, buyer told; declines → buyer told. Identities stay masked.",
                row_trigger("When_an_invite_is_answered", "gc_rfqinvite", 3, attributes="gc_status", concurrency=1, conditions=[
                    f"@and(not(empty({T('_gc_listing_value')})), or(equals({T('gc_status')}, {INVITE['Accepted']}), equals({T('gc_status')}, {INVITE['Declined']})))"]),
                steps)


# ---------------------------------------------------------------- inspection (plan #13)

def inspection_booking():
    name = "DealOS | Inspection booking"
    did = T("gc_dealid")
    insp = "outputs('Create_inspection')?['body/gc_inspectionid']"
    steps = [
        ("Wrong_stage_for_deal", condition(f"@not(equals({T('gc_stage')}, if(equals({T('gc_emaildesk')}, true), {STAGE['Signed']}, {STAGE['Funded']})))",
                                           [("Not_this_deal", terminate("Succeeded"))])),
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
        *notify_both("Notify", "Independent inspection is being arranged",
                     f"'{S(T('gc_name'))}' is ready for inspection. DealOS is booking an independent inspection (sampling, assay, weighing and sealing) "
                     "before shipment. You will be told the date.", "inspection.requested"),
        ("Audit", audit("flow:inspection", "inspection.requested", "gc_deal", S(did), props(inspection=insp))),
    ]
    return flow(name, "Deal Funded (marketplace) or Signed (email desk, no escrow) → one inspection (Requested) for the deal's lot → Logistics Coordinator task "
                      "to book an independent agency → both parties told.",
                row_trigger("When_a_deal_is_ready_for_inspection", "gc_deal", 3, filter=f"gc_stage eq {STAGE['Funded']} or gc_stage eq {STAGE['Signed']}",
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
        *notify_both("Passed", "Inspection passed",
                     f"The independent inspection for '{S(dl('gc_name'))}' found the material within specification. Shipment can now be booked.",
                     "inspection.passed", buyer=dl("_gc_buyer_value"), seller=dl("_gc_seller_value")),
    ]
    failed = [
        ("Off_spec", update("gc_inspections", S(iid), gc_result=INSPECTION_RESULT["OffSpec"])),
        ("Hold_deal", update("gc_deals", S(did), gc_statusoverlay=OVERLAY_ON_HOLD)),
        ("Off_spec_task", review_task(f"concat('Inspection off-spec: ', {dl('gc_name')})", "Other", "DealManager",
                                      props(inspectionId=iid, findings=T("gc_findings"),
                                            options="'Renegotiate the price through a new offer round, or cancel the deal (escrow is refunded). Lift the On Hold overlay when decided.'"),
                                      deal=S(did), kind="Review")),
        *notify_both("Failed", "Inspection found deviations",
                     f"The independent inspection for '{S(dl('gc_name'))}' found deviations from the agreed specification. Payments on this deal are on hold; "
                     "a DealOS deal manager will contact you about the next step (price adjustment or cancellation with refund).",
                     "inspection.failed", buyer=dl("_gc_buyer_value"), seller=dl("_gc_seller_value")),
    ]
    steps = [
        ("Deal", get_row("gc_deals", S(did), select="gc_name,_gc_buyer_value,_gc_seller_value")),
        ("Passed_or_failed", condition(f"@equals({T('gc_status')}, {INSPECTION['Passed']})", passed, failed)),
        ("Audit", audit("flow:inspection", "inspection.result", "gc_inspection", S(iid), props(status=T("gc_status"), deal=did))),
    ]
    return flow(name, "Inspection set to Passed → verification record (independent inspection, Confirmed) + Inspection milestone Completed with the report → both told. "
                      "Failed → deal On Hold + Deal Manager task (renegotiate or cancel with refund) → both told.",
                row_trigger("When_an_inspection_concludes", "gc_inspection", 3, attributes="gc_status", concurrency=1, conditions=[
                    f"@or(equals({T('gc_status')}, {INSPECTION['Passed']}), equals({T('gc_status')}, {INSPECTION['Failed']}))"]),
                steps)


# ---------------------------------------------------------------- disputes (plan #17)

def dispute_opened():
    name = "DealOS | Dispute opened"
    xid, did = T("gc_disputeid"), T("_gc_deal_value")
    dl = lambda c: body("Deal", c)
    task = "outputs('Dispute_task')?['body/gc_reviewtaskid']"
    steps = [
        ("Deal", get_row("gc_deals", S(did), select="gc_name,_gc_buyer_value,_gc_seller_value")),
        ("Pause_money", update("gc_deals", S(did), gc_statusoverlay=OVERLAY_DISPUTED)),
        ("Dispute_task", review_task(f"concat('Dispute: ', {dl('gc_name')})", "Other", "DealManager",
                                     props(disputeId=xid, reason=T("gc_reason"), description=T("gc_description"), amount=T("gc_amountdisputed")),
                                     deal=S(did), kind="Review")),
        ("Under_review", update("gc_disputes", S(xid), gc_status=DISPUTE["UnderReview"], gc_openedon=f"@coalesce({T('gc_openedon')}, utcNow())",
                                **{"gc_ReviewTask@odata.bind": S(f"concat('gc_reviewtasks(', {task}, ')')")})),
        *notify_both("Notify", "A dispute was raised",
                     f"A dispute was raised on '{S(dl('gc_name'))}'. Payments on this deal are paused while DealOS reviews it; "
                     "you may be asked for evidence. Escrow funds stay with the partner until it is resolved.",
                     "dispute.opened", buyer=dl("_gc_buyer_value"), seller=dl("_gc_seller_value")),
        ("Audit", audit("flow:dispute", "dispute.opened", "gc_dispute", S(xid), props(deal=did, reason=T("gc_reason")))),
    ]
    return flow(name, "Dispute raised → deal overlay Disputed (gc_InstructRelease refuses every release) → Deal Manager task → dispute Under Review → both told.",
                row_trigger("When_a_dispute_is_raised", "gc_dispute", 1, concurrency=1), steps)


def dispute_closed():
    name = "DealOS | Dispute closed"
    xid, did = T("gc_disputeid"), T("_gc_deal_value")
    dl = lambda c: body("Deal", c)
    open_states = " or ".join(f"gc_status eq {DISPUTE[k]}" for k in ("Open", "UnderReview", "AwaitingEvidence", "Escalated"))
    labels = json.dumps({str(B + i): l for i, l in enumerate(RESOLUTION)}).replace("'", "''")
    resolution = f"coalesce(json('{labels}')?[string({T('gc_resolution')})], 'None')"
    refund = f"or(equals({T('gc_resolution')}, {B + 2}), equals({T('gc_resolution')}, {B + 3}))"
    outcome = f"if(equals({T('gc_status')}, {DISPUTE['Withdrawn']}), 'withdrawn', {resolution})"
    steps = [
        ("Deal", get_row("gc_deals", S(did), select="gc_name,gc_statusoverlay,_gc_buyer_value,_gc_seller_value")),
        ("Stamp_resolved", update("gc_disputes", S(xid), gc_resolvedon="@utcNow()")),
        ("Close_task", condition(f"@not(empty({T('_gc_reviewtask_value')}))", [
            ("Task_state", get_row("gc_reviewtasks", S(T("_gc_reviewtask_value")), select="gc_status")),
            ("Still_open", condition(f"@equals(body('Task_state')?['gc_status'], {REVIEW_STATUS['Open']})", [
                ("Cancel_task", update("gc_reviewtasks", S(T("_gc_reviewtask_value")), gc_status=B + 3))])),  # Cancelled: nothing to apply
        ])),
        ("Other_open", list_rows("gc_disputes", filter=f"_gc_deal_value eq {S(did)} and gc_disputeid ne {S(xid)} and ({open_states})", select="gc_disputeid", top=1)),
        ("Lift_overlay", condition(f"@and({none_found('Other_open')[1:]}, equals({dl('gc_statusoverlay')}, {OVERLAY_DISPUTED}))", [
            ("Clear_overlay", update("gc_deals", S(did), gc_statusoverlay=OVERLAY["None_"]))])),
        ("Refund_outcome", condition(f"@and(equals({T('gc_status')}, {DISPUTE['Resolved']}), {refund})", [
            ("Refund_task", review_task(f"concat('Refund after dispute: ', {dl('gc_name')})", "Refund", "Finance",
                                        props(disputeId=xid, resolution=resolution, amount=T("gc_amountdisputed"), outcome=T("gc_financialoutcome")), deal=S(did)))])),
        ("Cancel_outcome", condition(f"@and(equals({T('gc_status')}, {DISPUTE['Resolved']}), equals({T('gc_resolution')}, {B + 5}))", [
            ("To_cancelled", transition(S(did), STAGE["Cancelled"], "Dispute resolved: deal cancelled")),
            ("Cancel_refused", condition(allowed("To_cancelled"), [
                ("Cancel_stuck_task", stuck_task(f"concat('Dispute resolved but deal not cancelled: ', {dl('gc_name')})", did, "To_cancelled"))])),
        ])),
        *notify_both("Notify", "Dispute closed",
                     f"The dispute on '{S(dl('gc_name'))}' is closed. Outcome: {S(outcome)}. "
                     "Any payment step that follows will be confirmed to you in DealOS.",
                     "dispute.closed", buyer=dl("_gc_buyer_value"), seller=dl("_gc_seller_value")),
        ("Audit", audit("flow:dispute", "dispute.closed", "gc_dispute", S(xid), props(status=T("gc_status"), resolution=resolution))),
    ]
    return flow(name, "Dispute Resolved or Withdrawn → overlay lifted when no other dispute is open → refund outcome: Finance Refund task; cancel outcome: "
                      "deal Cancelled → both told.",
                row_trigger("When_a_dispute_closes", "gc_dispute", 3, attributes="gc_status", concurrency=1, conditions=[
                    f"@or(equals({T('gc_status')}, {DISPUTE['Resolved']}), equals({T('gc_status')}, {DISPUTE['Withdrawn']}))"]),
                steps)


# ---------------------------------------------------------------- settlement, ratings, invoices (plan P5, P7)

def deal_settled():
    name = "DealOS | Deal settled"
    did = T("gc_dealid")
    upgrade = lambda side: [
        (f"{side}_account", get_row("accounts", S(T(f"_gc_{side.lower()}_value")), select="name,gc_trusttier")),
        (f"{side}_open_upgrade", list_rows("gc_reviewtasks", select="gc_reviewtaskid", top=1,
                                           filter=f"gc_status eq {REVIEW_STATUS['Open']} and gc_purpose eq {PURPOSE['TierUpgrade']} and _gc_account_value eq {or_empty_guid(T(f'_gc_{side.lower()}_value'))}")),
        (f"{side}_first_trade", condition(f"@and(equals(body('{side}_account')?['gc_trusttier'], {TIER['KYBVerified']}), {none_found(f'{side}_open_upgrade')[1:]})", [
            (f"{side}_tier_task", review_task(f"concat('Tier upgrade to Trade Verified: ', body('{side}_account')?['name'])", "TierUpgrade", "VerificationOfficer",
                                              props(tier="'Trade Verified'", tierValue=str(TIER_TRADE_VERIFIED), reason="'First trade settled on DealOS'", deal=did),
                                              account=S(T(f"_gc_{side.lower()}_value"))))])),
    ]
    steps = [
        *upgrade("Buyer"),
        *upgrade("Seller"),
        *notify_both("Notify", "Deal settled: please rate your counterparty",
                     f"'{S(T('gc_name'))}' is settled. Please take a minute to rate the other party in DealOS (quality, timeliness, communication, documents). "
                     "Ratings build each company's trust tier.", "deal.settled"),
        ("Audit", audit("flow:deal-settled", "deal.settled", "gc_deal", S(did), props(buyer=T("_gc_buyer_value"), seller=T("_gc_seller_value")))),
    ]
    return flow(name, "Deal Settled → a KYB-verified party gets a Tier Upgrade (Trade Verified) approval for its first settled trade → both asked to rate.",
                deal_trigger("When_a_deal_is_settled", "Settled"), steps)


def rating_received():
    name = "DealOS | Rating received"
    rid = T("gc_ratingid")
    steps = [
        ("Stamp_rating", update("gc_ratings", S(rid), gc_ratedon=f"@coalesce({T('gc_ratedon')}, utcNow())")),
        ("Low_rating", condition(f"@lessOrEquals(coalesce({T('gc_score')}, 5), 2)", [
            ("Low_rating_task", review_task(f"concat('Low rating (', string({T('gc_score')}), '/5): ', coalesce({T('gc_name')}, ''))", "Other", "Support",
                                            props(ratingId=rid, score=T("gc_score"), comment=T("gc_comment"), dimensions=T("gc_dimensions")),
                                            deal=S(T("_gc_deal_value")), account=S(T("_gc_ratee_value")), kind="Review"))])),
        ("Audit", audit("flow:rating", "rating.received", "gc_rating", S(rid), props(score=T("gc_score"), ratee=T("_gc_ratee_value")))),
    ]
    return flow(name, "Rating created → stamped → a score of 2 or less opens a Support review on the rated party.",
                row_trigger("When_a_rating_is_given", "gc_rating", 1), steps)


def commission_invoice():
    name = "DealOS | Commission invoice"
    rid, pid = T("gc_paymentreleaseid"), T("_gc_payment_value")
    deal = "body('Payment')?['_gc_deal_value']"
    steps = [
        ("Has_commission", condition(f"@greater(coalesce({T('gc_commissionamount')}, 0), 0)", [], [("No_commission", terminate("Succeeded"))])),
        ("Existing_invoice", list_rows("gc_invoices", filter=f"_gc_release_value eq {S(rid)}", select="gc_invoiceid", top=1)),
        ("Already_invoiced", condition("@greater(length(outputs('Existing_invoice')?['body/value']), 0)", [("Invoice_exists", terminate("Succeeded"))])),
        ("Payment", get_row("gc_payments", S(pid), select="gc_currency,_gc_deal_value")),
        ("Deal", get_row("gc_deals", S(deal), select="gc_name,_gc_seller_value")),
        ("Create_invoice", create("gc_invoices", gc_name=S(f"take(concat('Commission – ', body('Deal')?['gc_name']), 200)"),
                                  gc_invoicenumber=S(f"concat('DOS-', formatDateTime(utcNow(), 'yyyyMMdd'), '-', toUpper(take({rid}, 8)))"),
                                  gc_kind=INVOICE_KIND["Commission"], gc_status=INVOICE["Draft"], gc_currency=S("body('Payment')?['gc_currency']"),
                                  gc_amount=f"@{T('gc_commissionamount')}", gc_taxamount=0, gc_total=f"@{T('gc_commissionamount')}",
                                  gc_taxdetails="{\"note\": \"Deducted at source from the escrow release. Finance sets GST/VAT and issues the invoice.\"}",
                                  **{"gc_Deal@odata.bind": S(f"concat('gc_deals(', {deal}, ')')"),
                                     "gc_Release@odata.bind": S(f"concat('gc_paymentreleases(', {rid}, ')')"),
                                     "gc_BillTo@odata.bind": S("concat('accounts(', body('Deal')?['_gc_seller_value'], ')')")})),
        ("Audit", audit("flow:commission-invoice", "invoice.drafted", "gc_paymentrelease", S(rid),
                        props(invoice="outputs('Create_invoice')?['body/gc_invoiceid']", amount=T("gc_commissionamount")))),
    ]
    return flow(name, "Release Settled with commission → one Draft commission invoice (bill to the seller, amount = commission deducted at source) for Finance to complete and issue.",
                row_trigger("When_commission_is_taken", "gc_paymentrelease", 3, filter=f"gc_status eq {RELEASE['Settled']}", attributes="gc_status", concurrency=1),
                steps)


# ---------------------------------------------------------------- operations: deadlines and failures (plan #3 additions, #18)

def daily_deadlines():
    name = "DealOS | Daily deadlines"
    setting = lambda key: list_rows("gc_platformsettings", filter=f"gc_key eq '{key}'", select="gc_value", top=1)
    invite_days = f"mul(-1, int(coalesce({first_value('Invite_days_setting', 'gc_value')}, '5')))"
    pay = lambda c: f"items('Each_missed_payment')?['{c}']"
    rem = lambda c: f"items('Each_reminder')?['{c}']"
    ins = lambda c: f"items('Each_overdue_inspection')?['{c}']"
    rfq_name = "items('Each_expired_rfq')?['gc_name']"
    steps = [
        # RFQs past their validity
        ("Expired_rfqs", list_rows("gc_buyerrequirements", select="gc_buyerrequirementid,gc_name,_gc_buyer_value", top=100,
                                   filter=f"gc_status eq {REQUIREMENT['Open']} and gc_validuntil lt {S('utcNow()')}")),
        ("Each_expired_rfq", foreach("@outputs('Expired_rfqs')?['body/value']", [
            ("Expire_rfq", update("gc_buyerrequirements", S("items('Each_expired_rfq')?['gc_buyerrequirementid']"), gc_status=REQUIREMENT_EXPIRED)),
            ("Tell_buyer", notify(S("items('Each_expired_rfq')?['_gc_buyer_value']"), "Your RFQ has expired",
                                  f"Your RFQ '{S(rfq_name)}' passed its validity date and is now closed. "
                                  "Open DealOS to post it again if you still need the material.", "rfq.expired")),
        ])),
        # Invites nobody answered
        ("Invite_days_setting", setting("rfq.invite_days")),
        ("Stale_invites", list_rows("gc_rfqinvites", select="gc_rfqinviteid", top=200,
                                    filter=f"gc_status eq {INVITE['Invited']} and gc_invitedon lt {S(f'addDays(utcNow(), {invite_days})')}")),
        ("Each_stale_invite", foreach("@outputs('Stale_invites')?['body/value']", [
            ("Expire_invite", update("gc_rfqinvites", S("items('Each_stale_invite')?['gc_rfqinviteid']"), gc_status=INVITE["Expired"]))], concurrency=5)),
        # Escrow not funded by the deadline: a person decides (extend or cancel); the platform never cancels on its own
        ("Missed_funding", list_rows("gc_payments", select="gc_paymentid,gc_name,_gc_deal_value,gc_fundingdeadline", top=50,
                                     filter=f"gc_state eq {PAYMENT['AwaitingFunding']} and gc_fundingdeadline lt {S('utcNow()')}")),
        ("Each_missed_payment", foreach("@outputs('Missed_funding')?['body/value']", [
            ("Missed_task_exists", open_task_exists(f"concat('Funding deadline missed: ', {pay('gc_name')})", deal_expr=pay("_gc_deal_value"))),
            ("Open_missed_task", condition(none_found("Missed_task_exists"), [
                ("Missed_task", review_task(f"concat('Funding deadline missed: ', {pay('gc_name')})", "Other", "DealManager",
                                            props(paymentId=pay("gc_paymentid"), deadline=pay("gc_fundingdeadline"),
                                                  options="'Extend the deadline on the payment, or cancel the deal (stage Cancelled).'"),
                                            deal=S(pay("_gc_deal_value")), kind="Review"))])),
        ])),
        # Reminder two days before the funding deadline
        ("Due_soon", list_rows("gc_payments", select="gc_paymentid,_gc_deal_value,gc_amountdue,gc_currency,gc_fundingdeadline", top=50,
                               filter=f"gc_state eq {PAYMENT['AwaitingFunding']} and gc_fundingdeadline ge {S('utcNow()')} and gc_fundingdeadline lt {S('addDays(utcNow(), 2)')}")),
        ("Each_reminder", foreach("@outputs('Due_soon')?['body/value']", [
            ("Reminder_deal", get_row("gc_deals", S(rem("_gc_deal_value")), select="gc_name,_gc_buyer_value")),
            ("Remind_buyer", notify(S("body('Reminder_deal')?['_gc_buyer_value']"), "Reminder: escrow funding due",
                                    f"Please fund escrow for '{S(body('Reminder_deal', 'gc_name'))}': {S(rem('gc_currency'))} "
                                    f"{S(FMT_AMOUNT.format(rem('gc_amountdue')))} by {S(FMT_DATE.format(rem('gc_fundingdeadline')))}.", "escrow.reminder")),
        ])),
        # Inspections booked but no result two days after the date
        ("Overdue_inspections", list_rows("gc_inspections", select="gc_inspectionid,gc_name,_gc_deal_value,gc_scheduledon", top=50,
                                          filter=f"(gc_status eq {INSPECTION['Booked']} or gc_status eq {INSPECTION['SamplingDone']}) and gc_scheduledon lt {S('addDays(utcNow(), -2)')}")),
        ("Each_overdue_inspection", foreach("@outputs('Overdue_inspections')?['body/value']", [
            ("Overdue_task_exists", open_task_exists(f"concat('Inspection result overdue: ', {ins('gc_name')})", deal_expr=ins("_gc_deal_value"))),
            ("Open_overdue_task", condition(none_found("Overdue_task_exists"), [
                ("Overdue_task", review_task(f"concat('Inspection result overdue: ', {ins('gc_name')})", "Other", "LogisticsCoordinator",
                                             props(inspectionId=ins("gc_inspectionid"), scheduled=ins("gc_scheduledon")),
                                             deal=S(ins("_gc_deal_value")), kind="Review"))])),
        ])),
        ("Audit", audit("flow:daily-deadlines", "sweep.deadlines", "none", "", props(
            rfqs_expired="length(outputs('Expired_rfqs')?['body/value'])", invites_expired="length(outputs('Stale_invites')?['body/value'])",
            funding_missed="length(outputs('Missed_funding')?['body/value'])", reminders="length(outputs('Due_soon')?['body/value'])",
            inspections_overdue="length(outputs('Overdue_inspections')?['body/value'])"))),
    ]
    return flow(name, "Every day 02:30 UTC: expire RFQs past validity (buyer told) and invites unanswered for rfq.invite_days; Deal Manager task for escrow "
                      "not funded by the deadline (no automatic cancel); funding reminder 2 days before; Logistics task for inspection results overdue.",
                daily_trigger("Every_day_0230_UTC", 2, 30), steps)


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
            ("Store_attachment", unbound("gc_AttachEmailFile", retry_none=True, MessageId=S(ingest("MessageId")), FileName=S(att("fileName")),
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
        # New mail: the configured search, minus what is already handled
        ("New_email", gmail("ListMessages", q="@{outputs('Search')}",
                            maxResults=f"@int(coalesce({first_value('Max_setting', 'gc_value')}, '5'))")),
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
                                       "'Drafts are waiting in Gmail > Drafts (unless auto-send sent them).', 'No email was drafted.'))")),
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
    pending = [
        ("Profile", gmail("GetProfile")),
        ("Build", unbound("gc_BuildEmailRaw", retry_none=True, MessageId=S(mid), MailboxAddress="@{body('Profile')?['emailAddress']}")),
        ("Has_thread", condition(f"@not(empty({build('ThreadId')}))",
                                 [("Draft_in_thread", gmail("CreateDraft", body__message__raw=S(build("Raw")), body__message__threadId=S(build("ThreadId"))))],
                                 [("Draft_new_thread", gmail("CreateDraft", body__message__raw=S(build("Raw"))))])),
        ("Save_draft_id", update("gc_messages", S(mid), gc_gmaildraftid=S(created("['id']")))),
        ("New_thread", condition(f"@empty({build('ThreadId')})", [
            ("Save_thread_id", update("gc_conversations", S(conv), gc_gmailthreadid=S(created("['message']?['threadId']"))))])),
        ("Auto_send", condition(f"@equals({T('gc_autosend')}, true)", [("Send_now", gmail("SendDraft", body__id=S(created("['id']"))))])),
        ("Audit_draft", audit("flow:desk-drafts", "email.drafted", "gc_message", S(mid), props(to=build("To"), draft=created("['id']"), auto_send=T("gc_autosend")))),
    ]
    discarded = [
        ("Delete_gmail_draft", gmail("DeleteDraft", id=S(T("gc_gmaildraftid")))),
        ("Draft_gone", compose("deleted or already sent"), {"Delete_gmail_draft": ["Succeeded", "Failed"]}),
    ]
    steps = [
        ("Pending_or_discarded", condition(f"@equals({T('gc_draftstatus')}, {DRAFT['Pending']})", pending, discarded)),
    ]
    return flow(name, "A desk draft (gc_message, Draft, Pending) → gc_BuildEmailRaw → Gmail draft in the thread (new thread for a first enquiry; the "
                      "thread id is saved). A draft replaced by a newer one (Discarded) is deleted from Gmail.",
                row_trigger("When_a_desk_draft_changes", "gc_message", 4, attributes="gc_draftstatus", concurrency=1, conditions=[
                    f"@or(and(equals({T('gc_draftstatus')}, {DRAFT['Pending']}), empty({T('gc_gmaildraftid')})), "
                    f"and(equals({T('gc_draftstatus')}, {DRAFT['Discarded']}), not(empty({T('gc_gmaildraftid')}))))"]),
                steps, connections=(DV, GMAIL))


def desk_contract():
    name = "DealOS | Desk contract"
    cid = T("gc_contractid")
    steps = [
        ("Deal", get_row("gc_deals", S(T("_gc_deal_value")), select="gc_emaildesk,gc_name")),
        ("Desk_only", condition("@not(equals(body('Deal')?['gc_emaildesk'], true))", [("Marketplace_contract", terminate("Succeeded"))])),
        ("Contract_out", unbound("gc_DeskContract", retry_none=True, ContractId=S(cid))),
        ("Brief_owner", unbound("gc_DeskBrief", retry_none=True, Subject=S(f"concat('Contracts ready to send: ', coalesce(body('Deal')?['gc_name'], ''))"),
                                Text="Both contracts are generated (sales contract to the buyer at our price, purchase contract from the seller at their price) and "
                                     "attached to drafts in each thread. Check them in Gmail > Drafts and send. When a signed copy comes back, the desk opens a task to confirm it.")),
        ("Audit", audit("flow:desk-contract", "contract.sent_by_email", "gc_contract", S(cid), props(result="outputs('Contract_out')?['body/Result']"))),
    ]
    return flow(name, "Contract approved (Sent For Signature) on an email desk deal → gc_DeskContract: back-to-back contract PDFs, each attached to a draft "
                      "in its thread (buyer: sales contract at our price; seller: purchase contract at their price).",
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

ALL = [notify_party, listing_verification, document_intake, party_onboarding, rfq_matching, listing_published_matching, match_notifications,
       match_to_deal, offer_pricing, offer_accepted, terms_agreed, compliance_check, contracting, contract_signed, escrow_funded,
       milestone_progress, release_settled, deal_cancelled, review_decisions, approvals, daily_sweep, daily_digest,
       rfq_invite_sent, rfq_invite_answered, inspection_booking, inspection_result, dispute_opened, dispute_closed, deal_settled,
       rating_received, commission_invoice, daily_deadlines, flow_failure_triage, mailbox_sync, trade_desk, desk_drafts, desk_contract, seller_discovery]

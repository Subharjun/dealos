"""DealOS cloud flows that compose the gc_Agent_* custom APIs and the deterministic operations.

Each function returns a dict: name, description, id, definition, connections.
Existing flows keep their workflow id; new flows get a stable id from their name.
"""
import copy
import glob
import json
import os

from .lib import (AUTH, B, CONTRACT, DV, EMPTY_GUID, KYB, LISTING, MATCH, MESSAGE, MESSAGE_KIND, MILESTONE, MILESTONE_TYPE, NOTIFY_FLOW,
                  OFFER, OUTLOOK, OVERLAY, PARSE, PARTY, PAYMENT, PURPOSE, RELEASE, REQUIREMENT, REVIEW_STATUS, ROOT, SCOPE, SHIPMENT,
                  STAGE, TIER, agent, agent_ok, agent_result, audit, chain, compose, condition, create, daily_trigger, definition, flow_failure,
                  flow_id, foreach, get_row, list_rows, notify, review_task, row_trigger, run_agent, switch, terminate, transition, try_catch,
                  unbound, update)

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
                row_trigger("When_an_RFQ_opens", "gc_buyerrequirement", 4, filter=f"gc_status eq {REQUIREMENT['Open']}", attributes="gc_status", concurrency=1),
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
        ("Deal", get_row("gc_deals", S(did), select="gc_name,gc_stage,_gc_buyer_value,_gc_seller_value")),
        ("First_offer", condition(f"@equals({dl('gc_stage')}, {STAGE['Inquiry']})", [("To_negotiation", transition(S(did), STAGE["Negotiation"], "First offer received"))])),
        *run_agent("Pricing", S(oid), "offer-pricing", name, on_fail="record"),
        ("Notify_counterparty", notify(S(counterparty), "New offer on your deal",
                                       f"A new offer was made on '{S(dl('gc_name'))}': {S(T('gc_price'))} {S(T('gc_currency'))} per unit for "
                                       f"{S(T('gc_quantity'))} units, {S(incoterm_label(T('gc_incoterm')))}.\n"
                                       "Open DealOS to see your landed cost or net payout, then accept, counter or decline.", "offer.created")),
        ("Audit", audit("flow:offer-pricing", "offer.priced", "gc_offer", S(oid),
                        props(summary="outputs('Price_offer')?['body/Summary']", explanation="outputs('Run_Pricing')?['body/Summary']"))),
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


ALL = [notify_party, listing_verification, document_intake, party_onboarding, rfq_matching, listing_published_matching, match_notifications,
       match_to_deal, offer_pricing, offer_accepted, terms_agreed, compliance_check, contracting, contract_signed, escrow_funded,
       milestone_progress, release_settled, deal_cancelled, review_decisions, approvals, daily_sweep, daily_digest]

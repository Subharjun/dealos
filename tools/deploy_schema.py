"""Deploy the DealOS data-model additions (idempotent): plan section 8 tables, chat columns, agent choices.

Creates, inside the DealOS solution, only what is missing:
  - tables: gc_rfqinvite, gc_warehouse, gc_inspection, gc_shipmentdocument, gc_invoice, gc_rating, gc_dispute,
    gc_catalogentry (public masked catalog), gc_notificationpreference (user-owned, audited; columns and lookups below)
  - gc_conversation.gc_assistant (which chat agent answers) and gc_conversation.gc_contact (who chats)
  - gc_agent options Buyer Concierge (303300012) and Seller Assistant (303300013)
  - Email Desk (docs/EMAIL_DESK.md): Gmail thread id and triage on gc_conversation; direction, addresses, headers and
    triage on gc_message; gc_document.gc_message (attachment of an email); gc_agent options Mail Triage (303300014)
    and Trade Desk (303300015)

Usage:
  python3 tools/deploy_schema.py            # create what is missing, then publish
  python3 tools/deploy_schema.py --check    # list what is missing, change nothing
"""
import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

SOLUTION = "DealOS"
SOL = {"MSCRM.SolutionUniqueName": SOLUTION}
BASE = 303300000


def label(text):
    return {"@odata.type": "Microsoft.Dynamics.CRM.Label",
            "LocalizedLabels": [{"@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel", "Label": text, "LanguageCode": 1033}]}


# Column specs: (SchemaName, kind, display, description, extra)
#   kind: text(n) | memo | int | decimal | bool | datetime | choice | globalchoice | lookup
#   choice: extra = [labels]; globalchoice: extra = option set name; lookup: extra = target table
def text(n, display, desc, size=200): return (n, "text", display, desc, size)
def memo(n, display, desc): return (n, "memo", display, desc, None)
def num(n, display, desc): return (n, "decimal", display, desc, None)
def integer(n, display, desc): return (n, "int", display, desc, None)
def flag(n, display, desc): return (n, "bool", display, desc, None)
def when(n, display, desc): return (n, "datetime", display, desc, None)
def choice(n, display, desc, labels): return (n, "choice", display, desc, labels)
def gchoice(n, display, desc, optionset): return (n, "globalchoice", display, desc, optionset)
def lookup(n, display, desc, target): return (n, "lookup", display, desc, target)


# Email Desk choices; the order is the choice value (BASE + index) and must match MailCategories in the plug-in.
TRIAGE = ["Genuine", "Review", "Ignored"]
MAIL_CATEGORIES = ["Buyer Requirement", "Offer To Sell", "Thread Reply", "Documents", "Vendor Pitch", "Scam", "Not Trade"]
LEAD_ROLES = ["Seller", "Buyer", "Both"]
LEAD_SOURCES = ["Trade Data", "IndiaMART", "Warehouse", "Email", "Manual", "Web Search"]
LEAD_STATUSES = ["New", "Contacted", "Responded", "Converted", "Do Not Contact"]
DESK_STAGES = ["Qualifying", "Sourcing", "Quoted", "Negotiating", "Agreed", "Contract Sent", "Signed", "Closed"]

TABLES = [
    ("gc_RfqInvite", "RFQ invite", "RFQ invites", "One buyer RFQ sent to one seller (listing). Accepting an invite spawns a gc_deal.", [
        lookup("gc_Requirement", "RFQ", "The buyer requirement (RFQ) this invite belongs to.", "gc_buyerrequirement"),
        lookup("gc_Listing", "Listing", "The seller listing invited.", "gc_listing"),
        lookup("gc_Seller", "Seller", "The invited seller.", "account"),
        lookup("gc_Deal", "Deal", "The deal created when the seller accepted.", "gc_deal"),
        choice("gc_Status", "Status", "Invite status.", ["Invited", "Viewed", "Accepted", "Declined", "Expired", "Withdrawn"]),
        when("gc_InvitedOn", "Invited on", "When the invite was sent."),
        when("gc_RespondedOn", "Responded on", "When the seller accepted or declined."),
        memo("gc_BuyerNote", "Buyer note", "Optional note from the buyer to the seller (masked identity)."),
        memo("gc_DeclineReason", "Decline reason", "Why the seller declined."),
    ]),
    ("gc_Warehouse", "Warehouse", "Warehouses", "A place where goods are stored and physically inspected before shipment.", [
        lookup("gc_Operator", "Operator", "Company operating the warehouse.", "account"),
        lookup("gc_Country", "Country", "Country of the warehouse.", "gc_country"),
        text("gc_City", "City", "City or port."),
        memo("gc_Address", "Address", "Full address (restricted: not shown to counterparties before contract)."),
        choice("gc_Kind", "Kind", "Type of facility.", ["Seller Site", "Partner Warehouse", "Bonded Warehouse", "Port Terminal"]),
        flag("gc_IsBonded", "Bonded", "Customs-bonded facility."),
        num("gc_CapacityMt", "Capacity (MT)", "Storage capacity in metric tonnes."),
        choice("gc_Status", "Status", "Whether the platform uses this warehouse.", ["Active", "Suspended"]),
    ]),
    ("gc_Inspection", "Inspection", "Inspections", "Independent physical inspection of a lot before shipment: booking, sampling, result and report.", [
        lookup("gc_Deal", "Deal", "Deal being inspected.", "gc_deal"),
        lookup("gc_Lot", "Lot", "Lot being inspected.", "gc_lot"),
        lookup("gc_Warehouse", "Warehouse", "Where the inspection happens.", "gc_warehouse"),
        lookup("gc_Agency", "Agency", "Independent inspection agency (chosen by the platform, never the seller).", "account"),
        choice("gc_Status", "Status", "Inspection progress.", ["Requested", "Booked", "Sampling Done", "Report Received", "Passed", "Failed", "Cancelled"]),
        choice("gc_Result", "Result", "Outcome against the contract specification.", ["Pending", "Within Spec", "Off Spec", "Inconclusive"]),
        when("gc_ScheduledOn", "Scheduled on", "Booked inspection date."),
        when("gc_SampledOn", "Sampled on", "When samples were drawn and the lot sealed."),
        lookup("gc_Report", "Report", "Inspection report uploaded by the agency.", "gc_document"),
        lookup("gc_Verification", "Verification", "Verification record created from the result.", "gc_verification"),
        num("gc_WeightMt", "Weight (MT)", "Weight measured by the agency."),
        text("gc_SealNumbers", "Seal numbers", "Seals applied to the lot."),
        memo("gc_Findings", "Findings", "Agency findings, deviations from spec."),
    ]),
    ("gc_ShipmentDocument", "Shipment document", "Shipment documents", "Required versus received documents for a shipment's corridor.", [
        lookup("gc_Shipment", "Shipment", "Shipment the document belongs to.", "gc_shipment"),
        lookup("gc_Deal", "Deal", "Deal the document belongs to.", "gc_deal"),
        gchoice("gc_DocType", "Document type", "Kind of document required.", "gc_doctype"),
        flag("gc_Required", "Required", "Required for this corridor (false = optional)."),
        choice("gc_Status", "Status", "Checklist status.", ["Required", "Received", "Accepted", "Rejected", "Waived"]),
        lookup("gc_Document", "Document", "The uploaded document.", "gc_document"),
        text("gc_RuleSource", "Rule source", "Country rule or checklist entry that requires it."),
        when("gc_DueOn", "Due on", "When the document is needed."),
    ]),
    ("gc_Invoice", "Invoice", "Invoices", "Commission invoice (or service fee / credit note) to the paying party.", [
        text("gc_InvoiceNumber", "Invoice number", "Sequential tax invoice number.", 50),
        lookup("gc_Deal", "Deal", "Deal the invoice relates to.", "gc_deal"),
        lookup("gc_Release", "Release", "Escrow release the commission was deducted from.", "gc_paymentrelease"),
        lookup("gc_BillTo", "Bill to", "Party invoiced.", "account"),
        choice("gc_Kind", "Kind", "Invoice kind.", ["Commission", "Service Fee", "Credit Note"]),
        text("gc_Currency", "Currency", "ISO currency code.", 3),
        num("gc_Amount", "Amount", "Amount before tax."),
        num("gc_TaxAmount", "Tax amount", "Tax (e.g. GST) on the amount."),
        num("gc_Total", "Total", "Amount plus tax."),
        choice("gc_Status", "Status", "Invoice status.", ["Draft", "Issued", "Paid", "Void"]),
        when("gc_IssuedOn", "Issued on", "Issue date."),
        when("gc_DueOn", "Due on", "Payment due date (commission is normally deducted at source)."),
        memo("gc_TaxDetails", "Tax details", "JSON: tax regime, rates, GSTINs, place of supply."),
    ]),
    ("gc_Rating", "Rating", "Ratings", "Post-trade rating of the counterparty; feeds the trust tier.", [
        lookup("gc_Deal", "Deal", "Completed deal being rated.", "gc_deal"),
        lookup("gc_Rater", "Rater", "Party giving the rating.", "account"),
        lookup("gc_Ratee", "Ratee", "Party being rated.", "account"),
        integer("gc_Score", "Score", "Overall score 1-5."),
        memo("gc_Dimensions", "Dimensions", "JSON scores 1-5: quality, timeliness, communication, documentation."),
        memo("gc_Comment", "Comment", "Free-text comment."),
        when("gc_RatedOn", "Rated on", "When the rating was given."),
    ]),
    ("gc_Dispute", "Dispute", "Disputes", "A dispute on a deal: reason, evidence, resolution and financial outcome.", [
        lookup("gc_Deal", "Deal", "Disputed deal.", "gc_deal"),
        lookup("gc_RaisedBy", "Raised by", "Party that raised the dispute.", "account"),
        choice("gc_Reason", "Reason", "Why the dispute was raised.", ["Quality Off Spec", "Quantity Short", "Late Delivery", "Documents", "Non Payment", "Damage", "Other"]),
        memo("gc_Description", "Description", "What happened, in the party's words."),
        choice("gc_Status", "Status", "Dispute progress.", ["Open", "Under Review", "Awaiting Evidence", "Resolved", "Withdrawn", "Escalated"]),
        choice("gc_Resolution", "Resolution", "How it was resolved.", ["None", "Release To Seller", "Refund To Buyer", "Partial Refund", "Price Adjustment", "Deal Cancelled"]),
        num("gc_AmountDisputed", "Amount disputed", "Amount in deal currency."),
        memo("gc_FinancialOutcome", "Financial outcome", "What moved and why (approved by Finance)."),
        when("gc_OpenedOn", "Opened on", "When the dispute was raised."),
        when("gc_ResolvedOn", "Resolved on", "When it was resolved."),
        lookup("gc_ReviewTask", "Review task", "Review task handling the dispute.", "gc_reviewtask"),
    ]),
    ("gc_CatalogEntry", "Catalog entry", "Catalog entries", "Public, masked copy of a published listing for the marketplace site (no seller, asset, "
     "exact location or restricted facts). Maintained by the CatalogPlugin; never edited by hand.", [
        lookup("gc_Listing", "Listing", "The published listing this entry shows.", "gc_listing"),
        text("gc_Commodity", "Commodity", "Commodity name."),
        text("gc_Family", "Family", "Commodity family."),
        text("gc_Form", "Form", "Commodity form."),
        text("gc_Grade", "Grade", "Grade as listed."),
        num("gc_Quantity", "Quantity", "Listed quantity."),
        text("gc_Unit", "Unit", "Quantity unit.", 30),
        num("gc_AvailableQuantity", "Available quantity", "Quantity in Available lots."),
        num("gc_AskPrice", "Ask price", "Asking price per unit."),
        text("gc_Currency", "Currency", "ISO currency.", 3),
        text("gc_PriceBasis", "Price basis", "Fixed, index linked or formula.", 50),
        text("gc_Incoterm", "Incoterm", "Incoterm code.", 10),
        text("gc_NamedPlace", "Named place", "Incoterm named place or port."),
        text("gc_OriginCountry", "Origin country", "Country of origin."),
        text("gc_Badge", "Badge", "Evidence badge: Verified, Documented or None.", 20),
        when("gc_PublishedOn", "Published on", "When the listing was published."),
        memo("gc_Facts", "Facts", "JSON array of market-visible facts: name, value, status (Verified, Documented, Claimed)."),
        when("gc_RefreshedOn", "Refreshed on", "When this entry was last rebuilt from the listing."),
    ]),
    ("gc_Lead", "Lead", "Leads", "A company that may sell or buy, from trade data, IndiaMART, warehouses or email; not yet a verified party.", [
        choice("gc_Role", "Role", "Whether the lead sells, buys or both.", LEAD_ROLES),
        memo("gc_Commodities", "Commodities", "Products seen for this company (trade-data descriptions, offers), one per line."),
        text("gc_HsCodes", "HS codes", "HS codes seen, comma separated.", 400),
        text("gc_Country", "Country", "Country of the company.", 100),
        text("gc_Email", "Email", "Contact email (needed for outreach).", 320),
        text("gc_Phone", "Phone", "Contact phone or WhatsApp.", 100),
        text("gc_Website", "Website", "Company website.", 400),
        text("gc_ContactName", "Contact name", "Person to address.", 200),
        choice("gc_Source", "Source", "Where the lead came from.", LEAD_SOURCES),
        text("gc_SourceRef", "Source reference", "File, provider or message the lead was imported from.", 400),
        when("gc_LastSeen", "Last seen", "Latest shipment or offer date seen."),
        text("gc_Volume", "Volume", "Volume seen, e.g. '6 shipments, 140 MT in 6 months'.", 400),
        integer("gc_Shipments", "Shipments", "Number of shipments seen in the trade data."),
        choice("gc_Status", "Status", "Outreach status.", LEAD_STATUSES),
        lookup("gc_Account", "Account", "The account created when we first contacted the lead.", "account"),
        memo("gc_Notes", "Notes", "Free notes."),
    ]),
    ("gc_SellerLot", "Seller lot", "Seller lots", "A seller's stock (price, quantity) offered to several buyers on the Email Desk: timed (highest bids win at the deadline) or open-ended (bids go to the seller, who decides).", [
        lookup("gc_Seller", "Seller", "The seller offering the lot.", "account"),
        lookup("gc_SellerThread", "Seller thread", "The email thread with the seller.", "gc_conversation"),
        text("gc_CommodityText", "Commodity (as written)", "The material as the seller wrote it.", 400),
        memo("gc_Specification", "Specification", "Grade, purity and sizing as written."),
        num("gc_Quantity", "Quantity", "Quantity available."),
        text("gc_Unit", "Unit", "Quantity unit (MT, Kg, ...).", 30),
        num("gc_Price", "Seller price", "Seller's price per unit (never shown to buyers)."),
        text("gc_Currency", "Currency", "ISO currency.", 3),
        text("gc_Incoterm", "Incoterm", "Delivery basis code.", 10),
        text("gc_NamedPlace", "Named place", "Port or place of the delivery basis."),
        text("gc_Origin", "Origin", "Country of origin.", 100),
        memo("gc_Terms", "Terms (as written)", "Payment, lead time and packing as written."),
        when("gc_ValidUntil", "Valid until", "Validity of the seller's offer."),
        when("gc_BidDeadline", "Bid deadline", "When offers from buyers close. Empty on an open lot = open-ended (the seller decides)."),
        text("gc_Window", "Window", "How long buyers may bid, e.g. '48 hours (seller)', 'Open-ended (default)'.", 100),
        when("gc_DiscoveredOn", "Buyers searched on", "When the web search for buyers ran."),
        choice("gc_Status", "Status", "Lot progress.", ["Open", "Closed", "Allocated", "Withdrawn"]),
        num("gc_Allocated", "Allocated", "Quantity allocated to winning buyers."),
        when("gc_ClosedOn", "Closed on", "When bidding was closed."),
        when("gc_RemindedOn", "Reminded on", "When buyers were reminded that offers close soon."),
        memo("gc_Outcome", "Outcome", "JSON: floor price and the ranking of bids at close."),
    ]),
    ("gc_NotificationPreference", "Notification preference", "Notification preferences", "How and in which language a contact wants to be told about updates.", [
        lookup("gc_Contact", "Contact", "Person the preference belongs to.", "contact"),
        lookup("gc_Account", "Account", "Company of the contact.", "account"),
        choice("gc_Channel", "Channel", "Preferred channel.", ["Email", "WhatsApp", "Portal Only"]),
        text("gc_Language", "Language", "ISO 639-1 language code.", 10),
        choice("gc_Frequency", "Frequency", "How often to notify.", ["Immediate", "Daily Digest", "Weekly Digest"]),
        flag("gc_OptOut", "Opted out", "True = no notifications except legally required ones."),
        text("gc_WhatsAppNumber", "WhatsApp number", "E.164 number (restricted).", 30),
    ]),
]

EXTRA_COLUMNS = [  # (table logical name, column spec)
    ("gc_conversation", choice("gc_Assistant", "Assistant", "Which DealOS chat agent answers this conversation.", ["Buyer Concierge", "Seller Assistant"])),
    ("gc_conversation", lookup("gc_Contact", "Contact", "Portal user who started the conversation.", "contact")),
    # Email Desk
    ("gc_conversation", text("gc_GmailThreadId", "Gmail thread id", "Gmail thread this conversation mirrors (Email channel).", 100)),
    ("gc_conversation", choice("gc_Triage", "Triage", "Email Desk verdict for the thread.", TRIAGE)),
    ("gc_conversation", integer("gc_TriageScore", "Triage score", "Genuineness score 0-100 of the thread's latest triaged email.")),
    ("gc_conversation", choice("gc_Category", "Category", "What the thread is about (Email Desk triage).", MAIL_CATEGORIES)),
    ("gc_message", choice("gc_Direction", "Direction", "Email direction: received, sent by us, or a draft waiting for a person.", ["Inbound", "Outbound", "Draft"])),
    ("gc_message", text("gc_FromAddress", "From address", "Sender email address.", 320)),
    ("gc_message", text("gc_ToAddresses", "To addresses", "Recipients (To and Cc), separated by semicolons.", 2000)),
    ("gc_message", text("gc_Subject", "Subject", "Email subject.", 400)),
    ("gc_message", memo("gc_EmailMeta", "Email metadata", "JSON: selected headers, Gmail labels, attachments and link domains.")),
    ("gc_message", choice("gc_Triage", "Triage", "Email Desk verdict for this email.", TRIAGE)),
    ("gc_message", integer("gc_TriageScore", "Triage score", "Genuineness score 0-100 after the hard signals.")),
    ("gc_message", choice("gc_Category", "Category", "What the email is (Email Desk triage).", MAIL_CATEGORIES)),
    ("gc_message", memo("gc_TriageReasons", "Triage reasons", "JSON: the model's reasons, red flags and the hard signals that decided the verdict.")),
    ("gc_message", text("gc_GmailDraftId", "Gmail draft id", "Draft created in Gmail for a person to review and send.", 100)),
    ("gc_document", lookup("gc_Message", "Email", "Email this document arrived as an attachment of.", "gc_message")),
    # Email Desk phases 2-4: who a thread is with, and what the desk is doing for a requirement
    ("gc_conversation", choice("gc_Side", "Side", "Whether the thread is with the buyer or with a seller.", ["Buyer", "Seller"])),
    ("gc_conversation", lookup("gc_Requirement", "Requirement", "The buyer requirement this thread is about.", "gc_buyerrequirement")),
    ("gc_conversation", lookup("gc_Invite", "Source request", "For a seller thread: the source request (RFQ invite) sent to that seller.", "gc_rfqinvite")),
    ("gc_message", choice("gc_DraftStatus", "Draft status", "For drafts: waiting for a person, sent, or replaced.", ["Pending", "Sent", "Discarded"])),
    ("gc_message", text("gc_ToAddress", "Draft to", "For drafts: the recipient address.", 320)),
    ("gc_buyerrequirement", flag("gc_Confidential", "Confidential", "The buyer asked to keep the requirement confidential.")),
    ("gc_buyerrequirement", choice("gc_Source", "Source", "Where the requirement came from.", ["Portal", "Email", "IndiaMART", "Manual"])),
    ("gc_buyerrequirement", choice("gc_DeskStage", "Desk stage", "Email Desk progress for this requirement.", DESK_STAGES)),
    ("gc_buyerrequirement", text("gc_CommodityText", "Commodity (as written)", "The material exactly as the buyer wrote it.", 400)),
    ("gc_buyerrequirement", text("gc_DeliveryText", "Delivery (as written)", "Delivery basis, place and timing as the buyer wrote them.", 1000)),
    ("gc_buyerrequirement", text("gc_PaymentText", "Payment terms (as written)", "Payment terms as the buyer wrote them.", 1000)),
    ("gc_buyerrequirement", num("gc_OurPrice", "Our price", "Latest price per unit we quoted to the buyer.")),
    ("gc_deal", flag("gc_EmailDesk", "Email Desk", "The deal is run by the Email Desk (back to back: buyer and seller deal only with us).")),
    ("gc_deal", num("gc_BuyerPrice", "Buyer price", "Price per unit the buyer pays us (back to back); gc_price is the seller's price.")),
    ("gc_offer", memo("gc_Terms", "Terms (as written)", "Origin, lead time, payment, packing and other terms as written in the email.")),
    ("gc_message", flag("gc_AutoSend", "Auto-send", "The desk sends this draft itself (briefings to us; routine mail when email.autosend allows).")),
    ("gc_buyerrequirement", when("gc_DiscoveredOn", "Sellers discovered on", "When web seller discovery last ran for this requirement.")),
    # Seller lots and follow-ups (7 Oct 2026)
    ("gc_deal", lookup("gc_SellerLot", "Seller lot", "The seller lot this desk deal bids on.", "gc_sellerlot")),
    ("gc_deal", when("gc_BidOn", "Bid on", "When the buyer's bid on the lot was recorded (ties go to the earliest).")),
    ("gc_conversation", lookup("gc_SellerLot", "Seller lot", "The seller lot this thread is about.", "gc_sellerlot")),
    ("gc_conversation", integer("gc_Chases", "Chasers", "Follow-up drafts sent because the other side had not answered.")),
    ("gc_conversation", when("gc_ChasedOn", "Chased on", "When the last follow-up was drafted.")),
    # Sellers take turns (7 Oct 2026)
    ("gc_buyerrequirement", lookup("gc_ActiveDeal", "Active seller deal", "The seller deal the buyer is negotiating now (the first seller to quote); later sellers wait in the queue.", "gc_deal")),
]

# Options added to local choices after their column was created: (table, column, value, label)
EXTRA_OPTIONS = [("gc_lead", "gc_source", BASE + 5, "Web Search")]

AGENT_OPTIONS = [(BASE + 12, "Buyer Concierge"), (BASE + 13, "Seller Assistant"), (BASE + 14, "Mail Triage"), (BASE + 15, "Trade Desk")]


def ok(status, body, what):
    if status >= 300:
        sys.exit(f"FAILED {what}: {status} {str(body)[:1500]}")
    return body


def exists_table(logical):
    return dv.get(f"EntityDefinitions(LogicalName='{logical}')?$select=LogicalName")[0] == 200


def exists_column(table, logical):
    return dv.get(f"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='{logical}')?$select=LogicalName")[0] == 200


def attribute_body(spec):
    name, kind, display, desc, extra = spec
    common = {"SchemaName": name, "DisplayName": label(display), "Description": label(desc),
              "RequiredLevel": {"Value": "None", "CanBeChanged": True}}
    if kind == "text":
        return dict(common, **{"@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "MaxLength": extra, "FormatName": {"Value": "Text"}})
    if kind == "memo":
        return dict(common, **{"@odata.type": "Microsoft.Dynamics.CRM.MemoAttributeMetadata", "MaxLength": 100000, "Format": "TextArea"})
    if kind == "decimal":
        return dict(common, **{"@odata.type": "Microsoft.Dynamics.CRM.DecimalAttributeMetadata", "Precision": 4,
                               "MinValue": -100000000000, "MaxValue": 100000000000})
    if kind == "int":
        return dict(common, **{"@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "Format": "None",
                               "MinValue": -2147483648, "MaxValue": 2147483647})
    if kind == "bool":
        return dict(common, **{"@odata.type": "Microsoft.Dynamics.CRM.BooleanAttributeMetadata", "DefaultValue": False,
                               "OptionSet": {"TrueOption": {"Value": 1, "Label": label("Yes")}, "FalseOption": {"Value": 0, "Label": label("No")}}})
    if kind == "datetime":
        return dict(common, **{"@odata.type": "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata", "Format": "DateAndTime",
                               "DateTimeBehavior": {"Value": "UserLocal"}})
    if kind == "choice":
        return dict(common, **{"@odata.type": "Microsoft.Dynamics.CRM.PicklistAttributeMetadata",
                               "OptionSet": {"@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata", "IsGlobal": False, "OptionSetType": "Picklist",
                                             "Options": [{"Value": BASE + i, "Label": label(l)} for i, l in enumerate(extra)]}})
    if kind == "globalchoice":
        s, b = dv.get(f"GlobalOptionSetDefinitions(Name='{extra}')?$select=MetadataId")
        ok(s, b, "read global choice " + extra)
        return dict(common, **{"@odata.type": "Microsoft.Dynamics.CRM.PicklistAttributeMetadata",
                               "GlobalOptionSet@odata.bind": f"/GlobalOptionSetDefinitions({b['MetadataId']})"})
    raise ValueError(kind)


def create_lookup(table, spec):
    name, _, display, desc, target = spec
    target_key = "accountid" if target == "account" else "contactid" if target == "contact" else f"{target}id"
    rel = f"{target}_{table}_{name.lower()}"
    rel = (rel if rel.startswith("gc_") else "gc_" + rel)[:100]  # relationship names need the publisher prefix
    ok(*dv.request("POST", "RelationshipDefinitions", {
        "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata",
        "SchemaName": rel, "ReferencedEntity": target, "ReferencedAttribute": target_key, "ReferencingEntity": table,
        "CascadeConfiguration": {"Assign": "NoCascade", "Delete": "RemoveLink", "Merge": "NoCascade", "Reparent": "NoCascade",
                                 "Share": "NoCascade", "Unshare": "NoCascade"},
        "Lookup": {"@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata", "SchemaName": name,
                   "DisplayName": label(display), "Description": label(desc),
                   "RequiredLevel": {"Value": "None", "CanBeChanged": True}}}, SOL), f"lookup {table}.{name}")


def ensure_column(table, spec, check):
    logical = spec[0].lower()
    if exists_column(table, logical):
        return False
    if check:
        print(f"- missing {table}.{logical}")
        return True
    if spec[1] == "lookup":
        create_lookup(table, spec)
    else:
        ok(*dv.request("POST", f"EntityDefinitions(LogicalName='{table}')/Attributes", attribute_body(spec), SOL), f"column {table}.{logical}")
    print(f"+ {table}.{logical}")
    return True


def ensure_table(schema, display, plural, desc, check):
    logical = schema.lower()
    if exists_table(logical):
        return False
    if check:
        print(f"- missing table {logical}")
        return True
    ok(*dv.request("POST", "EntityDefinitions", {
        "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
        "SchemaName": schema, "DisplayName": label(display), "DisplayCollectionName": label(plural), "Description": label(desc),
        "OwnershipType": "UserOwned", "HasActivities": False, "HasNotes": True, "IsActivity": False,
        "IsAuditEnabled": {"Value": True, "CanBeChanged": True},
        "Attributes": [{"@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "SchemaName": "gc_Name", "IsPrimaryName": True,
                        "MaxLength": 200, "FormatName": {"Value": "Text"}, "RequiredLevel": {"Value": "ApplicationRequired", "CanBeChanged": True},
                        "DisplayName": label("Name"), "Description": label("Short name")}]}, SOL), "create table " + logical)
    print(f"+ table {logical}")
    return True


def ensure_agent_options(check):
    b = ok(*dv.get("GlobalOptionSetDefinitions(Name='gc_agent')"), "read gc_agent")
    have = {o["Value"] for o in b["Options"]}
    changed = False
    for value, text_ in AGENT_OPTIONS:
        if value in have:
            continue
        changed = True
        if check:
            print(f"- missing gc_agent option {text_}")
            continue
        ok(*dv.request("POST", "InsertOptionValue", {"OptionSetName": "gc_agent", "Value": value, "Label": label(text_),
                                                     "SolutionUniqueName": SOLUTION}), "insert option " + text_)
        print(f"+ gc_agent option {text_}")
    return changed


def ensure_options(check):
    changed = set()
    for table, column, value, text_ in EXTRA_OPTIONS:
        s_, b = dv.get(f"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='{column}')/Microsoft.Dynamics.CRM.PicklistAttributeMetadata?$select=LogicalName&$expand=OptionSet($select=Options)")
        if s_ != 200 or any(o["Value"] == value for o in b["OptionSet"]["Options"]):
            continue
        if check:
            print(f"- missing option {table}.{column} {text_}")
            continue
        ok(*dv.request("POST", "InsertOptionValue", {"EntityLogicalName": table, "AttributeLogicalName": column, "Value": value, "Label": label(text_),
                                                     "SolutionUniqueName": SOLUTION}), f"option {table}.{column}")
        print(f"+ option {table}.{column} {text_}")
        changed.add(table)
    return changed


def main():
    check = "--check" in sys.argv
    touched = set()
    for schema, display, plural, desc, columns in TABLES:
        table = schema.lower()
        if ensure_table(schema, display, plural, desc, check):
            touched.add(table)
        if check and table in touched:
            continue  # table missing: its columns are too
        for spec in columns:
            if ensure_column(table, spec, check):
                touched.add(table)
    for table, spec in EXTRA_COLUMNS:
        if ensure_column(table, spec, check):
            touched.add(table)
    options = ensure_agent_options(check)
    touched |= ensure_options(check)
    if check:
        print("nothing missing" if not touched and not options else "")
        return
    if touched or options:
        xml = "".join(f"<entity>{t}</entity>" for t in sorted(touched))
        ok(*dv.request("POST", "PublishXml", {"ParameterXml": f"<importexportxml><entities>{xml}</entities>"
                                                             "<optionsets><optionset>gc_agent</optionset></optionsets></importexportxml>"},
                       timeout=600), "publish")
        print(f"published {len(touched)} tables")
    else:
        print("schema up to date")


if __name__ == "__main__":
    main()

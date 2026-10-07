"""Email Desk test without Gmail: feeds Gmail-format messages through gc_IngestEmail, gc_AttachEmailFile and gc_Agent_MailTriage.

The samples are the four real trade emails shared on 6 Oct 2026 (vanadium RFQ, Tanzanian coal offer, Kyrgyz reply to an LOI,
BENE strontium LOI as a PDF) plus a phishing mail, a vendor pitch and a broker "mandate" chain. Senders use .example domains,
every subject starts with [AGENT-TEST], and Gmail ids start with agent-test- so cleanup finds them.

Usage:
  python3 tools/mail_test.py run [--dry] [--only vanadium,scam]   # ingest + triage, then print the verdicts
  python3 tools/mail_test.py cleanup                              # delete the test emails, threads and attachments
"""
import base64, json, os, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402
from seed_test_data import pdf  # noqa: E402

PREFIX = "[AGENT-TEST]"
TO = "desk+dealos@gmail.com"
PASS = "mx.google.com; dkim=pass header.i=@{d}; spf=pass smtp.mailfrom={d}; dmarc=pass (p=NONE) header.from={d}"

LOI_PDF = [
    "bene GLOBAL COMMODITIES - 8 The Green Street, Suite B, Dover, Delaware 19901, USA",
    "30 September 2026",
    "LETTER OF INTENT FOR PURCHASE OF STRONTIUM METAL",
    "To: Any authorized Strontium Metal supplier",
    "BENE LLC expresses its preliminary interest in purchasing Strontium Metal of non-sanctioned origin for supply to India,",
    "subject to agreement on final commercial terms and verification of the seller, origin, specifications and payment.",
    "Product: Strontium Metal   Purity: 99% minimum   Quantity: 3 MT",
    "Preferred delivery basis (seller to quote): CIP Mumbai Airport, India / FCA named point in country of origin",
    "Information requested: best price on CIP or FCA basis; specifications, recent COA, country-of-origin documents;",
    "earliest shipment schedule; payment terms and banking instrument; inspection terms.",
    "This letter records preliminary interest only and creates no binding obligation.",
    "For BENE LLC - Manish Kothary, CEO",
]

SAMPLES = {
    "vanadium": dict(
        sender="Rakesh Jain <rakesh.jain@ferroalloys-test.example>", subject="Requirement: Vanadium Pentoxide 10-15 MT India",
        text="""Hi, I have an immediate requirement for Vanadium Pentoxide (V2O5) in India.

Quantity: 10-15 MT
Required specification:
V2O5: 98% min
P: 0.05% max
-10 mm: 5% max
+60 mm: 10% max

Packing: 10 kg bags, consolidated into 1 MT jumbo bags.
Delivery: Immediate
Basis: Delivered/FOR India, inclusive of packing, freight and insurance.
Payment: 45 days credit.

Please let me know if you can supply this material. If yes, please send your best price, available quantity, origin,
technical specification/COA, delivery lead time, and payment terms.

Please treat this requirement as confidential.

Rakesh Jain
Purchase Manager, Ferro Alloys Test Pvt Ltd
www.ferroalloys-test.example""", expect="Genuine"),
    "coal": dict(
        sender="Coal Sales <sales@mtwara-coal-test.example>", subject="Offer: Tanzanian thermal coal FOB Mtwara",
        text="""Tanzanian thermal coal available for supply from Mtwara Port.

Indicative specs:
- GCV: ~5,400 kcal/kg GAR
- Ash: ~20.6%
- Sulphur: ~1.16%
- Moisture: ~10.3%
- Bulk vessel quantities: ~30,000+ MT
- Independent SGS testing available
- FOB Mtwara / CIF India can be discussed

Recent 33,700 MT vessel loading from Mtwara provides shipment track record.

Please let me know if this specification is of interest and your required quantity/destination port.

Regards, Sales Desk, Mtwara Coal Test Ltd, www.mtwara-coal-test.example""", expect="Genuine or Review"),
    "kyrgyz": dict(
        sender="Roman <roman@kg-rareearth-test.example>", subject="Re: LOI - Rare Earth Oxides",
        text="""Dear Harrsh,

Thank you for your Letter of Intent and interest in our Rare Earth products.

We confirm that the requested materials (Dysprosium Oxide 4N, Gadolinium Oxide 4N, Yttrium Oxide 5N, and Yttrium Metal 3N) are within
our production capabilities. However, our current inventory and production capacity are strictly allocated to existing long-term contract clients.

To evaluate allocating volumes and placing your request in our production queue, we must first align on the transaction structure.
We do not provide CIF/CIP quotations or confirm target prices without a mutually agreed commercial framework.

Please clarify:
1. Delivery Basis: Our standard model is strictly FCA Central Asia. We do not operate on CIF/CIP terms for initial allocations.
2. Payment Terms: Standard allocation requires 100% advance payment, made 2 to 3 months prior to dispatch, to secure a production slot.
3. End-Use & Compliance: Please specify the end-user, industry sector, and final destination, required for export control verification.
4. Transaction Framework: Please outline your proposed structure, including banking instruments and inspection terms
   (inspection to be conducted at our warehouse prior to shipment).

Indicative prices and FCA terms can only be reviewed after the commercial and payment structure is agreed. Upon receiving your confirmed
framework, we will present a formal corporate offer and a realistic delivery timeline.

Best regards,
Roman""", expect="Genuine or Review"),
    "bene_loi": dict(
        sender="Manish Kothary <manish@bene-test.example>", subject="Soft LOI - Strontium Metal 3 MT",
        text="""Dear Sir or Madam,

Please find attached our Letter of Intent for Strontium Metal (99% min, 3 MT) for supply to India, CIP Mumbai Airport or FCA origin.
Kindly send your best offer with COA and country-of-origin documents.

Best regards,
Manish Kothary, CEO, BENE LLC
8 The Green Street, Suite B, Dover, Delaware 19901, USA
https://www.bene-test.example/""", attach=("BENE LLC Strontium Metal Soft LOI.pdf", LOI_PDF), expect="Genuine"),
    "scam": dict(
        sender="Procurement Dept <purchase.order.dept@gmail.com>", subject="URGENT: Purchase Order 4471 - confirm now",
        auth="mx.google.com; spf=fail smtp.mailfrom=bulk-sender.example; dmarc=fail (p=REJECT) header.from=gmail.com",
        reply_to="orders@payments-update.example",
        text="""Dear Supplier,

Kindly find our purchase order attached. Login with your email password at https://bit.ly/po-4471 to view and confirm today,
otherwise the order will be cancelled. Our bank details have changed, please update before shipment.

Regards, Procurement""", attach=("PO_4471.html", None), expect="Ignored"),
    "pitch": dict(
        sender="Growth Team <hello@seo-agency-test.example>", subject="Get your commodity business to page 1 of Google",
        text="""Hi there,

We help commodity traders rank on page 1 of Google in 90 days. Our SEO and lead generation packages start at USD 299/month.
Book a free call: https://calendly.example/seo

Unsubscribe here.""", extra={"List-Unsubscribe": "<mailto:unsub@seo-agency-test.example>"}, expect="Ignored"),
    "mandate": dict(
        sender="Intl Petroleum Mandate <buyers.mandate.direct@yahoo.com>", subject="Buyer's mandate - 2,000,000 MT REBCO + 50,000 MT rare earth",
        auth=None,
        text="""Dear Seller,

We are the direct buyer's mandate for a major refinery. Our buyer needs 2,000,000 MT REBCO monthly x 12 months and 50,000 MT
Dysprosium Oxide, any origin. Procedure: Seller issues SCO, buyer issues ICPO, seller sends FCO and POP, buyer pays 0.5% commission
upfront to secure the allocation. Seller side must sign our IMFPA and NCNDA before any details.

Send your full corporate offer today.""", expect="Ignored"),
}


def b64u(data):
    return base64.urlsafe_b64encode(data if isinstance(data, bytes) else data.encode()).decode().rstrip("=")


def gmail_json(key, s):
    domain = s["sender"].split("@")[1].rstrip(">")
    headers = [("From", s["sender"]), ("To", TO), ("Subject", f"{PREFIX} {s['subject']}"),
               ("Date", "Tue, 6 Oct 2026 20:59:22 +0530"), ("Message-ID", f"<{key}@agent-test.example>")]
    auth = s.get("auth", PASS.format(d=domain))
    if auth:
        headers.append(("Authentication-Results", auth))
    if s.get("reply_to"):
        headers.append(("Reply-To", s["reply_to"]))
    for k, v in (s.get("extra") or {}).items():
        headers.append((k, v))
    parts = [{"mimeType": "text/plain", "filename": "", "body": {"size": len(s["text"]), "data": b64u(s["text"])}}]
    attachment = None
    if s.get("attach"):
        name, lines = s["attach"]
        data = pdf(lines) if lines else b"<html><body><form action='https://payments-update.example/login'>Password: <input></form></body></html>"
        mime = "application/pdf" if lines else "text/html"
        attachment = (name, mime, data)
        parts.append({"mimeType": mime, "filename": name, "body": {"attachmentId": f"att-{key}", "size": len(data)}})
    msg = {"id": f"agent-test-{key}", "threadId": f"agent-test-thread-{key}", "labelIds": ["INBOX", "UNREAD"], "snippet": s["text"][:120],
           "internalDate": "1791302362000",
           "payload": {"mimeType": "multipart/mixed", "headers": [{"name": k, "value": v} for k, v in headers], "parts": parts}}
    return json.dumps(msg), attachment


def call(api, body, timeout=150):
    s, b = dv.request("POST", api, body, timeout=timeout)
    if s >= 300:
        raise RuntimeError(f"{api}: {s} {json.dumps(b.get('error', b))[:800]}")
    return b


def run(dry, only):
    rows = []
    for key, sample in SAMPLES.items():
        if only and key not in only:
            continue
        message_json, attachment = gmail_json(key, sample)
        ing = call("gc_IngestEmail", {"MessageJson": message_json})
        pending = json.loads(ing.get("Attachments") or "[]")
        print(f"{key}: {ing['Status']} message {ing['MessageId']} ({ing['Summary']}) attachments to fetch: {len(pending)}")
        for att in pending:
            if attachment and att["fileName"] == attachment[0] and attachment[1] != "text/html":
                r = call("gc_AttachEmailFile", {"MessageId": ing["MessageId"], "FileName": attachment[0], "MimeType": attachment[1], "Data": b64u(attachment[2])})
                print(f"   attached {attachment[0]}: {r['Status']} {r['DocumentId']}")
        if not ing.get("NeedsTriage"):
            print("   already triaged")
            continue
        t = time.time()
        s, b = dv.request("POST", "gc_Agent_MailTriage", {"SubjectId": ing["MessageId"], "DryRun": dry}, timeout=150)
        if s >= 300:
            print(f"   triage HTTP {s}: {json.dumps(b.get('error', b))[:600]}")
            continue
        res = json.loads(b.get("Result") or "{}")
        r = res.get("result") or {}
        rows.append((key, sample["expect"], r.get("verdict"), r.get("category"), r.get("genuineness_score"), r.get("final_score"), b.get("Status")))
        print(f"   triage {b.get('Status')} in {time.time() - t:.0f}s: {r.get('verdict')} / {r.get('category')} "
              f"model {r.get('genuineness_score')} → final {r.get('final_score')}  label {r.get('label')}")
        if res.get("error"):
            print("   error:", res["error"])
        for reason in (r.get("reasons") or [])[:5] + (r.get("code_reasons") or []):
            print("     -", reason)
        if r.get("red_flags"):
            print("     red flags:", "; ".join(r["red_flags"]))
        time.sleep(2)  # gentle on the model rate limit
    print("\n%-10s %-18s %-9s %-18s %5s %5s" % ("sample", "expected", "verdict", "category", "model", "final"))
    for key, expect, verdict, cat, ms, fs, status in rows:
        print("%-10s %-18s %-9s %-18s %5s %5s%s" % (key, expect, verdict, cat, ms, fs, "" if status == "Succeeded" else "  (" + str(status) + ")"))
    wrong = [key for key, expect, verdict, *_ in rows if not verdict or verdict not in expect.split(" or ")]
    print("\nTRIAGE PASSED" if not wrong else "\n✗ TRIAGE MISMATCH: " + ", ".join(wrong))
    if wrong:
        sys.exit(1)


def cleanup():
    msgs = dv.get("gc_messages?$select=gc_messageid,_gc_conversation_value&$filter=startswith(gc_externalid,'agent-test-')")[1].get("value", [])
    convs = {m["_gc_conversation_value"] for m in msgs if m.get("_gc_conversation_value")}
    docs = 0
    for m in msgs:
        for d in dv.get(f"gc_documents?$select=gc_documentid&$filter=_gc_message_value eq {m['gc_messageid']}")[1].get("value", []):
            dv.delete(f"gc_documents({d['gc_documentid']})")
            docs += 1
        dv.delete(f"gc_messages({m['gc_messageid']})")
    for c in convs:
        dv.delete(f"gc_conversations({c})")
    print(f"removed {len(msgs)} test emails, {len(convs)} threads, {docs} attachments")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "run":
        only = None
        if "--only" in sys.argv:
            only = set(sys.argv[sys.argv.index("--only") + 1].split(","))
        run("--dry" in sys.argv, only)
    elif cmd == "cleanup":
        cleanup()
    else:
        print(__doc__)

"""Import trade-data exports (customs shipment records) as leads for the Email Desk.

Each row is one shipment: SUPPLIER (exporter) → seller lead, PURCHASER (importer) → buyer lead. Rows of the same company are
rolled up: products seen, HS codes, shipments, total weight, last shipment date. Existing leads (same name and role) are merged.
Trade data rarely has emails: add a column SUPPLIER EMAIL / PURCHASER EMAIL (or EMAIL) to make a lead contactable, or fill
the email on the lead later. The desk only writes to leads that have an email.

Columns are matched by name, case-insensitive (the export in the screenshot works as is):
  DATES / DATE, PRODUCT DESCRIPTION, HS CODE, SUPPLIER, PURCHASER, COUNTRY OF ORIGIN, PURCHASING COUNTRY, WEIGHT(KG), QUANTITY, UNIT,
  SUPPLIER EMAIL, PURCHASER EMAIL, SUPPLIER PHONE, SUPPLIER WEBSITE, SUPPLIER CONTACT

Usage:
  python3 tools/import_leads.py data/calcium-metal.csv --source "Trade data provider" [--sides seller,buyer] [--dry]
  python3 tools/import_leads.py data/export.xlsx --source "..."        # .xlsx needs: pip install openpyxl
"""
import argparse, csv, json, os, re, sys
from collections import defaultdict
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

B = 303300000
ROLE = {"seller": B, "buyer": B + 1}
SOURCE_TRADE_DATA = B
STATUS_NEW = B

ALIASES = {
    "date": ["dates", "date", "shipment date", "arrival date"],
    "product": ["product description", "product", "description", "goods description"],
    "hs": ["hs code", "hscode", "hs"],
    "supplier": ["supplier", "exporter", "shipper", "seller"],
    "purchaser": ["purchaser", "importer", "consignee", "buyer"],
    "origin": ["country of origin", "origin country", "origin"],
    "destination": ["purchasing country", "destination country", "destination"],
    "weight": ["weight(kg)", "weight (kg)", "weight", "net weight"],
    "quantity": ["quantity", "qty"],
    "unit": ["unit", "uqc"],
    "supplier_email": ["supplier email", "exporter email", "email"],
    "purchaser_email": ["purchaser email", "importer email"],
    "supplier_phone": ["supplier phone", "phone"],
    "supplier_website": ["supplier website", "website"],
    "supplier_contact": ["supplier contact", "contact"],
}


def read_rows(path):
    if path.lower().endswith((".xlsx", ".xlsm")):
        try:
            import openpyxl
        except ImportError:
            sys.exit("Reading .xlsx needs openpyxl: pip install openpyxl (or save the sheet as CSV).")
        ws = openpyxl.load_workbook(path, read_only=True, data_only=True).active
        rows = list(ws.iter_rows(values_only=True))
        header = [str(h or "").strip() for h in rows[0]]
        return [dict(zip(header, ["" if v is None else v for v in r])) for r in rows[1:]]
    with open(path, newline="", encoding="utf-8-sig") as f:
        return list(csv.DictReader(f))


def column_map(header):
    norm = {re.sub(r"\s+", " ", h.strip().lower()): h for h in header}
    out = {}
    for key, names in ALIASES.items():
        for n in names:
            if n in norm:
                out[key] = norm[n]
                break
    return out


def clean_company(name):
    name = re.sub(r"\s+", " ", str(name or "")).strip(" .*,")
    return "" if name.upper() in ("", "NONE", "N/A", "NA", "-", "TO ORDER", "UNKNOWN") else name


def parse_date(v):
    if isinstance(v, datetime):
        return v
    for fmt in ("%Y-%m-%d", "%d-%m-%Y", "%d/%m/%Y", "%m/%d/%Y", "%Y/%m/%d", "%d %b %Y"):
        try:
            return datetime.strptime(str(v).strip()[:10], fmt)
        except ValueError:
            continue
    return None


def to_float(v):
    try:
        return float(str(v).replace(",", ""))
    except ValueError:
        return 0.0


def aggregate(rows, cols, sides):
    leads = defaultdict(lambda: {"products": [], "hs": set(), "shipments": 0, "weight": 0.0, "last": None, "country": None,
                                 "email": None, "phone": None, "website": None, "contact": None})
    for r in rows:
        product = str(r.get(cols.get("product", ""), "") or "").strip()
        product = re.sub(r"\s+", " ", product)[:160]
        date = parse_date(r.get(cols.get("date", ""), ""))
        weight = to_float(r.get(cols.get("weight", ""), 0) or 0)
        hs = str(r.get(cols.get("hs", ""), "") or "").strip()
        for side in sides:
            name = clean_company(r.get(cols.get("supplier" if side == "seller" else "purchaser", ""), ""))
            if not name:
                continue
            lead = leads[(side, name.upper())]
            lead["name"] = name
            lead["side"] = side
            lead["shipments"] += 1
            lead["weight"] += weight
            if product and product.upper() not in (p.upper() for p in lead["products"]):
                lead["products"].append(product)
            if hs and hs.upper() != "NONE":
                lead["hs"].add(hs)
            if date and (lead["last"] is None or date > lead["last"]):
                lead["last"] = date
            lead["country"] = lead["country"] or str(r.get(cols.get("origin" if side == "seller" else "destination", ""), "") or "").strip() or None
            if side == "seller":
                for key in ("email", "phone", "website", "contact"):
                    v = str(r.get(cols.get("supplier_" + key, ""), "") or "").strip()
                    if v and not lead[key]:
                        lead[key] = v.lower() if key == "email" else v
            else:
                v = str(r.get(cols.get("purchaser_email", ""), "") or "").strip()
                if v and not lead["email"]:
                    lead["email"] = v.lower()
    return list(leads.values())


def upsert(lead, source, dry):
    name = lead["name"].replace("'", "''")
    s, b = dv.get(f"gc_leads?$select=gc_leadid,gc_commodities,gc_shipments,gc_email&$filter=gc_name eq '{name}' and gc_role eq {ROLE[lead['side']]}")
    existing = (b.get("value") or [None])[0] if s == 200 else None
    products = "\n".join(lead["products"][:40])
    volume = f"{lead['shipments']} shipment(s), {lead['weight'] / 1000:,.1f} MT in this file"
    body = {"gc_name": lead["name"][:200], "gc_role": ROLE[lead["side"]], "gc_source": SOURCE_TRADE_DATA, "gc_sourceref": source[:400],
            "gc_hscodes": ", ".join(sorted(lead["hs"]))[:400], "gc_volume": volume[:400], "gc_shipments": lead["shipments"]}
    if lead["country"]:
        body["gc_country"] = lead["country"][:100]
    if lead["last"]:
        body["gc_lastseen"] = lead["last"].strftime("%Y-%m-%dT00:00:00Z")
    for key, col in (("email", "gc_email"), ("phone", "gc_phone"), ("website", "gc_website"), ("contact", "gc_contactname")):
        if lead[key]:
            body[col] = lead[key][:320]
    if existing:
        merged = (existing.get("gc_commodities") or "").split("\n")
        for p in lead["products"]:
            if p.upper() not in (m.upper() for m in merged):
                merged.append(p)
        body["gc_commodities"] = "\n".join(x for x in merged if x)[:100000]
        body["gc_shipments"] = (existing.get("gc_shipments") or 0) + lead["shipments"]
        if existing.get("gc_email") and "gc_email" in body:
            body.pop("gc_email")  # never overwrite a contact someone already found
        if not dry:
            s, b = dv.patch(f"gc_leads({existing['gc_leadid']})", body)
            if s >= 300:
                return f"! {lead['name']}: {json.dumps(b)[:300]}"
        return f"= {lead['side']:6} {lead['name']}"
    body["gc_commodities"] = products
    body["gc_status"] = STATUS_NEW
    if not dry:
        s, b = dv.post("gc_leads", body)
        if s >= 300:
            return f"! {lead['name']}: {json.dumps(b)[:300]}"
    return f"+ {lead['side']:6} {lead['name']}" + ("" if lead["email"] else "   (no email yet)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--source", required=True, help="Provider or file description, kept on each lead")
    ap.add_argument("--sides", default="seller,buyer", help="seller, buyer or both (comma separated)")
    ap.add_argument("--dry", action="store_true")
    args = ap.parse_args()
    rows = read_rows(args.file)
    if not rows:
        sys.exit("The file has no rows.")
    cols = column_map(list(rows[0].keys()))
    if "supplier" not in cols and "purchaser" not in cols:
        sys.exit(f"No SUPPLIER or PURCHASER column found. Columns: {list(rows[0].keys())}")
    sides = [x.strip() for x in args.sides.split(",") if x.strip() in ROLE]
    leads = aggregate(rows, cols, sides)
    print(f"{len(rows)} rows → {len(leads)} leads ({', '.join(sides)})" + ("  [dry run]" if args.dry else ""))
    for lead in sorted(leads, key=lambda l: (l["side"], -l["shipments"])):
        print(upsert(lead, f"{args.source}: {os.path.basename(args.file)}", args.dry))


if __name__ == "__main__":
    main()

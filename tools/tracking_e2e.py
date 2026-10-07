"""Tracking updates after signing, end to end in Dev (no AI calls).

On a Signed [AGENT-TEST] email desk deal (the buyer-first E2E deal by default):
  1. inspection → Booked (date, agency)  → drafts to the buyer and the seller; the buyer's names no seller and no warehouse
  2. shipment   → In Transit (ports, ETA) → buyer: sailing and ETA; seller: shipping documents request
  3. shipment   → Delivered               → buyer: confirm receipt; seller: delivered
  4. the same status again                → nothing new (each status once per side)
Drafts go to the test threads (.example addresses); nobody real gets anything.
Usage: python3 tools/tracking_e2e.py run [deal-guid]
"""
import json, os, sys, time
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402
import desk_e2e as e  # noqa: E402

B = 303300000
INSPECTION_BOOKED = B + 1
SHIPMENT = {"In Transit": B + 3, "Delivered": B + 6}


def signed_deal():
    if len(sys.argv) > 2:
        return sys.argv[2]
    rows = e.get(f"gc_deals?$select=gc_dealid,gc_name&$filter=gc_emaildesk eq true and gc_stage eq {B + 5} and startswith(gc_name,'Desk')&$orderby=modifiedon desc&$top=5")
    rows = [r for r in rows if "AGENT-TEST" in r["gc_name"]]
    if not rows:
        raise SystemExit("No signed [AGENT-TEST] email desk deal: run tools/desk_e2e.py run first.")
    return rows[0]["gc_dealid"]


def threads(deal_id):
    deal = e.get(f"gc_deals({deal_id})?$select=_gc_requirement_value,_gc_seller_value")
    buyer = e.get(f"gc_conversations?$select=gc_conversationid&$filter=_gc_requirement_value eq {deal['_gc_requirement_value']} and gc_side eq {B}")[0]["gc_conversationid"]
    seller = e.get(f"gc_conversations?$select=gc_conversationid&$filter=_gc_deal_value eq {deal_id} and gc_side eq {B + 1}")[0]["gc_conversationid"]
    seller_name = e.get(f"accounts({deal['_gc_seller_value']})?$select=name")["name"]
    return buyer, seller, seller_name


def new_draft(conv, since, words):
    for d in e.drafts(conv, since=since):
        if words.lower() in (d.get("gc_text") or "").lower():
            return d
    return None


def server_now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def run():
    deal = signed_deal()
    buyer, seller, seller_name = threads(deal)
    e.log(f"deal {deal}, seller {seller_name}")

    e.log("1. inspection booked")
    insp = e.get(f"gc_inspections?$select=gc_inspectionid,gc_status&$filter=_gc_deal_value eq {deal}&$top=1")
    since = server_now()
    when = (datetime.now(timezone.utc) + timedelta(days=5)).strftime("%Y-%m-%dT09:00:00Z")
    if insp:
        dv.patch(f"gc_inspections({insp[0]['gc_inspectionid']})", {"gc_status": B, "gc_deskupdates": None})  # back to Requested, so Booked is a change
        time.sleep(5)
        dv.patch(f"gc_inspections({insp[0]['gc_inspectionid']})", {"gc_status": INSPECTION_BOOKED, "gc_scheduledon": when})
    else:
        dv.post("gc_inspections", {"gc_name": "[AGENT-TEST] Inspection", "gc_status": INSPECTION_BOOKED, "gc_scheduledon": when,
                                   "gc_Deal@odata.bind": f"/gc_deals({deal})"})
    b = e.wait("inspection update to the buyer", lambda: new_draft(buyer, since, "inspection"), 6, kick=False)
    s = e.wait("inspection update to the seller", lambda: new_draft(seller, since, "cargo ready"), 3, kick=False)
    if seller_name.lower() in b["gc_text"].lower():
        raise SystemExit("✗ the buyer's inspection update names the seller")
    e.log("   buyer: " + b["gc_text"].split("\n\n")[1][:160])

    e.log("2. shipment in transit")
    since = server_now()
    eta = (datetime.now(timezone.utc) + timedelta(days=21)).strftime("%Y-%m-%dT00:00:00Z")
    ship = e.get(f"gc_shipments?$select=gc_shipmentid&$filter=_gc_deal_value eq {deal}&$top=1")
    fields = {"gc_status": SHIPMENT["In Transit"], "gc_originport": "Fangcheng", "gc_destinationport": "Nhava Sheva", "gc_eta": eta, "gc_deskupdates": None}
    if ship:
        ship_id = ship[0]["gc_shipmentid"]
        dv.patch(f"gc_shipments({ship_id})", {"gc_status": B, "gc_deskupdates": None})
        time.sleep(5)
        dv.patch(f"gc_shipments({ship_id})", fields)
    else:
        dv.post("gc_shipments", dict(fields, gc_name="[AGENT-TEST] Shipment", **{"gc_deal@odata.bind": f"/gc_deals({deal})"}))
        ship_id = e.get(f"gc_shipments?$select=gc_shipmentid&$filter=_gc_deal_value eq {deal}&$top=1")[0]["gc_shipmentid"]
    b = e.wait("sailing update to the buyer (ETA)", lambda: new_draft(buyer, since, "ETA Nhava Sheva"), 6, kick=False)
    e.wait("documents request to the seller", lambda: new_draft(seller, since, "shipping documents"), 3, kick=False)
    if seller_name.lower() in b["gc_text"].lower():
        raise SystemExit("✗ the buyer's sailing update names the seller")

    e.log("3. delivered")
    since = server_now()
    dv.patch(f"gc_shipments({ship_id})", {"gc_status": SHIPMENT["Delivered"]})
    e.wait("receipt confirmation request to the buyer", lambda: new_draft(buyer, since, "received in good order"), 6, kick=False)
    e.wait("delivered note to the seller", lambda: new_draft(seller, since, "delivered to our buyer"), 3, kick=False)

    e.log("4. the same status again drafts nothing")
    since = server_now()
    dv.patch(f"gc_shipments({ship_id})", {"gc_destinationport": "Nhava Sheva (JNPT)"})
    dv.patch(f"gc_shipments({ship_id})", {"gc_status": SHIPMENT["Delivered"]})
    time.sleep(60)
    if new_draft(buyer, since, "received") or new_draft(seller, since, "delivered"):
        raise SystemExit("✗ a repeated status drafted again")
    e.log("✓ no repeat")
    print("TRACKING END TO END PASSED")


if __name__ == "__main__":
    if sys.argv[1:2] == ["run"]:
        run()
    else:
        print(__doc__)

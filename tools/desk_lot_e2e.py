"""End-to-end test of seller lots (several buyers bid, the highest price wins) on the real Gmail mailbox and Dev, by email only.

  1. a seller offers 20 MT Ferro Molybdenum at USD 41,000 CIF (a new email, not an answer to an enquiry)
                                                       → lot saved, offered (masked, USD 42,230 = +3%) to two buyer leads
  2. buyer A accepts our price for 10 MT; buyer B offers USD 43,000 for 15 MT
                                                       → two bids recorded, "offers close on ..." replies
  3. the deadline is moved to now and Desk timers runs  → B wins (highest price, 15 MT fits), A does not fit in the 5 MT left:
                                                         Confirm deal task for B, confirmations to B and the seller, "not this time" to A
  4. the Confirm deal task is approved                  → B's deal Terms Agreed (the rest is the normal contract path, see desk_e2e.py)

Seller-first negotiation (open-ended lot, `open`):
  1. a seller offers 8 MT cobalt at USD 32,000 "open until sold"  → open-ended lot (no bid deadline), offered at USD 32,960 to a buyer lead
  2. the buyer counters at USD 32,000                              → our bid to the seller: USD 31,067.96 (buyer masked)
  3. the seller counters at USD 31,500                             → our new price to the buyer: USD 32,445
  4. the buyer accepts                                             → our bid to the seller: USD 31,500
  5. the seller accepts that bid                                   → lot allocated, Confirm deal, confirmations to both
  6. the Confirm deal task is approved                             → deal Terms Agreed at seller USD 31,500 / buyer USD 32,445

Timed lot with no bid at the seller's price (`below`): the seller's "valid 48 hours" sets a 48 h window; the only bid is below
the seller's price; at the deadline the best bid goes to the seller and the lot carries on open-ended.

Usage: python3 tools/desk_lot_e2e.py run | single | open | below | coal | status
       (run: two competing buyers, about 15 min; single: one buyer, early close; open: seller-first negotiation; below: timed → open-ended)
Records are marked [AGENT-TEST]; counterparties use .example addresses (drafts to them bounce, which is expected).
"""
import csv, json, os, subprocess, sys
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402
from gmail_kit import Kit, run_flow, off  # noqa: E402
from desk_e2e import B, TRIAGE, get, log, wait, mime, message, send_all, insert, ingested, approve  # noqa: E402

ROOT = dv.ROOT
STATE = os.path.join(ROOT, "build", "desk_lot_e2e.json")
LOT_OPEN, LOT_ALLOCATED = B, B + 2
OFFER_OPEN = B
CANCELLED = B + 12


def save(state):
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    json.dump(state, open(STATE, "w"), indent=2, default=str)


def buyer_thread(lot_id, email):
    """The buyer thread the lot was offered in (via the lot's deals: a buyer with an open requirement gets it in that thread), by the draft's recipient."""
    for d in get(f"gc_deals?$select=_gc_requirement_value&$filter=_gc_sellerlot_value eq {lot_id}"):
        for c in get(f"gc_conversations?$select=gc_conversationid,_gc_requirement_value,gc_gmailthreadid&$filter=_gc_requirement_value eq {d['_gc_requirement_value']} and gc_side eq {B}"):
            if get(f"gc_messages?$select=gc_messageid&$filter=_gc_conversation_value eq {c['gc_conversationid']} and gc_toaddress eq '{email}'"):
                return c
    return None


def run():
    tag = "[AGENT-TEST] LOT " + datetime.now().strftime("%m%d-%H%M")
    state = {"tag": tag, "started": datetime.now().isoformat()}
    seller, buyer_a, buyer_b = "wang.lei@xinmo-moly.example", "anita.rao@alpha-steel-buyer.example", "ben.koh@beta-alloys-buyer.example"
    with Kit(keep_on=True) as kit:
        me = kit.call("profile")["emailAddress"].lower()
        user, domain = me.split("@")
        desk = f"{user}+dealos@{domain}"
        log(f"mailbox {me}; run {tag}")

        # 0. buyer leads for ferro molybdenum (trade data: purchasers become buyer leads)
        leads_csv = os.path.join(ROOT, "build", "lot_e2e_leads.csv")
        rows = [["DATES", "HS CODE", "PRODUCT DESCRIPTION", "SUPPLIER", "PURCHASER", "COUNTRY OF ORIGIN", "PURCHASING COUNTRY", "WEIGHT(KG)", "PURCHASER EMAIL"],
                ["2026-08-11", "72027000", "FERRO MOLYBDENUM FEMO70 LUMPS 10-50MM", "X", "[AGENT-TEST] Alpha Steel Test Pvt Ltd", "CHINA", "INDIA", "12000", buyer_a],
                ["2026-07-02", "72027000", "FERRO MOLYBDENUM 70% MIN", "X", "[AGENT-TEST] Beta Alloys Test Ltd", "CHINA", "INDIA", "16000", buyer_b]]
        with open(leads_csv, "w", newline="") as f:
            csv.writer(f).writerows(rows)
        print(subprocess.run([sys.executable, os.path.join(ROOT, "tools", "import_leads.py"), leads_csv, "--source", "[AGENT-TEST] LOT E2E", "--sides", "buyer"],
                             capture_output=True, text=True).stdout)

        # 1. the seller's offer to sell (a new thread)
        gid, seller_thread = insert(kit, mime(f"Wang Lei <{seller}>", desk, f"{tag} Offer: Ferro Molybdenum FeMo70 20 MT",
                                              "Dear Sir,\n\nWe offer from stock:\n\nProduct: Ferro Molybdenum FeMo70 (Mo 70% min, Cu 0.5% max), lumps 10-50 mm\n"
                                              "Quantity: 20 MT\nPrice: USD 41,000 per MT CIF Nhava Sheva\nPacking: 1 MT steel drums\nOrigin: China\n"
                                              "Shipment: within 3 weeks of order\nPayment: LC at sight\nValidity: 7 days\n\nBest regards,\nWang Lei\n[AGENT-TEST] Xinmo Moly Test Co"))
        log("1. seller offered 20 MT FeMo70 at USD 41,000 CIF")
        m = wait("seller email triaged", lambda: (lambda x: x if x and x.get("gc_triage") else None)(message(gid)))
        log(f"   triage: {TRIAGE.get(m['gc_triage'])}, score {m.get('gc_triagescore')}")
        conv = m["_gc_conversation_value"]
        lot = wait("lot saved and offered to buyers", lambda: (lambda c: c.get("_gc_sellerlot_value"))(get(f"gc_conversations({conv})?$select=_gc_sellerlot_value")), 12)
        state.update(seller_conv=conv, lot=lot, seller_gmail_thread=seller_thread)
        save(state)
        info = get(f"gc_sellerlots({lot})?$select=gc_name,gc_quantity,gc_price,gc_biddeadline")
        log(f"   lot {info['gc_name']}: {info['gc_quantity']} at {info['gc_price']}, offers close {info['gc_biddeadline']}")
        ta = wait("lot offered to buyer A", lambda: buyer_thread(lot, buyer_a), 5, kick=False)
        tb = wait("lot offered to buyer B", lambda: buyer_thread(lot, buyer_b), 5, kick=False)
        state.update(buyer_a_conv=ta["gc_conversationid"], buyer_b_conv=tb["gc_conversationid"])
        save(state)
        offers = send_all(kit, [ta["gc_conversationid"], tb["gc_conversationid"]], "lot offers to the buyers + thanks to the seller", m["createdon"], optional=[conv])
        if not all("42,230" in (d["gc_text"] or "") for d in offers if d["gc_toaddress"] in (buyer_a, buyer_b)):
            log("   ⚠ a lot offer does not show USD 42,230 (41,000 + 3%)")
        if any(x in (d["gc_text"] or "") for x in ("41,000", "41000", "Xinmo", "Wang") for d in offers if d["gc_toaddress"] in (buyer_a, buyer_b)):
            raise SystemExit("✗ a buyer offer reveals the seller or the seller's price")

        # 2. bids: A accepts for 10 MT (first), B offers above our price for 15 MT
        ta = get(f"gc_conversations({ta['gc_conversationid']})?$select=gc_conversationid,gc_gmailthreadid")
        tb = get(f"gc_conversations({tb['gc_conversationid']})?$select=gc_conversationid,gc_gmailthreadid")
        a_mail, _ = insert(kit, mime(f"Anita Rao <{buyer_a}>", desk, "Re: Offer: Ferro Molybdenum", "Dear Sir,\n\nWe confirm your offer at your price for 10 MT. "
                                                                                                    "Please send the contract.\n\nAnita Rao\nPurchase"), ta["gc_gmailthreadid"])
        log("2a. buyer A accepted our price for 10 MT")
        ingested(a_mail, "buyer A's acceptance")
        wait("buyer A's bid recorded", lambda: [d for d in get(f"gc_deals?$select=gc_buyerprice&$filter=_gc_sellerlot_value eq {lot} and gc_buyerprice gt 0")], 10)
        b_mail, _ = insert(kit, mime(f"Ben Koh <{buyer_b}>", desk, "Re: Offer: Ferro Molybdenum", "Hi,\n\nWe need 15 MT and can offer USD 43,000 per MT CIF Nhava Sheva "
                                                                                                  "if you can confirm today.\n\nBen"), tb["gc_gmailthreadid"])
        log("2b. buyer B offered USD 43,000 for 15 MT")
        since_b = ingested(b_mail, "buyer B's offer")
        bids = wait("both bids recorded", lambda: (lambda d: d if len(d) == 2 else None)(
            get(f"gc_deals?$select=gc_dealid,gc_buyerprice,gc_quantity,gc_bidon,_gc_buyer_value&$filter=_gc_sellerlot_value eq {lot} and gc_buyerprice gt 0")), 10)
        for d in bids:
            log(f"   bid {d['gc_buyerprice']} for {d['gc_quantity']} at {d['gc_bidon']}")
        send_all(kit, [tb["gc_conversationid"]], "'offers close' reply to buyer B", since_b, optional=[ta["gc_conversationid"]])

        # 3. close the lot now
        dv.patch(f"gc_sellerlots({lot})", {"gc_biddeadline": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")})
        log("3. bid deadline moved to now; running Desk timers")
        run_flow("DealOS | Desk timers", "Every_15_minutes")
        status = wait("lot closed", lambda: (lambda l: l if l.get("gc_status") != LOT_OPEN else None)(get(f"gc_sellerlots({lot})?$select=gc_status,gc_allocated,gc_outcome")), 6, kick=False)
        log(f"   lot status {status.get('gc_status@OData.Community.Display.V1.FormattedValue')}, allocated {status.get('gc_allocated')}")
        log("   outcome " + (status.get("gc_outcome") or "")[:600])
        if status.get("gc_status") != LOT_ALLOCATED:
            raise SystemExit("✗ the lot was not allocated")
        deal_b = next(d for d in bids if d["gc_buyerprice"] == 43000)["gc_dealid"]
        deal_a = next(d for d in bids if d["gc_dealid"] != deal_b)["gc_dealid"]
        if get(f"gc_deals({deal_a})?$select=gc_stage")["gc_stage"] != CANCELLED:
            raise SystemExit("✗ buyer A's deal (did not fit) was not closed")
        send_all(kit, [tb["gc_conversationid"], ta["gc_conversationid"], conv], "win to B, 'not this time' to A, purchase to the seller", since_b)

        # 4. a person confirms the winning deal
        approve("Confirm deal", deal_b, 5)
        wait("winning deal Terms Agreed or later", lambda: get(f"gc_deals({deal_b})?$select=gc_stage")["gc_stage"] >= B + 2, 8, kick=False)
        state.update(deal_b=deal_b, deal_a=deal_a, finished=datetime.now().isoformat())
        save(state)
    off()
    log("LOT END TO END PASSED")
    status_cmd()


def run_single():
    """One buyer: the lot is offered to the only matching buyer; their acceptance closes offers at once (nobody else to wait for)."""
    tag = "[AGENT-TEST] LOT1 " + datetime.now().strftime("%m%d-%H%M")
    seller, buyer = "olga.ivanova@uralw-tungsten.example", "ravi.menon@gamma-tools-buyer.example"
    with Kit(keep_on=True) as kit:
        me = kit.call("profile")["emailAddress"].lower()
        user, domain = me.split("@")
        desk = f"{user}+dealos@{domain}"
        log(f"mailbox {me}; run {tag} (single buyer)")
        leads_csv = os.path.join(ROOT, "build", "lot1_e2e_leads.csv")
        with open(leads_csv, "w", newline="") as f:
            csv.writer(f).writerows([["DATES", "HS CODE", "PRODUCT DESCRIPTION", "SUPPLIER", "PURCHASER", "COUNTRY OF ORIGIN", "PURCHASING COUNTRY", "WEIGHT(KG)", "PURCHASER EMAIL"],
                                     ["2026-08-20", "72028000", "FERRO TUNGSTEN FEW80 LUMPS", "X", "[AGENT-TEST] Gamma Tools Test Pvt Ltd", "RUSSIA", "INDIA", "5000", buyer]])
        print(subprocess.run([sys.executable, os.path.join(ROOT, "tools", "import_leads.py"), leads_csv, "--source", "[AGENT-TEST] LOT1 E2E", "--sides", "buyer"],
                             capture_output=True, text=True).stdout)
        gid, _ = insert(kit, mime(f"Olga Ivanova <{seller}>", desk, f"{tag} Offer: Ferro Tungsten FeW80 5 MT",
                                  "Dear Sir,\n\nWe can offer from stock 5 MT Ferro Tungsten FeW80 (W 80% min), lumps 10-50 mm, origin Russia.\n"
                                  "Price: USD 38,500 per MT CIF Nhava Sheva. Payment: LC at sight. Validity: 10 days.\n\nBest regards,\nOlga Ivanova"))
        log("1. seller offered 5 MT FeW80 at USD 38,500 CIF")
        m = wait("seller email triaged", lambda: (lambda x: x if x and x.get("gc_triage") else None)(message(gid)))
        conv = m["_gc_conversation_value"]
        lot = wait("lot saved and offered", lambda: get(f"gc_conversations({conv})?$select=_gc_sellerlot_value").get("_gc_sellerlot_value"), 12)
        deals = get(f"gc_deals?$select=gc_dealid&$filter=_gc_sellerlot_value eq {lot}")
        log(f"   lot offered to {len(deals)} buyer(s)")
        if len(deals) != 1:
            raise SystemExit(f"✗ expected exactly one buyer for FeW80, got {len(deals)}")
        t = wait("lot offered to the buyer", lambda: buyer_thread(lot, buyer), 5, kick=False)
        send_all(kit, [t["gc_conversationid"]], "lot offer to the buyer", m["createdon"], optional=[conv])
        t = get(f"gc_conversations({t['gc_conversationid']})?$select=gc_conversationid,gc_gmailthreadid")
        a, _ = insert(kit, mime(f"Ravi Menon <{buyer}>", desk, "Re: Offer: Ferro Tungsten", "Dear Sir,\n\nWe accept your offer for the full 5 MT. Please send the contract.\n\nRavi Menon"),
                      t["gc_gmailthreadid"])
        log("2. the only buyer accepted for 5 MT")
        ingested(a, "buyer's acceptance")
        wait("offers closed early (deadline moved to now)", lambda: (lambda l: l if l.get("gc_biddeadline") and l["gc_biddeadline"] <= datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ") else None)(
            get(f"gc_sellerlots({lot})?$select=gc_biddeadline")), 10)
        run_flow("DealOS | Desk timers", "Every_15_minutes")
        st = wait("lot allocated", lambda: (lambda l: l if l.get("gc_status") == LOT_ALLOCATED else None)(get(f"gc_sellerlots({lot})?$select=gc_status,gc_allocated")), 6, kick=False)
        log(f"   allocated {st.get('gc_allocated')}")
        approve("Confirm deal", deals[0]["gc_dealid"], 5)
        wait("deal Terms Agreed or later", lambda: get(f"gc_deals({deals[0]['gc_dealid']})?$select=gc_stage")["gc_stage"] >= B + 2, 8, kick=False)
    off()
    log("SINGLE-BUYER LOT END TO END PASSED")


def one_buyer_lead(tag_source, csv_name, product, origin, buyer, buyer_name):
    leads_csv = os.path.join(ROOT, "build", csv_name)
    with open(leads_csv, "w", newline="") as f:
        csv.writer(f).writerows([["DATES", "HS CODE", "PRODUCT DESCRIPTION", "SUPPLIER", "PURCHASER", "COUNTRY OF ORIGIN", "PURCHASING COUNTRY", "WEIGHT(KG)", "PURCHASER EMAIL"],
                                 ["2026-09-01", "81052000", product, "X", buyer_name, origin, "INDIA", "8000", buyer]])
    print(subprocess.run([sys.executable, os.path.join(ROOT, "tools", "import_leads.py"), leads_csv, "--source", tag_source, "--sides", "buyer"],
                         capture_output=True, text=True).stdout)


def seller_draft_with(kit, conv, figure, label, since, optional=()):
    """Sends the seller-thread draft (and any optional ones) once ready; fails unless it shows `figure` (our bid) or if it names the buyer or their price."""
    ds = send_all(kit, [conv], label, since, optional=optional)
    text = " ".join(d["gc_text"] or "" for d in ds if d["_gc_conversation_value"] == conv)
    if figure not in text:
        raise SystemExit(f"✗ the draft to the seller does not show {figure}")
    if any(x in text for x in ("Sharma", "Delta", "Kiran", "Epsilon", "32,960", "32,445", "17,000")):
        raise SystemExit("✗ the draft to the seller reveals the buyer or the buyer's price")
    return ds


def run_open():
    """Seller-first: open-ended lot, buyer counter → seller, seller counter → buyer, buyer accepts, seller accepts our bid."""
    tag = "[AGENT-TEST] LOTO " + datetime.now().strftime("%m%d-%H%M")
    seller, buyer = "chen.yu@hengrui-cobalt.example", "neha.sharma@delta-batteries-buyer.example"
    with Kit(keep_on=True) as kit:
        me = kit.call("profile")["emailAddress"].lower()
        user, domain = me.split("@")
        desk = f"{user}+dealos@{domain}"
        log(f"mailbox {me}; run {tag} (open-ended, seller first)")
        one_buyer_lead("[AGENT-TEST] LOTO E2E", "loto_e2e_leads.csv", "COBALT METAL CATHODE 99.8% MIN", "CONGO", buyer, "[AGENT-TEST] Delta Batteries Test Pvt Ltd")

        gid, seller_gt = insert(kit, mime(f"Chen Yu <{seller}>", desk, f"{tag} Looking for buyers: Cobalt metal 8 MT",
                                          "Dear Sir,\n\nWe are looking for buyers for the following material from stock:\n\nProduct: Cobalt metal cathode, Co 99.8% min\n"
                                          "Quantity: 8 MT\nPrice: USD 32,000 per MT CIF Nhava Sheva\nOrigin: DR Congo\nPayment: LC at sight\n"
                                          "The offer stays open until sold.\n\nPlease send us your buyers' offers.\n\nBest regards,\nChen Yu\n[AGENT-TEST] Hengrui Cobalt Test Co"))
        log("1. seller looks for buyers: 8 MT cobalt at USD 32,000 CIF, open until sold")
        m = wait("seller email triaged", lambda: (lambda x: x if x and x.get("gc_triage") else None)(message(gid)))
        log(f"   triage: {TRIAGE.get(m['gc_triage'])}, score {m.get('gc_triagescore')}")
        conv = m["_gc_conversation_value"]
        lot = wait("lot saved", lambda: get(f"gc_conversations({conv})?$select=_gc_sellerlot_value").get("_gc_sellerlot_value"), 12)
        info = get(f"gc_sellerlots({lot})?$select=gc_name,gc_biddeadline,gc_window")
        log(f"   lot {info['gc_name']}: window {info.get('gc_window')}, deadline {info.get('gc_biddeadline')}")
        if info.get("gc_biddeadline"):
            raise SystemExit("✗ 'open until sold' did not make the lot open-ended")
        state = {"tag": tag, "lot": lot, "seller_conv": conv, "scenario": "open"}
        save(state)
        t = wait("lot offered to the buyer", lambda: buyer_thread(lot, buyer), 5, kick=False)
        offers = send_all(kit, [t["gc_conversationid"]], "lot offer to the buyer + thanks to the seller", m["createdon"], optional=[conv])
        if not any("32,960" in (d["gc_text"] or "") for d in offers if d["gc_toaddress"] == buyer):
            raise SystemExit("✗ the buyer offer does not show USD 32,960 (32,000 + 3%)")
        t = get(f"gc_conversations({t['gc_conversationid']})?$select=gc_conversationid,gc_gmailthreadid")
        bconv = t["gc_conversationid"]

        b1, _ = insert(kit, mime(f"Neha Sharma <{buyer}>", desk, "Re: Offer: Cobalt", "Dear Sir,\n\nThank you. We can offer USD 32,000 per MT for the 8 MT.\n\nNeha Sharma\nDelta Batteries"),
                       t["gc_gmailthreadid"])
        log("2. buyer counters at USD 32,000")
        since = ingested(b1, "buyer's counter")
        seller_draft_with(kit, conv, "31,067.96", "our bid to the seller (31,067.96) + note to the buyer", since, optional=[bconv])

        s1, _ = insert(kit, mime(f"Chen Yu <{seller}>", desk, "Re: Looking for buyers: Cobalt metal 8 MT",
                                 "Dear Sir,\n\nThat is too low. The best we can do is USD 31,500 per MT CIF Nhava Sheva.\n\nChen Yu"), seller_gt)
        log("3. seller counters at USD 31,500")
        since = ingested(s1, "seller's counter")
        ds = send_all(kit, [bconv], "our new price to the buyer (32,445)", since, optional=[conv])
        if not any("32,445" in (d["gc_text"] or "") for d in ds if d["gc_toaddress"] == buyer):
            raise SystemExit("✗ the seller's counter did not reach the buyer as USD 32,445")

        b2, _ = insert(kit, mime(f"Neha Sharma <{buyer}>", desk, "Re: Offer: Cobalt", "Dear Sir,\n\nAgreed, we accept USD 32,445 per MT for 8 MT.\n\nNeha"), t["gc_gmailthreadid"])
        log("4. buyer accepts USD 32,445")
        since = ingested(b2, "buyer's acceptance")
        seller_draft_with(kit, conv, "31,500", "our bid to the seller (31,500)", since, optional=[bconv])

        s2, _ = insert(kit, mime(f"Chen Yu <{seller}>", desk, "Re: Looking for buyers: Cobalt metal 8 MT",
                                 "Dear Sir,\n\nWe accept your bid of USD 31,500 per MT for 8 MT. Please send the contract.\n\nChen Yu"), seller_gt)
        log("5. seller accepts our bid of USD 31,500")
        since = ingested(s2, "seller's acceptance")
        st = wait("lot allocated", lambda: (lambda l: l if l.get("gc_status") == LOT_ALLOCATED else None)(get(f"gc_sellerlots({lot})?$select=gc_status,gc_allocated")), 10)
        log(f"   allocated {st.get('gc_allocated')}")
        send_all(kit, [bconv, conv], "confirmations to the buyer and the seller", since)
        deal = get(f"gc_deals?$select=gc_dealid&$filter=_gc_sellerlot_value eq {lot}")[0]["gc_dealid"]
        approve("Confirm deal", deal, 5)
        d = wait("deal Terms Agreed or later", lambda: (lambda x: x if x["gc_stage"] >= B + 2 else None)(get(f"gc_deals({deal})?$select=gc_stage,gc_price,gc_buyerprice")), 8, kick=False)
        log(f"   deal: seller price {d.get('gc_price')}, buyer price {d.get('gc_buyerprice')}")
        if round(float(d.get("gc_price") or 0), 2) != 31500 or round(float(d.get("gc_buyerprice") or 0), 2) != 32445:
            raise SystemExit("✗ the deal prices are not seller 31,500 / buyer 32,445")
        state.update(deal=deal, finished=datetime.now().isoformat())
        save(state)
    off()
    log("OPEN-ENDED LOT (SELLER FIRST) END TO END PASSED")


def run_below():
    """Timed lot from the seller's window (48 h); the only bid is below the seller's price → at the deadline it goes to the seller, lot open-ended."""
    tag = "[AGENT-TEST] LOTB " + datetime.now().strftime("%m%d-%H%M")
    seller, buyer = "ivan.petrov@sibir-nickel.example", "kiran.das@epsilon-alloys-buyer.example"
    with Kit(keep_on=True) as kit:
        me = kit.call("profile")["emailAddress"].lower()
        user, domain = me.split("@")
        desk = f"{user}+dealos@{domain}"
        log(f"mailbox {me}; run {tag} (timed, best bid below the seller's price)")
        one_buyer_lead("[AGENT-TEST] LOTB E2E", "lotb_e2e_leads.csv", "NICKEL CATHODE 99.8% MIN", "RUSSIA", buyer, "[AGENT-TEST] Epsilon Alloys Test Ltd")
        gid, _ = insert(kit, mime(f"Ivan Petrov <{seller}>", desk, f"{tag} Offer: Nickel cathode 10 MT",
                                  "Dear Sir,\n\nWe offer 10 MT nickel cathode, Ni 99.8% min, origin Russia, at USD 17,200 per MT CIF Nhava Sheva. "
                                  "Payment LC at sight. The offer is open for 48 hours.\n\nBest regards,\nIvan Petrov"))
        log("1. seller offers 10 MT nickel at USD 17,200, open for 48 hours")
        m = wait("seller email triaged", lambda: (lambda x: x if x and x.get("gc_triage") else None)(message(gid)))
        conv = m["_gc_conversation_value"]
        lot = wait("lot saved", lambda: get(f"gc_conversations({conv})?$select=_gc_sellerlot_value").get("_gc_sellerlot_value"), 12)
        info = get(f"gc_sellerlots({lot})?$select=gc_biddeadline,gc_window,createdon")
        hours = (datetime.fromisoformat(info["gc_biddeadline"].replace("Z", "+00:00")) - datetime.fromisoformat(info["createdon"].replace("Z", "+00:00"))).total_seconds() / 3600
        log(f"   window {info.get('gc_window')}, deadline in {hours:.1f} h")
        if not 47 <= hours <= 49:
            raise SystemExit("✗ the seller's 48 hours did not set a 48 h window")
        save({"tag": tag, "lot": lot, "seller_conv": conv, "scenario": "below"})
        t = wait("lot offered to the buyer", lambda: buyer_thread(lot, buyer), 5, kick=False)
        send_all(kit, [t["gc_conversationid"]], "lot offer to the buyer", m["createdon"], optional=[conv])
        t = get(f"gc_conversations({t['gc_conversationid']})?$select=gc_conversationid,gc_gmailthreadid")
        b, _ = insert(kit, mime(f"Kiran Das <{buyer}>", desk, "Re: Offer: Nickel", "Dear Sir,\n\nWe can pay USD 17,000 per MT for 10 MT.\n\nKiran Das"), t["gc_gmailthreadid"])
        log("2. the only buyer bids USD 17,000 (below 17,716 = seller price + 3%)")
        since = ingested(b, "buyer's bid")
        wait("bid recorded", lambda: get(f"gc_deals?$select=gc_dealid&$filter=_gc_sellerlot_value eq {lot} and gc_buyerprice gt 0"), 10)
        lot_now = get(f"gc_sellerlots({lot})?$select=gc_biddeadline")
        if not lot_now.get("gc_biddeadline") or lot_now["gc_biddeadline"] > datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"):
            dv.patch(f"gc_sellerlots({lot})", {"gc_biddeadline": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")})
        log("3. deadline reached; running Desk timers")
        run_flow("DealOS | Desk timers", "Every_15_minutes")
        st = wait("lot open-ended with the best bid put to the seller",
                  lambda: (lambda l: l if l.get("gc_status") == LOT_OPEN and not l.get("gc_biddeadline") else None)(get(f"gc_sellerlots({lot})?$select=gc_status,gc_biddeadline,gc_window")), 6, kick=False)
        log(f"   lot {st.get('gc_window')}")
        seller_draft_with(kit, conv, "16,504.85", "best bid to the seller (16,504.85)", since, optional=[t["gc_conversationid"]])
    off()
    log("TIMED LOT → BEST BID TO SELLER PASSED")


def run_coal():
    """The owner's real coal case: a seller offers Tanzanian thermal coal FOB Mtwara (valid 48 hours); our buyer asks the price for
    Ennore port, then for our company profile and the quality report. The desk must not invent a price: a short 'let me confirm'
    and a task for a person each time. Every draft must read like a trader."""
    stamp = datetime.now().strftime("%m%d-%H%M")
    tag = "[AGENT-TEST] COAL " + stamp
    seller, buyer = "rashidi.mollel@mtwara-coal.example", f"s.kumar.{stamp.replace('-', '')}@ennore-coal-buyer.example"
    with Kit(keep_on=True) as kit:
        me = kit.call("profile")["emailAddress"].lower()
        user, domain = me.split("@")
        desk = f"{user}+dealos@{domain}"
        log(f"mailbox {me}; run {tag} (coal: other port, profile and report)")
        leads_csv = os.path.join(ROOT, "build", "coal_e2e_leads.csv")
        with open(leads_csv, "w", newline="") as f:
            csv.writer(f).writerows([["DATES", "HS CODE", "PRODUCT DESCRIPTION", "SUPPLIER", "PURCHASER", "COUNTRY OF ORIGIN", "PURCHASING COUNTRY", "WEIGHT(KG)", "PURCHASER EMAIL"],
                                     ["2026-09-12", "27011920", "STEAM COAL (THERMAL COAL) GCV 5400 KCAL GAR IN BULK", "X", f"[AGENT-TEST] Ennore Coal Traders {stamp}", "TANZANIA", "INDIA", "50000000", buyer]])
        print(subprocess.run([sys.executable, os.path.join(ROOT, "tools", "import_leads.py"), leads_csv, "--source", "[AGENT-TEST] COAL E2E", "--sides", "buyer"],
                             capture_output=True, text=True).stdout)
        gid, seller_gt = insert(kit, mime(f"Rashidi Mollel <{seller}>", desk, f"{tag} Tanzanian thermal coal, Mtwara",
                                          "Tanzanian thermal coal available for supply from Mtwara Port.\n\nIndicative specs:\n- GCV: ~5,400 kcal/kg GAR\n- Ash: ~20.6%\n"
                                          "- Sulphur: ~1.16%\n- Moisture: ~10.3%\n- Quantity: 30,000 MT bulk vessel\n- Independent SGS testing available\n\n"
                                          "Price: USD 98 per MT FOB Mtwara. Offer valid 48 hours.\n\nRecent 33,700 MT vessel loading from Mtwara provides shipment track record.\n\nRashidi"))
        log("1. seller offers 30,000 MT Tanzanian thermal coal at USD 98 FOB Mtwara, valid 48 hours")
        m = wait("seller email triaged", lambda: (lambda x: x if x and x.get("gc_triage") else None)(message(gid)))
        conv = m["_gc_conversation_value"]
        lot = wait("lot saved", lambda: get(f"gc_conversations({conv})?$select=_gc_sellerlot_value").get("_gc_sellerlot_value"), 12)
        info = get(f"gc_sellerlots({lot})?$select=gc_window,gc_price,gc_quantity")
        log(f"   lot window {info.get('gc_window')}, price {info.get('gc_price')}, quantity {info.get('gc_quantity')}")
        save({"tag": tag, "lot": lot, "seller_conv": conv, "scenario": "coal"})
        t = wait("lot offered to the buyer", lambda: buyer_thread(lot, buyer), 5, kick=False)
        ds = send_all(kit, [t["gc_conversationid"]], "coal offer to the buyer + note to the seller", m["createdon"], optional=[conv])
        if not any("100.94" in (d["gc_text"] or "") for d in ds if d["gc_toaddress"] == buyer):
            raise SystemExit("✗ the coal offer does not show USD 100.94 (98 + 3%)")
        t = get(f"gc_conversations({t['gc_conversationid']})?$select=gc_conversationid,gc_gmailthreadid")
        tasks_before = len(get("gc_reviewtasks?$select=gc_reviewtaskid&$filter=gc_status eq " + str(B)))

        q, _ = insert(kit, mime(f"S Kumar <{buyer}>", desk, "Re: Offer: Tanzanian thermal coal", "Let me know price for Ennore port"), t["gc_gmailthreadid"])
        log("2. buyer: 'Let me know price for Ennore port'")
        since = ingested(q, "buyer's question")
        ds = send_all(kit, [t["gc_conversationid"]], "holding reply to the buyer", since)
        text = " ".join(d["gc_text"] or "" for d in ds)
        import re as _re
        if _re.search(r"(USD|\$)\s?\d", text.split("Best regards,")[0]):
            raise SystemExit("✗ the reply to 'price for Ennore' quotes a price the desk does not have")
        wait("a task for a person to get the Ennore price", lambda: len(get("gc_reviewtasks?$select=gc_reviewtaskid&$filter=gc_status eq " + str(B))) > tasks_before, 5, kick=False)

        tasks_before = len(get("gc_reviewtasks?$select=gc_reviewtaskid&$filter=gc_status eq " + str(B)))
        q, _ = insert(kit, mime(f"S Kumar <{buyer}>", desk, "Re: Offer: Tanzanian thermal coal", "Pls send your company profile and quality report"), t["gc_gmailthreadid"])
        log("3. buyer: 'Pls send your company profile and quality report'")
        since = ingested(q, "buyer's request")
        send_all(kit, [t["gc_conversationid"]], "reply: profile and report will follow", since)
        wait("a task for a person to send the profile / masked report", lambda: len(get("gc_reviewtasks?$select=gc_reviewtaskid&$filter=gc_status eq " + str(B))) > tasks_before, 5, kick=False)
    off()
    log("COAL (OTHER PORT, PROFILE, REPORT) END TO END PASSED")


def status_cmd():
    if not os.path.exists(STATE):
        sys.exit("no run yet")
    st = json.load(open(STATE))
    print(json.dumps(st, indent=2))
    if st.get("lot"):
        l = get(f"gc_sellerlots({st['lot']})?$select=gc_name,gc_status,gc_allocated,gc_outcome")
        print("lot:", l.get("gc_name"), "|", l.get("gc_status@OData.Community.Display.V1.FormattedValue"), "| allocated", l.get("gc_allocated"))


if __name__ == "__main__":
    {"run": run, "single": run_single, "open": run_open, "below": run_below, "coal": run_coal, "status": status_cmd}.get(sys.argv[1] if len(sys.argv) > 1 else "", lambda: print(__doc__))()

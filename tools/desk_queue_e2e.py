"""End-to-end test of "sellers take turns" (buyer first) on the real Gmail mailbox and Dev, by email only.

  1. buyer asks for 5 MT niobium pentoxide              → requirement, enquiries to three seller leads
  2. seller B quotes USD 30,000 first                     → B is the ACTIVE seller: our offer USD 30,900 (+3%) drafted to the buyer
  3. seller A quotes USD 29,500 later (cheaper)           → A is QUEUED: only a thank-you to A, nothing new to the buyer
  4. seller C is not interested                           → ignored: nothing changes
  5. buyer turns the offer down but still needs material  → B's deal closes (B gets a polite note), A comes up:
                                                            "another offer" at USD 30,385 (29,500 + 3%) drafted to the buyer
  6. buyer accepts                                        → Confirm deal on A's deal → approved → Terms Agreed

Usage: python3 tools/desk_queue_e2e.py run | status
Records are marked [AGENT-TEST]; counterparties use .example addresses (drafts to them bounce, which is expected).
"""
import csv, json, os, subprocess, sys
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402
from gmail_kit import Kit, off  # noqa: E402
from desk_e2e import B, TRIAGE, get, log, wait, mime, message, send_all, insert, ingested, approve, drafts  # noqa: E402

ROOT = dv.ROOT
STATE = os.path.join(ROOT, "build", "desk_queue_e2e.json")
CANCELLED = B + 12


def save(state):
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    json.dump(state, open(STATE, "w"), indent=2, default=str)


def active(req):
    return get(f"gc_buyerrequirements({req})?$select=_gc_activedeal_value").get("_gc_activedeal_value")


def run():
    stamp = datetime.now().strftime("%m%d-%H%M")
    tag = "[AGENT-TEST] QUEUE " + stamp
    # A fresh buyer address per run: the same buyer with an open niobium requirement would (rightly) be treated as a follow-up.
    buyer = f"meera.iyer.{stamp.replace('-', '')}@omega-superalloys-buyer.example"
    sa, sb, sc = "pedro.costa@alpha-niobium.example", "joao.silva@beta-niobium.example", "ana.lima@gamma-niobium.example"
    state = {"tag": tag, "started": datetime.now().isoformat()}
    with Kit(keep_on=True) as kit:
        me = kit.call("profile")["emailAddress"].lower()
        user, domain = me.split("@")
        desk = f"{user}+dealos@{domain}"
        log(f"mailbox {me}; run {tag} (sellers take turns)")

        leads_csv = os.path.join(ROOT, "build", "queue_e2e_leads.csv")
        with open(leads_csv, "w", newline="") as f:
            csv.writer(f).writerows([
                ["DATES", "HS CODE", "PRODUCT DESCRIPTION", "SUPPLIER", "PURCHASER", "COUNTRY OF ORIGIN", "PURCHASING COUNTRY", "WEIGHT(KG)", "SUPPLIER EMAIL", "SUPPLIER CONTACT"],
                ["2026-09-02", "28259090", "NIOBIUM PENTOXIDE NB2O5 99.5% MIN", "[AGENT-TEST] Alpha Niobium Test SA", "X", "BRAZIL", "INDIA", "6000", sa, "Pedro Costa"],
                ["2026-08-20", "28259090", "NIOBIUM PENTOXIDE NB2O5 99.9%", "[AGENT-TEST] Beta Niobium Test Ltda", "X", "BRAZIL", "INDIA", "5000", sb, "Joao Silva"],
                ["2026-07-11", "28259090", "NIOBIUM PENTOXIDE POWDER", "[AGENT-TEST] Gamma Niobium Test SA", "X", "BRAZIL", "INDIA", "4000", sc, "Ana Lima"],
            ])
        print(subprocess.run([sys.executable, os.path.join(ROOT, "tools", "import_leads.py"), leads_csv, "--source", "[AGENT-TEST] QUEUE E2E", "--sides", "seller"],
                             capture_output=True, text=True).stdout)

        # 1. the buyer's requirement
        gid, buyer_gt = insert(kit, mime(f"Meera Iyer <{buyer}>", desk, f"{tag} Requirement: Niobium Pentoxide 5 MT",
                                         "Dear Sir,\n\nWe need 5 MT Niobium Pentoxide (Nb2O5), 99.5% min, powder.\nDelivery: CIF Nhava Sheva, India, within 6 weeks.\n"
                                         "Payment: LC at sight.\n\nPlease send your best offer with origin and COA.\n\nMeera Iyer\nProcurement\n[AGENT-TEST] Omega Superalloys Test Pvt Ltd"))
        log("1. buyer asks for 5 MT niobium pentoxide")
        m = wait("buyer email triaged", lambda: (lambda x: x if x and x.get("gc_triage") else None)(message(gid)))
        log(f"   triage: {TRIAGE.get(m['gc_triage'])}, score {m.get('gc_triagescore')}")
        conv = m["_gc_conversation_value"]
        req = wait("requirement saved and sourcing started", lambda: (lambda c: c.get("_gc_requirement_value") if c.get("_gc_requirement_value") and get(
            f"gc_rfqinvites?$select=gc_rfqinviteid&$filter=_gc_requirement_value eq {c['_gc_requirement_value']}") else None)(
            get(f"gc_conversations({conv})?$select=_gc_requirement_value")))
        sellers = get(f"gc_conversations?$select=gc_conversationid,gc_name,_gc_deal_value,gc_gmailthreadid&$filter=_gc_requirement_value eq {req} and gc_side eq {B + 1}")
        by = {k: next(s for s in sellers if k in s["gc_name"]) for k in ("Alpha", "Beta", "Gamma")}
        state.update(requirement=req, buyer_conv=conv, deals={k: v["_gc_deal_value"] for k, v in by.items()})
        save(state)
        log(f"   {len(sellers)} seller(s) contacted: " + ", ".join(s["gc_name"] for s in sellers))
        send_all(kit, [conv] + [s["gc_conversationid"] for s in sellers], "acknowledgement + enquiries", m["createdon"])
        th = {k: get(f"gc_conversations({v['gc_conversationid']})?$select=gc_conversationid,gc_gmailthreadid") for k, v in by.items()}

        # 2. Beta answers first
        q, _ = insert(kit, mime(f"Joao Silva <{sb}>", desk, "Re: Enquiry: Niobium Pentoxide",
                                "Dear Sir,\n\nWe can offer 5 MT Niobium Pentoxide 99.6%, origin Brazil, at USD 30,000 per MT CIF Nhava Sheva. "
                                "Shipment 4 weeks from LC. Payment LC at sight.\n\nJoao Silva"), th["Beta"]["gc_gmailthreadid"])
        log("2. seller B quotes USD 30,000 (first to answer)")
        since = ingested(q, "seller B's quote")
        ds = send_all(kit, [conv], "our offer to the buyer (30,900)", since, optional=[th["Beta"]["gc_conversationid"]])
        if not any("30,900" in (d["gc_text"] or "") for d in ds if d["gc_toaddress"] == buyer):
            raise SystemExit("✗ the buyer offer does not show USD 30,900 (B's 30,000 + 3%)")
        if active(req) != state["deals"]["Beta"]:
            raise SystemExit("✗ seller B (first to quote) is not the active seller")

        # 3. Alpha answers later, cheaper: queued
        q, _ = insert(kit, mime(f"Pedro Costa <{sa}>", desk, "Re: Enquiry: Niobium Pentoxide",
                                "Dear Sir,\n\nOur offer: 5 MT Niobium Pentoxide 99.5% min, origin Brazil, USD 29,500 per MT CIF Nhava Sheva, LC at sight.\n\nPedro Costa"),
                      th["Alpha"]["gc_gmailthreadid"])
        log("3. seller A quotes USD 29,500 (later, cheaper)")
        since = ingested(q, "seller A's quote")
        send_all(kit, [th["Alpha"]["gc_conversationid"]], "thank-you to seller A (queued)", since)
        if drafts(conv, since=since):
            raise SystemExit("✗ the queued seller's quote produced a draft to the buyer")
        if active(req) != state["deals"]["Beta"]:
            raise SystemExit("✗ the active seller changed when a later seller quoted")
        log("   A is queued; nothing new went to the buyer")

        # 4. Gamma is not interested
        q, _ = insert(kit, mime(f"Ana Lima <{sc}>", desk, "Re: Enquiry: Niobium Pentoxide", "Sorry, we have no material available at present.\n\nAna"),
                      th["Gamma"]["gc_gmailthreadid"])
        log("4. seller C is not interested")
        ingested(q, "seller C's decline")
        wait("seller C's decline recorded", lambda: (lambda i: i if i and i[0].get("gc_status") == B + 3 else None)(
            get(f"gc_rfqinvites?$select=gc_status&$filter=_gc_requirement_value eq {req} and _gc_deal_value eq {state['deals']['Gamma']}")), 10)
        if active(req) != state["deals"]["Beta"]:
            raise SystemExit("✗ a decline from a seller who was not active changed the active seller")

        # 5. the buyer turns B's offer down, still looking
        q, _ = insert(kit, mime(f"Meera Iyer <{buyer}>", desk, f"Re: {tag} Requirement: Niobium Pentoxide 5 MT",
                                "Dear Sir,\n\nThank you, but this offer does not work for us, it is too expensive. We still need the material, so please let us know "
                                "if you have another option.\n\nMeera"), buyer_gt)
        log("5. buyer turns the offer down, still looking")
        since = ingested(q, "buyer's rejection")
        wait("A is now the active seller", lambda: active(req) == state["deals"]["Alpha"], 10)
        ds = send_all(kit, [conv, th["Beta"]["gc_conversationid"]], "A's offer to the buyer (30,385) + note to B", since)
        if not any("30,385" in (d["gc_text"] or "") for d in ds if d["gc_toaddress"] == buyer):
            raise SystemExit("✗ the next seller's offer to the buyer does not show USD 30,385 (A's 29,500 + 3%)")
        if get(f"gc_deals({state['deals']['Beta']})?$select=gc_stage")["gc_stage"] != CANCELLED:
            raise SystemExit("✗ seller B's deal was not closed")
        if any(x in (d["gc_text"] or "") for x in ("Pedro", "Alpha", "Joao", "Beta", "29,500", "30,000") for d in ds if d["gc_toaddress"] == buyer):
            raise SystemExit("✗ the buyer draft reveals a seller or a seller price")

        # 6. the buyer accepts A's offer
        q, _ = insert(kit, mime(f"Meera Iyer <{buyer}>", desk, f"Re: {tag} Requirement: Niobium Pentoxide 5 MT",
                                "Dear Sir,\n\nUSD 30,385 per MT works for us. We confirm 5 MT, please send the contract.\n\nMeera"), buyer_gt)
        log("6. buyer accepts USD 30,385")
        ingested(q, "buyer's acceptance")
        approve("Confirm deal", state["deals"]["Alpha"], 8)
        d = wait("A's deal Terms Agreed or later", lambda: (lambda x: x if x["gc_stage"] >= B + 2 else None)(
            get(f"gc_deals({state['deals']['Alpha']})?$select=gc_stage,gc_price,gc_buyerprice")), 8, kick=False)
        log(f"   deal: seller price {d.get('gc_price')}, buyer price {d.get('gc_buyerprice')}")
        state.update(finished=datetime.now().isoformat())
        save(state)
    off()
    log("SELLERS TAKE TURNS END TO END PASSED")


def status():
    if not os.path.exists(STATE):
        sys.exit("no run yet")
    st = json.load(open(STATE))
    print(json.dumps(st, indent=2))
    if st.get("requirement"):
        print("active seller deal:", active(st["requirement"]))


if __name__ == "__main__":
    {"run": run, "status": status}.get(sys.argv[1] if len(sys.argv) > 1 else "", lambda: print(__doc__))()

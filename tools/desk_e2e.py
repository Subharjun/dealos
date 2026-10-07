"""End-to-end test of the Email Desk on the real Gmail mailbox and Dev, by email only.

The test plays the buyer and two sellers (their mail is put straight into the inbox with the test kit, from .example addresses;
not +aliases: Gmail treats +aliases of the mailbox as the owner and files them as sent mail) and plays the desk person (sends the drafts from Gmail, approves the tasks, does the KYB check). Everything else is
the real system: Mailbox sync → Mail Triage → Trade desk → Desk drafts → offers → Confirm deal → compliance → contract → signed.

  1. buyer asks for 10-15 MT vanadium pentoxide          → requirement, sourcing from the test leads, enquiry drafts
  2. seller 1 quotes USD 9,850 CIF, seller 2 declines      → quote recorded, our price to the buyer drafted
  3. buyer proposes USD 10,000                            → our bid to seller 1 drafted (buyer never named)
  4. seller 1 accepts our bid                              → "Confirm deal" task → approved → Terms Agreed → contract
  5. buyer returns the signed contract                     → task → approved → contract Signed → deal Signed (no escrow)

Usage:
  python3 tools/desk_e2e.py run          # about 30-45 minutes (Mailbox sync runs every 3 minutes; Gemini free tier is slow)
  python3 tools/desk_e2e.py status       # where the latest run is
All records are marked [AGENT-TEST]; tools/mail_test.py cleanup does not remove deals (cancel them; full cleanup is at the end of the build).
"""
import base64, csv, json, os, subprocess, sys, time
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402
from gmail_kit import Kit, b64u, run_flow, off  # noqa: E402
from seed_test_data import pdf  # noqa: E402

ROOT = dv.ROOT
STATE = os.path.join(ROOT, "build", "desk_e2e.json")
B = 303300000
TRIAGE = {B: "Genuine", B + 1: "Review", B + 2: "Ignored"}
PENDING, SENT = B, B + 1
REVIEW_APPROVED = B + 1


def log(msg):
    print(datetime.now().strftime("%H:%M:%S"), msg, flush=True)


def save(state):
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    json.dump(state, open(STATE, "w"), indent=2)


def get(path):
    s, b = dv.get(path)
    if s >= 300:
        raise RuntimeError(f"GET {path}: {s} {json.dumps(b)[:300]}")
    return b.get("value", b)


def wait(what, check, minutes=12, kick=True):
    """Polls check() until it returns something truthy; starts Mailbox sync every ~90 s so mail is read without waiting 3 minutes."""
    log(f"… waiting: {what}")
    end, last_kick = time.time() + minutes * 60, 0
    while time.time() < end:
        if kick and time.time() - last_kick > 90:
            run_flow("DealOS | Mailbox sync", "Every_3_minutes")
            last_kick = time.time()
        result = check()
        if result:
            log(f"✓ {what}")
            return result
        time.sleep(20)
    failures = get("gc_flowfailures?$select=gc_flowname,gc_step,gc_error,createdon&$orderby=createdon desc&$top=5")
    for f in failures:
        log(f"   recent failure {f['createdon']} {f['gc_flowname']} / {f['gc_step']}: {f['gc_error'][:400]}")
    raise SystemExit(f"✗ timed out waiting for: {what}")


def mime(frm, to, subject, body, attachment=None, in_reply_to=None):
    date = datetime.now(timezone.utc).strftime("%a, %d %b %Y %H:%M:%S +0000")
    head = f"From: {frm}\r\nTo: {to}\r\nSubject: {subject}\r\nDate: {date}\r\nMessage-ID: <{int(time.time() * 1000)}.e2e@agent-test.example>\r\nMIME-Version: 1.0\r\n"
    if in_reply_to:
        head += f"In-Reply-To: {in_reply_to}\r\nReferences: {in_reply_to}\r\n"
    text = base64.b64encode(body.encode()).decode()
    if not attachment:
        return head + "Content-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" + text + "\r\n"
    name, data = attachment
    return (head + 'Content-Type: multipart/mixed; boundary="e2e"\r\n\r\n--e2e\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n'
            + text + f'\r\n--e2e\r\nContent-Type: application/pdf; name="{name}"\r\nContent-Disposition: attachment; filename="{name}"\r\n'
            "Content-Transfer-Encoding: base64\r\n\r\n" + base64.b64encode(data).decode() + "\r\n--e2e--\r\n")


def message(gmail_id):
    rows = get(f"gc_messages?$select=gc_messageid,_gc_conversation_value,gc_triage,gc_category,gc_triagescore&$filter=gc_externalid eq '{gmail_id}'")
    return rows[0] if rows else None


def drafts(conv_id, status=PENDING):
    return get(f"gc_messages?$select=gc_messageid,gc_gmaildraftid,gc_subject,gc_text,gc_toaddress&$filter=_gc_conversation_value eq {conv_id} "
               f"and gc_draftstatus eq {status}")


def ready_drafts(conv_ids):
    out = []
    for c in conv_ids:
        ds = drafts(c)
        if not ds or not all(d.get("gc_gmaildraftid") for d in ds):
            return None
        out += ds
    return out


def send_all(kit, conv_ids, label):
    ds = wait(f"Gmail drafts ready ({label})", lambda: ready_drafts(conv_ids), 10, kick=False)
    for d in ds:
        log(f"   ✉ draft to {d['gc_toaddress']} — {d['gc_subject']}\n" + "\n".join("        | " + l for l in (d["gc_text"] or "").splitlines()))
        kit.call("send_draft", id=d["gc_gmaildraftid"])
    log(f"   sent {len(ds)} draft(s) from Gmail (as the desk person)")
    wait(f"sent mail recorded ({label})", lambda: all(not drafts(c) for c in conv_ids) and all(drafts(c, SENT) for c in conv_ids), 10)


def insert(kit, raw, thread_id=""):
    r = kit.call("insert", raw=b64u(raw), thread_id=thread_id)
    return r["id"], r["threadId"]


def approve(task_name_prefix, deal_id=None, minutes=8):
    flt = f"startswith(gc_name,'{task_name_prefix}') and gc_status eq {B}" + (f" and _gc_deal_value eq {deal_id}" if deal_id else "")
    task = wait(f"task '{task_name_prefix}…'", lambda: (get(f"gc_reviewtasks?$select=gc_reviewtaskid,gc_name,gc_payload&$filter={flt}") or [None])[0], minutes, kick=False)
    log(f"   approving task: {task['gc_name']}")
    s, b = dv.patch(f"gc_reviewtasks({task['gc_reviewtaskid']})", {"gc_status": REVIEW_APPROVED})
    if s >= 300:
        raise SystemExit(f"approve failed: {json.dumps(b)[:300]}")
    return task


def verify_party(account_id, name):
    """What staff do after checking the KYB documents (simulated): KYB Passed, tier KYB Verified, a Clear screening."""
    dv.patch(f"accounts({account_id})", {"gc_kybstatus": B + 2, "gc_trusttier": B + 2})
    dv.post("gc_screenings", {"gc_name": f"[AGENT-TEST] screening {name}", "gc_result": B, "gc_lists": "TEST ONLY",
                              "gc_screenedon": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                              "gc_account@odata.bind": f"/accounts({account_id})"})
    log(f"   KYB + screening recorded for {name} (simulating the staff check)")


def run():
    tag = "[AGENT-TEST] E2E " + datetime.now().strftime("%m%d-%H%M")
    state = {"tag": tag, "started": datetime.now().isoformat()}
    with Kit(keep_on=True) as kit:
        me = kit.call("profile")["emailAddress"].lower()
        user, domain = me.split("@")
        alias = lambda x: f"{user}+{x}@{domain}"
        # Counterparties write from their own domains. Drafts sent to them bounce (.example never delivers), which is fine for the test.
        buyer_addr, seller1_addr, seller2_addr = "rakesh.jain@ferroalloys-buyer.example", "li.wei@panzhihua-vanadium.example", "zhang.min@hunan-vanadium.example"
        desk = alias("dealos")
        log(f"mailbox {me}; desk alias {desk}; run {tag}")

        # 0. leads: a trade-data extract for vanadium pentoxide; two suppliers have (alias) emails, one has none
        leads_csv = os.path.join(ROOT, "build", "e2e_leads.csv")
        rows = [
            ["DATES", "HS CODE", "PRODUCT DESCRIPTION", "SUPPLIER", "PURCHASER", "COUNTRY OF ORIGIN", "PURCHASING COUNTRY", "WEIGHT(KG)", "SUPPLIER EMAIL", "SUPPLIER CONTACT"],
            ["2026-08-01", "28253010", "VANADIUM PENTOXIDE FLAKES 98% MIN (V2O5)", "[AGENT-TEST] Panzhihua Vanadium Test Co", "X", "CHINA", "INDIA", "20000", seller1_addr, "Li Wei"],
            ["2026-07-15", "28253010", "VANADIUM PENTOXIDE FLAKES V2O5 98.5%", "[AGENT-TEST] Panzhihua Vanadium Test Co", "X", "CHINA", "INDIA", "25000", seller1_addr, "Li Wei"],
            ["2026-06-10", "28253010", "V2O5 FLAKES 99% VANADIUM PENTOXIDE", "[AGENT-TEST] Hunan Vanadium Test Ltd", "X", "CHINA", "INDIA", "18000", seller2_addr, "Zhang Min"],
            ["2026-05-02", "28253010", "VANADIUM PENTOXIDE POWDER", "[AGENT-TEST] Vanadium Trading Test (no email)", "X", "RUSSIA", "INDIA", "10000", "", ""],
        ]
        os.makedirs(os.path.dirname(leads_csv), exist_ok=True)
        with open(leads_csv, "w", newline="") as f:
            csv.writer(f).writerows(rows)
        log("importing test leads")
        print(subprocess.run([sys.executable, os.path.join(ROOT, "tools", "import_leads.py"), leads_csv, "--source", "[AGENT-TEST] E2E", "--sides", "seller"],
                             capture_output=True, text=True).stdout)

        # 1. the buyer's requirement
        buyer_body = ("Hi, I have an immediate requirement for Vanadium Pentoxide (V2O5) in India.\n\nQuantity: 10-15 MT\n\nRequired specification:\n"
                      "V2O5: 98% min\nP: 0.05% max\n-10 mm: 5% max\n+60 mm: 10% max\n\nPacking: 10 kg bags, consolidated into 1 MT jumbo bags.\n"
                      "Delivery: Immediate\nBasis: CIF Nhava Sheva, India.\nPayment: LC at sight.\n\nPlease send your best price, available quantity, origin, "
                      "COA, delivery lead time and payment terms.\n\nPlease treat this requirement as confidential.\n\nRakesh Jain\nPurchase Manager\n"
                      "[AGENT-TEST] Ferro Alloys Buyer Pvt Ltd, Mumbai")
        gid, buyer_thread = insert(kit, mime(f"Rakesh Jain <{buyer_addr}>", desk, f"{tag} Requirement: Vanadium Pentoxide 10-15 MT", buyer_body))
        state.update(buyer_gmail=gid, buyer_gmail_thread=buyer_thread)
        save(state)
        log(f"1. buyer email in the inbox ({gid})")
        m = wait("buyer email triaged", lambda: (lambda x: x if x and x.get("gc_triage") else None)(message(gid)))
        log(f"   triage: {TRIAGE.get(m['gc_triage'])}, score {m.get('gc_triagescore')}")
        if TRIAGE.get(m["gc_triage"]) != "Genuine":
            raise SystemExit("buyer email was not judged Genuine; see gc_triagereasons")
        conv = m["_gc_conversation_value"]
        state["buyer_conv"] = conv
        req = wait("requirement saved and sourcing started", lambda: (lambda c: c if c.get("_gc_requirement_value") and get(
            f"gc_rfqinvites?$select=gc_rfqinviteid&$filter=_gc_requirement_value eq {c['_gc_requirement_value']}") else None)(
            get(f"gc_conversations({conv})?$select=_gc_requirement_value,gc_side")))["_gc_requirement_value"]
        state["requirement"] = req
        save(state)
        sellers = get(f"gc_conversations?$select=gc_conversationid,gc_name,_gc_deal_value,_gc_counterparty_value&$filter=_gc_requirement_value eq {req} and gc_side eq {B + 1}")
        log(f"   requirement {req}; {len(sellers)} seller thread(s): " + "; ".join(s_["gc_name"] for s_ in sellers))
        state["seller_convs"] = {s_["gc_name"]: s_["gc_conversationid"] for s_ in sellers}
        save(state)
        send_all(kit, [conv] + [s_["gc_conversationid"] for s_ in sellers], "acknowledgement + enquiries")

        # 2. seller 1 quotes, seller 2 declines
        threads = {s_["gc_name"]: get(f"gc_conversations({s_['gc_conversationid']})?$select=gc_gmailthreadid,gc_conversationid") for s_ in sellers}
        s1 = next(v for k, v in threads.items() if "Panzhihua" in k)
        s2 = next((v for k, v in threads.items() if "Hunan" in k), None)
        state.update(seller1_conv=s1["gc_conversationid"], seller2_conv=s2 and s2["gc_conversationid"])
        coa = pdf(["CERTIFICATE OF ANALYSIS", "Product: Vanadium Pentoxide flakes", "V2O5: 98.2%", "P: 0.03%", "S: 0.01%", "Lot: VP-2609-14"])
        insert(kit, mime(f"Li Wei <{seller1_addr}>", desk, "Re: Enquiry: Vanadium Pentoxide",
                         "Dear Sir,\n\nThank you for your enquiry. We can offer 15 MT Vanadium Pentoxide flakes, V2O5 98.2%, P 0.03% max, origin China, "
                         "packed in 1 MT jumbo bags.\n\nPrice: USD 9,850 per MT CIF Nhava Sheva.\nShipment: within 3 weeks of LC.\nPayment: LC at sight.\n"
                         "Validity: 7 days.\n\nCOA of the current lot attached.\n\nBest regards,\nLi Wei\nSales Manager", ("COA-VP-2609-14.pdf", coa)),
               s1["gc_gmailthreadid"])
        if s2:
            insert(kit, mime(f"Zhang Min <{seller2_addr}>", desk, "Re: Enquiry: Vanadium Pentoxide",
                             "Dear Sir,\n\nThank you, but we have no V2O5 available for prompt shipment at the moment.\n\nRegards,\nZhang Min"), s2["gc_gmailthreadid"])
        log("2. seller 1 quoted USD 9,850 CIF; seller 2 declined")
        wait("seller quote recorded and our offer drafted to the buyer",
             lambda: get(f"gc_offers?$select=gc_price&$filter=_gc_deal_value eq {next(x['_gc_deal_value'] for x in sellers if x['gc_conversationid'] == s1['gc_conversationid'])}")
             and ready_drafts([conv]), 15)
        send_all(kit, [conv], "our offer to the buyer")

        # 3. buyer proposes a price
        insert(kit, mime(f"Rakesh Jain <{buyer_addr}>", desk, f"Re: {tag} Requirement: Vanadium Pentoxide 10-15 MT",
                         "Thanks for the offer. The price is on the high side for us. We can do USD 10,000 per MT CIF Nhava Sheva for 15 MT, "
                         "other terms as offered. Please confirm.\n\nRakesh"), buyer_thread)
        log("3. buyer proposed USD 10,000")
        deal_id = next(x["_gc_deal_value"] for x in sellers if x["gc_conversationid"] == s1["gc_conversationid"])
        state.update(seller1_conv=s1["gc_conversationid"], seller1_thread=s1["gc_gmailthreadid"], deal=deal_id)
        save(state)
        steps_from_bid(kit, state, alias, desk)
    off()
    state["finished"] = datetime.now().isoformat()
    save(state)
    log("END TO END PASSED")
    status()


def bid_drafted(deal_id, conv_id):
    bids = get(f"gc_offers?$select=gc_price&$filter=startswith(gc_name,'Our bid') and _gc_deal_value eq {deal_id}&$orderby=createdon desc")
    return bids and ready_drafts([conv_id]) and bids


def steps_from_bid(kit, state, alias, desk):
    """Steps 3b-5: from the buyer's price proposal to the signed contract (also used by 'resume')."""
    tag, conv, deal_id, s1_conv = state["tag"], state["buyer_conv"], state["deal"], state["seller1_conv"]
    buyer_thread, s1_thread = state["buyer_gmail_thread"], state["seller1_thread"]
    buyer_addr, seller1_addr = "rakesh.jain@ferroalloys-buyer.example", "li.wei@panzhihua-vanadium.example"
    bid = wait("our bid drafted to seller 1", lambda: bid_drafted(deal_id, s1_conv), 15)[0]["gc_price"]
    send_all(kit, [c for c in (s1_conv, conv) if drafts(c)], "bid to seller + note to buyer")

    # 4. seller accepts our bid → confirm deal → (KYB) → compliance → contract
    insert(kit, mime(f"Li Wei <{seller1_addr}>", desk, "Re: Enquiry: Vanadium Pentoxide",
                     f"Dear Sir,\n\nWe accept your bid of USD {bid:,.2f} per MT CIF Nhava Sheva for 15 MT. Please send the contract.\n\nBest regards,\nLi Wei"),
           s1_thread)
    log(f"4. seller 1 accepted our bid of USD {bid:,.2f}")
    deal = get(f"gc_deals({deal_id})?$select=_gc_buyer_value,_gc_seller_value")
    verify_party(deal["_gc_buyer_value"], "buyer")
    verify_party(deal["_gc_seller_value"], "seller")
    approve("Confirm deal", deal_id, 15)
    stage = lambda: get(f"gc_deals({deal_id})?$select=gc_stage,gc_buyerprice,gc_price").get("gc_stage@OData.Community.Display.V1.FormattedValue")
    wait("deal past Terms Agreed (compliance)", lambda: stage() in ("Compliance Check", "Contracting"), 8, kick=False)
    contract_task = lambda: get(f"gc_reviewtasks?$select=gc_reviewtaskid&$filter=_gc_deal_value eq {deal_id} and gc_purpose eq {B + 3} and gc_status eq {B}")
    screening_task = lambda: get(f"gc_reviewtasks?$select=gc_reviewtaskid&$filter=_gc_deal_value eq {deal_id} and gc_purpose eq {B + 2} and gc_status eq {B}")
    first = wait("compliance decided (contract drafted, or a screening review for the officer)", lambda: contract_task() or screening_task(), 10, kick=False)
    if not contract_task() and screening_task():
        log("   compliance referred the deal to the officer; approving the screening clearance (simulating the officer)")
        dv.patch(f"gc_reviewtasks({screening_task()[0]['gc_reviewtaskid']})", {"gc_status": REVIEW_APPROVED})
        wait("contract drafted (Contract Issue task)", contract_task, 10, kick=False)
    approve("Review contract terms", deal_id)
    send_all(kit, [conv, s1_conv], "contracts to both sides")

    # 5. buyer returns the signed contract
    signed = pdf(["SALES CONTRACT (signed copy)", "Signed for the Buyer: Rakesh Jain, Purchase Manager", "Date: " + datetime.now().strftime("%d %B %Y")])
    insert(kit, mime(f"Rakesh Jain <{buyer_addr}>", desk, f"Re: {tag} Requirement: Vanadium Pentoxide 10-15 MT",
                     "Please find the signed sales contract attached. Our company documents follow separately.\n\nRakesh", ("Sales-contract-signed.pdf", signed)),
           buyer_thread)
    log("5. buyer returned the signed contract")
    approve("Signed contract received", deal_id, 15)
    wait("deal Signed (no escrow)", lambda: stage() == "Signed", 8, kick=False)
    wait("inspection requested", lambda: get(f"gc_inspections?$select=gc_name&$filter=_gc_deal_value eq {deal_id}"), 6, kick=False)


def resume():
    """Continue a run that stopped after the buyer's price proposal (state in build/desk_e2e.json)."""
    state = json.load(open(STATE))
    if "seller1_conv" not in state:
        s1 = next(v for k, v in state["seller_convs"].items() if "Panzhihua" in k)
        c = get(f"gc_conversations({s1})?$select=_gc_deal_value,gc_gmailthreadid")
        state.update(seller1_conv=s1, seller1_thread=c["gc_gmailthreadid"], deal=c["_gc_deal_value"])
        save(state)
    with Kit(keep_on=True) as kit:
        me = kit.call("profile")["emailAddress"].lower()
        user, domain = me.split("@")
        alias = lambda x: f"{user}+{x}@{domain}"
        log(f"resuming {state['tag']} from the bid to seller 1")
        steps_from_bid(kit, state, alias, alias("dealos"))
    off()
    state["finished"] = datetime.now().isoformat()
    save(state)
    log("END TO END PASSED")
    status()


def status():
    if not os.path.exists(STATE):
        sys.exit("no run yet")
    st = json.load(open(STATE))
    print(json.dumps(st, indent=2))
    if st.get("requirement"):
        r = get(f"gc_buyerrequirements({st['requirement']})?$select=gc_name,gc_deskstage,gc_targetprice,gc_ourprice")
        print("requirement:", r.get("gc_name"), "| stage", r.get("gc_deskstage@OData.Community.Display.V1.FormattedValue"),
              "| buyer price", r.get("gc_targetprice"), "| our price", r.get("gc_ourprice"))
    if st.get("deal"):
        d = get(f"gc_deals({st['deal']})?$select=gc_name,gc_stage,gc_price,gc_buyerprice")
        print("deal:", d.get("gc_name"), "| stage", d.get("gc_stage@OData.Community.Display.V1.FormattedValue"), "| seller price", d.get("gc_price"),
              "| buyer price", d.get("gc_buyerprice"))


if __name__ == "__main__":
    {"run": run, "resume": resume, "status": status}.get(sys.argv[1] if len(sys.argv) > 1 else "", lambda: print(__doc__))()

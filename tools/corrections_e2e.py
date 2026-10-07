"""Triage corrections from Gmail labels, end to end on the real mailbox.

  1. a vendor pitch arrives at the +dealos alias and is stored (and triaged when the model is available)
  2. the owner moves it to another DealOS label in Gmail (DealOS/Ignored, or DealOS/Review if it was already Ignored)
  3. the next Mailbox sync reads Gmail's label history and the email takes that verdict, with the correction recorded
DealOS/Ignored and DealOS/Review do not start the Trade Desk, so the test spends no model calls on the correction itself.
Usage: python3 tools/corrections_e2e.py run
"""
import os, sys, time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import desk_e2e as e  # noqa: E402
from gmail_kit import Kit  # noqa: E402

B = 303300000
VERDICT = {B: "Genuine", B + 1: "Review", B + 2: "Ignored"}


def run():
    stamp = datetime.now().strftime("%m%d-%H%M%S")
    with Kit() as kit:
        box = kit.call("profile")["emailAddress"].lower()
        user, domain = box.split("@")
        raw = e.mime("Growth Team <growth@seo-rankings-agency.example>", f"{user}+dealos@{domain}", f"[AGENT-TEST] Rank your metals website #1 {stamp}",
                     "Hi, we help commodity traders get more leads with SEO and paid ads. Book a free call this week. Unsubscribe anytime.")
        gid, _ = e.insert(kit, raw)
        e.log("1. vendor pitch inserted")
        msg = e.wait("pitch stored by Mailbox sync", lambda: e.message(gid), 8)
        time.sleep(60)  # let triage finish (or fail without model credits)
        msg = e.message(gid)
        before = VERDICT.get(msg.get("gc_triage"))
        target = "DealOS/Review" if before == "Ignored" else "DealOS/Ignored"
        e.log(f"   stored verdict: {before or 'not triaged'}; the owner moves it to {target}")
        labels = {l["name"]: l["id"] for l in kit.call("labels").get("labels", [])}
        kit.call("add_label", id=gid, q=labels[target])
        e.log("2. label added in Gmail")
        row = e.wait("correction applied by the next sync", lambda: (lambda m: m if m.get("gc_triagecorrection") else None)(
            e.get(f"gc_messages({msg['gc_messageid']})?$select=gc_triage,gc_triagecorrection,gc_correctedon")), 8)
        expected = target.split("/")[1]
        if VERDICT.get(row["gc_triage"]) != expected:
            raise SystemExit(f"✗ verdict is {VERDICT.get(row['gc_triage'])}, expected {expected}")
        e.log(f"   {row['gc_triagecorrection']}")
    print("TRIAGE CORRECTIONS END TO END PASSED")


if __name__ == "__main__":
    if sys.argv[1:] == ["run"]:
        run()
    else:
        print(__doc__)

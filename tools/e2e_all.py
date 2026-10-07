"""Runs every end-to-end scenario on the real mailbox and Dev, one after the other, and writes a summary.

  approve     tools/approval_e2e.py run           approve by reply: APPROVE, REJECT <reason>, a forged sender ignored, SEND, PIPELINE (no AI)
  corrections tools/corrections_e2e.py run        the owner moves an email to another DealOS label in Gmail → the verdict follows (no AI for the correction)
  triage      tools/mail_test.py run              7 sample emails: genuine trade vs scam, pitch, broker chain
  buyer       tools/desk_e2e.py run               buyer first: sourcing → sellers take turns → counter → bid → Confirm deal →
                                                  compliance → contract PDFs → signed → inspection
  tracking    tools/tracking_e2e.py run           on the signed deal: inspection booked, sailing, delivery → masked drafts to both sides, each once (no AI)
  queue       tools/desk_queue_e2e.py run         first seller active, later one queued, decline ignored, buyer rejects → next seller
  lot         tools/desk_lot_e2e.py run           seller lot, two buyers compete, highest price wins at the deadline
  single      tools/desk_lot_e2e.py single        seller lot, one buyer, early close
  open        tools/desk_lot_e2e.py open          seller first, open-ended: counters both ways, seller accepts our bid
  below       tools/desk_lot_e2e.py below         48 h window from the email; best bid below the price → to the seller
  coal        tools/desk_lot_e2e.py coal          the owner's coal case: other port, company profile, quality report
  discovery   gc_DiscoverSellers / gc_DiscoverBuyers (Force) on the queue requirement and the coal lot: real web search,
              leads saved, no drafts

Web discovery is switched off (email.discovery.enabled = false) while the scenarios run, so real companies found on the web
never get test drafts; it is switched back on for the discovery check at the end.

Usage: python3 tools/e2e_all.py [scenario ...]        (default: all, in the order above)
"""
import json, os, subprocess, sys, time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

ROOT = dv.ROOT
TOOLS = os.path.join(ROOT, "tools")
LOGS = os.path.join(ROOT, "build", "e2e")
SCENARIOS = [
    ("approve", ["approval_e2e.py", "run"], "APPROVE BY REPLY END TO END PASSED"),
    ("corrections", ["corrections_e2e.py", "run"], "TRIAGE CORRECTIONS END TO END PASSED"),
    ("triage", ["mail_test.py", "run"], "TRIAGE PASSED"),
    ("buyer", ["desk_e2e.py", "run"], "END TO END PASSED"),
    ("tracking", ["tracking_e2e.py", "run"], "TRACKING END TO END PASSED"),
    ("queue", ["desk_queue_e2e.py", "run"], "SELLERS TAKE TURNS END TO END PASSED"),
    ("lot", ["desk_lot_e2e.py", "run"], "LOT END TO END PASSED"),
    ("single", ["desk_lot_e2e.py", "single"], "SINGLE-BUYER LOT END TO END PASSED"),
    ("open", ["desk_lot_e2e.py", "open"], "OPEN-ENDED LOT (SELLER FIRST) END TO END PASSED"),
    ("below", ["desk_lot_e2e.py", "below"], "TIMED LOT → BEST BID TO SELLER PASSED"),
    ("coal", ["desk_lot_e2e.py", "coal"], "COAL (OTHER PORT, PROFILE, REPORT) END TO END PASSED"),
]


def setting(key, value):
    s, b = dv.get(f"gc_platformsettings?$select=gc_platformsettingid&$filter=gc_key eq '{key}'")
    rows = b.get("value", []) if s == 200 else []
    if rows:
        dv.patch(f"gc_platformsettings({rows[0]['gc_platformsettingid']})", {"gc_value": value})
    print(f"* {key} = {value}", flush=True)


def run_scenario(name, cmd, marker):
    path = os.path.join(LOGS, name + ".log")
    started = time.time()
    with open(path, "w") as out:
        p = subprocess.run([sys.executable, "-u", os.path.join(TOOLS, cmd[0])] + cmd[1:], stdout=out, stderr=subprocess.STDOUT, timeout=3600)
    text = open(path).read()
    ok = p.returncode == 0 and marker in text
    fail = next((l for l in text.splitlines() if l.startswith("✗") or "Traceback" in l or "SystemExit" in l), None)
    return {"scenario": name, "passed": ok, "minutes": round((time.time() - started) / 60, 1), "log": path,
            "failure": None if ok else (fail or text.strip().splitlines()[-1] if text.strip() else "no output")}


def discovery():
    """Real web search for sellers (queue requirement) and buyers (coal lot); prints what was found and where."""
    out = {"scenario": "discovery", "passed": False}
    q = os.path.join(ROOT, "build", "desk_queue_e2e.json")
    c = os.path.join(ROOT, "build", "desk_lot_e2e.json")
    results = []
    if os.path.exists(q) and json.load(open(q)).get("requirement"):
        s, b = dv.request("POST", "gc_DiscoverSellers", {"RequirementId": json.load(open(q))["requirement"], "Force": True})
        results.append(("sellers for niobium pentoxide", s, b))
    if os.path.exists(c) and json.load(open(c)).get("scenario") == "coal":
        s, b = dv.request("POST", "gc_DiscoverBuyers", {"LotId": json.load(open(c))["lot"], "Force": True})
        results.append(("buyers for Tanzanian thermal coal", s, b))
    lines = []
    found = 0
    for label, s, b in results:
        r = json.loads(b.get("Result", "{}")) if s < 300 else {"status": f"HTTP {s}", "reason": json.dumps(b)[:300]}
        lines.append(f"{label}: {r.get('status')}, {r.get('found', 0)} found, {r.get('with_email', 0)} with email {r.get('reason') or ''}")
        for l in r.get("leads", [])[:10]:
            lines.append(f"   - {l.get('name')} ({l.get('country', '')}) {l.get('email') or 'no email'} {l.get('website') or ''}")
        found += r.get("found", 0) or 0
    out.update(passed=found > 0, detail=lines)
    print("\n".join(lines), flush=True)
    return out


def main():
    wanted = sys.argv[1:] or [s[0] for s in SCENARIOS] + ["discovery"]
    os.makedirs(LOGS, exist_ok=True)
    summary = []
    setting("email.discovery.enabled", "false")
    try:
        for name, cmd, marker in SCENARIOS:
            if name not in wanted:
                continue
            print(f"{datetime.now():%H:%M} ▶ {name}", flush=True)
            if name == "triage":  # the samples are idempotent by Gmail id: clear the previous run so they are triaged again
                subprocess.run([sys.executable, os.path.join(TOOLS, "mail_test.py"), "cleanup"], capture_output=True, timeout=600)
            r = run_scenario(name, cmd, marker)
            summary.append(r)
            print(f"{datetime.now():%H:%M} {'PASS' if r['passed'] else 'FAIL'} {name} ({r['minutes']} min)" + ("" if r["passed"] else f": {r['failure']}"), flush=True)
    finally:
        setting("email.discovery.enabled", "true")
    if "discovery" in wanted:
        print(f"{datetime.now():%H:%M} ▶ discovery", flush=True)
        summary.append(discovery())
    json.dump(summary, open(os.path.join(LOGS, "summary.json"), "w"), indent=2, default=str)
    print("\nSUMMARY")
    for r in summary:
        print(f"  {'PASS' if r['passed'] else 'FAIL'}  {r['scenario']}" + ("" if r["passed"] else f"  ← {r.get('failure')}"))


if __name__ == "__main__":
    main()

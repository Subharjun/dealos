"""Run one DealOS agent through its Custom API and print the result.

Usage:
  python3 tools/run_agent.py <AgentName> [SubjectId] [--dry] [--input '{"k":"v"}'] [--raw]
  e.g. python3 tools/run_agent.py ListingVerification f6e59aa2-... --dry
"""
import json, os, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402


def run(agent, subject=None, dry=False, payload=None):
    body = {"DryRun": dry}
    if subject:
        body["SubjectId"] = subject
    if payload is not None:
        body["Input"] = json.dumps(payload)
    t = time.time()
    status, resp = dv.request("POST", f"gc_Agent_{agent}", body, timeout=150)
    return status, resp, time.time() - t


def main():
    args = sys.argv[1:]
    if not args:
        sys.exit(__doc__)
    agent = args.pop(0)
    dry = "--dry" in args
    raw = "--raw" in args
    payload = None
    if "--input" in args:
        payload = json.loads(args[args.index("--input") + 1])
    rest = [a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or args[i - 1] != "--input")]
    status, resp, secs = run(agent, rest[0] if rest else None, dry, payload)
    if status >= 300:
        print(f"HTTP {status} after {secs:.1f}s")
        print(json.dumps(resp.get("error", resp), indent=2)[:3000])
        sys.exit(1)
    result = json.loads(resp.get("Result") or "{}")
    if raw:
        print(json.dumps(result, indent=2))
        return
    print(f"{agent}: {resp.get('Status')} in {secs:.1f}s  needs_human={resp.get('NeedsHuman')}  run={resp.get('AgentRunId')}")
    print(f"model={result.get('model')} steps={result.get('steps')} tokens={result.get('tokens_in')}/{result.get('tokens_out')} dry_run={result.get('dry_run')}")
    if result.get("error"):
        print("error:", result["error"])
    print("summary:", resp.get("Summary"))
    print("result:", json.dumps(result.get("result"), indent=2)[:6000])
    print("actions:", json.dumps(result.get("actions"), indent=2)[:4000])


if __name__ == "__main__":
    main()

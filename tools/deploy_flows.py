"""Deploy the DealOS cloud flows (idempotent) into the DealOS solution and turn them on.

Definitions live in tools/flows/definitions.py. Existing flows keep their ids; new flows get stable ids,
so running this again updates them in place (deactivate → update → activate).

Usage:
  python3 tools/deploy_flows.py                       # deploy and activate all flows, then delete the retired ones (definitions.RETIRED)
  python3 tools/deploy_flows.py --only "Offer pricing" # one flow (substring of the name)
  python3 tools/deploy_flows.py --off                 # deploy but leave them off
  python3 tools/deploy_flows.py --dump build/flows    # write the generated JSON only, no deployment
  python3 tools/deploy_flows.py --tests --off         # the test-only flows (Email Desk test kit), left off
  python3 tools/deploy_flows.py --esign               # the DocuSign e-signature flows (after deploy_connector.py docusign + docusign-bind)
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402
from flows import definitions  # noqa: E402
from flows.lib import clientdata  # noqa: E402

SOL = {"MSCRM.SolutionUniqueName": "DealOS"}


def state(fid):
    s, b = dv.get(f"workflows({fid})?$select=statecode,name")
    return b.get("statecode") if s == 200 else None


def set_state(fid, on):
    return dv.patch(f"workflows({fid})", {"statecode": 1 if on else 0, "statuscode": 2 if on else 1})


def deploy(f, activate):
    data = json.dumps(clientdata(f["definition"], f["connections"]))
    current = state(f["id"])
    if current is None:
        s, b = dv.request("POST", "workflows", {
            "workflowid": f["id"], "name": f["name"], "description": f["description"][:2000], "category": 5, "type": 1,
            "primaryentity": "none", "clientdata": data}, SOL)
        verb = "+"
    else:
        if current == 1:
            s, b = set_state(f["id"], False)
            if s >= 300:
                return f"! {f['name']}: could not turn off: {json.dumps(b)[:600]}"
        s, b = dv.patch(f"workflows({f['id']})", {"name": f["name"], "description": f["description"][:2000], "clientdata": data})
        verb = "="
    if s >= 300:
        return f"! {f['name']}: {s} {json.dumps(b)[:1500]}"
    if not activate:
        return f"{verb} {f['name']} (off)"
    s, b = set_state(f["id"], True)
    if s >= 300:
        return f"! {f['name']}: saved but not activated: {json.dumps(b)[:1500]}"
    return f"{verb} {f['name']} (on)"


def retire():
    """Turns off and deletes the flows removed from the build (definitions.RETIRED), child flows last."""
    names = sorted(definitions.RETIRED, key=lambda n: n == "DealOS | Notify party")
    for name in names:
        s, b = dv.get(f"workflows?$select=workflowid,statecode&$filter=name eq '{name}' and category eq 5")
        for w in (b.get("value", []) if s == 200 else []):
            if w["statecode"] == 1:
                set_state(w["workflowid"], False)
            s2, b2 = dv.request("DELETE", f"workflows({w['workflowid']})")
            print(f"- {name} deleted" if s2 < 300 else f"! {name}: not deleted: {json.dumps(b2)[:400]}", flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only")
    ap.add_argument("--off", action="store_true")
    ap.add_argument("--dump")
    ap.add_argument("--tests", action="store_true", help="deploy the test-only flows instead of the product flows")
    ap.add_argument("--esign", action="store_true", help="deploy the DocuSign e-signature flows (needs the gc_docusign connection)")
    args = ap.parse_args()
    flows = [build() for build in (definitions.TESTS if args.tests else definitions.ESIGN_FLOWS if args.esign else definitions.ALL)]
    if args.only:
        flows = [f for f in flows if args.only.lower() in f["name"].lower()]
    if args.dump:
        os.makedirs(args.dump, exist_ok=True)
        for f in flows:
            path = os.path.join(args.dump, f["name"].replace("DealOS | ", "").replace(" ", "_") + ".json")
            with open(path, "w") as out:
                json.dump(clientdata(f["definition"], f["connections"]), out, indent=2)
        print(f"Wrote {len(flows)} flow definitions to {args.dump}")
        return
    failed = 0
    for f in flows:
        line = deploy(f, not args.off)
        failed += line.startswith("!")
        print(line, flush=True)
    print(f"{len(flows) - failed} of {len(flows)} flows deployed" + ("" if args.off else " and on"))
    if not args.tests and not args.esign and not args.only and not failed:
        retire()
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()

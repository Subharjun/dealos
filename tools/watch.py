"""Show what DealOS did since a point in time: flow runs, audit events, flow failures and review tasks.

Usage:
  python3 tools/watch.py 2026-10-06T10:00:00Z
  python3 tools/watch.py 15m          # the last 15 minutes
"""
import datetime as dt
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

F = "@OData.Community.Display.V1.FormattedValue"


def since(arg):
    if arg.endswith("m") and arg[:-1].isdigit():
        return (dt.datetime.now(dt.timezone.utc) - dt.timedelta(minutes=int(arg[:-1]))).strftime("%Y-%m-%dT%H:%M:%SZ")
    return arg


def rows(path):
    s, b = dv.get(path)
    return b.get("value", []) if s == 200 else [{"error": b}]


def main():
    t = since(sys.argv[1] if len(sys.argv) > 1 else "15m")
    print(f"--- flow runs since {t}")
    for r in rows(f"flowruns?$select=name,status,starttime,endtime,errorcode,errormessage,_workflow_value&$filter=starttime ge {t}&$orderby=starttime asc"):
        if "error" in r:
            print("  (flowruns not available)", str(r)[:200])
            break
        err = f"  {r.get('errorcode') or ''} {(r.get('errormessage') or '')[:300]}" if r.get("status") not in ("Succeeded", "Running") else ""
        print(f"  {r['starttime'][11:19]} {r.get('status', ''):10s} {r.get('_workflow_value' + F, r.get('_workflow_value'))}{err}")
    print("--- audit events")
    for r in rows(f"gc_auditevents?$select=gc_action,gc_actor,gc_details,createdon&$filter=createdon ge {t}&$orderby=createdon asc"):
        print(f"  {r['createdon'][11:19]} {r['gc_actor']:30s} {r['gc_action']:24s} {(r.get('gc_details') or '')[:220]}")
    print("--- flow failures")
    for r in rows(f"gc_flowfailures?$select=gc_flowname,gc_step,gc_error,createdon&$filter=createdon ge {t}&$orderby=createdon asc"):
        print(f"  {r['createdon'][11:19]} {r['gc_flowname']} @ {r['gc_step']}: {(r.get('gc_error') or '')[:600]}")
    print("--- review tasks")
    for r in rows(f"gc_reviewtasks?$select=gc_name,gc_purpose,gc_status,gc_payload,createdon&$filter=createdon ge {t}&$orderby=createdon asc"):
        print(f"  {r['createdon'][11:19]} {r.get('gc_purpose' + F)} [{r.get('gc_status' + F)}] {r['gc_name']} {(r.get('gc_payload') or '')[:200]}")


if __name__ == "__main__":
    main()

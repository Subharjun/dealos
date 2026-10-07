"""Approve by reply, end to end on the real mailbox (no AI calls, so it runs whatever the model quota).

  1. APPROVE   a test decision task is briefed (Approval briefing flow, reply code) → the owner replies APPROVE → task Approved
  2. REJECT    a second task → reply "REJECT not at this price" → task Rejected with the reason
  3. FORGED    a third task → a reply from another address saying APPROVE → task stays Open, "Reply not acted on" briefing
  4. SEND      a draft (to the mailbox itself) listed in a briefing → reply SEND → the Gmail draft is sent
  5. PIPELINE  reply PIPELINE → a pipeline briefing

The test tasks carry action desk.test_noop, so approving them changes nothing else. Records are marked [AGENT-TEST].
Usage: python3 tools/approval_e2e.py run
"""
import json, os, sys, time
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402
import desk_e2e as e  # noqa: E402
from gmail_kit import Kit  # noqa: E402

B = 303300000
OPEN, APPROVED, REJECTED = B, B + 1, B + 2
AUTH = "Authentication-Results: mx.google.com; dkim=pass header.i=@gmail.com; spf=pass smtp.mailfrom=gmail.com; dmarc=pass (p=NONE) header.from=gmail.com\r\n"


def mailbox(kit):
    return kit.call("profile")["emailAddress"].lower()


def alias(box):
    user, domain = box.split("@")
    return f"{user}+dealos@{domain}"


def task(title, payload):
    s, b = dv.post("gc_reviewtasks", {"gc_name": title[:100], "gc_kind": B, "gc_purpose": B + 9, "gc_assigneerole": B + 2, "gc_status": OPEN,
                                      "gc_payload": json.dumps(payload)})
    if s >= 300:
        raise SystemExit(f"task not created: {json.dumps(b)[:300]}")
    rows = e.get(f"gc_reviewtasks?$select=gc_reviewtaskid&$filter=gc_name eq '{title[:100]}'")
    return rows[0]["gc_reviewtaskid"]


def briefing_for(code):
    """The briefing with this reply code, once Gmail has it (draft created; it is sent automatically)."""
    rows = e.get(f"gc_messages?$select=gc_messageid,gc_subject,gc_gmaildraftid,_gc_conversation_value&$filter=contains(gc_subject,'(ref {code})')")
    rows = [r for r in rows if r.get("gc_gmaildraftid")]
    return rows[0] if rows else None


def briefed(task_id, what):
    t = e.wait(f"{what}: briefed with a reply code", lambda: (lambda r: r if r.get("gc_replycode") else None)(
        e.get(f"gc_reviewtasks({task_id})?$select=gc_replycode,gc_status")), 6, kick=False)
    b = e.wait(f"{what}: briefing in Gmail", lambda: briefing_for(t["gc_replycode"]), 6, kick=False)
    time.sleep(20)  # auto-send of the briefing
    return t["gc_replycode"], b


def thread_of(briefing):
    return e.get(f"gc_conversations({briefing['_gc_conversation_value']})?$select=gc_gmailthreadid")["gc_gmailthreadid"]


def reply(kit, frm, to, briefing, body, auth=True):
    raw = e.mime(frm, to, "Re: " + briefing["gc_subject"], body)
    if auth:
        raw = AUTH + raw
    gid, _ = e.insert(kit, raw, thread_of(briefing))
    return gid


def status(task_id):
    return e.get(f"gc_reviewtasks({task_id})?$select=gc_status,gc_decisionreason")


def run():
    stamp = datetime.now().strftime("%m%d-%H%M")
    with Kit() as kit:
        box = mailbox(kit)
        to = alias(box)
        quoted = "\n\nOn Tue, 7 Oct 2026 at 10:00, DealOS desk wrote:\n> Reply APPROVE or REJECT"

        e.log("1. APPROVE by reply")
        t1 = task(f"[AGENT-TEST] Approve by reply {stamp} A", {"action": "desk.test_noop", "what": "approve test", "buyerPrice": 10145.5,
                                                               "approve": "Approve = nothing happens (test task)."})
        _, b1 = briefed(t1, "task A")
        reply(kit, box, to, b1, "APPROVE" + quoted)
        e.wait("task A approved by reply", lambda: status(t1)["gc_status"] == APPROVED, 8)
        e.log(f"   reason: {status(t1)['gc_decisionreason']}")

        e.log("2. REJECT with a reason")
        t2 = task(f"[AGENT-TEST] Approve by reply {stamp} B", {"action": "desk.test_noop", "what": "reject test"})
        _, b2 = briefed(t2, "task B")
        reply(kit, box, to, b2, "REJECT not at this price" + quoted)
        e.wait("task B rejected with the reason", lambda: status(t2)["gc_status"] == REJECTED and "not at this price" in (status(t2)["gc_decisionreason"] or ""), 8)

        e.log("3. a reply from someone else is not acted on")
        t3 = task(f"[AGENT-TEST] Approve by reply {stamp} C", {"action": "desk.test_noop", "what": "forged reply test"})
        _, b3 = briefed(t3, "task C")
        reply(kit, "Boss <boss@forged-owner.example>", to, b3, "APPROVE" + quoted, auth=False)
        e.wait("'Reply not acted on' briefing", lambda: e.get("gc_messages?$select=gc_messageid&$filter=gc_subject eq '[DealOS] Reply not acted on' and createdon gt "
                                                             + datetime.now(timezone.utc).strftime("%Y-%m-%dT00:00:00Z")), 8)
        if status(t3)["gc_status"] != OPEN:
            raise SystemExit("✗ a reply from another address changed the task")
        e.log("✓ task C is still open")

        e.log("4. SEND releases the drafts listed in a briefing")
        conv = b1["_gc_conversation_value"]
        s, body = dv.post("gc_messages", {"gc_name": f"[AGENT-TEST] SEND probe {stamp}", "gc_subject": f"[AGENT-TEST] SEND probe {stamp}",
                                          "gc_text": "Test draft released by replying SEND to a briefing.", "gc_direction": B + 2, "gc_draftstatus": B,
                                          "gc_toaddress": "{mailbox}", "gc_isplatform": True, "gc_autosend": False,
                                          "gc_conversation@odata.bind": f"/gc_conversations({conv})"})
        probe = e.get(f"gc_messages?$select=gc_messageid&$filter=gc_name eq '[AGENT-TEST] SEND probe {stamp}'")[0]["gc_messageid"]
        e.wait("probe draft in Gmail", lambda: e.get(f"gc_messages({probe})?$select=gc_gmaildraftid").get("gc_gmaildraftid"), 6, kick=False)
        s, r = dv.request("POST", "gc_DeskBrief", {"Subject": f"[AGENT-TEST] Drafts to release {stamp}", "Text": "One test draft.", "Drafts": json.dumps([probe])})
        brief_id = r["MessageId"]
        b4 = e.wait("release briefing in Gmail", lambda: (lambda m: m if m.get("gc_gmaildraftid") else None)(
            e.get(f"gc_messages({brief_id})?$select=gc_messageid,gc_subject,gc_gmaildraftid,_gc_conversation_value")), 6, kick=False)
        time.sleep(20)
        reply(kit, box, to, b4, "Send" + quoted)
        e.wait("probe draft sent from Gmail", lambda: e.get("gc_auditevents?$select=gc_auditeventid&$filter=gc_action eq 'email.sent_on_owner_reply' "
                                                             f"and gc_subjectid eq '{probe}'"), 8)

        e.log("5. PIPELINE")
        since = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        reply(kit, box, to, b4, "pipeline")
        e.wait("pipeline briefing", lambda: e.get(f"gc_messages?$select=gc_messageid&$filter=startswith(gc_subject,'[[]DealOS] Pipeline') and createdon gt {since}"), 8)
    print("APPROVE BY REPLY END TO END PASSED")


if __name__ == "__main__":
    if sys.argv[1:] == ["run"]:
        run()
    else:
        print(__doc__)

"""Talk to the DealOS chat agents from the terminal, the way the portal does: write a gc_message, wait for the reply.

Usage:
  python3 tools/chat.py new <account-guid> buyer|seller [contact-guid]   # creates an [AGENT-TEST] conversation, prints its id
  python3 tools/chat.py say <conversation-guid> "message text"            # posts a user message and waits for the agent's reply
  python3 tools/chat.py show <conversation-guid>                          # prints the conversation

The reply is written by the asynchronous ChatPlugin step (Buyer Concierge or Seller Assistant), usually within 10-60 s.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

BASE = 303300000


def fail(what, status, body):
    sys.exit(f"{what} failed: {status} {json.dumps(body)[:800]}")


def new(account, side, contact=None):
    body = {"gc_name": f"[AGENT-TEST] {side} chat {time.strftime('%Y-%m-%d %H:%M')}",
            "gc_channel": BASE, "gc_assistant": BASE if side == "buyer" else BASE + 1,
            "gc_counterparty@odata.bind": f"/accounts({account})"}
    if contact:
        body["gc_Contact@odata.bind"] = f"/contacts({contact})"
    s, b = dv.request("POST", "gc_conversations", body, {"Prefer": "return=representation"})
    if s >= 300:
        fail("create conversation", s, b)
    print(b["gc_conversationid"])


def messages(conv):
    s, b = dv.get(f"gc_messages?$select=gc_text,gc_isplatform,gc_senderlabel,gc_attachments,createdon"
                  f"&$filter=_gc_conversation_value eq {conv}&$orderby=createdon asc")
    if s >= 300:
        fail("read messages", s, b)
    return b["value"]


def show(conv):
    for m in messages(conv):
        who = m.get("gc_senderlabel") or "DealOS" if m.get("gc_isplatform") else "user"
        print(f"[{m['createdon'][11:19]}] {who}: {m.get('gc_text')}")
        steps = json.loads(m["gc_attachments"]).get("next_steps") if m.get("gc_isplatform") and m.get("gc_attachments") else None
        if steps:
            print("    next: " + " | ".join(steps))


def say(conv, text, wait=150):
    before = {m["gc_messageid"] for m in messages(conv)}
    s, b = dv.request("POST", "gc_messages", {"gc_name": text[:100], "gc_text": text, "gc_isplatform": False,
                                              "gc_senton": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                                              "gc_conversation@odata.bind": f"/gc_conversations({conv})"})
    if s >= 300:
        fail("post message", s, b)
    print(f"user: {text}")
    started = time.time()
    while time.time() - started < wait:
        time.sleep(4)
        new_msgs = [m for m in messages(conv) if m["gc_messageid"] not in before and m.get("gc_isplatform")]
        if new_msgs:
            m = new_msgs[-1]
            print(f"{m.get('gc_senderlabel') or 'DealOS'} ({time.time() - started:.0f}s): {m.get('gc_text')}")
            steps = json.loads(m["gc_attachments"]).get("next_steps") if m.get("gc_attachments") else None
            if steps:
                print("    next: " + " | ".join(steps))
            return
    print(f"no reply after {wait}s; check System Jobs (asyncoperations) or: python3 tools/watch.py 10m")


if __name__ == "__main__":
    if len(sys.argv) >= 4 and sys.argv[1] == "new":
        new(sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else None)
    elif len(sys.argv) >= 4 and sys.argv[1] == "say":
        say(sys.argv[2], " ".join(sys.argv[3:]))
    elif len(sys.argv) == 3 and sys.argv[1] == "show":
        show(sys.argv[2])
    else:
        sys.exit(__doc__)

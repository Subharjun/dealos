"""Calls the 'DealOS | Email Desk test kit' flow (HTTP trigger) to reach Gmail from the command line during tests.

  from gmail_kit import Kit
  with Kit() as kit:                       # switches the kit flow on, and off again afterwards
      kit.call("insert", raw=..., thread_id=...)
"""
import base64, json, os, sys, time, urllib.request, urllib.error

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402
from flows.lib import flow_id  # noqa: E402

ENV = os.environ.get("PP_ENV_ID", "b77eedc7-f980-e3bb-a07e-757db98002d2")
KIT = flow_id("DealOS | Email Desk test kit")
URL_CACHE = os.path.join(dv.ROOT, "build", "kit_url.txt")  # signed trigger URL (secret; build/ is git-ignored)


def flow_token():
    with open(dv.TOKEN_FILE) as f:
        tok = json.load(f)
    r = dv._post_form(f"{dv.AUTH}/token", {"grant_type": "refresh_token", "client_id": dv.CLIENT_ID, "refresh_token": tok["refresh_token"],
                                            "scope": "https://service.flow.microsoft.com//.default offline_access"})
    if "access_token" not in r:
        sys.exit("flow token failed: " + r.get("error_description", "")[:300] + "\nSign in again: python3 tools/dv.py login")
    return r["access_token"]


def b64u(data):
    return base64.urlsafe_b64encode(data if isinstance(data, bytes) else data.encode()).decode().rstrip("=")


class Kit:
    def __init__(self, keep_on=False):
        self.keep_on = keep_on

    def __enter__(self):
        for attempt in range(4):
            if dv.get(f"workflows({KIT})?$select=statecode")[1].get("statecode") == 1:
                break
            s, b = dv.request("PATCH", f"workflows({KIT})", {"statecode": 1, "statuscode": 2}, timeout=300)
            if s < 300:
                break
            time.sleep(20)
        else:
            sys.exit(f"could not switch the test kit on: {s} {json.dumps(b)[:500]}")
        if os.path.exists(URL_CACHE):
            self.url = open(URL_CACHE).read().strip()
            return self
        url = (f"https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/environments/{ENV}/flows/{KIT}"
               "/triggers/manual/listCallbackUrl?api-version=2016-11-01")
        last = None
        for _ in range(6):
            req = urllib.request.Request(url, data=b"", method="POST", headers={"Authorization": "Bearer " + flow_token()})
            try:
                with urllib.request.urlopen(req, timeout=120) as r:
                    self.url = json.load(r)["response"]["value"]
                os.makedirs(os.path.dirname(URL_CACHE), exist_ok=True)
                fd = os.open(URL_CACHE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
                with os.fdopen(fd, "w") as f:
                    f.write(self.url)
                return self
            except urllib.error.HTTPError as e:
                last = e.read()[:300]
            except (TimeoutError, OSError) as e:
                last = str(e)
            time.sleep(10)
        sys.exit(f"no callback URL for the test kit: {last}")

    def __exit__(self, *exc):
        if not self.keep_on:
            off()


    def call(self, action, **kw):
        body = json.dumps(dict(action=action, **kw)).encode()
        for attempt in range(3):
            req = urllib.request.Request(self.url, data=body, method="POST", headers={"Content-Type": "application/json"})
            try:
                with urllib.request.urlopen(req, timeout=180) as r:
                    raw = r.read()
                    return json.loads(raw) if raw else {}
            except urllib.error.HTTPError as e:
                err = f"HTTP {e.code} {e.read()[:800]}"
                if e.code < 500:
                    break
            except (TimeoutError, OSError) as e:
                err = str(e)
            time.sleep(15)
        raise RuntimeError(f"kit {action}: {err}")


def run_flow(name, trigger):
    """Start a scheduled flow now (like 'Run' in Power Automate), e.g. run_flow("DealOS | Mailbox sync", "Every_3_minutes")."""
    url = (f"https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/environments/{ENV}/flows/{flow_id(name)}"
           f"/triggers/{trigger}/run?api-version=2016-11-01")
    req = urllib.request.Request(url, data=b"{}", method="POST", headers={"Authorization": "Bearer " + flow_token(), "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code
    except (TimeoutError, OSError):
        return 0


def off():
    """Switch the kit flow off (its URL lets anyone who has it write into the mailbox)."""
    dv.request("PATCH", f"workflows({KIT})", {"statecode": 0, "statuscode": 1}, timeout=300)

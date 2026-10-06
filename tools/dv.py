"""Minimal Dataverse Web API client for local dev/testing.

Auth: OAuth device-code flow (Azure CLI public client), refresh token cached in
.dv_token.json at the repo root (gitignored, mode 600).

Usage:
  python3 tools/dv.py login              # browser sign-in (auth code + PKCE on localhost; works with security defaults / MFA)
  python3 tools/dv.py login --device     # device-code sign-in (blocked by tenant security defaults)
  python3 tools/dv.py get  "<path>"      # e.g. gc_listings?$top=1
  python3 tools/dv.py post "<path>" '<json>'
"""
import base64, hashlib, http.server, json, os, secrets, sys, time, urllib.parse, urllib.request, urllib.error, webbrowser

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOKEN_FILE = os.path.join(ROOT, ".dv_token.json")
ORG = os.environ.get("DV_URL", "https://org61da3071.crm8.dynamics.com").rstrip("/")
TENANT = os.environ.get("DV_TENANT", "gigacoreenergypvtltd.onmicrosoft.com")
CLIENT_ID = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"  # Azure CLI public client
SCOPE = f"{ORG}/.default offline_access"
AUTH = f"https://login.microsoftonline.com/{TENANT}/oauth2/v2.0"


def _post_form(url, data):
    req = urllib.request.Request(url, data=urllib.parse.urlencode(data).encode(), method="POST")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        return json.load(e)


def _save(tok):
    tok["expires_at"] = time.time() + int(tok.get("expires_in", 3600)) - 120
    fd = os.open(TOKEN_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        json.dump(tok, f)


def login_browser(timeout=300):
    """Authorization code + PKCE with a localhost redirect, as `az login` does. Security defaults allow it (with MFA)."""
    verifier = secrets.token_urlsafe(64)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    state = secrets.token_urlsafe(16)
    result = {}

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            if q.get("state", [None])[0] == state:
                result.update({k: v[0] for k, v in q.items()})
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.end_headers()
            self.wfile.write(b"<p>DealOS dev sign-in finished. You can close this tab.</p>")

        def log_message(self, *args):
            pass

    server = http.server.HTTPServer(("localhost", 0), Handler)
    server.timeout = 5
    redirect = f"http://localhost:{server.server_port}"
    url = f"{AUTH}/authorize?" + urllib.parse.urlencode({
        "client_id": CLIENT_ID, "response_type": "code", "redirect_uri": redirect, "scope": SCOPE, "state": state,
        "code_challenge": challenge, "code_challenge_method": "S256", "prompt": "select_account"})
    print("Opening the browser to sign in. If it does not open, visit:\n" + url, flush=True)
    webbrowser.open(url)
    deadline = time.time() + timeout
    while not result and time.time() < deadline:
        server.handle_request()
    server.server_close()
    if "code" not in result:
        sys.exit(f"sign-in failed: {result.get('error', 'timed out')}: {result.get('error_description', '')}")
    tok = _post_form(f"{AUTH}/token", {"grant_type": "authorization_code", "client_id": CLIENT_ID, "code": result["code"],
                                       "redirect_uri": redirect, "code_verifier": verifier, "scope": SCOPE})
    if "access_token" not in tok:
        sys.exit(f"token exchange failed: {tok.get('error')}: {tok.get('error_description')}")
    _save(tok)
    print("SIGNED IN", flush=True)


def login(poll_seconds=900):
    dc = _post_form(f"{AUTH}/devicecode", {"client_id": CLIENT_ID, "scope": SCOPE})
    if "device_code" not in dc:
        sys.exit(f"device code request failed: {dc}")
    print(dc["message"], flush=True)
    deadline = time.time() + min(poll_seconds, int(dc.get("expires_in", 900)))
    while time.time() < deadline:
        time.sleep(int(dc.get("interval", 5)))
        tok = _post_form(f"{AUTH}/token", {
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
            "client_id": CLIENT_ID, "device_code": dc["device_code"]})
        if "access_token" in tok:
            _save(tok)
            print("SIGNED IN", flush=True)
            return
        if tok.get("error") not in ("authorization_pending", "slow_down"):
            sys.exit(f"sign-in failed: {tok.get('error')}: {tok.get('error_description')}")
    sys.exit("sign-in timed out")


def token():
    if not os.path.exists(TOKEN_FILE):
        sys.exit("not signed in: run `python3 tools/dv.py login`")
    with open(TOKEN_FILE) as f:
        tok = json.load(f)
    if time.time() < tok.get("expires_at", 0):
        return tok["access_token"]
    new = _post_form(f"{AUTH}/token", {"grant_type": "refresh_token", "client_id": CLIENT_ID,
                                       "refresh_token": tok["refresh_token"], "scope": SCOPE})
    if "access_token" not in new:
        sys.exit(f"token refresh failed: {new.get('error_description')}\nSign in again: python3 tools/dv.py login")
    new.setdefault("refresh_token", tok["refresh_token"])
    _save(new)
    return new["access_token"]


def request(method, path, body=None, headers=None, timeout=180):
    url = path if path.startswith("http") else f"{ORG}/api/data/v9.2/{path.lstrip('/')}"
    url = urllib.parse.quote(url, safe=":/?&=$,()'@*+;%-._~")
    h = {"Authorization": f"Bearer {token()}", "Accept": "application/json",
         "OData-MaxVersion": "4.0", "OData-Version": "4.0",
         "Prefer": 'odata.include-annotations="*",return=representation'}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        h["Content-Type"] = "application/json; charset=utf-8"
    h.update(headers or {})
    req = urllib.request.Request(url, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw else {})
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, {"raw": raw.decode(errors="replace")}


def get(path):
    return request("GET", path)


def post(path, body):
    return request("POST", path, body)


def patch(path, body):
    return request("PATCH", path, body)


def delete(path):
    return request("DELETE", path)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "help"
    if cmd == "login":
        login() if "--device" in sys.argv else login_browser()
    elif cmd in ("get", "delete"):
        s, b = request(cmd.upper(), sys.argv[2])
        print(s); print(json.dumps(b, indent=2)[:20000])
    elif cmd in ("post", "patch"):
        s, b = request(cmd.upper(), sys.argv[2], json.loads(sys.argv[3]) if len(sys.argv) > 3 else {})
        print(s); print(json.dumps(b, indent=2)[:20000])
    else:
        print(__doc__)

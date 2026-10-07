"""Deploy the DealOS Gmail custom connector (idempotent) and its connection reference gc_gmail.

The connector signs in with our own Google OAuth app (identity provider Google, scope gmail.modify), so a consumer
@gmail.com account can be used in flows next to Dataverse. The OAuth client id comes from .env (GMAIL_CLIENT_ID) or
--client-id. The client secret is never handled here: paste it once in the maker portal (connector → Security → Update).

Usage:
  python3 tools/deploy_connector.py                    # create or update the connector + connection reference
  python3 tools/deploy_connector.py --client-id <id>   # same, with the Google OAuth client id
  python3 tools/deploy_connector.py bind               # attach the Gmail connection you created to gc_gmail
  python3 tools/deploy_connector.py status             # show connector, redirect URL and connection reference

After the first deploy the connector's API name is written to tools/flows/connectors.json; the flow definitions read it.
"""
import json, os, sys, urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SWAGGER = os.path.join(ROOT, "tools", "connectors", "gmail.swagger.json")
NAMES = os.path.join(ROOT, "tools", "flows", "connectors.json")
SOL = {"MSCRM.SolutionUniqueName": "DealOS"}
NAME, DISPLAY, REF = "gc_dealosgmail", "DealOS Gmail", "gc_gmail"
ENV_ID = os.environ.get("PP_ENV_ID", "b77eedc7-f980-e3bb-a07e-757db98002d2")
SCOPE = "https://www.googleapis.com/auth/gmail.modify"


def ok(status, body, what):
    if status >= 300:
        sys.exit(f"FAILED {what}: {status} {json.dumps(body)[:1500]}")
    return body


def client_id():
    if "--client-id" in sys.argv:
        return sys.argv[sys.argv.index("--client-id") + 1]
    path = os.path.join(ROOT, ".env")
    if os.path.exists(path):
        for line in open(path):
            if line.startswith("GMAIL_CLIENT_ID="):
                return line.split("=", 1)[1].strip() or None
    return None


def connection_parameters(cid, redirect=None):
    return {"token": {"type": "oauthSetting", "oAuthSettings": {
        "identityProvider": "google", "clientId": cid, "scopes": [SCOPE], "redirectMode": "GlobalPerConnector",
        "redirectUrl": redirect or "https://global.consent.azure-apim.net/redirect",
        "properties": {"IsFirstParty": "False", "IsOnbehalfofLoginSupported": False}, "customParameters": {}},
        "uiDefinition": {"displayName": "Sign in with Google", "description": "Sign in with the Gmail account the Email Desk reads.",
                         "tooltip": "The DealOS trading mailbox", "constraints": {"required": "true"}}}}


def find():
    rows = ok(*dv.get(f"connectors?$select=connectorid,connectorinternalid,connectionparameters&$filter=name eq '{NAME}'"), "read connector").get("value", [])
    return rows[0] if rows else None


def deploy():
    cid = client_id()
    with open(SWAGGER) as f:
        swagger = f.read()
    row = find()
    if row:
        current = json.loads(row.get("connectionparameters") or "{}").get("token", {}).get("oAuthSettings", {})
        body = {"openapidefinition": swagger}
        if cid and cid != current.get("clientId"):
            body["connectionparameters"] = json.dumps(connection_parameters(cid, current.get("redirectUrl")))
        ok(*dv.patch(f"connectors({row['connectorid']})", body), "update connector")
        print(f"= connector {DISPLAY}" + (" (client id updated)" if "connectionparameters" in body else ""))
    else:
        ok(*dv.request("POST", "connectors", {
            "name": NAME, "displayname": DISPLAY, "connectortype": 1, "iconbrandcolor": "#c5221f",
            "description": "Gmail for the DealOS Email Desk (own Google OAuth app).",
            "openapidefinition": swagger, "connectionparameters": json.dumps(connection_parameters(cid or "set-the-google-client-id"))}, SOL), "create connector")
        print(f"+ connector {DISPLAY}")
    row = find()
    api = row["connectorinternalid"]
    # Creating with the solution header does not add a connector to the solution; without it the solution can't be exported.
    ok(*dv.request("POST", "AddSolutionComponent", {"ComponentId": row["connectorid"], "ComponentType": 372, "SolutionUniqueName": "DealOS",
                                                     "AddRequiredComponents": False}), "add connector to solution")
    with open(NAMES, "w") as f:
        json.dump({"gmail": api}, f, indent=2)
        f.write("\n")
    refs = ok(*dv.get(f"connectionreferences?$select=connectionreferenceid,connectorid&$filter=connectionreferencelogicalname eq '{REF}'"), "read ref").get("value", [])
    connector_path = f"/providers/Microsoft.PowerApps/apis/{api}"
    if not refs:
        ok(*dv.request("POST", "connectionreferences", {"connectionreferencelogicalname": REF, "connectionreferencedisplayname": DISPLAY,
                                                        "connectorid": connector_path, "description": "Gmail mailbox of the Email Desk"}, SOL), "create ref")
        print(f"+ connection reference {REF}")
    elif refs[0]["connectorid"] != connector_path:
        ok(*dv.patch(f"connectionreferences({refs[0]['connectionreferenceid']})", {"connectorid": connector_path}), "update ref")
        print(f"= connection reference {REF} (connector updated)")
    else:
        print(f"= connection reference {REF}")
    status()
    if not cid:
        print("\nNo Google client id yet: add GMAIL_CLIENT_ID=... to .env (or pass --client-id) and run this again.")


def status():
    row = find()
    if not row:
        print("connector not deployed")
        return
    o = json.loads(row.get("connectionparameters") or "{}").get("token", {}).get("oAuthSettings", {})
    print(f"connector api name : {row['connectorinternalid']}")
    print(f"google client id   : {o.get('clientId')}")
    print(f"redirect URI       : {o.get('redirectUrl')}  ← add this to the Google OAuth client's authorised redirect URIs")
    refs = dv.get(f"connectionreferences?$select=connectionid&$filter=connectionreferencelogicalname eq '{REF}'")[1].get("value", [])
    print(f"connection ({REF}) : {refs[0].get('connectionid') if refs else None}")


def powerapps_token():
    """Exchanges the cached Dataverse refresh token for a Power Apps API token (same tenant, same public client)."""
    with open(dv.TOKEN_FILE) as f:
        tok = json.load(f)
    new = dv._post_form(f"{dv.AUTH}/token", {"grant_type": "refresh_token", "client_id": dv.CLIENT_ID, "refresh_token": tok["refresh_token"],
                                              "scope": "https://service.powerapps.com/.default offline_access"})
    if "access_token" not in new:
        sys.exit(f"token exchange failed: {new.get('error_description')}")
    return new["access_token"]


def bind():
    row = find() or sys.exit("deploy the connector first")
    api = row["connectorinternalid"]
    url = (f"https://api.powerapps.com/providers/Microsoft.PowerApps/apis/{api}/connections"
           f"?api-version=2016-11-01&$filter=environment%20eq%20%27{ENV_ID}%27")
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {powerapps_token()}", "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        conns = json.load(r).get("value", [])
    good = [c for c in conns if any(s.get("status") == "Connected" for s in c.get("properties", {}).get("statuses", []))]
    for c in conns:
        p = c.get("properties", {})
        print(f"  {c['name']}  {p.get('displayName')}  {[s.get('status') for s in p.get('statuses', [])]}")
    if not good:
        sys.exit("No connected DealOS Gmail connection yet. Create one in Power Automate → Connections → New connection → DealOS Gmail.")
    ref = dv.get(f"connectionreferences?$select=connectionreferenceid&$filter=connectionreferencelogicalname eq '{REF}'")[1]["value"][0]
    ok(*dv.patch(f"connectionreferences({ref['connectionreferenceid']})", {"connectionid": good[0]["name"]}), "bind")
    print(f"= {REF} → {good[0]['name']} ({good[0]['properties'].get('displayName')})")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else "deploy"
    {"deploy": deploy, "bind": bind, "status": status}.get(cmd, lambda: print(__doc__))()

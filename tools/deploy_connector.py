"""Deploy the DealOS Gmail custom connector (idempotent) and its connection reference gc_gmail.

The connector signs in with our own Google OAuth app (identity provider Google, scope gmail.modify), so a consumer
@gmail.com account can be used in flows next to Dataverse. The OAuth client id comes from .env (GMAIL_CLIENT_ID) or
--client-id. The client secret is never handled here: paste it once in the maker portal (connector → Security → Update).

Usage:
  python3 tools/deploy_connector.py                    # create or update the connector + connection reference
  python3 tools/deploy_connector.py --client-id <id>   # same, with the Google OAuth client id
  python3 tools/deploy_connector.py bind               # attach the Gmail connection you created to gc_gmail
  python3 tools/deploy_connector.py status             # show connector, redirect URL and connection reference
  python3 tools/deploy_connector.py docusign [--prod]  # e-signature: connection reference gc_docusign (DocuSign Demo, or production)
  python3 tools/deploy_connector.py docusign-bind      # attach the DocuSign connection you created to gc_docusign
  python3 tools/deploy_connector.py registries         # KYB registers: DealOS GLEIF (no key) and DealOS Companies House
                                                       # (COMPANIES_HOUSE_API_KEY in .env), connections created and bound

After the first deploy the connector's API name is written to tools/flows/connectors.json; the flow definitions read it.
"""
import json, os, sys, urllib.error, urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SWAGGER = os.path.join(ROOT, "tools", "connectors", "gmail.swagger.json")
NAMES = os.path.join(ROOT, "tools", "flows", "connectors.json")
SOL = {"MSCRM.SolutionUniqueName": "DealOS"}
NAME, DISPLAY, REF = "gc_dealosgmail", "DealOS Gmail", "gc_gmail"
DOCUSIGN_REF = "gc_docusign"
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
    save_name("gmail", api)
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


def save_name(key, api):
    names = json.load(open(NAMES)) if os.path.exists(NAMES) else {}
    names[key] = api
    with open(NAMES, "w") as f:
        json.dump(names, f, indent=2)
        f.write("\n")


def ensure_reference(ref, display, api, description):
    connector_path = f"/providers/Microsoft.PowerApps/apis/{api}"
    refs = ok(*dv.get(f"connectionreferences?$select=connectionreferenceid,connectorid&$filter=connectionreferencelogicalname eq '{ref}'"), "read ref").get("value", [])
    if not refs:
        ok(*dv.request("POST", "connectionreferences", {"connectionreferencelogicalname": ref, "connectionreferencedisplayname": display,
                                                        "connectorid": connector_path, "description": description}, SOL), "create ref")
        print(f"+ connection reference {ref} ({api})")
    elif refs[0]["connectorid"] != connector_path:
        # Switching sandbox ↔ production: the reference points at the other connector and loses its connection until bound again.
        ok(*dv.patch(f"connectionreferences({refs[0]['connectionreferenceid']})", {"connectorid": connector_path, "connectionid": None}), "update ref")
        print(f"= connection reference {ref} now uses {api} (bind a connection again)")
    else:
        print(f"= connection reference {ref} ({api})")


def docusign():
    """E-signature: connection reference gc_docusign on Microsoft's DocuSign connector (Demo = sandbox; --prod = production)."""
    api = "shared_docusign" if "--prod" in sys.argv else "shared_docusigndemo"
    save_name("docusign", api)
    ensure_reference(DOCUSIGN_REF, "DealOS DocuSign", api, "DocuSign account that sends the contracts for e-signature")
    print(f"Next: Power Automate → Connections → New connection → {'Docusign' if api == 'shared_docusign' else 'Docusign Demo'} (sign in), then:\n"
          "  python3 tools/deploy_connector.py docusign-bind\n  python3 tools/deploy_flows.py --esign")


def docusign_bind():
    names = json.load(open(NAMES)) if os.path.exists(NAMES) else {}
    bind_connection(names.get("docusign", "shared_docusigndemo"), DOCUSIGN_REF, "Docusign")


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
    bind_connection(row["connectorinternalid"], REF, DISPLAY)


def bind_connection(api, ref_name, display):
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
        sys.exit(f"No connected {display} connection yet. Create one in Power Automate → Connections → New connection → {display}.")
    ref = dv.get(f"connectionreferences?$select=connectionreferenceid&$filter=connectionreferencelogicalname eq '{ref_name}'")[1]["value"][0]
    ok(*dv.patch(f"connectionreferences({ref['connectionreferenceid']})", {"connectionid": good[0]["name"]}), "bind")
    print(f"= {ref_name} → {good[0]['name']} ({good[0]['properties'].get('displayName')})")


# ---------------------------------------------------------------- KYB registers (custom connectors without OAuth)

REGISTRIES = [
    # key, connector name, display, swagger, connection reference, colour, description
    ("gleif", "gc_dealosgleif", "DealOS GLEIF", "gleif.swagger.json", "gc_gleif", "#0b5394", "GLEIF LEI register (free, no key): company registry check for KYB."),
    ("companieshouse", "gc_dealoscompanieshouse", "DealOS Companies House", "companieshouse.swagger.json", "gc_companieshouse", "#1d3c34",
     "UK Companies House public data API (free key): profile, officers and owners for KYB."),
]


def env_value(key):
    path = os.path.join(ROOT, ".env")
    if os.path.exists(path):
        for line in open(path):
            if line.startswith(key + "="):
                return line.split("=", 1)[1].strip() or None
    return os.environ.get(key)


def api_key_parameters():
    return {"api_key": {"type": "securestring", "uiDefinition": {
        "displayName": "API key", "description": "Companies House: 'Basic ' + base64 of '<your key>:' (deploy_connector.py registries does this from .env)",
        "tooltip": "Companies House REST API key", "constraints": {"tabIndex": 2, "clearText": False, "required": "true"}}}}


def deploy_api_connector(key, name, display, swagger_file, ref, color, description):
    swagger = open(os.path.join(ROOT, "tools", "connectors", swagger_file)).read()
    params = api_key_parameters() if "securityDefinitions" in json.loads(swagger) else {}
    rows = ok(*dv.get(f"connectors?$select=connectorid,connectorinternalid&$filter=name eq '{name}'"), "read connector").get("value", [])
    if rows:
        ok(*dv.patch(f"connectors({rows[0]['connectorid']})", {"openapidefinition": swagger, "connectionparameters": json.dumps(params)}), "update " + name)
        print(f"= connector {display}")
    else:
        ok(*dv.request("POST", "connectors", {"name": name, "displayname": display, "connectortype": 1, "iconbrandcolor": color, "description": description,
                                              "openapidefinition": swagger, "connectionparameters": json.dumps(params)}, SOL), "create " + name)
        print(f"+ connector {display}")
    row = ok(*dv.get(f"connectors?$select=connectorid,connectorinternalid&$filter=name eq '{name}'"), "read connector")["value"][0]
    ok(*dv.request("POST", "AddSolutionComponent", {"ComponentId": row["connectorid"], "ComponentType": 372, "SolutionUniqueName": "DealOS",
                                                     "AddRequiredComponents": False}), "add connector to solution")
    api = row["connectorinternalid"]
    save_name(key, api)
    ensure_reference(ref, display, api, description)
    return api


def create_connection(api, display, parameters):
    """A connection made from code (no sign-in needed: no auth, or an API key)."""
    import uuid
    body = {"properties": {"environment": {"id": f"/providers/Microsoft.PowerApps/environments/{ENV_ID}", "name": ENV_ID},
                           "displayName": display, "connectionParameters": parameters}}
    url = (f"https://api.powerapps.com/providers/Microsoft.PowerApps/apis/{api}/connections/{uuid.uuid4().hex}"
           f"?api-version=2016-11-01&$filter=environment%20eq%20%27{ENV_ID}%27")
    req = urllib.request.Request(url, data=json.dumps(body).encode(), method="PUT",
                                 headers={"Authorization": f"Bearer {powerapps_token()}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            print(f"+ connection {display}")
            return json.load(r)
    except urllib.error.HTTPError as e:
        sys.exit(f"FAILED connection {display}: {e.code} {e.read().decode()[:800]}")


def connections_of(api):
    url = (f"https://api.powerapps.com/providers/Microsoft.PowerApps/apis/{api}/connections"
           f"?api-version=2016-11-01&$filter=environment%20eq%20%27{ENV_ID}%27")
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {powerapps_token()}", "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r).get("value", [])


def registries():
    import base64
    for key, name, display, swagger, ref, color, description in REGISTRIES:
        api = deploy_api_connector(key, name, display, swagger, ref, color, description)
        if not connections_of(api):
            if key == "gleif":
                create_connection(api, display, {})
            else:
                k = env_value("COMPANIES_HOUSE_API_KEY")
                if not k:
                    print("  No COMPANIES_HOUSE_API_KEY in .env: get a free key at https://developer.company-information.service.gov.uk "
                          "(create an application, REST API key), add it to .env and run this again. Until then UK companies are checked in GLEIF only.")
                    continue
                create_connection(api, display, {"api_key": "Basic " + base64.b64encode((k + ":").encode()).decode()})
        bind_connection(api, ref, display)
        if key == "companieshouse":
            save_name("companieshouse_connected", "yes")   # the flows include the Companies House steps from now on
            print("  Switch it on: setting registry.companies_house = on, then deploy_flows.py --only \"Party onboarding\" and --only \"Party re-check\"")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else "deploy"
    {"deploy": deploy, "bind": bind, "status": status, "docusign": docusign, "docusign-bind": docusign_bind, "registries": registries}.get(cmd, lambda: print(__doc__))()

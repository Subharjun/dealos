"""Our company profile PDF, which the desk attaches when a buyer or seller asks for it.

  python3 tools/company_profile.py set <profile.pdf>   upload it (a gc_document) and point desk.company_profile at it
  python3 tools/company_profile.py show                which document is on file
  python3 tools/company_profile.py clear               stop attaching it (the desk opens a task instead)

Uploading a new file replaces the old one for future emails; drafts already written keep the file they had.
"""
import hashlib, json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

KEY = "desk.company_profile"
B = 303300000
PARSED, OTHER = B + 1, B + 27


def setting():
    rows = dv.get(f"gc_platformsettings?$select=gc_platformsettingid,gc_value&$filter=gc_key eq '{KEY}'")[1].get("value", [])
    if not rows:
        sys.exit(f"Setting {KEY} is missing: run python3 tools/deploy_agents.py first.")
    return rows[0]


def upload(doc_id, name, data):
    """Dataverse file column upload in blocks (InitializeFileBlocksUpload / UploadBlock / CommitFileBlocksUpload)."""
    import base64
    s, b = dv.request("POST", "InitializeFileBlocksUpload", {"Target": {"@odata.type": "Microsoft.Dynamics.CRM.gc_document", "gc_documentid": doc_id},
                                                            "FileAttributeName": "gc_file", "FileName": name})
    if s >= 300:
        sys.exit(f"upload init failed: {json.dumps(b)[:400]}")
    token, blocks, size = b["FileContinuationToken"], [], 4 * 1024 * 1024
    for i in range(0, len(data), size):
        block = base64.b64encode(f"{i:012d}".encode()).decode()
        s, b = dv.request("POST", "UploadBlock", {"BlockId": block, "BlockData": base64.b64encode(data[i:i + size]).decode(), "FileContinuationToken": token})
        if s >= 300:
            sys.exit(f"upload failed: {json.dumps(b)[:400]}")
        blocks.append(block)
    s, b = dv.request("POST", "CommitFileBlocksUpload", {"FileName": name, "MimeType": "application/pdf", "BlockList": blocks, "FileContinuationToken": token})
    if s >= 300:
        sys.exit(f"upload commit failed: {json.dumps(b)[:400]}")


def set_profile(path):
    data = open(path, "rb").read()
    if not data.startswith(b"%PDF"):
        sys.exit(f"{path} is not a PDF.")
    if len(data) > 10 * 1024 * 1024:
        sys.exit("Keep the profile under 10 MB (it goes out as an email attachment).")
    sha = hashlib.sha256(data).hexdigest()
    name = os.path.basename(path)
    rows = dv.get(f"gc_documents?$select=gc_documentid&$filter=gc_sha256 eq '{sha}'")[1].get("value", [])
    if rows:
        doc_id = rows[0]["gc_documentid"]
        print(f"= this file is already stored ({doc_id})")
    else:
        s, b = dv.request("POST", "gc_documents", {"gc_name": "Company profile (ours)", "gc_filename": name, "gc_mimetype": "application/pdf",
                                                    "gc_sizebytes": len(data), "gc_sha256": sha, "gc_parsestatus": PARSED, "gc_doctype": OTHER},
                          {"Prefer": "return=representation"})
        if s >= 300:
            sys.exit(f"document not created: {json.dumps(b)[:400]}")
        doc_id = b["gc_documentid"]
        upload(doc_id, name, data)
        print(f"+ uploaded {name} ({len(data) // 1024} KB) as {doc_id}")
    dv.patch(f"gc_platformsettings({setting()['gc_platformsettingid']})", {"gc_value": doc_id})
    print(f"= {KEY} = {doc_id}: the desk now attaches it when a party asks for our company profile.")


def show():
    v = (setting().get("gc_value") or "").strip()
    if not v:
        print("No company profile on file: the desk opens a task when a party asks for it.")
        return
    s, b = dv.get(f"gc_documents({v})?$select=gc_filename,gc_sizebytes,createdon")
    print(f"{KEY} = {v}: " + (f"{b['gc_filename']} ({(b.get('gc_sizebytes') or 0) // 1024} KB, {b['createdon'][:10]})" if s == 200 else "document not found"))


def clear():
    dv.patch(f"gc_platformsettings({setting()['gc_platformsettingid']})", {"gc_value": ""})
    print(f"= {KEY} cleared")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "set":
        set_profile(sys.argv[2])
    elif sys.argv[1:] == ["show"]:
        show()
    elif sys.argv[1:] == ["clear"]:
        clear()
    else:
        print(__doc__)

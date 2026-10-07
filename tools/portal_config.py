"""Generate the marketplace site's table permissions and Web API site settings (security as code).

Writes YAML into portal/.powerpages-site/ (created by the first `pac pages upload-code-site`):
  table-permissions/DealOS-*.tablepermission.yml   who may read / create / write which rows
  site-settings/Webapi-<table>-*.sitesetting.yml   which tables and columns the Web API exposes
Ids are derived from names (uuid5), so re-running rewrites the same files; delete a file here to drop a permission.

Security model (see docs/PORTAL.md):
  * reads are limited by these permissions (own company, own contact, or the public masked catalog);
  * every write is also checked in Dataverse by the PortalGuardPlugin (ownership, protected columns, status changes).

Usage:
  python3 tools/portal_config.py            # write the files
  cd portal && npm run build && pac pages upload-code-site --rootPath .
"""
import glob
import os
import re
import sys
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SITE = os.path.join(ROOT, "portal", ".powerpages-site")
NS = uuid.UUID("0c0f9a52-2d1e-4b8e-9a6e-5f1d7d3c2b10")
SCOPE = {"Global": 756150000, "Contact": 756150001, "Account": 756150002, "Parent": 756150003, "Self": 756150004}

# name, table, scope, roles, privileges, relationship (Contact/Account/Parent), parent permission name
PERMISSIONS = [
    ("Catalog - public read", "gc_catalogentry", "Global", ["anon", "auth"], "read", None, None),
    ("Commodities - read", "gc_commodity", "Global", ["auth"], "read appendto", None, None),
    ("Countries - read", "gc_country", "Global", ["auth"], "read appendto", None, None),
    ("Contact - self", "contact", "Self", ["auth"], "read write append appendto", None, None),
    ("Company - create", "account", "Global", ["auth"], "create append", None, None),
    ("Company - own", "account", "Contact", ["auth"], "read write append appendto", "account_primary_contact", None),
    ("Listings - seller", "gc_listing", "Account", ["auth"], "create read write delete append appendto", "gc_listing_gc_seller", None),
    ("Listings - link published", "gc_listing", "Global", ["auth"], "appendto", None, None),
    ("Listing facts - seller", "gc_fact", "Parent", ["auth"], "read", "gc_fact_gc_listing", "Listings - seller"),
    ("Listing questions - seller", "gc_question", "Parent", ["auth"], "read", "gc_question_gc_listing", "Listings - seller"),
    ("Documents - company", "gc_document", "Account", ["auth"], "create read write delete append", "gc_document_gc_account", None),
    ("RFQs - buyer", "gc_buyerrequirement", "Account", ["auth"], "create read write delete append appendto", "gc_buyerrequirement_gc_buyer", None),
    ("Matches - buyer", "gc_match", "Parent", ["auth"], "read write", "gc_match_gc_requirement", "RFQs - buyer"),
    ("Matches - seller", "gc_match", "Parent", ["auth"], "read write", "gc_match_gc_listing", "Listings - seller"),
    ("RFQ invites - buyer", "gc_rfqinvite", "Parent", ["auth"], "create read write append", "gc_buyerrequirement_gc_rfqinvite_gc_requirement", "RFQs - buyer"),
    ("RFQ invites - seller", "gc_rfqinvite", "Account", ["auth"], "read write", "gc_account_gc_rfqinvite_gc_seller", None),
    ("Deals - buyer", "gc_deal", "Account", ["auth"], "read appendto", "gc_deal_gc_buyer", None),
    ("Deals - seller", "gc_deal", "Account", ["auth"], "read appendto", "gc_deal_gc_seller", None),
    ("Offers - buyer deals", "gc_offer", "Parent", ["auth"], "read", "gc_offer_gc_deal", "Deals - buyer"),
    ("Offers - seller deals", "gc_offer", "Parent", ["auth"], "read", "gc_offer_gc_deal", "Deals - seller"),
    ("Ratings - buyer deals", "gc_rating", "Parent", ["auth"], "create append", "gc_deal_gc_rating_gc_deal", "Deals - buyer"),
    ("Ratings - seller deals", "gc_rating", "Parent", ["auth"], "create append", "gc_deal_gc_rating_gc_deal", "Deals - seller"),
    ("Disputes - buyer deals", "gc_dispute", "Parent", ["auth"], "create read append", "gc_deal_gc_dispute_gc_deal", "Deals - buyer"),
    ("Disputes - seller deals", "gc_dispute", "Parent", ["auth"], "create read append", "gc_deal_gc_dispute_gc_deal", "Deals - seller"),
    ("Conversations - own", "gc_conversation", "Contact", ["auth"], "create read append appendto", "gc_contact_gc_conversation_gc_contact", None),
    ("Messages - own conversations", "gc_message", "Parent", ["auth"], "create read append", "gc_message_gc_conversation", "Conversations - own"),
]

# Web API column allowlist per table: logical names for values, _x_value for lookup reads, navigation property for @odata.bind writes.
FIELDS = {
    "gc_catalogentry": "gc_catalogentryid,gc_name,gc_commodity,gc_family,gc_form,gc_grade,gc_quantity,gc_unit,gc_availablequantity,gc_askprice,gc_currency,"
                       "gc_pricebasis,gc_incoterm,gc_namedplace,gc_origincountry,gc_badge,gc_publishedon,gc_facts,_gc_listing_value",
    "gc_commodity": "gc_commodityid,gc_name",
    "gc_country": "gc_countryid,gc_name",
    "contact": "contactid,firstname,lastname,_parentcustomerid_value,parentcustomerid_account",
    "account": "accountid,name,gc_kybstatus,gc_trusttier,gc_partyrole,gc_registrationnumber,primarycontactid,gc_country",
    "gc_listing": "gc_listingid,gc_name,gc_status,gc_badge,gc_quantity,gc_quantityunit,gc_askprice,gc_currency,gc_incoterm,gc_namedplace,gc_grade,"
                  "createdon,gc_commodity,gc_origincountry,gc_seller",
    "gc_fact": "gc_factid,gc_attributekey,gc_displayvalue,gc_status,_gc_listing_value",
    "gc_question": "gc_questionid,gc_name,gc_askedon,_gc_answeredby_value,_gc_listing_value",
    "gc_document": "gc_documentid,gc_filename,gc_name,gc_doctype,gc_parsestatus,gc_mimetype,gc_file,createdon,_gc_listing_value,_gc_deal_value,gc_listing,gc_account",
    "gc_buyerrequirement": "gc_buyerrequirementid,gc_name,gc_status,gc_quantity,gc_quantityunit,gc_incoterm,gc_targetprice,gc_currency,gc_specification,"
                           "gc_destinationport,gc_validuntil,gc_inspectionrequired,createdon,gc_commodity,gc_destinationcountry,gc_buyer",
    "gc_match": "gc_matchid,gc_name,gc_score,gc_status,gc_buyeroptin,gc_selleroptin,gc_explanation,_gc_listing_value,_gc_requirement_value",
    "gc_rfqinvite": "gc_rfqinviteid,gc_name,gc_status,gc_invitedon,gc_respondedon,gc_buyernote,gc_declinereason,_gc_listing_value,_gc_requirement_value,"
                    "gc_Requirement,gc_Listing",
    "gc_deal": "gc_dealid,gc_name,gc_dealnumber,gc_stage,gc_statusoverlay,gc_quantity,gc_quantityunit,gc_price,gc_currency,gc_incoterm,gc_namedplace,modifiedon",
    "gc_offer": "gc_offerid,gc_price,gc_quantity,gc_currency,gc_incoterm,gc_status,gc_round,gc_validuntil,_gc_deal_value",
    "gc_rating": "gc_ratingid,gc_name,gc_score,gc_comment,gc_Deal",
    "gc_dispute": "gc_disputeid,gc_name,gc_reason,gc_description,gc_status,gc_Deal",
    "gc_conversation": "gc_conversationid,gc_name,gc_assistant,createdon,gc_Contact",
    "gc_message": "gc_messageid,gc_name,gc_text,gc_isplatform,gc_senderlabel,gc_attachments,createdon,_gc_conversation_value,gc_conversation",
}

EXTRA_SETTINGS = [
    ("Webapi/error/innererror", True, "Detailed Web API errors (Dev only; turn off before go-live)."),
]


def pid(name):
    return str(uuid.uuid5(NS, "perm:" + name))


def sid(name):
    return str(uuid.uuid5(NS, "setting:" + name))


def yaml_value(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, int):
        return str(v)
    s = str(v)
    if s == "" or s in ("true", "false", "null") or re.search(r"[:#{}\[\],&*?|<>=!%@`]", s) or s[0] in "-'\"" or s != s.strip():
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'
    return s


def roles():
    out = {}
    for path in glob.glob(os.path.join(SITE, "web-roles", "*.webrole.yml")):
        data = dict(line.split(": ", 1) for line in open(path).read().splitlines() if ": " in line)
        if data.get("anonymoususersrole") == "true":
            out["anon"] = data["id"]
        if data.get("authenticatedusersrole") == "true":
            out["auth"] = data["id"]
    if set(out) != {"anon", "auth"}:
        sys.exit("Anonymous / Authenticated web roles not found; upload the site once first.")
    return out


def write(path, fields, role_ids=None):
    lines = []
    for k in sorted(list(fields) + (["adx_entitypermission_webrole"] if role_ids else [])):
        if k == "adx_entitypermission_webrole":
            lines.append("adx_entitypermission_webrole:")
            lines += [f"- {r}" for r in role_ids]
        else:
            lines.append(f"{k}: {yaml_value(fields[k])}")
    with open(path, "w") as f:
        f.write("\n".join(lines) + "\n")


def main():
    if not os.path.isdir(SITE):
        sys.exit("portal/.powerpages-site not found; run `pac pages upload-code-site --rootPath portal` once first.")
    role = roles()
    tp_dir = os.path.join(SITE, "table-permissions")
    ss_dir = os.path.join(SITE, "site-settings")
    os.makedirs(tp_dir, exist_ok=True)
    for old in glob.glob(os.path.join(tp_dir, "DealOS-*.tablepermission.yml")):
        os.remove(old)  # regenerated below; removed permissions disappear on the next upload
    names = {p[0] for p in PERMISSIONS}
    for name, table, scope, who, privs, relationship, parent in PERMISSIONS:
        p = privs.split()
        fields = {"entityname": f"DealOS - {name}", "entitylogicalname": table, "id": pid(name), "scope": SCOPE[scope],
                  "read": "read" in p, "create": "create" in p, "write": "write" in p, "delete": "delete" in p,
                  "append": "append" in p, "appendto": "appendto" in p}
        if scope == "Contact":
            fields["contactrelationship"] = relationship
        elif scope == "Account":
            fields["accountrelationship"] = relationship
        elif scope == "Parent":
            assert parent in names, parent
            fields["parententitypermission"] = pid(parent)
            fields["parentrelationship"] = relationship
        write(os.path.join(tp_dir, "DealOS-" + re.sub(r"[\s-]+", "-", name) + ".tablepermission.yml"), fields, [role[w] for w in who])

    settings = []
    for table, columns in FIELDS.items():
        settings.append((f"Webapi/{table}/enabled", True, f"DealOS: Web API access to {table}"))
        settings.append((f"Webapi/{table}/fields", ",".join(sorted(set(columns.split(",")))), f"DealOS: columns of {table} the site may read or write"))
    settings += EXTRA_SETTINGS
    for name, value, desc in settings:
        write(os.path.join(ss_dir, name.replace("/", "-") + ".sitesetting.yml"), {"description": desc, "id": sid(name), "name": name, "value": value})
    print(f"wrote {len(PERMISSIONS)} table permissions and {len(settings)} site settings to {os.path.relpath(SITE, ROOT)}")


if __name__ == "__main__":
    main()

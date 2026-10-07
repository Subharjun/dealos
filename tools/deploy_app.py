"""The deal pipeline in the DealOS Admin app (model-driven), deployed from code (idempotent).

Adds an "Email desk" area at the top of the app with:
  Pipeline   open requirements by desk stage (with a chart), open seller lots, desk deals, contracts out for signature
  To do      decisions waiting for you (with their reply codes), drafts waiting in Gmail
Every list is a system view in the DealOS solution, so it is exported with it.

Usage: python3 tools/deploy_app.py
"""
import json, os, sys, uuid
from xml.sax.saxutils import escape

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

SOL = {"MSCRM.SolutionUniqueName": "DealOS"}
APP = "gc_DealOSAdmin"
B = 303300000
NS = uuid.UUID("0d3c7f5e-2a41-4c9b-9e7d-6f1b2c3a4d5e")

# name, table, otc, filter (FetchXML conditions), columns [(name, width)], order (attribute, descending)
VIEWS = [
    ("Desk pipeline: open requirements", "gc_buyerrequirement", 10620,
     f'<condition attribute="gc_source" operator="eq" value="{B + 1}" /><condition attribute="gc_deskstage" operator="ne" value="{B + 7}" />'
     '<condition attribute="gc_deskstage" operator="not-null" />',
     [("gc_commoditytext", 220), ("gc_deskstage", 120), ("gc_quantity", 90), ("gc_ourprice", 100), ("gc_name", 220), ("modifiedon", 130)], ("gc_deskstage", False)),
    ("Desk pipeline: open seller lots", "gc_sellerlot", 10660, f'<condition attribute="gc_status" operator="eq" value="{B}" />',
     [("gc_commoditytext", 220), ("gc_quantity", 90), ("gc_price", 100), ("gc_window", 140), ("gc_biddeadline", 130), ("modifiedon", 130)], ("modifiedon", True)),
    ("Desk pipeline: deals", "gc_deal", 10622, f'<condition attribute="gc_emaildesk" operator="eq" value="1" /><condition attribute="gc_stage" operator="ne" value="{B + 12}" />',
     [("gc_name", 300), ("gc_stage", 120), ("gc_price", 100), ("gc_buyerprice", 100), ("gc_quantity", 90), ("modifiedon", 130)], ("modifiedon", True)),
    ("Desk pipeline: contracts out for signature", "gc_contract", 10627, f'<condition attribute="gc_status" operator="eq" value="{B + 2}" />',
     [("gc_name", 300), ("gc_esignstatus", 140), ("modifiedon", 130)], ("modifiedon", True)),
    ("Waiting for a decision", "gc_reviewtask", 10646, f'<condition attribute="gc_status" operator="eq" value="{B}" />',
     [("gc_name", 320), ("gc_kind", 100), ("gc_replycode", 90), ("gc_briefedon", 130), ("createdon", 130)], ("createdon", True)),
    ("Drafts waiting in Gmail", "gc_message", 10636,
     f'<condition attribute="gc_direction" operator="eq" value="{B + 2}" /><condition attribute="gc_draftstatus" operator="eq" value="{B}" />'
     '<condition attribute="gc_autosend" operator="eq" value="0" />',
     [("gc_subject", 320), ("gc_toaddress", 200), ("createdon", 130)], ("createdon", True)),
]
CHART = ("Requirements by desk stage", "gc_buyerrequirement", 10620, "gc_deskstage")


def ok(status, body, what):
    if status >= 300:
        sys.exit(f"FAILED {what}: {status} {json.dumps(body)[:1200]}")
    return body


def view_id(name):
    return str(uuid.uuid5(NS, "view:" + name))


def upsert_view(name, table, otc, conditions, columns, order):
    pk = table + "id"
    attrs = "".join(f'<attribute name="{c}" />' for c, _ in columns)
    fetch = (f'<fetch version="1.0" mapping="logical"><entity name="{table}"><attribute name="{pk}" />{attrs}'
             f'<order attribute="{order[0]}" descending="{str(order[1]).lower()}" /><filter type="and">{conditions}</filter></entity></fetch>')
    layout = (f'<grid name="resultset" object="{otc}" jump="{columns[0][0]}" select="1" icon="1" preview="1"><row name="result" id="{pk}">'
              + "".join(f'<cell name="{c}" width="{w}" />' for c, w in columns) + "</row></grid>")
    vid = view_id(name)
    body = {"name": name, "returnedtypecode": table, "fetchxml": fetch, "layoutxml": layout, "querytype": 0,
            "description": "Email desk pipeline (tools/deploy_app.py)."}
    if dv.get(f"savedqueries({vid})?$select=savedqueryid")[0] == 200:
        ok(*dv.patch(f"savedqueries({vid})", body), "update view " + name)
        print(f"= view {name}")
    else:
        ok(*dv.request("POST", "savedqueries", dict(body, savedqueryid=vid), SOL), "create view " + name)
        print(f"+ view {name}")
    return vid


def upsert_chart(name, table, otc, group_by):
    cid = str(uuid.uuid5(NS, "chart:" + name))
    data = (f'<datadefinition><fetchcollection><fetch mapping="logical" aggregate="true"><entity name="{table}">'
            f'<attribute name="{table}id" aggregate="count" alias="count" /><attribute name="{group_by}" groupby="true" alias="stage" />'
            f'</entity></fetch></fetchcollection><categorycollection><category><measurecollection><measure alias="count" /></measurecollection>'
            f'</category></categorycollection></datadefinition>')
    presentation = ('<Chart Palette="None" PaletteCustomColors="91,151,213; 237,125,49; 160,116,166; 255,192,0; 68,114,196; 112,173,71">'
                    '<Series><Series ChartType="Column" IsValueShownAsLabel="True" Font="{0}, 9.5px" LabelForeColor="59, 59, 59" '
                    'CustomProperties="PointWidth=0.75, MaxPixelPointWidth=40"><SmartLabelStyle Enabled="True" /></Series></Series>'
                    '<ChartAreas><ChartArea BorderColor="White" BorderDashStyle="Solid"><AxisY LabelAutoFitMinFontSize="8" TitleForeColor="59, 59, 59" '
                    'TitleFont="{0}, 10.5px" LineColor="165, 172, 181" IntervalAutoMode="VariableCount"><MajorGrid LineColor="239, 242, 246" />'
                    '<LabelStyle Font="{0}, 10.5px" ForeColor="59, 59, 59" /></AxisY><AxisX LabelAutoFitMinFontSize="8" TitleForeColor="59, 59, 59" '
                    'TitleFont="{0}, 10.5px" LineColor="165, 172, 181" IntervalAutoMode="VariableCount"><MajorTickMark LineColor="165, 172, 181" />'
                    '<MajorGrid LineColor="Transparent" /><LabelStyle Font="{0}, 10.5px" ForeColor="59, 59, 59" /></AxisX></ChartArea></ChartAreas>'
                    '<Titles><Title Alignment="TopLeft" DockingOffset="-3" Font="{0}, 13px" ForeColor="59, 59, 59" /></Titles></Chart>')
    body = {"name": name, "primaryentitytypecode": table, "datadescription": data, "presentationdescription": presentation,
            "description": "Open email desk requirements per desk stage (tools/deploy_app.py)."}
    if dv.get(f"savedqueryvisualizations({cid})?$select=savedqueryvisualizationid")[0] == 200:
        ok(*dv.patch(f"savedqueryvisualizations({cid})", body), "update chart")
        print(f"= chart {name}")
    else:
        ok(*dv.request("POST", "savedqueryvisualizations", dict(body, savedqueryvisualizationid=cid), SOL), "create chart")
        print(f"+ chart {name}")
    return cid


def subarea(sid, title, table, vid):
    url = f"/main.aspx?pagetype=entitylist&amp;etn={table}&amp;viewid=%7b{vid}%7d&amp;viewtype=1039"
    return (f'<SubArea Id="{sid}" Entity="{table}" Url="{url}" Client="All,Outlook,OutlookLaptopClient,OutlookWorkstationClient,Web" '
            f'AvailableOffline="true" PassParams="false" Sku="All,OnPremise,Live,SPLA"><Titles><Title LCID="1033" Title="{escape(title)}" /></Titles></SubArea>')


def update_sitemap(views):
    s, b = dv.get(f"sitemaps?$select=sitemapid,sitemapxml&$filter=sitemapnameunique eq '{APP}'")
    row = ok(s, b, "read sitemap")["value"][0]
    xml = row["sitemapxml"]
    start = xml.find('<Area Id="emaildesk"')
    if start >= 0:  # replace our area, keep the rest of the app as it is
        end = xml.find("</Area>", start) + len("</Area>")
        xml = xml[:start] + xml[end:]
    area = ('<Area Id="emaildesk" ShowGroups="true"><Titles><Title LCID="1033" Title="Email desk" /></Titles>'
            '<Group Id="emaildesk_pipeline" IsProfile="false"><Titles><Title LCID="1033" Title="Pipeline" /></Titles>'
            + subarea("sa_desk_requirements", "Requirements", "gc_buyerrequirement", views["Desk pipeline: open requirements"])
            + subarea("sa_desk_lots", "Seller lots", "gc_sellerlot", views["Desk pipeline: open seller lots"])
            + subarea("sa_desk_deals", "Deals", "gc_deal", views["Desk pipeline: deals"])
            + subarea("sa_desk_contracts", "Contracts out", "gc_contract", views["Desk pipeline: contracts out for signature"])
            + '</Group><Group Id="emaildesk_todo" IsProfile="false"><Titles><Title LCID="1033" Title="To do" /></Titles>'
            + subarea("sa_desk_decisions", "Decisions waiting", "gc_reviewtask", views["Waiting for a decision"])
            + subarea("sa_desk_drafts", "Drafts waiting", "gc_message", views["Drafts waiting in Gmail"])
            + "</Group></Area>")
    at = xml.find("<Area ")
    xml = xml[:at] + area + xml[at:]
    ok(*dv.patch(f"sitemaps({row['sitemapid']})", {"sitemapxml": xml}), "update sitemap")
    print("= sitemap: Email desk area")
    return row["sitemapid"]


def main():
    views = {}
    for v in VIEWS:
        views[v[0]] = upsert_view(*v)
    chart = upsert_chart(*CHART)
    sitemap = update_sitemap(views)
    app = ok(*dv.get(f"appmodules?$select=appmoduleid&$filter=uniquename eq '{APP}'"), "read app")["value"][0]["appmoduleid"]
    components = [{"savedqueryid": vid, "@odata.type": "Microsoft.Dynamics.CRM.savedquery"} for vid in views.values()]
    components.append({"savedqueryvisualizationid": chart, "@odata.type": "Microsoft.Dynamics.CRM.savedqueryvisualization"})
    s, b = dv.request("POST", "AddAppComponents", {"AppId": app, "Components": components})
    print("= views and chart added to the app" if s < 300 else f"! AddAppComponents: {s} {json.dumps(b)[:300]} (the app may show all views anyway)")
    tables = "".join(f"<entity>{t}</entity>" for t in sorted({v[1] for v in VIEWS}))
    ok(*dv.request("POST", "PublishXml", {"ParameterXml": f"<importexportxml><entities>{tables}</entities><sitemaps><sitemap>{sitemap}</sitemap></sitemaps>"
                                                         f"<appmodules><appmodule>{app}</appmodule></appmodules></importexportxml>"}, timeout=600), "publish")
    print("published: DealOS Admin → Email desk")


if __name__ == "__main__":
    main()

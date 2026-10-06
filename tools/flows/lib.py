"""Building blocks for DealOS cloud flow definitions (Logic Apps workflow JSON as used by Power Automate).

Conventions kept from the original flows:
  * every flow is Try / Catch; Catch writes gc_flowfailure and terminates the run as Failed
  * Dataverse goes through the connection reference gc_dataverse, Outlook through gc_outlook
  * agents are called with "Perform an unbound action" gc_Agent_<Name>; Result is parsed with Parse JSON
"""
import json
import os
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AGENT_SCHEMAS = os.path.join(ROOT, "build", "agents")

DV = "shared_commondataserviceforapps"
OUTLOOK = "shared_office365"
APPROVALS = "shared_approvals"
CONNECTIONS = {
    APPROVALS: {"runtimeSource": "embedded", "connection": {"connectionReferenceLogicalName": "gc_approvals"}, "api": {"name": APPROVALS}},
    DV: {"runtimeSource": "embedded", "connection": {"connectionReferenceLogicalName": "gc_dataverse"}, "api": {"name": DV}},
    OUTLOOK: {"runtimeSource": "embedded", "connection": {"connectionReferenceLogicalName": "gc_outlook"}, "api": {"name": OUTLOOK}},
}
AUTH = "@parameters('$authentication')"
NS = uuid.UUID("6a1f3c52-9d7e-4f0b-8c21-0d5e0a7b4f10")

B = 303300000  # choice value base (publisher prefix 30330)

# Choice values used by the flows
STAGE = dict(Inquiry=B, Negotiation=B + 1, TermsAgreed=B + 2, ComplianceCheck=B + 3, Contracting=B + 4, Signed=B + 5,
             AwaitingFunding=B + 6, Funded=B + 7, InTransit=B + 8, Delivered=B + 9, Settled=B + 10, Closed=B + 11, Cancelled=B + 12)
LISTING = dict(Draft=B, Submitted=B + 1, InVerification=B + 2, NeedsInfo=B + 3, Published=B + 4)
PARSE = dict(Pending=B, Parsed=B + 1, Failed=B + 2)
REVIEW_STATUS = dict(Open=B, Approved=B + 1, Rejected=B + 2)
REVIEW_KIND = dict(Approval=B, Review=B + 1)
PURPOSE = dict(TierUpgrade=B, ListingPublish=B + 1, ScreeningClearance=B + 2, ContractIssue=B + 3, FundRelease=B + 4, Refund=B + 5,
               ShipmentBooking=B + 6, MessageSend=B + 8, Other=B + 9)
ROLE = dict(SuperAdmin=B, VerificationOfficer=B + 1, DealManager=B + 2, ComplianceOfficer=B + 3, Finance=B + 4, LogisticsCoordinator=B + 5)
OFFER = dict(Open=B, Countered=B + 1, Accepted=B + 2)
CONTRACT = dict(Draft=B, SentForSignature=B + 2, Signed=B + 3, Void=B + 4)
PAYMENT = dict(AwaitingFunding=B, Funded=B + 1, Settled=B + 4)
RELEASE = dict(Settled=B + 4)
MILESTONE = dict(Completed=B + 1, Verified=B + 2)
MILESTONE_TYPE = dict(BLIssued=B + 2, Departure=B + 3, Delivered=B + 7)
MESSAGE = dict(Approved=B + 1, Rejected=B + 2, Sent=B + 3)
MESSAGE_KIND = dict(Market=B, Source=B + 1)
REQUIREMENT = dict(Open=B + 1)
MATCH = dict(Proposed=B, Mutual=B + 2)
PARTY = dict(Seller=B, Buyer=B + 1)
KYB = dict(NotStarted=B, InProgress=B + 1, Passed=B + 2)
TIER = dict(KYBVerified=B + 2)
OVERLAY = dict(None_=B, ComplianceHold=B + 3)
SHIPMENT = dict(Planned=B)
SCOPE = dict(Domestic=B, International=B + 1)
EMPTY_GUID = "00000000-0000-0000-0000-000000000000"


def flow_id(name):
    """Stable workflow id for a new flow, so redeploying updates the same flow."""
    return str(uuid.uuid5(NS, name))


# ---------- actions ----------

def _dv(op, params, retry=None):
    a = {"type": "OpenApiConnection",
         "inputs": {"host": {"connectionName": DV, "operationId": op, "apiId": f"/providers/Microsoft.PowerApps/apis/{DV}"},
                    "parameters": params, "authentication": AUTH}}
    if retry is not None:
        a["inputs"]["retryPolicy"] = retry
    return a


def item(fields):
    return {f"item/{k}": v for k, v in fields.items()}


def create(entity_set, **fields):
    return _dv("CreateRecord", {"entityName": entity_set, **item(fields)})


def update(entity_set, record_id, **fields):
    return _dv("UpdateRecord", {"entityName": entity_set, "recordId": record_id, **item(fields)})


def list_rows(entity_set, filter=None, select=None, top=None, expand=None, orderby=None):
    p = {"entityName": entity_set}
    if select:
        p["$select"] = select
    if filter:
        p["$filter"] = filter
    if top:
        p["$top"] = top
    if expand:
        p["$expand"] = expand
    if orderby:
        p["$orderby"] = orderby
    return _dv("ListRecords", p)


def get_row(entity_set, record_id, select=None):
    p = {"entityName": entity_set, "recordId": record_id}
    if select:
        p["$select"] = select
    return _dv("GetItem", p)


def unbound(action, retry_none=False, **params):
    return _dv("PerformUnboundAction", {"actionName": action, **item(params)}, {"type": "none"} if retry_none else None)


def agent(name, subject, flow_label):
    """Calls gc_Agent_<name>. No connector retries: each retry would be a new paid model run; the agent retries models itself."""
    params = {"actionName": f"gc_Agent_{name}", "item/Input": json.dumps({"trigger": f"flow:{flow_label}"})}
    if subject is not None:
        params["item/SubjectId"] = subject
    return _dv("PerformUnboundAction", params, {"type": "none"})


def transition(deal_id, stage, reason):
    return unbound("gc_TransitionDeal", DealId=deal_id, TargetStage=stage, Reason=reason)


def compose(value):
    return {"type": "Compose", "inputs": value}


def terminate(status="Succeeded", message=None):
    a = {"type": "Terminate", "inputs": {"runStatus": status}}
    if status == "Failed":
        a["inputs"]["runError"] = {"code": "DealOSFlowFailure", "message": message or "Recorded in Flow failures"}
    return a


def condition(expression, yes, no=None):
    return {"type": "If", "expression": expression, "actions": chain(yes), "else": {"actions": chain(no or [])}}


def foreach(items, actions, concurrency=1):
    a = {"type": "Foreach", "foreach": items, "actions": chain(actions)}
    if concurrency:
        a["runtimeConfiguration"] = {"concurrency": {"repetitions": concurrency}}
    return a


def scope(actions):
    return {"type": "Scope", "actions": chain(actions)}


def switch(expression, cases, default=None):
    return {"type": "Switch", "expression": expression,
            "cases": {name: {"case": value, "actions": chain(acts)} for name, value, acts in cases},
            "default": {"actions": chain(default or [])}}


def chain(steps):
    """[(name, action), ...] → actions dict, each running after the previous one succeeded.
    A step may be (name, action, {"After": ["Failed", ...]}) to set its own runAfter on the previous step."""
    out, prev = {}, None
    for step in steps:
        name, action = step[0], dict(step[1])
        run_after = step[2] if len(step) > 2 else None
        if run_after is not None:
            action["runAfter"] = run_after
        else:
            action["runAfter"] = {prev: ["Succeeded"]} if prev else {}
        out[name] = action
        prev = name
    return out


# ---------- agent results ----------

def _lenient(schema):
    """Gemini response schema → lenient JSON Schema for Parse JSON (lower-case types, nulls allowed, nothing required)."""
    t = schema.get("type", "STRING").lower()
    out = {"type": [t, "null"]}
    if t == "object":
        out["properties"] = {k: _lenient(v) for k, v in schema.get("properties", {}).items()}
    elif t == "array":
        out["items"] = _lenient(schema.get("items", {"type": "STRING"}))
    return out


def agent_schema(name):
    with open(os.path.join(AGENT_SCHEMAS, f"{name}.output.json")) as f:
        result = _lenient(json.load(f))
    return {"type": "object", "properties": {
        "agent": {"type": "string"}, "run_id": {"type": "string"}, "status": {"type": "string"},
        "model": {"type": ["string", "null"]}, "error": {"type": ["string", "null"]},
        "result": result, "actions": {"type": ["array", "null"]}}}


def parse_agent(run_action, name):
    return {"type": "ParseJson", "inputs": {"content": f"@json(coalesce(outputs('{run_action}')?['body/Result'], '{{}}'))", "schema": agent_schema(name)}}


def agent_ok(run_action):
    return f"@equals(outputs('{run_action}')?['body/Status'], 'Succeeded')"


def run_agent(name, subject, flow_label, flow_name, on_fail="stop"):
    """Run_<name> + Parse_<name>.
    on_fail="stop":   a failed run records a flow failure and fails this run (the record keeps its stage, so the run can be resubmitted)
    on_fail="record": a failed run records a flow failure and the flow continues (the agent's work is advisory here)"""
    run, parsed = f"Run_{name}", f"Parse_{name}"
    failed = [(f"Record_{name}_failure", flow_failure(flow_name, run, f"@{{coalesce(outputs('{run}')?['body/Summary'], 'agent call failed')}}"))]
    if on_fail == "stop":
        failed.append((f"Stop_{name}_failed", terminate("Failed", f"{name} agent failed; see Flow failures")))
    return [(run, agent(name, subject, flow_label)),
            (f"Check_{name}", condition(f"@not({agent_ok(run)[1:]})", failed), {run: ["Succeeded", "Failed", "TimedOut"]}),
            (parsed, parse_agent(run, name))]


def agent_result(name, field):
    return f"body('Parse_{name}')?['result']?['{field}']"


# ---------- shared pieces ----------

def flow_failure(flow_name, step, error):
    return create("gc_flowfailures", gc_name=flow_name, gc_flowname=flow_name,
                  gc_runurl="https://make.powerautomate.com/environments/@{workflow()?['tags']?['environmentName']}/flows/@{workflow()?['name']}/runs/@{workflow()?['run']?['name']}",
                  gc_step=step, gc_error=error, gc_payload="@{take(string(triggerOutputs()?['body']), 100000)}", gc_status=B)


def audit(actor, action, subject_type, subject_id, details):
    """details: a Logic Apps expression (without @) that yields an object.
    gc_hash is required by the table but computed by the invariants plug-in (pre-operation), which overwrites the placeholder."""
    return create("gc_auditevents", gc_hash="set-by-invariants-plugin", gc_name=action, gc_actor=actor, gc_action=action, gc_subjecttype=subject_type,
                  gc_subjectid=subject_id, gc_details=f"@{{take(string({details}), 100000)}}")


def review_task(name, purpose, role, payload_expr, deal=None, listing=None, account=None, kind="Approval"):
    fields = dict(gc_name=f"@{{take({name}, 100)}}", gc_kind=REVIEW_KIND[kind], gc_purpose=PURPOSE[purpose],
                  gc_assigneerole=ROLE[role], gc_status=REVIEW_STATUS["Open"], gc_payload=f"@{{string({payload_expr})}}")
    if deal:
        fields["gc_deal@odata.bind"] = f"gc_deals({deal})"
    if listing:
        fields["gc_listing@odata.bind"] = f"gc_listings({listing})"
    if account:
        fields["gc_account@odata.bind"] = f"accounts({account})"
    return create("gc_reviewtasks", **fields)


NOTIFY_FLOW = "DealOS | Notify party"


def notify(account_expr, subject, body, event):
    """Runs the Notify party child flow. Failures there never fail the caller (the child catches its own errors)."""
    return {"type": "Workflow",
            "inputs": {"host": {"workflowReferenceName": flow_id(NOTIFY_FLOW)},
                       "body": {"text": account_expr, "text_1": subject, "text_2": body, "text_3": event}}}


def try_catch(flow_name, steps, catch_extra=None):
    catch = [("Record_failure", flow_failure(flow_name, "Try", "@{take(string(result('Try')), 3900)}"))]
    catch += catch_extra or []
    catch.append(("Fail_run", terminate("Failed")))
    return {
        "Try": {"type": "Scope", "actions": chain(steps), "runAfter": {}},
        "Catch": {"type": "Scope", "actions": chain(catch), "runAfter": {"Try": ["Failed", "TimedOut"]}},
    }


# ---------- triggers ----------

def row_trigger(name, entity, message, filter=None, attributes=None, conditions=None, concurrency=None):
    """message: 1 Added, 3 Modified, 4 Added or Modified."""
    params = {"subscriptionRequest/message": message, "subscriptionRequest/entityname": entity, "subscriptionRequest/scope": 4}
    if filter:
        params["subscriptionRequest/filterexpression"] = filter
    if attributes:
        params["subscriptionRequest/filteringattributes"] = attributes
    t = {"type": "OpenApiConnectionWebhook",
         "inputs": {"host": {"connectionName": DV, "operationId": "SubscribeWebhookTrigger", "apiId": f"/providers/Microsoft.PowerApps/apis/{DV}"},
                    "parameters": params, "authentication": AUTH}}
    if conditions:
        t["conditions"] = [{"expression": c} for c in conditions]
    if concurrency:
        t["runtimeConfiguration"] = {"concurrency": {"runs": concurrency}}
    return {name: t}


def daily_trigger(name, hour):
    return {name: {"type": "Recurrence", "recurrence": {"frequency": "Day", "interval": 1, "schedule": {"hours": [str(hour)]}, "timeZone": "UTC"}}}


def definition(triggers, actions):
    return {"$schema": "https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#",
            "contentVersion": "1.0.0.0",
            "parameters": {"$connections": {"defaultValue": {}, "type": "Object"}, "$authentication": {"defaultValue": {}, "type": "SecureObject"}},
            "triggers": triggers, "actions": actions}


def clientdata(defn, connections=(DV,)):
    return {"properties": {"connectionReferences": {c: CONNECTIONS[c] for c in connections}, "definition": defn, "templateName": None},
            "schemaVersion": "1.0.0.0"}

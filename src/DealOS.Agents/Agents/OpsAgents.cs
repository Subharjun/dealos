using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using DealOS.Agents.Tools;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Agents
{
    /// <summary>Escrow planning and release readiness. Money gates are decided in code, not by the model.</summary>
    public sealed class PaymentAgent : AgentDefinition
    {
        public override string Name { get { return "Payment"; } }
        public override string DisplayName { get { return "Payment & Escrow"; } }
        public override string Description { get { return "Proposes the escrow release schedule with commission (deterministic maths) and checks which releases have verified milestones; opens Fund Release approvals for Finance only when evidence exists."; } }
        public override int AgentChoice { get { return Choice.Base + 8; } }
        public override string SubjectTable { get { return "gc_deal"; } }
        public override string[] Tools { get { return new[] { "compute_release_schedule", "get_record", "query_records" }; } }
        public override string[] ReadTables { get { return new[] { "gc_deal", "gc_payment", "gc_paymentrelease", "gc_paymentevent", "gc_milestone", "gc_commissionplan", "gc_contract" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the Payment agent. You never move money; you plan and check.
1. If CONTEXT.releases is empty, propose a schedule with compute_release_schedule using CONTEXT.deal.contract_value and, unless the contract data says otherwise, two tranches: 30% 'Inspection passed and BL issued', 70% 'Delivered and discharge inspection accepted'. Put the tool's tranches in 'schedule'.
2. For each existing release that is Pending or Conditions Met, map its condition text to the milestone types it depends on (from: Inspection, Loading, BL Issued, Departure, Arrival, Customs Cleared, Discharge Inspection, Delivered) and list it in releases_ready with those milestone types. The system will verify the milestones itself.
3. List blockers: status overlay (On Hold, Disputed, Compliance Hold), payment not funded, missing contract value, missing commission plan.
4. Call finish.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Payment plan",
                    "contract_value", S.Num("Contract value."),
                    "currency", S.Str("Currency."),
                    "schedule", S.Arr("Proposed tranches (from compute_release_schedule).", S.Obj(null,
                        "sequence", S.Int("Order."), "pct", S.Num("Percent."), "condition", S.Str("Condition."),
                        "gross", S.Num("Gross."), "commission_deducted", S.Num("Commission."), "net_to_seller", S.Num("Net."))),
                    "releases_ready", S.Arr("Existing releases whose conditions may be met.", S.Obj(null,
                        "release_id", S.Str("gc_paymentrelease id."),
                        "milestone_types", S.Arr("Milestones the condition depends on.", S.Enum("Milestone.", Choice.MilestoneTypes)))),
                    "blockers", S.Arr("What prevents funding or release.", S.Str("blocker")));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var deal = H.Require(ctx);
            var price = deal.GetAttributeValue<decimal?>("gc_price");
            var qty = deal.GetAttributeValue<decimal?>("gc_quantity");
            var payments = ctx.Dv.Query("gc_payment", null, 5, "gc_deal", ConditionOperator.Equal, deal.Id);
            var releases = new List<object>();
            var releaseIds = new HashSet<string>();
            foreach (var p in payments)
                foreach (var r in ctx.Dv.Query("gc_paymentrelease", null, 20, "gc_payment", ConditionOperator.Equal, p.Id))
                {
                    releaseIds.Add(r.Id.ToString());
                    releases.Add(J.Obj("release_id", r.Id.ToString(), "sequence", r.GetAttributeValue<int?>("gc_sequence"), "pct", r.GetAttributeValue<decimal?>("gc_pct"),
                        "amount", r.GetAttributeValue<decimal?>("gc_amount"), "condition", r.GetAttributeValue<string>("gc_condition"), "status", Dv.Label(r, "gc_status")));
                }
            ctx.Scratch["release_ids"] = releaseIds;
            return J.Obj(
                "deal", J.Obj("name", deal.GetAttributeValue<string>("gc_name"), "stage", Dv.Label(deal, "gc_stage"), "overlay", Dv.Label(deal, "gc_statusoverlay"),
                              "incoterm", Dv.Label(deal, "gc_incoterm"), "payment_terms", Dv.Label(deal, "gc_paymentterms"),
                              "price", price, "quantity", qty, "currency", deal.GetAttributeValue<string>("gc_currency"),
                              "contract_value", price != null && qty != null ? price * qty : null),
                "payments", payments.Select(p => (object)J.Obj("id", p.Id.ToString(), "state", Dv.Label(p, "gc_state"), "amount_due", p.GetAttributeValue<decimal?>("gc_amountdue"),
                                                               "amount_funded", p.GetAttributeValue<decimal?>("gc_amountfunded"), "partner", p.GetAttributeValue<string>("gc_partner"),
                                                               "funding_deadline", p.GetAttributeValue<DateTime?>("gc_fundingdeadline"))).ToList(),
                "releases", releases,
                "milestones", H.Milestones(ctx, deal.Id),
                "commission_plan", H.CommissionPlan(ctx, H.Ref(deal, "gc_commissionplan")),
                "release_approval_threshold_usd", ctx.Dv.SettingNum("payment.release_threshold_usd", 250000),
                "readable_tables", ReadTables);
        }

        public override void AfterFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            var deal = ctx.Subject;
            var overlay = Dv.Label(deal, "gc_statusoverlay");
            var milestones = ctx.Dv.Query("gc_milestone", new[] { "gc_type", "gc_status", "gc_evidencedocument" }, 30, "gc_deal", ConditionOperator.Equal, deal.Id);
            var releaseIds = (HashSet<string>)ctx.Scratch["release_ids"];
            var checkedReleases = new List<object>();
            foreach (var r in J.Arr(result, "releases_ready").OfType<Dictionary<string, object>>())
            {
                var id = J.Str(r, "release_id");
                var types = J.Arr(r, "milestone_types").Select(t => t.ToString()).ToList();
                string verdict;
                if (!releaseIds.Contains(id ?? "")) verdict = "unknown release id";
                else if (overlay != null && overlay != "None") verdict = "blocked by " + overlay;
                else if (types.Count == 0) verdict = "no milestones mapped";
                else
                {
                    var missing = types.Where(t => !milestones.Any(m => Dv.Label(m, "gc_type") == t &&
                        (Dv.Label(m, "gc_status") == "Verified" || (Dv.Label(m, "gc_status") == "Completed" && m.GetAttributeValue<EntityReference>("gc_evidencedocument") != null)))).ToList();
                    verdict = missing.Count == 0 ? "ready" : "waiting for verified: " + string.Join(", ", missing);
                }
                checkedReleases.Add(J.Obj("release_id", id, "milestone_types", types, "system_check", verdict));
                if (verdict == "ready")
                    ToolCatalog.CreateReviewTask(ctx, "Fund Release", "Approval", "Release funds (" + string.Join(" + ", types) + "): " + deal.GetAttributeValue<string>("gc_name"),
                        J.Obj("releaseId", id, "milestones", types), "Finance", false);
            }
            result["releases_ready"] = checkedReleases;
            if (checkedReleases.Count > 0) result["needs_human"] = true;
        }
    }

    /// <summary>Execution logistics: document checklist per corridor, milestones, chasing.</summary>
    public sealed class LogisticsAgent : AgentDefinition
    {
        public override string Name { get { return "Logistics"; } }
        public override string DisplayName { get { return "Logistics"; } }
        public override string Description { get { return "Builds the shipment document checklist for the corridor (domestic or international), creates missing milestones, drafts document chasers and asks for shipment booking approval."; } }
        public override int AgentChoice { get { return Choice.Base + 9; } }
        public override string SubjectTable { get { return "gc_deal"; } }
        public override string[] Tools { get { return new[] { "create_milestone", "draft_message", "create_review_task", "get_record", "query_records" }; } }
        public override string[] ReadTables { get { return new[] { "gc_deal", "gc_shipment", "gc_milestone", "gc_document", "gc_countryrule", "gc_country" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the Logistics agent for this deal.
1. scope: Domestic if origin and destination countries are the same, else International.
2. Build the document checklist from CONTEXT.country_rules first; add the reference checklist items that apply to the scope and Incoterm. Mark each Received only if a matching document is in CONTEXT.documents (by type), otherwise Missing; 'Not Required' when the Incoterm/scope makes it irrelevant. Rules marked is_sample need confirmation – say so.
3. Create missing milestones that apply (Inspection always before Loading; BL Issued/Departure/Arrival/Customs Cleared only for international sea/air) with create_milestone.
4. If documents are missing and the deal is Funded or later, draft_message (audience Source) to the party that must provide them – short list, no other party's name.
5. If there is no shipment yet and the deal is Funded, create_review_task (purpose Shipment Booking, kind Approval, role Logistics Coordinator).
6. Call finish.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Logistics plan",
                    "scope", S.Enum("Shipment scope.", "Domestic", "International"),
                    "checklist", S.Arr("Documents.", S.Obj(null,
                        "document", S.Str("Document."), "required_by", S.Str("Rule, Incoterm or reference."),
                        "status", S.Enum("Status.", "Received", "Missing", "Not Required"), "document_id?", S.Str("gc_document id if received."))),
                    "next_steps", S.Arr("Next steps in order.", S.Str("step")),
                    "risks", S.Arr("Risks.", S.Str("risk")));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var deal = H.Require(ctx);
            var origin = H.Ref(deal, "gc_origincountry");
            var dest = H.Ref(deal, "gc_destinationcountry");
            ctx.Scratch["counterparty_terms"] = H.Strings(H.AccountName(ctx, H.Ref(deal, "gc_seller")), H.AccountName(ctx, H.Ref(deal, "gc_buyer")));
            return J.Obj(
                "deal", J.Obj("name", deal.GetAttributeValue<string>("gc_name"), "stage", Dv.Label(deal, "gc_stage"), "incoterm", Dv.Label(deal, "gc_incoterm"),
                              "named_place", deal.GetAttributeValue<string>("gc_namedplace"), "hs_code", deal.GetAttributeValue<string>("gc_hscode"),
                              "commodity", H.LookupName(deal, "gc_commodity"), "quantity", deal.GetAttributeValue<decimal?>("gc_quantity"), "unit", Dv.Label(deal, "gc_quantityunit")),
                "origin_country", H.Country(ctx, origin),
                "destination_country", H.Country(ctx, dest),
                "shipments", ctx.Dv.Query("gc_shipment", null, 5, "gc_deal", ConditionOperator.Equal, deal.Id).Select(s => (object)J.Obj(
                    "scope", Dv.Label(s, "gc_scope"), "mode", Dv.Label(s, "gc_mode"), "status", Dv.Label(s, "gc_status"), "origin_port", s.GetAttributeValue<string>("gc_originport"),
                    "destination_port", s.GetAttributeValue<string>("gc_destinationport"), "etd", s.GetAttributeValue<DateTime?>("gc_etd"), "eta", s.GetAttributeValue<DateTime?>("gc_eta"),
                    "bl_number", s.GetAttributeValue<string>("gc_blnumber"))).ToList(),
                "milestones", H.Milestones(ctx, deal.Id),
                "documents", H.Documents(ctx, "gc_deal", deal.Id),
                "country_rules", H.CountryRules(ctx, new[] { origin, dest }, H.Ref(deal, "gc_commodity"), "Export Rules", "Import Rules", "Logistics"),
                "reference_checklist", J.Obj(
                    "domestic", new[] { "Tax invoice (with GST/VAT number)", "E-way bill or transport permit where required", "Lorry receipt / consignment note", "Weighbridge slip", "Inspection / assay report", "Transit insurance" },
                    "international", new[] { "Commercial invoice", "Packing list", "Export declaration / shipping bill", "Bill of lading or air waybill", "Certificate of origin", "Independent inspection certificate (weight and quality)", "Insurance certificate (CIF/CIP)", "Export licence or permit if the commodity is controlled", "Import permit if required at destination" }),
                "readable_tables", ReadTables);
        }
    }

    /// <summary>Daily operations digest for staff (read-only).</summary>
    public sealed class AdminSupervisorAgent : AgentDefinition
    {
        public override string Name { get { return "AdminSupervisor"; } }
        public override string DisplayName { get { return "Ops Supervisor"; } }
        public override string Description { get { return "Read-only operations digest: open approvals, stuck deals, listings waiting on sellers, failed flows and agent runs, AI spend against budget, with prioritised actions."; } }
        public override int AgentChoice { get { return Choice.Base + 10; } }
        public override string SubjectTable { get { return null; } }
        public override string[] Tools { get { return new[] { "query_records", "get_record" }; } }
        public override string[] ReadTables { get { return new[] { "gc_reviewtask", "gc_flowfailure", "gc_agentrun", "gc_deal", "gc_listing", "gc_modelcall", "gc_payment" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the Ops Supervisor. Write a short daily digest for platform staff from CONTEXT only.
headline: one sentence with the most important number. sections: Approvals waiting, Deals needing attention, Listings waiting on sellers, System health, AI usage – each 1-5 short items with numbers. actions: the 3-7 most valuable actions, each with an owner role and priority. Never invent numbers.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Ops digest",
                    "headline", S.Str("One sentence."),
                    "sections", S.Arr("Digest sections.", S.Obj(null, "title", S.Str("Title."), "items", S.Arr("Items.", S.Str("item")))),
                    "actions", S.Arr("Prioritised actions.", S.Obj(null, "action", S.Str("Action."), "owner_role", S.Enum("Owner.", Choice.AdminRoles), "priority", S.Enum("Priority.", "High", "Medium", "Low"))));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var now = DateTime.UtcNow;
            var open = Fetch(ctx, "gc_reviewtask", new[] { "gc_name", "gc_purpose", "gc_assigneerole", "createdon" }, "gc_status", Choice.ReviewStatus.Open);
            var failures = Fetch(ctx, "gc_flowfailure", new[] { "gc_flowname", "gc_step", "gc_error", "createdon" }, "gc_status", Choice.Base);
            var runs = Since(ctx, "gc_agentrun", new[] { "gc_agent", "gc_status", "gc_error", "createdon" }, now.AddHours(-48));
            var deals = Fetch(ctx, "gc_deal", new[] { "gc_name", "gc_stage", "gc_statusoverlay", "modifiedon" }, null, 0);
            var listings = Fetch(ctx, "gc_listing", new[] { "gc_name", "gc_status", "modifiedon" }, null, 0);
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var calls = Since(ctx, "gc_modelcall", new[] { "gc_tokensin", "gc_tokensout", "gc_costusd", "gc_outcome", "gc_provider" }, monthStart);

            Func<Entity, double> age = e => Math.Round((now - (e.GetAttributeValue<DateTime?>("createdon") ?? now)).TotalDays, 1);
            var closed = new[] { "Closed", "Cancelled", "Settled" };
            return J.Obj(
                "as_of_utc", now,
                "approvals_open", J.Obj("count", open.Count,
                    "by_purpose", open.GroupBy(e => Dv.Label(e, "gc_purpose") ?? "?").Select(g => (object)J.Obj("purpose", g.Key, "count", g.Count(), "oldest_days", g.Max(age))).ToList(),
                    "oldest", open.OrderBy(e => e.GetAttributeValue<DateTime?>("createdon")).Take(5).Select(e => (object)J.Obj("title", e.GetAttributeValue<string>("gc_name"), "role", Dv.Label(e, "gc_assigneerole"), "age_days", age(e))).ToList()),
                "flow_failures_new", J.Obj("count", failures.Count, "latest", failures.Take(5).Select(e => (object)J.Obj("flow", e.GetAttributeValue<string>("gc_flowname"), "step", e.GetAttributeValue<string>("gc_step"), "error", GeminiClient.Truncate(e.GetAttributeValue<string>("gc_error"), 200))).ToList()),
                "agent_runs_48h", J.Obj("total", runs.Count, "failed", runs.Count(r => Dv.Label(r, "gc_status") == "Failed"),
                    "failures", runs.Where(r => Dv.Label(r, "gc_status") == "Failed").Take(5).Select(r => (object)J.Obj("agent", Dv.Label(r, "gc_agent"), "error", GeminiClient.Truncate(r.GetAttributeValue<string>("gc_error"), 200))).ToList()),
                "deals", J.Obj("by_stage", deals.GroupBy(d => Dv.Label(d, "gc_stage") ?? "?").Select(g => (object)J.Obj("stage", g.Key, "count", g.Count())).ToList(),
                    "on_overlay", deals.Where(d => (Dv.Label(d, "gc_statusoverlay") ?? "None") != "None").Select(d => (object)J.Obj("deal", d.GetAttributeValue<string>("gc_name"), "overlay", Dv.Label(d, "gc_statusoverlay"))).ToList(),
                    "stale_7d", deals.Where(d => !closed.Contains(Dv.Label(d, "gc_stage")) && d.GetAttributeValue<DateTime?>("modifiedon") < now.AddDays(-7)).Take(10).Select(d => (object)J.Obj("deal", d.GetAttributeValue<string>("gc_name"), "stage", Dv.Label(d, "gc_stage"))).ToList()),
                "listings", J.Obj("by_status", listings.GroupBy(l => Dv.Label(l, "gc_status") ?? "?").Select(g => (object)J.Obj("status", g.Key, "count", g.Count())).ToList()),
                "ai_usage_month", J.Obj("calls", calls.Count, "tokens_in", calls.Sum(c => c.GetAttributeValue<int?>("gc_tokensin") ?? 0),
                    "tokens_out", calls.Sum(c => c.GetAttributeValue<int?>("gc_tokensout") ?? 0),
                    "cost_usd_recorded", calls.Sum(c => c.GetAttributeValue<decimal?>("gc_costusd") ?? 0m),
                    "errors", calls.Count(c => Dv.Label(c, "gc_outcome") == "Error"),
                    "budget_usd", ctx.Dv.SettingNum("ai.monthly_budget_usd", 0)),
                "readable_tables", ReadTables);
        }

        private static List<Entity> Fetch(AgentContext ctx, string table, string[] cols, string filterCol, int value)
        {
            var q = new QueryExpression(table) { ColumnSet = new ColumnSet(cols), TopCount = 500 };
            if (filterCol != null) q.Criteria.AddCondition(filterCol, ConditionOperator.Equal, value);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            q.AddOrder("createdon", OrderType.Descending);
            return ctx.Dv.Svc.RetrieveMultiple(q).Entities.ToList();
        }

        private static List<Entity> Since(AgentContext ctx, string table, string[] cols, DateTime since)
        {
            var q = new QueryExpression(table) { ColumnSet = new ColumnSet(cols), TopCount = 5000 };
            q.Criteria.AddCondition("createdon", ConditionOperator.GreaterEqual, since);
            return ctx.Dv.Svc.RetrieveMultiple(q).Entities.ToList();
        }
    }
}

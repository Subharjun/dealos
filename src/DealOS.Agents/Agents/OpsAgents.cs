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

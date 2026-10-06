using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Tools
{
    public sealed class AttributeDef
    {
        public string Key;
        public string Name;
        public string DataType;
        public string Unit;
        public string Definition;
        public int Sensitivity;
        public int? FreshnessDays;

        public bool MarketVisible { get { return Sensitivity == Choice.Sensitivity.MarketSafe || Sensitivity == Choice.Sensitivity.MarketGeneralise; } }

        public string SensitivityLabel
        {
            get
            {
                switch (Sensitivity - Choice.Base)
                {
                    case 0: return "Market Safe";
                    case 1: return "Market Generalise";
                    case 2: return "Internal";
                    default: return "Restricted";
                }
            }
        }
    }

    public sealed class FactRow
    {
        public string Key;
        public int Status;
        public string StatusLabel;
        public string DisplayValue;
        public string ValueNorm;
        public string Confidence;
        public DateTime? ExpiresOn;
        public AttributeDef Def;

        public Dictionary<string, object> ToJson(bool includeValue = true)
        {
            var d = J.Obj("attribute_key", Key, "name", Def == null ? Key : Def.Name, "status", StatusLabel,
                          "sensitivity", Def == null ? "Internal" : Def.SensitivityLabel);
            if (includeValue && DisplayValue != null) d["value"] = DisplayValue;
            if (Confidence != null) d["confidence"] = Confidence;
            if (ExpiresOn != null) d["fresh_until"] = ExpiresOn.Value.ToString("yyyy-MM-dd");
            return d;
        }
    }

    /// <summary>Read helpers over the evidence ledger (gc_attributedef, gc_fact, gc_question).</summary>
    public static class Evidence
    {
        public static Dictionary<string, AttributeDef> Defs(AgentContext ctx)
        {
            object cached;
            if (ctx.Scratch.TryGetValue("defs", out cached)) return (Dictionary<string, AttributeDef>)cached;
            var q = new QueryExpression("gc_attributedef")
            {
                ColumnSet = new ColumnSet("gc_key", "gc_name", "gc_datatype", "gc_canonicalunit", "gc_definition", "gc_sensitivity", "gc_freshnessdays")
            };
            var defs = new Dictionary<string, AttributeDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in ctx.Dv.System.RetrieveMultiple(q).Entities)
            {
                var key = e.GetAttributeValue<string>("gc_key");
                if (string.IsNullOrEmpty(key)) continue;
                var sens = e.GetAttributeValue<OptionSetValue>("gc_sensitivity");
                defs[key] = new AttributeDef
                {
                    Key = key,
                    Name = e.GetAttributeValue<string>("gc_name"),
                    DataType = e.GetAttributeValue<string>("gc_datatype"),
                    Unit = e.GetAttributeValue<string>("gc_canonicalunit"),
                    Definition = e.GetAttributeValue<string>("gc_definition"),
                    Sensitivity = sens == null ? Choice.Sensitivity.Internal : sens.Value,
                    FreshnessDays = e.GetAttributeValue<int?>("gc_freshnessdays")
                };
            }
            ctx.Scratch["defs"] = defs;
            return defs;
        }

        public static List<object> DefsJson(AgentContext ctx)
        {
            return Defs(ctx).Values.OrderBy(d => d.Key).Select(d => (object)J.Obj(
                "attribute_key", d.Key, "name", d.Name, "type", d.DataType, "unit", d.Unit,
                "sensitivity", d.SensitivityLabel, "fresh_days", d.FreshnessDays)).ToList();
        }

        public static List<FactRow> Facts(AgentContext ctx, int subjectType, Guid subjectId)
        {
            var q = new QueryExpression("gc_fact")
            {
                ColumnSet = new ColumnSet("gc_attributekey", "gc_status", "gc_displayvalue", "gc_valuenorm", "gc_confidenceband", "gc_freshnessexpireson"),
                TopCount = 200
            };
            q.Criteria.AddCondition("gc_subjecttype", ConditionOperator.Equal, subjectType);
            q.Criteria.AddCondition("gc_subjectid", ConditionOperator.Equal, subjectId.ToString());
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var defs = Defs(ctx);
            var rows = new List<FactRow>();
            foreach (var e in ctx.Dv.Svc.RetrieveMultiple(q).Entities)
            {
                var key = e.GetAttributeValue<string>("gc_attributekey");
                var status = e.GetAttributeValue<OptionSetValue>("gc_status");
                AttributeDef def;
                defs.TryGetValue(key ?? "", out def);
                rows.Add(new FactRow
                {
                    Key = key,
                    Status = status == null ? Choice.FactStatus.Missing : status.Value,
                    StatusLabel = Dv.Label(e, "gc_status") ?? "Missing",
                    DisplayValue = e.GetAttributeValue<string>("gc_displayvalue"),
                    ValueNorm = e.GetAttributeValue<string>("gc_valuenorm"),
                    Confidence = Dv.Label(e, "gc_confidenceband"),
                    ExpiresOn = e.GetAttributeValue<DateTime?>("gc_freshnessexpireson"),
                    Def = def
                });
            }
            return rows.OrderBy(r => r.Key).ToList();
        }

        public static List<object> FactsJson(IEnumerable<FactRow> facts)
        {
            return facts.Select(f => (object)f.ToJson()).ToList();
        }

        /// <summary>Recent questions on a listing / deal / account (the do-not-ask-twice ledger).</summary>
        public static List<Entity> Questions(AgentContext ctx, string lookup, Guid id, int top = 30)
        {
            var q = new QueryExpression("gc_question")
            {
                ColumnSet = new ColumnSet("gc_name", "gc_attributekey", "gc_kind", "gc_askedon", "gc_answeredby", "gc_isfollowup"),
                TopCount = top
            };
            q.Criteria.AddCondition(lookup, ConditionOperator.Equal, id);
            q.AddOrder("gc_askedon", OrderType.Descending);
            return ctx.Dv.Svc.RetrieveMultiple(q).Entities.ToList();
        }

        public static List<object> QuestionsJson(IEnumerable<Entity> qs)
        {
            return qs.Select(e => (object)J.Obj(
                "attribute_key", e.GetAttributeValue<string>("gc_attributekey"),
                "kind", Dv.Label(e, "gc_kind"),
                "asked_on", e.GetAttributeValue<DateTime?>("gc_askedon"),
                "answered", e.GetAttributeValue<EntityReference>("gc_answeredby") != null,
                "text", e.GetAttributeValue<string>("gc_name"))).ToList();
        }

        /// <summary>Calls the deterministic gc_ResolveEvidence API (rebuilds facts, conflicts and badge).</summary>
        public static Dictionary<string, object> Resolve(AgentContext ctx, int subjectType, Guid subjectId)
        {
            var req = new OrganizationRequest("gc_ResolveEvidence");
            req["SubjectType"] = subjectType;
            req["SubjectId"] = subjectId.ToString();
            var resp = ctx.Dv.Svc.Execute(req);
            var gaps = new List<object>();
            var gapsText = resp.Results.Contains("Gaps") ? resp.Results["Gaps"] as string : null;
            if (!string.IsNullOrEmpty(gapsText))
            {
                try { gaps = Json.Parse(gapsText) as List<object> ?? gaps; } catch (FormatException) { }
            }
            return J.Obj(
                "facts", resp.Results.Contains("Facts") ? resp.Results["Facts"] : null,
                "conflicts", resp.Results.Contains("Conflicts") ? resp.Results["Conflicts"] : null,
                "missing", resp.Results.Contains("Missing") ? resp.Results["Missing"] : null,
                "summary", resp.Results.Contains("Summary") ? resp.Results["Summary"] : null,
                "gaps", gaps);
        }

        public static int SubjectTypeOf(string table)
        {
            switch (table)
            {
                case "account": return Choice.SubjectType.Party;
                case "contact": return Choice.SubjectType.Contact;
                case "gc_asset": return Choice.SubjectType.Asset;
                case "gc_licence": return Choice.SubjectType.Licence;
                case "gc_listing": return Choice.SubjectType.Listing;
                case "gc_lot": return Choice.SubjectType.Lot;
                case "gc_deal": return Choice.SubjectType.Deal;
                case "gc_document": return Choice.SubjectType.Document;
                default: throw new ToolRefusal("Unsupported subject table: " + table);
            }
        }

        public static int SubjectTypeOfLabel(string label)
        {
            switch ((label ?? "").Trim().ToLowerInvariant())
            {
                case "party": case "account": return Choice.SubjectType.Party;
                case "contact": return Choice.SubjectType.Contact;
                case "asset": return Choice.SubjectType.Asset;
                case "licence": return Choice.SubjectType.Licence;
                case "listing": return Choice.SubjectType.Listing;
                case "lot": return Choice.SubjectType.Lot;
                case "deal": return Choice.SubjectType.Deal;
                case "document": return Choice.SubjectType.Document;
                default: throw new ToolRefusal("subject_type must be one of Party, Contact, Asset, Licence, Listing, Lot, Deal, Document.");
            }
        }
    }
}

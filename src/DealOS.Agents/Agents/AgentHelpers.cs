using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using DealOS.Agents.Tools;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Agents
{
    /// <summary>Shared deterministic prefetch used by several agents.</summary>
    internal static class H
    {
        public static Entity Require(AgentContext ctx)
        {
            if (ctx.Subject == null) throw new ToolRefusal(ctx.Agent.ApiName + " needs SubjectId (" + ctx.Agent.SubjectTable + ").");
            return ctx.Subject;
        }

        public static Guid? Ref(Entity e, string column)
        {
            var r = e == null ? null : e.GetAttributeValue<EntityReference>(column);
            return r == null ? (Guid?)null : r.Id;
        }

        public static Entity TryGet(AgentContext ctx, string table, Guid? id, params string[] columns)
        {
            return id == null ? null : ctx.Dv.Retrieve(table, id.Value, columns);
        }

        public static List<Entity> Rows(AgentContext ctx, string table, string lookup, Guid? id, int top = 25, params string[] columns)
        {
            if (id == null) return new List<Entity>();
            return ctx.Dv.Query(table, columns, top, lookup, ConditionOperator.Equal, id.Value);
        }

        public static Dictionary<string, object> Party(AgentContext ctx, Guid? accountId, bool withIdentity)
        {
            var a = TryGet(ctx, "account", accountId, "name", "gc_trusttier", "gc_kybstatus", "gc_compliancehold", "gc_country",
                           "gc_partyrole", "gc_buyertype", "gc_sellertype", "gc_registrationnumber", "gc_lei");
            if (a == null) return null;
            var d = J.Obj(
                "trust_tier", Dv.Label(a, "gc_trusttier"),
                "kyb_status", Dv.Label(a, "gc_kybstatus"),
                "compliance_hold", a.GetAttributeValue<bool?>("gc_compliancehold") ?? false,
                "country", a.GetAttributeValue<EntityReference>("gc_country") == null ? null : a.GetAttributeValue<EntityReference>("gc_country").Name,
                "roles", Dv.Label(a, "gc_partyrole"),
                "buyer_type", Dv.Label(a, "gc_buyertype"),
                "seller_type", Dv.Label(a, "gc_sellertype"));
            if (withIdentity)
            {
                d["id"] = a.Id.ToString();
                d["name"] = a.GetAttributeValue<string>("name");
                d["registration_number"] = a.GetAttributeValue<string>("gc_registrationnumber");
                d["lei"] = a.GetAttributeValue<string>("gc_lei");
            }
            return d;
        }

        public static string AccountName(AgentContext ctx, Guid? id)
        {
            var a = TryGet(ctx, "account", id, "name");
            return a == null ? null : a.GetAttributeValue<string>("name");
        }

        public static List<object> Screenings(AgentContext ctx, Guid? accountId)
        {
            return Rows(ctx, "gc_screening", "gc_account", accountId, 10, "gc_result", "gc_lists", "gc_screenedon", "gc_clearancereason")
                .Select(e => (object)J.Obj("result", Dv.Label(e, "gc_result"), "lists", e.GetAttributeValue<string>("gc_lists"),
                                           "screened_on", e.GetAttributeValue<DateTime?>("gc_screenedon"),
                                           "clearance", e.GetAttributeValue<string>("gc_clearancereason"))).ToList();
        }

        public static List<object> KycChecks(AgentContext ctx, Guid? accountId)
        {
            return Rows(ctx, "gc_kyccheck", "gc_account", accountId, 25, "gc_checktype", "gc_result", "gc_provider", "gc_checkedon", "gc_reportdocument", "gc_name")
                .Select(e => (object)J.Obj("check_type", Dv.Label(e, "gc_checktype"), "result", Dv.Label(e, "gc_result"),
                                           "provider", e.GetAttributeValue<string>("gc_provider"), "checked_on", e.GetAttributeValue<DateTime?>("gc_checkedon"),
                                           "has_report", e.GetAttributeValue<EntityReference>("gc_reportdocument") != null,
                                           "note", e.GetAttributeValue<string>("gc_name"))).ToList();
        }

        public static List<object> Documents(AgentContext ctx, string lookup, Guid? id)
        {
            return Rows(ctx, "gc_document", lookup, id, 40, "gc_filename", "gc_doctype", "gc_parsestatus", "gc_riskflags", "gc_docdate", "gc_provenanceclass", "gc_pagecount")
                .Select(e => (object)J.Obj("id", e.Id.ToString(), "file", e.GetAttributeValue<string>("gc_filename"), "doc_type", Dv.Label(e, "gc_doctype"),
                                           "parse_status", Dv.Label(e, "gc_parsestatus"), "provenance", Dv.Label(e, "gc_provenanceclass"),
                                           "doc_date", e.GetAttributeValue<DateTime?>("gc_docdate"), "pages", e.GetAttributeValue<int?>("gc_pagecount"),
                                           "risk_flags", e.GetAttributeValue<string>("gc_riskflags"))).ToList();
        }

        public static Dictionary<string, object> Country(AgentContext ctx, Guid? id)
        {
            var c = TryGet(ctx, "gc_country", id, "gc_name", "gc_iso2", "gc_fatfstatus", "gc_cahra", "gc_launchstatus");
            if (c == null) return null;
            return J.Obj("id", c.Id.ToString(), "name", c.GetAttributeValue<string>("gc_name"), "iso2", c.GetAttributeValue<string>("gc_iso2"),
                         "fatf", Dv.Label(c, "gc_fatfstatus"), "cahra", c.GetAttributeValue<bool?>("gc_cahra") ?? false,
                         "launch_status", Dv.Label(c, "gc_launchstatus"));
        }

        public static Dictionary<string, object> Commodity(AgentContext ctx, Guid? id)
        {
            var c = TryGet(ctx, "gc_commodity", id, "gc_name", "gc_code", "gc_family", "gc_form", "gc_defaulthscode");
            if (c == null) return null;
            return J.Obj("id", c.Id.ToString(), "name", c.GetAttributeValue<string>("gc_name"), "code", c.GetAttributeValue<string>("gc_code"),
                         "family", Dv.Label(c, "gc_family"), "form", Dv.Label(c, "gc_form"), "hs_code", c.GetAttributeValue<string>("gc_defaulthscode"));
        }

        /// <summary>Country rules (sample rules are flagged) for the given countries, optionally limited to sections.</summary>
        public static List<object> CountryRules(AgentContext ctx, IEnumerable<Guid?> countries, Guid? commodity, params string[] sections)
        {
            var result = new List<object>();
            foreach (var cid in countries.Where(c => c != null).Distinct())
            {
                foreach (var e in Rows(ctx, "gc_countryrule", "gc_country", cid, 50, "gc_name", "gc_section", "gc_rulekey", "gc_value", "gc_summary", "gc_commodity", "gc_hscode", "gc_issample", "gc_sourceurl", "gc_status"))
                {
                    var section = Dv.Label(e, "gc_section");
                    if (sections.Length > 0 && !sections.Contains(section)) continue;
                    var ruleCommodity = Ref(e, "gc_commodity");
                    if (ruleCommodity != null && commodity != null && ruleCommodity != commodity) continue;
                    result.Add(J.Obj("country", e.GetAttributeValue<EntityReference>("gc_country") == null ? null : e.GetAttributeValue<EntityReference>("gc_country").Name,
                        "section", section, "rule", e.GetAttributeValue<string>("gc_rulekey") ?? e.GetAttributeValue<string>("gc_name"),
                        "value", e.GetAttributeValue<string>("gc_value"), "summary", e.GetAttributeValue<string>("gc_summary"),
                        "hs_code", e.GetAttributeValue<string>("gc_hscode"), "is_sample", e.GetAttributeValue<bool?>("gc_issample") ?? false,
                        "status", Dv.Label(e, "gc_status"), "source", e.GetAttributeValue<string>("gc_sourceurl")));
                }
            }
            return result;
        }

        public static Dictionary<string, object> CommissionPlan(AgentContext ctx, Guid? planId)
        {
            var p = TryGet(ctx, "gc_commissionplan", planId, "gc_name", "gc_ratepct", "gc_base", "gc_payer", "gc_minamount", "gc_maxamount", "gc_sellersharepct", "gc_currency");
            if (p == null)
            {
                p = ctx.Dv.Query("gc_commissionplan", new[] { "gc_name", "gc_ratepct", "gc_base", "gc_payer", "gc_minamount", "gc_maxamount", "gc_sellersharepct", "gc_currency" }, 1,
                    "gc_isdefault", ConditionOperator.Equal, true).FirstOrDefault();
            }
            if (p == null) return null;
            var plan = J.Obj("name", p.GetAttributeValue<string>("gc_name"), "rate_pct", (double?)p.GetAttributeValue<decimal?>("gc_ratepct"),
                             "base", Dv.Label(p, "gc_base"), "payer", Dv.Label(p, "gc_payer"),
                             "min_amount", (double?)p.GetAttributeValue<decimal?>("gc_minamount"), "max_amount", (double?)p.GetAttributeValue<decimal?>("gc_maxamount"),
                             "seller_share_pct", (double?)p.GetAttributeValue<decimal?>("gc_sellersharepct"), "currency", p.GetAttributeValue<string>("gc_currency"));
            ctx.Scratch["commission_plan"] = plan;
            return plan;
        }

        public static List<object> Milestones(AgentContext ctx, Guid dealId)
        {
            return Rows(ctx, "gc_milestone", "gc_deal", dealId, 30, "gc_name", "gc_type", "gc_status", "gc_dueon", "gc_completedon", "gc_evidencedocument", "gc_shipment")
                .Select(e => (object)J.Obj("id", e.Id.ToString(), "type", Dv.Label(e, "gc_type"), "status", Dv.Label(e, "gc_status"),
                                           "due_on", e.GetAttributeValue<DateTime?>("gc_dueon"), "completed_on", e.GetAttributeValue<DateTime?>("gc_completedon"),
                                           "has_evidence", e.GetAttributeValue<EntityReference>("gc_evidencedocument") != null)).ToList();
        }

        /// <summary>Downloads a Dataverse file column (block by block).</summary>
        public static byte[] DownloadFile(AgentContext ctx, string table, Guid id, string column, out string fileName)
        {
            var init = new OrganizationRequest("InitializeFileBlocksDownload");
            init["Target"] = new EntityReference(table, id);
            init["FileAttributeName"] = column;
            var r = ctx.Dv.Svc.Execute(init);
            var token = (string)r.Results["FileContinuationToken"];
            var size = Convert.ToInt64(r.Results["FileSizeInBytes"]);
            fileName = r.Results.Contains("FileName") ? r.Results["FileName"] as string : null;
            using (var ms = new MemoryStream())
            {
                long offset = 0;
                const long block = 4L * 1024 * 1024;
                while (offset < size)
                {
                    var dl = new OrganizationRequest("DownloadBlock");
                    dl["FileContinuationToken"] = token;
                    dl["Offset"] = offset;
                    dl["BlockLength"] = Math.Min(block, size - offset);
                    var data = (byte[])ctx.Dv.Svc.Execute(dl).Results["Data"];
                    if (data == null || data.Length == 0) break;
                    ms.Write(data, 0, data.Length);
                    offset += data.Length;
                }
                return ms.ToArray();
            }
        }

        public static string LookupName(Entity e, string column)
        {
            var r = e == null ? null : e.GetAttributeValue<EntityReference>(column);
            return r == null ? null : r.Name;
        }

        public static List<string> Strings(params string[] values)
        {
            return values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        }
    }
}

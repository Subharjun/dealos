using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Collections.Concurrent;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Infrastructure
{
    /// <summary>Choice values used by the agents (publisher option prefix 30330).</summary>
    public static class Choice
    {
        public const int Base = 303300000;

        public static class SubjectType { public const int Party = Base, Contact = Base + 1, Asset = Base + 2, Licence = Base + 3, Listing = Base + 4, Lot = Base + 5, Deal = Base + 6, Document = Base + 7; }
        public static class RunStatus { public const int Running = Base, Succeeded = Base + 1, Failed = Base + 2, Paused = Base + 3; }
        public static class ModelOutcome { public const int Ok = Base, Abstain = Base + 1, SchemaFail = Base + 2, Error = Base + 3; }
        public static class FactStatus { public const int Verified = Base, Documented = Base + 1, Claimed = Base + 2, Unverified = Base + 3, Conflicting = Base + 4, Outdated = Base + 5, Missing = Base + 6, NotApplicable = Base + 7; }
        public static class Sensitivity { public const int MarketSafe = Base, MarketGeneralise = Base + 1, Internal = Base + 2, Restricted = Base + 3; }
        public static class MessageKind { public const int Market = Base, Source = Base + 1; }
        public static class Realiser { public const int Llm = Base, Template = Base + 1; }
        public static class Draft { public const int Draft_ = Base; }
        public static class ReviewStatus { public const int Open = Base; }
        public static class MatchStatus { public const int Proposed = Base; }
        public static class MilestoneStatus { public const int Pending = Base; }
        public static class ContractStatus { public const int Draft = Base; }
        public static class ParseStatus { public const int Pending = Base, Parsed = Base + 1, Failed = Base + 2, Quarantined = Base + 3; }
        public static class DocTypeMethod { public const int Llm = Base + 3; }
        public static class SourceKind { public const int Document = Base; }
        public static class ExtractionMethod { public const int Llm = Base + 2; }

        public static readonly string[] ReviewKinds = { "Approval", "Review", "Question To Human", "Conflict Resolution" };
        public static readonly string[] ReviewPurposes = { "Tier Upgrade", "Listing Publish", "Screening Clearance", "Contract Issue", "Fund Release", "Refund", "Shipment Booking", "Rate Table Change", "Message Send", "Other" };
        public static readonly string[] AdminRoles = { "Super Admin", "Verification Officer", "Deal Manager", "Compliance Officer", "Finance", "Logistics Coordinator", "Support" };
        public static readonly string[] QuestionKinds = { "Value", "Evidence", "Clarify Conflict", "Update Outdated", "Legibility" };
        public static readonly string[] KycCheckTypes = { "Company Registry", "Id Document", "UBO", "Proof Of Funds", "Bank Reference", "Address" };
        public static readonly string[] KycResults = { "Pass", "Fail", "Refer", "Pending" };
        public static readonly string[] MilestoneTypes = { "Inspection", "Loading", "BL Issued", "Departure", "Arrival", "Customs Cleared", "Discharge Inspection", "Delivered" };
        public static readonly string[] FactStatuses = { "Verified", "Documented", "Claimed", "Unverified", "Conflicting", "Outdated", "Missing", "Not Applicable" };
        public static readonly string[] DocTypes = { "Assay", "Certificate Of Analysis", "Inspection Report", "Mining Licence", "Export Permit", "Certificate Of Origin", "Incorporation Certificate", "Shareholder Register", "UBO Declaration", "Id Document", "Proof Of Funds", "Bank Reference", "LOI", "ICPO", "FCO", "SPA", "Contract", "Invoice", "Packing List", "Bill Of Lading", "Insurance Certificate", "Warehouse Receipt", "Photo", "Production Report", "Technical Report", "Mandate", "Chat Export", "Other", "Unknown" };

        /// <summary>Maps a label from one of the arrays above to its choice value; -1 when unknown.</summary>
        public static int ValueOf(string[] labels, string label)
        {
            if (label == null) return -1;
            for (var i = 0; i < labels.Length; i++)
                if (string.Equals(labels[i], label.Trim(), StringComparison.OrdinalIgnoreCase)) return Base + i;
            return -1;
        }
    }

    /// <summary>Dataverse helpers shared by tools and agents.</summary>
    public sealed class Dv
    {
        private static readonly HashSet<string> NoiseColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "createdby", "createdonbehalfby", "modifiedby", "modifiedonbehalfby", "owningbusinessunit", "owninguser",
            "owningteam", "versionnumber", "timezoneruleversionnumber", "utcconversiontimezonecode", "importsequencenumber",
            "overriddencreatedon", "processid", "stageid", "traversedpath", "exchangerate", "transactioncurrencyid"
        };

        public readonly IOrganizationService Svc;
        public readonly IOrganizationService System;
        private Dictionary<string, string> _settings;

        public Dv(IOrganizationService userService, IOrganizationService systemService)
        {
            Svc = userService;
            System = systemService;
        }

        public string Setting(string key, string fallback = null)
        {
            if (_settings == null)
            {
                _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var q = new QueryExpression("gc_platformsetting") { ColumnSet = new ColumnSet("gc_key", "gc_value") };
                foreach (var e in System.RetrieveMultiple(q).Entities)
                {
                    var k = e.GetAttributeValue<string>("gc_key");
                    if (!string.IsNullOrEmpty(k)) _settings[k] = e.GetAttributeValue<string>("gc_value");
                }
            }
            string v;
            return _settings.TryGetValue(key, out v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
        }

        public int SettingInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Setting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public double SettingNum(string key, double fallback)
        {
            double v;
            return double.TryParse(Setting(key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        /// <summary>Reads a secret from gc_secret as SYSTEM; the table has no privileges for normal roles.</summary>
        public string Secret(string name)
        {
            var q = new QueryExpression("gc_secret") { ColumnSet = new ColumnSet("gc_value"), TopCount = 1 };
            q.Criteria.AddCondition("gc_name", ConditionOperator.Equal, name);
            var e = System.RetrieveMultiple(q).Entities.FirstOrDefault();
            return e == null ? null : e.GetAttributeValue<string>("gc_value");
        }

        private static readonly ConcurrentDictionary<string, Dictionary<string, AttributeTypeCode?>> ColumnCache =
            new ConcurrentDictionary<string, Dictionary<string, AttributeTypeCode?>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Column logical names and types of a table (cached). Used to validate model-supplied columns,
        /// because a failing service call inside the plug-in transaction aborts the whole run.</summary>
        public Dictionary<string, AttributeTypeCode?> Columns(string table)
        {
            return ColumnCache.GetOrAdd(table, t =>
            {
                var resp = (RetrieveEntityResponse)System.Execute(new RetrieveEntityRequest { LogicalName = t, EntityFilters = EntityFilters.Attributes });
                return resp.EntityMetadata.Attributes
                    .Where(a => a.AttributeOf == null)
                    .ToDictionary(a => a.LogicalName, a => a.AttributeType, StringComparer.OrdinalIgnoreCase);
            });
        }

        public ColumnSet SafeColumns(string table, string[] columns)
        {
            if (columns == null || columns.Length == 0) return new ColumnSet(true);
            var known = Columns(table);
            return new ColumnSet(columns.Where(c => c != null && known.ContainsKey(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        public static string PrimaryKey(string table) { return table + "id"; }

        /// <summary>Retrieve that returns null instead of throwing when the record does not exist (or is not visible).</summary>
        public Entity Retrieve(string table, Guid id, params string[] columns)
        {
            var q = new QueryExpression(table) { ColumnSet = SafeColumns(table, columns), TopCount = 1 };
            q.Criteria.AddCondition(PrimaryKey(table), ConditionOperator.Equal, id);
            return Svc.RetrieveMultiple(q).Entities.FirstOrDefault();
        }

        public bool Exists(string table, Guid id)
        {
            return Retrieve(table, id, PrimaryKey(table)) != null;
        }

        public List<Entity> Query(string table, string[] columns, int top, params object[] conditions)
        {
            var q = new QueryExpression(table) { ColumnSet = SafeColumns(table, columns), TopCount = top };
            for (var i = 0; i + 2 < conditions.Length; i += 3)
            {
                var op = (ConditionOperator)conditions[i + 1];
                var val = conditions[i + 2];
                if (op == ConditionOperator.Null || op == ConditionOperator.NotNull) q.Criteria.AddCondition((string)conditions[i], op);
                else q.Criteria.AddCondition((string)conditions[i], op, val);
            }
            q.AddOrder("createdon", OrderType.Descending);
            return Svc.RetrieveMultiple(q).Entities.ToList();
        }

        /// <summary>Converts a record into plain JSON-friendly values for the model.</summary>
        public static Dictionary<string, object> Flatten(Entity e, int maxText = 3000)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["id"] = e.Id.ToString();
            d["table"] = e.LogicalName;
            foreach (var kv in e.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                if (NoiseColumns.Contains(kv.Key) || kv.Key == e.LogicalName + "id") continue;
                var v = kv.Value;
                if (v == null) continue;
                string formatted;
                e.FormattedValues.TryGetValue(kv.Key, out formatted);
                var er = v as EntityReference;
                var os = v as OptionSetValue;
                var osc = v as OptionSetValueCollection;
                var money = v as Money;
                var alias = v as AliasedValue;
                if (er != null) d[kv.Key] = J.Obj("id", er.Id.ToString(), "table", er.LogicalName, "name", er.Name ?? formatted);
                else if (os != null) d[kv.Key] = formatted ?? os.Value.ToString(CultureInfo.InvariantCulture);
                else if (osc != null) d[kv.Key] = formatted ?? string.Join(";", osc.Select(o => o.Value.ToString(CultureInfo.InvariantCulture)));
                else if (money != null) d[kv.Key] = money.Value;
                else if (alias != null) d[kv.Key] = alias.Value is EntityReference ? (object)((EntityReference)alias.Value).Id.ToString() : alias.Value;
                else if (v is string) d[kv.Key] = GeminiClient.Truncate((string)v, maxText);
                else if (v is bool || v is int || v is long || v is decimal || v is double || v is DateTime || v is Guid) d[kv.Key] = v;
                else d[kv.Key] = formatted ?? Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            return d;
        }

        public static List<object> FlattenAll(IEnumerable<Entity> list, int maxText = 1500)
        {
            return list.Select(e => (object)Flatten(e, maxText)).ToList();
        }

        public static string Label(Entity e, string column)
        {
            string f;
            return e.FormattedValues.TryGetValue(column, out f) ? f : null;
        }
    }
}

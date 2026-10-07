using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Portal
{
    /// <summary>
    /// Keeps gc_catalogentry (the public, masked marketplace catalog) in step with published listings.
    /// Steps (asynchronous): gc_listing Create/Update, gc_fact Create/Update, gc_lot Create/Update → refresh that listing's entry.
    /// Custom API gc_RefreshCatalog(ListingId?) → one listing, or every published listing plus removal of stale entries.
    /// Only market-safe data is copied: no seller, asset, listing title, exact location or Internal/Restricted facts.
    /// </summary>
    public sealed class CatalogPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(null); // SYSTEM: the catalog is derived data
            if (context.MessageName == "gc_RefreshCatalog")
            {
                var text = context.InputParameters.Contains("ListingId") ? context.InputParameters["ListingId"] as string : null;
                Guid one;
                if (!string.IsNullOrWhiteSpace(text) && Guid.TryParse(text, out one))
                {
                    context.OutputParameters["Refreshed"] = CatalogSync.Refresh(svc, one) ? 1 : 0;
                    context.OutputParameters["Removed"] = 0;
                }
                else
                {
                    int refreshed, removed;
                    CatalogSync.RefreshAll(svc, out refreshed, out removed);
                    context.OutputParameters["Refreshed"] = refreshed;
                    context.OutputParameters["Removed"] = removed;
                }
                return;
            }

            var target = context.InputParameters.Contains("Target") ? context.InputParameters["Target"] as Entity : null;
            if (target == null) return;
            Guid? listingId = null;
            switch (context.PrimaryEntityName)
            {
                case "gc_listing":
                    listingId = target.Id;
                    break;
                case "gc_fact":
                case "gc_lot":
                    var row = svc.Retrieve(context.PrimaryEntityName, target.Id, new ColumnSet("gc_listing"));
                    var r = row.GetAttributeValue<EntityReference>("gc_listing");
                    listingId = r == null ? (Guid?)null : r.Id;
                    break;
            }
            if (listingId != null) CatalogSync.Refresh(svc, listingId.Value);
        }
    }

    public static class CatalogSync
    {
        private const int Published = Choice.Base + 4;

        /// <summary>Creates, updates or removes the entry for one listing. Returns true when an entry exists afterwards.</summary>
        public static bool Refresh(IOrganizationService svc, Guid listingId)
        {
            var l = svc.RetrieveMultiple(new QueryExpression("gc_listing")
            {
                ColumnSet = new ColumnSet("gc_status", "statecode", "gc_commodity", "gc_grade", "gc_quantity", "gc_quantityunit", "gc_askprice",
                                          "gc_currency", "gc_pricebasis", "gc_incoterm", "gc_namedplace", "gc_origincountry", "gc_badge", "gc_publishedon"),
                Criteria = { Conditions = { new ConditionExpression("gc_listingid", ConditionOperator.Equal, listingId) } }
            }).Entities.FirstOrDefault();
            var existing = svc.RetrieveMultiple(new QueryExpression("gc_catalogentry")
            {
                ColumnSet = new ColumnSet(false),
                Criteria = { Conditions = { new ConditionExpression("gc_listing", ConditionOperator.Equal, listingId) } }
            }).Entities.ToList();

            var show = l != null && Opt(l, "gc_status") == Published && Opt(l, "statecode") == 0;
            if (!show)
            {
                foreach (var e in existing) svc.Delete("gc_catalogentry", e.Id);
                return false;
            }

            var commodity = l.GetAttributeValue<EntityReference>("gc_commodity");
            var com = commodity == null ? null : svc.Retrieve("gc_commodity", commodity.Id, new ColumnSet("gc_name", "gc_family", "gc_form"));
            var unit = Label(l, "gc_quantityunit");
            var entry = new Entity("gc_catalogentry");
            entry["gc_name"] = Trunc(string.Join(", ", new[] { Name(com, commodity), l.GetAttributeValue<string>("gc_grade"),
                l.GetAttributeValue<decimal?>("gc_quantity") == null ? null : l.GetAttributeValue<decimal?>("gc_quantity").Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + unit }
                .Where(s => !string.IsNullOrWhiteSpace(s))), 200);
            entry["gc_listing"] = new EntityReference("gc_listing", listingId);
            entry["gc_commodity"] = Trunc(Name(com, commodity), 200);
            entry["gc_family"] = com == null ? null : Label(com, "gc_family");
            entry["gc_form"] = com == null ? null : Label(com, "gc_form");
            entry["gc_grade"] = Trunc(l.GetAttributeValue<string>("gc_grade"), 200);
            entry["gc_quantity"] = l.GetAttributeValue<decimal?>("gc_quantity");
            entry["gc_unit"] = unit;
            entry["gc_availablequantity"] = AvailableQuantity(svc, listingId);
            entry["gc_askprice"] = l.GetAttributeValue<decimal?>("gc_askprice");
            entry["gc_currency"] = Trunc(l.GetAttributeValue<string>("gc_currency"), 3);
            entry["gc_pricebasis"] = Label(l, "gc_pricebasis");
            entry["gc_incoterm"] = Label(l, "gc_incoterm");
            entry["gc_namedplace"] = Trunc(l.GetAttributeValue<string>("gc_namedplace"), 200);
            var origin = l.GetAttributeValue<EntityReference>("gc_origincountry");
            entry["gc_origincountry"] = origin == null ? null : Trunc(origin.Name ?? svc.Retrieve("gc_country", origin.Id, new ColumnSet("gc_name")).GetAttributeValue<string>("gc_name"), 200);
            entry["gc_badge"] = Label(l, "gc_badge") ?? "None";
            entry["gc_publishedon"] = l.GetAttributeValue<DateTime?>("gc_publishedon");
            entry["gc_facts"] = Json.Serialize(MarketFacts(svc, listingId));
            entry["gc_refreshedon"] = DateTime.UtcNow;

            if (existing.Count == 0) svc.Create(entry);
            else
            {
                entry.Id = existing[0].Id;
                svc.Update(entry);
                foreach (var dup in existing.Skip(1)) svc.Delete("gc_catalogentry", dup.Id);
            }
            return true;
        }

        public static void RefreshAll(IOrganizationService svc, out int refreshed, out int removed)
        {
            refreshed = 0;
            removed = 0;
            var published = svc.RetrieveMultiple(new QueryExpression("gc_listing")
            {
                ColumnSet = new ColumnSet(false),
                TopCount = 500,
                Criteria = { Conditions = { new ConditionExpression("gc_status", ConditionOperator.Equal, Published), new ConditionExpression("statecode", ConditionOperator.Equal, 0) } }
            }).Entities.Select(e => e.Id).ToList();
            foreach (var id in published) if (Refresh(svc, id)) refreshed++;
            var entries = svc.RetrieveMultiple(new QueryExpression("gc_catalogentry") { ColumnSet = new ColumnSet("gc_listing"), TopCount = 5000 }).Entities;
            foreach (var e in entries)
            {
                var listing = e.GetAttributeValue<EntityReference>("gc_listing");
                if (listing != null && published.Contains(listing.Id)) continue;
                svc.Delete("gc_catalogentry", e.Id);
                removed++;
            }
        }

        /// <summary>Facts whose attribute is Market Safe / Market Generalise and whose status is Verified, Documented or Claimed.</summary>
        private static List<object> MarketFacts(IOrganizationService svc, Guid listingId)
        {
            var defs = svc.RetrieveMultiple(new QueryExpression("gc_attributedef") { ColumnSet = new ColumnSet("gc_key", "gc_name", "gc_sensitivity"), TopCount = 500 })
                .Entities.Where(d => Opt(d, "gc_sensitivity") == Choice.Sensitivity.MarketSafe || Opt(d, "gc_sensitivity") == Choice.Sensitivity.MarketGeneralise)
                .GroupBy(d => d.GetAttributeValue<string>("gc_key") ?? "").ToDictionary(g => g.Key, g => g.First().GetAttributeValue<string>("gc_name"));
            var facts = svc.RetrieveMultiple(new QueryExpression("gc_fact")
            {
                ColumnSet = new ColumnSet("gc_attributekey", "gc_displayvalue", "gc_status", "gc_freshnessexpireson"),
                TopCount = 200,
                Criteria =
                {
                    Conditions =
                    {
                        new ConditionExpression("gc_subjecttype", ConditionOperator.Equal, Choice.SubjectType.Listing),
                        new ConditionExpression("gc_subjectid", ConditionOperator.Equal, listingId.ToString()),
                        new ConditionExpression("statecode", ConditionOperator.Equal, 0)
                    }
                }
            }).Entities;
            return facts.Where(f => defs.ContainsKey(f.GetAttributeValue<string>("gc_attributekey") ?? "") && Opt(f, "gc_status") <= Choice.FactStatus.Claimed && Opt(f, "gc_status") >= Choice.Base)
                .OrderBy(f => Opt(f, "gc_status")).ThenBy(f => f.GetAttributeValue<string>("gc_attributekey"))
                .Select(f => (object)J.Obj("key", f.GetAttributeValue<string>("gc_attributekey"), "name", defs[f.GetAttributeValue<string>("gc_attributekey")],
                                           "value", f.GetAttributeValue<string>("gc_displayvalue"), "status", Label(f, "gc_status"),
                                           "fresh_until", f.GetAttributeValue<DateTime?>("gc_freshnessexpireson") == null ? null
                                               : f.GetAttributeValue<DateTime?>("gc_freshnessexpireson").Value.ToString("yyyy-MM-dd"))).ToList();
        }

        private static decimal AvailableQuantity(IOrganizationService svc, Guid listingId)
        {
            return svc.RetrieveMultiple(new QueryExpression("gc_lot")
            {
                ColumnSet = new ColumnSet("gc_quantity"),
                Criteria = { Conditions = { new ConditionExpression("gc_listing", ConditionOperator.Equal, listingId), new ConditionExpression("gc_status", ConditionOperator.Equal, Choice.Base) } }
            }).Entities.Sum(e => e.GetAttributeValue<decimal?>("gc_quantity") ?? 0);
        }

        private static string Name(Entity com, EntityReference r) { return com != null ? com.GetAttributeValue<string>("gc_name") : r == null ? null : r.Name; }

        private static int Opt(Entity e, string column)
        {
            var o = e.GetAttributeValue<OptionSetValue>(column);
            return o == null ? -1 : o.Value;
        }

        private static string Label(Entity e, string column)
        {
            string f;
            return e.FormattedValues.TryGetValue(column, out f) ? f : null;
        }

        private static string Trunc(string s, int n) { return s == null ? null : (s.Length <= n ? s : s.Substring(0, n)); }
    }
}

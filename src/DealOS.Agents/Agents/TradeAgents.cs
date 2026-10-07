using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using DealOS.Agents.Tools;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Agents
{

    internal static class Offers
    {
        public static Dictionary<string, object> Summary(AgentContext ctx, Entity o, Entity deal)
        {
            var from = H.Ref(o, "gc_fromparty");
            string side = null;
            if (deal != null && from != null) side = from == H.Ref(deal, "gc_seller") ? "seller" : from == H.Ref(deal, "gc_buyer") ? "buyer" : "other";
            return J.Obj("id", o.Id.ToString(), "round", o.GetAttributeValue<int?>("gc_round"), "status", Dv.Label(o, "gc_status"), "from_side", side,
                         "price", o.GetAttributeValue<decimal?>("gc_price"), "quantity", o.GetAttributeValue<decimal?>("gc_quantity"),
                         "currency", o.GetAttributeValue<string>("gc_currency"), "incoterm", Dv.Label(o, "gc_incoterm"),
                         "named_place", o.GetAttributeValue<string>("gc_namedplace"), "payment_terms", Dv.Label(o, "gc_paymentterms"),
                         "valid_until", o.GetAttributeValue<DateTime?>("gc_validuntil"));
        }
    }

    /// <summary>Builds the term sheet from agreed terms and opens the contract draft for review.</summary>
    public sealed class ContractAgent : AgentDefinition
    {
        public override string Name { get { return "Contract"; } }
        public override string DisplayName { get { return "Contract"; } }
        public override string Description { get { return "Builds a term sheet strictly from the accepted offer and deal data, flags missing or non-standard terms, and opens a draft contract for Deal Manager review."; } }
        public override int AgentChoice { get { return Choice.Base + 7; } }
        public override string SubjectTable { get { return "gc_deal"; } }
        public override string[] Tools { get { return new[] { "create_contract_draft", "get_record", "query_records", "get_facts" }; } }
        public override string[] ReadTables { get { return new[] { "gc_deal", "gc_offer", "gc_pricequote", "account", "gc_commodity", "gc_listing", "gc_fact", "gc_contract", "gc_countryrule", "gc_commissionplan" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the Contract agent. Build the term sheet ONLY from CONTEXT (accepted offer, deal, parties, evidence). Do not invent terms.
Standard template 'spot-physical-v1' assumptions you may use: independent inspection at loading by a platform-appointed agency (final for weight and quality unless discharge inspection is agreed); payment through the platform escrow partner in tranches; title and risk per Incoterms 2020 for the agreed term; documents per the country rules.
Critical terms: seller, buyer, commodity, specification, quantity (+ tolerance), price + currency, Incoterm + named place, payment terms. If any critical term is missing, do NOT create the draft: list it in missing_terms and set needs_human.
Governing law and dispute resolution: if not in data, write 'To be confirmed by Deal Manager' and list them in missing_terms (they do not block the draft).
nonstandard_clauses = true if anything departs from the template.
If no critical term is missing, call create_contract_draft with the term sheet as JSON. Then call finish.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Term sheet",
                    "term_sheet", S.Obj(null,
                        "seller", S.Str("Seller legal name."), "buyer", S.Str("Buyer legal name."), "commodity", S.Str("Commodity."),
                        "specification", S.Str("Specification with evidence status."), "quantity", S.Str("Quantity, unit and tolerance."),
                        "price", S.Str("Price, currency and basis."), "incoterm", S.Str("Incoterm and named place."),
                        "payment_terms", S.Str("Payment terms and escrow tranches."), "inspection", S.Str("Inspection clause."),
                        "delivery", S.Str("Delivery period."), "documents", S.Str("Documents required."),
                        "title_and_risk", S.Str("Title/risk transfer."), "governing_law", S.Str("Governing law."), "dispute_resolution", S.Str("Dispute resolution.")),
                    "missing_terms", S.Arr("Missing terms.", S.Str("term")),
                    "nonstandard_clauses", S.Bool("Departs from template."),
                    "contract_created", S.Bool("True if create_contract_draft succeeded."));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var deal = H.Require(ctx);
            var offers = ctx.Dv.Query("gc_offer", null, 10, "gc_deal", ConditionOperator.Equal, deal.Id);
            var accepted = offers.FirstOrDefault(o => Dv.Label(o, "gc_status") == "Accepted");
            var listingId = H.Ref(deal, "gc_listing");
            var facts = listingId == null ? new List<FactRow>() : Evidence.Facts(ctx, Choice.SubjectType.Listing, listingId.Value);
            return J.Obj(
                "deal", Dv.Flatten(deal),
                "accepted_offer", accepted == null ? null : Offers.Summary(ctx, accepted, deal),
                "seller", H.Party(ctx, H.Ref(deal, "gc_seller"), true),
                "buyer", H.Party(ctx, H.Ref(deal, "gc_buyer"), true),
                "commodity", H.Commodity(ctx, H.Ref(deal, "gc_commodity")),
                "evidence", Evidence.FactsJson(facts),
                "commission_plan", H.CommissionPlan(ctx, H.Ref(deal, "gc_commissionplan")),
                "existing_contracts", ctx.Dv.Query("gc_contract", new[] { "gc_name", "gc_status" }, 5, "gc_deal", ConditionOperator.Equal, deal.Id).Select(c => (object)J.Obj("name", c.GetAttributeValue<string>("gc_name"), "status", Dv.Label(c, "gc_status"))).ToList(),
                "country_rules", H.CountryRules(ctx, new[] { H.Ref(deal, "gc_origincountry"), H.Ref(deal, "gc_destinationcountry") }, H.Ref(deal, "gc_commodity"), "Export Rules", "Import Rules", "Logistics"),
                "readable_tables", ReadTables);
        }

        public override void AfterFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            result["contract_created"] = ctx.Actions.OfType<Dictionary<string, object>>().Any(a => J.Str(a, "action") == "create_contract_draft");
            if (J.Arr(result, "missing_terms").Count > 0 || J.Bool(result, "nonstandard_clauses")) result["needs_human"] = true;
        }
    }
}

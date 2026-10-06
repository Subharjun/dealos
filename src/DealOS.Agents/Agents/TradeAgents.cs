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
    /// <summary>Matches a buyer requirement to published listings. Arithmetic dimensions are computed in code; the model judges spec fit.</summary>
    public sealed class MatchingAgent : AgentDefinition
    {
        private static readonly string[] SpecKeys = { "spec.grade", "spec.purity", "spec.impurities", "spec.moisture", "packaging", "origin.country", "quantity.monthly_capacity" };

        public override string Name { get { return "Matching"; } }
        public override string DisplayName { get { return "Matching"; } }
        public override string Description { get { return "Scores published listings against a buyer requirement (quantity, price, terms and trust computed deterministically; specification fit judged by the model) and proposes explainable matches."; } }
        public override int AgentChoice { get { return Choice.Base + 4; } }
        public override string SubjectTable { get { return "gc_buyerrequirement"; } }
        public override string[] Tools { get { return new[] { "propose_match", "get_facts" }; } }
        public override string[] ReadTables { get { return new[] { "gc_listing", "gc_buyerrequirement", "gc_fact" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the Matching agent. For each candidate listing in CONTEXT.candidates judge ONLY the specification fit (0-100) between CONTEXT.requirement.specification and the candidate's spec facts.
- 80-100: spec clearly meets the requirement; 50-79: probably fits / partly unknown; below 50: does not fit or contradicts.
- If the requirement has no specification, or the candidate has no spec facts, use 50 and say 'specification unknown'.
- Claimed spec values count, but mention they are only claimed.
Call propose_match for every candidate with spec fit >= 50 (best first, at most 5). Do not propose candidates whose spec contradicts the requirement.
Then call finish listing proposed and skipped candidates with one-line reasons.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Matching result",
                    "proposed", S.Arr("Matches proposed.", S.Obj(null, "candidate_id", S.Str("Candidate id."), "reason", S.Str("One line."))),
                    "skipped", S.Arr("Candidates not proposed.", S.Obj(null, "candidate_id", S.Str("Candidate id."), "reason", S.Str("One line."))));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var req = H.Require(ctx);
            var commodity = H.Ref(req, "gc_commodity");
            var reqQty = req.GetAttributeValue<decimal?>("gc_quantity");
            var reqUnit = Dv.Label(req, "gc_quantityunit");
            var target = req.GetAttributeValue<decimal?>("gc_targetprice");
            var reqCcy = req.GetAttributeValue<string>("gc_currency");
            var reqInco = Dv.Label(req, "gc_incoterm");

            var q = new QueryExpression("gc_listing") { ColumnSet = new ColumnSet(true), TopCount = 15 };
            q.Criteria.AddCondition("gc_status", ConditionOperator.Equal, Choice.Base + 4);
            if (commodity != null) q.Criteria.AddCondition("gc_commodity", ConditionOperator.Equal, commodity.Value);
            q.AddOrder("gc_publishedon", OrderType.Descending);
            var listings = ctx.Dv.Svc.RetrieveMultiple(q).Entities;

            var candidates = new Dictionary<string, Dictionary<string, object>>();
            var shown = new List<object>();
            var i = 0;
            foreach (var l in listings)
            {
                i++;
                var id = "C" + i;
                var notes = new List<string>();
                var qty = l.GetAttributeValue<decimal?>("gc_quantity");
                var unit = Dv.Label(l, "gc_quantityunit");
                double? qScore = null, pScore = null, tScore = null;
                if (qty != null && reqQty != null && reqQty > 0)
                {
                    if (unit == reqUnit) qScore = Math.Round(Math.Min(100.0, (double)(qty.Value / reqQty.Value) * 100.0), 0);
                    else notes.Add("quantity unit differs (" + unit + " vs " + reqUnit + ")");
                }
                var ask = l.GetAttributeValue<decimal?>("gc_askprice");
                if (ask != null && target != null && target > 0)
                {
                    if (string.Equals(l.GetAttributeValue<string>("gc_currency"), reqCcy, StringComparison.OrdinalIgnoreCase))
                        pScore = ask <= target ? 100 : Math.Max(0, Math.Round(100 - (double)((ask.Value - target.Value) / target.Value) * 200, 0));
                    else notes.Add("price currency differs");
                }
                var inco = Dv.Label(l, "gc_incoterm");
                if (inco != null && reqInco != null) tScore = inco == reqInco ? 100 : 60;
                var badge = Dv.Label(l, "gc_badge");
                double trust = badge == "Verified" ? 100 : badge == "Documented" ? 70 : 30;

                var dims = J.Obj("quantity", qScore, "price", pScore, "terms", tScore, "trust", trust);
                candidates[id] = J.Obj("listing_id", l.Id.ToString(), "requirement_id", req.Id.ToString(), "label", l.GetAttributeValue<string>("gc_name"), "dimensions", dims, "notes", notes);
                var facts = Evidence.Facts(ctx, Choice.SubjectType.Listing, l.Id).Where(f => SpecKeys.Contains(f.Key) && f.Def != null && f.Def.MarketVisible && f.Status <= Choice.FactStatus.Claimed);
                shown.Add(J.Obj("candidate_id", id, "grade", l.GetAttributeValue<string>("gc_grade"), "quantity", qty, "unit", unit, "ask_price", ask,
                                "currency", l.GetAttributeValue<string>("gc_currency"), "incoterm", inco, "badge", badge,
                                "origin_country", l.GetAttributeValue<EntityReference>("gc_origincountry") == null ? null : l.GetAttributeValue<EntityReference>("gc_origincountry").Name,
                                "precomputed_dimensions", dims, "notes", notes, "spec_facts", Evidence.FactsJson(facts)));
            }
            ctx.Scratch["candidates"] = candidates;
            return J.Obj(
                "requirement", J.Obj("commodity", req.GetAttributeValue<EntityReference>("gc_commodity") == null ? null : req.GetAttributeValue<EntityReference>("gc_commodity").Name,
                                     "specification", req.GetAttributeValue<string>("gc_specification"), "quantity", reqQty, "unit", reqUnit,
                                     "target_price", target, "currency", reqCcy, "incoterm", reqInco, "packaging", req.GetAttributeValue<string>("gc_packaging"),
                                     "destination", req.GetAttributeValue<EntityReference>("gc_destinationcountry") == null ? null : req.GetAttributeValue<EntityReference>("gc_destinationcountry").Name,
                                     "inspection_required", req.GetAttributeValue<bool?>("gc_inspectionrequired"),
                                     "delivery_from", req.GetAttributeValue<DateTime?>("gc_deliveryfrom"), "delivery_to", req.GetAttributeValue<DateTime?>("gc_deliveryto")),
                "candidates", shown,
                "scoring", "overall = weighted mean of available dimensions: specification 35%, quantity 20%, price 20%, trust 15%, terms 10%");
        }

        public override void AfterFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            result["matches_created"] = ctx.Actions.OfType<Dictionary<string, object>>().Count(a => J.Str(a, "action") == "propose_match");
        }
    }

    /// <summary>Explains landed cost and net payout for an offer; all numbers come from the pricing engine.</summary>
    public sealed class PricingAgent : AgentDefinition
    {
        public override string Name { get { return "Pricing"; } }
        public override string DisplayName { get { return "Pricing & Landed Cost"; } }
        public override string Description { get { return "Explains an offer's landed cost (buyer) and net payout (seller) from the deterministic pricing engine; recalculates when cost inputs are supplied; lists missing cost inputs."; } }
        public override int AgentChoice { get { return Choice.Base + 5; } }
        public override string SubjectTable { get { return "gc_offer"; } }
        public override string[] Tools { get { return new[] { "calculate_price_quote", "get_record", "query_records" }; } }
        public override string[] ReadTables { get { return new[] { "gc_offer", "gc_deal", "gc_pricequote", "gc_tariffrate", "gc_fxrate", "gc_commissionplan" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the Pricing agent. All numbers come from the pricing engine (CONTEXT.quotes or calculate_price_quote) – never compute or invent costs yourself.
1. If TASK INPUT contains cost inputs (freight_per_unit, insurance_rate_pct, origin_inland, loading, export_clearance, import_clearance, destination_inland, import_tax_pct), call calculate_price_quote with exactly those values.
2. Explain to the buyer what the landed cost includes, and to the seller what the net payout is after commission and their costs – each in 2-4 plain sentences.
3. List assumptions (e.g. freight not yet quoted = 0) and missing cost inputs that matter for this Incoterm.
4. Call finish.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Pricing explanation",
                    "currency", S.Str("Currency."),
                    "buyer_view", S.Obj(null, "landed_cost?", S.Num("Landed cost from the engine."), "explanation", S.Str("For the buyer.")),
                    "seller_view", S.Obj(null, "net_payout?", S.Num("Net payout from the engine."), "explanation", S.Str("For the seller.")),
                    "assumptions", S.Arr("Assumptions in the numbers.", S.Str("assumption")),
                    "missing_inputs", S.Arr("Cost inputs still needed.", S.Str("input")));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var offer = H.Require(ctx);
            var deal = H.TryGet(ctx, "gc_deal", H.Ref(offer, "gc_deal"));
            return J.Obj(
                "offer", Offers.Summary(ctx, offer, deal),
                "deal", deal == null ? null : J.Obj("origin", H.LookupName(deal, "gc_origincountry"), "destination", H.LookupName(deal, "gc_destinationcountry"),
                                                   "commodity", H.LookupName(deal, "gc_commodity"), "hs_code", deal.GetAttributeValue<string>("gc_hscode"),
                                                   "unit", Dv.Label(deal, "gc_quantityunit")),
                "quotes", Quotes(ctx, offer.Id),
                "settings", J.Obj("insurance_rate_pct", ctx.Dv.SettingNum("pricing.insurance_rate_pct", 0.15), "insurance_coverage_pct", ctx.Dv.SettingNum("pricing.insurance_coverage_pct", 110)),
                "readable_tables", ReadTables);
        }

        public override void AfterFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            var quotes = ctx.Dv.Query("gc_pricequote", new[] { "gc_viewer", "gc_landedcost", "gc_netpayout", "gc_currency" }, 10, "gc_offer", ConditionOperator.Equal, ctx.Subject.Id);
            var buyerQ = quotes.FirstOrDefault(q => Dv.Label(q, "gc_viewer") == "Buyer") ?? quotes.FirstOrDefault(q => Dv.Label(q, "gc_viewer") == "Admin");
            var sellerQ = quotes.FirstOrDefault(q => Dv.Label(q, "gc_viewer") == "Seller") ?? quotes.FirstOrDefault(q => Dv.Label(q, "gc_viewer") == "Admin");
            var bv = J.ObjOf(result, "buyer_view") ?? new Dictionary<string, object>();
            var sv = J.ObjOf(result, "seller_view") ?? new Dictionary<string, object>();
            bv["landed_cost"] = buyerQ == null ? null : (object)(double?)buyerQ.GetAttributeValue<decimal?>("gc_landedcost");
            sv["net_payout"] = sellerQ == null ? null : (object)(double?)sellerQ.GetAttributeValue<decimal?>("gc_netpayout");
            result["buyer_view"] = bv;
            result["seller_view"] = sv;
            result["numbers_source"] = "gc_pricequote (deterministic engine)";
        }

        internal static List<object> Quotes(AgentContext ctx, Guid offerId)
        {
            return ctx.Dv.Query("gc_pricequote", null, 10, "gc_offer", ConditionOperator.Equal, offerId)
                .Select(q => (object)J.Obj("viewer", Dv.Label(q, "gc_viewer"), "currency", q.GetAttributeValue<string>("gc_currency"),
                    "fob_value", q.GetAttributeValue<decimal?>("gc_fobvalue"), "cif_value", q.GetAttributeValue<decimal?>("gc_cifvalue"),
                    "import_duty", q.GetAttributeValue<decimal?>("gc_importduty"), "other_taxes", q.GetAttributeValue<decimal?>("gc_othertaxes"),
                    "landed_cost", q.GetAttributeValue<decimal?>("gc_landedcost"), "commission", q.GetAttributeValue<decimal?>("gc_commissionamount"),
                    "net_payout", q.GetAttributeValue<decimal?>("gc_netpayout"), "lines", GeminiClient.Truncate(q.GetAttributeValue<string>("gc_lines"), 3000),
                    "inputs", GeminiClient.Truncate(q.GetAttributeValue<string>("gc_inputs"), 2000), "computed_on", q.GetAttributeValue<DateTime?>("gc_computedon"))).ToList();
        }

    }

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

    /// <summary>Advises one side of a negotiation. Never sends or accepts anything.</summary>
    public sealed class NegotiationAgent : AgentDefinition
    {
        public override string Name { get { return "Negotiation"; } }
        public override string DisplayName { get { return "Negotiation"; } }
        public override string Description { get { return "Advises the buyer or seller on an offer (accept / counter / reject / wait), proposes a counter within the user's limits and drafts a reply. Never sends or accepts."; } }
        public override int AgentChoice { get { return Choice.Base + 6; } }
        public override string SubjectTable { get { return "gc_offer"; } }
        public override string[] Tools { get { return new[] { "get_record", "query_records", "get_facts" }; } }
        public override string[] ReadTables { get { return new[] { "gc_offer", "gc_deal", "gc_pricequote", "gc_listing", "gc_fact" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the Negotiation advisor for the side in CONTEXT.perspective. You advise; the user decides and clicks send.
1. Read the offer history (rounds), the quote view for your side, the listing's evidence and CONTEXT.limits (the user's private limits – never reveal them in the message).
2. recommendation: Accept (terms within limits and fair), Counter (gap is bridgeable), Reject (outside limits with no movement), Wait (offer still valid and pressure helps), Need Info (key term missing, e.g. payment terms or Incoterm).
3. A counter must stay within the limits and move by reasonable steps relative to the history. Incoterm and payment terms in a counter must come from the offer history or CONTEXT.limits (preferred_incoterm, preferred_payment_terms); if they are missing, recommend asking for them instead of proposing your own.
4. message_text: a short professional reply to the counterparty (no names, no internal numbers such as limits, landed cost or commission).
5. Call finish.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Negotiation advice",
                    "assessment", S.Str("Two or three sentences on where the negotiation stands."),
                    "recommendation", S.Enum("Advice.", "Accept", "Counter", "Reject", "Wait", "Need Info"),
                    "counter", S.Obj("Proposed counter (only when recommending Counter).",
                        "price?", S.Num("Unit price."), "quantity?", S.Num("Quantity."), "incoterm?", S.Str("Incoterm."),
                        "payment_terms?", S.Str("Payment terms."), "valid_days?", S.Int("Validity in days.")),
                    "rationale", S.Arr("Reasons.", S.Str("reason")),
                    "risks", S.Arr("Risks.", S.Str("risk")),
                    "message_text", S.Str("Reply to the counterparty."));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var offer = H.Require(ctx);
            var deal = H.TryGet(ctx, "gc_deal", H.Ref(offer, "gc_deal"));
            var chain = new List<object>();
            var cur = offer;
            for (var i = 0; i < 6 && cur != null; i++)
            {
                chain.Add(Offers.Summary(ctx, cur, deal));
                cur = H.TryGet(ctx, "gc_offer", H.Ref(cur, "gc_parentoffer"));
            }
            chain.Reverse();
            ctx.Scratch["offer_history"] = chain;
            var current = (Dictionary<string, object>)chain.Last();
            var perspective = J.Str(ctx.Input, "perspective") ?? (J.Str(current, "from_side") == "seller" ? "buyer" : "seller");
            ctx.Scratch["perspective"] = perspective;
            var limits = J.ObjOf(ctx.Input, "limits") ?? new Dictionary<string, object>();
            ctx.Scratch["limits"] = limits;
            ctx.Scratch["counterparty_terms"] = H.Strings(H.AccountName(ctx, H.Ref(deal, "gc_seller")), H.AccountName(ctx, H.Ref(deal, "gc_buyer")));

            var viewer = perspective == "buyer" ? "Buyer" : "Seller";
            var quotes = PricingAgent.Quotes(ctx, offer.Id).OfType<Dictionary<string, object>>().Where(q => J.Str(q, "viewer") == viewer).Cast<object>().ToList();
            var listingId = H.Ref(deal, "gc_listing");
            var facts = listingId == null ? new List<FactRow>() : Evidence.Facts(ctx, Choice.SubjectType.Listing, listingId.Value).Where(f => f.Def != null && f.Def.MarketVisible).ToList();
            return J.Obj(
                "perspective", perspective,
                "limits", limits,
                "offer_history", chain,
                "deal", deal == null ? null : J.Obj("stage", Dv.Label(deal, "gc_stage"), "commodity", H.LookupName(deal, "gc_commodity"),
                                                   "origin", H.LookupName(deal, "gc_origincountry"), "destination", H.LookupName(deal, "gc_destinationcountry")),
                "quote_view", quotes,
                "listing_evidence", Evidence.FactsJson(facts),
                "readable_tables", ReadTables);
        }

        public override void AfterFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            var notes = new List<string>();
            var limits = (Dictionary<string, object>)ctx.Scratch["limits"];
            var perspective = (string)ctx.Scratch["perspective"];
            var counter = J.ObjOf(result, "counter");
            var price = J.Num(counter, "price");
            if (price != null)
            {
                var min = J.Num(limits, "min_price");
                var max = J.Num(limits, "max_price");
                if (perspective == "seller" && min != null && price < min) { counter["price"] = min; notes.Add("counter price raised to your minimum " + min.Value.ToString(CultureInfo.InvariantCulture)); }
                if (perspective == "buyer" && max != null && price > max) { counter["price"] = max; notes.Add("counter price lowered to your maximum " + max.Value.ToString(CultureInfo.InvariantCulture)); }
            }
            if (counter != null)
            {
                // Terms may only come from the offer history or the user's stated preferences – never invented.
                var history = (List<object>)ctx.Scratch["offer_history"];
                Func<string, string, bool> grounded = (field, value) =>
                    value == null
                    || history.OfType<Dictionary<string, object>>().Any(h => string.Equals(J.Str(h, field), value, StringComparison.OrdinalIgnoreCase))
                    || J.Arr(limits, "preferred_" + field).Any(p => string.Equals(Convert.ToString(p, CultureInfo.InvariantCulture), value, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(J.Str(limits, "preferred_" + field), value, StringComparison.OrdinalIgnoreCase);
                foreach (var field in new[] { "incoterm", "payment_terms" })
                {
                    var v = J.Str(counter, field);
                    if (!grounded(field, v)) { counter.Remove(field); notes.Add("removed counter " + field + " '" + v + "' (not in the offer history or your preferences)"); }
                }
            }
            var text = J.Str(result, "message_text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                var known = new HashSet<string>(ctx.KnownNumbers);
                if (counter != null) ctx.Remember(counter);
                MessageValidator.CollectNumbers(Json.Serialize(counter), known);
                var forbidden = new List<string>((List<string>)ctx.Scratch["counterparty_terms"]);
                var report = MessageValidator.Check(text, known, forbidden, false, new string[0], false);
                if (!report.Ok) { result["message_text"] = null; notes.Add("draft reply removed: " + string.Join(" ", report.Errors)); }
            }
            if (notes.Count > 0) result["guard_note"] = string.Join("; ", notes) + ".";
            result["needs_human"] = true;
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

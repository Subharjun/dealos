using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using DealOS.Agents.Tools;
using Microsoft.Xrm.Sdk;

namespace DealOS.Agents.Agents
{
    /// <summary>KYB for a counterparty. Seller and buyer variants differ only in the checks they require.</summary>
    public class PartyVerificationAgent : AgentDefinition
    {
        private static readonly string[] Tiers = { "Unverified", "Basic", "KYB Verified", "Trade Verified", "Trusted" };
        private readonly bool _buyer;

        public PartyVerificationAgent() : this(false) { }

        protected PartyVerificationAgent(bool buyer) { _buyer = buyer; }

        public override string Name { get { return _buyer ? "BuyerVerification" : "OnboardingKYB"; } }
        public override string DisplayName { get { return _buyer ? "Buyer Verification" : "Onboarding KYB"; } }
        public override string Description
        {
            get
            {
                return _buyer
                    ? "Buyer KYB: company, UBO, proof of funds / bank reference, screening; records pending checks, asks for missing documents, recommends a trust tier."
                    : "Seller/party KYB: company, UBO, directors' ID, authority to sell, screening; records pending checks, asks for missing documents, recommends a trust tier.";
            }
        }
        public override int AgentChoice { get { return Choice.Base + (_buyer ? 2 : 0); } }
        public override string SubjectTable { get { return "account"; } }
        public override string[] Tools { get { return new[] { "record_kyc_check", "ask_question", "draft_message", "create_review_task", "get_facts", "get_record", "query_records" }; } }
        public override string[] ReadTables { get { return new[] { "account", "contact", "gc_kyccheck", "gc_screening", "gc_document", "gc_fact", "gc_country", "gc_countryrule", "gc_licence", "gc_partylink" }; } }

        private string[] RequiredChecks
        {
            get
            {
                return _buyer
                    ? new[] { "Company Registry", "UBO", "Id Document", "Proof Of Funds or Bank Reference" }
                    : new[] { "Company Registry", "UBO", "Id Document", "Address", "Authority to sell (mining licence, mandate or ownership of stock)" };
            }
        }

        public override string Instructions
        {
            get
            {
                return
@"You are the " + DisplayName + @" agent. Decide what this counterparty still needs to be trusted, and get it moving.
Tier ladder: Unverified → Basic (legal name + registration number provided) → KYB Verified (Company Registry PASS, UBO PASS, ID of UBOs/directors PASS, no open screening match, no compliance hold" + (_buyer ? ", Proof Of Funds or Bank Reference PASS" : "") + @") → Trade Verified / Trusted (only from completed trades – never recommend these).
1. Compare CONTEXT.required_checks with CONTEXT.kyc_checks, CONTEXT.documents and CONTEXT.facts.
2. For each required check with no record: call record_kyc_check with result Pending and say exactly which document is needed. Use Refer only when the data shows a problem (mismatch, expired document, high-risk country).
3. Screening: any 'Potential Match' or 'Confirmed Match' → create_review_task (purpose Screening Clearance, kind Review, role Compliance Officer). No screening at all → note it in missing_items.
4. Ask the party for missing documents: ask_question (max CONTEXT.limits.max_asks), then draft_message audience Source – short, professional, list the documents.
5. If the evidence supports a higher tier than the current one, create_review_task (purpose Tier Upgrade, kind Approval, role Verification Officer).
6. Call finish. recommended_tier must follow the ladder strictly.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Party verification result",
                    "current_tier", S.Str("Tier on the account now."),
                    "recommended_tier", S.Enum("Tier the evidence supports.", Tiers),
                    "checks", S.Arr("Status of each required check.", S.Obj(null,
                        "check", S.Str("Required check."),
                        "status", S.Enum("Status.", "Pass", "Fail", "Refer", "Pending", "Missing"),
                        "evidence", S.Str("Where this comes from."))),
                    "missing_items", S.Arr("Documents or data still needed.", S.Str("item")),
                    "risk_flags", S.Arr("Risks for compliance.", S.Str("flag")));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var acc = H.Require(ctx);
            ctx.Scratch["account_id"] = acc.Id;
            var contacts = H.Rows(ctx, "contact", "parentcustomerid", acc.Id, 25, "fullname", "jobtitle", "gc_isubo", "gc_ownershippct", "gc_pepstatus", "gc_country")
                .Select(c => (object)J.Obj("id", c.Id.ToString(), "name", c.GetAttributeValue<string>("fullname"), "role", c.GetAttributeValue<string>("jobtitle"),
                                           "is_ubo", c.GetAttributeValue<bool?>("gc_isubo") ?? false, "ownership_pct", c.GetAttributeValue<decimal?>("gc_ownershippct"),
                                           "pep", Dv.Label(c, "gc_pepstatus"))).ToList();
            var countryId = H.Ref(acc, "gc_country");
            var facts = Evidence.Facts(ctx, Choice.SubjectType.Party, acc.Id);
            ctx.Scratch["facts"] = facts;
            return J.Obj(
                "party", H.Party(ctx, acc.Id, true),
                "contacts", contacts,
                "required_checks", RequiredChecks,
                "kyc_checks", H.KycChecks(ctx, acc.Id),
                "screenings", H.Screenings(ctx, acc.Id),
                "documents", H.Documents(ctx, "gc_account", acc.Id),
                "facts", Evidence.FactsJson(facts),
                "country", H.Country(ctx, countryId),
                "country_rules", H.CountryRules(ctx, new[] { countryId }, null, "Company Verification", "KYC Norms", "Sanctions Risk"),
                "previous_questions", Evidence.QuestionsJson(Evidence.Questions(ctx, "gc_recipient", acc.Id)),
                "attributes", Evidence.DefsJson(ctx),
                "limits", J.Obj("max_asks", ctx.Dv.SettingInt("dd1.max_asks", 3)),
                "readable_tables", ReadTables);
        }

        public override void AfterFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            // Deterministic cap: the tier can never exceed what the recorded checks support.
            var acc = ctx.Subject;
            var checks = ctx.Dv.Query("gc_kyccheck", new[] { "gc_checktype", "gc_result" }, 50, "gc_account", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, acc.Id);
            Func<string, bool> passed = t => checks.Any(c => Dv.Label(c, "gc_checktype") == t && Dv.Label(c, "gc_result") == "Pass");
            var screens = ctx.Dv.Query("gc_screening", new[] { "gc_result" }, 20, "gc_account", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, acc.Id);
            var openMatch = screens.Any(s => Dv.Label(s, "gc_result") != "Clear");
            var hold = acc.GetAttributeValue<bool?>("gc_compliancehold") ?? false;

            var allowed = "Unverified";
            if (!string.IsNullOrEmpty(acc.GetAttributeValue<string>("name")) && !string.IsNullOrEmpty(acc.GetAttributeValue<string>("gc_registrationnumber"))) allowed = "Basic";
            var kyb = passed("Company Registry") && passed("UBO") && passed("Id Document") && screens.Count > 0 && !openMatch && !hold
                      && (!_buyer || passed("Proof Of Funds") || passed("Bank Reference"));
            if (kyb) allowed = "KYB Verified";
            var current = Dv.Label(acc, "gc_trusttier") ?? "Unverified";
            if (Array.IndexOf(Tiers, current) > Array.IndexOf(Tiers, allowed)) allowed = current;

            var rec = J.Str(result, "recommended_tier");
            if (Array.IndexOf(Tiers, rec) > Array.IndexOf(Tiers, allowed))
            {
                result["recommended_tier"] = allowed;
                result["guard_note"] = "Capped from " + rec + " to " + allowed + ": recorded checks/screening do not support a higher tier.";
                result["needs_human"] = true;
            }
            result["current_tier"] = current;
        }
    }

    public sealed class BuyerVerificationAgent : PartyVerificationAgent
    {
        public BuyerVerificationAgent() : base(true) { }
    }

    /// <summary>Pre-contract compliance gate for a deal: parties, screening, countries, commodity controls.</summary>
    public sealed class ComplianceAgent : AgentDefinition
    {
        public override string Name { get { return "Compliance"; } }
        public override string DisplayName { get { return "Compliance"; } }
        public override string Description { get { return "Pre-contract compliance gate: party tiers, screening, FATF/CAHRA countries, controlled commodities and permits; returns Clear / Refer / Block with evidence."; } }
        public override int AgentChoice { get { return Choice.Base + 3; } }
        public override string SubjectTable { get { return "gc_deal"; } }
        public override string[] Tools { get { return new[] { "create_review_task", "get_record", "query_records", "get_facts" }; } }
        public override string[] ReadTables { get { return new[] { "gc_deal", "account", "contact", "gc_screening", "gc_kyccheck", "gc_country", "gc_countryrule", "gc_commodity", "gc_document", "gc_dealparty", "gc_listing" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the Compliance agent. Decide whether this deal may proceed to contracting.
Rules (apply all; the strictest wins):
- Block: a Confirmed screening match on any party; a country with FATF 'Call For Action'.
- Refer: any Potential screening match or missing screening; a party below 'KYB Verified'; a compliance hold; origin flagged CAHRA (enhanced due diligence); a controlled commodity family (Rare Earths, Battery Metals, Precious Metals) without an export permit / licence document in CONTEXT; country rules that require a permit not evidenced; deal value above the payment release threshold without proof of funds.
- Clear: none of the above.
For each reason cite the evidence (which party/country/rule). List required permits and actions.
If not Clear, create_review_task (purpose Screening Clearance, kind Review, role Compliance Officer) with the reasons.
Country rules marked is_sample are illustrative – mention them but say they need confirmation.
Call finish.";
            }
        }

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Compliance decision",
                    "decision", S.Enum("Gate decision.", "Clear", "Refer", "Block"),
                    "reasons", S.Arr("Why.", S.Obj(null, "reason", S.Str("Finding."), "evidence", S.Str("Where it comes from."))),
                    "required_permits", S.Arr("Permits/licences required.", S.Str("permit")),
                    "required_actions", S.Arr("Actions before contracting.", S.Str("action")));
            }
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var deal = H.Require(ctx);
            var seller = H.Ref(deal, "gc_seller");
            var buyer = H.Ref(deal, "gc_buyer");
            var origin = H.Ref(deal, "gc_origincountry");
            var dest = H.Ref(deal, "gc_destinationcountry");
            var commodity = H.Ref(deal, "gc_commodity");
            var price = deal.GetAttributeValue<decimal?>("gc_price");
            var qty = deal.GetAttributeValue<decimal?>("gc_quantity");
            var seq = new[] { "seller", "buyer" };
            var parties = new Dictionary<string, object>();
            foreach (var p in seq)
            {
                var id = p == "seller" ? seller : buyer;
                var info = H.Party(ctx, id, true);
                if (info == null) continue;
                info["screenings"] = H.Screenings(ctx, id);
                info["kyc_checks"] = H.KycChecks(ctx, id);
                parties[p] = info;
            }
            ctx.Scratch["deal_parties"] = parties;
            return J.Obj(
                "deal", J.Obj("id", deal.Id.ToString(), "name", deal.GetAttributeValue<string>("gc_name"), "stage", Dv.Label(deal, "gc_stage"),
                              "overlay", Dv.Label(deal, "gc_statusoverlay"), "types", Dv.Label(deal, "gc_dealtypes"), "hs_code", deal.GetAttributeValue<string>("gc_hscode"),
                              "incoterm", Dv.Label(deal, "gc_incoterm"), "price", price, "quantity", qty, "unit", Dv.Label(deal, "gc_quantityunit"),
                              "currency", deal.GetAttributeValue<string>("gc_currency"), "value", price != null && qty != null ? price * qty : null),
                "parties", parties,
                "commodity", H.Commodity(ctx, commodity),
                "origin_country", H.Country(ctx, origin),
                "destination_country", H.Country(ctx, dest),
                "country_rules", H.CountryRules(ctx, new[] { origin, dest }, commodity, "Export Rules", "Import Rules", "Sanctions Risk"),
                "deal_documents", H.Documents(ctx, "gc_deal", deal.Id),
                "payment_release_threshold_usd", ctx.Dv.SettingNum("payment.release_threshold_usd", 250000),
                "readable_tables", ReadTables);
        }

        public override void AfterFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            // Deterministic floor: the model may be stricter than these rules, never looser.
            var parties = (Dictionary<string, object>)ctx.Scratch["deal_parties"];
            var floor = "Clear";
            var notes = new List<string>();
            foreach (var kv in parties)
            {
                var p = (Dictionary<string, object>)kv.Value;
                var screens = J.Arr(p, "screenings").OfType<Dictionary<string, object>>().Select(s => J.Str(s, "result")).ToList();
                if (screens.Contains("Confirmed Match")) { floor = "Block"; notes.Add(kv.Key + " has a confirmed screening match"); }
                else if (screens.Contains("Potential Match") || screens.Count == 0) { floor = Max(floor, "Refer"); notes.Add(kv.Key + (screens.Count == 0 ? " not screened" : " has a potential screening match")); }
                if (J.Bool(p, "compliance_hold")) { floor = Max(floor, "Refer"); notes.Add(kv.Key + " on compliance hold"); }
                var tier = J.Str(p, "trust_tier") ?? "Unverified";
                if (tier == "Unverified" || tier == "Basic") { floor = Max(floor, "Refer"); notes.Add(kv.Key + " tier is " + tier); }
            }
            if (parties.Count < 2) { floor = Max(floor, "Refer"); notes.Add("seller or buyer missing on deal"); }

            var decision = J.Str(result, "decision");
            if (Rank(decision) < Rank(floor))
            {
                result["decision"] = floor;
                result["guard_note"] = "Raised from " + decision + " to " + floor + ": " + string.Join("; ", notes) + ".";
            }
            if (J.Str(result, "decision") != "Clear")
            {
                result["needs_human"] = true;
                ToolCatalog.CreateReviewTask(ctx, "Screening Clearance", "Review", "Compliance " + J.Str(result, "decision") + ": " + ctx.Subject.GetAttributeValue<string>("gc_name"),
                    J.Obj("decision", J.Str(result, "decision"), "reasons", J.Get(result, "reasons"), "guard", J.Get(result, "guard_note")), "Compliance Officer", true);
            }
        }

        private static int Rank(string d) { return d == "Block" ? 2 : d == "Refer" ? 1 : 0; }
        private static string Max(string a, string b) { return Rank(a) >= Rank(b) ? a : b; }
    }
}

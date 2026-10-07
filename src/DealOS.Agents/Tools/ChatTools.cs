using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Tools
{
    /// <summary>
    /// Tools for the front-door chat agents (Buyer Concierge, Seller Assistant). The person chatting is an external
    /// user, so every tool is scoped in code to the conversation's account (ctx.Scratch["account_id"]): another
    /// party's records are only reachable as published, masked catalog data. Nothing here binds anyone: RFQs and
    /// listings are created as Draft for the user to send from the portal.
    /// </summary>
    public static class ChatTools
    {
        private const int Published = Choice.Base + 4;
        private const int Signed = Choice.Base + 5;
        private const int Cancelled = Choice.Base + 12;

        public static IEnumerable<Tool> Build()
        {
            yield return new Tool
            {
                Name = "search_catalog",
                Description = "Search published listings (masked: no seller identity, asset or exact location). Returns up to 10, newest first, with market-visible facts and their evidence status.",
                Parameters = S.Obj(null,
                    "commodity?", S.Str("Words from the commodity name, e.g. 'copper cathode', 'neodymium oxide'."),
                    "family?", S.Enum("Commodity family.", "Base Metals", "Precious Metals", "Battery Metals", "Rare Earths", "Minor Metals", "Ferrous And Alloys", "Industrial Minerals", "Scrap And Recycled"),
                    "min_quantity?", S.Num("Minimum listed quantity (in the listing's unit)."),
                    "incoterm?", S.Enum("Incoterm.", Incoterms),
                    "origin_country?", S.Str("Origin country name."),
                    "min_badge?", S.Enum("Minimum evidence badge.", "Any", "Documented", "Verified")),
                Run = SearchCatalog
            };

            yield return new Tool
            {
                Name = "get_listing",
                Description = "Details of one published listing: masked summary, available lot quantity and every market-visible fact with its evidence status.",
                Parameters = S.Obj(null, "listing_id", S.Str("Listing GUID from search_catalog.")),
                Run = GetListing
            };

            yield return new Tool
            {
                Name = "my_buying",
                Description = "The buyer's own RFQs, matches, deals and offers (with the buyer's landed-cost quote). Seller identities are masked until the contract is signed.",
                Parameters = S.Obj(null),
                Run = MyBuying
            };

            yield return new Tool
            {
                Name = "draft_rfq",
                Description = "Create a DRAFT request for quotation for the buyer. It is not sent: the buyer reviews and opens it from 'My RFQs' in the portal.",
                Writes = true,
                Parameters = S.Obj(null,
                    "commodity", S.Str("Commodity name as the buyer said it; must match one catalog commodity."),
                    "quantity", S.Num("Quantity wanted."),
                    "unit", S.Enum("Quantity unit.", Units),
                    "specification?", S.Str("Specification in the buyer's words (grade, purity, impurities, packaging)."),
                    "incoterm?", S.Enum("Incoterm wanted.", Incoterms),
                    "destination_country?", S.Str("Destination country name."),
                    "destination_port?", S.Str("Destination port or place."),
                    "target_price?", S.Num("Target price per unit, only if the buyer gave one."),
                    "currency?", S.Str("ISO currency of the target price, e.g. USD."),
                    "inspection_required?", S.Bool("True if the buyer wants independent inspection.")),
                Run = DraftRfq
            };

            yield return new Tool
            {
                Name = "my_selling",
                Description = "The seller's own listings (status, badge, evidence counts, open questions), RFQ invites, matches waiting for the seller, deals and offers (with the seller's net-payout quote). Buyer identities are masked until the contract is signed.",
                Parameters = S.Obj(null),
                Run = MySelling
            };

            yield return new Tool
            {
                Name = "my_listing_detail",
                Description = "Full evidence view of one of the seller's own listings: facts with status, open questions from the verification team, and documents.",
                Parameters = S.Obj(null, "listing_id", S.Str("Listing GUID from my_selling.")),
                Run = MyListingDetail
            };

            yield return new Tool
            {
                Name = "draft_listing",
                Description = "Create a DRAFT listing for the seller. It is not submitted: the seller uploads documents and submits it for verification from 'My listings' in the portal.",
                Writes = true,
                Parameters = S.Obj(null,
                    "commodity", S.Str("Commodity name as the seller said it; must match one catalog commodity."),
                    "quantity", S.Num("Quantity offered."),
                    "unit", S.Enum("Quantity unit.", Units),
                    "grade?", S.Str("Grade as the seller stated it."),
                    "ask_price?", S.Num("Asking price per unit, only if the seller gave one."),
                    "currency?", S.Str("ISO currency of the price."),
                    "incoterm?", S.Enum("Incoterm offered.", Incoterms),
                    "named_place?", S.Str("Named place or port for the Incoterm."),
                    "origin_country?", S.Str("Origin country name.")),
                Run = DraftListing
            };

            yield return new Tool
            {
                Name = "record_answer",
                Description = "Record the seller's answer to one open verification question, using the seller's own words from LATEST MESSAGE. It is stored as a seller statement (Claimed) and the evidence is re-checked; documents are still needed to reach Documented or Verified.",
                Writes = true,
                Parameters = S.Obj(null,
                    "question_id", S.Str("Open question GUID from my_listing_detail or my_selling."),
                    "answer_quote", S.Str("The exact words from LATEST MESSAGE that answer the question (copied, not paraphrased).")),
                Run = RecordAnswer
            };
        }

        public static readonly string[] Incoterms = { "EXW", "FCA", "FAS", "FOB", "CFR", "CIF", "CPT", "CIP", "DAP", "DPU", "DDP" };
        public static readonly string[] Units = { "MT", "DMT", "WMT", "Kg", "Lb", "Troy Oz", "Short Ton", "Long Ton", "Flask", "Unit" };
        private static readonly string[] Families = { "Base Metals", "Precious Metals", "Battery Metals", "Rare Earths", "Minor Metals", "Ferrous And Alloys", "Industrial Minerals", "Scrap And Recycled", "Other" };

        private static readonly string[] ListingColumns =
        {
            "gc_name", "gc_commodity", "gc_grade", "gc_quantity", "gc_quantityunit", "gc_askprice", "gc_currency", "gc_pricebasis",
            "gc_incoterm", "gc_namedplace", "gc_origincountry", "gc_badge", "gc_publishedon", "gc_status", "gc_seller", "gc_asset"
        };

        // ---------- buyer ----------

        private static object SearchCatalog(AgentContext ctx, Dictionary<string, object> a)
        {
            var q = new QueryExpression("gc_listing") { ColumnSet = new ColumnSet(ListingColumns), TopCount = 10 };
            q.Criteria.AddCondition("gc_status", ConditionOperator.Equal, Published);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var commodityIds = Commodities(ctx, J.Str(a, "commodity"), J.Str(a, "family"));
            if (commodityIds != null)
            {
                if (commodityIds.Count == 0) return J.Obj("count", 0, "listings", new List<object>(), "note", "No commodity in the catalog matches that name or family.");
                q.Criteria.AddCondition("gc_commodity", ConditionOperator.In, commodityIds.Cast<object>().ToArray());
            }
            var minQty = J.Num(a, "min_quantity");
            if (minQty != null) q.Criteria.AddCondition("gc_quantity", ConditionOperator.GreaterEqual, (decimal)minQty.Value);
            var inco = Array.IndexOf(Incoterms, J.Str(a, "incoterm") ?? "");
            if (inco >= 0) q.Criteria.AddCondition("gc_incoterm", ConditionOperator.Equal, Choice.Base + inco);
            var badge = J.Str(a, "min_badge");
            if (badge == "Documented") q.Criteria.AddCondition("gc_badge", ConditionOperator.In, Choice.Base + 1, Choice.Base + 2);
            if (badge == "Verified") q.Criteria.AddCondition("gc_badge", ConditionOperator.Equal, Choice.Base + 2);
            var origin = J.Str(a, "origin_country");
            if (!string.IsNullOrWhiteSpace(origin))
            {
                var countries = Ids(ctx, "gc_country", "gc_name", origin);
                if (countries.Count == 0) return J.Obj("count", 0, "listings", new List<object>(), "note", "Unknown origin country '" + origin + "'.");
                q.Criteria.AddCondition("gc_origincountry", ConditionOperator.In, countries.Cast<object>().ToArray());
            }
            q.AddOrder("gc_publishedon", OrderType.Descending);
            var rows = ctx.Dv.Svc.RetrieveMultiple(q).Entities;
            var listings = rows.Select(l => (object)MaskedListing(ctx, l, false)).ToList();
            return J.Obj("count", listings.Count, "listings", listings);
        }

        private static object GetListing(AgentContext ctx, Dictionary<string, object> a)
        {
            var id = RequireId(a, "listing_id");
            var l = ctx.Dv.Retrieve("gc_listing", id, ListingColumns);
            if (l == null || Status(l) != Published) throw new ToolRefusal("No published listing with id " + id + ".");
            return MaskedListing(ctx, l, true);
        }

        /// <summary>Catalog view of another party's listing: no seller, asset or listing title (sellers sometimes put their name in it).</summary>
        private static Dictionary<string, object> MaskedListing(AgentContext ctx, Entity l, bool detail)
        {
            Forbid(ctx, H(l, "gc_seller"), H(l, "gc_asset"), l.GetAttributeValue<string>("gc_name"));
            var commodity = H(l, "gc_commodity");
            var facts = Evidence.Facts(ctx, Choice.SubjectType.Listing, l.Id)
                .Where(f => f.Def != null && f.Def.MarketVisible && f.Status <= Choice.FactStatus.Claimed).ToList();
            foreach (var f in facts)
            {
                if (f.Status == Choice.FactStatus.Verified) ctx.Scratch["has_verified"] = true;
                if (f.Status == Choice.FactStatus.Claimed) Claimed(ctx, f.DisplayValue);
            }
            var d = J.Obj(
                "listing_id", l.Id.ToString(),
                "title", string.Join(", ", new[] { commodity, l.GetAttributeValue<string>("gc_grade"), Qty(l) }.Where(s => !string.IsNullOrWhiteSpace(s))),
                "commodity", commodity, "grade", l.GetAttributeValue<string>("gc_grade"),
                "quantity", l.GetAttributeValue<decimal?>("gc_quantity"), "unit", Dv.Label(l, "gc_quantityunit"),
                "ask_price", l.GetAttributeValue<decimal?>("gc_askprice"), "currency", l.GetAttributeValue<string>("gc_currency"),
                "price_basis", Dv.Label(l, "gc_pricebasis"), "incoterm", Dv.Label(l, "gc_incoterm"), "named_place", l.GetAttributeValue<string>("gc_namedplace"),
                "origin_country", H(l, "gc_origincountry"), "badge", Dv.Label(l, "gc_badge") ?? "None",
                "published_on", Day(l.GetAttributeValue<DateTime?>("gc_publishedon")),
                "facts", facts.Select(f => (object)J.Obj("name", f.Def.Name, "value", f.DisplayValue, "status", f.StatusLabel)).ToList());
            if (detail)
            {
                var lots = ctx.Dv.Query("gc_lot", new[] { "gc_quantity", "gc_status" }, 50, "gc_listing", ConditionOperator.Equal, l.Id);
                d["available_quantity"] = lots.Where(x => Status(x) == Choice.Base).Sum(x => x.GetAttributeValue<decimal?>("gc_quantity") ?? 0);
                d["how_to_buy"] = "Ask for a quote from the listing page, or include it in an RFQ. Funds go to escrow with a licensed partner and are released in stages after independent inspection.";
            }
            return d;
        }

        private static object MyBuying(AgentContext ctx, Dictionary<string, object> a)
        {
            var account = Account(ctx);
            var rfqs = ctx.Dv.Query("gc_buyerrequirement", new[] { "gc_name", "gc_status", "gc_commodity", "gc_quantity", "gc_quantityunit", "gc_incoterm", "gc_validuntil" }, 15,
                "gc_buyer", ConditionOperator.Equal, account);
            var rfqIds = rfqs.Select(r => r.Id).ToList();
            var matches = rfqIds.Count == 0 ? new List<Entity>() : Query(ctx, "gc_match", new[] { "gc_requirement", "gc_listing", "gc_score", "gc_status", "gc_buyeroptin", "gc_selleroptin" }, 20,
                "gc_requirement", rfqIds);
            var deals = Deals(ctx, "gc_buyer", account, "Seller", Choice.Base + 1);
            return J.Obj(
                "rfqs", rfqs.Select(r => (object)J.Obj("rfq_id", r.Id.ToString(), "name", r.GetAttributeValue<string>("gc_name"), "status", Dv.Label(r, "gc_status"),
                    "commodity", H(r, "gc_commodity"), "quantity", r.GetAttributeValue<decimal?>("gc_quantity"), "unit", Dv.Label(r, "gc_quantityunit"),
                    "incoterm", Dv.Label(r, "gc_incoterm"), "valid_until", Day(r.GetAttributeValue<DateTime?>("gc_validuntil")))).ToList(),
                "matches", matches.Select(m => (object)J.Obj("rfq_id", Ref(m, "gc_requirement"), "listing_id", Ref(m, "gc_listing"),
                    "score", m.GetAttributeValue<decimal?>("gc_score"), "status", Dv.Label(m, "gc_status"),
                    "you_opted_in", m.GetAttributeValue<bool?>("gc_buyeroptin") ?? false, "seller_opted_in", m.GetAttributeValue<bool?>("gc_selleroptin") ?? false)).ToList(),
                "deals", deals);
        }

        private static object DraftRfq(AgentContext ctx, Dictionary<string, object> a)
        {
            var account = Account(ctx);
            Recent(ctx, "gc_buyerrequirement", "gc_buyer", account, Choice.Base, 5, "draft RFQs");
            var commodity = OneCommodity(ctx, J.Str(a, "commodity"));
            var qty = J.Num(a, "quantity");
            if (qty == null || qty <= 0) throw new ToolRefusal("quantity must be a positive number.");
            var unit = Array.IndexOf(Units, J.Str(a, "unit") ?? "");
            if (unit < 0) throw new ToolRefusal("unit must be one of: " + string.Join(", ", Units));

            var r = new Entity("gc_buyerrequirement");
            r["gc_name"] = Trunc("RFQ: " + Num(qty.Value) + " " + Units[unit] + " " + commodity.Name, 100);
            r["gc_buyer"] = new EntityReference("account", account);
            r["gc_commodity"] = commodity;
            r["gc_quantity"] = (decimal)qty.Value;
            r["gc_quantityunit"] = new OptionSetValue(Choice.Base + unit);
            r["gc_status"] = new OptionSetValue(Choice.Base); // Draft: the buyer opens it from the portal
            var spec = J.Str(a, "specification");
            if (!string.IsNullOrWhiteSpace(spec)) r["gc_specification"] = Trunc(spec, 4000);
            var inco = Array.IndexOf(Incoterms, J.Str(a, "incoterm") ?? "");
            if (inco >= 0) r["gc_incoterm"] = new OptionSetValue(Choice.Base + inco);
            var dest = J.Str(a, "destination_country");
            if (!string.IsNullOrWhiteSpace(dest))
            {
                var ids = Ids(ctx, "gc_country", "gc_name", dest);
                if (ids.Count == 1) r["gc_destinationcountry"] = new EntityReference("gc_country", ids[0]);
            }
            var port = J.Str(a, "destination_port");
            if (!string.IsNullOrWhiteSpace(port)) r["gc_destinationport"] = Trunc(port, 100);
            var target = J.Num(a, "target_price");
            if (target != null && target > 0)
            {
                if (!Said(ctx, Num(target.Value))) throw new ToolRefusal("target_price must be a number the buyer stated; ask them instead of assuming.");
                r["gc_targetprice"] = (decimal)target.Value;
                r["gc_currency"] = Trunc((J.Str(a, "currency") ?? "USD").ToUpperInvariant(), 3);
            }
            if (J.Get(a, "inspection_required") != null) r["gc_inspectionrequired"] = J.Bool(a, "inspection_required");
            var id = ctx.Create(r, "draft_rfq", J.Obj("commodity", commodity.Name, "quantity", qty, "unit", Units[unit]));
            return J.Obj("ok", true, "rfq_id", ctx.DryRun ? null : id.ToString(), "status", "Draft",
                         "note", "Saved as a draft. The buyer opens it from 'My RFQs' to send it; matching starts then.");
        }

        // ---------- seller ----------

        private static object MySelling(AgentContext ctx, Dictionary<string, object> a)
        {
            var account = Account(ctx);
            var listings = ctx.Dv.Query("gc_listing", ListingColumns, 15, "gc_seller", ConditionOperator.Equal, account);
            var listingIds = listings.Select(l => l.Id).ToList();
            var invites = ctx.Dv.Query("gc_rfqinvite", new[] { "gc_requirement", "gc_listing", "gc_status", "gc_invitedon", "gc_buyernote" }, 15,
                "gc_seller", ConditionOperator.Equal, account, "gc_status", ConditionOperator.Equal, Choice.Base);
            var matches = listingIds.Count == 0 ? new List<Entity>() : Query(ctx, "gc_match", new[] { "gc_listing", "gc_score", "gc_status", "gc_buyeroptin", "gc_selleroptin" }, 20,
                "gc_listing", listingIds);
            return J.Obj(
                "listings", listings.Select(l =>
                {
                    var facts = Evidence.Facts(ctx, Choice.SubjectType.Listing, l.Id);
                    var open = OpenQuestions(ctx, l.Id);
                    return (object)J.Obj("listing_id", l.Id.ToString(), "name", l.GetAttributeValue<string>("gc_name"), "status", Dv.Label(l, "gc_status"),
                        "badge", Dv.Label(l, "gc_badge") ?? "None", "quantity", l.GetAttributeValue<decimal?>("gc_quantity"), "unit", Dv.Label(l, "gc_quantityunit"),
                        "ask_price", l.GetAttributeValue<decimal?>("gc_askprice"), "currency", l.GetAttributeValue<string>("gc_currency"),
                        "facts_by_status", facts.GroupBy(f => f.StatusLabel).ToDictionary(g => g.Key, g => (object)g.Count()),
                        "open_questions", open.Count);
                }).ToList(),
                "rfq_invites", invites.Select(i => (object)J.Obj("invite_id", i.Id.ToString(), "listing_id", Ref(i, "gc_listing"), "rfq", H(i, "gc_requirement"),
                    "invited_on", Day(i.GetAttributeValue<DateTime?>("gc_invitedon")), "buyer_note", i.GetAttributeValue<string>("gc_buyernote"))).ToList(),
                "matches", matches.Select(m => (object)J.Obj("listing_id", Ref(m, "gc_listing"), "score", m.GetAttributeValue<decimal?>("gc_score"), "status", Dv.Label(m, "gc_status"),
                    "buyer_interested", m.GetAttributeValue<bool?>("gc_buyeroptin") ?? false, "you_opted_in", m.GetAttributeValue<bool?>("gc_selleroptin") ?? false)).ToList(),
                "deals", Deals(ctx, "gc_seller", account, "Buyer", Choice.Base));
        }

        private static object MyListingDetail(AgentContext ctx, Dictionary<string, object> a)
        {
            var l = OwnListing(ctx, RequireId(a, "listing_id"));
            var facts = Evidence.Facts(ctx, Choice.SubjectType.Listing, l.Id);
            var docs = ctx.Dv.Query("gc_document", new[] { "gc_filename", "gc_doctype", "gc_parsestatus" }, 30, "gc_listing", ConditionOperator.Equal, l.Id);
            return J.Obj(
                "listing_id", l.Id.ToString(), "name", l.GetAttributeValue<string>("gc_name"), "status", Dv.Label(l, "gc_status"), "badge", Dv.Label(l, "gc_badge") ?? "None",
                "facts", facts.Select(f => (object)J.Obj("attribute_key", f.Key, "name", f.Def == null ? f.Key : f.Def.Name, "value", f.DisplayValue, "status", f.StatusLabel)).ToList(),
                "open_questions", OpenQuestions(ctx, l.Id).Select(q => (object)J.Obj("question_id", q.Id.ToString(), "question", q.GetAttributeValue<string>("gc_name"),
                    "attribute_key", q.GetAttributeValue<string>("gc_attributekey"), "kind", Dv.Label(q, "gc_kind"), "asked_on", Day(q.GetAttributeValue<DateTime?>("gc_askedon")))).ToList(),
                "documents", docs.Select(d => (object)J.Obj("file", d.GetAttributeValue<string>("gc_filename"), "type", Dv.Label(d, "gc_doctype"), "parse_status", Dv.Label(d, "gc_parsestatus"))).ToList(),
                "evidence_ladder", "Verified = independently confirmed; Documented = supported by a document you uploaded; Claimed = only stated by you. Buyers see the status next to each value.");
        }

        private static object DraftListing(AgentContext ctx, Dictionary<string, object> a)
        {
            var account = Account(ctx);
            Recent(ctx, "gc_listing", "gc_seller", account, Choice.Base, 5, "draft listings");
            var commodity = OneCommodity(ctx, J.Str(a, "commodity"));
            var qty = J.Num(a, "quantity");
            if (qty == null || qty <= 0) throw new ToolRefusal("quantity must be a positive number.");
            var unit = Array.IndexOf(Units, J.Str(a, "unit") ?? "");
            if (unit < 0) throw new ToolRefusal("unit must be one of: " + string.Join(", ", Units));

            var l = new Entity("gc_listing");
            var grade = J.Str(a, "grade");
            l["gc_name"] = Trunc(commodity.Name + (string.IsNullOrWhiteSpace(grade) ? "" : " " + grade) + " " + Num(qty.Value) + " " + Units[unit], 100);
            l["gc_seller"] = new EntityReference("account", account);
            l["gc_commodity"] = commodity;
            l["gc_quantity"] = (decimal)qty.Value;
            l["gc_quantityunit"] = new OptionSetValue(Choice.Base + unit);
            l["gc_status"] = new OptionSetValue(Choice.Base); // Draft: the seller submits it from the portal
            if (!string.IsNullOrWhiteSpace(grade)) l["gc_grade"] = Trunc(grade, 100);
            var price = J.Num(a, "ask_price");
            if (price != null && price > 0)
            {
                if (!Said(ctx, Num(price.Value))) throw new ToolRefusal("ask_price must be a number the seller stated; ask them instead of assuming.");
                l["gc_askprice"] = (decimal)price.Value;
                l["gc_currency"] = Trunc((J.Str(a, "currency") ?? "USD").ToUpperInvariant(), 3);
            }
            var inco = Array.IndexOf(Incoterms, J.Str(a, "incoterm") ?? "");
            if (inco >= 0) l["gc_incoterm"] = new OptionSetValue(Choice.Base + inco);
            var place = J.Str(a, "named_place");
            if (!string.IsNullOrWhiteSpace(place)) l["gc_namedplace"] = Trunc(place, 100);
            var origin = J.Str(a, "origin_country");
            if (!string.IsNullOrWhiteSpace(origin))
            {
                var ids = Ids(ctx, "gc_country", "gc_name", origin);
                if (ids.Count == 1) l["gc_origincountry"] = new EntityReference("gc_country", ids[0]);
            }
            var id = ctx.Create(l, "draft_listing", J.Obj("commodity", commodity.Name, "quantity", qty, "unit", Units[unit]));
            return J.Obj("ok", true, "listing_id", ctx.DryRun ? null : id.ToString(), "status", "Draft",
                         "note", "Saved as a draft. Next: upload the COA / assay, licence and photos on the listing page, then submit it for verification.");
        }

        private static object RecordAnswer(AgentContext ctx, Dictionary<string, object> a)
        {
            var account = Account(ctx);
            var qid = RequireId(a, "question_id");
            var q = ctx.Dv.Retrieve("gc_question", qid, "gc_name", "gc_attributekey", "gc_listing", "gc_recipient", "gc_answeredby");
            if (q == null) throw new ToolRefusal("No question with id " + qid + ".");
            var listingId = Ref(q, "gc_listing");
            var ownsListing = listingId != null && OwnListingOrNull(ctx, Guid.Parse(listingId)) != null;
            var recipient = Ref(q, "gc_recipient");
            if (!ownsListing && recipient != account.ToString()) throw new ToolRefusal("That question is not addressed to this seller.");
            if (q.GetAttributeValue<EntityReference>("gc_answeredby") != null) throw new ToolRefusal("That question is already answered.");

            var quote = (J.Str(a, "answer_quote") ?? "").Trim();
            var message = ctx.Scratch.ContainsKey("message_text") ? (string)ctx.Scratch["message_text"] : "";
            if (quote.Length < 1 || Normal(message).IndexOf(Normal(quote), StringComparison.OrdinalIgnoreCase) < 0)
                throw new ToolRefusal("answer_quote must be copied exactly from LATEST MESSAGE.");

            var key = q.GetAttributeValue<string>("gc_attributekey") ?? "";
            AttributeDef def;
            Evidence.Defs(ctx).TryGetValue(key, out def);
            var messageId = ctx.Scratch.ContainsKey("message_id") ? (Guid?)ctx.Scratch["message_id"] : null;
            if (def == null)
            {
                // Free-form question (other.*): no evidence attribute to record against; link the message for the team.
                var link = new Entity("gc_question", qid);
                if (messageId != null) link["gc_message"] = new EntityReference("gc_message", messageId.Value);
                ctx.Update(link, "link_answer_message", J.Obj("question_id", qid.ToString()));
                return J.Obj("ok", true, "note", "Passed to the verification team (this question has no evidence field).");
            }

            var subjectType = ownsListing ? Choice.SubjectType.Listing : Choice.SubjectType.Party;
            var subjectId = ownsListing ? Guid.Parse(listingId) : account;
            var asr = new Entity("gc_assertion");
            asr["gc_name"] = key;
            asr["gc_subjecttype"] = new OptionSetValue(subjectType);
            asr["gc_subjectid"] = subjectId.ToString();
            if (ownsListing) asr["gc_listing"] = new EntityReference("gc_listing", subjectId);
            else asr["gc_account"] = new EntityReference("account", account);
            asr["gc_attributekey"] = key;
            asr["gc_valueraw"] = quote;
            var m = Regex.Match(quote, @"(-?\d+(?:[.,]\d+)*)\s*([A-Za-z%/°]+)?");
            asr["gc_valuenorm"] = def.DataType == "quantity" && m.Success
                ? Json.Serialize(J.Obj("num", double.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture), "unit", m.Groups[2].Value))
                : Json.Serialize(J.Obj("text", quote));
            asr["gc_provenanceclass"] = new OptionSetValue(Choice.Base); // Counterparty Statement → Claimed at most
            asr["gc_sourcekind"] = new OptionSetValue(Choice.Base + 1);   // Message
            if (messageId != null) asr["gc_message"] = new EntityReference("gc_message", messageId.Value);
            asr["gc_quote"] = quote;
            asr["gc_extractionmethod"] = new OptionSetValue(Choice.Base + 3); // Human: typed by the seller
            asr["gc_extractorversion"] = "chat-" + ctx.Agent.Version;
            asr["gc_extractionconfidence"] = 1m;
            asr["gc_assertedby"] = new EntityReference("account", account);
            asr["gc_agentrun"] = new EntityReference("gc_agentrun", ctx.RunId);
            var assertionId = ctx.Create(asr, "record_answer", J.Obj("question_id", qid.ToString(), "attribute_key", key, "quote", quote));

            var upd = new Entity("gc_question", qid);
            if (!ctx.DryRun) upd["gc_answeredby"] = new EntityReference("gc_assertion", assertionId);
            if (messageId != null) upd["gc_message"] = new EntityReference("gc_message", messageId.Value);
            ctx.Update(upd, "mark_question_answered", J.Obj("question_id", qid.ToString()));

            object evidence = null;
            if (!ctx.DryRun) evidence = Evidence.Resolve(ctx, subjectType, subjectId);
            return J.Obj("ok", true, "recorded_as", "Claimed (seller statement)", "evidence", evidence,
                         "note", "To raise it to Documented, the seller uploads a supporting document on the listing page.");
        }

        // ---------- shared helpers ----------

        private static List<object> Deals(AgentContext ctx, string side, Guid account, string otherLabel, int viewer)
        {
            var other = side == "gc_buyer" ? "gc_seller" : "gc_buyer";
            var deals = ctx.Dv.Query("gc_deal", new[] { "gc_name", "gc_dealnumber", "gc_stage", "gc_statusoverlay", "gc_commodity", "gc_quantity", "gc_quantityunit",
                "gc_price", "gc_currency", "gc_incoterm", other }, 15, side, ConditionOperator.Equal, account);
            var result = new List<object>();
            foreach (var d in deals)
            {
                var stage = Status(d, "gc_stage");
                var revealed = stage >= Signed && stage != Cancelled;
                var otherName = H(d, other);
                if (!revealed) Forbid(ctx, otherName);
                var offers = ctx.Dv.Query("gc_offer", new[] { "gc_price", "gc_quantity", "gc_currency", "gc_incoterm", "gc_paymentterms", "gc_status", "gc_round", "gc_validuntil", "gc_fromparty" }, 10,
                    "gc_deal", ConditionOperator.Equal, d.Id);
                result.Add(J.Obj(
                    "deal_id", d.Id.ToString(), "deal_number", d.GetAttributeValue<string>("gc_dealnumber"), "stage", Dv.Label(d, "gc_stage"),
                    "hold", Dv.Label(d, "gc_statusoverlay"), "commodity", H(d, "gc_commodity"),
                    "quantity", d.GetAttributeValue<decimal?>("gc_quantity"), "unit", Dv.Label(d, "gc_quantityunit"),
                    "price", d.GetAttributeValue<decimal?>("gc_price"), "currency", d.GetAttributeValue<string>("gc_currency"), "incoterm", Dv.Label(d, "gc_incoterm"),
                    "counterparty", revealed ? otherName : otherLabel + " (identity shared after the contract is signed)",
                    "offers", offers.Select(o =>
                    {
                        var quote = ctx.Dv.Query("gc_pricequote", new[] { "gc_landedcost", "gc_netpayout", "gc_currency" }, 1,
                            "gc_offer", ConditionOperator.Equal, o.Id, "gc_viewer", ConditionOperator.Equal, viewer).FirstOrDefault();
                        var fromMe = Ref(o, "gc_fromparty") == account.ToString();
                        return (object)J.Obj("offer_id", o.Id.ToString(), "from", fromMe ? "you" : otherLabel.ToLowerInvariant(), "status", Dv.Label(o, "gc_status"),
                            "round", o.GetAttributeValue<int?>("gc_round"), "price", o.GetAttributeValue<decimal?>("gc_price"), "quantity", o.GetAttributeValue<decimal?>("gc_quantity"),
                            "currency", o.GetAttributeValue<string>("gc_currency"), "incoterm", Dv.Label(o, "gc_incoterm"), "payment_terms", Dv.Label(o, "gc_paymentterms"),
                            "valid_until", Day(o.GetAttributeValue<DateTime?>("gc_validuntil")),
                            viewer == Choice.Base + 1 ? "your_landed_cost" : "your_net_payout",
                            quote == null ? null : (object)(viewer == Choice.Base + 1 ? quote.GetAttributeValue<decimal?>("gc_landedcost") : quote.GetAttributeValue<decimal?>("gc_netpayout")));
                    }).ToList()));
            }
            return result;
        }

        private static List<Entity> OpenQuestions(AgentContext ctx, Guid listingId)
        {
            return Evidence.Questions(ctx, "gc_listing", listingId, 30).Where(q => q.GetAttributeValue<EntityReference>("gc_answeredby") == null).ToList();
        }

        private static Entity OwnListing(AgentContext ctx, Guid id)
        {
            var l = OwnListingOrNull(ctx, id);
            if (l == null) throw new ToolRefusal("No listing with id " + id + " belongs to this seller.");
            return l;
        }

        private static Entity OwnListingOrNull(AgentContext ctx, Guid id)
        {
            var l = ctx.Dv.Retrieve("gc_listing", id, ListingColumns);
            return l != null && Ref(l, "gc_seller") == Account(ctx).ToString() ? l : null;
        }

        public static Guid Account(AgentContext ctx)
        {
            object v;
            if (ctx.Scratch.TryGetValue("account_id", out v) && v is Guid) return (Guid)v;
            throw new ToolRefusal("This conversation is not linked to a company account yet.");
        }

        /// <summary>Commodity ids matching a name fragment and/or family; null when neither was given.</summary>
        private static List<Guid> Commodities(AgentContext ctx, string name, string family)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(family)) return null;
            var q = new QueryExpression("gc_commodity") { ColumnSet = new ColumnSet("gc_name"), TopCount = 50 };
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var fam = Array.IndexOf(Families, family ?? "");
            if (fam >= 0) q.Criteria.AddCondition("gc_family", ConditionOperator.Equal, Choice.Base + fam);
            var rows = ctx.Dv.Svc.RetrieveMultiple(q).Entities;
            if (string.IsNullOrWhiteSpace(name)) return rows.Select(r => r.Id).ToList();
            var words = Words(name);
            return rows.Where(r => words.All(w => (r.GetAttributeValue<string>("gc_name") ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                       .Select(r => r.Id).ToList();
        }

        private static EntityReference OneCommodity(AgentContext ctx, string name)
        {
            var q = new QueryExpression("gc_commodity") { ColumnSet = new ColumnSet("gc_name"), TopCount = 200 };
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var all = ctx.Dv.Svc.RetrieveMultiple(q).Entities;
            var words = Words(name ?? "");
            var hits = all.Where(r => words.Count > 0 && words.All(w => (r.GetAttributeValue<string>("gc_name") ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (hits.Count == 1) return new EntityReference("gc_commodity", hits[0].Id) { Name = hits[0].GetAttributeValue<string>("gc_name") };
            var options = (hits.Count > 1 ? hits : all.ToList()).Select(r => r.GetAttributeValue<string>("gc_name")).Take(15);
            throw new ToolRefusal((hits.Count == 0 ? "No catalog commodity matches '" + name + "'." : "'" + name + "' matches several commodities.") +
                                  " Ask the user which one: " + string.Join("; ", options));
        }

        private static List<Guid> Ids(AgentContext ctx, string table, string nameColumn, string name)
        {
            var q = new QueryExpression(table) { ColumnSet = new ColumnSet(nameColumn), TopCount = 5 };
            q.Criteria.AddCondition(nameColumn, ConditionOperator.Like, "%" + name.Trim().Replace("%", "").Replace("[", "[[]") + "%");
            return ctx.Dv.Svc.RetrieveMultiple(q).Entities.Select(e => e.Id).ToList();
        }

        private static List<Entity> Query(AgentContext ctx, string table, string[] columns, int top, string column, List<Guid> ids)
        {
            var q = new QueryExpression(table) { ColumnSet = ctx.Dv.SafeColumns(table, columns), TopCount = top };
            q.Criteria.AddCondition(column, ConditionOperator.In, ids.Cast<object>().ToArray());
            q.AddOrder("createdon", OrderType.Descending);
            return ctx.Dv.Svc.RetrieveMultiple(q).Entities.ToList();
        }

        /// <summary>Refuses when the account already created this many drafts in the last 24 hours (stops a chat loop filling the tables).</summary>
        private static void Recent(AgentContext ctx, string table, string owner, Guid account, int draftStatus, int max, string what)
        {
            var count = ctx.Dv.Query(table, new[] { "createdon" }, max, owner, ConditionOperator.Equal, account,
                "gc_status", ConditionOperator.Equal, draftStatus, "createdon", ConditionOperator.GreaterThan, DateTime.UtcNow.AddHours(-24)).Count;
            if (count >= max) throw new ToolRefusal("There are already " + count + " " + what + " from the last 24 hours. Ask the user to finish or delete those first.");
        }

        /// <summary>True when the number appears in the user's own messages (prices must come from the user, not the model).</summary>
        private static bool Said(AgentContext ctx, string number)
        {
            object v;
            var said = ctx.Scratch.TryGetValue("user_numbers", out v) ? (HashSet<string>)v : new HashSet<string>();
            return MessageValidator.Normalise(number).Any(said.Contains);
        }

        private static void Forbid(AgentContext ctx, params string[] terms)
        {
            object v;
            if (!ctx.Scratch.TryGetValue("counterparty_terms", out v)) ctx.Scratch["counterparty_terms"] = v = new List<string>();
            ((List<string>)v).AddRange(terms.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        private static void Claimed(AgentContext ctx, string value)
        {
            object v;
            if (!ctx.Scratch.TryGetValue("claimed_numbers", out v)) ctx.Scratch["claimed_numbers"] = v = new HashSet<string>();
            MessageValidator.CollectNumbers(value, (HashSet<string>)v);
        }

        private static List<string> Words(string s)
        {
            return Regex.Split(s ?? "", @"[^\p{L}\p{N}]+").Where(w => w.Length >= 2).ToList();
        }

        private static string Normal(string s) { return Regex.Replace(s ?? "", @"\s+", " ").Trim(); }

        private static Guid RequireId(Dictionary<string, object> a, string key)
        {
            var id = J.Id(a, key);
            if (id == null) throw new ToolRefusal(key + " must be a GUID.");
            return id.Value;
        }

        private static int Status(Entity e, string column = "gc_status")
        {
            var o = e.GetAttributeValue<OptionSetValue>(column);
            return o == null ? -1 : o.Value;
        }

        private static string H(Entity e, string column)
        {
            var r = e.GetAttributeValue<EntityReference>(column);
            return r == null ? null : r.Name;
        }

        private static string Ref(Entity e, string column)
        {
            var r = e.GetAttributeValue<EntityReference>(column);
            return r == null ? null : r.Id.ToString();
        }

        private static string Qty(Entity l)
        {
            var q = l.GetAttributeValue<decimal?>("gc_quantity");
            return q == null ? null : Num((double)q.Value) + " " + Dv.Label(l, "gc_quantityunit");
        }

        private static string Num(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        private static string Day(DateTime? d) { return d == null ? null : d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        private static string Trunc(string s, int n) { return GeminiClient.Truncate(s, n); }
    }
}

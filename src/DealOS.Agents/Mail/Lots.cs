using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>
    /// Seller lots: a seller's stock (price, quantity) offered to several buyers at once. Two kinds:
    ///   timed (24 h, 48 h, ... from the seller's email or trade.bid_window_hours): buyers bid until the deadline, then the highest
    ///     buyer prices win while quantity lasts (ties: earliest bid). If no bid reaches the seller's price, the best bid goes to the
    ///     seller and the lot carries on open-ended.
    ///   open-ended (no bid deadline): every bid goes to the seller as it comes (buyer price less our margin); the seller closes the
    ///     lot by accepting a bid price, counters with a new price (it goes to the buyers) or withdraws.
    /// Each winner goes through a person's Confirm deal. Back to back as everywhere on the desk: buyers see only our price, the seller only us.
    /// </summary>
    public static class Lots
    {
        private const int B = Choice.Base;
        public static class Status { public const int Open = B, Closed = B + 1, Allocated = B + 2, Withdrawn = B + 3; }
        public static readonly string[] Statuses = { "Open", "Closed", "Allocated", "Withdrawn" };

        private static readonly string[] LotColumns = { "gc_name", "gc_seller", "gc_sellerthread", "gc_commoditytext", "gc_specification", "gc_quantity", "gc_unit",
                                                        "gc_price", "gc_currency", "gc_incoterm", "gc_namedplace", "gc_origin", "gc_terms", "gc_validuntil",
                                                        "gc_biddeadline", "gc_status", "gc_allocated", "gc_remindedon", "gc_window", "gc_discoveredon" };

        public static Entity Get(Dv dv, Guid lotId) { return dv.Retrieve("gc_sellerlot", lotId, LotColumns); }

        /// <summary>The lot behind a deal, or null.</summary>
        public static Entity OfDeal(Dv dv, Guid dealId)
        {
            var deal = dv.Retrieve("gc_deal", dealId, "gc_sellerlot");
            var lotId = deal == null ? null : Desk.H(deal, "gc_sellerlot");
            return lotId == null ? null : Get(dv, lotId.Value);
        }

        public static int StatusOf(Entity lot) { var v = lot.GetAttributeValue<OptionSetValue>("gc_status"); return v == null ? Status.Open : v.Value; }

        public static DateTime? Deadline(Entity lot) { return lot.GetAttributeValue<DateTime?>("gc_biddeadline"); }

        /// <summary>An open lot without a bid deadline: bids go to the seller as they come and the seller decides when to close.</summary>
        public static bool OpenEnded(Entity lot) { return StatusOf(lot) == Status.Open && Deadline(lot) == null; }

        /// <summary>How long buyers can bid: hours, or null for open-ended; Stated when the seller's email gave it.</summary>
        public sealed class Window
        {
            public double? Hours;
            public bool Stated;
            public string Label { get { return (Hours == null ? "Open-ended" : Desk.Num((decimal)Hours.Value) + " hours") + (Stated ? " (seller)" : " (default)"); } }
        }

        /// <summary>
        /// The window from the seller's email (open_ended, or window_hours), else the default setting trade.bid_window_hours
        /// (a number of hours; 0 or "open" = open-ended). Pure, for tests.
        /// </summary>
        public static Window WindowOf(bool openEnded, double? hours, string setting)
        {
            if (openEnded) return new Window { Stated = true };
            if (hours != null && hours > 0) return new Window { Hours = Math.Min(720, Math.Max(1, hours.Value)), Stated = true };
            var s = (setting ?? "24").Trim().ToLowerInvariant();
            double h;
            if (s == "open" || s == "open-ended" || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out h)) h = s.StartsWith("open") ? 0 : 24;
            return new Window { Hours = h <= 0 ? (double?)null : Math.Min(720, Math.Max(1, h)) };
        }

        /// <summary>What a party or the owner is told about when offers close.</summary>
        public static string Closes(Entity lot)
        {
            var d = Deadline(lot);
            return StatusOf(lot) != Status.Open ? "closed" : d == null ? "open-ended (the supplier decides when to close)" : When(d.Value);
        }

        public static string When(DateTime utc) { return utc.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC"; }

        // ---------------------------------------------------------------- create and market

        /// <summary>
        /// Saves the seller's offer in a seller thread as a lot (or updates the thread's open lot) and, for a new lot, offers it to
        /// matching buyers. Returns the lot, its deadline and the buyers it was offered to.
        /// </summary>
        public static Dictionary<string, object> Save(DeskWriter w, Entity conv, Guid sellerId, Dictionary<string, object> a)
        {
            var dv = w.Dv;
            var existingId = Desk.H(conv, "gc_sellerlot");
            var existing = existingId == null ? null : Get(dv, existingId.Value);
            var isNew = existing == null || StatusOf(existing) != Status.Open;
            var lot = isNew ? new Entity("gc_sellerlot") : new Entity("gc_sellerlot", existing.Id);
            var commodity = J.Str(a, "commodity") ?? (existing == null ? null : existing.GetAttributeValue<string>("gc_commoditytext"));
            var qty = J.Num(a, "quantity");
            var unit = J.Str(a, "unit") ?? "MT";
            lot["gc_name"] = Desk.Cut("Lot: " + commodity + (qty == null ? "" : " " + Desk.Num((decimal)qty.Value) + " " + unit), 100);
            lot["gc_commoditytext"] = Desk.Cut(commodity, 400);
            if (J.Str(a, "specification") != null) lot["gc_specification"] = J.Str(a, "specification");
            if (qty != null) lot["gc_quantity"] = (decimal)qty.Value;
            lot["gc_unit"] = Desk.Cut(unit, 30);
            lot["gc_price"] = (decimal)J.Num(a, "price").Value;
            lot["gc_currency"] = (J.Str(a, "currency") ?? "USD").ToUpperInvariant();
            if (J.Str(a, "incoterm") != null && Choice.ValueOf(DeskChoice.Incoterms, J.Str(a, "incoterm")) >= 0) lot["gc_incoterm"] = J.Str(a, "incoterm");
            if (J.Str(a, "named_place") != null) lot["gc_namedplace"] = Desk.Cut(J.Str(a, "named_place"), 200);
            if (J.Str(a, "origin") != null) lot["gc_origin"] = Desk.Cut(J.Str(a, "origin"), 100);
            if (J.Str(a, "terms_text") != null) lot["gc_terms"] = Desk.Cut(J.Str(a, "terms_text"), 4000);
            DateTime valid;
            if (DateTime.TryParse(J.Str(a, "valid_until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out valid))
                lot["gc_validuntil"] = valid;
            Guid lotId;
            DateTime? deadline;
            var window = WindowOf(J.Bool(a, "open_ended"), J.Num(a, "window_hours"), dv.Setting("trade.bid_window_hours"));
            if (isNew || window.Stated)
            {
                // Timed: the window, but never past the seller's own validity. Open-ended: no bid deadline (the validity still binds the offer).
                deadline = window.Hours == null ? (DateTime?)null : DateTime.UtcNow.AddHours(window.Hours.Value);
                if (deadline != null && lot.Contains("gc_validuntil") && valid < deadline.Value) deadline = valid;
                lot["gc_biddeadline"] = deadline;
                lot["gc_window"] = window.Label;
                if (!isNew) lot["gc_remindedon"] = null;
            }
            else deadline = Deadline(existing);
            var repriced = 0;
            if (isNew)
            {
                lot["gc_status"] = new OptionSetValue(Status.Open);
                lot["gc_allocated"] = 0m;
                lot["gc_seller"] = new EntityReference("account", sellerId);
                lot["gc_sellerthread"] = new EntityReference("gc_conversation", conv.Id);
                lotId = w.Create(lot, "create_seller_lot", J.Obj("commodity", commodity, "quantity", qty, "price", J.Num(a, "price")));
                var c = new Entity("gc_conversation", conv.Id);
                c["gc_side"] = new OptionSetValue(DeskChoice.Side.Seller);
                c["gc_counterparty"] = new EntityReference("account", sellerId);
                if (!w.DryRun) c["gc_sellerlot"] = new EntityReference("gc_sellerlot", lotId);
                w.Update(c, "link_lot_thread");
            }
            else
            {
                lotId = existing.Id;
                w.Update(lot, "update_seller_lot", J.Obj("price", J.Num(a, "price"), "quantity", qty, "window", window.Stated ? window.Label : null));
                var price = (decimal)J.Num(a, "price").Value;
                RefreshOffers(w, lotId, price);
                if (price != existing.GetAttributeValue<decimal>("gc_price") && !w.DryRun) repriced = Reprice(w, Get(dv, lotId));
            }
            var offered = isNew && !w.DryRun ? Market(w, lotId) : new List<object>();
            var closes = deadline == null ? "open-ended" : When(deadline.Value);
            string note;
            if (isNew)
                note = deadline == null
                    ? "The lot is offered (masked) to matching buyers, open-ended: each buyer's offer is put to the seller as it comes, and the seller decides when to close. Tell the seller we are presenting it to our buyers and will come back with their offers."
                    : "The lot is offered (masked) to matching buyers; offers close at offers_close. Tell the seller we are presenting it to our buyers and will revert by then.";
            else
                note = "Lot updated" + (repriced > 0 ? "; the new price went to " + repriced + " buyer(s) as drafts" : "") +
                       ". Thank the seller briefly; never mention buyers, how many there are, or their prices.";
            return J.Obj("ok", true, "lot_id", w.DryRun ? null : lotId.ToString(), "new", isNew, "window", window.Stated || isNew ? window.Label : null, "offers_close", closes,
                         "offered_to_buyers", offered.Count, "note", note);
        }

        /// <summary>
        /// A seller's new price for an open lot (a counter): the seller offers on its deals follow, our bids to the seller count as
        /// countered, and each buyer's "our price" moves (buyer_accepts works from it).
        /// </summary>
        private static void RefreshOffers(DeskWriter w, Guid lotId, decimal price)
        {
            var ours = Desk.PriceToBuyer(price, Desk.Margin(w.Dv));
            foreach (var d in LiveDeals(w.Dv, lotId))
            {
                foreach (var o in w.Dv.Query("gc_offer", new[] { "gc_fromparty", "gc_status" }, 20, "gc_deal", ConditionOperator.Equal, d.Id,
                                             "gc_status", ConditionOperator.Equal, DeskChoice.Offer.Open))
                {
                    var u = new Entity("gc_offer", o.Id);
                    if (Desk.H(o, "gc_fromparty") == Desk.H(d, "gc_seller")) u["gc_price"] = price;
                    else u["gc_status"] = new OptionSetValue(DeskChoice.Offer.Countered);
                    w.Update(u, Desk.H(o, "gc_fromparty") == Desk.H(d, "gc_seller") ? "lot_price_updated" : "lot_bid_countered");
                }
                var reqId = Desk.H(d, "gc_requirement");
                if (reqId == null) continue;
                var r = new Entity("gc_buyerrequirement", reqId.Value);
                r["gc_ourprice"] = ours;
                w.Update(r, "our_lot_price_to_buyer", J.Obj("price", ours));
            }
        }

        /// <summary>Deals on the lot still in play (Inquiry or Negotiation).</summary>
        public static List<Entity> LiveDeals(Dv dv, Guid lotId)
        {
            return dv.Query("gc_deal", new[] { "gc_name", "gc_stage", "gc_buyerprice", "gc_bidon", "gc_quantity", "gc_requirement", "gc_buyer", "gc_seller" }, 200,
                            "gc_sellerlot", ConditionOperator.Equal, lotId)
                     .Where(d => (d.GetAttributeValue<OptionSetValue>("gc_stage") ?? new OptionSetValue(B)).Value <= DeskChoice.DealStage.Negotiation).ToList();
        }

        /// <summary>The buyer thread of a lot deal, or null.</summary>
        private static Guid? BuyerThread(Dv dv, Entity deal)
        {
            var reqId = Desk.H(deal, "gc_requirement");
            var t = reqId == null ? null : dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_requirement", ConditionOperator.Equal, reqId.Value,
                                                    "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
            return t == null ? (Guid?)null : t.Id;
        }

        /// <summary>
        /// A draft that adds to an unsent draft of the thread instead of replacing it (the earlier text stays first), unless that
        /// draft is an older version of the same kind (it contains marker).
        /// </summary>
        private static Guid AddDraft(DeskWriter w, Guid threadId, string to, string body, string marker, string action, bool auto)
        {
            var dv = w.Dv;
            var pending = dv.Query("gc_message", new[] { "gc_text" }, 1, "gc_conversation", ConditionOperator.Equal, threadId,
                                   "gc_draftstatus", ConditionOperator.Equal, DeskChoice.DraftStatus.Pending).FirstOrDefault();
            var earlier = pending == null ? null : Desk.WithoutSignature(dv, pending.GetAttributeValue<string>("gc_text"));
            var text = earlier != null && earlier.IndexOf(marker, StringComparison.Ordinal) < 0
                ? earlier.TrimEnd() + "\n\n" + body
                : Desk.Greet("Dear Sir,", Desk.ContactName(dv, threadId)) + "\n\n" + body;
            return Desk.Draft(w, threadId, to, Desk.ReplySubject(dv, threadId), text, null, action, auto);
        }

        private static string Address(Dv dv, Guid threadId, Guid? accountId)
        {
            var to = Desk.ReplyAddress(dv, threadId);
            if (!string.IsNullOrWhiteSpace(to) || accountId == null) return to;
            var acc = dv.Retrieve("account", accountId.Value, "emailaddress1");
            return acc == null ? null : acc.GetAttributeValue<string>("emailaddress1");
        }

        /// <summary>The seller countered with a new lot price: each buyer still in play gets our new price (masked) as a draft. Returns how many.</summary>
        private static int Reprice(DeskWriter w, Entity lot)
        {
            var dv = w.Dv;
            var currency = lot.GetAttributeValue<string>("gc_currency") ?? "USD";
            var unit = lot.GetAttributeValue<string>("gc_unit") ?? "MT";
            var ours = Desk.PriceToBuyer(lot.GetAttributeValue<decimal>("gc_price"), Desk.Margin(dv));
            var basis = Basis(lot);
            var qty = lot.GetAttributeValue<decimal?>("gc_quantity");
            var n = 0;
            foreach (var d in LiveDeals(dv, lot.Id))
            {
                var thread = BuyerThread(dv, d);
                var to = thread == null ? null : Address(dv, thread.Value, Desk.H(d, "gc_buyer"));
                if (string.IsNullOrWhiteSpace(to)) continue;
                var body = "Update on " + lot.GetAttributeValue<string>("gc_commoditytext") + ": the price is now " + currency + " " + Desk.Num(ours) + " per " + unit +
                           (basis.Length == 0 ? "" : ", " + basis) + (qty == null ? "" : ", for up to " + Desk.Num(qty.Value) + " " + unit) + "." +
                           (Deadline(lot) == null ? "" : " Offers close on " + When(Deadline(lot).Value) + ".") +
                           " Please let me know if this works for you, or your best price.";
                AddDraft(w, thread.Value, to, body, "the price is now", "draft_lot_reprice", Desk.AutoSend(dv, "reply", body));
                n++;
            }
            return n;
        }

        private static string Basis(Entity lot)
        {
            return ((lot.GetAttributeValue<string>("gc_incoterm") ?? "") + (lot.GetAttributeValue<string>("gc_namedplace") == null ? "" : " " + lot.GetAttributeValue<string>("gc_namedplace"))).Trim();
        }

        /// <summary>
        /// Offers an open lot to buyers: open email requirements for the material first (in their own thread), then buyer leads
        /// (a new thread each). Buyers already offered this lot are skipped, so it runs again after a web search for buyers.
        /// At most email.marketing.max_buyers new buyers per run. Returns who was offered.
        /// </summary>
        public static List<object> Market(DeskWriter w, Guid lotId)
        {
            var dv = w.Dv;
            var lot = Get(dv, lotId);
            var offered = new List<object>();
            if (lot == null || StatusOf(lot) != Status.Open) return offered;
            var commodity = lot.GetAttributeValue<string>("gc_commoditytext") ?? "";
            var tokens = Desk.Tokens(commodity);
            var max = Math.Max(1, dv.SettingInt("email.marketing.max_buyers", 5));
            var sellerId = Desk.H(lot, "gc_seller");
            var now = DateTime.UtcNow;
            var buyers = new HashSet<Guid>(dv.Query("gc_deal", new[] { "gc_buyer" }, 500, "gc_sellerlot", ConditionOperator.Equal, lotId)
                                             .Select(d => Desk.H(d, "gc_buyer")).Where(b => b != null).Select(b => b.Value));
            if (tokens.Count == 0) return offered;

            var rq = new QueryExpression("gc_buyerrequirement") { ColumnSet = new ColumnSet("gc_commoditytext", "gc_buyer", "gc_quantity", "gc_quantityunit", "gc_deskstage"), TopCount = 300 };
            rq.Criteria.AddCondition("gc_source", ConditionOperator.Equal, DeskChoice.RequirementSource.Email);
            rq.Criteria.AddCondition("gc_deskstage", ConditionOperator.In, DeskChoice.Stage.Qualifying, DeskChoice.Stage.Sourcing, DeskChoice.Stage.Quoted, DeskChoice.Stage.Negotiating);
            foreach (var req in dv.Svc.RetrieveMultiple(rq).Entities
                                  .Where(r => Desk.LeadScore(tokens, r.GetAttributeValue<string>("gc_commoditytext"), 1, null, now) > 0))
            {
                if (offered.Count >= max) break;
                var buyer = Desk.H(req, "gc_buyer");
                if (buyer == null || buyer == sellerId || buyers.Contains(buyer.Value)) continue;
                var thread = dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_requirement", ConditionOperator.Equal, req.Id,
                                      "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
                if (thread == null) continue;
                var to = Desk.ReplyAddress(dv, thread.Id);
                if (string.IsNullOrWhiteSpace(to))
                {
                    var acc = dv.Retrieve("account", buyer.Value, "emailaddress1");
                    to = acc == null ? null : acc.GetAttributeValue<string>("emailaddress1");
                }
                if (string.IsNullOrWhiteSpace(to)) continue;
                var dealId = OpenLotDeal(w, lot, buyer.Value, req);
                var text = OfferText(dv, lot, Desk.ContactName(dv, thread.Id), req.GetAttributeValue<decimal?>("gc_quantity"));
                // Keep an unsent draft of that thread: the lot offer is added to it, not swapped for it.
                var pending = dv.Query("gc_message", new[] { "gc_text" }, 1, "gc_conversation", ConditionOperator.Equal, thread.Id,
                                       "gc_draftstatus", ConditionOperator.Equal, DeskChoice.DraftStatus.Pending).FirstOrDefault();
                if (pending != null) text = Desk.WithoutSignature(dv, pending.GetAttributeValue<string>("gc_text")) + "\n\nIn addition, w" + text.Substring(text.IndexOf("e can offer", StringComparison.Ordinal));
                Desk.Draft(w, thread.Id, to, Desk.ReplySubject(dv, thread.Id), text, null, "draft_lot_offer", Desk.AutoSend(dv, "reply", text));
                buyers.Add(buyer.Value);
                offered.Add(J.Obj("buyer_requirement", req.Id.ToString(), "deal_id", dealId.ToString()));
            }

            var lq = new QueryExpression("gc_lead") { ColumnSet = new ColumnSet(true), TopCount = 2000 };
            lq.Criteria.AddCondition("gc_status", ConditionOperator.NotEqual, DeskChoice.LeadStatus.DoNotContact);
            lq.Criteria.AddCondition("gc_role", ConditionOperator.In, DeskChoice.LeadRole.Buyer, DeskChoice.LeadRole.Both);
            var leads = dv.Svc.RetrieveMultiple(lq).Entities
                .Select(l => new { Lead = l, Score = Desk.LeadScore(tokens, l.GetAttributeValue<string>("gc_commodities"), l.GetAttributeValue<int?>("gc_shipments") ?? 0, l.GetAttributeValue<DateTime?>("gc_lastseen"), now) })
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score);
            foreach (var x in leads)
            {
                if (offered.Count >= max) break;
                var email = (x.Lead.GetAttributeValue<string>("gc_email") ?? "").Trim().ToLowerInvariant();
                if (email.IndexOf('@') < 0) continue;
                var name = x.Lead.GetAttributeValue<string>("gc_name") ?? "Buyer";
                var buyerId = Desk.SellerAccount(w, x.Lead, email, name);
                if (buyerId == sellerId || buyers.Contains(buyerId)) continue;
                offered.Add(OfferToLead(w, lot, buyerId, email, x.Lead));
                buyers.Add(buyerId);
            }
            return offered;
        }

        /// <summary>A buyer lead gets the lot in a new thread: requirement (from the lot), buyer thread, deal and the offer draft.</summary>
        private static object OfferToLead(DeskWriter w, Entity lot, Guid buyerId, string email, Entity lead)
        {
            var dv = w.Dv;
            var commodity = lot.GetAttributeValue<string>("gc_commoditytext");
            var qty = lot.GetAttributeValue<decimal?>("gc_quantity");
            var unit = Choice.ValueOf(DeskChoice.Units, lot.GetAttributeValue<string>("gc_unit"));
            var r = new Entity("gc_buyerrequirement");
            r["gc_name"] = Desk.Cut("Lot offer: " + commodity, 100);
            r["gc_commoditytext"] = Desk.Cut(commodity, 400);
            if (qty != null) r["gc_quantity"] = qty.Value;
            if (unit >= 0) r["gc_quantityunit"] = new OptionSetValue(unit);
            r["gc_currency"] = lot.GetAttributeValue<string>("gc_currency") ?? "USD";
            r["gc_buyer"] = new EntityReference("account", buyerId);
            r["gc_source"] = new OptionSetValue(DeskChoice.RequirementSource.Email);
            r["gc_status"] = new OptionSetValue(DeskChoice.Requirement.Open);
            r["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Quoted);
            r["gc_discoveredon"] = DateTime.UtcNow; // no web seller search for a lot offer: the seller is known
            var reqId = w.Create(r, "create_lot_requirement", J.Obj("commodity", commodity));
            var req = dv.Retrieve("gc_buyerrequirement", reqId, "gc_quantity", "gc_quantityunit", "gc_commoditytext");

            var conv = new Entity("gc_conversation");
            conv["gc_name"] = Desk.Cut("Offer: " + commodity + " – " + (lead.GetAttributeValue<string>("gc_name") ?? email), 200);
            conv["gc_channel"] = new OptionSetValue(MailChoice.ChannelEmail);
            conv["gc_side"] = new OptionSetValue(DeskChoice.Side.Buyer);
            conv["gc_requirement"] = new EntityReference("gc_buyerrequirement", reqId);
            conv["gc_counterparty"] = new EntityReference("account", buyerId);
            conv["gc_sellerlot"] = new EntityReference("gc_sellerlot", lot.Id);
            var convId = w.Create(conv, "create_lot_buyer_thread", J.Obj("buyer", lead.GetAttributeValue<string>("gc_name")));
            var dealId = OpenLotDeal(w, lot, buyerId, req);
            var text = OfferText(dv, lot, Desk.PersonName(lead.GetAttributeValue<string>("gc_contactname")), null) +
                       "\n\nIf this is not of interest, a short reply is enough and we will not write about it again.";
            var subject = "Offer: " + commodity + (qty != null ? " – " + Desk.Num(qty.Value) + " " + (lot.GetAttributeValue<string>("gc_unit") ?? "MT") : "");
            var draftId = Desk.Draft(w, convId, email, subject, text, null, "draft_lot_offer_lead", Desk.AutoSend(dv, "enquiry", text));
            return J.Obj("buyer_lead", lead.GetAttributeValue<string>("gc_name"), "email", email, "deal_id", dealId.ToString(), "thread_id", convId.ToString(), "draft_id", draftId.ToString());
        }

        /// <summary>A desk deal buyer ← us ← seller on the lot, with the seller's price as an open seller offer (so the buyer side sees it as a supplier quote).</summary>
        private static Guid OpenLotDeal(DeskWriter w, Entity lot, Guid buyerId, Entity req)
        {
            var sellerId = Desk.H(lot, "gc_seller").Value;
            var lotQty = lot.GetAttributeValue<decimal?>("gc_quantity");
            var wanted = req.GetAttributeValue<decimal?>("gc_quantity");
            var qty = wanted == null ? lotQty : lotQty == null ? wanted : Math.Min(wanted.Value, lotQty.Value);
            var unit = Choice.ValueOf(DeskChoice.Units, lot.GetAttributeValue<string>("gc_unit"));
            var seller = w.Dv.Retrieve("account", sellerId, "name");
            var deal = new Entity("gc_deal");
            deal["gc_name"] = Desk.Cut("Desk lot: " + lot.GetAttributeValue<string>("gc_commoditytext") + " – " + (seller == null ? "seller" : seller.GetAttributeValue<string>("name")), 100);
            deal["gc_stage"] = new OptionSetValue(DeskChoice.DealStage.Inquiry);
            deal["gc_emaildesk"] = true;
            deal["gc_buyer"] = new EntityReference("account", buyerId);
            deal["gc_seller"] = new EntityReference("account", sellerId);
            deal["gc_requirement"] = new EntityReference("gc_buyerrequirement", req.Id);
            deal["gc_sellerlot"] = new EntityReference("gc_sellerlot", lot.Id);
            if (qty != null) deal["gc_quantity"] = qty.Value;
            if (unit >= 0) deal["gc_quantityunit"] = new OptionSetValue(unit);
            deal["gc_currency"] = lot.GetAttributeValue<string>("gc_currency") ?? "USD";
            var inc = Choice.ValueOf(DeskChoice.Incoterms, lot.GetAttributeValue<string>("gc_incoterm"));
            if (inc >= 0) deal["gc_incoterm"] = new OptionSetValue(inc);
            if (lot.GetAttributeValue<string>("gc_namedplace") != null) deal["gc_namedplace"] = lot.GetAttributeValue<string>("gc_namedplace");
            var dealId = w.Create(deal, "create_lot_deal");

            var o = new Entity("gc_offer");
            o["gc_name"] = Desk.Cut("Seller lot price: " + lot.GetAttributeValue<decimal>("gc_price").ToString(CultureInfo.InvariantCulture) + " " + (lot.GetAttributeValue<string>("gc_currency") ?? "USD"), 100);
            o["gc_deal"] = new EntityReference("gc_deal", dealId);
            o["gc_fromparty"] = new EntityReference("account", sellerId);
            o["gc_price"] = lot.GetAttributeValue<decimal>("gc_price");
            o["gc_currency"] = lot.GetAttributeValue<string>("gc_currency") ?? "USD";
            if (qty != null) o["gc_quantity"] = qty.Value;
            if (inc >= 0) o["gc_incoterm"] = new OptionSetValue(inc);
            if (lot.GetAttributeValue<string>("gc_namedplace") != null) o["gc_namedplace"] = lot.GetAttributeValue<string>("gc_namedplace");
            if (lot.GetAttributeValue<DateTime?>("gc_validuntil") != null) o["gc_validuntil"] = lot.GetAttributeValue<DateTime?>("gc_validuntil");
            o["gc_status"] = new OptionSetValue(DeskChoice.Offer.Open);
            o["gc_round"] = 1;
            var terms = new List<string>();
            if (lot.GetAttributeValue<string>("gc_origin") != null) terms.Add("Origin: " + lot.GetAttributeValue<string>("gc_origin"));
            if (lot.GetAttributeValue<string>("gc_terms") != null) terms.Add(lot.GetAttributeValue<string>("gc_terms"));
            o["gc_terms"] = string.Join("\n", terms);
            w.Create(o, "lot_seller_offer");
            // Our price to this buyer, unless an ordinary quote already gave them a better one (buyer_accepts works from it).
            var ours = Desk.PriceToBuyer(lot.GetAttributeValue<decimal>("gc_price"), Desk.Margin(w.Dv));
            var current = w.Dv.Retrieve("gc_buyerrequirement", req.Id, "gc_ourprice");
            var last = current == null ? null : current.GetAttributeValue<decimal?>("gc_ourprice");
            if (current != null && (last == null || ours < last.Value))
            {
                var r = new Entity("gc_buyerrequirement", req.Id);
                r["gc_ourprice"] = ours;
                w.Update(r, "our_lot_price_to_buyer", J.Obj("price", ours));
            }
            return dealId;
        }

        /// <summary>The masked offer of a lot to a buyer: our price (seller price + margin), never the seller.</summary>
        public static string OfferText(Dv dv, Entity lot, string addressee, decimal? wanted)
        {
            var currency = lot.GetAttributeValue<string>("gc_currency") ?? "USD";
            var unit = lot.GetAttributeValue<string>("gc_unit") ?? "MT";
            var ours = Desk.PriceToBuyer(lot.GetAttributeValue<decimal>("gc_price"), Desk.Margin(dv));
            var basis = (lot.GetAttributeValue<string>("gc_incoterm") ?? "") + (lot.GetAttributeValue<string>("gc_namedplace") == null ? "" : " " + lot.GetAttributeValue<string>("gc_namedplace"));
            var l = new List<string> { Desk.Greet("Dear Sir,", addressee), "", "We can offer the following, available now:", "",
                                       "- Product: " + lot.GetAttributeValue<string>("gc_commoditytext") };
            var sellerId = Desk.H(lot, "gc_seller");
            var seller = sellerId == null ? null : dv.Retrieve("account", sellerId.Value, "name");
            var spec = Desk.SafeSpec(lot.GetAttributeValue<string>("gc_specification"), seller == null ? null : seller.GetAttributeValue<string>("name"));
            var sellerPrice = lot.GetAttributeValue<decimal>("gc_price");
            var digits = new System.Text.RegularExpressions.Regex(@"[^\d]");
            if (spec != null && sellerPrice > 0 && digits.Replace(spec, "").Contains(digits.Replace(sellerPrice.ToString("0.##", CultureInfo.InvariantCulture), "")) &&
                digits.Replace(sellerPrice.ToString("0.##", CultureInfo.InvariantCulture), "").Length >= 3)
                spec = null; // the seller's price figure must never reach a buyer, even without a currency
            if (spec != null) l.Add("- Specification: " + spec);
            var qty = lot.GetAttributeValue<decimal?>("gc_quantity");
            if (qty != null) l.Add("- Quantity available: " + Desk.Num(qty.Value) + " " + unit);
            l.Add("- Price: " + currency + " " + Desk.Num(ours) + " per " + unit + (string.IsNullOrWhiteSpace(basis) ? "" : ", " + basis.Trim()));
            if (lot.GetAttributeValue<string>("gc_origin") != null) l.Add("- Origin: " + lot.GetAttributeValue<string>("gc_origin"));
            var deadline = Deadline(lot);
            l.Add("");
            l.Add("COA and company profile available on request. Other discharge ports can be discussed.");
            l.Add("");
            l.Add((deadline == null ? "" : "Offers close on " + When(deadline.Value) + ". ") +
                  "Please let me know if this is of interest, the quantity you need" + (wanted != null && qty != null && wanted.Value < qty.Value ? " (your requirement: " + Desk.Num(wanted.Value) + " " + unit + ")" : "") +
                  " and whether the price works, or your best price.");
            return string.Join("\n", l);
        }

        // ---------------------------------------------------------------- bids

        /// <summary>
        /// A buyer's acceptance or price on a lot deal is a bid until the deadline: recorded on the deal (buyer price, bid time), nothing goes
        /// to the seller. Refuses after the deadline or once the lot is closed.
        /// </summary>
        public static Dictionary<string, object> RecordBid(DeskWriter w, Entity lot, Guid dealId, Guid requirementId, decimal buyerPrice, decimal? quantity)
        {
            var deadline = Deadline(lot);
            var openEnded = OpenEnded(lot);
            if (StatusOf(lot) != Status.Open || (deadline != null && deadline.Value <= DateTime.UtcNow))
                throw new ToolRefusal("Offers for this lot have closed" + (StatusOf(lot) == Status.Allocated ? " and the material is allocated" : "") +
                                      ". Tell the buyer politely that this material is no longer available and that we will come back with other offers.");
            var d = new Entity("gc_deal", dealId);
            d["gc_buyerprice"] = buyerPrice;
            d["gc_bidon"] = DateTime.UtcNow;
            if (quantity != null && quantity > 0)
            {
                var lotQty = lot.GetAttributeValue<decimal?>("gc_quantity");
                d["gc_quantity"] = lotQty == null ? quantity.Value : Math.Min(quantity.Value, lotQty.Value);
            }
            w.Update(d, "lot_bid", J.Obj("buyer_price", buyerPrice, "quantity", quantity));
            var r = new Entity("gc_buyerrequirement", requirementId);
            r["gc_targetprice"] = buyerPrice;
            r["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Negotiating);
            w.Update(r, "lot_bid_requirement");
            if (openEnded)
            {
                // Open-ended: the bid goes to the seller now (buyer price less our margin, buyer masked); the seller decides.
                if (!w.DryRun) Relay(w, lot, w.Dv.Retrieve("gc_deal", dealId, "gc_name", "gc_buyer", "gc_seller", "gc_quantity"), buyerPrice);
                return J.Obj("ok", true, "bid_recorded", buyerPrice, "offers_close", "open-ended: the supplier decides",
                             "note", "The bid is drafted to the supplier automatically. Draft to the buyer ONLY: we have put their offer of " + Desk.Num(buyerPrice) +
                                     " to the supplier and will revert as soon as they answer. Do not promise the material.");
            }
            if (!w.DryRun && EveryoneAnswered(w.Dv, lot.Id, dealId))
            {
                // Nobody left to wait for (one buyer, or all have bid or declined): offers close now; Desk timers confirms within 15 minutes.
                CloseNow(w, lot.Id, "all buyers answered");
                return J.Obj("ok", true, "bid_recorded", buyerPrice, "offers_close", "now (every buyer offered this lot has answered)",
                             "note", "Draft to the buyer ONLY: we have noted their offer of " + Desk.Num(buyerPrice) + " and will confirm shortly. Do not contact the seller and do not promise the material.");
            }
            return J.Obj("ok", true, "bid_recorded", buyerPrice, "offers_close", deadline == null ? null : When(deadline.Value),
                         "note", "This is a bid on a lot that other buyers may want. Draft to the buyer ONLY: we have noted their offer of " + Desk.Num(buyerPrice) +
                                 " and will confirm right after offers close (offers_close). Do not contact the seller and do not promise the material.");
        }

        /// <summary>Every live deal on the lot has a bid (the deal just bid on counts as answered); declined buyers' deals are already closed.</summary>
        public static bool EveryoneAnswered(Dv dv, Guid lotId, Guid justBid)
        {
            return dv.Query("gc_deal", new[] { "gc_buyerprice", "gc_stage" }, 200, "gc_sellerlot", ConditionOperator.Equal, lotId)
                     .Where(d => (d.GetAttributeValue<OptionSetValue>("gc_stage") ?? new OptionSetValue(B)).Value <= DeskChoice.DealStage.Negotiation)
                     .All(d => d.Id == justBid || (d.GetAttributeValue<decimal?>("gc_buyerprice") ?? 0) > 0);
        }

        private static void CloseNow(DeskWriter w, Guid lotId, string why)
        {
            var u = new Entity("gc_sellerlot", lotId);
            u["gc_biddeadline"] = DateTime.UtcNow;
            w.Update(u, "lot_closes_early", J.Obj("why", why));
        }

        /// <summary>
        /// A buyer says no to a lot offer: their lot deal closes (nothing to wait for) and, if every other buyer has already bid,
        /// offers close now.
        /// </summary>
        public static Dictionary<string, object> Decline(DeskWriter w, Entity lot, Guid dealId, string reason)
        {
            var req = new OrganizationRequest("gc_TransitionDeal");
            req["DealId"] = dealId;
            req["TargetStage"] = DeskChoice.DealStage.Cancelled;
            req["Reason"] = "Buyer declined the lot offer" + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + Desk.Cut(reason, 200));
            if (!w.DryRun) w.Dv.System.Execute(req);
            if (!w.DryRun) CloseLotRequirement(w, Desk.H(w.Dv.Retrieve("gc_deal", dealId, "gc_requirement"), "gc_requirement"));
            var others = w.Dv.Query("gc_deal", new[] { "gc_buyerprice", "gc_stage" }, 200, "gc_sellerlot", ConditionOperator.Equal, lot.Id)
                          .Where(d => d.Id != dealId && (d.GetAttributeValue<OptionSetValue>("gc_stage") ?? new OptionSetValue(B)).Value <= DeskChoice.DealStage.Negotiation).ToList();
            var closing = !w.DryRun && StatusOf(lot) == Status.Open && !OpenEnded(lot) && others.Count > 0 && others.All(d => (d.GetAttributeValue<decimal?>("gc_buyerprice") ?? 0) > 0);
            if (closing) CloseNow(w, lot.Id, "the last buyer declined");
            return J.Obj("ok", true, "declined", true, "offers_close_now", closing,
                         "note", "Draft a short, polite thank-you to the buyer (no other details). We will keep them in mind for future offers.");
        }

        // ---------------------------------------------------------------- bids to the seller (open-ended lots)

        /// <summary>
        /// Our firm bid to the seller for one buyer's price on the lot: an open offer from the buyer side at the buyer price less our
        /// margin. An earlier open bid of that buyer is superseded (countered). Returns the open bid.
        /// </summary>
        public static Entity BidOffer(DeskWriter w, Entity lot, Entity deal, decimal buyerPrice)
        {
            var dv = w.Dv;
            var buyerId = Desk.H(deal, "gc_buyer");
            var bid = Desk.BidToSeller(buyerPrice, Desk.Margin(dv));
            var offers = dv.Query("gc_offer", new[] { "gc_price", "gc_status", "gc_fromparty", "gc_quantity", "gc_currency" }, 50, "gc_deal", ConditionOperator.Equal, deal.Id);
            foreach (var o in offers.Where(o => Desk.H(o, "gc_fromparty") == buyerId && (o.GetAttributeValue<OptionSetValue>("gc_status") ?? new OptionSetValue(-1)).Value == DeskChoice.Offer.Open))
            {
                if (o.GetAttributeValue<decimal>("gc_price") == bid) return o;
                var u = new Entity("gc_offer", o.Id);
                u["gc_status"] = new OptionSetValue(DeskChoice.Offer.Countered);
                w.Update(u, "lot_bid_superseded");
            }
            var currency = lot.GetAttributeValue<string>("gc_currency") ?? "USD";
            var qty = deal.GetAttributeValue<decimal?>("gc_quantity") ?? lot.GetAttributeValue<decimal?>("gc_quantity");
            var e = new Entity("gc_offer");
            e["gc_name"] = Desk.Cut("Our bid on lot: " + bid.ToString(CultureInfo.InvariantCulture) + " " + currency, 100);
            e["gc_deal"] = new EntityReference("gc_deal", deal.Id);
            if (buyerId != null) e["gc_fromparty"] = new EntityReference("account", buyerId.Value);
            e["gc_price"] = bid;
            e["gc_currency"] = currency;
            if (qty != null) e["gc_quantity"] = qty.Value;
            var inc = Choice.ValueOf(DeskChoice.Incoterms, lot.GetAttributeValue<string>("gc_incoterm"));
            if (inc >= 0) e["gc_incoterm"] = new OptionSetValue(inc);
            if (lot.GetAttributeValue<string>("gc_namedplace") != null) e["gc_namedplace"] = lot.GetAttributeValue<string>("gc_namedplace");
            e["gc_status"] = new OptionSetValue(DeskChoice.Offer.Open);
            e["gc_round"] = offers.Count + 1;
            e["gc_terms"] = "Our firm bid on behalf of our buyer (seller lot).";
            e.Id = w.Create(e, "our_lot_bid_to_seller", J.Obj("bid", bid, "buyer_price", buyerPrice));
            return e;
        }

        /// <summary>Our open bids to the seller on a lot (one per buyer in play), highest first.</summary>
        public static List<Entity> OpenBids(Dv dv, Guid lotId)
        {
            var bids = new List<Entity>();
            foreach (var d in LiveDeals(dv, lotId))
                bids.AddRange(dv.Query("gc_offer", new[] { "gc_price", "gc_currency", "gc_quantity", "gc_fromparty", "gc_deal", "createdon" }, 20, "gc_deal", ConditionOperator.Equal, d.Id,
                                       "gc_status", ConditionOperator.Equal, DeskChoice.Offer.Open)
                                .Where(o => Desk.H(o, "gc_fromparty") == Desk.H(d, "gc_buyer")));
            return bids.OrderByDescending(o => o.GetAttributeValue<decimal>("gc_price")).ThenBy(o => o.GetAttributeValue<DateTime>("createdon")).ToList();
        }

        /// <summary>A buyer's bid goes to the seller: our bid offer and a draft in the seller thread listing every open bid.</summary>
        private static void Relay(DeskWriter w, Entity lot, Entity deal, decimal buyerPrice)
        {
            BidOffer(w, lot, deal, buyerPrice);
            SellerBidsDraft(w, lot);
        }

        private const string BidsMarker = "irm bid";

        /// <summary>The draft to the seller with our open bids (masked: price to the seller and quantity only). Null when there is none.</summary>
        public static Guid? SellerBidsDraft(DeskWriter w, Entity lot)
        {
            var dv = w.Dv;
            var thread = Desk.H(lot, "gc_sellerthread");
            if (thread == null) return null;
            var to = Address(dv, thread.Value, Desk.H(lot, "gc_seller"));
            var bids = OpenBids(dv, lot.Id);
            if (string.IsNullOrWhiteSpace(to) || bids.Count == 0) return null;
            var unit = lot.GetAttributeValue<string>("gc_unit") ?? "MT";
            var basis = Basis(lot);
            var lines = bids.Select(b => "- " + (b.GetAttributeValue<string>("gc_currency") ?? "USD") + " " + Desk.Num(b.GetAttributeValue<decimal>("gc_price")) + " per " + unit +
                                         (b.GetAttributeValue<decimal?>("gc_quantity") == null ? "" : " for " + Desk.Num(b.GetAttributeValue<decimal>("gc_quantity")) + " " + unit) +
                                         (basis.Length == 0 ? "" : ", " + basis));
            var body = (bids.Count == 1 ? "Firm bid from our buyer for your " : "Firm bids from our buyers for your ") + lot.GetAttributeValue<string>("gc_commoditytext") + ":\n\n" +
                       string.Join("\n", lines) + "\n\nPlease confirm if you accept" + (bids.Count == 1 ? "" : " (and which one)") + ", or let me know your best price. Once confirmed, our purchase contract follows.";
            return AddDraft(w, thread.Value, to, body, BidsMarker, "draft_lot_bids", Desk.AutoSend(dv, "reply", body));
        }

        // ---------------------------------------------------------------- the seller decides (open-ended lots)

        /// <summary>
        /// The seller accepts our bids at or above acceptPrice (their price per unit, as in our bid email): the best of them win while
        /// quantity lasts, each through Confirm deal, at our bid to the seller. Or the seller withdraws the lot.
        /// </summary>
        public static object SellerDecides(DeskWriter w, Entity lot, decimal? acceptPrice, bool withdraw, string evidence)
        {
            if (StatusOf(lot) != Status.Open) throw new ToolRefusal("This lot is already " + Statuses[StatusOf(lot) - B].ToLowerInvariant() + ".");
            if (withdraw) return Withdraw(w, lot, evidence);
            var margin = Desk.Margin(w.Dv);
            var deals = LiveDeals(w.Dv, lot.Id);
            var bids = BidsOf(deals, lot);
            var current = OpenBids(w.Dv, lot.Id).Select(o => Desk.Num(o.GetAttributeValue<decimal>("gc_price"))).ToList();
            if (acceptPrice == null || acceptPrice <= 0)
                throw new ToolRefusal("Give accept_price: the price per unit the seller accepts. Our open bids to them: " + (current.Count == 0 ? "none" : string.Join(", ", current)) + ".");
            var eligible = bids.Where(b => Desk.BidToSeller(b.Price, margin) >= acceptPrice.Value - 0.01m).ToList();
            if (eligible.Count == 0)
                throw new ToolRefusal("No bid of ours is at or above " + Desk.Num(acceptPrice.Value) + ". Our open bids: " + (current.Count == 0 ? "none" : string.Join(", ", current)) +
                                      ". If the seller names a new price, that is a counter: save_seller_lot with the new price (it goes to the buyers).");
            if (w.DryRun) return J.Obj("ok", true, "note", "Dry run: " + eligible.Count + " bid(s) would win.");
            return Allocate(w, lot, deals, bids, eligible.Min(b => b.Price), true, evidence);
        }

        private static object Withdraw(DeskWriter w, Entity lot, string evidence)
        {
            var dv = w.Dv;
            var commodity = lot.GetAttributeValue<string>("gc_commoditytext");
            if (w.DryRun) return J.Obj("ok", true, "note", "Dry run: the lot would be withdrawn.");
            var u = new Entity("gc_sellerlot", lot.Id);
            u["gc_status"] = new OptionSetValue(Status.Withdrawn);
            u["gc_closedon"] = DateTime.UtcNow;
            w.Update(u, "lot_withdrawn", J.Obj("evidence", evidence));
            var notes = 0;
            foreach (var d in LiveDeals(dv, lot.Id))
            {
                var bid = (d.GetAttributeValue<decimal?>("gc_buyerprice") ?? 0) > 0;
                Cancel(dv, d.Id, "The seller withdrew the lot");
                if (bid && BuyerRegretNote(w, d, commodity, "The material on offer is no longer available") != null) notes++;
                CloseLotRequirement(w, Desk.H(d, "gc_requirement"));
            }
            Desk.Brief(w, "Lot withdrawn by the seller: " + commodity, "The seller withdrew the lot" + (string.IsNullOrWhiteSpace(evidence) ? "." : ": \"" + Desk.Cut(evidence, 300) + "\"") +
                       "\n\n" + notes + " buyer(s) who bid get a short 'no longer available' draft.");
            return J.Obj("ok", true, "withdrawn", true, "regret_notes", notes, "note", "Buyers are told by draft. Draft a short acknowledgement to the seller.");
        }

        private static void Cancel(Dv dv, Guid dealId, string reason)
        {
            var req = new OrganizationRequest("gc_TransitionDeal");
            req["DealId"] = dealId;
            req["TargetStage"] = DeskChoice.DealStage.Cancelled;
            req["Reason"] = reason;
            dv.System.Execute(req);
        }

        private static List<Bid> BidsOf(List<Entity> deals, Entity lot)
        {
            var lotQty = lot.GetAttributeValue<decimal?>("gc_quantity") ?? decimal.MaxValue;
            return deals.Where(d => (d.GetAttributeValue<decimal?>("gc_buyerprice") ?? 0) > 0)
                        .Select(d => new Bid { DealId = d.Id, Price = d.GetAttributeValue<decimal>("gc_buyerprice"),
                                               Quantity = d.GetAttributeValue<decimal?>("gc_quantity") ?? lotQty, On = d.GetAttributeValue<DateTime?>("gc_bidon") ?? DateTime.MaxValue })
                        .ToList();
        }

        // ---------------------------------------------------------------- close

        /// <summary>Closes every open lot whose deadline has passed. Returns one line per lot for the briefing.</summary>
        public static Dictionary<string, object> CloseDue(DeskWriter w)
        {
            var q = new QueryExpression("gc_sellerlot") { ColumnSet = new ColumnSet(LotColumns), TopCount = 50 };
            q.Criteria.AddCondition("gc_status", ConditionOperator.Equal, Status.Open);
            q.Criteria.AddCondition("gc_biddeadline", ConditionOperator.LessEqual, DateTime.UtcNow);
            var results = new List<object>();
            foreach (var lot in w.Dv.Svc.RetrieveMultiple(q).Entities) results.Add(Close(w, lot));
            return J.Obj("closed", results.Count, "lots", results);
        }

        /// <summary>A bid on a lot: its deal, buyer price, quantity and time.</summary>
        public sealed class Bid
        {
            public Guid DealId;
            public decimal Price;
            public decimal Quantity;
            public DateTime On;
        }

        /// <summary>
        /// Ranks bids (highest buyer price first, then earliest) and picks winners: at or above the floor (seller price + margin)
        /// and only while the lot has enough quantity left. Pure, for tests.
        /// </summary>
        public static List<Bid> Winners(IEnumerable<Bid> bids, decimal floor, decimal available)
        {
            var winners = new List<Bid>();
            var left = available;
            foreach (var b in bids.OrderByDescending(b => b.Price).ThenBy(b => b.On))
            {
                if (b.Price < floor) break;
                if (b.Quantity <= left) { winners.Add(b); left -= b.Quantity; }
            }
            return winners;
        }

        public static object Close(DeskWriter w, Entity lot)
        {
            var dv = w.Dv;
            var floor = Desk.PriceToBuyer(lot.GetAttributeValue<decimal>("gc_price"), Desk.Margin(dv));
            var unit = lot.GetAttributeValue<string>("gc_unit") ?? "MT";
            var commodity = lot.GetAttributeValue<string>("gc_commoditytext");
            var deals = LiveDeals(dv, lot.Id);
            var bids = BidsOf(deals, lot);
            var available = (lot.GetAttributeValue<decimal?>("gc_quantity") ?? decimal.MaxValue) - (lot.GetAttributeValue<decimal?>("gc_allocated") ?? 0);
            if (Winners(bids, floor, available).Count > 0) return Allocate(w, lot, deals, bids, floor, false, null);

            var u = new Entity("gc_sellerlot", lot.Id);
            u["gc_outcome"] = Desk.Cut(Json.Serialize(J.Obj("floor", floor, "ranking", Ranking(deals, bids, new List<Bid>()))), 100000);
            if (bids.Count == 0)
            {
                u["gc_closedon"] = DateTime.UtcNow;
                u["gc_status"] = new OptionSetValue(Status.Closed);
                w.Update(u, "lot_closed_no_bid");
                Desk.Brief(w, "Lot closed without a bid: " + commodity, "No buyer bid before offers closed (needed " + Desk.Num(floor) + " per " + unit + " to cover the seller's price and our margin).");
                return J.Obj("lot", lot.GetAttributeValue<string>("gc_name"), "winners", 0, "bids", 0);
            }

            // No bid reaches the seller's price: the best one goes to the seller, and the lot carries on open-ended (the seller decides).
            var best = bids.OrderByDescending(b => b.Price).ThenBy(b => b.On).First();
            u["gc_biddeadline"] = null;
            u["gc_window"] = "Open-ended (best bid put to the seller)";
            w.Update(u, "lot_best_bid_to_seller", J.Obj("best", best.Price, "floor", floor));
            var deal = deals.First(d => d.Id == best.DealId);
            BidOffer(w, lot, deal, best.Price);
            var drafted = SellerBidsDraft(w, Get(dv, lot.Id)) != null;
            Desk.Brief(w, "Lot: best bid put to the seller: " + commodity, "Offers closed with " + bids.Count + " bid(s); none reached the seller's price + margin (" + Desk.Num(floor) + " per " + unit + "). " +
                       "Best bid: " + Desk.Num(best.Price) + " for " + Desk.Num(best.Quantity) + " " + unit + ", put to the seller as " +
                       Desk.Num(Desk.BidToSeller(best.Price, Desk.Margin(dv))) + (drafted ? " (draft in the seller thread)." : " (no seller address: write to them yourself).") +
                       "\n\nThe lot stays open-ended: further bids go to the seller as they come; the seller accepts, counters or withdraws.");
            return J.Obj("lot", lot.GetAttributeValue<string>("gc_name"), "winners", 0, "bids", bids.Count, "best_bid_to_seller", best.Price);
        }

        private static List<object> Ranking(List<Entity> deals, List<Bid> bids, List<Bid> winners)
        {
            return bids.OrderByDescending(b => b.Price).ThenBy(b => b.On)
                       .Select(b => (object)J.Obj("deal", deals.First(d => d.Id == b.DealId).GetAttributeValue<string>("gc_name"), "buyer_price", b.Price, "quantity", b.Quantity,
                                                  "bid_on", b.On, "won", winners.Any(x => x.DealId == b.DealId))).ToList();
        }

        /// <summary>
        /// Allocates the lot: bids at or above floor (a buyer price) win, highest first, while quantity lasts. Timed close: each winner
        /// buys at the seller's lot price. Seller's acceptance (sellerAccepted): at our bid to the seller for that buyer. Confirm deal tasks,
        /// confirmation drafts to winners and seller, "not this time" to the others, briefing.
        /// </summary>
        private static object Allocate(DeskWriter w, Entity lot, List<Entity> deals, List<Bid> bids, decimal floor, bool sellerAccepted, string evidence)
        {
            var dv = w.Dv;
            var sellerPrice = lot.GetAttributeValue<decimal>("gc_price");
            var currency = lot.GetAttributeValue<string>("gc_currency") ?? "USD";
            var available = (lot.GetAttributeValue<decimal?>("gc_quantity") ?? decimal.MaxValue) - (lot.GetAttributeValue<decimal?>("gc_allocated") ?? 0);
            var unit = lot.GetAttributeValue<string>("gc_unit") ?? "MT";
            var commodity = lot.GetAttributeValue<string>("gc_commoditytext");
            var winners = Winners(bids, floor, available);
            var ranking = Ranking(deals, bids, winners);

            var u = new Entity("gc_sellerlot", lot.Id);
            u["gc_closedon"] = DateTime.UtcNow;
            u["gc_outcome"] = Desk.Cut(Json.Serialize(J.Obj("floor", floor, "seller_accepted", sellerAccepted, "evidence", evidence, "ranking", ranking)), 100000);
            var sold = winners.Sum(x => x.Quantity);
            u["gc_status"] = new OptionSetValue(Status.Allocated);
            u["gc_allocated"] = (lot.GetAttributeValue<decimal?>("gc_allocated") ?? 0) + sold;
            w.Update(u, "lot_allocated", J.Obj("winners", winners.Count, "quantity", sold, "seller_accepted", sellerAccepted));

            var purchases = new List<string>();
            foreach (var win in winners)
            {
                var deal = deals.First(d => d.Id == win.DealId);
                Entity offer;
                if (sellerAccepted) offer = BidOffer(w, lot, deal, win.Price);
                else offer = dv.Query("gc_offer", new[] { "gc_price", "gc_currency", "gc_fromparty" }, 10, "gc_deal", ConditionOperator.Equal, win.DealId,
                                      "gc_status", ConditionOperator.Equal, DeskChoice.Offer.Open).FirstOrDefault(o => Desk.H(o, "gc_fromparty") != Desk.H(deal, "gc_buyer"));
                if (offer == null) continue;
                var paid = sellerAccepted ? offer.GetAttributeValue<decimal>("gc_price") : sellerPrice;
                purchases.Add(Desk.Num(win.Quantity) + " " + unit + " at " + currency + " " + Desk.Num(paid) + " per " + unit);
                ConfirmDeal(w, offer, paid, win, deal.GetAttributeValue<string>("gc_name"), sellerAccepted ? "Seller accepted our bid on the lot" : "Highest offer when the lot closed");
                var thread = BuyerThread(dv, deal);
                if (thread != null && Desk.ReplyAddress(dv, thread.Value) != null)
                {
                    var text = Desk.Greet("Dear Sir,", Desk.ContactName(dv, thread.Value)) + "\n\nConfirmed: " + commodity + ", " + Desk.Num(win.Quantity) + " " + unit + " at " + currency + " " +
                               Desk.Num(win.Price) + " per " + unit + ".\n\nOur sales contract follows. For our records, please send your company registration certificate, " +
                               "GST and IEC (or the equivalent in your country) and the name of the authorised signatory.";
                    Desk.Draft(w, thread.Value, Desk.ReplyAddress(dv, thread.Value), Desk.ReplySubject(dv, thread.Value), text, null, "draft_lot_win", false);
                }
            }
            var sellerThread = Desk.H(lot, "gc_sellerthread");
            if (sellerThread != null && Desk.ReplyAddress(dv, sellerThread.Value) != null && purchases.Count > 0)
            {
                var text = Desk.Greet("Dear Sir,", Desk.ContactName(dv, sellerThread.Value)) + "\n\n" + (sellerAccepted ? "Thanks for confirming. W" : "Further to your offer, w") +
                           "e confirm our purchase of " + commodity + ": " + (purchases.Count == 1 ? purchases[0] + "." : "\n\n- " + string.Join("\n- ", purchases)) +
                           "\n\nPurchase contract follows. Please keep the material reserved for us and send your company registration certificate, export licence (where needed) and the name of the authorised signatory.";
                Desk.Draft(w, sellerThread.Value, Desk.ReplyAddress(dv, sellerThread.Value), Desk.ReplySubject(dv, sellerThread.Value), text, null, "draft_lot_purchase", false);
            }

            var losers = 0;
            foreach (var d in deals.Where(d => winners.All(x => x.DealId != d.Id)))
            {
                var bid = bids.Any(b => b.DealId == d.Id);
                Cancel(dv, d.Id, bid ? "Lot allocated to a higher offer" : "Lot allocated; no offer received");
                if (bid && BuyerRegretNote(w, d, commodity) != null) losers++;
                CloseLotRequirement(w, Desk.H(d, "gc_requirement"));
            }
            Desk.Brief(w, "Lot allocated: " + commodity, (sellerAccepted ? "The seller accepted our bid. " : "") + winners.Count + " winning offer(s) for " + Desk.Num(sold) + " " + unit +
                       " (best " + Desk.Num(winners.Max(x => x.Price)) + " per " + unit + "; seller's price " + (purchases.Count == 0 ? "-" : string.Join(", ", purchases)) +
                       "). Confirm deal task(s) are open for you.\n\n" + losers + " other bidder(s) get a 'not this time' draft." +
                       "\n\nRanking:\n" + string.Join("\n", ranking.Select(x => { var o = (Dictionary<string, object>)x; return "- " + Desk.Num(Convert.ToDecimal(o["buyer_price"])) + " for " +
                           Desk.Num(Convert.ToDecimal(o["quantity"])) + " " + unit + ((bool)o["won"] ? "  WON" : ""); })));
            return J.Obj("lot", lot.GetAttributeValue<string>("gc_name"), "winners", winners.Count, "quantity", sold, "bids", bids.Count, "regret_notes", losers,
                         "note", sellerAccepted ? "Confirm deal task(s) are open and the confirmation drafts to the seller and the winning buyer(s) are created: do NOT draft to the seller again." : null);
        }

        /// <summary>Same task as the Trade Desk's Confirm deal: approval accepts the offer at the buyer's bid (Review decisions flow).</summary>
        private static void ConfirmDeal(DeskWriter w, Entity offer, decimal sellerPrice, Bid win, string dealName, string what)
        {
            var open = w.Dv.Query("gc_reviewtask", new[] { "gc_reviewtaskid" }, 1, "gc_deal", ConditionOperator.Equal, win.DealId,
                                  "gc_status", ConditionOperator.Equal, Choice.ReviewStatus.Open, "gc_name", ConditionOperator.Like, "Confirm deal%").FirstOrDefault();
            if (open != null) return;
            var t = new Entity("gc_reviewtask");
            t["gc_name"] = Desk.Cut("Confirm deal: " + dealName, 100);
            t["gc_kind"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewKinds, "Approval"));
            t["gc_purpose"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewPurposes, "Other"));
            t["gc_assigneerole"] = new OptionSetValue(Choice.ValueOf(Choice.AdminRoles, "Deal Manager"));
            t["gc_status"] = new OptionSetValue(Choice.ReviewStatus.Open);
            t["gc_deal"] = new EntityReference("gc_deal", win.DealId);
            t["gc_payload"] = Json.Serialize(J.Obj("action", "desk.accept_offer", "offerId", offer.Id.ToString(), "dealId", win.DealId.ToString(),
                "buyerPrice", win.Price, "sellerPrice", sellerPrice, "currency", offer.GetAttributeValue<string>("gc_currency"),
                "what", what, "quantity", win.Quantity,
                "approve", "Approve = the offer is accepted, the deal moves to Terms Agreed, compliance checks and the contract follow. Send the drafted confirmations from Gmail."));
            w.Create(t, "confirm_lot_deal");
            var deal = w.Dv.Retrieve("gc_deal", win.DealId, "gc_requirement");
            var reqId = Desk.H(deal, "gc_requirement");
            if (reqId != null)
            {
                var r = new Entity("gc_buyerrequirement", reqId.Value);
                r["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Agreed);
                w.Update(r, "requirement_agreed");
            }
        }

        /// <summary>
        /// A requirement that exists only because we offered a lot ("Lot offer: ...") closes with the buyer's lot deal, so later lots of the
        /// same material go to the buyer as a lead again, not into an old thread as if they had asked. Requirements the buyer sent stay open.
        /// </summary>
        private static void CloseLotRequirement(DeskWriter w, Guid? reqId)
        {
            if (reqId == null) return;
            var req = w.Dv.Retrieve("gc_buyerrequirement", reqId.Value, "gc_name", "gc_deskstage");
            if (req == null || !(req.GetAttributeValue<string>("gc_name") ?? "").StartsWith("Lot offer:", StringComparison.Ordinal)) return;
            var live = w.Dv.Query("gc_deal", new[] { "gc_stage" }, 20, "gc_requirement", ConditionOperator.Equal, reqId.Value)
                        .Any(d => (d.GetAttributeValue<OptionSetValue>("gc_stage") ?? new OptionSetValue(B)).Value != DeskChoice.DealStage.Cancelled);
            if (live) return;
            var r = new Entity("gc_buyerrequirement", reqId.Value);
            r["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Closed);
            w.Update(r, "lot_requirement_closed");
        }

        /// <summary>A bidder who lost a lot: a polite "allocated this time" draft in their thread (no price, no other buyer).</summary>
        public static Guid? BuyerRegretNote(DeskWriter w, Entity deal, string commodity, string why = "The material on offer has now been allocated")
        {
            var dv = w.Dv;
            var reqId = Desk.H(deal, "gc_requirement");
            var thread = reqId == null ? null : dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_requirement", ConditionOperator.Equal, reqId.Value,
                                                         "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
            if (thread == null) return null;
            var to = Desk.ReplyAddress(dv, thread.Id);
            if (string.IsNullOrWhiteSpace(to)) return null;
            var text = Desk.Greet("Dear Sir,", Desk.ContactName(dv, thread.Id)) + "\n\nThanks for your offer on " + commodity +
                       ". " + why + ", so we can't proceed this time.\n\nWe have your requirement noted and will come back with the next suitable offer.";
            return Desk.Draft(w, thread.Id, to, Desk.ReplySubject(dv, thread.Id), text, null, "draft_lot_regret", Desk.AutoSend(dv, "reply", text));
        }
    }
}

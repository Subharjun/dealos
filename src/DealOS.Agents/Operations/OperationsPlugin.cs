using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Tools;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Operations
{
    /// <summary>Choice values for the deterministic deal operations (publisher option prefix 30330).</summary>
    internal static class Ops
    {
        public const int B = Choice.Base;
        public static class Stage { public const int Inquiry = B, Negotiation = B + 1, TermsAgreed = B + 2, Signed = B + 5, AwaitingFunding = B + 6, Cancelled = B + 12; }
        public static class Offer { public const int Open = B, Countered = B + 1, Accepted = B + 2, Rejected = B + 3; }
        public static class Lot { public const int Available = B, Reserved = B + 1; }
        public static class Requirement { public const int Fulfilled = B + 3; }
        public static class Payment { public const int AwaitingFunding = B, Funded = B + 1, ReleasePending = B + 2, ReleaseInstructed = B + 3, Cancelled = B + 7; }
        public static class Release { public const int Pending = B, ConditionsMet = B + 1, AwaitingApproval = B + 2, Instructed = B + 3; }
        public static class Review { public const int FundRelease = B + 4, Approved = B + 1; }
        public const int OverlayNone = B;
    }

    /// <summary>
    /// Deterministic deal operations exposed as Custom APIs. No AI is involved.
    ///   gc_AcceptOffer(OfferId)  → Status, DealId, Stage, ClosedDeals, Summary
    ///   gc_ReleaseDeal(DealId)   → Status, Lots, Offers, Summary          (frees what a cancelled deal held)
    /// Each call is one Dataverse transaction: any refusal throws, and nothing is saved.
    /// The caller must be able to read the offer or deal; writes run as SYSTEM so competing deals of other parties can be closed.
    /// </summary>
    public sealed class OperationsPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var dv = new Dv(factory.CreateOrganizationService(context.UserId), factory.CreateOrganizationService(null));
            var actor = "user:" + context.InitiatingUserId;
            switch (context.MessageName)
            {
                case "gc_AcceptOffer":
                    new AcceptOffer(dv, actor).Run(RequireId(context, "OfferId"), context.OutputParameters);
                    break;
                case "gc_ReleaseDeal":
                    ReleaseDeal(dv, actor, RequireId(context, "DealId"), context.OutputParameters);
                    break;
                default:
                    throw new InvalidPluginExecutionException("OperationsPlugin does not handle " + context.MessageName + ".");
            }
        }

        /// <summary>For a cancelled deal: frees the lots reserved for it and rejects its open offers.</summary>
        private static void ReleaseDeal(Dv dv, string actor, Guid dealId, ParameterCollection o)
        {
            var deal = dv.Retrieve("gc_deal", dealId, "gc_stage");
            if (deal == null) Refuse("Deal " + dealId + " was not found or you cannot read it.");
            if (Opt(deal, "gc_stage") != Ops.Stage.Cancelled) Refuse("Only a Cancelled deal releases its lot and offers (this one is " + Dv.Label(deal, "gc_stage") + ").");
            var lots = 0;
            foreach (var lot in dv.Query("gc_lot", new[] { "gc_status" }, 50, "gc_reservedfor", ConditionOperator.Equal, dealId))
            {
                var free = new Entity("gc_lot", lot.Id);
                free["gc_reservedfor"] = null;
                free["gc_status"] = new OptionSetValue(Ops.Lot.Available);
                dv.System.Update(free);
                lots++;
            }
            var offers = 0;
            foreach (var offer in dv.Query("gc_offer", new[] { "gc_status" }, 200, "gc_deal", ConditionOperator.Equal, dealId)
                         .Where(x => Opt(x, "gc_status") == Ops.Offer.Open || Opt(x, "gc_status") == Ops.Offer.Countered))
            {
                var r = new Entity("gc_offer", offer.Id);
                r["gc_status"] = new OptionSetValue(Ops.Offer.Rejected);
                dv.System.Update(r);
                offers++;
            }
            Audit(dv, actor, "deal.released", "gc_deal", dealId, J.Obj("lots", lots, "offers", offers));
            o["Status"] = "Released";
            o["Lots"] = lots;
            o["Offers"] = offers;
            o["Summary"] = lots + " lot(s) made available again; " + offers + " open offer(s) rejected.";
        }

        private static Guid RequireId(IPluginExecutionContext context, string name)
        {
            object v;
            if (context.InputParameters.TryGetValue(name, out v) && v is Guid && (Guid)v != Guid.Empty) return (Guid)v;
            throw new InvalidPluginExecutionException(name + " is required.");
        }

        internal static void Refuse(string message)
        {
            throw new InvalidPluginExecutionException(OperationStatus.Failed, message);
        }

        internal static Guid? Ref(Entity e, string column)
        {
            var r = e.GetAttributeValue<EntityReference>(column);
            return r == null ? (Guid?)null : r.Id;
        }

        internal static int? Opt(Entity e, string column)
        {
            var o = e.GetAttributeValue<OptionSetValue>(column);
            return o == null ? (int?)null : o.Value;
        }

        /// <summary>Calls gc_TransitionDeal; it returns refusals rather than throwing.</summary>
        internal static string Transition(Dv dv, Guid dealId, int stage, string reason)
        {
            var req = new OrganizationRequest("gc_TransitionDeal");
            req["DealId"] = dealId;
            req["TargetStage"] = stage;
            req["Reason"] = reason;
            var resp = dv.System.Execute(req);
            var allowed = resp.Results.Contains("Allowed") && resp["Allowed"] is bool && (bool)resp["Allowed"];
            return allowed ? null : (resp.Results.Contains("Failures") ? resp["Failures"] as string : null) ?? "refused";
        }

        internal static void Audit(Dv dv, string actor, string action, string table, Guid id, Dictionary<string, object> details)
        {
            var a = new Entity("gc_auditevent");
            a["gc_name"] = action;
            a["gc_action"] = action;
            a["gc_actor"] = actor;
            a["gc_at"] = DateTime.UtcNow;
            a["gc_subjecttype"] = table;
            a["gc_subjectid"] = id.ToString();
            a["gc_details"] = GeminiClient.Truncate(Json.Serialize(details), 100000);
            dv.System.Create(a);
        }
    }

    /// <summary>
    /// Accepts an offer: copies its terms to the deal, reserves the lot (splitting it when only part is bought),
    /// rejects the deal's other open offers, closes competing deals (same lot, or same RFQ once it is covered)
    /// and moves the deal to Terms Agreed. Idempotent: calling it again for an accepted deal changes nothing.
    /// </summary>
    internal sealed class AcceptOffer
    {
        private readonly Dv _dv;
        private readonly string _actor;
        private readonly List<string> _notes = new List<string>();

        public AcceptOffer(Dv dv, string actor) { _dv = dv; _actor = actor; }

        public void Run(Guid offerId, ParameterCollection output)
        {
            var offer = _dv.Retrieve("gc_offer", offerId, "gc_name", "gc_status", "gc_deal", "gc_price", "gc_quantity", "gc_currency", "gc_incoterm",
                "gc_namedplace", "gc_paymentterms", "gc_quote", "gc_validuntil");
            if (offer == null) OperationsPlugin.Refuse("Offer " + offerId + " was not found or you cannot read it.");
            var dealId = OperationsPlugin.Ref(offer, "gc_deal");
            if (dealId == null) OperationsPlugin.Refuse("The offer is not linked to a deal.");
            var dealCols = new[] { "gc_name", "gc_stage", "gc_statusoverlay", "gc_lot", "gc_listing", "gc_requirement", "gc_buyer" };
            var deal = _dv.Retrieve("gc_deal", dealId.Value, dealCols);
            if (deal == null) OperationsPlugin.Refuse("Deal " + dealId + " was not found or you cannot read it.");

            var status = OperationsPlugin.Opt(offer, "gc_status");
            var stage = OperationsPlugin.Opt(deal, "gc_stage") ?? Ops.Stage.Inquiry;
            if (status == Ops.Offer.Accepted && stage != Ops.Stage.Inquiry && stage != Ops.Stage.Negotiation)
            {
                Output(output, "AlreadyAccepted", deal.Id, stage, 0, "The offer was already accepted; the deal is at " + (Dv.Label(deal, "gc_stage") ?? "a later stage") + ".");
                return;
            }
            if (status != Ops.Offer.Open && status != Ops.Offer.Countered && status != Ops.Offer.Accepted)
                OperationsPlugin.Refuse("Only an Open or Countered offer can be accepted (this one is " + Dv.Label(offer, "gc_status") + ").");
            if (stage != Ops.Stage.Inquiry && stage != Ops.Stage.Negotiation)
                OperationsPlugin.Refuse("The deal is at " + Dv.Label(deal, "gc_stage") + "; offers can only be accepted during Inquiry or Negotiation.");
            var overlay = OperationsPlugin.Opt(deal, "gc_statusoverlay");
            if (overlay != null && overlay != Ops.OverlayNone) OperationsPlugin.Refuse("The deal is " + Dv.Label(deal, "gc_statusoverlay") + "; clear the overlay first.");
            var validUntil = offer.GetAttributeValue<DateTime?>("gc_validuntil");
            if (validUntil != null && validUntil.Value < DateTime.UtcNow) OperationsPlugin.Refuse("The offer expired on " + validUntil.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
            var qty = offer.GetAttributeValue<decimal?>("gc_quantity");
            var price = offer.GetAttributeValue<decimal?>("gc_price");
            if (qty == null || qty <= 0 || price == null || price <= 0) OperationsPlugin.Refuse("The offer needs a positive price and quantity.");

            // The engine quote is a guard of Terms Agreed; price the offer now if the pricing flow has not.
            var quote = OperationsPlugin.Ref(offer, "gc_quote");
            if (quote == null)
            {
                var req = new OrganizationRequest("gc_CalculatePriceQuote");
                req["OfferId"] = offerId;
                _dv.System.Execute(req);
                quote = OperationsPlugin.Ref(_dv.Retrieve("gc_offer", offerId, "gc_quote"), "gc_quote");
                if (quote == null) OperationsPlugin.Refuse("The offer could not be priced, so it cannot be accepted.");
            }

            // Writing the deal first takes its row lock, so a concurrent accept on the same deal waits and then sees the new stage.
            var terms = new Entity("gc_deal", deal.Id);
            terms["gc_price"] = price;
            terms["gc_quantity"] = qty;
            terms["gc_acceptedquote"] = new EntityReference("gc_pricequote", quote.Value);
            foreach (var c in new[] { "gc_currency", "gc_namedplace" })
                if (offer.Contains(c)) terms[c] = offer[c];
            foreach (var c in new[] { "gc_incoterm", "gc_paymentterms" })
                if (offer.GetAttributeValue<OptionSetValue>(c) != null) terms[c] = offer[c];
            _dv.System.Update(terms);
            deal = _dv.Retrieve("gc_deal", deal.Id, dealCols);
            stage = OperationsPlugin.Opt(deal, "gc_stage") ?? Ops.Stage.Inquiry;
            if (stage != Ops.Stage.Inquiry && stage != Ops.Stage.Negotiation)
                OperationsPlugin.Refuse("Another offer on this deal was accepted at the same time.");

            var closed = ReserveLot(deal, qty.Value);

            if (status != Ops.Offer.Accepted)  // when staff set it to Accepted, rewriting it would re-trigger the Offer accepted flow
            {
                var acceptedOffer = new Entity("gc_offer", offerId);
                acceptedOffer["gc_status"] = new OptionSetValue(Ops.Offer.Accepted);
                _dv.System.Update(acceptedOffer);
            }
            foreach (var other in _dv.Query("gc_offer", new[] { "gc_status" }, 200, "gc_deal", ConditionOperator.Equal, deal.Id)
                         .Where(o => o.Id != offerId && (OperationsPlugin.Opt(o, "gc_status") == Ops.Offer.Open || OperationsPlugin.Opt(o, "gc_status") == Ops.Offer.Countered)))
            {
                var r = new Entity("gc_offer", other.Id);
                r["gc_status"] = new OptionSetValue(Ops.Offer.Rejected);
                _dv.System.Update(r);
            }

            closed += CloseRfqCompetitors(deal, qty.Value);

            if (stage == Ops.Stage.Inquiry)
            {
                var f = OperationsPlugin.Transition(_dv, deal.Id, Ops.Stage.Negotiation, "Offer accepted during inquiry");
                if (f != null) OperationsPlugin.Refuse("The deal cannot move to Negotiation: " + f);
            }
            var failures = OperationsPlugin.Transition(_dv, deal.Id, Ops.Stage.TermsAgreed, "Offer accepted: " + offer.GetAttributeValue<string>("gc_name"));
            if (failures != null) OperationsPlugin.Refuse("The deal cannot move to Terms Agreed: " + failures);

            var summary = "Offer accepted; deal moved to Terms Agreed." + (_notes.Count > 0 ? " " + string.Join(" ", _notes) : "");
            OperationsPlugin.Audit(_dv, _actor, "offer.accepted", "gc_offer", offerId,
                J.Obj("deal", deal.Id.ToString(), "price", price, "quantity", qty, "quote", quote.ToString(), "closed_deals", closed, "notes", _notes));
            Output(output, "Accepted", deal.Id, Ops.Stage.TermsAgreed, closed, summary);
        }

        /// <summary>Reserves the deal's lot (or the best available lot of its listing). Returns the number of competing deals cancelled.</summary>
        private int ReserveLot(Entity deal, decimal qty)
        {
            var lotId = OperationsPlugin.Ref(deal, "gc_lot");
            var listingId = OperationsPlugin.Ref(deal, "gc_listing");
            if (lotId == null && listingId != null)
            {
                var lots = _dv.Query("gc_lot", new[] { "gc_quantity" }, 50, "gc_listing", ConditionOperator.Equal, listingId.Value,
                    "gc_status", ConditionOperator.Equal, Ops.Lot.Available);
                var pick = lots.Where(l => (l.GetAttributeValue<decimal?>("gc_quantity") ?? 0) >= qty).OrderBy(l => l.GetAttributeValue<decimal?>("gc_quantity")).FirstOrDefault()
                           ?? lots.OrderByDescending(l => l.GetAttributeValue<decimal?>("gc_quantity") ?? 0).FirstOrDefault();
                if (pick != null) lotId = pick.Id;
            }
            if (lotId == null)
            {
                _notes.Add("No lot is recorded for this listing, so nothing was reserved.");
                return 0;
            }

            // Touch the lot first to take its row lock; a concurrent accept on another deal then waits and sees this reservation.
            var lotCols = new[] { "gc_name", "gc_status", "gc_quantity", "gc_reservedfor", "gc_listing", "gc_location", "gc_lotnumber" };
            var before = _dv.Retrieve("gc_lot", lotId.Value, lotCols);
            if (before == null) OperationsPlugin.Refuse("The deal's lot was not found.");
            var touch = new Entity("gc_lot", lotId.Value);
            touch["gc_status"] = before["gc_status"];
            _dv.System.Update(touch);
            var lot = _dv.Retrieve("gc_lot", lotId.Value, lotCols);
            var reservedFor = OperationsPlugin.Ref(lot, "gc_reservedfor");
            var lotStatus = OperationsPlugin.Opt(lot, "gc_status");
            if (reservedFor != null && reservedFor != deal.Id) OperationsPlugin.Refuse("The lot is already reserved for another deal.");
            if (lotStatus != Ops.Lot.Available && reservedFor != deal.Id) OperationsPlugin.Refuse("The lot is " + Dv.Label(lot, "gc_status") + ", not Available.");

            var lotQty = lot.GetAttributeValue<decimal?>("gc_quantity") ?? 0;
            if (lotQty > 0 && qty > lotQty) OperationsPlugin.Refuse("The offer quantity (" + Qty(qty) + ") exceeds the lot quantity (" + Qty(lotQty) + ").");

            Guid? remainder = null;
            var reserve = new Entity("gc_lot", lot.Id);
            reserve["gc_status"] = new OptionSetValue(Ops.Lot.Reserved);
            reserve["gc_reservedfor"] = new EntityReference("gc_deal", deal.Id);
            if (lotQty > qty)
            {
                reserve["gc_quantity"] = qty;
                var rest = new Entity("gc_lot");
                rest["gc_name"] = GeminiClient.Truncate((lot.GetAttributeValue<string>("gc_name") ?? "Lot") + " (remainder)", 100);
                rest["gc_quantity"] = lotQty - qty;
                rest["gc_status"] = new OptionSetValue(Ops.Lot.Available);
                foreach (var c in new[] { "gc_listing", "gc_location" })
                    if (lot.Contains(c)) rest[c] = lot[c];
                if (lot.Contains("gc_lotnumber")) rest["gc_lotnumber"] = GeminiClient.Truncate(lot.GetAttributeValue<string>("gc_lotnumber") + "-R", 100);
                remainder = _dv.System.Create(rest);
                _notes.Add("Lot split: " + Qty(qty) + " reserved, " + Qty(lotQty - qty) + " left available.");
            }
            _dv.System.Update(reserve);
            if (OperationsPlugin.Ref(deal, "gc_lot") != lot.Id)
            {
                var link = new Entity("gc_deal", deal.Id);
                link["gc_lot"] = new EntityReference("gc_lot", lot.Id);
                _dv.System.Update(link);
            }

            var closed = 0;
            foreach (var other in OpenDeals("gc_lot", lot.Id, deal.Id))
            {
                if (remainder != null)
                {
                    var move = new Entity("gc_deal", other.Id);
                    move["gc_lot"] = new EntityReference("gc_lot", remainder.Value);
                    _dv.System.Update(move);
                }
                else if (Cancel(other, "Lot sold to another buyer")) closed++;
            }
            return closed;
        }

        /// <summary>When the buyer's RFQ is covered by accepted deals, cancels the buyer's other open deals for it.</summary>
        private int CloseRfqCompetitors(Entity deal, decimal qty)
        {
            var reqId = OperationsPlugin.Ref(deal, "gc_requirement");
            if (reqId == null) return 0;
            var req = _dv.Retrieve("gc_buyerrequirement", reqId.Value, "gc_quantity", "gc_status");
            if (req == null) return 0;
            var wanted = req.GetAttributeValue<decimal?>("gc_quantity") ?? 0;
            var accepted = qty + _dv.Query("gc_deal", new[] { "gc_quantity", "gc_stage" }, 200, "gc_requirement", ConditionOperator.Equal, reqId.Value)
                .Where(d => d.Id != deal.Id && OperationsPlugin.Opt(d, "gc_stage") > Ops.Stage.Negotiation && OperationsPlugin.Opt(d, "gc_stage") != Ops.Stage.Cancelled)
                .Sum(d => d.GetAttributeValue<decimal?>("gc_quantity") ?? 0);
            if (wanted > 0 && accepted < wanted)
            {
                _notes.Add("RFQ partly covered (" + Qty(accepted) + " of " + Qty(wanted) + "); other sellers' deals stay open.");
                return 0;
            }
            var closed = 0;
            foreach (var other in OpenDeals("gc_requirement", reqId.Value, deal.Id))
                if (Cancel(other, "Buyer accepted another offer for this RFQ"))
                {
                    closed++;
                    if (Mail.Desk.RegretNote(new Mail.DeskWriter(_dv), other.Id) != null)
                        _notes.Add("'Not this time' note drafted to the seller of '" + other.GetAttributeValue<string>("gc_name") + "'.");
                }
            var done = new Entity("gc_buyerrequirement", reqId.Value);
            done["gc_status"] = new OptionSetValue(Ops.Requirement.Fulfilled);
            _dv.System.Update(done);
            return closed;
        }

        private List<Entity> OpenDeals(string column, Guid value, Guid except)
        {
            return _dv.Query("gc_deal", new[] { "gc_name", "gc_stage", "gc_statusoverlay" }, 500, column, ConditionOperator.Equal, value)
                .Where(d => d.Id != except && (OperationsPlugin.Opt(d, "gc_stage") ?? Ops.Stage.Inquiry) <= Ops.Stage.Negotiation).ToList();
        }

        private bool Cancel(Entity other, string reason)
        {
            var overlay = OperationsPlugin.Opt(other, "gc_statusoverlay");
            if (overlay != null && overlay != Ops.OverlayNone)
            {
                _notes.Add("Deal '" + other.GetAttributeValue<string>("gc_name") + "' is " + Dv.Label(other, "gc_statusoverlay") + " and was left for a human to close.");
                return false;
            }
            var f = OperationsPlugin.Transition(_dv, other.Id, Ops.Stage.Cancelled, reason);
            if (f != null) _notes.Add("Deal '" + other.GetAttributeValue<string>("gc_name") + "' could not be cancelled: " + f);
            return f == null;
        }

        private static string Qty(decimal q) { return q.ToString("0.####", CultureInfo.InvariantCulture); }

        private static void Output(ParameterCollection o, string status, Guid deal, int stage, int closed, string summary)
        {
            o["Status"] = status;
            o["DealId"] = deal.ToString();
            o["Stage"] = stage;
            o["ClosedDeals"] = closed;
            o["Summary"] = summary;
        }
    }

}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>
    /// Tracking updates after signing (EMAIL_DESK.md 7): when an inspection or a shipment of an Email Desk deal changes status, short
    /// updates are drafted to the buyer and the seller in their own threads (a person sends them, or replies SEND to the briefing).
    /// Back to back: the buyer never sees the seller's name, warehouse or documents; the seller never sees the buyer.
    /// Each status is drafted once per side (gc_deskupdates on the inspection or shipment).
    /// </summary>
    public static class Tracking
    {
        public static readonly string[] ShipmentStatuses = { "Planned", "Booked", "Loading", "In Transit", "Arrived", "Cleared", "Delivered", "Cancelled" };

        public static Dictionary<string, object> Run(DeskWriter w, string kind, Guid recordId, string skipSide = null)
        {
            var dv = w.Dv;
            if (!(dv.Setting("desk.tracking.enabled") ?? "true").Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return J.Obj("status", "Off");
            var table = kind == "inspection" ? "gc_inspection" : kind == "shipment" ? "gc_shipment" : null;
            if (table == null) throw new InvalidPluginExecutionException("Kind must be inspection or shipment.");
            var rec = dv.Retrieve(table, recordId);
            if (rec == null) return J.Obj("status", "NotFound");
            var dealId = Desk.H(rec, "gc_deal");
            var deal = dealId == null ? null : dv.Retrieve("gc_deal", dealId.Value, "gc_name", "gc_emaildesk", "gc_requirement", "gc_sellerlot", "gc_seller");
            if (deal == null || !(deal.GetAttributeValue<bool?>("gc_emaildesk") ?? false)) return J.Obj("status", "NotEmailDesk");
            var status = Dv.Label(rec, "gc_status");
            var texts = kind == "inspection" ? Inspection(dv, rec, status) : Shipment(rec, status);
            if (texts == null) return J.Obj("status", "NothingToSay", "record_status", status);

            var done = Done(rec);
            var threads = Threads(dv, deal);
            var drafts = new List<Guid>();
            var sides = new List<object>();
            foreach (var side in new[] { "buyer", "seller" })
            {
                string text;
                if (!texts.TryGetValue(side, out text) || text == null) continue;
                var key = status + ":" + side;
                if (done.Contains(key)) continue;
                done.Add(key);
                if (side == skipSide) continue;  // the side that told us gets its answer from the Trade Desk agent
                var thread = side == "buyer" ? threads.Key : threads.Value;
                if (thread == null) continue;
                var to = Desk.ReplyAddress(dv, thread.Value);
                if (string.IsNullOrWhiteSpace(to)) continue;
                var body = Desk.Greet("Dear Sir,", Desk.ContactName(dv, thread.Value)) + "\n\n" + text;
                drafts.Add(Desk.Draft(w, thread.Value, to, Desk.ReplySubject(dv, thread.Value), body, null, "draft_tracking_" + kind, Desk.AutoSend(dv, "reply", body)));
                sides.Add(side);
            }
            var u = new Entity(table, recordId);
            u["gc_deskupdates"] = Json.Serialize(done.Cast<object>().ToList());
            w.Update(u, "tracking_updates_marked");
            if (drafts.Count > 0)
                Desk.BriefWithDrafts(w, Cap(kind) + " " + (status ?? "").ToLowerInvariant() + ": " + deal.GetAttributeValue<string>("gc_name"),
                                     "Update drafted to the " + string.Join(" and the ", sides) + " (masked, in their own threads).", drafts);
            return J.Obj("status", drafts.Count > 0 ? "Drafted" : "Nothing new", "record_status", status, "drafted_to", sides);
        }

        /// <summary>What each side is told for an inspection status (null = nothing to tell).</summary>
        public static Dictionary<string, string> Inspection(Dv dv, Entity insp, string status)
        {
            var when = insp.GetAttributeValue<DateTime?>("gc_scheduledon");
            var agencyId = Desk.H(insp, "gc_agency");
            var agency = agencyId == null ? null : dv.Retrieve("account", agencyId.Value, "name");
            var by = agency == null ? "an independent agency" : agency.GetAttributeValue<string>("name");
            var date = when == null ? "the agreed date" : when.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            var whId = Desk.H(insp, "gc_warehouse");
            var wh = whId == null ? null : dv.Retrieve("gc_warehouse", whId.Value, "gc_name");
            switch (status)
            {
                case "Booked":
                    return new Dictionary<string, string>
                    {
                        { "buyer", "Independent inspection of quality and weight at loading is booked for " + date + " with " + by + ". We will share the result once the report is in." },
                        { "seller", "Independent inspection is booked for " + date + " with " + by + (wh == null ? "" : " at " + wh.GetAttributeValue<string>("gc_name")) +
                                    ". Please have the cargo ready for sampling and weighing and send me the contact at site." }
                    };
                case "Passed":
                    return new Dictionary<string, string>
                    {
                        { "buyer", "Inspection is done and the cargo is within the contract specification. The certificate will follow with the shipping documents." },
                        { "seller", "Inspection passed. Please go ahead with loading as per contract and send the draft B/L and shipping documents for our check." }
                    };
                case "Failed":
                    var findings = Desk.Cut((insp.GetAttributeValue<string>("gc_findings") ?? "").Trim(), 300);
                    return new Dictionary<string, string>
                    {
                        { "seller", "The inspection report shows the cargo off specification" + (findings.Length == 0 ? "" : ":\n" + findings) + "\n\nLet me check on our side and revert." }
                    };
                default:
                    return null;
            }
        }

        /// <summary>What each side is told for a shipment status (null = nothing to tell).</summary>
        public static Dictionary<string, string> Shipment(Entity s, string status)
        {
            var from = s.GetAttributeValue<string>("gc_originport");
            var to = s.GetAttributeValue<string>("gc_destinationport");
            var etd = Date(s.GetAttributeValue<DateTime?>("gc_etd"));
            var eta = Date(s.GetAttributeValue<DateTime?>("gc_eta"));
            switch (status)
            {
                case "Loading":
                    return new Dictionary<string, string>
                    {
                        { "buyer", "Loading has started" + (from == null ? "" : " at " + from) + "." + (etd == null ? "" : " ETD " + etd + ".") + " I will send the sailing details." }
                    };
                case "In Transit":
                    return new Dictionary<string, string>
                    {
                        { "buyer", "The cargo has sailed" + (from == null ? "" : " from " + from) + (etd == null ? "" : " on " + etd) + "." +
                                   (eta == null ? "" : " ETA " + (to ?? "discharge port") + ": " + eta + ".") + " Shipping documents will follow after our check." },
                        { "seller", "Noted the cargo has sailed, thanks. Please send the full set of shipping documents: B/L, commercial invoice, packing list, COA, certificate of origin and inspection certificate." }
                    };
                case "Arrived":
                    return new Dictionary<string, string>
                    {
                        { "buyer", "The vessel has arrived" + (to == null ? "" : " at " + to) + ". Please arrange clearance and let me know once the cargo is received." }
                    };
                case "Delivered":
                    return new Dictionary<string, string>
                    {
                        { "buyer", "Please confirm the cargo was received in good order and the quantity matches the documents." },
                        { "seller", "The cargo has been delivered to our buyer. Thanks for the smooth execution." }
                    };
                default:
                    return null;
            }
        }

        /// <summary>The buyer thread (requirement) and the seller thread (enquiry, or the lot's thread) of a desk deal.</summary>
        public static KeyValuePair<Guid?, Guid?> Threads(Dv dv, Entity deal)
        {
            Guid? buyer = null, seller = null;
            var reqId = Desk.H(deal, "gc_requirement");
            if (reqId != null)
            {
                var b = dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_requirement", ConditionOperator.Equal, reqId.Value,
                                 "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
                if (b != null) buyer = b.Id;
            }
            var s = dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_deal", ConditionOperator.Equal, deal.Id,
                             "gc_side", ConditionOperator.Equal, DeskChoice.Side.Seller).FirstOrDefault();
            if (s != null) seller = s.Id;
            else if (Desk.H(deal, "gc_sellerlot") != null)
                seller = Desk.H(dv.Retrieve("gc_sellerlot", Desk.H(deal, "gc_sellerlot").Value, "gc_sellerthread"), "gc_sellerthread");
            return new KeyValuePair<Guid?, Guid?>(buyer, seller);
        }

        private static HashSet<string> Done(Entity rec)
        {
            try
            {
                var raw = rec.GetAttributeValue<string>("gc_deskupdates");
                return new HashSet<string>(string.IsNullOrWhiteSpace(raw) ? new string[0] : ((Json.Parse(raw) as List<object>) ?? new List<object>()).OfType<string>());
            }
            catch (FormatException) { return new HashSet<string>(); }
        }

        private static string Date(DateTime? d) { return d == null ? null : d.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture); }

        private static string Cap(string s) { return char.ToUpperInvariant(s[0]) + s.Substring(1); }
    }
}

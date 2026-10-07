using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>
    /// Deals run over days: one polite chaser when a seller has not answered our enquiry (or the bids on their open-ended lot) or a
    /// buyer has not answered our offer (after desk.chase_after_hours, default 48), and a reminder to lot buyers who have not bid
    /// when offers close within 6 hours.
    /// Drafts only, like every desk email; no prices in chasers. Run by the Desk timers flow (gc_DeskFollowUps).
    /// </summary>
    public static class FollowUps
    {
        public static Dictionary<string, object> Run(DeskWriter w)
        {
            var dv = w.Dv;
            var now = DateTime.UtcNow;
            var cutoff = now.AddHours(-Math.Max(1, dv.SettingNum("desk.chase_after_hours", 48)));
            var sellers = 0;
            var buyers = 0;
            var reminders = 0;
            var offers = Desk.AdvanceQueues(w);

            // Sellers who have not answered our enquiry.
            var iq = new QueryExpression("gc_rfqinvite") { ColumnSet = new ColumnSet("gc_requirement"), TopCount = 200 };
            iq.Criteria.AddCondition("gc_status", ConditionOperator.Equal, DeskChoice.Invite.Invited);
            iq.Criteria.AddCondition("createdon", ConditionOperator.LessEqual, cutoff);
            foreach (var invite in dv.Svc.RetrieveMultiple(iq).Entities)
            {
                var conv = dv.Query("gc_conversation", new[] { "gc_chases", "gc_requirement" }, 1, "gc_invite", ConditionOperator.Equal, invite.Id).FirstOrDefault();
                if (conv == null || !Quiet(dv, conv, cutoff)) continue;
                var commodity = Commodity(dv, Desk.H(conv, "gc_requirement"));
                var text = Desk.Greet("Dear Sir,", Desk.ContactName(dv, conv.Id)) + "\n\nFollowing up on our enquiry below for " + commodity +
                           ". If you can supply, please send your best price with basis, quantity and lead time. If not, a one-line reply is fine.";
                if (Chase(w, conv, text, "draft_seller_chaser")) sellers++;
            }

            // Lot sellers who have not answered the bids we put to them (open-ended lots).
            var oq = new QueryExpression("gc_sellerlot") { ColumnSet = new ColumnSet("gc_commoditytext", "gc_sellerthread"), TopCount = 100 };
            oq.Criteria.AddCondition("gc_status", ConditionOperator.Equal, Lots.Status.Open);
            oq.Criteria.AddCondition("gc_biddeadline", ConditionOperator.Null);
            foreach (var lot in dv.Svc.RetrieveMultiple(oq).Entities)
            {
                var threadId = Desk.H(lot, "gc_sellerthread");
                if (threadId == null || Lots.OpenBids(dv, lot.Id).Count == 0) continue;
                var conv = dv.Retrieve("gc_conversation", threadId.Value, "gc_chases");
                if (conv == null || !Quiet(dv, conv, cutoff)) continue;
                var text = Desk.Greet("Dear Sir,", Desk.ContactName(dv, conv.Id)) + "\n\nFollowing up on the bid below for your " + lot.GetAttributeValue<string>("gc_commoditytext") +
                           ". Please let me know if you accept, or your best price, so I can revert to the buyer.";
                if (Chase(w, conv, text, "draft_lot_seller_chaser")) sellers++;
            }

            // Buyers who have not answered our offer (lot offers get the deadline reminder instead).
            var rq = new QueryExpression("gc_buyerrequirement") { ColumnSet = new ColumnSet("gc_commoditytext", "gc_name"), TopCount = 200 };
            rq.Criteria.AddCondition("gc_source", ConditionOperator.Equal, DeskChoice.RequirementSource.Email);
            rq.Criteria.AddCondition("gc_deskstage", ConditionOperator.Equal, DeskChoice.Stage.Quoted);
            foreach (var req in dv.Svc.RetrieveMultiple(rq).Entities)
            {
                if (dv.Query("gc_deal", new[] { "gc_sellerlot" }, 50, "gc_requirement", ConditionOperator.Equal, req.Id).Any(d => Desk.H(d, "gc_sellerlot") != null)) continue;
                var conv = dv.Query("gc_conversation", new[] { "gc_chases", "gc_requirement" }, 1, "gc_requirement", ConditionOperator.Equal, req.Id,
                                    "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
                if (conv == null || !Quiet(dv, conv, cutoff)) continue;
                var text = Desk.Greet("Dear Sir,", Desk.ContactName(dv, conv.Id)) + "\n\nFollowing up on our offer below for " +
                           (req.GetAttributeValue<string>("gc_commoditytext") ?? "your requirement") + ". Please let me know if it works for you, or your best price.";
                if (Chase(w, conv, text, "draft_buyer_chaser")) buyers++;
            }

            // Lot buyers who have not bid, when offers close within 6 hours.
            var lq = new QueryExpression("gc_sellerlot") { ColumnSet = new ColumnSet("gc_commoditytext", "gc_biddeadline", "gc_remindedon"), TopCount = 50 };
            lq.Criteria.AddCondition("gc_status", ConditionOperator.Equal, Lots.Status.Open);
            lq.Criteria.AddCondition("gc_biddeadline", ConditionOperator.Between, now, now.AddHours(6));
            lq.Criteria.AddCondition("gc_remindedon", ConditionOperator.Null);
            foreach (var lot in dv.Svc.RetrieveMultiple(lq).Entities)
            {
                var deadline = lot.GetAttributeValue<DateTime>("gc_biddeadline");
                foreach (var deal in dv.Query("gc_deal", new[] { "gc_buyerprice", "gc_requirement", "gc_stage" }, 100, "gc_sellerlot", ConditionOperator.Equal, lot.Id))
                {
                    if ((deal.GetAttributeValue<decimal?>("gc_buyerprice") ?? 0) > 0) continue;
                    if ((deal.GetAttributeValue<OptionSetValue>("gc_stage") ?? new OptionSetValue(DeskChoice.DealStage.Inquiry)).Value > DeskChoice.DealStage.Negotiation) continue;
                    var reqId = Desk.H(deal, "gc_requirement");
                    var conv = reqId == null ? null : dv.Query("gc_conversation", new[] { "gc_chases" }, 1, "gc_requirement", ConditionOperator.Equal, reqId.Value,
                                                               "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
                    if (conv == null || HasPendingDraft(dv, conv.Id)) continue;
                    var to = Desk.ReplyAddress(dv, conv.Id) ?? LastRecipient(dv, conv.Id);
                    if (string.IsNullOrWhiteSpace(to)) continue;
                    var text = Desk.Greet("Dear Sir,", Desk.ContactName(dv, conv.Id)) + "\n\nQuick reminder: offers for " + lot.GetAttributeValue<string>("gc_commoditytext") +
                               " close on " + Lots.When(deadline) + ". If you are interested, please send your confirmation or price before then.";
                    Desk.Draft(w, conv.Id, to, Desk.ReplySubject(dv, conv.Id), text, null, "draft_lot_reminder", Desk.AutoSend(dv, "reply", text));
                    reminders++;
                }
                var u = new Entity("gc_sellerlot", lot.Id);
                u["gc_remindedon"] = now;
                w.Update(u, "lot_reminded");
            }

            if (sellers + buyers + reminders + offers > 0)
                Desk.Brief(w, "Follow-ups drafted", "Buyers moved to the next queued seller (previous deal off or seller silent after a reminder): " + offers + "\nChasers to sellers who have not answered our enquiry or our bids: " + sellers + "\nChasers to buyers who have not answered our offer: " + buyers +
                           "\nReminders to lot buyers before offers close: " + reminders + "\n\nThey are in Gmail > Drafts.");
            // Decisions still waiting for a person get one reminder briefing (desk.approval_remind_hours after the first).
            var approvalReminders = Approvals.Remind(w, now);
            return J.Obj("next_seller", offers, "seller_chasers", sellers, "buyer_chasers", buyers, "lot_reminders", reminders, "approval_reminders", approvalReminders.Count);
        }

        /// <summary>The thread's last email is ours, older than the cutoff, unanswered, no draft waiting and not chased yet.</summary>
        private static bool Quiet(Dv dv, Entity conv, DateTime cutoff)
        {
            if ((conv.GetAttributeValue<int?>("gc_chases") ?? 0) >= 1) return false;
            var messages = Messages(dv, conv.Id);
            if (messages.Any(m => Dir(m) == MailChoice.Direction.Draft && Status(m) == DeskChoice.DraftStatus.Pending)) return false;
            var last = messages.Where(m => Dir(m) != MailChoice.Direction.Draft).OrderByDescending(Time).FirstOrDefault();
            return last != null && Dir(last) == MailChoice.Direction.Outbound && Time(last) <= cutoff;
        }

        private static bool Chase(DeskWriter w, Entity conv, string text, string action)
        {
            var dv = w.Dv;
            var to = Desk.ReplyAddress(dv, conv.Id) ?? LastRecipient(dv, conv.Id);
            if (string.IsNullOrWhiteSpace(to)) return false;
            Desk.Draft(w, conv.Id, to, Desk.ReplySubject(dv, conv.Id), text, null, action, Desk.AutoSend(dv, "reply", text));
            var c = new Entity("gc_conversation", conv.Id);
            c["gc_chases"] = (conv.GetAttributeValue<int?>("gc_chases") ?? 0) + 1;
            c["gc_chasedon"] = DateTime.UtcNow;
            w.Update(c, "thread_chased");
            return true;
        }

        private static bool HasPendingDraft(Dv dv, Guid convId)
        {
            return dv.Query("gc_message", new[] { "gc_messageid" }, 1, "gc_conversation", ConditionOperator.Equal, convId,
                            "gc_draftstatus", ConditionOperator.Equal, DeskChoice.DraftStatus.Pending).Any();
        }

        /// <summary>Who our last email in the thread went to (a thread where they never wrote back has no Reply-To to use).</summary>
        private static string LastRecipient(Dv dv, Guid convId)
        {
            var sent = Messages(dv, convId).Where(m => Dir(m) == MailChoice.Direction.Draft && Status(m) == DeskChoice.DraftStatus.Sent).OrderByDescending(Time).FirstOrDefault();
            return sent == null ? null : sent.GetAttributeValue<string>("gc_toaddress");
        }

        private static List<Entity> Messages(Dv dv, Guid convId)
        {
            return dv.Query("gc_message", new[] { "gc_direction", "gc_draftstatus", "gc_senton", "createdon", "gc_toaddress" }, 50, "gc_conversation", ConditionOperator.Equal, convId);
        }

        private static string Commodity(Dv dv, Guid? reqId)
        {
            var req = reqId == null ? null : dv.Retrieve("gc_buyerrequirement", reqId.Value, "gc_commoditytext", "gc_name");
            return req == null ? "the material" : (req.GetAttributeValue<string>("gc_commoditytext") ?? req.GetAttributeValue<string>("gc_name") ?? "the material");
        }

        private static int Dir(Entity m) { var v = m.GetAttributeValue<OptionSetValue>("gc_direction"); return v == null ? -1 : v.Value; }
        private static int Status(Entity m) { var v = m.GetAttributeValue<OptionSetValue>("gc_draftstatus"); return v == null ? -1 : v.Value; }
        private static DateTime Time(Entity m) { return m.GetAttributeValue<DateTime?>("gc_senton") ?? m.GetAttributeValue<DateTime>("createdon"); }
    }
}

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
    /// <summary>
    /// Front-door chat agent over one gc_conversation. Runs on each inbound gc_message (ChatPlugin) or through its
    /// Custom API with Input {"message_id": "..."}. The reply is validated (grounded numbers, no counterparty identity,
    /// evidence wording) before it is accepted, then stored as a platform gc_message in the same conversation.
    /// </summary>
    public abstract class ChatAgent : AgentDefinition
    {
        public override string SubjectTable { get { return "gc_conversation"; } }
        public override int MaxSteps { get { return 6; } }

        /// <summary>True for the buyer side: replies are held to the market-message rules (no contacts, hedged claims).</summary>
        protected abstract bool BuyerSide { get; }

        protected const string HowDealOsWorks =
@"How DealOS works (use when asked): every value on a listing has an evidence status: Verified (independently confirmed), Documented (supported by a document the seller uploaded) or Claimed (only stated by the seller). The listing badge is Verified, Documented or None. Both parties pass company checks (KYB) and screening. After an offer is accepted and the contract is signed, the buyer funds escrow at a licensed partner (DealOS never holds the money), an independent agency inspects and seals the goods before shipment, and funds are released to the seller in stages as milestones are verified. The DealOS commission is deducted at release. Identities are shared after the contract is signed.";

        protected const string CommonRules =
@"Conversation rules:
- Reply to LATEST MESSAGE; HISTORY is earlier context. Greet only in the first reply.
- Call tools for any account or catalog data instead of guessing. Use only numbers that appear in CONTEXT or tool results.
- You cannot accept or send offers, send RFQs, submit or publish listings, negotiate on anyone's behalf, promise delivery, or move money. Anything you create is a DRAFT the user finishes in the portal; say so.
- Ask at most one clarifying question at a time.
- If the user asks for something outside the marketplace, wants a person, or you cannot help, say a DealOS team member will follow up and set needs_human = true.
- Plain text, under 120 words unless you list several items; no tables. Put up to 3 short follow-ups in next_steps.";

        public override Dictionary<string, object> OutputSchema
        {
            get
            {
                return Output("Chat reply",
                    "reply", S.Str("The reply to the user: plain text, ready to show."),
                    "next_steps", S.Arr("Up to 3 short follow-ups the user can take, e.g. 'Open My RFQs to send the draft'.", S.Str("step")),
                    "topic", S.Enum("Main topic of the message.", "Catalog", "RFQ", "Listing", "Verification", "Deal", "Onboarding", "Platform", "Other"));
            }
        }

        public override void LinkRun(Entity run, AgentContext ctx)
        {
            if (ctx.SubjectId == null) return;
            var conv = ctx.Dv.Retrieve("gc_conversation", ctx.SubjectId.Value, "gc_counterparty");
            var account = conv == null ? null : conv.GetAttributeValue<EntityReference>("gc_counterparty");
            if (account != null) run["gc_account"] = new EntityReference("account", account.Id);
        }

        public override Dictionary<string, object> BuildContext(AgentContext ctx)
        {
            var conv = H.Require(ctx);
            var account = H.Ref(conv, "gc_counterparty");
            if (account == null) throw new ToolRefusal("The conversation has no company account (gc_counterparty).");
            ctx.Scratch["account_id"] = account.Value;

            var history = ctx.Dv.Query("gc_message", new[] { "gc_text", "gc_isplatform", "gc_senton", "createdon" },
                Math.Max(2, Math.Min(40, ctx.Dv.SettingInt("chat.history_messages", 12))), "gc_conversation", ConditionOperator.Equal, conv.Id);
            history.Reverse(); // oldest first

            Entity latest = null;
            var messageId = J.Id(ctx.Input, "message_id");
            if (messageId != null) latest = history.FirstOrDefault(m => m.Id == messageId.Value) ?? ctx.Dv.Query("gc_message", new[] { "gc_text", "gc_conversation", "gc_isplatform" }, 1,
                "gc_messageid", ConditionOperator.Equal, messageId.Value, "gc_conversation", ConditionOperator.Equal, conv.Id).FirstOrDefault();
            if (latest == null) latest = history.LastOrDefault(m => !(m.GetAttributeValue<bool?>("gc_isplatform") ?? false));
            if (latest == null) throw new ToolRefusal("The conversation has no message from the user to answer.");
            var text = latest.GetAttributeValue<string>("gc_text") ?? "";
            ctx.Scratch["message_id"] = latest.Id;
            ctx.Scratch["message_text"] = text;

            var userNumbers = new HashSet<string>();
            foreach (var m in history.Where(m => !(m.GetAttributeValue<bool?>("gc_isplatform") ?? false))) MessageValidator.CollectNumbers(m.GetAttributeValue<string>("gc_text"), userNumbers);
            MessageValidator.CollectNumbers(text, userNumbers);
            ctx.Scratch["user_numbers"] = userNumbers;

            var contact = H.TryGet(ctx, "contact", H.Ref(conv, "gc_contact"), "firstname");
            return J.Obj(
                "today", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "you_are", DisplayName,
                "user", J.Obj("first_name", contact == null ? null : contact.GetAttributeValue<string>("firstname"), "company", H.Party(ctx, account, true)),
                "channel", Dv.Label(conv, "gc_channel") ?? "Portal",
                "history", history.Where(m => m.Id != latest.Id).Select(m => (object)J.Obj(
                    "from", (m.GetAttributeValue<bool?>("gc_isplatform") ?? false) ? "assistant" : "user",
                    "text", GeminiClient.Truncate(m.GetAttributeValue<string>("gc_text"), 1500))).ToList(),
                "latest_message", GeminiClient.Truncate(text, 4000));
        }

        public override string CheckFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            var reply = J.Str(result, "reply");
            object v;
            var forbidden = ctx.Scratch.TryGetValue("counterparty_terms", out v) ? (List<string>)v : new List<string>();
            var claimed = ctx.Scratch.TryGetValue("claimed_numbers", out v) ? (HashSet<string>)v : new HashSet<string>();
            var report = MessageValidator.Check(reply, ctx.KnownNumbers, forbidden, ctx.Scratch.ContainsKey("has_verified"), claimed, BuyerSide);
            return report.Ok ? null : "Reply rejected: " + string.Join(" ", report.Errors) + " Rewrite the reply.";
        }

        public override void AfterFinish(AgentContext ctx, Dictionary<string, object> result)
        {
            var reply = J.Str(result, "reply");
            var steps = J.Arr(result, "next_steps").Select(s => s.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList();
            var msg = new Entity("gc_message");
            msg["gc_name"] = GeminiClient.Truncate(DisplayName + ": " + reply, 100);
            msg["gc_conversation"] = new EntityReference("gc_conversation", ctx.SubjectId.Value);
            msg["gc_isplatform"] = true;
            msg["gc_senderlabel"] = DisplayName;
            msg["gc_text"] = reply;
            msg["gc_senton"] = DateTime.UtcNow;
            msg["gc_attachments"] = Json.Serialize(J.Obj("kind", "suggestions", "next_steps", steps.Cast<object>().ToList(),
                "needs_human", J.Bool(result, "needs_human"), "agent_run", ctx.RunId.ToString()));
            var id = ctx.Create(msg, "reply", J.Obj("text", reply));
            result["reply_message_id"] = ctx.DryRun ? null : id.ToString();
        }
    }

    public sealed class BuyerConciergeAgent : ChatAgent
    {
        public override string Name { get { return "BuyerConcierge"; } }
        public override string DisplayName { get { return "Buyer Concierge"; } }
        public override string Description { get { return "Chat agent for buyers: searches the masked catalog, explains evidence, shows the buyer's RFQs, deals and offers, and drafts RFQs for the buyer to send."; } }
        public override int AgentChoice { get { return Choice.Base + 12; } }
        protected override bool BuyerSide { get { return true; } }
        public override string[] Tools { get { return new[] { "search_catalog", "get_listing", "my_buying", "draft_rfq" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the DealOS Buyer Concierge, chatting on the DealOS website with a buyer (CONTEXT.user.company) of rare earths, critical minerals and metals.
You help the buyer to: find material (search_catalog, then get_listing for detail), understand evidence and badges, check their own RFQs, matches, deals and offers (my_buying), and put a request into an RFQ (draft_rfq).
Evidence wording, strictly: call something verified only when its status is Verified; for Documented say 'per the seller's documents'; for Claimed say 'the seller states'. Never call a listing verified unless its badge is Verified.
Listings are anonymous: never name or guess the seller, mine, asset, exact location or contact details.
draft_rfq only when the buyer asks for an RFQ (or agrees to one) and has given commodity, quantity and unit; never invent a target price. Then tell them it is a draft to review and send from 'My RFQs'.
Prices: show listing ask prices and the landed-cost figures from my_buying as they are; never estimate freight, duty or a total yourself.
Incoterms: EXW/FCA/FAS/FOB name the seller's place or loading port; CFR/CIF/CPT/CIP/DAP/DPU/DDP name the destination. Never attach the buyer's destination to FOB (say 'FOB, delivery to Hamburg', not 'FOB Hamburg').
" + CommonRules + "\n" + HowDealOsWorks;
            }
        }
    }

    public sealed class SellerAssistantAgent : ChatAgent
    {
        public override string Name { get { return "SellerAssistant"; } }
        public override string DisplayName { get { return "Seller Assistant"; } }
        public override string Description { get { return "Chat agent for sellers: shows listing status and evidence gaps, records the seller's answers to verification questions, drafts listings, and explains RFQ invites, matches, deals and payouts."; } }
        public override int AgentChoice { get { return Choice.Base + 13; } }
        protected override bool BuyerSide { get { return false; } }
        public override string[] Tools { get { return new[] { "my_selling", "my_listing_detail", "draft_listing", "record_answer" }; } }

        public override string Instructions
        {
            get
            {
                return
@"You are the DealOS Seller Assistant, chatting on the DealOS website with a seller (CONTEXT.user.company) of rare earths, critical minerals and metals.
You help the seller to: see their listings, badges and what verification still needs (my_selling, my_listing_detail), answer the verification team's open questions (record_answer), start a new listing (draft_listing), and follow RFQ invites, matches, deals, offers and net payouts (my_selling).
When LATEST MESSAGE answers an open question, call my_listing_detail to get the question_id, then record_answer with the seller's exact words. Explain that a typed answer counts as Claimed and that uploading a supporting document (COA, assay, licence) on the listing page is what raises it to Documented; independent inspection makes it Verified.
draft_listing only when the seller asks to list material and has given commodity, quantity and unit; never invent a price. Then tell them to upload documents and submit it from 'My listings'.
Buyers are anonymous until the contract is signed: never name or guess a buyer.
Payouts: quote only the net-payout figures from my_selling; the commission is deducted at each escrow release.
" + CommonRules + "\n" + HowDealOsWorks;
            }
        }
    }
}

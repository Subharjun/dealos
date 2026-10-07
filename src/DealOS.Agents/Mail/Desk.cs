using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>Choice values of the Email Desk tables (order = tools/deploy_schema.py).</summary>
    public static class DeskChoice
    {
        private const int B = Choice.Base;
        public static class Side { public const int Buyer = B, Seller = B + 1; }
        public static class DraftStatus { public const int Pending = B, Sent = B + 1, Discarded = B + 2; }
        public static class Stage { public const int Qualifying = B, Sourcing = B + 1, Quoted = B + 2, Negotiating = B + 3, Agreed = B + 4, ContractSent = B + 5, Signed = B + 6, Closed = B + 7; }
        public static class Requirement { public const int Draft = B, Open = B + 1; }
        public static class RequirementSource { public const int Email = B + 1; }
        public static class LeadRole { public const int Seller = B, Buyer = B + 1, Both = B + 2; }
        public static class LeadSource { public const int TradeData = B, IndiaMart = B + 1, Warehouse = B + 2, Email = B + 3, Manual = B + 4, WebSearch = B + 5; }
        public static class LeadStatus { public const int New = B, Contacted = B + 1, Responded = B + 2, Converted = B + 3, DoNotContact = B + 4; }
        public static class Invite { public const int Invited = B, Accepted = B + 2, Declined = B + 3; }
        public static class DealStage { public const int Inquiry = B, Negotiation = B + 1, TermsAgreed = B + 2, Settled = B + 10, Closed = B + 11, Cancelled = B + 12; }
        public static class Offer { public const int Open = B, Countered = B + 1, Accepted = B + 2, Rejected = B + 3; }
        public static class Contract { public const int SentForSignature = B + 2, Signed = B + 3; }
        public const int DocumentParsed = B + 1;
        public static readonly string[] Units = { "MT", "DMT", "WMT", "Kg", "Lb", "Troy Oz", "Short Ton", "Long Ton", "Flask", "Unit" };
        public static readonly string[] Incoterms = { "EXW", "FCA", "FAS", "FOB", "CFR", "CIF", "CPT", "CIP", "DAP", "DPU", "DDP" };
        public static readonly string[] PaymentTerms = { "LC Sight", "LC Usance", "SBLC", "TT Advance", "TT Against Docs", "CAD", "Escrow", "Other" };
        public static readonly string[] Stages = { "Qualifying", "Sourcing", "Quoted", "Negotiating", "Agreed", "Contract Sent", "Signed", "Closed" };
    }

    /// <summary>Writes through the agent context when there is one (dry-run aware and logged), otherwise directly.</summary>
    public sealed class DeskWriter
    {
        public readonly Dv Dv;
        private readonly AgentContext _ctx;

        public DeskWriter(Dv dv, AgentContext ctx = null) { Dv = dv; _ctx = ctx; }

        public bool DryRun { get { return _ctx != null && _ctx.DryRun; } }

        public Guid Create(Entity e, string action, object detail = null)
        {
            return _ctx != null ? _ctx.Create(e, action, detail) : Dv.Svc.Create(e);
        }

        public void Update(Entity e, string action, object detail = null)
        {
            if (_ctx != null) _ctx.Update(e, action, detail);
            else Dv.Svc.Update(e);
        }

        /// <summary>
        /// Threads the desk's own code drafted into during this agent run (e.g. the next supplier's offer). The agent's draft_email
        /// must not replace those drafts in the same run.
        /// </summary>
        public void Drafted(Guid threadId, string action)
        {
            if (_ctx == null || action == "draft_email") return;
            object v;
            var set = _ctx.Scratch.TryGetValue("desk_drafted_threads", out v) ? (HashSet<Guid>)v : new HashSet<Guid>();
            set.Add(threadId);
            _ctx.Scratch["desk_drafted_threads"] = set;
        }
    }

    /// <summary>
    /// The Email Desk's deterministic trade logic: prices (margin), drafts, sourcing from leads, contract documents.
    /// The desk works back to back: the buyer and each seller deal only with us, in separate threads.
    /// </summary>
    public static class Desk
    {
        private static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "the", "for", "with", "min", "max", "minimum", "maximum", "grade", "high", "quality", "purity", "pure", "india", "tons", "ton",
            "bags", "bag", "packing", "approx", "material", "materials", "product", "products", "supply", "need", "required", "requirement",
            "kg", "mt", "mts", "lumps", "powder", "form", "type", "spec", "specification", "from", "per", "best", "price"
        };

        // ---------------------------------------------------------------- money

        public static double Margin(Dv dv) { return Math.Max(0, dv.SettingNum("trade.margin_percent", 3)); }

        /// <summary>Our price to the buyer for a seller price: seller price plus our margin.</summary>
        public static decimal PriceToBuyer(decimal sellerPrice, double marginPct)
        {
            return Math.Round(sellerPrice * (1 + (decimal)marginPct / 100m), 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>Our bid to the seller for a price the buyer will pay: the buyer price less our margin (inverse of PriceToBuyer).</summary>
        public static decimal BidToSeller(decimal buyerPrice, double marginPct)
        {
            return Math.Floor(buyerPrice / (1 + (decimal)marginPct / 100m) * 100m) / 100m;
        }

        public static string Signature(Dv dv)
        {
            return (dv.Setting("email.signature") ?? "Best regards,\nTrade Desk\nGigacore Energy Pvt Ltd").Replace("\\n", "\n");
        }

        public static string CompanyName(Dv dv) { return dv.Setting("trade.company_name") ?? "Gigacore Energy Pvt Ltd"; }

        /// <summary>Our company profile PDF (setting desk.company_profile = its gc_document id, set by tools/company_profile.py); null when none.</summary>
        public static Guid? CompanyProfile(Dv dv)
        {
            Guid id;
            return Guid.TryParse((dv.Setting("desk.company_profile") ?? "").Trim(), out id) && dv.Exists("gc_document", id) ? id : (Guid?)null;
        }

        // ---------------------------------------------------------------- drafts

        /// <summary>
        /// A reply draft for a person to check and send from Gmail (the "Desk drafts" flow puts it into the Gmail thread).
        /// Any earlier pending draft in the same thread is discarded: only the latest is relevant.
        /// </summary>
        public static Guid Draft(DeskWriter w, Guid conversationId, string to, string subject, string body, IEnumerable<Guid> documents, string action, bool autoSend = false,
                                 bool replacePending = true)
        {
            if (replacePending)
            {
                foreach (var old in w.Dv.Query("gc_message", new[] { "gc_messageid" }, 10, "gc_conversation", ConditionOperator.Equal, conversationId,
                                               "gc_draftstatus", ConditionOperator.Equal, DeskChoice.DraftStatus.Pending))
                {
                    var discard = new Entity("gc_message", old.Id);
                    discard["gc_draftstatus"] = new OptionSetValue(DeskChoice.DraftStatus.Discarded);
                    w.Update(discard, "discard_draft");
                }
            }
            var m = new Entity("gc_message");
            m["gc_name"] = Cut(subject, 200);
            m["gc_conversation"] = new EntityReference("gc_conversation", conversationId);
            m["gc_text"] = Cut(WithoutSignOff(body) + "\n\n" + Signature(w.Dv), 100000);
            m["gc_isplatform"] = true;
            m["gc_direction"] = new OptionSetValue(MailChoice.Direction.Draft);
            m["gc_draftstatus"] = new OptionSetValue(DeskChoice.DraftStatus.Pending);
            m["gc_toaddress"] = Cut(to, 320);
            m["gc_subject"] = Cut(subject, 400);
            m["gc_senderlabel"] = autoSend ? "Email Desk (auto-send)" : "Email Desk (draft)";
            m["gc_autosend"] = autoSend;
            var docs = (documents ?? new Guid[0]).ToList();
            if (docs.Count > 0) m["gc_attachments"] = Json.Serialize(J.Obj("documents", docs.Select(d => (object)d.ToString()).ToList()));
            var id = w.Create(m, action, J.Obj("to", to, "subject", subject, "attachments", docs.Count, "auto_send", autoSend));
            w.Drafted(conversationId, action);
            return id;
        }

        private static readonly Regex QuantityRx = new Regex(@"\b\d[\d,.]*\s?(mt|tons?|tonnes?|kgs?|dmt|wmt|metric tons?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TermsWordsRx = new Regex(@"\b(price|priced|cost|payment|paid|lc|l/c|tt|advance|validity|valid|quantity|qty|shipment|delivery|packing|origin|commission|contact|email|phone|whatsapp|tel)\b",
                                                               RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A seller's specification as shown to a buyer: only the technical part. Sentences with a price, terms, quantity, contact details
        /// or the seller's name are dropped (prices and terms are shown from our own fields, never copied from the seller's text).
        /// </summary>
        public static string SafeSpec(string spec, string sellerName)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var nameWords = Tokens(Regex.Replace(sellerName ?? "", @"\[[^\]]*\]", " ")).Where(t => t.Length > 3 && !new[] { "test", "trading", "company", "limited", "private", "group", "international", "industries", "exports", "metals", "minerals" }.Contains(t)).ToList();
            var kept = Regex.Split(spec, @"(?<=[.;])\s+|\r?\n|;\s*")
                .Select(x => x.Trim().TrimEnd('.', ';').Trim())
                .Select(x => Regex.Replace(x, @"^(product|specification|spec|grade)\s*:\s*", "", RegexOptions.IgnoreCase))
                .Where(x => x.Length > 0 && !PriceRx.IsMatch(x) && !TermsWordsRx.IsMatch(x) && x.IndexOf('@') < 0 && !Regex.IsMatch(x, @"https?://|www\.", RegexOptions.IgnoreCase))
                .Where(x => !nameWords.Any(w => Regex.IsMatch(x, @"\b" + Regex.Escape(w) + @"\b", RegexOptions.IgnoreCase)))
                .Distinct().ToList();
            return kept.Count == 0 ? null : string.Join("; ", kept);
        }

        /// <summary>
        /// A seller's terms as shown to a buyer (origin, lead time, payment, packing): lines or sentences with a price or amount,
        /// the seller's price figure, contact details, links or the seller's name are dropped. Null when nothing is left.
        /// </summary>
        public static string SafeTerms(string terms, string sellerName, decimal? sellerPrice)
        {
            if (string.IsNullOrWhiteSpace(terms)) return null;
            var nameWords = Tokens(Regex.Replace(sellerName ?? "", @"\[[^\]]*\]", " ")).Where(t => t.Length > 3 && !new[] { "test", "trading", "company", "limited", "private", "group", "international", "industries", "exports", "metals", "minerals" }.Contains(t)).ToList();
            var figure = sellerPrice == null ? "" : Regex.Replace(sellerPrice.Value.ToString("0.##", CultureInfo.InvariantCulture), @"[^\d]", "");
            var kept = Regex.Split(terms, @"(?<=[.;])\s+|\r?\n")
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !PriceRx.IsMatch(x) && x.IndexOf('@') < 0 && !Regex.IsMatch(x, @"https?://|www\.|\+?\d[\d\s().-]{8,}\d", RegexOptions.IgnoreCase))
                .Where(x => figure.Length < 3 || !Regex.Replace(x, @"[^\d]", "").Contains(figure))
                .Where(x => !nameWords.Any(w => Regex.IsMatch(x, @"\b" + Regex.Escape(w) + @"\b", RegexOptions.IgnoreCase)))
                .Distinct().ToList();
            return kept.Count == 0 ? null : string.Join("\n", kept);
        }

        /// <summary>The seller's account name behind a deal, for masking.</summary>
        public static string SellerName(Dv dv, Guid? dealId)
        {
            var deal = dealId == null ? null : dv.Retrieve("gc_deal", dealId.Value, "gc_seller");
            var seller = deal == null || H(deal, "gc_seller") == null ? null : dv.Retrieve("account", H(deal, "gc_seller").Value, "name");
            return seller == null ? null : seller.GetAttributeValue<string>("name");
        }

        /// <summary>An email that states both a price and a quantity (an offer that can become a seller lot).</summary>
        public static bool HasPriceAndQuantity(string text)
        {
            return PriceRx.IsMatch(text ?? "") && QuantityRx.IsMatch(text ?? "");
        }

        private static readonly Regex PriceRx = new Regex(@"(usd|us\$|eur|inr|rs\.?|\$|€|₹)\s?\d|\d\s?(usd|eur|inr)\b|per\s+(mt|ton|tonne|kg|lb)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Whether a desk email goes out without a person pressing Send (setting email.autosend):
        /// off (default) = never; routine = enquiries to sellers and replies that mention no price; all = everything except contracts.
        /// </summary>
        public static bool AutoSend(Dv dv, string kind, string body)
        {
            var mode = (dv.Setting("email.autosend") ?? "off").Trim().ToLowerInvariant();
            if (kind == "contract" || mode == "off") return false;
            if (mode == "all") return true;
            return mode == "routine" && (kind == "enquiry" || (kind == "reply" && !PriceRx.IsMatch(body ?? "")));
        }

        /// <summary>
        /// A short briefing email to the desk owner (setting desk.owner_email; "{mailbox}" = the connected mailbox), all in one
        /// "DealOS desk briefing" thread. Sent automatically: it only goes to us.
        /// documents are attached (e.g. contracts to check before they go out); meta (JSON on the briefing: reply code, task, drafts)
        /// lets the owner answer the briefing by reply (Approvals).
        /// </summary>
        public static Guid Brief(DeskWriter w, string subject, string text, IEnumerable<Guid> documents = null, Dictionary<string, object> meta = null)
        {
            var owner = w.Dv.Setting("desk.owner_email") ?? "{mailbox}";
            if (owner.Trim().ToLowerInvariant() == "off") return Guid.Empty;
            var conv = w.Dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_name", ConditionOperator.Equal, Approvals.BriefingThread).FirstOrDefault();
            Guid convId;
            if (conv != null) convId = conv.Id;
            else
            {
                var c = new Entity("gc_conversation");
                c["gc_name"] = Approvals.BriefingThread;
                c["gc_channel"] = new OptionSetValue(MailChoice.ChannelEmail);
                convId = w.Create(c, "create_briefing_thread");
            }
            var m = new Entity("gc_message");
            m["gc_name"] = Cut(subject, 200);
            m["gc_conversation"] = new EntityReference("gc_conversation", convId);
            m["gc_text"] = Cut(text, 100000);
            m["gc_isplatform"] = true;
            m["gc_direction"] = new OptionSetValue(MailChoice.Direction.Draft);
            m["gc_draftstatus"] = new OptionSetValue(DeskChoice.DraftStatus.Pending);
            m["gc_toaddress"] = owner;
            m["gc_subject"] = Cut("[DealOS] " + subject, 400);
            m["gc_senderlabel"] = "Email Desk (briefing)";
            m["gc_autosend"] = true;
            var docs = (documents ?? new Guid[0]).ToList();
            if (docs.Count > 0) m["gc_attachments"] = Json.Serialize(J.Obj("documents", docs.Select(d => (object)d.ToString()).ToList()));
            if (meta != null) m["gc_emailmeta"] = Json.Serialize(meta);
            return w.Create(m, "brief_owner", J.Obj("subject", subject, "attachments", docs.Count));
        }

        /// <summary>
        /// A briefing that lists drafts a person may release by replying SEND (and, with a task, APPROVE): the drafts and the code
        /// are stored on the briefing. No drafts: a plain briefing.
        /// </summary>
        public static Guid BriefWithDrafts(DeskWriter w, string subject, string text, IEnumerable<Guid> drafts, IEnumerable<Guid> documents = null)
        {
            var ids = (drafts ?? new Guid[0]).Where(d => d != Guid.Empty).Distinct().ToList();
            if (ids.Count == 0) return Brief(w, subject, text, documents);
            var code = Approvals.NewCode();
            var body = text.TrimEnd() + "\n\nReply SEND to send the " + (ids.Count == 1 ? "draft" : ids.Count + " drafts") +
                       " as they stand in Gmail now (edit them there first if needed), or send them yourself from Gmail.\nRef " + code + ".";
            return Brief(w, subject + " (ref " + code + ")", body, documents,
                         J.Obj("code", code, "drafts", ids.Select(d => (object)d.ToString()).ToList()));
        }

        /// <summary>A stored draft's text without the desk signature that Draft appended (so an agent can rework it).</summary>
        public static string WithoutSignature(Dv dv, string text)
        {
            var t = text ?? "";
            var sig = Signature(dv);
            return !string.IsNullOrEmpty(sig) && t.EndsWith(sig) ? t.Substring(0, t.Length - sig.Length).TrimEnd() : t;
        }

        /// <summary>Drops a closing the writer added ("Best regards," and a name), because the desk signature follows.</summary>
        public static string WithoutSignOff(string body)
        {
            var text = (body ?? "").TrimEnd();
            var m = Regex.Match(text, @"\n\s*(best|kind|warm)?\s*(regards|wishes)[^\n]{0,20}(\n[^\n]{0,60}){0,3}$|\n\s*(thanks|thank you|sincerely|yours (sincerely|faithfully|truly))[ ,.!]*(\n[^\n]{0,60}){0,3}$",
                                RegexOptions.IgnoreCase);
            return m.Success && m.Index > 0 ? text.Substring(0, m.Index).TrimEnd() : text;
        }

        /// <summary>Reply subject for a thread: "Re: " + the first subject, unless it already starts with Re.</summary>
        public static string ReplySubject(Dv dv, Guid conversationId)
        {
            var first = dv.Query("gc_message", new[] { "gc_subject", "createdon" }, 20, "gc_conversation", ConditionOperator.Equal, conversationId)
                          .Where(m => !string.IsNullOrWhiteSpace(m.GetAttributeValue<string>("gc_subject")))
                          .OrderBy(m => m.GetAttributeValue<DateTime>("createdon")).FirstOrDefault();
            var s = first == null ? "Your enquiry" : first.GetAttributeValue<string>("gc_subject");
            return Regex.IsMatch(s, @"^\s*re\s*:", RegexOptions.IgnoreCase) ? s : "Re: " + s;
        }

        /// <summary>Address to write to in a thread: the last received email's Reply-To, else its sender.</summary>
        public static string ReplyAddress(Dv dv, Guid conversationId)
        {
            var last = dv.Query("gc_message", new[] { "gc_fromaddress", "gc_emailmeta", "gc_direction" }, 20, "gc_conversation", ConditionOperator.Equal, conversationId)
                         .FirstOrDefault(m => (m.GetAttributeValue<OptionSetValue>("gc_direction") ?? new OptionSetValue(0)).Value == MailChoice.Direction.Inbound);
            if (last == null) return null;
            try
            {
                var replyTo = J.Str(Json.ParseObject(last.GetAttributeValue<string>("gc_emailmeta") ?? "{}"), "reply_to");
                if (!string.IsNullOrWhiteSpace(replyTo)) return replyTo;
            }
            catch (FormatException) { }
            return last.GetAttributeValue<string>("gc_fromaddress");
        }

        /// <summary>
        /// A new email (new Gmail thread) from a seller we have an open enquiry with: that enquiry's thread, so a quote sent as a fresh
        /// email joins the seller's deal. Sellers only: a buyer's new email is often a new requirement, so it stays a thread of its own
        /// (the Trade Desk sees the buyer's other open requirements). Open = deal before Settled and not cancelled. With several open
        /// enquiries, the one whose name shares most words with the subject wins; a tie links nothing.
        /// </summary>
        public static Guid? OpenThreadFor(Dv dv, string address, string subject)
        {
            var open = new List<Entity>();
            foreach (var acc in AccountsOf(dv, address))
                foreach (var t in dv.Query("gc_conversation", new[] { "gc_name", "gc_side", "gc_deal" }, 20, "gc_counterparty", ConditionOperator.Equal, acc,
                                           "gc_side", ConditionOperator.Equal, DeskChoice.Side.Seller))
                {
                    var dealId = H(t, "gc_deal");
                    var deal = dealId == null ? null : dv.Retrieve("gc_deal", dealId.Value, "gc_stage");
                    var stage = deal == null ? null : deal.GetAttributeValue<OptionSetValue>("gc_stage");
                    if (deal == null || (stage != null && stage.Value >= DeskChoice.DealStage.Settled)) continue;
                    open.Add(t);
                }
            return PickThread(open.Select(t => new KeyValuePair<Guid, string>(t.Id, t.GetAttributeValue<string>("gc_name"))).ToList(), subject);
        }

        /// <summary>The companies an email address belongs to (contact's parent account, or an account with that email).</summary>
        public static List<Guid> AccountsOf(Dv dv, string address)
        {
            var accounts = new List<Guid>();
            if (string.IsNullOrWhiteSpace(address)) return accounts;
            foreach (var c in dv.Query("contact", new[] { "parentcustomerid" }, 5, "emailaddress1", ConditionOperator.Equal, address))
            {
                var p = c.GetAttributeValue<EntityReference>("parentcustomerid");
                if (p != null && p.LogicalName == "account" && !accounts.Contains(p.Id)) accounts.Add(p.Id);
            }
            foreach (var a in dv.Query("account", new[] { "accountid" }, 5, "emailaddress1", ConditionOperator.Equal, address))
                if (!accounts.Contains(a.Id)) accounts.Add(a.Id);
            return accounts;
        }

        /// <summary>One candidate → it; several → the unique best word overlap with the subject; otherwise none.</summary>
        public static Guid? PickThread(List<KeyValuePair<Guid, string>> candidates, string subject)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0].Key;
            var words = new HashSet<string>(Tokens(subject));
            var scored = candidates.Select(c => new { c.Key, Score = Tokens(c.Value).Count(words.Contains) }).OrderByDescending(x => x.Score).ToList();
            return scored[0].Score > 0 && scored[0].Score > scored[1].Score ? scored[0].Key : (Guid?)null;
        }

        /// <summary>The person we write to in a thread: the display name of the last received email, else the lead's contact for the counterparty.</summary>
        public static string ContactName(Dv dv, Guid conversationId)
        {
            var last = dv.Query("gc_message", new[] { "gc_senderlabel", "gc_direction" }, 20, "gc_conversation", ConditionOperator.Equal, conversationId)
                         .FirstOrDefault(m => (m.GetAttributeValue<OptionSetValue>("gc_direction") ?? new OptionSetValue(0)).Value == MailChoice.Direction.Inbound);
            var name = PersonName(last == null ? null : last.GetAttributeValue<string>("gc_senderlabel"));
            if (name != null) return name;
            var conv = dv.Retrieve("gc_conversation", conversationId, "gc_counterparty");
            var acc = conv == null ? null : H(conv, "gc_counterparty");
            var lead = acc == null ? null : dv.Query("gc_lead", new[] { "gc_contactname" }, 1, "gc_account", ConditionOperator.Equal, acc.Value).FirstOrDefault();
            return PersonName(lead == null ? null : lead.GetAttributeValue<string>("gc_contactname"));
        }

        /// <summary>"Rakesh Jain &lt;r@x.com&gt;" → "Rakesh Jain"; null when there is no usable name (bare address, digits, too long).</summary>
        public static string PersonName(string label)
        {
            var name = (label ?? "").Split('<')[0].Trim().Trim('"', '\'').Trim();
            return name.Length < 2 || name.Length > 60 || name.Contains("@") || Regex.IsMatch(name, @"\d") ? null : name;
        }

        private static readonly Regex GenericGreetingRx = new Regex(
            @"^\s*(dear|hello|hi)\s+(?<who>buyer|seller|supplier|customer|client|sir\s*(or|/)\s*madam|sirs?|madam|team|all)\b(?:[ \t]*[,!:.]|[ \t]*(?=\r?\n|$))[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Replaces a generic opening ("Dear Buyer,", "Dear Sir,") with the contact's name when we know it. Without a name,
        /// "Dear Buyer/Seller/Supplier/Customer" becomes "Dear Sir or Madam" (a role is not a salutation). Other openings are left alone.
        /// </summary>
        public static string Greet(string body, string name)
        {
            var text = body ?? "";
            var m = GenericGreetingRx.Match(text);
            if (!m.Success) return text;
            var who = m.Groups["who"].Value.ToLowerInvariant();
            string greeting;
            if (!string.IsNullOrWhiteSpace(name)) greeting = "Dear " + name.Trim() + ",";
            else if (new[] { "buyer", "seller", "supplier", "customer", "client" }.Contains(who)) greeting = "Dear Sir or Madam,";
            else return text;
            return greeting + text.Substring(m.Length);
        }

        // ---------------------------------------------------------------- sourcing

        public static List<string> Tokens(string text)
        {
            return Regex.Matches((text ?? "").ToLowerInvariant(), @"[a-z][a-z0-9]{2,}").Cast<Match>().Select(m => m.Value)
                        .Where(t => !Stopwords.Contains(t)).Distinct().ToList();
        }

        /// <summary>How well a lead's products match the wanted commodity (0 = no match).</summary>
        private static readonly HashSet<string> GenericPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ferro", "silico", "metal", "metals", "metallic", "alloy", "alloys", "ore", "ores", "concentrate", "concentrates", "oxide", "refined",
            "carbon", "low", "flakes", "granules", "briquettes", "scrap", "crude", "rare", "earth", "earths"
        };

        /// <summary>The word that names the material: the first token that is not a generic prefix ("Ferro Tungsten" → tungsten).</summary>
        public static string MainToken(List<string> tokens)
        {
            return tokens.FirstOrDefault(t => !GenericPrefixes.Contains(t)) ?? tokens[0];
        }

        public static int LeadScore(List<string> tokens, string leadCommodities, int shipments, DateTime? lastSeen, DateTime now)
        {
            if (tokens.Count == 0 || string.IsNullOrWhiteSpace(leadCommodities)) return 0;
            var text = leadCommodities.ToLowerInvariant();
            var hits = tokens.Count(t => Regex.IsMatch(text, @"\b" + Regex.Escape(t)));
            if (hits == 0) return 0;
            // The most specific word ("vanadium", "strontium", "tungsten" in "Ferro Tungsten") must match; generic words alone are not enough.
            if (!Regex.IsMatch(text, @"\b" + Regex.Escape(MainToken(tokens)))) return 0;
            var score = hits * 10 + (hits == tokens.Count ? 20 : 0) + Math.Min(shipments, 20);
            if (lastSeen != null && (now - lastSeen.Value).TotalDays <= 180) score += 10;
            return score;
        }

        /// <summary>
        /// Shortlists seller leads for a requirement and opens one source request per seller with an email address:
        /// seller account, deal (Inquiry, back to back), RFQ invite, seller thread and a masked enquiry draft.
        /// Leads without an email go to a review task for a person to find a contact. Idempotent per lead.
        /// </summary>
        public static Dictionary<string, object> Source(DeskWriter w, Guid requirementId)
        {
            var dv = w.Dv;
            var req = dv.Retrieve("gc_buyerrequirement", requirementId);
            if (req == null) throw new ToolRefusal("Requirement " + requirementId + " was not found.");
            var commodity = req.GetAttributeValue<string>("gc_commoditytext") ?? req.GetAttributeValue<string>("gc_name");
            var tokens = Tokens(commodity);
            if (tokens.Count == 0) throw new ToolRefusal("The requirement has no commodity to source.");
            var buyer = req.GetAttributeValue<EntityReference>("gc_buyer");
            var max = Math.Max(1, dv.SettingInt("email.sourcing.max_sellers", 5));

            var q = new QueryExpression("gc_lead") { ColumnSet = new ColumnSet(true), TopCount = 2000 };
            q.Criteria.AddCondition("gc_status", ConditionOperator.NotEqual, DeskChoice.LeadStatus.DoNotContact);
            q.Criteria.AddCondition("gc_role", ConditionOperator.In, DeskChoice.LeadRole.Seller, DeskChoice.LeadRole.Both);
            var now = DateTime.UtcNow;
            var ranked = dv.Svc.RetrieveMultiple(q).Entities
                .Select(l => new { Lead = l, Score = LeadScore(tokens, l.GetAttributeValue<string>("gc_commodities"), l.GetAttributeValue<int?>("gc_shipments") ?? 0, l.GetAttributeValue<DateTime?>("gc_lastseen"), now) })
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();

            var existingInvites = dv.Query("gc_rfqinvite", new[] { "gc_seller" }, 200, "gc_requirement", ConditionOperator.Equal, requirementId)
                                    .Select(i => H(i, "gc_seller")).Where(x => x != null).Select(x => x.Value).ToList();
            var invited = new List<object>();
            var noEmail = new List<object>();
            foreach (var x in ranked)
            {
                if (invited.Count >= max) break;
                var lead = x.Lead;
                var email = (lead.GetAttributeValue<string>("gc_email") ?? "").Trim().ToLowerInvariant();
                var name = lead.GetAttributeValue<string>("gc_name") ?? "Supplier";
                if (string.IsNullOrEmpty(email) || email.IndexOf('@') < 0)
                {
                    if (noEmail.Count < 25) noEmail.Add(J.Obj("lead", name, "country", lead.GetAttributeValue<string>("gc_country"), "phone", lead.GetAttributeValue<string>("gc_phone"),
                                                              "website", lead.GetAttributeValue<string>("gc_website"), "products", Cut(lead.GetAttributeValue<string>("gc_commodities"), 200),
                                                              "source", lead.GetAttributeValue<string>("gc_sourceref"), "lead_id", lead.Id.ToString()));
                    continue;
                }
                var sellerId = SellerAccount(w, lead, email, name);
                if (existingInvites.Contains(sellerId)) continue;
                invited.Add(OpenSourceRequest(w, req, commodity, sellerId, name, email, lead));
                existingInvites.Add(sellerId);
            }

            var current = req.GetAttributeValue<OptionSetValue>("gc_deskstage");
            if (current == null || current.Value < DeskChoice.Stage.Sourcing)
            {
                var stage = new Entity("gc_buyerrequirement", requirementId);
                stage["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Sourcing);
                w.Update(stage, "requirement_sourcing");
            }

            if (noEmail.Count > 0)
                Task(w, "Find contacts for sourcing: " + Cut(commodity, 60), J.Obj("requirementId", requirementId.ToString(), "commodity", commodity,
                     "why", "These matching sellers have no email address. Find a contact (email) and add it to the lead, then run sourcing again.", "leads", noEmail), null);
            if (ranked.Count == 0)
                Task(w, "No sellers found: " + Cut(commodity, 70), J.Obj("requirementId", requirementId.ToString(), "commodity", commodity,
                     "why", "No lead mentions this commodity. Import trade data or add leads (tools/import_leads.py), then run sourcing again."), null);
            return J.Obj("matched_leads", ranked.Count, "source_requests", invited, "invited", invited.Count, "leads_without_email", noEmail.Count);
        }

        internal static Guid SellerAccount(DeskWriter w, Entity lead, string email, string name)
        {
            var linked = H(lead, "gc_account");
            if (linked != null) return linked.Value;
            var existing = w.Dv.Query("account", new[] { "accountid" }, 1, "emailaddress1", ConditionOperator.Equal, email).FirstOrDefault();
            Guid id;
            if (existing != null) id = existing.Id;
            else
            {
                // No party role on purpose: KYB starts only once a deal is agreed (the Trade Desk asks for the documents then).
                var a = new Entity("account");
                a["name"] = Cut(name, 160);
                a["emailaddress1"] = email;
                if (!string.IsNullOrWhiteSpace(lead.GetAttributeValue<string>("gc_website"))) a["websiteurl"] = Cut(lead.GetAttributeValue<string>("gc_website"), 200);
                if (!string.IsNullOrWhiteSpace(lead.GetAttributeValue<string>("gc_phone"))) a["telephone1"] = Cut(lead.GetAttributeValue<string>("gc_phone"), 50);
                id = w.Create(a, "create_seller_account", J.Obj("name", name));
            }
            var l = new Entity("gc_lead", lead.Id);
            l["gc_account"] = new EntityReference("account", id);
            l["gc_status"] = new OptionSetValue(DeskChoice.LeadStatus.Contacted);
            w.Update(l, "lead_contacted");
            return id;
        }

        private static object OpenSourceRequest(DeskWriter w, Entity req, string commodity, Guid sellerId, string sellerName, string email, Entity lead)
        {
            var dv = w.Dv;
            var buyer = req.GetAttributeValue<EntityReference>("gc_buyer");
            var qty = req.GetAttributeValue<decimal?>("gc_quantity");
            var unit = req.GetAttributeValue<OptionSetValue>("gc_quantityunit");
            var deal = new Entity("gc_deal");
            deal["gc_name"] = Cut("Desk: " + commodity + " – " + sellerName, 100);
            deal["gc_stage"] = new OptionSetValue(DeskChoice.DealStage.Inquiry);
            deal["gc_emaildesk"] = true;
            if (buyer != null) deal["gc_buyer"] = buyer;
            deal["gc_seller"] = new EntityReference("account", sellerId);
            deal["gc_requirement"] = new EntityReference("gc_buyerrequirement", req.Id);
            if (qty != null) deal["gc_quantity"] = qty;
            if (unit != null) deal["gc_quantityunit"] = unit;
            deal["gc_currency"] = req.GetAttributeValue<string>("gc_currency") ?? "USD";
            if (req.GetAttributeValue<OptionSetValue>("gc_incoterm") != null) deal["gc_incoterm"] = req.GetAttributeValue<OptionSetValue>("gc_incoterm");
            if (req.GetAttributeValue<EntityReference>("gc_commodity") != null) deal["gc_commodity"] = req.GetAttributeValue<EntityReference>("gc_commodity");
            var dealId = w.Create(deal, "create_desk_deal", J.Obj("seller", sellerName));

            var invite = new Entity("gc_rfqinvite");
            invite["gc_name"] = Cut("Source request: " + commodity + " – " + sellerName, 200);
            invite["gc_requirement"] = new EntityReference("gc_buyerrequirement", req.Id);
            invite["gc_seller"] = new EntityReference("account", sellerId);
            invite["gc_deal"] = new EntityReference("gc_deal", dealId);
            invite["gc_status"] = new OptionSetValue(DeskChoice.Invite.Invited);
            invite["gc_invitedon"] = DateTime.UtcNow;
            var inviteId = w.Create(invite, "create_source_request", J.Obj("seller", sellerName));

            var conv = new Entity("gc_conversation");
            conv["gc_name"] = Cut(commodity + " enquiry – " + sellerName, 200);
            conv["gc_channel"] = new OptionSetValue(MailChoice.ChannelEmail);
            conv["gc_side"] = new OptionSetValue(DeskChoice.Side.Seller);
            conv["gc_requirement"] = new EntityReference("gc_buyerrequirement", req.Id);
            conv["gc_invite"] = new EntityReference("gc_rfqinvite", inviteId);
            conv["gc_counterparty"] = new EntityReference("account", sellerId);
            conv["gc_deal"] = new EntityReference("gc_deal", dealId);
            var convId = w.Create(conv, "create_seller_thread", J.Obj("seller", sellerName));

            var contact = lead.GetAttributeValue<string>("gc_contactname");
            var subject = "Enquiry: " + commodity + (qty != null ? " – " + Num(qty.Value) + " " + UnitLabel(unit) : "");
            var text = SourceRequestText(req, commodity, contact ?? sellerName);
            var draftId = Draft(w, convId, email, subject, text, null, "draft_source_request", AutoSend(dv, "enquiry", text));
            return J.Obj("seller", sellerName, "email", email, "deal_id", dealId.ToString(), "invite_id", inviteId.ToString(), "thread_id", convId.ToString(),
                         "draft_id", draftId.ToString());
        }

        /// <summary>The enquiry to a seller: specification and terms only; the buyer is never named.</summary>
        public static string SourceRequestText(Entity req, string commodity, string addressee)
        {
            var lines = new List<string> { "Dear " + (string.IsNullOrWhiteSpace(addressee) ? "Sir" : addressee) + ",", "",
                                           "We have a buyer for the following:", "" };
            lines.Add("- Product: " + commodity);
            var spec = req.GetAttributeValue<string>("gc_specification");
            if (!string.IsNullOrWhiteSpace(spec)) lines.Add("- Specification: " + spec.Trim().Replace("\n", "; "));
            var qty = req.GetAttributeValue<decimal?>("gc_quantity");
            if (qty != null) lines.Add("- Quantity: " + Num(qty.Value) + " " + UnitLabel(req.GetAttributeValue<OptionSetValue>("gc_quantityunit")));
            var packing = req.GetAttributeValue<string>("gc_packaging");
            if (!string.IsNullOrWhiteSpace(packing)) lines.Add("- Packing: " + packing);
            var delivery = req.GetAttributeValue<string>("gc_deliverytext");
            if (!string.IsNullOrWhiteSpace(delivery)) lines.Add("- Delivery: " + delivery);
            lines.Add("");
            lines.Add("If you can supply, please send your best price with the basis (FOB / CIF and port), quantity available, origin and lead time, along with a recent COA or SGS report and your company profile.");
            return string.Join("\n", lines);
        }

        // ---------------------------------------------------------------- quote window

        // ---------------------------------------------------------------- seller queue (buyer first)

        /// <summary>
        /// Buyer first, sellers in turn: the first seller to quote is the ACTIVE seller (gc_buyerrequirement.gc_activedeal) and is
        /// negotiated with the buyer. Sellers who quote later are queued in the order they answered. If the deal with the active
        /// seller breaks (seller walks away or goes silent, buyer turns the offer down) and the buyer is still looking, the next
        /// queued seller comes up. Sellers who never answered stay open; sellers who decline are left alone. Lot deals are not queued.
        /// </summary>
        public static Guid? ActiveDeal(Dv dv, Guid requirementId)
        {
            var req = dv.Retrieve("gc_buyerrequirement", requirementId, "gc_activedeal");
            var id = req == null ? null : H(req, "gc_activedeal");
            if (id == null) return null;
            var deal = dv.Retrieve("gc_deal", id.Value, "gc_stage");
            return deal == null || Stage(deal) == DeskChoice.DealStage.Cancelled ? (Guid?)null : id;
        }

        private static int Stage(Entity deal) { return (deal.GetAttributeValue<OptionSetValue>("gc_stage") ?? new OptionSetValue(DeskChoice.DealStage.Inquiry)).Value; }

        /// <summary>Sellers waiting their turn: live enquiry deals (not lots, not the active one) with a quote, in the order they first quoted.</summary>
        public static List<Entity> Queue(Dv dv, Guid requirementId)
        {
            var active = ActiveDeal(dv, requirementId);
            var queued = new List<KeyValuePair<DateTime, Entity>>();
            foreach (var d in dv.Query("gc_deal", new[] { "gc_seller", "gc_stage", "gc_sellerlot", "gc_name" }, 50, "gc_requirement", ConditionOperator.Equal, requirementId))
            {
                if (d.Id == active || H(d, "gc_sellerlot") != null || Stage(d) > DeskChoice.DealStage.Negotiation) continue;
                var quotes = dv.Query("gc_offer", new[] { "gc_fromparty", "createdon" }, 20, "gc_deal", ConditionOperator.Equal, d.Id)
                               .Where(o => H(o, "gc_fromparty") == H(d, "gc_seller")).ToList();
                if (quotes.Count > 0) queued.Add(new KeyValuePair<DateTime, Entity>(quotes.Min(o => o.GetAttributeValue<DateTime>("createdon")), d));
            }
            return queued.OrderBy(x => x.Key).Select(x => x.Value).ToList();
        }

        /// <summary>A seller quoted: with no active seller this deal becomes active ("active"), otherwise it waits in the queue ("queued").</summary>
        public static string TakeTurn(DeskWriter w, Guid requirementId, Guid dealId)
        {
            var active = ActiveDeal(w.Dv, requirementId);
            if (active == dealId) return "active";
            if (active != null) return "queued";
            var r = new Entity("gc_buyerrequirement", requirementId);
            r["gc_activedeal"] = new EntityReference("gc_deal", dealId);
            w.Update(r, "active_seller", J.Obj("deal", dealId));
            return "active";
        }

        /// <summary>The seller's latest live quote on a deal (open or countered, not expired), or null.</summary>
        public static Entity LatestQuote(Dv dv, Guid dealId)
        {
            var deal = dv.Retrieve("gc_deal", dealId, "gc_seller");
            return dv.Query("gc_offer", new[] { "gc_price", "gc_currency", "gc_quantity", "gc_incoterm", "gc_namedplace", "gc_terms", "gc_status", "gc_fromparty", "gc_deal", "gc_validuntil", "createdon" }, 20,
                            "gc_deal", ConditionOperator.Equal, dealId)
                     .Where(o => H(o, "gc_fromparty") == H(deal, "gc_seller"))
                     .Where(o => { var st = o.GetAttributeValue<OptionSetValue>("gc_status"); return st == null || st.Value == DeskChoice.Offer.Open || st.Value == DeskChoice.Offer.Countered; })
                     .Where(o => o.GetAttributeValue<DateTime?>("gc_validuntil") == null || o.GetAttributeValue<DateTime>("gc_validuntil") >= DateTime.UtcNow.Date)
                     .OrderByDescending(o => o.GetAttributeValue<DateTime>("createdon")).FirstOrDefault();
        }

        /// <summary>
        /// The deal with the active seller is off: the deal closes (the seller gets a short note when the buyer turned it down), then the
        /// next queued seller comes up if the buyer is still looking; otherwise the requirement closes and queued sellers get "not this time".
        /// </summary>
        public static Dictionary<string, object> Break(DeskWriter w, Guid requirementId, string reason, bool buyerStillLooking, bool noteToSeller)
        {
            var dv = w.Dv;
            var active = ActiveDeal(dv, requirementId);
            if (active != null && !w.DryRun)
            {
                var req = new OrganizationRequest("gc_TransitionDeal");
                req["DealId"] = active.Value;
                req["TargetStage"] = DeskChoice.DealStage.Cancelled;
                req["Reason"] = Cut("Desk: " + reason, 400);
                dv.System.Execute(req);
                if (noteToSeller) RegretNote(w, active.Value, "Our buyer has decided not to go ahead with this offer, so we cannot proceed this time");
            }
            var r = new Entity("gc_buyerrequirement", requirementId);
            r["gc_activedeal"] = null;
            r["gc_ourprice"] = null;
            w.Update(r, "active_seller_off", J.Obj("reason", reason, "buyer_still_looking", buyerStillLooking));
            if (w.DryRun) return J.Obj("ok", true, "note", "Dry run: the deal would close and the next seller would come up.");
            if (buyerStillLooking) return NextSeller(w, requirementId, reason);

            var notes = 0;
            foreach (var d in Queue(dv, requirementId))
            {
                var req = new OrganizationRequest("gc_TransitionDeal");
                req["DealId"] = d.Id;
                req["TargetStage"] = DeskChoice.DealStage.Cancelled;
                req["Reason"] = "Desk: buyer closed the requirement";
                dv.System.Execute(req);
                if (RegretNote(w, d.Id) != null) notes++;
            }
            var close = new Entity("gc_buyerrequirement", requirementId);
            close["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Closed);
            w.Update(close, "requirement_closed", J.Obj("reason", reason));
            Brief(w, "Requirement closed: " + Commodity(dv, requirementId), "The deal with the active seller is off (" + reason + ") and the buyer is no longer looking.\n\n" +
                  notes + " queued seller(s) get a 'not this time' draft.");
            return J.Obj("ok", true, "requirement_closed", true, "regret_notes", notes, "note", "Draft a short, polite closing note to the buyer only if useful.");
        }

        /// <summary>The next queued seller becomes active: our price (their quote + margin) is drafted to the buyer. None queued: back to sourcing.</summary>
        public static Dictionary<string, object> NextSeller(DeskWriter w, Guid requirementId, string why)
        {
            var dv = w.Dv;
            var commodity = Commodity(dv, requirementId);
            foreach (var d in Queue(dv, requirementId))
            {
                var quote = LatestQuote(dv, d.Id);
                if (quote == null) continue;
                var r = new Entity("gc_buyerrequirement", requirementId);
                r["gc_activedeal"] = new EntityReference("gc_deal", d.Id);
                w.Update(r, "active_seller", J.Obj("deal", d.Id, "after", why));
                var ours = OfferToBuyer(w, requirementId, quote, "We have another option for your requirement:");
                Brief(w, "Next seller up: " + commodity, "The deal with the previous seller is off (" + why + "). The next seller in the queue (" + d.GetAttributeValue<string>("gc_name") +
                      ") is now active: our offer to the buyer is " + (ours == null ? "-" : Num(ours.Value)) + " (their quote " + Num(quote.GetAttributeValue<decimal>("gc_price")) + " + margin)." +
                      "\n\nThe offer is drafted in the buyer thread.");
                return J.Obj("ok", true, "next_seller", true, "our_price_to_buyer", ours,
                             "note", "The next supplier's offer (our price " + (ours == null ? "-" : Num(ours.Value)) + ") is drafted to the buyer automatically. Do not draft another offer to the buyer; " +
                                     "never mention the previous supplier.");
            }
            var pending = dv.Query("gc_rfqinvite", new[] { "gc_status" }, 100, "gc_requirement", ConditionOperator.Equal, requirementId)
                            .Count(i => (i.GetAttributeValue<OptionSetValue>("gc_status") ?? new OptionSetValue(DeskChoice.Invite.Invited)).Value == DeskChoice.Invite.Invited);
            var back = new Entity("gc_buyerrequirement", requirementId);
            back["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Sourcing);
            w.Update(back, "back_to_sourcing", J.Obj("after", why, "sellers_not_answered", pending));
            Brief(w, "No seller queued: " + commodity, "The deal with the active seller is off (" + why + ") and no other seller has quoted yet." +
                  (pending > 0 ? " " + pending + " contacted seller(s) have not answered; the first to quote comes up." : " Every contacted seller has answered: consider contacting more sellers (start_sourcing more)."));
            return J.Obj("ok", true, "next_seller", false, "sellers_not_answered", pending,
                         "note", "No other supplier has quoted yet. Tell the buyer we are checking with other suppliers and will revert.");
        }

        /// <summary>Our offer to the buyer for one seller quote (price = quote + margin), drafted in the buyer thread. Returns our price.</summary>
        public static decimal? OfferToBuyer(DeskWriter w, Guid requirementId, Entity quote, string intro)
        {
            var dv = w.Dv;
            var req = dv.Retrieve("gc_buyerrequirement", requirementId, "gc_commoditytext", "gc_name", "gc_quantityunit");
            var thread = dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_requirement", ConditionOperator.Equal, requirementId,
                                  "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
            var to = thread == null ? null : ReplyAddress(dv, thread.Id);
            var ours = PriceToBuyer(quote.GetAttributeValue<decimal>("gc_price"), Margin(dv));
            var r = new Entity("gc_buyerrequirement", requirementId);
            r["gc_ourprice"] = ours;
            r["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.Quoted);
            w.Update(r, "our_price_to_buyer", J.Obj("price", ours));
            if (string.IsNullOrWhiteSpace(to)) return ours;
            var unit = UnitLabel(req.GetAttributeValue<OptionSetValue>("gc_quantityunit"));
            var basis = Label(DeskChoice.Incoterms, quote.GetAttributeValue<OptionSetValue>("gc_incoterm")) +
                        (quote.GetAttributeValue<string>("gc_namedplace") == null ? "" : " " + quote.GetAttributeValue<string>("gc_namedplace"));
            var l = new List<string> { Greet("Dear Sir,", ContactName(dv, thread.Id)), "", intro, "",
                                       "- Product: " + (req.GetAttributeValue<string>("gc_commoditytext") ?? req.GetAttributeValue<string>("gc_name")) };
            var qty = quote.GetAttributeValue<decimal?>("gc_quantity");
            if (qty != null) l.Add("- Quantity: " + Num(qty.Value) + " " + unit);
            l.Add("- Price: " + (quote.GetAttributeValue<string>("gc_currency") ?? "USD") + " " + Num(ours) + " per " + unit + (string.IsNullOrWhiteSpace(basis) ? "" : ", " + basis.Trim()));
            var terms = SafeTerms(quote.GetAttributeValue<string>("gc_terms"), SellerName(dv, H(quote, "gc_deal")), quote.GetAttributeValue<decimal?>("gc_price"));
            foreach (var line in (terms ?? "").Split('\n').Where(x => !string.IsNullOrWhiteSpace(x))) l.Add("- " + line.Trim());
            l.Add("");
            l.Add("Please let me know if this works for you, or your best price.");
            var text = string.Join("\n", l);
            Draft(w, thread.Id, to, ReplySubject(dv, thread.Id), text, null, "draft_offer_to_buyer", AutoSend(dv, "reply", text));
            return ours;
        }

        /// <summary>
        /// Desk timers: requirements with no active seller but a queued quote (the active deal closed some other way) get the next
        /// seller; an active seller silent after a chaser and desk.chase_after_hours more is moved on from. Returns how many moved.
        /// </summary>
        public static int AdvanceQueues(DeskWriter w)
        {
            var dv = w.Dv;
            var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, dv.SettingNum("desk.chase_after_hours", 48)));
            var q = new QueryExpression("gc_buyerrequirement") { ColumnSet = new ColumnSet("gc_activedeal"), TopCount = 200 };
            q.Criteria.AddCondition("gc_source", ConditionOperator.Equal, DeskChoice.RequirementSource.Email);
            q.Criteria.AddCondition("gc_deskstage", ConditionOperator.In, DeskChoice.Stage.Sourcing, DeskChoice.Stage.Quoted, DeskChoice.Stage.Negotiating);
            var moved = 0;
            foreach (var req in dv.Svc.RetrieveMultiple(q).Entities)
            {
                var active = ActiveDeal(dv, req.Id);
                if (active == null)
                {
                    if (Queue(dv, req.Id).Count == 0) continue;
                    if (J.Bool(NextSeller(w, req.Id, "the previous deal closed"), "next_seller")) moved++;
                    continue;
                }
                var thread = dv.Query("gc_conversation", new[] { "gc_chases", "gc_chasedon" }, 1, "gc_deal", ConditionOperator.Equal, active.Value,
                                      "gc_side", ConditionOperator.Equal, DeskChoice.Side.Seller).FirstOrDefault();
                if (thread == null || (thread.GetAttributeValue<int?>("gc_chases") ?? 0) < 1 || (thread.GetAttributeValue<DateTime?>("gc_chasedon") ?? DateTime.UtcNow) > cutoff) continue;
                var last = dv.Query("gc_message", new[] { "gc_direction", "gc_senton", "createdon" }, 50, "gc_conversation", ConditionOperator.Equal, thread.Id)
                             .Where(m => (m.GetAttributeValue<OptionSetValue>("gc_direction") ?? new OptionSetValue(-1)).Value != MailChoice.Direction.Draft)
                             .OrderByDescending(m => m.GetAttributeValue<DateTime?>("gc_senton") ?? m.GetAttributeValue<DateTime>("createdon")).FirstOrDefault();
                if (last == null || (last.GetAttributeValue<OptionSetValue>("gc_direction") ?? new OptionSetValue(-1)).Value != MailChoice.Direction.Outbound) continue;
                if (Queue(dv, req.Id).Count == 0) continue; // nobody to move on to: keep waiting for this seller
                Break(w, req.Id, "the seller did not answer after a reminder", true, false);
                moved++;
            }
            return moved;
        }

        private static string Commodity(Dv dv, Guid requirementId)
        {
            var req = dv.Retrieve("gc_buyerrequirement", requirementId, "gc_commoditytext", "gc_name");
            return req == null ? "the requirement" : (req.GetAttributeValue<string>("gc_commoditytext") ?? req.GetAttributeValue<string>("gc_name") ?? "the requirement");
        }

        // ---------------------------------------------------------------- contract documents

        /// <summary>
        /// For an Email Desk deal whose contract was approved: two PDF contracts (back to back), each attached to a draft in its own thread:
        /// sales contract us → buyer at the buyer price, purchase contract seller → us at the seller price.
        /// </summary>
        public static Dictionary<string, object> ContractOut(DeskWriter w, Guid contractId)
        {
            var dv = w.Dv;
            var contract = dv.Retrieve("gc_contract", contractId, "gc_name", "gc_deal", "gc_status");
            if (contract == null) throw new ToolRefusal("Contract " + contractId + " was not found.");
            var dealRef = contract.GetAttributeValue<EntityReference>("gc_deal");
            var deal = dv.Retrieve("gc_deal", dealRef.Id);
            if (!(deal.GetAttributeValue<bool?>("gc_emaildesk") ?? false)) return J.Obj("status", "NotEmailDesk");
            var reqId = H(deal, "gc_requirement");
            var req = reqId == null ? null : dv.Retrieve("gc_buyerrequirement", reqId.Value);
            var existing = dv.Query("gc_document", new[] { "gc_documentid", "gc_filename" }, 10, "gc_deal", ConditionOperator.Equal, deal.Id)
                             .Where(d => (d.GetAttributeValue<string>("gc_filename") ?? "").StartsWith("Contract-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (existing.Count > 0) return J.Obj("status", "Exists", "documents", existing.Count);

            var buyer = dv.Retrieve("account", H(deal, "gc_buyer") ?? Guid.Empty, "name", "address1_composite");
            var seller = dv.Retrieve("account", H(deal, "gc_seller") ?? Guid.Empty, "name", "address1_composite");
            var accepted = dv.Query("gc_offer", new[] { "gc_terms", "gc_price", "gc_namedplace" }, 1, "gc_deal", ConditionOperator.Equal, deal.Id,
                                    "gc_status", ConditionOperator.Equal, DeskChoice.Offer.Accepted).FirstOrDefault();
            var sellerOffer = dv.Query("gc_offer", new[] { "gc_terms" }, 10, "gc_deal", ConditionOperator.Equal, deal.Id)
                                .FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.GetAttributeValue<string>("gc_terms")));
            var us = CompanyName(dv);
            var commodity = req == null ? deal.GetAttributeValue<string>("gc_name") : (req.GetAttributeValue<string>("gc_commoditytext") ?? req.GetAttributeValue<string>("gc_name"));
            var qty = deal.GetAttributeValue<decimal?>("gc_quantity") ?? 0;
            var unit = UnitLabel(deal.GetAttributeValue<OptionSetValue>("gc_quantityunit"));
            var currency = deal.GetAttributeValue<string>("gc_currency") ?? "USD";
            var sellerPrice = deal.GetAttributeValue<decimal?>("gc_price") ?? (accepted == null ? 0 : accepted.GetAttributeValue<decimal?>("gc_price") ?? 0);
            var buyerPrice = deal.GetAttributeValue<decimal?>("gc_buyerprice") ?? PriceToBuyer(sellerPrice, Margin(dv));
            var incoterm = Label(DeskChoice.Incoterms, deal.GetAttributeValue<OptionSetValue>("gc_incoterm"));
            var place = deal.GetAttributeValue<string>("gc_namedplace") ?? (accepted == null ? null : accepted.GetAttributeValue<string>("gc_namedplace"));
            var spec = req == null ? null : req.GetAttributeValue<string>("gc_specification");
            var number = deal.GetAttributeValue<string>("gc_dealnumber") ?? deal.Id.ToString().Substring(0, 8).ToUpperInvariant();

            Func<string, string, string, string, decimal, string, IEnumerable<string>> body = (kind, sellerParty, buyerParty, paymentText, price, extra) =>
            {
                var l = new List<string>
                {
                    "Contract no.: " + number + "-" + kind.Substring(0, 1) + "    Date: " + DateTime.UtcNow.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), "",
                    "SELLER: " + sellerParty, "BUYER: " + buyerParty, "",
                    "1. Product: " + commodity,
                    "2. Specification: " + (string.IsNullOrWhiteSpace(spec) ? "as per the agreed specification and certificate of analysis" : spec.Replace("\n", "; ")),
                    "3. Quantity: " + Num(qty) + " " + unit + " (+/- 5% at Seller's option unless agreed otherwise)",
                    "4. Price: " + currency + " " + Num(price) + " per " + unit + ", " + (incoterm ?? "delivery basis as agreed") + (string.IsNullOrWhiteSpace(place) ? "" : " " + place) + " (Incoterms 2020)",
                    "5. Total value: " + currency + " " + Num(Math.Round(price * qty, 2)),
                    "6. Payment: " + (string.IsNullOrWhiteSpace(paymentText) ? "as agreed in writing between the parties" : paymentText),
                    "7. Inspection: independent inspection of quality and weight at loading by an agency agreed by both parties; its certificate is final for quality and weight.",
                    "8. Documents: commercial invoice, packing list, certificate of analysis, certificate of origin, transport document, inspection certificate.",
                    "9. Delivery: " + (req == null ? "as agreed" : (req.GetAttributeValue<string>("gc_deliverytext") ?? "as agreed")),
                    "10. Compliance: each party confirms it is not subject to sanctions, that the goods are of non-sanctioned origin, and that it holds the licences needed for this trade.",
                    "11. Force majeure, governing law and disputes: " + (dv.Setting("trade.governing_law") ?? "laws of India; disputes by arbitration in Mumbai under the Arbitration and Conciliation Act, 1996") + ".",
                    "12. This contract becomes binding when signed by both parties."
                };
                if (!string.IsNullOrWhiteSpace(extra)) { l.Add(""); l.Add("Other agreed terms: " + extra); }
                l.AddRange(new[] { "", "", "For the Seller: ______________________   Name / title / date", "", "For the Buyer:  ______________________   Name / title / date", "",
                                   "Template " + (dv.Setting("trade.contract_template") ?? "DEALOS-B2B-v1") + ". Generated from the agreed terms; review with counsel before first use." });
                return l;
            };
            var buyerName = buyer == null ? "Buyer" : buyer.GetAttributeValue<string>("name");
            var sellerName = seller == null ? "Seller" : seller.GetAttributeValue<string>("name");
            var buyerPayment = req == null ? null : req.GetAttributeValue<string>("gc_paymenttext");
            var sellerTerms = sellerOffer == null ? null : sellerOffer.GetAttributeValue<string>("gc_terms");

            var salesPdf = PdfWriter.Write("SALES CONTRACT", body("Sales", us, buyerName, buyerPayment, buyerPrice, null));
            var purchasePdf = PdfWriter.Write("PURCHASE CONTRACT", body("Purchase", sellerName, us, null, sellerPrice, sellerTerms));
            var salesDoc = Document(w, deal.Id, "Contract-Sales-" + number + ".pdf", salesPdf);
            var purchaseDoc = Document(w, deal.Id, "Contract-Purchase-" + number + ".pdf", purchasePdf);

            var summary = "Sales contract to " + buyerName + ": " + Num(qty) + " " + unit + " " + commodity + " at " + currency + " " + Num(buyerPrice) + " per " + unit +
                          ". Purchase contract from " + sellerName + ": at " + currency + " " + Num(sellerPrice) + " per " + unit + ".";
            if (Esign.On(dv))
            {
                // E-signature: nothing goes out yet. The owner sees both PDFs and approves sending them through DocuSign.
                var approval = Esign.RequestApproval(w, contractId, deal, salesDoc, purchaseDoc, summary);
                return J.Obj("status", "AwaitingEsignApproval", "documents", 2, "esign", approval);
            }

            var drafts = new List<object>();
            var buyerThread = req == null ? null : w.Dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_requirement", ConditionOperator.Equal, req.Id,
                                                               "gc_side", ConditionOperator.Equal, DeskChoice.Side.Buyer).FirstOrDefault();
            if (buyerThread != null)
            {
                var text = "Thank you for confirming. Please find attached our sales contract for " + Num(qty) + " " + unit + " " + commodity + " at " + currency + " " + Num(buyerPrice) +
                           " per " + unit + ".\n\nKindly sign and return a scanned copy by reply to this email. For our compliance file, please also send your company registration certificate, GST and IEC (or the equivalent in your country) and the name of the authorised signatory.";
                drafts.Add(Draft(w, buyerThread.Id, ReplyAddress(dv, buyerThread.Id), ReplySubject(dv, buyerThread.Id), text, new[] { salesDoc }, "draft_sales_contract").ToString());
            }
            var sellerThread = w.Dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_deal", ConditionOperator.Equal, deal.Id,
                                          "gc_side", ConditionOperator.Equal, DeskChoice.Side.Seller).FirstOrDefault();
            // A lot deal has no enquiry thread of its own: the purchase contract goes to the lot's thread, next to any other winner's contract.
            var lotThread = sellerThread == null && H(deal, "gc_sellerlot") != null ? H(dv.Retrieve("gc_sellerlot", H(deal, "gc_sellerlot").Value, "gc_sellerthread"), "gc_sellerthread") : null;
            if (lotThread != null) sellerThread = new Entity("gc_conversation", lotThread.Value);
            if (sellerThread != null)
            {
                var text = "Further to your confirmation, please find attached our purchase contract for " + Num(qty) + " " + unit + " " + commodity + " at " + currency + " " + Num(sellerPrice) +
                           " per " + unit + ".\n\nKindly sign and return a scanned copy by reply to this email, together with your company registration certificate, export licence (where required) and the name of the authorised signatory.";
                drafts.Add(Draft(w, sellerThread.Id, ReplyAddress(dv, sellerThread.Id) ?? (seller == null ? null : FirstEmail(dv, seller.Id)), ReplySubject(dv, sellerThread.Id), text,
                                 new[] { purchaseDoc }, "draft_purchase_contract", false, lotThread == null).ToString());
            }
            if (req != null)
            {
                var r = new Entity("gc_buyerrequirement", req.Id);
                r["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.ContractSent);
                w.Update(r, "requirement_contract_sent");
            }
            BriefWithDrafts(w, "Contracts ready to send: " + deal.GetAttributeValue<string>("gc_name"),
                            summary + "\n\nBoth PDFs are attached here for your check and to the drafts in each thread (buyer: sales contract at our price; " +
                            "seller: purchase contract at their price). When a signed copy comes back, the desk opens a task to confirm it.",
                            drafts.Select(d => Guid.Parse((string)d)), new[] { salesDoc, purchaseDoc });
            return J.Obj("status", "Created", "documents", 2, "drafts", drafts);
        }

        /// <summary>
        /// The deal of a seller who quoted was closed because the buyer's requirement is covered by another seller:
        /// a short, polite "not this time" draft in that seller's thread (no price, no buyer, no reason beyond "closed").
        /// Sellers who never quoted or who declined get nothing. Returns the draft id, or null when no note is due.
        /// </summary>
        public static Guid? RegretNote(DeskWriter w, Guid dealId, string why = "Our buyer has now closed this requirement, so we will not proceed this time")
        {
            var dv = w.Dv;
            var deal = dv.Retrieve("gc_deal", dealId, "gc_emaildesk", "gc_requirement", "gc_seller");
            if (deal == null || !(deal.GetAttributeValue<bool?>("gc_emaildesk") ?? false)) return null;
            var sellerId = H(deal, "gc_seller");
            var quoted = dv.Query("gc_offer", new[] { "gc_offerid", "gc_fromparty" }, 20, "gc_deal", ConditionOperator.Equal, dealId)
                           .Any(o => sellerId != null && H(o, "gc_fromparty") == sellerId);
            if (!quoted) return null;
            var thread = dv.Query("gc_conversation", new[] { "gc_conversationid" }, 1, "gc_deal", ConditionOperator.Equal, dealId,
                                  "gc_side", ConditionOperator.Equal, DeskChoice.Side.Seller).FirstOrDefault();
            if (thread == null) return null;
            var to = ReplyAddress(dv, thread.Id) ?? (sellerId == null ? null : FirstEmail(dv, sellerId.Value));
            if (string.IsNullOrWhiteSpace(to)) return null;
            var reqId = H(deal, "gc_requirement");
            var req = reqId == null ? null : dv.Retrieve("gc_buyerrequirement", reqId.Value, "gc_commoditytext", "gc_name");
            var commodity = req == null ? "this material" : (req.GetAttributeValue<string>("gc_commoditytext") ?? req.GetAttributeValue<string>("gc_name") ?? "this material");
            var text = Greet("Dear Sir,", ContactName(dv, thread.Id)) + "\n\n" +
                       "Thanks for your offer and the quick response on " + commodity + ". " + why + ".\n\n" +
                       "We will come back to you with our next enquiry for " + commodity + ".";
            return Draft(w, thread.Id, to, ReplySubject(dv, thread.Id), text, null, "draft_regret_note", AutoSend(dv, "reply", text));
        }

        private static string FirstEmail(Dv dv, Guid accountId)
        {
            var a = dv.Retrieve("account", accountId, "emailaddress1");
            return a == null ? null : a.GetAttributeValue<string>("emailaddress1");
        }

        private static Guid Document(DeskWriter w, Guid dealId, string fileName, byte[] pdf)
        {
            string sha;
            using (var h = SHA256.Create()) sha = string.Concat(h.ComputeHash(pdf).Select(b => b.ToString("x2")));
            var doc = new Entity("gc_document");
            doc["gc_name"] = Cut(fileName, 200);
            doc["gc_filename"] = fileName;
            doc["gc_mimetype"] = "application/pdf";
            doc["gc_sizebytes"] = pdf.Length;
            doc["gc_sha256"] = sha;
            doc["gc_parsestatus"] = new OptionSetValue(DeskChoice.DocumentParsed);
            doc["gc_deal"] = new EntityReference("gc_deal", dealId);
            var id = w.Create(doc, "create_contract_document", J.Obj("file", fileName));
            if (!w.DryRun) MailFiles.Upload(w.Dv.Svc, new EntityReference("gc_document", id), "gc_file", fileName, "application/pdf", pdf);
            return id;
        }

        // ---------------------------------------------------------------- helpers

        public static void Task(DeskWriter w, string title, Dictionary<string, object> payload, Guid? dealId)
        {
            var open = w.Dv.Query("gc_reviewtask", new[] { "gc_reviewtaskid" }, 1, "gc_name", ConditionOperator.Equal, Cut(title, 100),
                                  "gc_status", ConditionOperator.Equal, Choice.ReviewStatus.Open).FirstOrDefault();
            if (open != null) return;
            var t = new Entity("gc_reviewtask");
            t["gc_name"] = Cut(title, 100);
            t["gc_kind"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewKinds, "Review"));
            t["gc_purpose"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewPurposes, "Other"));
            t["gc_assigneerole"] = new OptionSetValue(Choice.ValueOf(Choice.AdminRoles, "Deal Manager"));
            t["gc_status"] = new OptionSetValue(Choice.ReviewStatus.Open);
            t["gc_payload"] = Cut(Json.Serialize(payload), 100000);
            if (dealId != null) t["gc_deal"] = new EntityReference("gc_deal", dealId.Value);
            w.Create(t, "create_review_task", J.Obj("title", title));
        }

        public static string UnitLabel(OptionSetValue v) { return Label(DeskChoice.Units, v) ?? "MT"; }

        public static string Label(string[] labels, OptionSetValue v)
        {
            if (v == null) return null;
            var i = v.Value - Choice.Base;
            return i >= 0 && i < labels.Length ? labels[i] : null;
        }

        public static string Num(decimal d) { return d.ToString(d == Math.Floor(d) ? "#,0" : "#,0.##", CultureInfo.InvariantCulture); }

        public static Guid? H(Entity e, string column)
        {
            var r = e == null ? null : e.GetAttributeValue<EntityReference>(column);
            return r == null ? (Guid?)null : r.Id;
        }

        public static string Cut(string s, int max) { return s == null ? null : s.Length <= max ? s : s.Substring(0, max); }
    }

    /// <summary>File column upload (Dataverse block upload messages).</summary>
    public static class MailFiles
    {
        public static void Upload(IOrganizationService svc, EntityReference target, string column, string fileName, string mime, byte[] bytes)
        {
            var init = new OrganizationRequest("InitializeFileBlocksUpload");
            init["Target"] = target;
            init["FileAttributeName"] = column;
            init["FileName"] = fileName;
            var token = (string)svc.Execute(init).Results["FileContinuationToken"];
            var blocks = new List<string>();
            const int size = 4 * 1024 * 1024;
            for (var offset = 0; offset < bytes.Length; offset += size)
            {
                var chunk = new byte[Math.Min(size, bytes.Length - offset)];
                Buffer.BlockCopy(bytes, offset, chunk, 0, chunk.Length);
                var blockId = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                var up = new OrganizationRequest("UploadBlock");
                up["BlockId"] = blockId;
                up["BlockData"] = chunk;
                up["FileContinuationToken"] = token;
                svc.Execute(up);
                blocks.Add(blockId);
            }
            var commit = new OrganizationRequest("CommitFileBlocksUpload");
            commit["FileName"] = fileName;
            commit["MimeType"] = mime;
            commit["BlockList"] = blocks.ToArray();
            commit["FileContinuationToken"] = token;
            svc.Execute(commit);
        }

        public static byte[] Download(IOrganizationService svc, EntityReference target, string column)
        {
            var init = new OrganizationRequest("InitializeFileBlocksDownload");
            init["Target"] = target;
            init["FileAttributeName"] = column;
            var r = svc.Execute(init);
            var token = (string)r.Results["FileContinuationToken"];
            var size = Convert.ToInt64(r.Results["FileSizeInBytes"]);
            using (var ms = new System.IO.MemoryStream())
            {
                long offset = 0;
                while (offset < size)
                {
                    var dl = new OrganizationRequest("DownloadBlock");
                    dl["FileContinuationToken"] = token;
                    dl["Offset"] = offset;
                    dl["BlockLength"] = Math.Min(4L * 1024 * 1024, size - offset);
                    var data = (byte[])svc.Execute(dl).Results["Data"];
                    if (data == null || data.Length == 0) break;
                    ms.Write(data, 0, data.Length);
                    offset += data.Length;
                }
                return ms.ToArray();
            }
        }
    }
}

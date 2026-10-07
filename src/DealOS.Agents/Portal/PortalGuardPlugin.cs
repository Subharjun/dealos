using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Portal
{
    /// <summary>Status values and allowed transitions for writes that come from the marketplace site. Pure: unit-tested offline.</summary>
    public static class PortalRules
    {
        private const int B = Choice.Base;

        public static class Listing { public const int Draft = B, Submitted = B + 1, InVerification = B + 2, NeedsInfo = B + 3, Published = B + 4, Suspended = B + 5, Withdrawn = B + 6, SoldOut = B + 7; }
        public static class Rfq { public const int Draft = B, Open = B + 1, Matched = B + 2, Fulfilled = B + 3, Expired = B + 4, Withdrawn = B + 5; }
        public static class Invite { public const int Invited = B, Viewed = B + 1, Accepted = B + 2, Declined = B + 3, Expired = B + 4, Withdrawn = B + 5; }
        public static class Stage { public const int Signed = B + 5, Funded = B + 7, Delivered = B + 9, Settled = B + 10, Closed = B + 11; }
        public static class Dispute { public const int Open = B, Withdrawn = B + 4; }

        /// <summary>Listing status changes a seller may make from the site: submit for verification, or withdraw.</summary>
        public static bool ListingMove(int from, int to)
        {
            if (from == to) return true;
            if (to == Listing.Submitted) return from == Listing.Draft || from == Listing.NeedsInfo;
            if (to == Listing.Withdrawn) return from != Listing.SoldOut && from != Listing.Withdrawn;
            return false;
        }

        /// <summary>Other listing fields are editable only before verification starts (or when verification asked for more).</summary>
        public static bool ListingEditable(int status) { return status == Listing.Draft || status == Listing.NeedsInfo; }

        public static bool RfqMove(int from, int to)
        {
            if (from == to) return true;
            if (to == Rfq.Open) return from == Rfq.Draft;
            if (to == Rfq.Withdrawn) return from == Rfq.Draft || from == Rfq.Open || from == Rfq.Matched;
            return false;
        }

        public static bool InviteMoveBySeller(int from, int to)
        {
            if (from == to) return true;
            if (to == Invite.Viewed) return from == Invite.Invited;
            if (to == Invite.Accepted || to == Invite.Declined) return from == Invite.Invited || from == Invite.Viewed;
            return false;
        }

        public static bool InviteMoveByBuyer(int from, int to)
        {
            return from == to || (to == Invite.Withdrawn && (from == Invite.Invited || from == Invite.Viewed));
        }

        /// <summary>Roles a company may give itself; Service Partner and Platform are set by staff.</summary>
        public static readonly int[] SelfServiceRoles = { B, B + 1, B + 2 };

        /// <summary>Ownership lookups the guard sets on create; the site may send them only with its own company / contact.</summary>
        public static readonly Dictionary<string, string[]> OwnerLinks = new Dictionary<string, string[]>
        {
            { "account", new[] { "primarycontactid" } },
            { "gc_listing", new[] { "gc_seller" } },
            { "gc_buyerrequirement", new[] { "gc_buyer" } },
            { "gc_document", new[] { "gc_account", "gc_uploadedby" } },
        };

        /// <summary>Columns the site may never write, per table (staff, agents and flows own them).</summary>
        public static readonly Dictionary<string, string[]> Protected = new Dictionary<string, string[]>
        {
            { "account", new[] { "gc_kybstatus", "gc_trusttier", "gc_compliancehold", "primarycontactid", "parentaccountid" } },
            { "contact", new[] { "gc_pepstatus", "gc_isubo", "gc_ownershippct" } },
            { "gc_listing", new[] { "gc_seller", "gc_badge", "gc_publishedon", "gc_asset" } },
            { "gc_buyerrequirement", new[] { "gc_buyer" } },
            { "gc_document", new[] { "gc_account", "gc_uploadedby", "gc_parsestatus", "gc_provenanceclass", "gc_riskflags", "gc_doctypeconfidence",
                                     "gc_doctypemethod", "gc_issuer", "gc_duplicateof", "gc_ocrconfidence", "gc_language", "gc_pagecount" } },
        };
    }

    /// <summary>
    /// Synchronous pre-operation guard for every write that arrives through the Power Pages site (IsPortalsClientCall).
    /// Table permissions decide which rows a site user can see; this guard decides what a write may do: it forces ownership
    /// to the signed-in contact's company, refuses protected columns, and allows only the status changes a party may make.
    /// Staff, flows and agents (not portal calls) are unaffected.
    /// </summary>
    public sealed class PortalGuardPlugin : IPlugin
    {
        private const int B = Choice.Base;

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var portal = context as IPluginExecutionContext2;
            if (portal == null || !portal.IsPortalsClientCall) return;
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(null);

            var table = context.PrimaryEntityName;
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            trace.Trace("DealOS portal guard: {0} {1} by contact {2}", context.MessageName, table, portal.PortalsContactId);
            // Sign-up and sign-in: Power Pages maintains the contact itself before a portal contact id exists.
            if (table == "contact" && (context.MessageName == "Create" || portal.PortalsContactId == Guid.Empty)) return;
            if (portal.PortalsContactId == Guid.Empty) Deny("Sign in to make changes.");
            var g = new Guard(svc, portal.PortalsContactId);
            var target = context.InputParameters.Contains("Target") ? context.InputParameters["Target"] as Entity : null;
            var targetRef = context.InputParameters.Contains("Target") ? context.InputParameters["Target"] as EntityReference : null;
            var message = context.MessageName;

            if (message == "Delete")
            {
                g.Delete(table, targetRef == null ? Guid.Empty : targetRef.Id);
                return;
            }
            if (target == null || (message != "Create" && message != "Update")) return;
            var create = message == "Create";
            if (create) g.DropOwnLinks(table, target);
            string[] blocked;
            if (PortalRules.Protected.TryGetValue(table, out blocked))
            {
                // The guard sets ownership itself on create (below), so any other caller-supplied value is refused.
                var hit = blocked.FirstOrDefault(target.Attributes.Contains);
                if (hit != null) Deny("The field '" + hit + "' is managed by DealOS and can't be changed from the site.");
            }
            switch (table)
            {
                case "contact": if (!create) g.Contact(target); break;
                case "account": if (create) g.NewAccount(target); else g.Account(target); break;
                case "gc_listing": if (create) g.NewListing(target); else g.Listing(target); break;
                case "gc_buyerrequirement": if (create) g.NewRfq(target); else g.Rfq(target); break;
                case "gc_rfqinvite": if (create) g.NewInvite(target); else g.Invite(target); break;
                case "gc_match": if (create) Deny("Matches are proposed by DealOS."); g.Match(target); break;
                case "gc_conversation": if (create) g.NewConversation(target); else Deny("Conversations can't be edited."); break;
                case "gc_message": if (create) g.NewMessage(target); else Deny("Messages can't be edited."); break;
                case "gc_document": if (create) g.NewDocument(target); else g.Document(target); break;
                case "gc_rating": if (create) g.NewRating(target); else Deny("Ratings can't be edited."); break;
                case "gc_dispute": if (create) g.NewDispute(target); else g.Dispute(target); break;
                default: Deny("The site can't change " + table + " records."); break;
            }
        }

        internal static void Deny(string reason) { throw new InvalidPluginExecutionException(reason); }

        private sealed class Guard
        {
            private readonly IOrganizationService _svc;
            private readonly Guid _contact;
            private readonly Guid? _account;

            public Guard(IOrganizationService svc, Guid contact)
            {
                _svc = svc;
                _contact = contact;
                var c = Get("contact", contact, "parentcustomerid");
                if (c == null) Deny("Your contact record was not found.");
                var parent = c.GetAttributeValue<EntityReference>("parentcustomerid");
                _account = parent != null && parent.LogicalName == "account" ? parent.Id : (Guid?)null;
            }

            /// <summary>On create, a site may send its own company / contact as the owner lookup (table permissions need it);
            /// those values are dropped here and set again by the guard. Any other value stays and is refused as protected.</summary>
            public void DropOwnLinks(string table, Entity t)
            {
                foreach (var column in PortalRules.OwnerLinks.ContainsKey(table) ? PortalRules.OwnerLinks[table] : new string[0])
                {
                    var r = t.GetAttributeValue<EntityReference>(column);
                    if (r == null) continue;
                    var own = (r.LogicalName == "account" && _account == r.Id) || (r.LogicalName == "contact" && r.Id == _contact);
                    if (own) t.Attributes.Remove(column);
                }
            }

            private Guid Account()
            {
                if (_account == null) Deny("Complete your company profile first.");
                return _account.Value;
            }

            // ---- parties ----

            public void Contact(Entity t)
            {
                if (t.Id != _contact) Deny("You can only change your own profile.");
                if (!t.Attributes.Contains("parentcustomerid")) return;
                var to = t.GetAttributeValue<EntityReference>("parentcustomerid");
                if (_account != null) Deny("Your sign-in already belongs to a company; contact DealOS to move it.");
                if (to == null || to.LogicalName != "account") Deny("Choose the company you created.");
                var acc = Get("account", to.Id, "primarycontactid");
                if (acc == null || Ref(acc, "primarycontactid") != _contact) Deny("You can only join a company you created. Ask DealOS to add you to an existing one.");
            }

            public void NewAccount(Entity t)
            {
                if (_account != null) Deny("Your sign-in already belongs to a company.");
                Roles(t);
                t["primarycontactid"] = new EntityReference("contact", _contact);
                t["gc_kybstatus"] = new OptionSetValue(B);       // Not Started: onboarding flow starts KYB
                t["gc_trusttier"] = new OptionSetValue(B);       // Unverified
                t["gc_compliancehold"] = false;
            }

            public void Account(Entity t)
            {
                var acc = Get("account", t.Id, "primarycontactid");
                if (acc == null) Deny("Company not found.");
                var own = _account == t.Id || (_account == null && Ref(acc, "primarycontactid") == _contact);
                if (!own) Deny("You can only change your own company.");
                Roles(t);
            }

            private static void Roles(Entity t)
            {
                var roles = t.GetAttributeValue<OptionSetValueCollection>("gc_partyrole");
                if (roles != null && roles.Any(r => !PortalRules.SelfServiceRoles.Contains(r.Value)))
                    Deny("Choose Buyer, Seller or Intermediary; other roles are set by DealOS.");
            }

            // ---- supply ----

            public void NewListing(Entity t)
            {
                t["gc_seller"] = new EntityReference("account", Account());
                t["gc_status"] = new OptionSetValue(PortalRules.Listing.Draft);
            }

            public void Listing(Entity t)
            {
                var l = Get("gc_listing", t.Id, "gc_seller", "gc_status");
                if (l == null || Ref(l, "gc_seller") != Account()) Deny("You can only change your own listings.");
                var from = Opt(l, "gc_status");
                var to = t.Contains("gc_status") ? Opt(t, "gc_status") : from;
                if (!PortalRules.ListingMove(from, to)) Deny("A listing can only be submitted for verification or withdrawn from the site.");
                var others = t.Attributes.Keys.Where(k => k != "gc_status" && k != "gc_listingid" && !k.StartsWith("modified", StringComparison.Ordinal)).ToList();
                if (others.Count > 0 && !PortalRules.ListingEditable(from)) Deny("This listing is in verification or published; withdraw it or ask DealOS to change it.");
            }

            public void NewDocument(Entity t)
            {
                var account = Account();
                var listing = t.GetAttributeValue<EntityReference>("gc_listing");
                if (listing != null)
                {
                    var l = Get("gc_listing", listing.Id, "gc_seller");
                    if (l == null || Ref(l, "gc_seller") != account) Deny("You can only upload documents to your own listings.");
                }
                var deal = t.GetAttributeValue<EntityReference>("gc_deal");
                if (deal != null) OwnDeal(deal.Id);
                t["gc_account"] = new EntityReference("account", account);
                t["gc_uploadedby"] = new EntityReference("contact", _contact);
                t["gc_parsestatus"] = new OptionSetValue(B);          // Pending: Document intake runs Document Intelligence
                t["gc_provenanceclass"] = new OptionSetValue(B + 1);  // Counterparty Document: never more than Documented
            }

            public void Document(Entity t)
            {
                var d = Get("gc_document", t.Id, "gc_account", "gc_parsestatus");
                if (d == null || Ref(d, "gc_account") != Account()) Deny("You can only change your own documents.");
                var fileOnly = t.Attributes.Keys.All(k => k == "gc_documentid" || k == "gc_file" || k == "gc_filename" || k == "gc_mimetype" || k == "gc_sizebytes"
                                                          || k == "gc_name" || k == "gc_doctype" || k.StartsWith("modified", StringComparison.Ordinal));
                if (!fileOnly) Deny("Only the file, name and type of a document can be changed.");
                if (Opt(d, "gc_parsestatus") != B) Deny("This document has already been processed; upload a new one instead.");
            }

            // ---- demand ----

            public void NewRfq(Entity t)
            {
                t["gc_buyer"] = new EntityReference("account", Account());
                t["gc_status"] = new OptionSetValue(PortalRules.Rfq.Draft);
            }

            public void Rfq(Entity t)
            {
                var r = Get("gc_buyerrequirement", t.Id, "gc_buyer", "gc_status");
                if (r == null || Ref(r, "gc_buyer") != Account()) Deny("You can only change your own RFQs.");
                var from = Opt(r, "gc_status");
                var to = t.Contains("gc_status") ? Opt(t, "gc_status") : from;
                if (!PortalRules.RfqMove(from, to)) Deny("An RFQ can only be sent (opened) or withdrawn from the site.");
                var others = t.Attributes.Keys.Where(k => k != "gc_status" && k != "gc_buyerrequirementid" && !k.StartsWith("modified", StringComparison.Ordinal)).ToList();
                if (others.Count > 0 && from != PortalRules.Rfq.Draft) Deny("Only draft RFQs can be edited; withdraw it and post a new one.");
            }

            public void NewInvite(Entity t)
            {
                var account = Account();
                var req = t.GetAttributeValue<EntityReference>("gc_requirement");
                var lst = t.GetAttributeValue<EntityReference>("gc_listing");
                if (req == null || lst == null) Deny("An invite needs an RFQ and a listing.");
                var r = Get("gc_buyerrequirement", req.Id, "gc_buyer", "gc_status");
                if (r == null || Ref(r, "gc_buyer") != account) Deny("You can only invite sellers to your own RFQs.");
                if (Opt(r, "gc_status") != PortalRules.Rfq.Open) Deny("Send (open) the RFQ before inviting sellers.");
                var l = Get("gc_listing", lst.Id, "gc_seller", "gc_status");
                if (l == null || Opt(l, "gc_status") != PortalRules.Listing.Published) Deny("Only published listings can be invited.");
                if (Ref(l, "gc_seller") == account) Deny("You can't invite your own listing.");
                var dup = _svc.RetrieveMultiple(new QueryExpression("gc_rfqinvite")
                {
                    ColumnSet = new ColumnSet(false),
                    TopCount = 1,
                    Criteria = { Conditions = { new ConditionExpression("gc_requirement", ConditionOperator.Equal, req.Id), new ConditionExpression("gc_listing", ConditionOperator.Equal, lst.Id) } }
                }).Entities.Any();
                if (dup) Deny("This listing is already invited to that RFQ.");
                t["gc_status"] = new OptionSetValue(PortalRules.Invite.Invited);
                t["gc_seller"] = new EntityReference("account", Ref(l, "gc_seller").Value);
                t["gc_invitedon"] = DateTime.UtcNow;
                t.Attributes.Remove("gc_deal");
                t.Attributes.Remove("gc_respondedon");
            }

            public void Invite(Entity t)
            {
                var account = Account();
                var i = Get("gc_rfqinvite", t.Id, "gc_seller", "gc_requirement", "gc_status");
                if (i == null) Deny("Invite not found.");
                var from = Opt(i, "gc_status");
                var to = t.Contains("gc_status") ? Opt(t, "gc_status") : from;
                var fields = t.Attributes.Keys.Where(k => k != "gc_rfqinviteid" && !k.StartsWith("modified", StringComparison.Ordinal)).ToList();
                if (Ref(i, "gc_seller") == account)
                {
                    if (fields.Any(k => k != "gc_status" && k != "gc_declinereason")) Deny("Sellers can only accept or decline an invite.");
                    if (!PortalRules.InviteMoveBySeller(from, to)) Deny("This invite can no longer be answered.");
                    return;
                }
                var r = Get("gc_buyerrequirement", Ref(i, "gc_requirement") ?? Guid.Empty, "gc_buyer");
                if (r != null && Ref(r, "gc_buyer") == account)
                {
                    if (fields.Any(k => k != "gc_status" && k != "gc_buyernote")) Deny("Buyers can only withdraw an invite or edit the note.");
                    if (!PortalRules.InviteMoveByBuyer(from, to)) Deny("This invite can no longer be withdrawn.");
                    return;
                }
                Deny("You can only change your own invites.");
            }

            public void Match(Entity t)
            {
                var account = Account();
                var m = Get("gc_match", t.Id, "gc_requirement", "gc_listing");
                if (m == null) Deny("Match not found.");
                var fields = t.Attributes.Keys.Where(k => k != "gc_matchid" && !k.StartsWith("modified", StringComparison.Ordinal)).ToList();
                var r = Get("gc_buyerrequirement", Ref(m, "gc_requirement") ?? Guid.Empty, "gc_buyer");
                var l = Get("gc_listing", Ref(m, "gc_listing") ?? Guid.Empty, "gc_seller");
                string allowed = null;
                if (r != null && Ref(r, "gc_buyer") == account) allowed = "gc_buyeroptin";
                else if (l != null && Ref(l, "gc_seller") == account) allowed = "gc_selleroptin";
                if (allowed == null) Deny("You can only respond to your own matches.");
                if (fields.Any(k => k != allowed) || t.GetAttributeValue<bool?>(allowed) != true) Deny("You can only opt in to a match.");
            }

            // ---- conversation ----

            public void NewConversation(Entity t)
            {
                var assistant = t.GetAttributeValue<OptionSetValue>("gc_assistant");
                foreach (var k in t.Attributes.Keys.ToList()) if (k != "gc_name" && k != "gc_assistant") t.Attributes.Remove(k);
                if (assistant != null && assistant.Value != B && assistant.Value != B + 1) Deny("Unknown assistant.");
                t["gc_counterparty"] = new EntityReference("account", Account());
                t["gc_contact"] = new EntityReference("contact", _contact);
                t["gc_channel"] = new OptionSetValue(B); // Portal
                if (!t.Contains("gc_name")) t["gc_name"] = "Website chat " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
            }

            public void NewMessage(Entity t)
            {
                var conv = t.GetAttributeValue<EntityReference>("gc_conversation");
                if (conv == null) Deny("A message needs a conversation.");
                var c = Get("gc_conversation", conv.Id, "gc_counterparty");
                if (c == null || Ref(c, "gc_counterparty") != Account()) Deny("You can only write in your own conversations.");
                var text = t.GetAttributeValue<string>("gc_text");
                if (string.IsNullOrWhiteSpace(text)) Deny("The message is empty.");
                if (text.Length > 4000) Deny("Messages are limited to 4,000 characters.");
                foreach (var k in t.Attributes.Keys.ToList()) if (k != "gc_text" && k != "gc_conversation" && k != "gc_name") t.Attributes.Remove(k);
                t["gc_isplatform"] = false;
                t["gc_sendercontact"] = new EntityReference("contact", _contact);
                t["gc_senton"] = DateTime.UtcNow;
                if (!t.Contains("gc_name")) t["gc_name"] = text.Length <= 100 ? text : text.Substring(0, 100);
            }

            // ---- after the trade ----

            public void NewRating(Entity t)
            {
                var account = Account();
                var deal = t.GetAttributeValue<EntityReference>("gc_deal");
                if (deal == null) Deny("A rating needs a deal.");
                var d = OwnDeal(deal.Id);
                var stage = Opt(d, "gc_stage");
                if (stage != PortalRules.Stage.Settled && stage != PortalRules.Stage.Closed) Deny("You can rate the other party once the deal is settled.");
                var score = t.GetAttributeValue<int?>("gc_score");
                if (score == null || score < 1 || score > 5) Deny("The score must be 1 to 5.");
                var other = Ref(d, "gc_buyer") == account ? Ref(d, "gc_seller") : Ref(d, "gc_buyer");
                if (other == null) Deny("This deal has no counterparty to rate.");
                var dup = _svc.RetrieveMultiple(new QueryExpression("gc_rating")
                {
                    ColumnSet = new ColumnSet(false),
                    TopCount = 1,
                    Criteria = { Conditions = { new ConditionExpression("gc_deal", ConditionOperator.Equal, deal.Id), new ConditionExpression("gc_rater", ConditionOperator.Equal, account) } }
                }).Entities.Any();
                if (dup) Deny("You have already rated this deal.");
                t["gc_rater"] = new EntityReference("account", account);
                t["gc_ratee"] = new EntityReference("account", other.Value);
                t["gc_ratedon"] = DateTime.UtcNow;
            }

            public void NewDispute(Entity t)
            {
                var deal = t.GetAttributeValue<EntityReference>("gc_deal");
                if (deal == null) Deny("A dispute needs a deal.");
                var d = OwnDeal(deal.Id);
                var stage = Opt(d, "gc_stage");
                if (stage < PortalRules.Stage.Signed || stage > PortalRules.Stage.Settled) Deny("Disputes can be raised between contract signing and settlement.");
                foreach (var k in new[] { "gc_status", "gc_resolution", "gc_financialoutcome", "gc_reviewtask", "gc_resolvedon", "gc_openedon" }) t.Attributes.Remove(k);
                t["gc_raisedby"] = new EntityReference("account", Account());
                t["gc_status"] = new OptionSetValue(PortalRules.Dispute.Open);
                t["gc_openedon"] = DateTime.UtcNow;
            }

            public void Dispute(Entity t)
            {
                var x = Get("gc_dispute", t.Id, "gc_raisedby", "gc_status");
                if (x == null || Ref(x, "gc_raisedby") != Account()) Deny("You can only change disputes you raised.");
                var fields = t.Attributes.Keys.Where(k => k != "gc_disputeid" && !k.StartsWith("modified", StringComparison.Ordinal)).ToList();
                if (fields.Any(k => k != "gc_status" && k != "gc_description") || (t.Contains("gc_status") && Opt(t, "gc_status") != PortalRules.Dispute.Withdrawn))
                    Deny("From the site you can add detail to a dispute or withdraw it.");
            }

            public void Delete(string table, Guid id)
            {
                var account = Account();
                if (table == "gc_listing")
                {
                    var l = Get("gc_listing", id, "gc_seller", "gc_status");
                    if (l != null && Ref(l, "gc_seller") == account && Opt(l, "gc_status") == PortalRules.Listing.Draft) return;
                    Deny("Only your own draft listings can be deleted.");
                }
                if (table == "gc_buyerrequirement")
                {
                    var r = Get("gc_buyerrequirement", id, "gc_buyer", "gc_status");
                    if (r != null && Ref(r, "gc_buyer") == account && Opt(r, "gc_status") == PortalRules.Rfq.Draft) return;
                    Deny("Only your own draft RFQs can be deleted.");
                }
                if (table == "gc_document")
                {
                    var d = Get("gc_document", id, "gc_account", "gc_parsestatus");
                    if (d != null && Ref(d, "gc_account") == account && Opt(d, "gc_parsestatus") == B) return;
                    Deny("Only your own unprocessed documents can be deleted.");
                }
                Deny("These records can't be deleted from the site.");
            }

            private Entity OwnDeal(Guid dealId)
            {
                var d = Get("gc_deal", dealId, "gc_buyer", "gc_seller", "gc_stage");
                var account = Account();
                if (d == null || (Ref(d, "gc_buyer") != account && Ref(d, "gc_seller") != account)) Deny("You can only act on your own deals.");
                return d;
            }

            private Entity Get(string table, Guid id, params string[] columns)
            {
                return _svc.RetrieveMultiple(new QueryExpression(table)
                {
                    ColumnSet = new ColumnSet(columns),
                    TopCount = 1,
                    Criteria = { Conditions = { new ConditionExpression(table + "id", ConditionOperator.Equal, id) } }
                }).Entities.FirstOrDefault();
            }
        }

        private static Guid? Ref(Entity e, string column)
        {
            var r = e.GetAttributeValue<EntityReference>(column);
            return r == null ? (Guid?)null : r.Id;
        }

        private static int Opt(Entity e, string column)
        {
            var o = e.GetAttributeValue<OptionSetValue>(column);
            return o == null ? -1 : o.Value;
        }
    }
}

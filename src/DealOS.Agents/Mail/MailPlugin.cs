using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>
    /// Email Desk operations called by the "Mailbox sync" flow (no AI):
    ///   gc_IngestEmail(MessageJson, MailboxAddress?)  → Status, MessageId, ConversationId, Direction, NeedsTriage, Attachments, Summary
    ///       One Gmail message (users.messages.get, format=full) → gc_conversation (by Gmail thread) + gc_message.
    ///       Idempotent on the Gmail message id. Attachments that Gmail returned inline are stored at once; the others are
    ///       listed in Attachments (JSON) for the flow to fetch and pass to gc_AttachEmailFile.
    ///   gc_BuildEmailRaw(MessageId, MailboxAddress) → Raw, ThreadId, To   (a pending desk draft as a Gmail draft message)
    ///   gc_SourceRequirement(RequirementId) → Invited, Result           (source requests to matching seller leads)
    ///   gc_DeskContract(ContractId) → Result                             (back-to-back contract PDFs + drafts to both sides)
    ///   gc_DiscoverBuyers(LotId, Force?) → Found, Result                 (web search for buyers of a seller lot → leads)
    ///   gc_MarketLot(LotId) → Offered, Result                            (offer an open lot to buyers not offered it yet)
    ///   gc_AttachEmailFile(MessageId, FileName, MimeType, Data) → Status, DocumentId
    ///       Stores one attachment (base64url) as a Quarantined gc_document of the email, so Document intake does not
    ///       spend model calls on unscreened mail. The Trade Desk releases it (Pending) once the thread is genuine.
    /// </summary>
    public sealed class MailPlugin : IPlugin
    {
        private static readonly string[] Storable = { "application/pdf", "image/png", "image/jpeg", "image/jpg", "image/webp", "image/heic", "image/heif", "image/gif", "image/tiff",
            "text/plain", "text/csv", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/msword",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.ms-excel" };

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var dv = new Dv(factory.CreateOrganizationService(context.UserId), factory.CreateOrganizationService(null));
            switch (context.MessageName)
            {
                case "gc_IngestEmail":
                    Ingest(dv, trace, Get<string>(context, "MessageJson"), Get<string>(context, "MailboxAddress"), context.OutputParameters);
                    break;
                case "gc_BuildEmailRaw":
                    BuildRaw(dv, Get<Guid>(context, "MessageId"), Get<string>(context, "MailboxAddress"), context.OutputParameters);
                    break;
                case "gc_SourceRequirement":
                    var sourced = Desk.Source(new DeskWriter(dv), Get<Guid>(context, "RequirementId"));
                    context.OutputParameters["Invited"] = Convert.ToInt32(J.Get(sourced, "invited"));
                    context.OutputParameters["Result"] = Json.Serialize(sourced);
                    break;
                case "gc_DiscoverSellers":
                    var found = Discovery.Run(dv, Get<Guid>(context, "RequirementId"), Get<bool?>(context, "Force") ?? false, x => trace.Trace(x));
                    context.OutputParameters["Found"] = Convert.ToInt32(J.Get(found, "found") ?? 0);
                    context.OutputParameters["Result"] = Json.Serialize(found);
                    break;
                case "gc_DiscoverBuyers":
                    var buyers = Discovery.RunBuyers(dv, Get<Guid>(context, "LotId"), Get<bool?>(context, "Force") ?? false, x => trace.Trace(x));
                    context.OutputParameters["Found"] = Convert.ToInt32(J.Get(buyers, "found") ?? 0);
                    context.OutputParameters["Result"] = Json.Serialize(buyers);
                    break;
                case "gc_MarketLot":
                    var marketed = Lots.Market(new DeskWriter(dv), Get<Guid>(context, "LotId"));
                    context.OutputParameters["Offered"] = marketed.Count;
                    context.OutputParameters["Result"] = Json.Serialize(J.Obj("offered", marketed));
                    break;
                case "gc_DeskBrief":
                    context.OutputParameters["MessageId"] = Desk.BriefWithDrafts(new DeskWriter(dv), Get<string>(context, "Subject") ?? "Update", Get<string>(context, "Text") ?? "",
                                                                                Ids(Get<string>(context, "Drafts"))).ToString();
                    break;
                case "gc_DeskBriefTask":
                    var briefed = Approvals.BriefTask(new DeskWriter(dv), Get<Guid>(context, "TaskId"));
                    context.OutputParameters["MessageId"] = briefed == null ? "" : briefed.Value.ToString();
                    break;
                case "gc_DeskPipeline":
                    context.OutputParameters["Text"] = Approvals.Pipeline(dv);
                    break;
                case "gc_TriageCorrections":
                    var corrected = Corrections.FromHistory(new DeskWriter(dv), Get<string>(context, "HistoryJson"), Get<string>(context, "LabelMap"));
                    context.OutputParameters["Corrected"] = Convert.ToInt32(J.Get(corrected, "corrected") ?? 0);
                    context.OutputParameters["Result"] = Json.Serialize(corrected);
                    break;
                case "gc_DeskTrack":
                    context.OutputParameters["Result"] = Json.Serialize(Tracking.Run(new DeskWriter(dv), (Get<string>(context, "Kind") ?? "").Trim().ToLowerInvariant(), Get<Guid>(context, "RecordId")));
                    break;
                case "gc_EsignEnvelopes":
                    context.OutputParameters["Result"] = Json.Serialize(Esign.Envelopes(dv, Get<Guid>(context, "ContractId")));
                    break;
                case "gc_EsignRecord":
                    context.OutputParameters["Result"] = Json.Serialize(Esign.Record(new DeskWriter(dv), Get<Guid>(context, "ContractId"), Get<string>(context, "Side"), Get<string>(context, "EnvelopeId")));
                    break;
                case "gc_EsignPending":
                    context.OutputParameters["Result"] = Json.Serialize(Esign.Pending(dv));
                    break;
                case "gc_EsignUpdate":
                    context.OutputParameters["Result"] = Json.Serialize(Esign.Update(new DeskWriter(dv), Get<Guid>(context, "ContractId"), Get<string>(context, "Side"),
                                                                                     Get<string>(context, "RecipientsJson"), Get<string>(context, "SignedPdf")));
                    break;
                case "gc_DeskContract":
                    context.OutputParameters["Result"] = Json.Serialize(Desk.ContractOut(new DeskWriter(dv), Get<Guid>(context, "ContractId")));
                    break;
                case "gc_CloseLots":
                    context.OutputParameters["Result"] = Json.Serialize(Lots.CloseDue(new DeskWriter(dv)));
                    break;
                case "gc_DeskFollowUps":
                    context.OutputParameters["Result"] = Json.Serialize(FollowUps.Run(new DeskWriter(dv)));
                    break;
                case "gc_AttachEmailFile":
                    Attach(dv, Get<Guid>(context, "MessageId"), Get<string>(context, "FileName"), Get<string>(context, "MimeType"),
                           Get<string>(context, "Data"), context.OutputParameters);
                    break;
                default:
                    throw new InvalidPluginExecutionException("MailPlugin does not handle " + context.MessageName + ".");
            }
        }

        // ---------------------------------------------------------------- ingest

        private static void Ingest(Dv dv, ITracingService trace, string json, string mailbox, ParameterCollection o)
        {
            if (string.IsNullOrWhiteSpace(json)) Refuse("MessageJson is empty: pass the Gmail message (format=full).");
            GmailMessage m;
            try { m = GmailMessage.Parse(json); }
            catch (FormatException ex) { Refuse("MessageJson is not a Gmail message: " + ex.Message); return; }
            if (string.IsNullOrEmpty(m.Id) || string.IsNullOrEmpty(m.ThreadId)) Refuse("MessageJson has no id or threadId.");

            var maxBytes = (long)(dv.SettingNum("email.attachments.max_mb", 10) * 1024 * 1024);
            // The connected mailbox (passed by the flow from Gmail's profile) is always "us"; email.self_addresses adds aliases.
            var self = SelfAddresses(dv);
            if (!string.IsNullOrWhiteSpace(mailbox)) self.Add(mailbox.Trim().ToLowerInvariant());
            var outbound = m.IsSent || (m.From != null && self.Contains(m.From.Address));

            var existing = dv.Query("gc_message", new[] { "gc_messageid", "gc_conversation", "gc_direction", "gc_triage" }, 1,
                                    "gc_externalid", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, m.Id).FirstOrDefault();
            if (existing != null)
            {
                var dir = existing.GetAttributeValue<OptionSetValue>("gc_direction");
                var isOut = dir != null && dir.Value == MailChoice.Direction.Outbound;
                o["Status"] = "Exists";
                o["MessageId"] = existing.Id.ToString();
                o["ConversationId"] = existing.GetAttributeValue<EntityReference>("gc_conversation").Id.ToString();
                o["Direction"] = isOut ? "Outbound" : "Inbound";
                // A reply to a desk briefing is a command, never trade mail.
                o["NeedsTriage"] = !isOut && existing.GetAttributeValue<OptionSetValue>("gc_triage") == null &&
                                   !Approvals.IsBriefingReply(dv, existing.GetAttributeValue<EntityReference>("gc_conversation").Id, m.Subject);
                o["Attachments"] = Json.Serialize(Pending(dv, existing.Id, m, maxBytes));
                o["Summary"] = "Already stored.";
                return;
            }

            var conv = dv.Query("gc_conversation", new[] { "gc_conversationid", "gc_counterparty" }, 1,
                                "gc_gmailthreadid", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, m.ThreadId).FirstOrDefault();
            if (outbound && conv == null)
            {
                // Our own mail outside the desk's threads (personal mail in the same mailbox) is not stored.
                o["Status"] = "Skipped";
                o["MessageId"] = "";
                o["ConversationId"] = "";
                o["Direction"] = "Outbound";
                o["NeedsTriage"] = false;
                o["Attachments"] = "[]";
                o["Summary"] = "Sent mail outside the desk's threads; not stored.";
                return;
            }
            Guid convId;
            var joined = conv == null && !outbound && m.From != null ? Desk.OpenThreadFor(dv, m.From.Address, m.Subject) : null;
            if (joined != null)
            {
                // A new email from a party with an open deal joins that deal's thread (our replies stay in the original Gmail thread).
                conv = dv.Retrieve("gc_conversation", joined.Value, "gc_counterparty");
                convId = joined.Value;
            }
            else if (conv == null)
            {
                var c = new Entity("gc_conversation");
                c["gc_name"] = Cut(string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject, 200);
                c["gc_channel"] = new OptionSetValue(MailChoice.ChannelEmail);
                c["gc_gmailthreadid"] = m.ThreadId;
                convId = dv.Svc.Create(c);
            }
            else convId = conv.Id;

            // A sender we already know (contact email) links the thread to their company.
            Entity contact = null;
            if (!outbound && m.From != null)
            {
                contact = dv.Query("contact", new[] { "contactid", "parentcustomerid" }, 1, "emailaddress1", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, m.From.Address).FirstOrDefault();
                var company = contact == null ? null : contact.GetAttributeValue<EntityReference>("parentcustomerid");
                if (company != null && company.LogicalName == "account" && (conv == null || conv.GetAttributeValue<EntityReference>("gc_counterparty") == null))
                {
                    var link = new Entity("gc_conversation", convId);
                    link["gc_counterparty"] = company;
                    dv.Svc.Update(link);
                }
            }

            var signals = MailSignals.From(m);
            var headers = new Dictionary<string, object>();
            foreach (var h in GmailMessage.KeptHeaders)
            {
                var v = m.Header(h);
                if (v != null) headers[h] = Cut(v, 2000);
            }
            var meta = J.Obj(
                "gmail_id", m.Id, "thread_id", m.ThreadId, "labels", m.Labels.Cast<object>().ToList(), "snippet", m.Snippet,
                "headers", headers, "reply_to", m.ReplyTo == null ? null : m.ReplyTo.Address,
                "attachments", m.Attachments.Select(a => (object)J.Obj("file", a.FileName, "mime", a.MimeType, "size", a.Size)).ToList(),
                "signals", signals.ToJson());

            var msg = new Entity("gc_message");
            msg["gc_name"] = Cut(string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject, 200);
            msg["gc_conversation"] = new EntityReference("gc_conversation", convId);
            msg["gc_text"] = Cut(m.BodyText, 100000);
            msg["gc_externalid"] = m.Id;
            msg["gc_isplatform"] = outbound;
            msg["gc_senderlabel"] = Cut(m.From == null ? null : m.From.ToString(), 200);
            if (m.Date != null) msg["gc_senton"] = m.Date.Value;
            msg["gc_direction"] = new OptionSetValue(outbound ? MailChoice.Direction.Outbound : MailChoice.Direction.Inbound);
            msg["gc_fromaddress"] = Cut(m.From == null ? null : m.From.Address, 320);
            msg["gc_toaddresses"] = Cut(string.Join("; ", m.To.Concat(m.Cc).Select(a => a.Address)), 2000);
            msg["gc_subject"] = Cut(m.Subject, 400);
            msg["gc_emailmeta"] = Cut(Json.Serialize(meta), 100000);
            if (contact != null) msg["gc_sendercontact"] = new EntityReference("contact", contact.Id);
            var msgId = dv.Svc.Create(msg);

            // A person sent the desk's reply from Gmail: the pending draft of this thread is done.
            // Only drafts that existed when this mail was sent: a newer draft in the same thread is still waiting.
            var sentAt = m.InternalDate ?? m.Date ?? DateTime.UtcNow;
            var briefingReply = Approvals.IsBriefingReply(dv, convId, m.Subject);
            if (outbound && !briefingReply)
                foreach (var d in dv.Query("gc_message", new[] { "gc_messageid", "createdon" }, 10, "gc_conversation", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, convId,
                                           "gc_draftstatus", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, DeskChoice.DraftStatus.Pending)
                                     .Where(x => x.GetAttributeValue<DateTime>("createdon") <= sentAt.AddSeconds(5)))
                {
                    var sent = new Entity("gc_message", d.Id);
                    sent["gc_draftstatus"] = new OptionSetValue(DeskChoice.DraftStatus.Sent);
                    dv.Svc.Update(sent);
                }

            // The owner answering a briefing (APPROVE / REJECT / SEND / PIPELINE): a command, not trade mail; never triaged.
            if (briefingReply)
            {
                var command = Approvals.FromReply(new DeskWriter(dv), m.From == null ? null : m.From.Address, m.IsSent, signals.Authenticated, mailbox, m.Subject, m.BodyText);
                var mark = new Entity("gc_message", msgId);
                mark["gc_emailmeta"] = Cut(Json.Serialize(J.Obj("gmail_id", m.Id, "thread_id", m.ThreadId, "headers", headers, "command", command)), 100000);
                dv.Svc.Update(mark);
                o["Status"] = "Command";
                o["MessageId"] = msgId.ToString();
                o["ConversationId"] = convId.ToString();
                o["Direction"] = outbound ? "Outbound" : "Inbound";
                o["NeedsTriage"] = false;
                o["Attachments"] = "[]";
                o["Summary"] = "Reply to a desk briefing: " + J.Str(command, "status") + ".";
                return;
            }

            // Small attachments arrive inline; store them now so the flow only fetches the large ones.
            var stored = 0;
            foreach (var a in m.Attachments.Where(x => x.InlineData != null && Wanted(x, maxBytes)))
            {
                StoreDocument(dv, msgId, a.FileName, a.MimeType, GmailMessage.FromBase64Url(a.InlineData));
                stored++;
            }

            o["Status"] = "Created";
            o["MessageId"] = msgId.ToString();
            o["ConversationId"] = convId.ToString();
            o["Direction"] = outbound ? "Outbound" : "Inbound";
            o["NeedsTriage"] = !outbound;
            o["Attachments"] = Json.Serialize(Pending(dv, msgId, m, maxBytes));
            o["Summary"] = (outbound ? "Sent" : "Received") + " email stored" + (stored > 0 ? " with " + stored + " inline attachment(s)" : "") + (joined != null ? "; a new email from a party with an open deal, joined to that deal's thread" : "") + ".";
            trace.Trace("Ingested Gmail {0} (thread {1}) as gc_message {2}", m.Id, m.ThreadId, msgId);
        }

        /// <summary>Attachments still to fetch: wanted types and sizes, with an id, and not yet stored for this message.</summary>
        private static List<object> Pending(Dv dv, Guid msgId, GmailMessage m, long maxBytes)
        {
            var have = new HashSet<string>(dv.Query("gc_document", new[] { "gc_filename" }, 50, "gc_message", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, msgId)
                                             .Select(d => d.GetAttributeValue<string>("gc_filename") ?? ""), StringComparer.OrdinalIgnoreCase);
            return m.Attachments.Where(a => a.AttachmentId != null && a.InlineData == null && Wanted(a, maxBytes) && !have.Contains(a.FileName))
                                .Take(5)
                                .Select(a => (object)J.Obj("attachmentId", a.AttachmentId, "fileName", a.FileName, "mimeType", a.MimeType, "size", a.Size))
                                .ToList();
        }

        private static bool Wanted(MailAttachment a, long maxBytes)
        {
            return a.Size <= maxBytes && Storable.Contains((a.MimeType ?? "").ToLowerInvariant());
        }

        private static HashSet<string> SelfAddresses(Dv dv)
        {
            return new HashSet<string>((dv.Setting("email.self_addresses") ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(x => x.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- drafts

        /// <summary>A pending desk draft → the base64url RFC 2822 message for Gmail drafts.create, threaded as a reply when the thread exists.</summary>
        private static void BuildRaw(Dv dv, Guid messageId, string mailbox, ParameterCollection o)
        {
            var msg = dv.Retrieve("gc_message", messageId, "gc_conversation", "gc_toaddress", "gc_subject", "gc_text", "gc_attachments", "gc_direction");
            if (msg == null) Refuse("Draft " + messageId + " was not found.");
            var dir = msg.GetAttributeValue<OptionSetValue>("gc_direction");
            if (dir == null || dir.Value != MailChoice.Direction.Draft) Refuse("Message " + messageId + " is not a draft.");
            var to = msg.GetAttributeValue<string>("gc_toaddress");
            if (!string.IsNullOrWhiteSpace(mailbox) && to != null) to = to.Replace("{mailbox}", mailbox.Trim().ToLowerInvariant());
            if (string.IsNullOrWhiteSpace(to) || to.Contains("{")) Refuse("The draft has no recipient.");
            var convId = msg.GetAttributeValue<EntityReference>("gc_conversation").Id;
            var conv = dv.Retrieve("gc_conversation", convId, "gc_gmailthreadid");
            var threadId = conv == null ? null : conv.GetAttributeValue<string>("gc_gmailthreadid");

            string inReplyTo = null, references = null;
            if (!string.IsNullOrEmpty(threadId))
            {
                // Headers of the last stored email (drafts and briefings keep other JSON in gc_emailmeta).
                var last = dv.Query("gc_message", new[] { "gc_emailmeta", "gc_direction" }, 20, "gc_conversation", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, convId)
                             .FirstOrDefault(x => !string.IsNullOrEmpty(x.GetAttributeValue<string>("gc_emailmeta")) &&
                                                  (x.GetAttributeValue<OptionSetValue>("gc_direction") ?? new OptionSetValue(0)).Value != MailChoice.Direction.Draft);
                if (last != null)
                {
                    try
                    {
                        var headers = J.ObjOf(Json.ParseObject(last.GetAttributeValue<string>("gc_emailmeta")), "headers") ?? new Dictionary<string, object>();
                        inReplyTo = J.Str(headers, "Message-ID");
                        references = J.Str(headers, "References");
                    }
                    catch (FormatException) { }
                }
            }

            var replyTo = dv.Setting("email.reply_to");
            if (!string.IsNullOrWhiteSpace(replyTo) && !string.IsNullOrWhiteSpace(mailbox) && mailbox.Contains("@"))
            {
                var parts = mailbox.Trim().ToLowerInvariant().Split('@');
                replyTo = replyTo.Replace("{user}", parts[0]).Replace("{domain}", parts[1]);
            }
            else replyTo = null;

            var files = new List<MimeBuilder.Attachment>();
            var att = msg.GetAttributeValue<string>("gc_attachments");
            if (!string.IsNullOrWhiteSpace(att))
            {
                foreach (var idText in J.Arr(Json.ParseObject(att), "documents").OfType<string>())
                {
                    Guid docId;
                    if (!Guid.TryParse(idText, out docId)) continue;
                    var doc = dv.Retrieve("gc_document", docId, "gc_filename", "gc_mimetype");
                    if (doc == null) continue;
                    files.Add(new MimeBuilder.Attachment
                    {
                        FileName = doc.GetAttributeValue<string>("gc_filename") ?? "document.pdf",
                        MimeType = doc.GetAttributeValue<string>("gc_mimetype") ?? "application/pdf",
                        Data = MailFiles.Download(dv.Svc, new EntityReference("gc_document", docId), "gc_file")
                    });
                }
            }
            o["Raw"] = MimeBuilder.Build(to, msg.GetAttributeValue<string>("gc_subject"), msg.GetAttributeValue<string>("gc_text"), replyTo, inReplyTo, references, files);
            o["ThreadId"] = threadId ?? "";
            o["To"] = to;
        }

        // ---------------------------------------------------------------- attachments

        private static void Attach(Dv dv, Guid messageId, string fileName, string mime, string data, ParameterCollection o)
        {
            if (messageId == Guid.Empty) Refuse("MessageId is required.");
            if (string.IsNullOrWhiteSpace(fileName)) Refuse("FileName is required.");
            if (dv.Retrieve("gc_message", messageId, "gc_messageid") == null) Refuse("Email " + messageId + " was not found.");
            var existing = dv.Query("gc_document", new[] { "gc_documentid" }, 1, "gc_message", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, messageId,
                                    "gc_filename", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, fileName).FirstOrDefault();
            if (existing != null)
            {
                o["Status"] = "Exists";
                o["DocumentId"] = existing.Id.ToString();
                return;
            }
            byte[] bytes;
            try { bytes = GmailMessage.FromBase64Url(data); }
            catch (FormatException) { Refuse("Data is not base64url."); return; }
            if (bytes.Length == 0) Refuse("The attachment is empty.");
            o["Status"] = "Created";
            o["DocumentId"] = StoreDocument(dv, messageId, fileName, mime, bytes).ToString();
        }

        private static Guid StoreDocument(Dv dv, Guid messageId, string fileName, string mime, byte[] bytes)
        {
            string sha;
            using (var h = SHA256.Create()) sha = string.Concat(h.ComputeHash(bytes).Select(b => b.ToString("x2")));
            var doc = new Entity("gc_document");
            doc["gc_name"] = Cut("Email: " + fileName, 200);
            doc["gc_filename"] = Cut(fileName, 400);
            doc["gc_mimetype"] = Cut(string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime.ToLowerInvariant(), 200);
            doc["gc_sizebytes"] = bytes.Length;
            // gc_sha256 is a unique key. The same file sent again (a seller reusing one COA for several enquiries) is stored for this
            // email too, keyed by hash(file hash + email) and pointing at the first copy: gc_duplicateof is the reuse signal for the evidence checks.
            var first = dv.Query("gc_document", new[] { "gc_documentid" }, 1, "gc_sha256", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, sha).FirstOrDefault();
            if (first != null)
            {
                doc["gc_duplicateof"] = new EntityReference("gc_document", first.Id);
                using (var h = SHA256.Create())
                    sha = string.Concat(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sha + "|" + messageId.ToString("N"))).Select(b => b.ToString("x2")));
            }
            doc["gc_sha256"] = sha;
            doc["gc_parsestatus"] = new OptionSetValue(MailChoice.DocumentQuarantined);
            doc["gc_message"] = new EntityReference("gc_message", messageId);
            var id = dv.Svc.Create(doc);
            MailFiles.Upload(dv.Svc, new EntityReference("gc_document", id), "gc_file", fileName, (string)doc["gc_mimetype"], bytes);
            return id;
        }

        // ---------------------------------------------------------------- helpers

        private static T Get<T>(IPluginExecutionContext context, string name)
        {
            object v;
            return context.InputParameters.TryGetValue(name, out v) && v is T ? (T)v : default(T);
        }

        /// <summary>A JSON array of ids (as the flows pass them) → the ids; anything else → none.</summary>
        private static List<Guid> Ids(string json)
        {
            var ids = new List<Guid>();
            if (string.IsNullOrWhiteSpace(json)) return ids;
            try
            {
                foreach (var x in (Json.Parse(json) as List<object>) ?? new List<object>())
                {
                    Guid g;
                    if (x is string && Guid.TryParse((string)x, out g)) ids.Add(g);
                }
            }
            catch (FormatException) { }
            return ids;
        }

        private static string Cut(string s, int max) { return s == null ? null : s.Length <= max ? s : s.Substring(0, max); }

        private static void Refuse(string message) { throw new InvalidPluginExecutionException(message); }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>
    /// E-signature of the back-to-back contracts through DocuSign (setting contract.esign = docusign; off = scan and return by email).
    /// Order, with a person at every commitment:
    ///   1. Contract terms approved (Contract Issue) → both contract PDFs are generated, nothing is sent.
    ///   2. "Send for e-signature" approval with both PDFs attached is briefed to the owner. Nothing goes out before APPROVE.
    ///   3. Approved → gc_esignstatus Sending → the Desk e-signature flow creates one envelope per side (party signs first, then our signatory
    ///      contract.signatory countersigns) and sends it; envelope ids are recorded here.
    ///   4. Desk timers poll the envelopes; when both are completed the signed PDFs are stored and the contract becomes Signed
    ///      (Contract signed → inspection). A declined or voided envelope opens a task.
    /// Back to back: the buyer's envelope carries only the sales contract, the seller's only the purchase contract.
    /// </summary>
    public static class Esign
    {
        public static readonly string[] Statuses = { "Not Used", "Awaiting Approval", "Sending", "Sent", "Completed", "Declined", "Voided", "Failed", "Rejected" };
        private static readonly Regex AddressRx = new Regex(@"^\s*(?<name>[^<]*?)\s*<(?<email>[^>]+@[^>]+)>\s*$|^\s*(?<email2>[^\s<>]+@[^\s<>]+)\s*$", RegexOptions.Compiled);

        public static bool On(Dv dv) { return (dv.Setting("contract.esign") ?? "off").Trim().Equals("docusign", StringComparison.OrdinalIgnoreCase); }

        public static int StatusValue(string label) { return Choice.ValueOf(Statuses, label); }

        /// <summary>Our signatory from contract.signatory ("Name &lt;email&gt;"); null when not set.</summary>
        public static KeyValuePair<string, string>? Signatory(Dv dv)
        {
            var m = AddressRx.Match(dv.Setting("contract.signatory") ?? "");
            if (!m.Success) return null;
            var email = m.Groups["email"].Success && m.Groups["email"].Value.Length > 0 ? m.Groups["email"].Value : m.Groups["email2"].Value;
            var name = m.Groups["name"].Value.Trim();
            return new KeyValuePair<string, string>(name.Length > 0 ? name : Desk.CompanyName(dv), email.Trim().ToLowerInvariant());
        }

        /// <summary>Step 2: the approval to send both contracts for e-signature (with the PDFs), opened once per contract.</summary>
        public static Dictionary<string, object> RequestApproval(DeskWriter w, Guid contractId, Entity deal, Guid salesDoc, Guid purchaseDoc, string summary)
        {
            var dv = w.Dv;
            var parties = Parties(dv, deal);
            var signatory = Signatory(dv);
            var problems = new List<string>();
            if (signatory == null) problems.Add("contract.signatory is not set (our signatory, 'Name <email>')");
            if (parties["buyer"].Value == null) problems.Add("no email for the buyer's signatory");
            if (parties["seller"].Value == null) problems.Add("no email for the seller's signatory");
            var t = new Entity("gc_reviewtask");
            t["gc_name"] = Desk.Cut("Send for e-signature: " + deal.GetAttributeValue<string>("gc_name"), 100);
            t["gc_kind"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewKinds, "Approval"));
            t["gc_purpose"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewPurposes, "Other"));
            t["gc_assigneerole"] = new OptionSetValue(Choice.ValueOf(Choice.AdminRoles, "Deal Manager"));
            t["gc_status"] = new OptionSetValue(Choice.ReviewStatus.Open);
            t["gc_deal"] = new EntityReference("gc_deal", deal.Id);
            t["gc_payload"] = Json.Serialize(J.Obj(
                "action", "desk.esign_send", "contractId", contractId.ToString(), "contracts", summary,
                "buyer signs", parties["buyer"].Key + " <" + (parties["buyer"].Value ?? "email missing") + ">",
                "seller signs", parties["seller"].Key + " <" + (parties["seller"].Value ?? "email missing") + ">",
                "we countersign", signatory == null ? "not set" : signatory.Value.Key + " <" + signatory.Value.Value + ">",
                "blocking", problems.Count == 0 ? null : string.Join("; ", problems),
                "documents", new List<object> { salesDoc.ToString(), purchaseDoc.ToString() },
                "approve", "Approve = both contracts go out through DocuSign now: the buyer signs the sales contract, the seller the purchase contract, then we countersign. Check the attached PDFs first."));
            var id = w.Create(t, "esign_approval_task");
            var c = new Entity("gc_contract", contractId);
            c["gc_esignprovider"] = "DocuSign";
            c["gc_esignstatus"] = new OptionSetValue(StatusValue("Awaiting Approval"));
            w.Update(c, "esign_awaiting_approval");
            return J.Obj("status", "AwaitingEsignApproval", "task", id.ToString(), "problems", problems.Cast<object>().ToList());
        }

        /// <summary>Step 3: the envelopes to create (one per side), for the Desk e-signature flow. Refuses when a signer is missing.</summary>
        public static List<object> Envelopes(Dv dv, Guid contractId)
        {
            var contract = dv.Retrieve("gc_contract", contractId, "gc_name", "gc_deal", "gc_esignstatus");
            if (contract == null) throw new InvalidPluginExecutionException("Contract " + contractId + " was not found.");
            var deal = dv.Retrieve("gc_deal", Desk.H(contract, "gc_deal") ?? Guid.Empty, "gc_name", "gc_requirement", "gc_sellerlot", "gc_buyer", "gc_seller", "gc_dealnumber");
            if (deal == null) throw new InvalidPluginExecutionException("The contract has no deal.");
            var signatory = Signatory(dv);
            if (signatory == null) throw new InvalidPluginExecutionException("Set contract.signatory ('Name <email>') before sending contracts for e-signature.");
            var parties = Parties(dv, deal);
            var docs = dv.Query("gc_document", new[] { "gc_documentid", "gc_filename" }, 10, "gc_deal", ConditionOperator.Equal, deal.Id)
                         .Where(d => (d.GetAttributeValue<string>("gc_filename") ?? "").StartsWith("Contract-", StringComparison.OrdinalIgnoreCase) &&
                                     !(d.GetAttributeValue<string>("gc_filename") ?? "").EndsWith("-signed.pdf", StringComparison.OrdinalIgnoreCase)).ToList();
            var us = Desk.CompanyName(dv);
            var list = new List<object>();
            foreach (var side in new[] { "buyer", "seller" })
            {
                var prefix = side == "buyer" ? "Contract-Sales-" : "Contract-Purchase-";
                var doc = docs.FirstOrDefault(d => d.GetAttributeValue<string>("gc_filename").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (doc == null) throw new InvalidPluginExecutionException("The " + (side == "buyer" ? "sales" : "purchase") + " contract PDF is missing on the deal.");
                var party = parties[side];
                if (party.Value == null) throw new InvalidPluginExecutionException("No email is known for the " + side + "'s signatory.");
                var pdf = MailFiles.Download(dv.Svc, new EntityReference("gc_document", doc.Id), "gc_file");
                // In our PDFs the signature lines read "For the Seller:" and "For the Buyer:". Sales: we sell; purchase: we buy.
                var partyAnchor = side == "buyer" ? "For the Buyer:" : "For the Seller:";
                var ourAnchor = side == "buyer" ? "For the Seller:" : "For the Buyer:";
                list.Add(J.Obj(
                    "side", side,
                    "subject", Desk.Cut((side == "buyer" ? "Sales contract " : "Purchase contract ") + (deal.GetAttributeValue<string>("gc_dealnumber") ?? "") + " for signature, " + us, 100),
                    "blurb", "Please review and sign the attached contract. " + us + " countersigns after you.",
                    "document_name", doc.GetAttributeValue<string>("gc_filename"),
                    "document_base64", Convert.ToBase64String(pdf),
                    "signers", new List<object>
                    {
                        J.Obj("recipient_id", "1", "routing_order", "1", "name", party.Key, "email", party.Value, "anchor", partyAnchor),
                        J.Obj("recipient_id", "2", "routing_order", "2", "name", signatory.Value.Key, "email", signatory.Value.Value, "anchor", ourAnchor)
                    }));
            }
            return list;
        }

        /// <summary>Step 3 done for one side: the envelope id is kept on the contract.</summary>
        public static Dictionary<string, object> Record(DeskWriter w, Guid contractId, string side, string envelopeId)
        {
            var dv = w.Dv;
            var contract = dv.Retrieve("gc_contract", contractId, "gc_name", "gc_deal", "gc_envelopes");
            if (contract == null) throw new InvalidPluginExecutionException("Contract " + contractId + " was not found.");
            var env = Envelopes(contract);
            env[side] = J.Obj("id", envelopeId, "status", "sent", "on", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            var c = new Entity("gc_contract", contractId);
            c["gc_envelopes"] = Json.Serialize(env);
            c["gc_envelopeid"] = envelopeId;
            var both = env.ContainsKey("buyer") && env.ContainsKey("seller");
            if (both) c["gc_esignstatus"] = new OptionSetValue(StatusValue("Sent"));
            w.Update(c, "esign_envelope_recorded", J.Obj("side", side, "envelope", envelopeId));
            if (both)
            {
                var deal = dv.Retrieve("gc_deal", Desk.H(contract, "gc_deal") ?? Guid.Empty, "gc_name", "gc_requirement");
                var req = Desk.H(deal, "gc_requirement");
                if (req != null)
                {
                    var r = new Entity("gc_buyerrequirement", req.Value);
                    r["gc_deskstage"] = new OptionSetValue(DeskChoice.Stage.ContractSent);
                    w.Update(r, "requirement_contract_sent");
                }
                Desk.Brief(w, "Contracts out for e-signature: " + (deal == null ? contract.GetAttributeValue<string>("gc_name") : deal.GetAttributeValue<string>("gc_name")),
                           "DocuSign sent the sales contract to the buyer and the purchase contract to the seller. Our signatory countersigns after each of them. " +
                           "When both envelopes are completed the signed copies are stored on the deal and the deal moves to Signed.");
            }
            return J.Obj("status", both ? "Sent" : "Recorded", "side", side);
        }

        /// <summary>Envelopes still out for signature: [{contract, side, envelope}] for the polling flow.</summary>
        public static List<object> Pending(Dv dv)
        {
            var list = new List<object>();
            foreach (var c in dv.Query("gc_contract", new[] { "gc_contractid", "gc_envelopes" }, 50, "gc_esignstatus", ConditionOperator.Equal, StatusValue("Sent")))
                foreach (var kv in Envelopes(c))
                {
                    var e = kv.Value as Dictionary<string, object>;
                    if (e != null && J.Str(e, "status") == "sent") list.Add(J.Obj("contract", c.Id.ToString(), "side", kv.Key, "envelope", J.Str(e, "id")));
                }
            return list;
        }

        /// <summary>
        /// Step 4: the recipients' status of one envelope (DocuSign "List recipients" body) → completed (with the signed PDF, base64),
        /// declined or still out. Both sides completed → contract Signed.
        /// </summary>
        public static Dictionary<string, object> Update(DeskWriter w, Guid contractId, string side, string recipientsJson, string signedPdfBase64)
        {
            var dv = w.Dv;
            var contract = dv.Retrieve("gc_contract", contractId, "gc_name", "gc_deal", "gc_envelopes", "gc_status");
            if (contract == null) throw new InvalidPluginExecutionException("Contract " + contractId + " was not found.");
            var env = Envelopes(contract);
            var e = env.ContainsKey(side) ? env[side] as Dictionary<string, object> : null;
            if (e == null) return J.Obj("status", "UnknownSide");
            var state = RecipientsState(recipientsJson);
            if (state == "sent") return J.Obj("status", "Waiting", "side", side);
            var deal = dv.Retrieve("gc_deal", Desk.H(contract, "gc_deal") ?? Guid.Empty, "gc_name", "gc_dealnumber");
            var dealName = deal == null ? contract.GetAttributeValue<string>("gc_name") : deal.GetAttributeValue<string>("gc_name");
            e["status"] = state;
            e["on"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var c = new Entity("gc_contract", contractId);
            if (state == "completed" && deal != null && !string.IsNullOrEmpty(signedPdfBase64))
            {
                var bytes = Convert.FromBase64String(signedPdfBase64);
                var name = (side == "buyer" ? "Contract-Sales-" : "Contract-Purchase-") + (deal.GetAttributeValue<string>("gc_dealnumber") ?? deal.Id.ToString().Substring(0, 8).ToUpperInvariant()) + "-signed.pdf";
                e["document"] = StoreSigned(w, deal.Id, name, bytes).ToString();
            }
            var all = new[] { "buyer", "seller" }.All(s => env.ContainsKey(s) && J.Str(env[s] as Dictionary<string, object>, "status") == "completed");
            c["gc_envelopes"] = Json.Serialize(env);
            if (all)
            {
                c["gc_esignstatus"] = new OptionSetValue(StatusValue("Completed"));
                c["gc_status"] = new OptionSetValue(DeskChoice.Contract.Signed);
                c["gc_signedon"] = DateTime.UtcNow;
            }
            else if (state == "declined") c["gc_esignstatus"] = new OptionSetValue(StatusValue("Declined"));
            w.Update(c, "esign_status", J.Obj("side", side, "state", state, "all_signed", all));

            if (state == "declined")
                Desk.Task(w, "E-signature declined: " + dealName, J.Obj("side", side, "envelope", J.Str(e, "id"),
                          "how", "The " + side + " declined to sign in DocuSign. Read their reason in DocuSign, settle it by email, then void the other envelope and re-issue the contract."),
                          deal == null ? (Guid?)null : deal.Id);
            Desk.Brief(w, (all ? "Contracts signed: " : state == "declined" ? "E-signature declined: " : "Contract signed by the " + side + ": ") + dealName,
                       all ? "Both contracts are signed in DocuSign and stored on the deal. The deal moves to Signed and the inspection booking follows."
                           : state == "declined" ? "The " + side + " declined to sign. A task is open."
                           : "The " + side + " and our signatory have signed. Waiting for the other side.");
            return J.Obj("status", state, "side", side, "all_signed", all);
        }

        /// <summary>DocuSign recipients → "completed" (every signer completed), "declined" (any declined or voided) or "sent".</summary>
        public static string RecipientsState(string recipientsJson)
        {
            Dictionary<string, object> r;
            try { r = Json.ParseObject(string.IsNullOrWhiteSpace(recipientsJson) ? "{}" : recipientsJson); }
            catch (FormatException) { return "sent"; }
            var signers = J.Arr(r, "signers").OfType<Dictionary<string, object>>().ToList();
            if (signers.Count == 0) return "sent";
            var states = signers.Select(s => (J.Str(s, "status") ?? "").ToLowerInvariant()).ToList();
            if (states.Any(s => s == "declined" || s == "voided" || s == "autoresponded")) return "declined";
            return states.All(s => s == "completed" || s == "signed") ? "completed" : "sent";
        }

        /// <summary>Who signs for each side: the person we write to in its thread, else the company's email.</summary>
        public static Dictionary<string, KeyValuePair<string, string>> Parties(Dv dv, Entity deal)
        {
            var threads = Tracking.Threads(dv, deal);
            var result = new Dictionary<string, KeyValuePair<string, string>>();
            foreach (var side in new[] { "buyer", "seller" })
            {
                var thread = side == "buyer" ? threads.Key : threads.Value;
                var accountId = Desk.H(deal, side == "buyer" ? "gc_buyer" : "gc_seller");
                var account = accountId == null ? null : dv.Retrieve("account", accountId.Value, "name", "emailaddress1");
                var email = thread == null ? null : Desk.ReplyAddress(dv, thread.Value);
                if (string.IsNullOrWhiteSpace(email) && account != null) email = account.GetAttributeValue<string>("emailaddress1");
                var name = thread == null ? null : Desk.ContactName(dv, thread.Value);
                if (string.IsNullOrWhiteSpace(name)) name = account == null ? side : account.GetAttributeValue<string>("name");
                result[side] = new KeyValuePair<string, string>(name, string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant());
            }
            return result;
        }

        private static Dictionary<string, object> Envelopes(Entity contract)
        {
            try { return Json.ParseObject(contract.GetAttributeValue<string>("gc_envelopes") ?? "{}"); }
            catch (FormatException) { return new Dictionary<string, object>(); }
        }

        private static Guid StoreSigned(DeskWriter w, Guid dealId, string fileName, byte[] pdf)
        {
            string sha;
            using (var h = SHA256.Create()) sha = string.Concat(h.ComputeHash(pdf).Select(b => b.ToString("x2")));
            var existing = w.Dv.Query("gc_document", new[] { "gc_documentid" }, 1, "gc_sha256", ConditionOperator.Equal, sha).FirstOrDefault();
            if (existing != null) return existing.Id;
            var doc = new Entity("gc_document");
            doc["gc_name"] = Desk.Cut(fileName, 200);
            doc["gc_filename"] = fileName;
            doc["gc_mimetype"] = "application/pdf";
            doc["gc_sizebytes"] = pdf.Length;
            doc["gc_sha256"] = sha;
            doc["gc_parsestatus"] = new OptionSetValue(DeskChoice.DocumentParsed);
            doc["gc_deal"] = new EntityReference("gc_deal", dealId);
            var id = w.Create(doc, "store_signed_contract", J.Obj("file", fileName));
            if (!w.DryRun) MailFiles.Upload(w.Dv.Svc, new EntityReference("gc_document", id), "gc_file", fileName, "application/pdf", pdf);
            return id;
        }
    }
}

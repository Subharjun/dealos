using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOS.Agents;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Tools;

// Usage: dotnet run -- <output dir>
var outDir = args.Length > 0 ? args[0] : "out";
Directory.CreateDirectory(outDir);
var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

// JSON round trip
var parsed = Json.Parse("{\"a\":[1,2.5,\"x\\n\\u00e9\",true,null,{\"b\":-3e2}]}");
Check(Json.Serialize(parsed) == "{\"a\":[1,2.5,\"x\\né\",true,null,{\"b\":-300}]}", "json round trip");
Check(J.Str(Json.ParseLenient("```json\n{\"k\":\"v\"}\n```") as Dictionary<string, object>, "k") == "v", "json lenient fences");

// Number grounding
var known = new HashSet<string>();
MessageValidator.CollectNumbers("qty 500.0000 MT, price 1,250.50, purity 99.99%", known);
Check(known.SetEquals(new[] { "500", "1250.5", "99.99" }), "number normalisation");

// Validator: hallucinated number, confidential name, contact details, unhedged claim, false 'verified'
var r1 = MessageValidator.Check("500 MT of copper cathode 99.99%, 2,000 MT/month. Email john@x.com", known, new[] { "Seller Mining Ltd" }, false, new string[0], true);
Check(r1.Errors.Any(e => e.Contains("2,000")), "rejects number not in data");
Check(r1.Errors.Any(e => e.Contains("email")), "rejects contact details in market message");
var r2 = MessageValidator.Check("Seller Mining Ltd offers 500 MT", known, new[] { "Seller Mining Ltd" }, false, new string[0], true);
Check(r2.Errors.Any(e => e.Contains("confidential")), "rejects confidential term");
var r3 = MessageValidator.Check("Verified 500 MT copper cathode", known, new string[0], false, new string[0], true);
Check(r3.Errors.Any(e => e.Contains("verified")), "rejects 'verified' without a Verified fact");
var r4 = MessageValidator.Check("500 MT copper cathode available", known, new string[0], false, new[] { "500" }, true);
Check(r4.Errors.Any(e => e.Contains("Claimed")), "requires hedging for claimed values");
var r5 = MessageValidator.Check("Seller states 500 MT copper cathode, 99.99% Cu, CIF basis.", known, new[] { "Seller Mining Ltd" }, false, new[] { "500" }, true);
Check(r5.Ok, "accepts grounded, hedged teaser");

// Escrow maths (shared by the Payment agent and gc_OpenEscrow): tranches add up exactly and commission + net = gross
var plan = J.Obj("name", "1.5% seller", "rate_pct", 1.5, "payer", "Seller");
var tranchesIn = new List<Dictionary<string, object>> { J.Obj("pct", 30.0, "condition", "BL"), J.Obj("pct", 70.0, "condition", "Delivered") };
var sched = ToolCatalog.ReleaseMath(4500000.01, tranchesIn, plan);
var rows = J.Arr(sched, "tranches").OfType<Dictionary<string, object>>().ToList();
decimal Dec(Dictionary<string, object> o, string k) => Convert.ToDecimal(J.Get(o, k));
Check(rows.Sum(t => Dec(t, "gross")) == 4500000.01m, "release tranches sum to contract value");
Check(rows.Sum(t => Dec(t, "commission_deducted")) == Dec(sched, "commission_total"), "seller-paid commission fully deducted");
Check(rows.All(t => Dec(t, "commission_deducted") + Dec(t, "net_to_seller") == Dec(t, "gross")), "commission + net = gross per tranche");
var buyerPays = ToolCatalog.ReleaseMath(1000, tranchesIn, J.Obj("rate_pct", 2.0, "payer", "Buyer"));
Check(Dec(buyerPays, "buyer_commission") == 20m && J.Arr(buyerPays, "tranches").OfType<Dictionary<string, object>>().All(t => Dec(t, "commission_deducted") == 0), "buyer-paid commission not deducted from seller");
Check(Dec(ToolCatalog.ReleaseMath(1000, tranchesIn, null), "commission_total") == 0m, "no plan means zero commission");

// Chat reply guard (Buyer Concierge): seller identity and unhedged claimed values are sent back to the model
var concierge = new DealOS.Agents.Agents.BuyerConciergeAgent();
var chat = new DealOS.Agents.Runtime.AgentContext { Agent = concierge };
MessageValidator.CollectNumbers("300 MT, 9000 USD, 99.99", chat.KnownNumbers);
chat.Scratch["counterparty_terms"] = new List<string> { "Seller Mining Ltd" };
chat.Scratch["claimed_numbers"] = new HashSet<string> { "99.99" };
Check(concierge.CheckFinish(chat, J.Obj("reply", "Seller Mining Ltd has 300 MT at 9000 USD.")) != null, "chat reply: rejects seller identity");
Check(concierge.CheckFinish(chat, J.Obj("reply", "Purity 99.99% Cu, 300 MT.")) != null, "chat reply: requires hedging for claimed values");
Check(concierge.CheckFinish(chat, J.Obj("reply", "One listing: 300 MT at 9000 USD FOB; the seller states 99.99% Cu.")) == null, "chat reply: accepts grounded, masked, hedged reply");
Check(concierge.CheckFinish(chat, J.Obj("reply", "Expect about 9500 USD landed.")) != null, "chat reply: rejects invented numbers");

// Portal guard rules: what a site user may do to a status
int Bv = Choice.Base;
Check(DealOS.Agents.Portal.PortalRules.ListingMove(Bv, Bv + 1) && DealOS.Agents.Portal.PortalRules.ListingMove(Bv + 3, Bv + 1), "portal: seller can submit a draft or needs-info listing");
Check(!DealOS.Agents.Portal.PortalRules.ListingMove(Bv + 1, Bv + 4) && !DealOS.Agents.Portal.PortalRules.ListingMove(Bv, Bv + 4), "portal: seller can never publish a listing");
Check(DealOS.Agents.Portal.PortalRules.ListingMove(Bv + 4, Bv + 6) && !DealOS.Agents.Portal.PortalRules.ListingMove(Bv + 6, Bv), "portal: published listing can be withdrawn, withdrawn cannot come back");
Check(!DealOS.Agents.Portal.PortalRules.ListingEditable(Bv + 4) && DealOS.Agents.Portal.PortalRules.ListingEditable(Bv), "portal: only draft / needs-info listings are editable");
Check(DealOS.Agents.Portal.PortalRules.RfqMove(Bv, Bv + 1) && !DealOS.Agents.Portal.PortalRules.RfqMove(Bv + 1, Bv + 3), "portal: buyer can open an RFQ but not mark it fulfilled");
Check(DealOS.Agents.Portal.PortalRules.InviteMoveBySeller(Bv, Bv + 2) && !DealOS.Agents.Portal.PortalRules.InviteMoveBySeller(Bv + 3, Bv + 2), "portal: seller accepts an open invite, not a declined one");
Check(!DealOS.Agents.Portal.PortalRules.InviteMoveByBuyer(Bv, Bv + 2) && DealOS.Agents.Portal.PortalRules.InviteMoveByBuyer(Bv, Bv + 5), "portal: buyer can withdraw but not accept an invite");

// Email Desk: Gmail parsing, hard signals and the triage verdict
string B64u(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
string GmailJson(string from, string subject, string auth, string text, string extraHeaders, string attachmentPart) =>
    "{\"id\":\"m1\",\"threadId\":\"t1\",\"labelIds\":[\"INBOX\",\"UNREAD\"],\"snippet\":\"s\",\"internalDate\":\"1790000000000\",\"payload\":{\"mimeType\":\"multipart/mixed\",\"headers\":[" +
    "{\"name\":\"From\",\"value\":" + Json.Serialize(from) + "},{\"name\":\"To\",\"value\":\"\\\"Trade, Desk\\\" <desk+dealos@gmail.com>, b@x.com\"}," +
    "{\"name\":\"Subject\",\"value\":" + Json.Serialize(subject) + "},{\"name\":\"Date\",\"value\":\"Tue, 6 Oct 2026 20:59:22 +0530 (IST)\"}," +
    "{\"name\":\"Authentication-Results\",\"value\":" + Json.Serialize(auth) + "}" + extraHeaders + "]," +
    "\"parts\":[{\"mimeType\":\"multipart/alternative\",\"parts\":[{\"mimeType\":\"text/plain\",\"filename\":\"\",\"body\":{\"size\":10,\"data\":\"" + B64u(text) + "\"}}," +
    "{\"mimeType\":\"text/html\",\"filename\":\"\",\"body\":{\"size\":10,\"data\":\"" + B64u("<p>" + text + "</p>") + "</p>\"}}]}" + attachmentPart + "]}}";
var loi = DealOS.Agents.Mail.GmailMessage.Parse(GmailJson("\"Manish Kothary\" <Manish@BeneLLC.com>", "LOI: Strontium Metal 3 MT",
    "mx.google.com; dkim=pass header.i=@benellc.com; spf=pass smtp.mailfrom=benellc.com; dmarc=pass (p=NONE) header.from=benellc.com",
    "BENE LLC expresses its preliminary interest in purchasing Strontium Metal 99% min, 3 MT, CIP Mumbai Airport.\nhttps://www.benellc.com/", "",
    ",{\"mimeType\":\"application/pdf\",\"filename\":\"BENE LOI.pdf\",\"body\":{\"attachmentId\":\"ANGjdJ8\",\"size\":182044}}"));
Check(loi.From.Address == "manish@benellc.com" && loi.From.Name == "Manish Kothary" && loi.From.Domain == "benellc.com", "gmail: From name and address");
Check(loi.To.Count == 2 && loi.To[0].Name == "Trade, Desk" && loi.To[0].Address == "desk+dealos@gmail.com", "gmail: quoted comma in a display name");
Check(loi.BodyText.StartsWith("BENE LLC expresses") && loi.Date == new DateTime(2026, 10, 6, 15, 29, 22, DateTimeKind.Utc), "gmail: text body and Date header");
Check(loi.Attachments.Count == 1 && loi.Attachments[0].AttachmentId == "ANGjdJ8" && loi.Attachments[0].MimeType == "application/pdf", "gmail: attachment listed for fetching");
Check(System.Text.Encoding.UTF8.GetString(DealOS.Agents.Mail.GmailMessage.FromBase64Url(B64u("ü?>~"))) == "ü?>~", "gmail: base64url without padding");
var good = DealOS.Agents.Mail.MailSignals.From(loi);
Check(good.Spf == "pass" && good.Dkim == "pass" && good.Dmarc == "pass" && !good.AuthFailed && !good.FreeMailSender && good.SenderDomainInBody, "signals: authenticated corporate sender, own website in body");
Check(DealOS.Agents.Mail.MailSignals.Registrable("mail.tata.co.in") == "tata.co.in" && DealOS.Agents.Mail.MailSignals.Registrable("a.b.example.com") == "example.com", "signals: registrable domain");

var scamMsg = DealOS.Agents.Mail.GmailMessage.Parse(GmailJson("Procurement <buyer@gmail.com>", "URGENT purchase order",
    "mx.google.com; spf=fail smtp.mailfrom=evil.example; dmarc=fail header.from=gmail.com",
    "Please review our order and login here: https://bit.ly/x1 to confirm. Need 50000 MT Dysprosium oxide.",
    ",{\"name\":\"Reply-To\",\"value\":\"orders@evil-domain.ru\"}",
    ",{\"mimeType\":\"text/html\",\"filename\":\"Order.html\",\"body\":{\"attachmentId\":\"X\",\"size\":2000}}"));
var bad = DealOS.Agents.Mail.MailSignals.From(scamMsg);
Check(bad.Dmarc == "fail" && bad.AuthFailed && bad.FreeMailSender && bad.ReplyToMismatch && bad.ShortLinks.Contains("bit.ly") && bad.DangerousAttachments.Count == 1, "signals: phishing markers");
Check(DealOS.Agents.Mail.MailSignals.FromJson(Json.ParseObject(Json.Serialize(bad.ToJson()))).DangerousAttachments.Count == 1, "signals: round trip through the stored JSON");

var T = DealOS.Agents.Mail.TriageDecision.Decide;
var d1 = T("Buyer Requirement", 82, good, false, false, false, 65, 30);
Check(d1.Verdict == "Genuine" && d1.Label == "DealOS/Buyer", "triage: specific, authenticated buyer requirement is Genuine");
var d2 = T("Buyer Requirement", 95, bad, false, false, false, 65, 30);
Check(d2.Verdict == "Ignored" && d2.Score <= 10, "triage: hard signals override a high model score");
Check(T("Offer To Sell", 50, good, false, false, false, 65, 30).Verdict == "Review", "triage: middle score waits for a person");
Check(T("Vendor Pitch", 90, good, false, false, false, 65, 30).Verdict == "Ignored", "triage: vendor pitch is ignored");
Check(T("Not Trade", 5, good, true, false, false, 65, 30).Verdict == "Review", "triage: a known sender is never ignored");
Check(T("Scam", 5, bad, false, true, false, 65, 30).Verdict == "Ignored", "triage: dangerous attachment ignored even in our thread");
Check(T("Buyer Requirement", 90, good, false, false, true, 65, 30).Verdict == "Review", "triage: prompt injection is never Genuine");
Check(T("Offer To Sell", 70, good, false, false, false, 65, 30).Label == "DealOS/Seller", "triage: genuine seller offer label");

// Email Desk phases 2-4: margin maths, lead matching, outgoing mail, contract PDF
Check(DealOS.Agents.Mail.Desk.PriceToBuyer(1000m, 3) == 1030m && DealOS.Agents.Mail.Desk.PriceToBuyer(8765.43m, 2.5) == 8984.57m, "desk: price to buyer = seller price + margin");
Check(DealOS.Agents.Mail.Desk.BidToSeller(1030m, 3) == 1000m && DealOS.Agents.Mail.Desk.PriceToBuyer(DealOS.Agents.Mail.Desk.BidToSeller(9999.99m, 3), 3) <= 9999.99m,
      "desk: bid to seller is the inverse and never exceeds the buyer's price");
var vanadium = DealOS.Agents.Mail.Desk.Tokens("Vanadium Pentoxide (V2O5) 98% min, high purity");
Check(vanadium.SequenceEqual(new[] { "vanadium", "pentoxide", "v2o5" }), "desk: commodity tokens skip generic words");
var now = new DateTime(2026, 10, 6);
Check(DealOS.Agents.Mail.Desk.LeadScore(vanadium, "VANADIUM PENTOXIDE FLAKES 98% (V2O5)", 6, now.AddDays(-30), now) >
      DealOS.Agents.Mail.Desk.LeadScore(vanadium, "Ferro vanadium 80%", 20, now.AddDays(-400), now), "desk: exact product beats a related one");
Check(DealOS.Agents.Mail.Desk.LeadScore(vanadium, "Calcium metal lumps; pentoxide catalyst", 50, now, now) == 0, "desk: the main word must match (no vanadium = no match)");
Check(DealOS.Agents.Mail.Desk.LeadScore(DealOS.Agents.Mail.Desk.Tokens("Strontium Metal"), "STRONTIUM METAL 99% granules", 1, null, now) > 0, "desk: strontium metal lead matches");
var raw = DealOS.Agents.Mail.MimeBuilder.Build("seller@x.example", "Enquiry: V₂O₅ – 15 MT", "Dear Sir,\nPrice please.", "desk+dealos@gmail.com", "<abc@mail>", null,
    new[] { new DealOS.Agents.Mail.MimeBuilder.Attachment { FileName = "Contract.pdf", MimeType = "application/pdf", Data = new byte[] { 1, 2, 3 } } });
var mime = System.Text.Encoding.UTF8.GetString(DealOS.Agents.Mail.GmailMessage.FromBase64Url(raw));
Check(mime.Contains("To: seller@x.example\r\n") && mime.Contains("Reply-To: desk+dealos@gmail.com") && mime.Contains("In-Reply-To: <abc@mail>") &&
      mime.Contains("Subject: =?UTF-8?B?") && mime.Contains("multipart/mixed") && mime.Contains("filename=\"Contract.pdf\"") && !raw.Contains("=") && !raw.Contains("+"),
      "desk: reply email with UTF-8 subject, threading headers and attachment, base64url");
Check(DealOS.Agents.Mail.Desk.WithoutSignOff("Dear Rakesh,\n\nWe will revert shortly.\n\nBest regards,") == "Dear Rakesh,\n\nWe will revert shortly." &&
      DealOS.Agents.Mail.Desk.WithoutSignOff("Price confirmed.\n\nKind regards,\nTrade Desk") == "Price confirmed." &&
      DealOS.Agents.Mail.Desk.WithoutSignOff("Thank you for the quote. We revert today.") == "Thank you for the quote. We revert today.", "desk: drafts lose the writer's own sign-off");
var pdf = DealOS.Agents.Mail.PdfWriter.Write("SALES CONTRACT", Enumerable.Range(1, 120).Select(i => "Clause " + i + ": (terms) apply \\ as agreed"));
var pdfText = System.Text.Encoding.ASCII.GetString(pdf);
Check(pdfText.StartsWith("%PDF-1.4") && pdfText.Contains("/Count 3") && pdfText.Contains("\\(terms\\)") && pdfText.TrimEnd().EndsWith("%%EOF"), "desk: contract PDF with 3 pages and escaped text");

// Schemas + manifest
var manifest = new List<object>();
foreach (var a in AgentCatalog.All)
{
    var tools = ToolCatalog.For(a).Select(t => (object)J.Obj("name", t.Name, "description", t.Description, "parameters", t.Parameters)).ToList();
    Check(tools.Count == a.Tools.Length, a.ApiName + " tools resolve (" + tools.Count + ")");
    tools.Add(J.Obj("name", "finish", "description", "Return result", "parameters", a.OutputSchema));
    File.WriteAllText(Path.Combine(outDir, a.Name + ".tools.json"), Json.Serialize(tools));
    File.WriteAllText(Path.Combine(outDir, a.Name + ".output.json"), Json.Serialize(a.OutputSchema));
    manifest.Add(J.Obj("name", a.Name, "api", a.ApiName, "display", a.DisplayName, "description", a.Description,
                       "subject_table", a.SubjectTable, "agent_choice", a.AgentChoice, "structured", a.StructuredOnly,
                       "model_setting", a.ModelSettingKey, "tools", a.Tools, "read_tables", a.ReadTables));
}
File.WriteAllText(Path.Combine(outDir, "agents.manifest.json"), Json.Serialize(manifest));
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

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
Check(DealOS.Agents.Mail.Desk.Greet("Dear Buyer,\n\nWe are pleased to confirm.", "Rakesh Jain") == "Dear Rakesh Jain,\n\nWe are pleased to confirm." &&
      DealOS.Agents.Mail.Desk.Greet("Dear Seller,\n\nConfirmed.", null) == "Dear Sir or Madam,\n\nConfirmed." &&
      DealOS.Agents.Mail.Desk.Greet("Dear Sir,\n\nConfirmed.", null) == "Dear Sir,\n\nConfirmed." &&
      DealOS.Agents.Mail.Desk.Greet("Rakesh,\n\nNoted.", "Rakesh Jain") == "Rakesh,\n\nNoted." &&
      DealOS.Agents.Mail.Desk.Greet("Dear Buyers' team is...", "X") == "Dear Buyers' team is...", "desk: generic greetings use the contact's name");
Check(DealOS.Agents.Mail.Desk.PersonName("Rakesh Jain <rakesh.jain@x.example>") == "Rakesh Jain" && DealOS.Agents.Mail.Desk.PersonName("\"Li Wei\" <l@x.example>") == "Li Wei" &&
      DealOS.Agents.Mail.Desk.PersonName("sales@x.example") == null && DealOS.Agents.Mail.Desk.PersonName("") == null, "desk: contact name from the sender label");
var t1 = Guid.NewGuid(); var t2 = Guid.NewGuid();
var threads = new List<KeyValuePair<Guid, string>> { new KeyValuePair<Guid, string>(t1, "Vanadium Pentoxide (V2O5) enquiry – X"), new KeyValuePair<Guid, string>(t2, "Calcium metal enquiry – X") };
Check(DealOS.Agents.Mail.Desk.PickThread(threads, "Quotation: vanadium pentoxide flakes") == t1 && DealOS.Agents.Mail.Desk.PickThread(threads, "Our price list") == null &&
      DealOS.Agents.Mail.Desk.PickThread(threads.Take(1).ToList(), "Our price list") == t1, "desk: a seller's fresh email joins the matching open enquiry");
{
    var t0 = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);
    Func<int, decimal, decimal, int, DealOS.Agents.Mail.Lots.Bid> bid = (n, price, q, minute) =>
        new DealOS.Agents.Mail.Lots.Bid { DealId = new Guid(n, 0, 0, new byte[8]), Price = price, Quantity = q, On = t0.AddMinutes(minute) };
    var bids = new[] { bid(1, 10200m, 10m, 5), bid(2, 10400m, 10m, 9), bid(3, 10400m, 5m, 2), bid(4, 9900m, 5m, 1) };
    var won = DealOS.Agents.Mail.Lots.Winners(bids, 10145.50m, 15m);
    // 10,400 x 5 (earlier) and 10,400 x 10 fill the 15 MT; 10,200 does not fit; 9,900 is under the floor
    Check(won.Count == 2 && won[0].DealId == bids[2].DealId && won[1].DealId == bids[1].DealId, "lots: highest price wins, ties go to the earliest bid, while quantity lasts");
    var partial = DealOS.Agents.Mail.Lots.Winners(new[] { bid(1, 10500m, 20m, 1), bid(2, 10300m, 8m, 2) }, 10145.50m, 15m);
    Check(partial.Count == 1 && partial[0].Price == 10300m, "lots: a bid larger than the lot is skipped, the next one that fits wins");
    Check(DealOS.Agents.Mail.Lots.Winners(new[] { bid(1, 10100m, 5m, 1) }, 10145.50m, 15m).Count == 0, "lots: no winner below the floor (seller price + margin)");
    Func<bool, double?, string, DealOS.Agents.Mail.Lots.Window> win = DealOS.Agents.Mail.Lots.WindowOf;
    Check(win(false, 48, "24").Hours == 48 && win(false, 48, "24").Stated, "lots: the seller's window (48 h) wins over the default");
    Check(win(true, null, "24").Hours == null && win(true, 48, "24").Label == "Open-ended (seller)", "lots: 'until sold' makes the lot open-ended");
    Check(win(false, null, "24").Hours == 24 && !win(false, null, "24").Stated && win(false, null, null).Hours == 24, "lots: no window in the email = the default (24 h)");
    Check(win(false, null, "0").Hours == null && win(false, null, "open").Hours == null, "lots: default 0 or 'open' = open-ended");
    Check(win(false, 5000, "24").Hours == 720 && win(false, 0.2, "24").Hours == 1, "lots: a window is kept between 1 hour and 30 days");
}
Check(DealOS.Agents.Mail.Desk.HasPriceAndQuantity("Quantity: 20 MT\nPrice: USD 41,000 per MT CIF") && !DealOS.Agents.Mail.Desk.HasPriceAndQuantity("We have FeMo70 in stock, please ask for price") &&
      !DealOS.Agents.Mail.Desk.HasPriceAndQuantity("Price: USD 41,000 per MT"), "lots: an offer with price and quantity is a lot, not a lead");
{
    var leaked = "Product: Ferro Molybdenum FeMo70 (Mo 70% min, Cu 0.5% max), lumps 10-50 mm. Quantity: 20 MT. Price: USD 41,000 per MT CIF Nhava Sheva. Packing: 1 MT steel drums. " +
                 "Origin: China. Payment: LC at sight. Validity: 7 days. Contact Wang at wang@x.example. Made by Xinmo Moly works.";
    var safe = DealOS.Agents.Mail.Desk.SafeSpec(leaked, "[AGENT-TEST] Xinmo Moly Test Co");
    Check(safe == "Ferro Molybdenum FeMo70 (Mo 70% min, Cu 0.5% max), lumps 10-50 mm", "lots: a buyer sees only the technical specification (no seller price, terms, contact or name): " + safe);
    Check(DealOS.Agents.Mail.Desk.SafeSpec("Mo 70% min\nCu 0.5% max\nSize 10-50 mm", "X") == "Mo 70% min; Cu 0.5% max; Size 10-50 mm", "lots: a clean specification is kept");
}
Check(DealOS.Agents.Mail.Desk.LeadScore(DealOS.Agents.Mail.Desk.Tokens("Ferro Tungsten FeW80"), "FERRO MOLYBDENUM FEMO70 LUMPS", 5, null, now) == 0 &&
      DealOS.Agents.Mail.Desk.LeadScore(DealOS.Agents.Mail.Desk.Tokens("Ferro Tungsten FeW80"), "FERRO TUNGSTEN FEW80 LUMPS", 5, null, now) > 0 &&
      DealOS.Agents.Mail.Desk.LeadScore(DealOS.Agents.Mail.Desk.Tokens("Calcium metal"), "CALCIUM METAL GRANULES", 1, null, now) > 0, "desk: 'Ferro X' leads match only on X, not on 'ferro'");
var pdf = DealOS.Agents.Mail.PdfWriter.Write("SALES CONTRACT", Enumerable.Range(1, 120).Select(i => "Clause " + i + ": (terms) apply \\ as agreed"));
var pdfText = System.Text.Encoding.ASCII.GetString(pdf);
Check(pdfText.StartsWith("%PDF-1.4") && pdfText.Contains("/Count 3") && pdfText.Contains("\\(terms\\)") && pdfText.TrimEnd().EndsWith("%%EOF"), "desk: contract PDF with 3 pages and escaped text");

// OpenAI: the runtime's Gemini-format request translates to the Responses API and back
{
    var g = J.Obj(
        "systemInstruction", J.Obj("parts", new List<object> { J.Obj("text", "RULES") }),
        "contents", new List<object> {
            J.Obj("role", "user", "parts", new List<object> { J.Obj("text", "CONTEXT"), J.Obj("inlineData", J.Obj("mimeType", "application/pdf", "data", "QUJD")) }),
            J.Obj("role", "model", "parts", new List<object> { J.Obj("functionCall", J.Obj("name", "save_quote", "id", "call_1", "args", J.Obj("price", 9850))) }),
            J.Obj("role", "user", "parts", new List<object> { J.Obj("functionResponse", J.Obj("name", "save_quote", "id", "call_1", "response", J.Obj("ok", true)) ) }) },
        "tools", new List<object> { J.Obj("functionDeclarations", new List<object> { J.Obj("name", "save_quote", "description", "d", "parameters", S.Obj(null, "price", S.Num("p"), "note?", S.Str("n"))) }) },
        "toolConfig", J.Obj("functionCallingConfig", J.Obj("mode", "ANY", "allowedFunctionNames", new List<object> { "finish" })),
        "generationConfig", J.Obj("temperature", 0.2));
    var o = OpenAIClient.Translate("gpt-5.4-mini", g, "low");
    var input = (List<object>)o["input"];
    var tool = (Dictionary<string, object>)((List<object>)o["tools"])[0];
    var ps = (Dictionary<string, object>)tool["parameters"];
    Check(J.Str(o, "instructions") == "RULES" && (bool)o["store"] == false, "openai: system rules become instructions; nothing stored at OpenAI");
    Check(input.Count == 3 && J.Str((Dictionary<string, object>)((List<object>)((Dictionary<string, object>)input[0])["content"])[1], "type") == "input_file" &&
          J.Str((Dictionary<string, object>)input[1], "type") == "function_call" && J.Str((Dictionary<string, object>)input[2], "type") == "function_call_output" &&
          J.Str((Dictionary<string, object>)input[2], "call_id") == "call_1", "openai: PDF part, tool call and tool result map to Responses items with the same call id");
    Check(J.Str(ps, "type") == "object" && !ps.ContainsKey("propertyOrdering") && J.Str((Dictionary<string, object>)((Dictionary<string, object>)ps["properties"])["price"], "type") == "number",
          "openai: tool schemas become JSON Schema (lower-case types, no propertyOrdering)");
    Check(J.Str((Dictionary<string, object>)o["tool_choice"], "name") == "finish" && J.Str((Dictionary<string, object>)o["reasoning"], "effort") == "low" && !o.ContainsKey("temperature"),
          "openai: forced finish maps to tool_choice; gpt-5 gets reasoning effort, not temperature");
    Check(OpenAIClient.Translate("gpt-4.1-mini", g, "low").ContainsKey("temperature") && !OpenAIClient.Translate("gpt-4.1-mini", g, "low").ContainsKey("reasoning"),
          "openai: gpt-4.1 fallback gets temperature, not reasoning");
    var resp = OpenAIClient.Read(Json.ParseObject("{\"model\":\"gpt-5.4-mini\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5},\"output\":[" +
        "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"encrypted_content\":\"x\"},{\"type\":\"function_call\",\"call_id\":\"call_9\",\"name\":\"finish\",\"arguments\":\"{\\\"summary\\\":\\\"ok\\\"}\",\"status\":\"completed\"}]}"), "gpt-5.4-mini");
    Check(resp.Calls.Count == 1 && resp.Calls[0].Id == "call_9" && J.Str(resp.Calls[0].Args, "summary") == "ok" && resp.TokensIn == 10 &&
          ((List<object>)resp.Content["openai_items"]).Count == 2, "openai: function calls are read back with their call id; reasoning items ride along");
    var next = OpenAIClient.Translate("gpt-4.1-mini", J.Obj("contents", new List<object> { resp.Content }), "low");
    Check(((List<object>)next["input"]).Count == 1, "openai: after a fallback to another model, the first model's encrypted reasoning is dropped");
}

{
    var schema = S.Obj(null, "category", S.Str("c"), "offer?", S.Obj("o", "commodity", S.Str("x"), "price?", S.Str("p")), "company?", S.Str("c"));
    var v = J.Obj("category", "Buyer Requirement", "offer", J.Obj("commodity", "", "price", null), "company", "");
    S.Prune(schema, v);
    Check(!v.ContainsKey("offer") && !v.ContainsKey("company") && S.Validate(schema, v).Count == 0, "schema: empty optional blocks a model filled in are dropped before validation");
}

{
    var t = DealOS.Agents.Mail.Desk.SafeTerms("Origin: Brazil\nLead time: within 6 weeks\nOur offer: 5 MT Niobium Pentoxide 99.5% min, origin Brazil, USD 29,500 per MT CIF Nhava Sheva, LC at sight.\n" +
                                              "Payment: LC at sight\nContact Pedro at pedro@alpha.example\nPrice 29500 firm", "[AGENT-TEST] Alpha Niobium Test SA", 29500m);
    Check(t == "Origin: Brazil\nLead time: within 6 weeks\nPayment: LC at sight", "desk: a seller's terms reach the buyer without the seller's price, contacts or name: " + t);
}

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

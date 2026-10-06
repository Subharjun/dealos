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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Mail;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Checks
{
    /// <summary>
    /// Company registry check for KYB, from free public registers called by the "Party checks" flow (custom connectors):
    ///   GLEIF (global LEI register, no key) and UK Companies House (free key; only for UK companies, when connected).
    /// A matched LEI is filled in on the account; the registration number never is (a change to it re-runs the checks).
    /// gc_RegistryQuery tells the flow what to look up; gc_RegistryRecord compares what came back with the party:
    ///   Pass    the register has the company under the number (or LEI) we hold, under the same name, and it is active
    ///   Fail    the register shows it dissolved / inactive
    ///   Refer   the number belongs to a differently named company, or it is in liquidation / administration, or the LEI lapsed
    ///   Pending a likely record by name only: a person confirms it against the registration certificate
    /// Directors and persons with significant control (Companies House) are returned for sanctions screening.
    /// </summary>
    public static class Registry
    {
        public static Dictionary<string, object> Query(Dv dv, Guid accountId)
        {
            var acc = dv.Retrieve("account", accountId, "name", "gc_lei", "gc_registrationnumber", "gc_country");
            if (acc == null) throw new InvalidPluginExecutionException("Account " + accountId + " not found.");
            string iso2 = null;
            var c = acc.GetAttributeValue<EntityReference>("gc_country");
            if (c != null) iso2 = (dv.Retrieve("gc_country", c.Id, "gc_iso2") ?? new Entity()).GetAttributeValue<string>("gc_iso2");
            iso2 = (iso2 ?? "").Trim().ToUpperInvariant();
            var number = (acc.GetAttributeValue<string>("gc_registrationnumber") ?? "").Trim();
            var name = (acc.GetAttributeValue<string>("name") ?? "").Trim();
            var uk = iso2 == "GB" || iso2 == "UK" || (iso2.Length == 0 && Regex.IsMatch(number, @"^(\d{8}|(SC|NI|OC|SO|NC|R0|LP|SL)\d{6})$", RegexOptions.IgnoreCase));
            return J.Obj("name", name, "lei", (acc.GetAttributeValue<string>("gc_lei") ?? "").Trim().ToUpperInvariant(),
                         "registration_number", number, "country", iso2, "uk", uk && dv.Setting("registry.companies_house", "off") == "on",
                         // GLEIF full-text search wants the distinctive part of the name
                         "search", string.Join(" ", Sanctions.Tokens(name)));
        }

        public static Dictionary<string, object> Record(DeskWriter w, Guid accountId, string gleifLeiJson, string gleifSearchJson, string gleifNameSearchJson,
                                                         string chProfileJson, string chOfficersJson, string chPscJson)
        {
            var dv = w.Dv;
            var acc = dv.Retrieve("account", accountId, "name", "gc_lei", "gc_registrationnumber", "gc_country");
            if (acc == null) throw new InvalidPluginExecutionException("Account " + accountId + " not found.");
            var q = Query(dv, accountId);
            var name = J.Str(q, "name");
            var number = Norm(J.Str(q, "registration_number"));
            var results = new List<object>();
            var names = new List<object>();

            // ---- GLEIF
            var records = new List<Dictionary<string, object>>();
            var one = Obj(gleifLeiJson);
            if (one != null && J.ObjOf(one, "data") != null) records.Add(J.ObjOf(one, "data"));
            foreach (var search in new[] { gleifSearchJson, gleifNameSearchJson })
            {
                var many = Obj(search);
                if (many != null) records.AddRange(J.Arr(many, "data").OfType<Dictionary<string, object>>());
            }
            var gleif = records.Select(r => Gleif(r, name, number, J.Str(q, "lei"), J.Str(q, "country")))
                               .Where(x => x != null).OrderByDescending(x => x.Rank).FirstOrDefault();
            if (gleif != null)
            {
                results.Add(Save(w, accountId, "GLEIF", gleif.Lei, gleif.Result, gleif.Details));
                // The LEI is filled in; the registration number is not (changing it re-runs the party checks), it is in the check's details.
                if (gleif.Result == "Pass" && string.IsNullOrEmpty(acc.GetAttributeValue<string>("gc_lei")))
                {
                    var set = new Entity("account", accountId);
                    set["gc_lei"] = gleif.Lei;
                    w.Update(set, "registry_details_from_gleif", J.Obj("lei", gleif.Lei));
                }
            }

            // ---- Companies House (UK)
            var profile = Obj(chProfileJson);
            if (profile != null && !string.IsNullOrEmpty(J.Str(profile, "company_number")))
            {
                var ch = CompaniesHouse(profile, Obj(chOfficersJson), Obj(chPscJson), name, number);
                results.Add(Save(w, accountId, "Companies House", J.Str(profile, "company_number"), ch.Result, ch.Details));
                names.AddRange(ch.Names);
            }

            var overall = results.Count == 0 ? "Not Found"
                        : results.Any(r => J.Str((Dictionary<string, object>)r, "result") == "Fail") ? "Fail"
                        : results.Any(r => J.Str((Dictionary<string, object>)r, "result") == "Pass") ? "Pass"
                        : results.Any(r => J.Str((Dictionary<string, object>)r, "result") == "Refer") ? "Refer" : "Pending";
            return J.Obj("result", overall, "checks", results, "names", names,
                         "summary", results.Count == 0 ? "No record found in GLEIF" + (J.Bool(q, "uk") ? " or Companies House" : "") + " for " + name + ": a person checks the registration certificate."
                                                       : string.Join(" ", results.Select(r => J.Str((Dictionary<string, object>)r, "provider") + ": " + J.Str((Dictionary<string, object>)r, "result") + ".")));
        }

        private sealed class Found
        {
            public string Lei, RegisteredAs, Result, Details;
            public int Rank;
        }

        private static Found Gleif(Dictionary<string, object> r, string name, string number, string lei, string country)
        {
            var attr = J.ObjOf(r, "attributes");
            if (attr == null) return null;
            var entity = J.ObjOf(attr, "entity") ?? new Dictionary<string, object>();
            var registration = J.ObjOf(attr, "registration") ?? new Dictionary<string, object>();
            var legalName = J.Str(J.ObjOf(entity, "legalName") ?? new Dictionary<string, object>(), "name") ?? "";
            var address = J.ObjOf(entity, "legalAddress") ?? new Dictionary<string, object>();
            var recLei = J.Str(attr, "lei") ?? J.Str(r, "id");
            var registeredAs = J.Str(entity, "registeredAs");
            var nameScore = NameScore(name, legalName);
            var leiMatch = !string.IsNullOrEmpty(lei) && lei == recLei;
            var numMatch = !string.IsNullOrEmpty(number) && Norm(registeredAs) == number;
            var countryMatch = string.IsNullOrEmpty(country) || country == (J.Str(address, "country") ?? "").ToUpperInvariant();
            if (!leiMatch && !numMatch && (nameScore < 0.85 || !countryMatch)) return null;

            var entityStatus = (J.Str(entity, "status") ?? "").ToUpperInvariant();
            var regStatus = (J.Str(registration, "status") ?? "").ToUpperInvariant();
            string result, why;
            if (entityStatus == "INACTIVE") { result = "Fail"; why = "the legal entity is INACTIVE in GLEIF (dissolved, merged or retired)"; }
            else if ((leiMatch || numMatch) && nameScore < 0.85) { result = "Refer"; why = (leiMatch ? "our LEI" : "our registration number") + " belongs to \"" + legalName + "\", not \"" + name + "\""; }
            else if (leiMatch || numMatch) { result = regStatus == "ISSUED" ? "Pass" : "Refer"; why = regStatus == "ISSUED" ? "matched by " + (leiMatch ? "LEI" : "registration number") + " and name" : "matched, but the LEI is " + regStatus + " (not renewed)"; }
            else { result = "Pending"; why = "a likely record by name and country only: confirm the registration number against the certificate"; }
            var details = "GLEIF " + recLei + ": " + legalName + "; registered as " + (registeredAs ?? "-") + " (" + (J.Str(entity, "jurisdiction") ?? "-") + "); " +
                          string.Join(", ", new[] { J.Str(address, "city"), J.Str(address, "country") }.Where(x => !string.IsNullOrEmpty(x))) +
                          "; entity " + entityStatus + ", LEI " + regStatus + ". Result: " + why + ".";
            return new Found { Lei = recLei, RegisteredAs = registeredAs, Result = result, Details = details, Rank = (leiMatch ? 4 : 0) + (numMatch ? 2 : 0) + (nameScore >= 0.85 ? 1 : 0) };
        }

        private sealed class ChResult
        {
            public string Result, Details;
            public List<object> Names = new List<object>();
        }

        private static ChResult CompaniesHouse(Dictionary<string, object> profile, Dictionary<string, object> officers, Dictionary<string, object> psc, string name, string number)
        {
            var r = new ChResult();
            var chName = J.Str(profile, "company_name") ?? "";
            var chNumber = J.Str(profile, "company_number") ?? "";
            var status = (J.Str(profile, "company_status") ?? "").ToLowerInvariant();
            var numMatch = !string.IsNullOrEmpty(number) && Norm(chNumber).TrimStart('0') == number.TrimStart('0');
            var nameScore = NameScore(name, chName);
            string why;
            if (nameScore < 0.85 && !numMatch) { r.Result = "Pending"; why = "the closest company found is \"" + chName + "\": confirm it is the same party"; }
            else if (status == "dissolved" || status == "converted-closed" || status == "removed") { r.Result = "Fail"; why = "the company is " + status; }
            else if (status != "active") { r.Result = "Refer"; why = "company status is " + status; }
            else if (numMatch && nameScore < 0.85) { r.Result = "Refer"; why = "our registration number belongs to \"" + chName + "\""; }
            else { r.Result = "Pass"; why = "active, matched by " + (numMatch ? "company number and name" : "name"); }

            var people = new List<string>();
            foreach (var o in J.Arr(officers ?? new Dictionary<string, object>(), "items").OfType<Dictionary<string, object>>())
            {
                if (!string.IsNullOrEmpty(J.Str(o, "resigned_on"))) continue;
                var role = J.Str(o, "officer_role") ?? "officer";
                var n = J.Str(o, "name");
                if (string.IsNullOrEmpty(n)) continue;
                r.Names.Add(J.Obj("name", n, "role", role, "person", !role.Contains("corporate")));
                people.Add(n + " (" + role + ")");
            }
            foreach (var p in J.Arr(psc ?? new Dictionary<string, object>(), "items").OfType<Dictionary<string, object>>())
            {
                if (!string.IsNullOrEmpty(J.Str(p, "ceased_on"))) continue;
                var kind = J.Str(p, "kind") ?? "";
                var n = J.Str(p, "name");
                if (string.IsNullOrEmpty(n)) continue;
                r.Names.Add(J.Obj("name", n, "role", "person with significant control", "person", kind.Contains("individual")));
                people.Add(n + " (PSC: " + string.Join(", ", J.Arr(p, "natures_of_control").Select(x => Convert.ToString(x))) + ")");
            }
            var address = J.ObjOf(profile, "registered_office_address") ?? new Dictionary<string, object>();
            r.Details = "Companies House " + chNumber + ": " + chName + "; " + status + "; incorporated " + (J.Str(profile, "date_of_creation") ?? "-") + "; " +
                        string.Join(", ", new[] { "address_line_1", "locality", "postal_code" }.Select(k => J.Str(address, k)).Where(x => !string.IsNullOrEmpty(x))) +
                        ". Officers and owners: " + (people.Count == 0 ? "none listed" : string.Join("; ", people.Take(15))) + ". Result: " + why + ".";
            return r;
        }

        /// <summary>One Company Registry check per provider (updated in place on a re-check).</summary>
        private static Dictionary<string, object> Save(DeskWriter w, Guid accountId, string provider, string reference, string result, string details)
        {
            var type = Choice.Base + Array.IndexOf(Choice.KycCheckTypes, "Company Registry");
            var existing = w.Dv.Query("gc_kyccheck", new[] { "gc_kyccheckid" }, 1, "gc_account", ConditionOperator.Equal, accountId,
                                      "gc_checktype", ConditionOperator.Equal, type, "gc_provider", ConditionOperator.Equal, provider).FirstOrDefault();
            var e = new Entity("gc_kyccheck");
            if (existing != null) e.Id = existing.Id;
            e["gc_name"] = Desk.Cut("Company Registry (" + provider + "): " + result, 200);
            e["gc_account"] = new EntityReference("account", accountId);
            e["gc_checktype"] = new OptionSetValue(type);
            e["gc_result"] = new OptionSetValue(Choice.Base + Array.IndexOf(Choice.KycResults, result));
            e["gc_provider"] = provider;
            e["gc_providerref"] = Desk.Cut(reference, 100);
            e["gc_checkedon"] = DateTime.UtcNow;
            e["gc_details"] = Desk.Cut(details, 100000);
            if (existing == null) w.Create(e, "registry_check_recorded", J.Obj("provider", provider, "result", result));
            else w.Update(e, "registry_check_recorded", J.Obj("provider", provider, "result", result));
            return J.Obj("provider", provider, "reference", reference, "result", result, "details", details);
        }

        private static double NameScore(string a, string b)
        {
            var x = Sanctions.Tokens(a);
            var y = Sanctions.Tokens(b);
            if (x.Length == 0 || y.Length == 0) return 0;
            return Math.Min(Sanctions.Score(x, y), Sanctions.Score(y, x));
        }

        private static string Norm(string s) { return Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z0-9]", ""); }

        private static Dictionary<string, object> Obj(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return Json.Parse(json) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
        }
    }
}

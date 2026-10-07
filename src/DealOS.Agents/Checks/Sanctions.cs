using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Mail;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Checks
{
    /// <summary>
    /// Sanctions screening against the official public lists (no paid provider):
    ///   ofac_sdn   US Treasury OFAC Specially Designated Nationals (SDN.CSV + ALT.CSV)
    ///   ofac_cons  US Treasury OFAC consolidated non-SDN lists (CONS_PRIM.CSV + CONS_ALT.CSV)
    ///   un         UN Security Council consolidated list (XML)
    ///   uk         UK Sanctions List (FCDO, XML)
    /// The "Sanctions lists" flow downloads each list into its gc_sanctionlist row (file gc_raw, aliases in gc_rawaliases) and calls
    /// gc_SanctionsLoad, which keeps a compact name index (file gc_index). gc_ScreenParty matches a company, its contacts and any extra
    /// names (e.g. directors from the company registry) against every index. A hit is never decided by code: it becomes a
    /// "Potential Match" screening and a Screening Clearance task for a person; no hit is "Clear".
    /// </summary>
    public static class Sanctions
    {
        public sealed class Entry
        {
            public string List, Id, Kind, Name, Program;
            public string[] Tokens;
        }

        public sealed class Hit
        {
            public string Subject, List, Id, Name, Program;
            public double Score;
            public string Key { get { return List + ":" + Id; } }
        }

        // ---------------------------------------------------------------- loading the lists

        /// <summary>Parses the raw list file(s) of one gc_sanctionlist row into its name index. Changed = the list differs from the last load.</summary>
        public static Dictionary<string, object> Load(Dv dv, Guid listId)
        {
            var row = dv.Retrieve("gc_sanctionlist", listId, "gc_source", "gc_sha256", "gc_name");
            if (row == null) throw new InvalidPluginExecutionException("gc_sanctionlist " + listId + " not found.");
            var source = (row.GetAttributeValue<string>("gc_source") ?? "").Trim().ToLowerInvariant();
            var target = new EntityReference("gc_sanctionlist", listId);
            var raw = MailFiles.Download(dv.Svc, target, "gc_raw");
            if (raw.Length == 0) throw new InvalidPluginExecutionException("The list file of " + source + " is empty: the download did not arrive.");
            byte[] aliases = null;
            if (source.StartsWith("ofac")) aliases = MailFiles.Download(dv.Svc, target, "gc_rawaliases");

            string listDate;
            var entries = Parse(source, raw, aliases, out listDate);
            if (entries.Count < 50) throw new InvalidPluginExecutionException("Only " + entries.Count + " names read from " + source + ": the file format may have changed. The previous index is kept.");

            var text = new StringBuilder();
            foreach (var e in entries)
                text.Append(e.Id).Append('\t').Append(e.Kind).Append('\t').Append(Clean(e.Name)).Append('\t').Append(Clean(e.Program)).Append('\n');
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            string sha;
            using (var h = SHA256.Create()) sha = BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            var changed = sha != row.GetAttributeValue<string>("gc_sha256");
            if (changed) MailFiles.Upload(dv.Svc, target, "gc_index", source + ".tsv", "text/tab-separated-values", bytes);

            var u = new Entity("gc_sanctionlist", listId);
            u["gc_entries"] = entries.Count;
            u["gc_sha256"] = sha;
            u["gc_fetchedon"] = DateTime.UtcNow;
            u["gc_listdate"] = listDate;
            dv.Svc.Update(u);
            return J.Obj("source", source, "names", entries.Count, "list_date", listDate, "changed", changed);
        }

        public static List<Entry> Parse(string source, byte[] raw, byte[] aliases, out string listDate)
        {
            listDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            switch (source)
            {
                case "ofac_sdn":
                case "ofac_cons":
                    return Ofac(source, Text(raw), aliases == null ? "" : Text(aliases));
                case "un":
                    return Un(raw, out listDate);
                case "uk":
                    return Uk(raw, out listDate);
                default:
                    throw new InvalidPluginExecutionException("Unknown sanctions list source '" + source + "' (ofac_sdn, ofac_cons, un, uk).");
            }
        }

        /// <summary>OFAC CSV: primary rows (ent_num, name, type, program, ...) and alias rows (ent_num, alt_num, alt_type, alt_name, remarks).</summary>
        private static List<Entry> Ofac(string list, string primary, string alias)
        {
            var list_ = new List<Entry>();
            var byId = new Dictionary<string, Entry>();
            foreach (var r in Csv(primary))
            {
                if (r.Count < 4 || string.IsNullOrWhiteSpace(r[1])) continue;
                var type = Null(r[2]);
                var e = new Entry { List = list, Id = r[0].Trim(), Name = r[1].Trim(), Program = Null(r[3]),
                                    Kind = type == "individual" ? "I" : type == "vessel" ? "V" : type == "aircraft" ? "A" : "E" };
                list_.Add(e);
                byId[e.Id] = e;
            }
            foreach (var r in Csv(alias))
            {
                if (r.Count < 4 || string.IsNullOrWhiteSpace(Null(r[3]))) continue;
                Entry p;
                byId.TryGetValue(r[0].Trim(), out p);
                list_.Add(new Entry { List = list, Id = r[0].Trim(), Name = r[3].Trim(), Kind = p == null ? "E" : p.Kind, Program = p == null ? null : p.Program });
            }
            return list_;
        }

        private static List<Entry> Un(byte[] raw, out string listDate)
        {
            var list = new List<Entry>();
            listDate = null;
            using (var reader = XmlReader.Create(new MemoryStream(raw), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true }))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Name == "CONSOLIDATED_LIST" && listDate == null)
                    {
                        var generated = reader.GetAttribute("dateGenerated");
                        if (!string.IsNullOrEmpty(generated)) listDate = generated.Length >= 10 ? generated.Substring(0, 10) : generated;
                        continue;
                    }
                    if (reader.Name != "INDIVIDUAL" && reader.Name != "ENTITY") continue;
                    var individual = reader.Name == "INDIVIDUAL";
                    var x = (XElement)XNode.ReadFrom(reader);
                    var id = (string)x.Element("REFERENCE_NUMBER") ?? (string)x.Element("DATAID");
                    var program = (string)x.Element("UN_LIST_TYPE");
                    var name = string.Join(" ", new[] { "FIRST_NAME", "SECOND_NAME", "THIRD_NAME", "FOURTH_NAME" }
                                                  .Select(n => ((string)x.Element(n) ?? "").Trim()).Where(n => n.Length > 0));
                    var kind = individual ? "I" : "E";
                    if (name.Length > 0) list.Add(new Entry { List = "un", Id = id, Kind = kind, Name = name, Program = program });
                    foreach (var a in x.Elements(individual ? "INDIVIDUAL_ALIAS" : "ENTITY_ALIAS"))
                    {
                        var alias = ((string)a.Element("ALIAS_NAME") ?? "").Trim();
                        if (alias.Length > 0) list.Add(new Entry { List = "un", Id = id, Kind = kind, Name = alias, Program = program });
                    }
                }
            }
            listDate = listDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return list;
        }

        private static List<Entry> Uk(byte[] raw, out string listDate)
        {
            var list = new List<Entry>();
            listDate = null;
            using (var reader = XmlReader.Create(new MemoryStream(raw), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true }))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Name == "DateGenerated" && listDate == null)
                    {
                        DateTime d;
                        var s = reader.ReadElementContentAsString();
                        listDate = DateTime.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : s;
                        continue;
                    }
                    if (reader.Name != "Designation") continue;
                    var x = (XElement)XNode.ReadFrom(reader);
                    var id = (string)x.Element("UniqueID");
                    var program = (string)x.Element("RegimeName");
                    var type = ((string)x.Element("IndividualEntityShip") ?? "").Trim();
                    var kind = type == "Individual" ? "I" : type == "Ship" ? "V" : "E";
                    var names = x.Element("Names");
                    if (names == null) continue;
                    foreach (var n in names.Elements("Name"))
                    {
                        var name = string.Join(" ", Enumerable.Range(1, 6).Select(i => ((string)n.Element("Name" + i) ?? "").Trim()).Where(p => p.Length > 0));
                        if (name.Length > 0) list.Add(new Entry { List = "uk", Id = id, Kind = kind, Name = name, Program = program });
                    }
                }
            }
            listDate = listDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return list;
        }

        // ---------------------------------------------------------------- screening

        /// <summary>The index of one loaded list (cached per content hash: the sandbox may reuse the plug-in between calls).</summary>
        private static readonly Dictionary<string, Index> Cache = new Dictionary<string, Index>();

        public sealed class Index
        {
            public string List, ListDate;
            public List<Entry> Entries = new List<Entry>();
            public Dictionary<string, List<int>> Blocks = new Dictionary<string, List<int>>();

            public static Index Build(string list, string listDate, IEnumerable<Entry> entries)
            {
                var ix = new Index { List = list, ListDate = listDate };
                foreach (var e in entries)
                {
                    e.Tokens = Tokens(e.Name);
                    if (e.Tokens.Length == 0) continue;
                    ix.Entries.Add(e);
                    foreach (var b in e.Tokens.Where(t => t.Length >= 3).Select(Block).Distinct())
                    {
                        List<int> slot;
                        if (!ix.Blocks.TryGetValue(b, out slot)) ix.Blocks[b] = slot = new List<int>();
                        slot.Add(ix.Entries.Count - 1);
                    }
                }
                return ix;
            }

            public IEnumerable<Entry> Candidates(string[] tokens)
            {
                var seen = new HashSet<int>();
                foreach (var b in tokens.Where(t => t.Length >= 3).Select(Block).Distinct())
                {
                    List<int> slot;
                    if (!Blocks.TryGetValue(b, out slot)) continue;
                    foreach (var i in slot) if (seen.Add(i)) yield return Entries[i];
                }
            }
        }

        public static List<Index> Indexes(Dv dv)
        {
            var result = new List<Index>();
            foreach (var row in dv.Query("gc_sanctionlist", new[] { "gc_source", "gc_sha256", "gc_listdate", "gc_entries" }, 20))
            {
                var sha = row.GetAttributeValue<string>("gc_sha256");
                if (string.IsNullOrEmpty(sha) || (row.GetAttributeValue<int?>("gc_entries") ?? 0) == 0) continue;
                var source = row.GetAttributeValue<string>("gc_source");
                Index ix;
                lock (Cache)
                    if (Cache.TryGetValue(sha, out ix)) { result.Add(ix); continue; }
                var text = Text(MailFiles.Download(dv.Svc, new EntityReference("gc_sanctionlist", row.Id), "gc_index"));
                var entries = text.Split('\n').Where(l => l.Length > 0).Select(l => l.Split('\t'))
                                  .Where(p => p.Length >= 3).Select(p => new Entry { List = source, Id = p[0], Kind = p[1], Name = p[2], Program = p.Length > 3 ? p[3] : null });
                ix = Index.Build(source, row.GetAttributeValue<string>("gc_listdate"), entries);
                lock (Cache) Cache[sha] = ix;
                result.Add(ix);
            }
            return result;
        }

        /// <summary>
        /// Screens a party: the company name, its contacts and any extra names ([{"name": "...", "role": "director", "person": true}]).
        /// One gc_screening per screened name (updated in place on a re-screen). A hit a person already cleared stays Clear.
        /// New hits → one Screening Clearance task for the company (Compliance Officer) and a briefing.
        /// </summary>
        public static Dictionary<string, object> Screen(DeskWriter w, Guid accountId, string extraNamesJson, List<Index> indexes = null)
        {
            var dv = w.Dv;
            var acc = dv.Retrieve("account", accountId, "name");
            if (acc == null) throw new InvalidPluginExecutionException("Account " + accountId + " not found.");
            indexes = indexes ?? Indexes(dv);
            if (indexes.Count == 0) return J.Obj("status", "NoLists", "reason", "No sanctions list is loaded yet: run the Sanctions lists flow.");

            var subjects = new List<Tuple<string, Guid?, bool, string>>();   // name, contact, person, role
            var seen = new HashSet<string>();
            Action<string, Guid?, bool, string> add = (name, contact, person, role) =>
            {
                var key = string.Join(" ", Tokens(name));
                if (key.Length > 0 && seen.Add(key)) subjects.Add(Tuple.Create(name.Trim(), contact, person, role));
            };
            add(acc.GetAttributeValue<string>("name") ?? "", null, false, "company");
            foreach (var c in dv.Query("contact", new[] { "fullname", "jobtitle" }, 25, "parentcustomerid", ConditionOperator.Equal, accountId))
                add(c.GetAttributeValue<string>("fullname") ?? "", c.Id, true, c.GetAttributeValue<string>("jobtitle") ?? "contact");
            foreach (var x in ExtraNames(extraNamesJson))
                add(J.Str(x, "name") ?? "", null, J.Bool(x, "person", true), J.Str(x, "role") ?? "officer");

            var threshold = dv.SettingNum("screening.threshold", 0.84);
            var lists = string.Join(", ", indexes.Select(i => ListLabel(i.List) + " " + i.ListDate));
            var newHits = new List<Hit>();
            var screeningIds = new List<object>();
            var results = new List<object>();
            foreach (var s in subjects)
            {
                var hits = Match(s.Item1, s.Item3, indexes, threshold);
                var title = Desk.Cut("Screening: " + s.Item1, 200);
                var existing = dv.Query("gc_screening", new[] { "gc_result", "gc_lists", "gc_clearancereason" }, 1,
                                        "gc_account", ConditionOperator.Equal, accountId, "gc_name", ConditionOperator.Equal, title).FirstOrDefault();
                var keys = hits.Select(h => h.Key).Distinct().ToList();
                var before = existing == null ? null : Dv.Label(existing, "gc_result");
                var sameHits = existing != null && keys.All(k => (existing.GetAttributeValue<string>("gc_lists") ?? "").Contains(k));
                // A person decided these exact hits before (same entries on the lists): their decision stands.
                var decided = sameHits && !string.IsNullOrEmpty(existing.GetAttributeValue<string>("gc_clearancereason"));
                var result = hits.Count == 0 || (decided && before == "Clear") ? "Clear"
                           : sameHits && before == "Confirmed Match" ? "Confirmed Match" : "Potential Match";
                var e = new Entity("gc_screening");
                if (existing != null) e.Id = existing.Id;
                e["gc_name"] = title;
                e["gc_account"] = new EntityReference("account", accountId);
                if (s.Item2 != null) e["gc_contact"] = new EntityReference("contact", s.Item2.Value);
                e["gc_result"] = new OptionSetValue(Choice.Base + (result == "Clear" ? 0 : result == "Potential Match" ? 1 : 2));
                e["gc_screenedon"] = DateTime.UtcNow;
                e["gc_lists"] = Desk.Cut((hits.Count == 0 ? "No match. " : "Hits: " + string.Join(", ", keys.Take(8)) + ". ") + "Lists: " + lists, 500);
                Guid id;
                if (existing == null) id = w.Create(e, "screening_recorded", J.Obj("name", s.Item1, "result", result));
                else { w.Update(e, "screening_recorded", J.Obj("name", s.Item1, "result", result)); id = existing.Id; }
                if (result == "Potential Match")
                {
                    // Already waiting for a person with the same hits: no second task.
                    var waiting = sameHits && before == "Potential Match";
                    if (!waiting) newHits.AddRange(hits.Select(h => { h.Subject = s.Item1 + " (" + s.Item4 + ")"; return h; }));
                    screeningIds.Add(id.ToString());
                }
                results.Add(J.Obj("name", s.Item1, "role", s.Item4, "result", result,
                                  "hits", hits.Take(5).Select(h => (object)J.Obj("list", ListLabel(h.List), "id", h.Id, "name", h.Name, "program", h.Program, "score", Math.Round(h.Score, 3))).ToList()));
            }

            if (newHits.Count > 0)
            {
                var company = acc.GetAttributeValue<string>("name");
                var lines = newHits.Take(12).Select(h => "- " + h.Subject + " ~ " + h.Name + " (" + ListLabel(h.List) + " " + h.Id + (string.IsNullOrEmpty(h.Program) ? "" : ", " + h.Program) +
                                                         ", score " + h.Score.ToString("0.00", CultureInfo.InvariantCulture) + ")").ToList();
                var t = new Entity("gc_reviewtask");
                t["gc_name"] = Desk.Cut("Sanctions screening: possible match for " + company, 100);
                t["gc_kind"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewKinds, "Review"));
                t["gc_purpose"] = new OptionSetValue(Choice.ValueOf(Choice.ReviewPurposes, "Screening Clearance"));
                t["gc_assigneerole"] = new OptionSetValue(Choice.ValueOf(Choice.AdminRoles, "Compliance Officer"));
                t["gc_status"] = new OptionSetValue(Choice.ReviewStatus.Open);
                t["gc_account"] = new EntityReference("account", accountId);
                t["gc_payload"] = Desk.Cut(Json.Serialize(J.Obj("action", "screening.clear", "company", company, "screeningIds", screeningIds,
                    "hits", newHits.Take(12).Select(h => (object)J.Obj("subject", h.Subject, "list", ListLabel(h.List), "id", h.Id, "listed_name", h.Name, "program", h.Program,
                                                                      "score", Math.Round(h.Score, 3))).ToList(),
                    "check", "Compare date of birth, nationality, address and registration details on the list entry with the party's documents.",
                    "approve", "Approve = not the same person or company: the screening becomes Clear.",
                    "reject", "Reject = a true match: the party goes on compliance hold and its deals cannot go to contract.")), 100000);
                w.Create(t, "screening_clearance_task", J.Obj("company", company, "hits", newHits.Count));
                Desk.Brief(w, "Sanctions: possible match for " + company,
                           "The official sanctions lists show names close to this party. Nothing goes to contract until a person decides.\n\n" + string.Join("\n", lines) +
                           "\n\nCheck the list entries (date of birth, nationality, address, registration) against the party's documents, then answer the decision briefing.");
            }
            return J.Obj("status", "Screened", "account", accountId.ToString(), "names", subjects.Count, "new_hits", newHits.Count,
                         "result", results.Any(r => J.Str((Dictionary<string, object>)r, "result") != "Clear") ? "Potential Match" : "Clear", "lists", lists, "screened", results);
        }

        /// <summary>Re-screens the parties the desk deals with (KYB started), oldest screening first; after a list changed.</summary>
        public static Dictionary<string, object> ScreenAll(DeskWriter w, int max)
        {
            var dv = w.Dv;
            var indexes = Indexes(dv);
            if (indexes.Count == 0) return J.Obj("status", "NoLists");
            // Only when a list changed since the last re-screen (the lists flow runs daily; most days nothing changes).
            var listsHash = string.Join(",", dv.Query("gc_sanctionlist", new[] { "gc_sha256" }, 20).Select(r => r.GetAttributeValue<string>("gc_sha256") ?? "").OrderBy(x => x));
            var setting = dv.Query("gc_platformsetting", new[] { "gc_value" }, 1, "gc_key", ConditionOperator.Equal, "screening.lists_hash").FirstOrDefault();
            if (setting != null && setting.GetAttributeValue<string>("gc_value") == listsHash) return J.Obj("status", "Unchanged");
            var q = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), TopCount = Math.Max(1, Math.Min(max, 500)) };
            q.Criteria.AddCondition("gc_kybstatus", ConditionOperator.NotNull);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            q.AddOrder("modifiedon", OrderType.Ascending);
            var done = 0;
            var matches = new List<object>();
            var started = DateTime.UtcNow;
            foreach (var a in dv.Svc.RetrieveMultiple(q).Entities)
            {
                if ((DateTime.UtcNow - started).TotalSeconds > 90) break;   // stay inside the plug-in time limit; the next run continues
                var r = Screen(w, a.Id, null, indexes);
                done++;
                if (J.Num(r, "new_hits") > 0) matches.Add(a.GetAttributeValue<string>("name"));
            }
            var keep = new Entity("gc_platformsetting");
            if (setting != null) keep.Id = setting.Id;
            keep["gc_value"] = listsHash;
            if (setting == null)
            {
                keep["gc_key"] = "screening.lists_hash";
                keep["gc_name"] = "screening.lists_hash";
                keep["gc_description"] = "Hashes of the sanctions lists at the last re-screen of all parties (kept by gc_ScreenParties).";
                dv.Svc.Create(keep);
            }
            else dv.Svc.Update(keep);
            return J.Obj("status", "Screened", "parties", done, "new_matches", matches);
        }

        public static List<Hit> Match(string name, bool person, List<Index> indexes, double threshold)
        {
            var q = Tokens(name);
            var hits = new List<Hit>();
            if (q.Length == 0) return hits;
            foreach (var ix in indexes)
                foreach (var e in ix.Candidates(q))
                {
                    if (person ? e.Kind != "I" : e.Kind == "I") continue;   // companies against entities, vessels and aircraft; people against individuals
                    var s = Score(q, e.Tokens);
                    if (s >= threshold) hits.Add(new Hit { List = ix.List, Id = e.Id, Name = e.Name, Program = e.Program, Score = s });
                }
            // One hit per listed entry (its best name), strongest first
            return hits.GroupBy(h => h.Key).Select(g => g.OrderByDescending(h => h.Score).First()).OrderByDescending(h => h.Score).Take(10).ToList();
        }

        // ---------------------------------------------------------------- name matching

        private static readonly HashSet<string> LegalForms = new HashSet<string>(
            ("LTD LIMITED LLC LLP INC INCORPORATED CORP CORPORATION CO COMPANY PLC GMBH AG SA SAS SARL SRL SPA BV NV PTE PVT PRIVATE PT TBK " +
             "OOO OAO ZAO PAO AO JSC OJSC CJSC PJSC FZE FZCO FZ DMCC THE AND OF DE DA DO EL AL").Split(' '));

        /// <summary>Words that say little about who a company is: they count for less, and a hit needs a distinctive word.</summary>
        private static readonly HashSet<string> Generic = new HashSet<string>(
            ("INTERNATIONAL INTL TRADING TRADE TRADERS TRADER ENTERPRISE ENTERPRISES GROUP HOLDING HOLDINGS GLOBAL INDUSTRIES INDUSTRY INDUSTRIAL " +
             "METAL METALS MINERAL MINERALS MINING STEEL ENERGY RESOURCES SHIPPING LOGISTICS GENERAL CHEMICAL CHEMICALS IMPEX EXPORT EXPORTS IMPORT IMPORTS " +
             "OVERSEAS SERVICES SERVICE SOLUTIONS TECHNOLOGY TECHNOLOGIES TECH INVESTMENT INVESTMENTS PETROLEUM OIL GAS MARINE BANK COMMERCIAL DEVELOPMENT " +
             "MANAGEMENT PARTNERS AGENCY COMMODITIES TRANSPORT ALLOYS ORE ORES COAL GOLD").Split(' '));

        public static string[] Tokens(string name)
        {
            var d = (name ?? "").Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder(d.Length);
            foreach (var ch in d)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToUpperInvariant(ch));
            return Regex.Split(sb.ToString(), "[^A-Z0-9]+").Where(t => t.Length > 0 && !LegalForms.Contains(t)).ToArray();
        }

        /// <summary>
        /// 0..1. Each word counts when it closely matches a word of the other name (Jaro-Winkler 0.88+), weighted by length (generic words 0.3).
        /// Three quarters from how much of the screened name is on the list entry, one quarter from the reverse; at least one distinctive word must
        /// match strongly (6+ letters: 0.92+, shorter: exactly).
        /// </summary>
        public static double Score(string[] a, string[] b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a.SequenceEqual(b)) return 1;
            if (!a.Any(t => !Generic.Contains(t) && t.Length >= 3 && (t.Length < 6 ? b.Contains(t) : b.Max(u => JaroWinkler(t, u)) >= 0.92))) return 0;
            return 0.75 * Direction(a, b) + 0.25 * Direction(b, a);
        }

        private static double Direction(string[] a, string[] b)
        {
            double total = 0, matched = 0;
            foreach (var t in a)
            {
                var weight = t.Length * (Generic.Contains(t) ? 0.3 : 1.0);
                total += weight;
                var best = b.Max(u => JaroWinkler(t, u));
                if (best >= 0.88) matched += weight * best;
            }
            return total == 0 ? 0 : matched / total;
        }

        public static double JaroWinkler(string a, string b)
        {
            if (a == b) return 1;
            int la = a.Length, lb = b.Length;
            if (la == 0 || lb == 0) return 0;
            var range = Math.Max(0, Math.Max(la, lb) / 2 - 1);
            var ma = new bool[la];
            var mb = new bool[lb];
            var m = 0;
            for (var i = 0; i < la; i++)
                for (var j = Math.Max(0, i - range); j < Math.Min(lb, i + range + 1); j++)
                    if (!mb[j] && a[i] == b[j]) { ma[i] = mb[j] = true; m++; break; }
            if (m == 0) return 0;
            int k = 0, t = 0;
            for (var i = 0; i < la; i++)
            {
                if (!ma[i]) continue;
                while (!mb[k]) k++;
                if (a[i] != b[k]) t++;
                k++;
            }
            var jaro = ((double)m / la + (double)m / lb + (m - t / 2.0) / m) / 3;
            var p = 0;
            while (p < Math.Min(4, Math.Min(la, lb)) && a[p] == b[p]) p++;
            return jaro + p * 0.1 * (1 - jaro);
        }

        private static string Block(string token) { return token.Length > 4 ? token.Substring(0, 4) : token; }

        // ---------------------------------------------------------------- helpers

        public static string ListLabel(string source)
        {
            switch (source)
            {
                case "ofac_sdn": return "OFAC SDN";
                case "ofac_cons": return "OFAC Consolidated";
                case "un": return "UN";
                case "uk": return "UK";
                default: return source;
            }
        }

        private static IEnumerable<Dictionary<string, object>> ExtraNames(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Enumerable.Empty<Dictionary<string, object>>();
            try { return (Json.Parse(json) as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>(); }
            catch (FormatException) { return Enumerable.Empty<Dictionary<string, object>>(); }
        }

        private static string Text(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(bytes); }
        }

        private static string Null(string s)
        {
            var t = (s ?? "").Trim();
            return t == "-0-" || t.Length == 0 ? null : t;
        }

        private static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim(); }

        /// <summary>Minimal RFC 4180 reader (quoted fields, doubled quotes, commas and newlines inside quotes).</summary>
        public static IEnumerable<List<string>> Csv(string text)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString()); field.Clear();
                    if (row.Count > 1 || row[0].Length > 0) yield return row;
                    row = new List<string>();
                }
                else field.Append(c);
            }
            if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); yield return row; }
        }
    }
}

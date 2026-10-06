using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Tools
{
    /// <summary>
    /// Every tool an agent can call. Read tools respect the agent's table allow-list and the caller's
    /// security roles. Write tools only create drafts, ledger rows and review tasks, never approvals.
    /// </summary>
    public static class ToolCatalog
    {
        private static readonly Dictionary<string, Tool> All = Build().ToDictionary(t => t.Name);

        public static List<Tool> For(AgentDefinition def)
        {
            return def.Tools.Where(All.ContainsKey).Select(n => All[n]).ToList();
        }

        public static IEnumerable<string> Names { get { return All.Keys; } }

        private static IEnumerable<Tool> Build()
        {
            yield return new Tool
            {
                Name = "get_record",
                Description = "Read one Dataverse record by table logical name and id. Only tables listed in CONTEXT.readable_tables are allowed.",
                Parameters = S.Obj(null,
                    "table", S.Str("Table logical name, e.g. gc_listing, account, gc_deal."),
                    "id", S.Str("Record GUID."),
                    "columns?", S.Arr("Column logical names to return; omit for all.", S.Str("column"))),
                Run = (ctx, a) =>
                {
                    var table = Readable(ctx, J.Str(a, "table"));
                    var id = RequireId(a, "id");
                    var cols = J.Arr(a, "columns").Select(c => c.ToString()).ToArray();
                    var unknown = cols.Where(c => !ctx.Dv.Columns(table).ContainsKey(c)).ToList();
                    var rec = ctx.Dv.Retrieve(table, id, cols);
                    if (rec == null) throw new ToolRefusal("No " + table + " record with id " + id + " (or it is not visible to you).");
                    var flat = Dv.Flatten(rec);
                    if (unknown.Count > 0) flat["_ignored_unknown_columns"] = unknown;
                    return flat;
                }
            };

            yield return new Tool
            {
                Name = "query_records",
                Description = "List up to 25 records from a table with simple AND filters. Choice columns are filtered by numeric value (303300000 = first option). Lookups are filtered by GUID.",
                Parameters = S.Obj(null,
                    "table", S.Str("Table logical name."),
                    "filters?", S.Arr("AND conditions.", S.Obj(null,
                        "column", S.Str("Column logical name."),
                        "operator", S.Enum("Comparison.", "eq", "ne", "gt", "ge", "lt", "le", "like", "null", "notnull"),
                        "value?", S.Str("Value as text (GUID, number, true/false, ISO date or text; use % with like)."))),
                    "columns?", S.Arr("Columns to return; omit for all.", S.Str("column")),
                    "top?", S.Int("Max rows, default 10, max 25.")),
                Run = (ctx, a) =>
                {
                    var table = Readable(ctx, J.Str(a, "table"));
                    var cols = J.Arr(a, "columns").Select(c => c.ToString()).ToArray();
                    var known = ctx.Dv.Columns(table);
                    var q = new QueryExpression(table)
                    {
                        ColumnSet = ctx.Dv.SafeColumns(table, cols),
                        TopCount = Math.Max(1, Math.Min(25, (int)(J.Num(a, "top") ?? 10)))
                    };
                    foreach (var f in J.Arr(a, "filters").OfType<Dictionary<string, object>>())
                    {
                        var col = (J.Str(f, "column") ?? "").Trim().ToLowerInvariant();
                        if (!known.ContainsKey(col))
                            throw new ToolRefusal("Unknown column '" + col + "' on " + table + ". Columns: " + string.Join(", ", known.Keys.Where(k => k.StartsWith("gc_") || k == "name" || k == "statecode" || k == "createdon").OrderBy(k => k).Take(60)));
                        var op = Operator(J.Str(f, "operator"));
                        if (op == ConditionOperator.Null || op == ConditionOperator.NotNull) q.Criteria.AddCondition(col, op);
                        else q.Criteria.AddCondition(col, op, Coerce(J.Str(f, "value"), known[col], col));
                    }
                    q.AddOrder("createdon", OrderType.Descending);
                    var rows = ctx.Dv.Svc.RetrieveMultiple(q).Entities;
                    return J.Obj("count", rows.Count, "rows", Dv.FlattenAll(rows));
                }
            };

            yield return new Tool
            {
                Name = "get_facts",
                Description = "Current evidence facts (value, status, sensitivity) for a subject.",
                Parameters = S.Obj(null,
                    "subject_type", S.Enum("Subject kind.", "Party", "Listing", "Deal", "Asset", "Licence", "Lot", "Document", "Contact"),
                    "subject_id", S.Str("Subject GUID.")),
                Run = (ctx, a) => J.Obj("facts", Evidence.FactsJson(Evidence.Facts(ctx, Evidence.SubjectTypeOfLabel(J.Str(a, "subject_type")), RequireSubject(ctx, a))))
            };

            yield return new Tool
            {
                Name = "resolve_evidence",
                Description = "Re-run the deterministic evidence resolver for a subject; returns counts, summary and the system's own gap list.",
                Parameters = S.Obj(null,
                    "subject_type", S.Enum("Subject kind.", "Party", "Listing", "Deal", "Asset", "Licence", "Lot", "Document", "Contact"),
                    "subject_id", S.Str("Subject GUID.")),
                Run = (ctx, a) => Evidence.Resolve(ctx, Evidence.SubjectTypeOfLabel(J.Str(a, "subject_type")), RequireSubject(ctx, a))
            };

            yield return new Tool
            {
                Name = "calculate_price_quote",
                Description = "Deterministic landed-cost engine for an offer. Creates buyer, seller and admin quote views. Pass only cost inputs that appear in the data; omit unknown ones.",
                Writes = true,
                Parameters = S.Obj(null,
                    "offer_id", S.Str("gc_offer GUID."),
                    "freight_per_unit?", S.Num("Freight per quantity unit, offer currency."),
                    "insurance_rate_pct?", S.Num("Insurance rate %."),
                    "origin_inland?", S.Num("Origin inland transport, total."),
                    "loading?", S.Num("Loading cost, total."),
                    "export_clearance?", S.Num("Export clearance, total."),
                    "import_clearance?", S.Num("Import clearance, total."),
                    "destination_inland?", S.Num("Destination inland transport, total."),
                    "import_tax_pct?", S.Num("Import tax %.")),
                Run = (ctx, a) =>
                {
                    var offerId = RequireId(a, "offer_id");
                    var offer = ctx.Dv.Retrieve("gc_offer", offerId, "gc_deal", "gc_price", "gc_quantity");
                    if (offer == null) throw new ToolRefusal("No gc_offer with id " + offerId + ".");
                    if (offer.GetAttributeValue<EntityReference>("gc_deal") == null || offer.GetAttributeValue<decimal?>("gc_price") == null || offer.GetAttributeValue<decimal?>("gc_quantity") == null)
                        throw new ToolRefusal("The offer needs a deal, price and quantity before it can be priced.");
                    var req = new OrganizationRequest("gc_CalculatePriceQuote");
                    req["OfferId"] = offerId;
                    Map(a, req, "freight_per_unit", "FreightPerUnit");
                    Map(a, req, "insurance_rate_pct", "InsuranceRatePct");
                    Map(a, req, "origin_inland", "OriginInland");
                    Map(a, req, "loading", "Loading");
                    Map(a, req, "export_clearance", "ExportClearance");
                    Map(a, req, "import_clearance", "ImportClearance");
                    Map(a, req, "destination_inland", "DestinationInland");
                    Map(a, req, "import_tax_pct", "ImportTaxPct");
                    if (ctx.DryRun)
                    {
                        ctx.Actions.Add(J.Obj("action", "calculate_price_quote", "dry_run", true, "detail", J.Obj("offer_id", req["OfferId"].ToString())));
                        return J.Obj("dry_run", true, "note", "Quote not recalculated in a dry run; use the quotes in CONTEXT.");
                    }
                    var r = ctx.Dv.Svc.Execute(req).Results;
                    ctx.Actions.Add(J.Obj("action", "calculate_price_quote", "table", "gc_pricequote", "dry_run", false, "detail", J.Obj("offer_id", req["OfferId"].ToString())));
                    var result = J.Obj(
                        "landed_cost", r.Contains("LandedCost") ? r["LandedCost"] : null,
                        "net_payout", r.Contains("NetPayout") ? r["NetPayout"] : null,
                        "summary", r.Contains("Summary") ? r["Summary"] : null,
                        "buyer_quote_id", r.Contains("BuyerQuoteId") ? r["BuyerQuoteId"] : null,
                        "seller_quote_id", r.Contains("SellerQuoteId") ? r["SellerQuoteId"] : null);
                    ctx.Scratch["last_quote"] = result;
                    return result;
                }
            };

            yield return new Tool
            {
                Name = "compute_release_schedule",
                Description = "Deterministic escrow release maths: gross, commission and net per tranche, using the deal's commission plan from CONTEXT.",
                Parameters = S.Obj(null,
                    "contract_value", S.Num("Total contract value in deal currency."),
                    "tranches", S.Arr("Release tranches in order; percentages must sum to 100.", S.Obj(null,
                        "pct", S.Num("Percent of contract value."),
                        "condition", S.Str("Release condition, e.g. 'Inspection passed and BL issued'.")))),
                Run = (ctx, a) => ReleaseSchedule(ctx, J.Num(a, "contract_value") ?? 0, J.Arr(a, "tranches").OfType<Dictionary<string, object>>().ToList())
            };

            yield return new Tool
            {
                Name = "ask_question",
                Description = "Record ONE question for the counterparty in the question ledger (do-not-ask-twice). Refused if the information is already present, already asked recently, or the per-run limit is reached. Then include the asks in draft_message.",
                Writes = true,
                Parameters = S.Obj(null,
                    "attribute_key", S.Str("Attribute key from CONTEXT.attributes (e.g. licence.mining), or other.<short_name>."),
                    "kind", S.Enum("Why we ask.", Choice.QuestionKinds),
                    "question", S.Str("The question, one sentence."),
                    "recipient_account_id?", S.Str("Account GUID of the recipient, if known.")),
                Run = AskQuestion
            };

            yield return new Tool
            {
                Name = "draft_message",
                Description = "Draft a message for human approval. Market = to prospective buyers/investors (anonymous teaser). Source = to the counterparty that owns the data (seller, buyer). The text is validated; on error, fix it and call again.",
                Writes = true,
                Parameters = S.Obj(null,
                    "audience", S.Enum("Recipient side.", "Market", "Source"),
                    "text", S.Str("Final message text, ready to send, no placeholders."),
                    "title?", S.Str("Short internal title."),
                    "recipient_account_id?", S.Str("Account GUID of the recipient, if known.")),
                Run = DraftMessage
            };

            yield return new Tool
            {
                Name = "create_review_task",
                Description = "Ask a human to approve, review or decide something. One open task per purpose and subject; duplicates return the existing task.",
                Writes = true,
                Parameters = S.Obj(null,
                    "purpose", S.Enum("What the task is for.", Choice.ReviewPurposes),
                    "kind", S.Enum("Task kind.", Choice.ReviewKinds),
                    "title", S.Str("Short title (under 90 characters)."),
                    "details", S.Str("What the reviewer must check or decide, with the evidence."),
                    "assignee_role?", S.Enum("Role that should act.", Choice.AdminRoles)),
                Run = ReviewTask
            };

            yield return new Tool
            {
                Name = "propose_match",
                Description = "Propose a buyer–seller match for one candidate in CONTEXT.candidates. You supply only the specification fit; the other dimensions are pre-computed.",
                Writes = true,
                Parameters = S.Obj(null,
                    "candidate_id", S.Str("id of the candidate from CONTEXT.candidates."),
                    "spec_score", S.Int("Specification fit 0-100, judged only from data shown."),
                    "explanation", S.Str("Why this is (or is not) a fit, citing the data.")),
                Run = ProposeMatch
            };

            yield return new Tool
            {
                Name = "record_kyc_check",
                Description = "Record a KYB/KYC check as Pending (needs evidence or provider) or Refer (needs compliance review). Agents can never mark a check Pass or Fail.",
                Writes = true,
                Parameters = S.Obj(null,
                    "check_type", S.Enum("Check.", Choice.KycCheckTypes),
                    "result", S.Enum("Agent-allowed result.", "Pending", "Refer"),
                    "notes", S.Str("What is missing or why it is referred."),
                    "contact_id?", S.Str("Contact GUID for person-level checks (ID, UBO).")),
                Run = KycCheck
            };

            yield return new Tool
            {
                Name = "create_contract_draft",
                Description = "Create the draft contract record for this deal and a 'Contract Issue' review task carrying the term sheet. Refused if a draft already exists.",
                Writes = true,
                Parameters = S.Obj(null,
                    "terms_json", S.Str("The term sheet as a JSON object string."),
                    "nonstandard_clauses", S.Bool("True if any term departs from the standard template."),
                    "template_version", S.Str("Template identifier, e.g. 'spot-physical-v1'.")),
                Run = ContractDraft
            };

            yield return new Tool
            {
                Name = "create_milestone",
                Description = "Add a pending execution milestone to the deal (one per type).",
                Writes = true,
                Parameters = S.Obj(null,
                    "type", S.Enum("Milestone type.", Choice.MilestoneTypes),
                    "due_on?", S.Str("Due date YYYY-MM-DD, only if stated in the data.")),
                Run = Milestone
            };
        }

        // ---------- write tools ----------

        private static object AskQuestion(AgentContext ctx, Dictionary<string, object> a)
        {
            var link = SubjectLink(ctx);
            if (link == null) throw new ToolRefusal("ask_question needs a listing, deal or account subject.");
            var max = ctx.Dv.SettingInt("dd1.max_asks", 3);
            var cooldownHours = ctx.Dv.SettingInt("questions.cooldown_hours", 72);
            var outstanding = Evidence.Questions(ctx, link.LogicalName == "account" ? "gc_recipient" : link.LogicalName, link.Id, 50)
                .Count(q => q.GetAttributeValue<EntityReference>("gc_answeredby") == null && q.GetAttributeValue<DateTime?>("gc_askedon") > DateTime.UtcNow.AddHours(-cooldownHours));
            outstanding += ctx.QuestionIds.Count; // asked earlier in this run (also counted in dry runs)
            if (outstanding >= max)
                throw new ToolRefusal(outstanding + " questions are already outstanding with this counterparty (limit " + max + " per " + cooldownHours + "h). Wait for answers; do not add more.");

            var key = (J.Str(a, "attribute_key") ?? "").Trim();
            var defs = Evidence.Defs(ctx);
            if (!defs.ContainsKey(key) && !key.StartsWith("other.", StringComparison.Ordinal))
                throw new ToolRefusal("Unknown attribute_key '" + key + "'. Use a key from CONTEXT.attributes or other.<short_name>.");
            var kind = Choice.ValueOf(Choice.QuestionKinds, J.Str(a, "kind"));
            if (kind < 0) throw new ToolRefusal("kind must be one of: " + string.Join(", ", Choice.QuestionKinds));

            var facts = SubjectFacts(ctx);
            var fact = facts.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
            if (fact != null)
            {
                var k = kind - Choice.Base;
                if (fact.Status == Choice.FactStatus.Verified)
                    throw new ToolRefusal("'" + key + "' is already Verified. Do not ask for it.");
                if (fact.Status == Choice.FactStatus.Documented && (k == 0 || k == 1))
                    throw new ToolRefusal("'" + key + "' is already supported by a document. Do not ask for it again.");
                if (fact.Status == Choice.FactStatus.Claimed && k == 0)
                    throw new ToolRefusal("The counterparty already gave '" + key + "' (" + fact.DisplayValue + "). Ask for supporting evidence (kind Evidence) instead of the value.");
                if (fact.Status == Choice.FactStatus.Conflicting && k != 2)
                    throw new ToolRefusal("'" + key + "' is Conflicting. Use kind 'Clarify Conflict'.");
                if (fact.Status == Choice.FactStatus.Outdated && k != 3)
                    throw new ToolRefusal("'" + key + "' is Outdated. Use kind 'Update Outdated'.");
            }

            var cooldown = cooldownHours;
            var previous = Evidence.Questions(ctx, link.LogicalName == "account" ? "gc_recipient" : link.LogicalName, link.Id, 50)
                .Where(q => string.Equals(q.GetAttributeValue<string>("gc_attributekey"), key, StringComparison.OrdinalIgnoreCase)).ToList();
            var recent = previous.FirstOrDefault(q => q.GetAttributeValue<DateTime?>("gc_askedon") > DateTime.UtcNow.AddHours(-cooldown));
            if (recent != null)
                throw new ToolRefusal("'" + key + "' was already asked on " + recent.GetAttributeValue<DateTime?>("gc_askedon").Value.ToString("yyyy-MM-dd") + " (cool-down " + cooldown + "h). Do not ask again yet.");

            var q2 = new Entity("gc_question");
            q2["gc_name"] = GeminiClient.Truncate(J.Str(a, "question") ?? key, 100);
            q2["gc_attributekey"] = key;
            q2["gc_kind"] = new OptionSetValue(kind);
            q2["gc_askedon"] = DateTime.UtcNow;
            q2["gc_isfollowup"] = previous.Count > 0;
            q2[link.LogicalName == "account" ? "gc_recipient" : link.LogicalName] = link;
            var recipient = J.Id(a, "recipient_account_id");
            if (recipient != null && !ctx.Dv.Exists("account", recipient.Value)) recipient = null; // ignore ids the model made up
            if (recipient == null && link.LogicalName == "account") recipient = link.Id;
            if (recipient != null) q2["gc_recipient"] = new EntityReference("account", recipient.Value);
            var id = ctx.Create(q2, "ask_question", J.Obj("attribute_key", key, "kind", J.Str(a, "kind"), "question", J.Str(a, "question")));
            ctx.QuestionIds.Add(id);
            return J.Obj("ok", true, "question_id", ctx.DryRun ? null : id.ToString(), "follow_up", previous.Count > 0,
                         "note", "Recorded. Put the question in the Source message.");
        }

        private static object DraftMessage(AgentContext ctx, Dictionary<string, object> a)
        {
            var audience = J.Str(a, "audience");
            var market = audience == "Market";
            if (!market && audience != "Source") throw new ToolRefusal("audience must be Market or Source.");
            var count = ctx.Actions.OfType<Dictionary<string, object>>().Count(x => J.Str(x, "action") == "draft_message" && J.Str(J.ObjOf(x, "detail"), "audience") == audience);
            if (count >= 1) throw new ToolRefusal("A " + audience + " message was already drafted in this run.");
            var text = (J.Str(a, "text") ?? "").Trim();
            var subjectLink = SubjectLink(ctx);
            if (subjectLink != null && subjectLink.LogicalName != "account")
            {
                var pending = ctx.Dv.Query("gc_generatedmessage", new[] { "gc_generatedmessageid", "createdon" }, 1,
                    subjectLink.LogicalName, ConditionOperator.Equal, subjectLink.Id,
                    "gc_kind", ConditionOperator.Equal, market ? Choice.MessageKind.Market : Choice.MessageKind.Source,
                    "gc_status", ConditionOperator.Equal, Choice.Draft.Draft_).FirstOrDefault();
                if (pending != null)
                    throw new ToolRefusal("A " + audience + " draft (" + pending.Id + ") is already waiting for approval. Do not draft another; mention it in your summary.");
            }
            var facts = SubjectFacts(ctx);

            List<object> sheet;
            HashSet<string> known;
            var forbidden = new List<string>(ScratchList(ctx, "forbidden_terms"));
            var claimedNumbers = new List<string>();
            if (market)
            {
                if (ctx.Agent.SubjectTable != "gc_listing") throw new ToolRefusal("Market messages are only drafted for listings.");
                var usable = facts.Where(f => f.Def != null && f.Def.MarketVisible &&
                    (f.Status == Choice.FactStatus.Verified || f.Status == Choice.FactStatus.Documented || f.Status == Choice.FactStatus.Claimed)).ToList();
                sheet = usable.Select(f => (object)f.ToJson()).ToList();
                known = new HashSet<string>();
                MessageValidator.CollectNumbers(Json.Serialize(sheet), known);
                foreach (var n in ScratchList(ctx, "market_numbers")) MessageValidator.CollectNumbers(n, known);
                claimedNumbers = usable.Where(f => f.Status == Choice.FactStatus.Claimed)
                    .SelectMany(f => { var h = new HashSet<string>(); MessageValidator.CollectNumbers(f.DisplayValue, h); return h; }).ToList();
                forbidden.AddRange(facts.Where(f => f.Def == null || !f.Def.MarketVisible).Select(f => f.DisplayValue));
            }
            else
            {
                sheet = facts.Select(f => (object)f.ToJson()).ToList();
                known = ctx.KnownNumbers;
                forbidden = ScratchList(ctx, "counterparty_terms").ToList();
            }

            var report = MessageValidator.Check(text, known, forbidden, facts.Any(f => f.Status == Choice.FactStatus.Verified), claimedNumbers, market);
            if (!report.Ok) throw new ToolRefusal("Message rejected by validator: " + string.Join(" ", report.Errors));

            var link = SubjectLink(ctx);
            var fs = new Entity("gc_factsheet");
            fs["gc_name"] = GeminiClient.Truncate((market ? "Market" : "Source") + " fact sheet – " + ctx.Agent.DisplayName, 100);
            fs["gc_kind"] = new OptionSetValue(market ? Choice.MessageKind.Market : Choice.MessageKind.Source);
            fs["gc_content"] = Json.Serialize(J.Obj("audience", audience, "facts", sheet));
            fs["gc_policyversion"] = "agents-" + ctx.Agent.Version;
            fs["gc_agentrun"] = new EntityReference("gc_agentrun", ctx.RunId);
            SetLink(fs, link);
            var fsId = ctx.Create(fs, "create_factsheet", J.Obj("audience", audience, "facts", sheet.Count));

            var msg = new Entity("gc_generatedmessage");
            msg["gc_name"] = GeminiClient.Truncate(J.Str(a, "title") ?? ((market ? "Market teaser – " : "Request – ") + ctx.Agent.DisplayName), 100);
            msg["gc_kind"] = new OptionSetValue(market ? Choice.MessageKind.Market : Choice.MessageKind.Source);
            msg["gc_text"] = text;
            msg["gc_realiser"] = new OptionSetValue(Choice.Realiser.Llm);
            msg["gc_status"] = new OptionSetValue(Choice.Draft.Draft_);
            // gc_question.gc_message points at inbound chat messages (gc_message), so the asks are linked here instead.
            msg["gc_verifierreport"] = Json.Serialize(J.Obj("checks", report.Checks, "model", ctx.Model, "agent", ctx.Agent.ApiName, "run", ctx.RunId.ToString(),
                "question_ids", market ? new List<object>() : ctx.QuestionIds.Where(q => q != Guid.Empty).Select(q => (object)q.ToString()).ToList()));
            if (!ctx.DryRun) msg["gc_factsheet"] = new EntityReference("gc_factsheet", fsId);
            SetLink(msg, link);
            var msgId = ctx.Create(msg, "draft_message", J.Obj("audience", audience, "text", text));

            var taskId = CreateReviewTask(ctx, "Message Send", "Approval",
                (market ? "Send market teaser: " : "Send request: ") + (ctx.Subject == null ? ctx.Agent.DisplayName : ctx.Subject.GetAttributeValue<string>("gc_name") ?? ctx.Agent.DisplayName),
                J.Obj("messageId", ctx.DryRun ? null : msgId.ToString(), "audience", audience, "text", text,
                      "questionIds", market ? new List<object>() : ctx.QuestionIds.Where(q => q != Guid.Empty).Select(q => (object)q.ToString()).ToList()),
                ctx.Agent.SubjectTable == "gc_deal" ? "Deal Manager" : "Verification Officer", false);
            if (!ctx.DryRun && taskId != Guid.Empty)
            {
                var upd = new Entity("gc_generatedmessage", msgId);
                upd["gc_reviewtask"] = new EntityReference("gc_reviewtask", taskId);
                ctx.Dv.Svc.Update(upd);
            }
            return J.Obj("ok", true, "message_id", ctx.DryRun ? null : msgId.ToString(), "status", "Draft – awaiting human approval", "checks", report.Checks);
        }

        private static object ReviewTask(AgentContext ctx, Dictionary<string, object> a)
        {
            var id = CreateReviewTask(ctx, J.Str(a, "purpose"), J.Str(a, "kind"), J.Str(a, "title"),
                J.Obj("details", J.Str(a, "details")), J.Str(a, "assignee_role"), true);
            return J.Obj("ok", true, "review_task_id", id == Guid.Empty ? null : id.ToString());
        }

        /// <summary>Creates (or returns the existing open) review task for this subject and purpose.</summary>
        public static Guid CreateReviewTask(AgentContext ctx, string purpose, string kind, string title, Dictionary<string, object> payload, string role, bool dedupe)
        {
            var p = Choice.ValueOf(Choice.ReviewPurposes, purpose);
            var k = Choice.ValueOf(Choice.ReviewKinds, kind);
            if (p < 0) throw new ToolRefusal("purpose must be one of: " + string.Join(", ", Choice.ReviewPurposes));
            if (k < 0) throw new ToolRefusal("kind must be one of: " + string.Join(", ", Choice.ReviewKinds));
            var r = Choice.ValueOf(Choice.AdminRoles, role ?? "Verification Officer");
            if (r < 0) r = Choice.ValueOf(Choice.AdminRoles, "Verification Officer");
            var link = SubjectLink(ctx);

            if (dedupe && link != null)
            {
                var existing = ctx.Dv.Query("gc_reviewtask", new[] { "gc_reviewtaskid" }, 1,
                    link.LogicalName == "account" ? "gc_account" : link.LogicalName, ConditionOperator.Equal, link.Id,
                    "gc_purpose", ConditionOperator.Equal, p,
                    "gc_status", ConditionOperator.Equal, Choice.ReviewStatus.Open).FirstOrDefault();
                if (existing != null)
                {
                    ctx.Actions.Add(J.Obj("action", "create_review_task", "table", "gc_reviewtask", "id", existing.Id.ToString(), "dry_run", ctx.DryRun, "detail", J.Obj("purpose", purpose, "existing", true)));
                    return existing.Id;
                }
            }

            payload["agent"] = ctx.Agent.ApiName;
            payload["run"] = ctx.RunId.ToString();
            var t = new Entity("gc_reviewtask");
            t["gc_name"] = GeminiClient.Truncate(title ?? purpose, 100);
            t["gc_kind"] = new OptionSetValue(k);
            t["gc_purpose"] = new OptionSetValue(p);
            t["gc_assigneerole"] = new OptionSetValue(r);
            t["gc_status"] = new OptionSetValue(Choice.ReviewStatus.Open);
            t["gc_payload"] = GeminiClient.Truncate(Json.Serialize(payload), 100000);
            if (link != null) t[link.LogicalName == "account" ? "gc_account" : link.LogicalName] = link;
            var account = ScratchId(ctx, "account_id");
            if (account != null && link != null && link.LogicalName != "account") t["gc_account"] = new EntityReference("account", account.Value);
            return ctx.Create(t, "create_review_task", J.Obj("purpose", purpose, "kind", kind, "title", title, "role", role));
        }

        private static object ProposeMatch(AgentContext ctx, Dictionary<string, object> a)
        {
            object c;
            if (!ctx.Scratch.TryGetValue("candidates", out c)) throw new ToolRefusal("No candidates in this run.");
            var candidates = (Dictionary<string, Dictionary<string, object>>)c;
            var cid = J.Str(a, "candidate_id") ?? "";
            Dictionary<string, object> cand;
            if (!candidates.TryGetValue(cid, out cand)) throw new ToolRefusal("candidate_id must be one of: " + string.Join(", ", candidates.Keys));
            var spec = Math.Max(0, Math.Min(100, (int)(J.Num(a, "spec_score") ?? 0)));
            var dims = new Dictionary<string, object>(J.ObjOf(cand, "dimensions")) { ["specification"] = spec };
            var weights = J.Obj("quantity", 0.2, "specification", 0.35, "price", 0.2, "terms", 0.1, "trust", 0.15);
            double total = 0, wsum = 0;
            foreach (var w in weights)
            {
                var v = J.Num(dims, w.Key);
                if (v == null) continue;
                total += v.Value * (double)w.Value;
                wsum += (double)w.Value;
            }
            var score = wsum == 0 ? 0 : Math.Round(total / wsum, 1);

            var listingId = J.Id(cand, "listing_id").Value;
            var requirementId = J.Id(cand, "requirement_id").Value;
            var existing = ctx.Dv.Query("gc_match", new[] { "gc_matchid" }, 1,
                "gc_listing", ConditionOperator.Equal, listingId, "gc_requirement", ConditionOperator.Equal, requirementId).FirstOrDefault();
            if (existing != null) return J.Obj("ok", true, "match_id", existing.Id.ToString(), "score", score, "note", "Match already existed; not duplicated.");

            var m = new Entity("gc_match");
            m["gc_name"] = GeminiClient.Truncate("Match " + J.Str(cand, "label"), 100);
            m["gc_listing"] = new EntityReference("gc_listing", listingId);
            m["gc_requirement"] = new EntityReference("gc_buyerrequirement", requirementId);
            m["gc_score"] = (decimal)score;
            m["gc_explanation"] = Json.Serialize(J.Obj("overall", score, "dimensions", dims, "weights", weights, "notes", J.Get(cand, "notes"), "spec_reason", J.Str(a, "explanation")));
            m["gc_status"] = new OptionSetValue(Choice.MatchStatus.Proposed);
            m["gc_buyeroptin"] = false;
            m["gc_selleroptin"] = false;
            var id = ctx.Create(m, "propose_match", J.Obj("candidate", cid, "score", score));
            return J.Obj("ok", true, "match_id", ctx.DryRun ? null : id.ToString(), "score", score, "dimensions", dims);
        }

        private static object KycCheck(AgentContext ctx, Dictionary<string, object> a)
        {
            if (ctx.Agent.SubjectTable != "account" || ctx.SubjectId == null) throw new ToolRefusal("record_kyc_check is only available for account subjects.");
            var type = Choice.ValueOf(Choice.KycCheckTypes, J.Str(a, "check_type"));
            if (type < 0) throw new ToolRefusal("check_type must be one of: " + string.Join(", ", Choice.KycCheckTypes));
            var result = J.Str(a, "result");
            if (result != "Pending" && result != "Refer") throw new ToolRefusal("Agents may only record Pending or Refer. Pass/Fail is decided by a provider or an officer.");
            var existing = ctx.Dv.Query("gc_kyccheck", new[] { "gc_kyccheckid", "gc_result" }, 5,
                "gc_account", ConditionOperator.Equal, ctx.SubjectId.Value, "gc_checktype", ConditionOperator.Equal, type);
            if (existing.Any(e => e.GetAttributeValue<OptionSetValue>("gc_result") != null))
                return J.Obj("ok", true, "note", "A " + J.Str(a, "check_type") + " check already exists; not duplicated.", "existing", existing.Select(e => Dv.Label(e, "gc_result")).ToList());
            var k = new Entity("gc_kyccheck");
            k["gc_name"] = GeminiClient.Truncate(J.Str(a, "check_type") + " – " + result + ": " + J.Str(a, "notes"), 100);
            k["gc_account"] = new EntityReference("account", ctx.SubjectId.Value);
            k["gc_checktype"] = new OptionSetValue(type);
            k["gc_result"] = new OptionSetValue(Choice.ValueOf(Choice.KycResults, result));
            k["gc_provider"] = "DealOS agent";
            k["gc_providerref"] = ctx.RunId.ToString();
            var contact = J.Id(a, "contact_id");
            if (contact != null && !ctx.Dv.Exists("contact", contact.Value)) throw new ToolRefusal("contact_id " + contact + " does not exist; use an id from CONTEXT.contacts or omit it.");
            if (contact != null) k["gc_contact"] = new EntityReference("contact", contact.Value);
            var id = ctx.Create(k, "record_kyc_check", J.Obj("check_type", J.Str(a, "check_type"), "result", result, "notes", J.Str(a, "notes")));
            return J.Obj("ok", true, "kyc_check_id", ctx.DryRun ? null : id.ToString());
        }

        private static object ContractDraft(AgentContext ctx, Dictionary<string, object> a)
        {
            if (ctx.Agent.SubjectTable != "gc_deal" || ctx.SubjectId == null) throw new ToolRefusal("create_contract_draft needs a deal subject.");
            Dictionary<string, object> terms;
            try { terms = Json.ParseObject(J.Str(a, "terms_json") ?? "{}"); }
            catch (FormatException) { throw new ToolRefusal("terms_json must be a JSON object string."); }
            var existing = ctx.Dv.Query("gc_contract", new[] { "gc_contractid", "gc_status" }, 5, "gc_deal", ConditionOperator.Equal, ctx.SubjectId.Value)
                .Where(e => Dv.Label(e, "gc_status") != "Void").ToList();
            if (existing.Count > 0) throw new ToolRefusal("A contract already exists for this deal (" + Dv.Label(existing[0], "gc_status") + "). Do not create another.");
            var c = new Entity("gc_contract");
            c["gc_name"] = GeminiClient.Truncate("Contract – " + (ctx.Subject.GetAttributeValue<string>("gc_name") ?? "deal"), 100);
            c["gc_deal"] = new EntityReference("gc_deal", ctx.SubjectId.Value);
            c["gc_status"] = new OptionSetValue(Choice.ContractStatus.Draft);
            c["gc_nonstandardclauses"] = J.Bool(a, "nonstandard_clauses");
            c["gc_templateversion"] = GeminiClient.Truncate(J.Str(a, "template_version") ?? "spot-physical-v1", 100);
            var id = ctx.Create(c, "create_contract_draft", J.Obj("nonstandard", J.Bool(a, "nonstandard_clauses")));
            var task = CreateReviewTask(ctx, "Contract Issue", "Approval", "Review contract terms: " + (ctx.Subject.GetAttributeValue<string>("gc_name") ?? "deal"),
                J.Obj("contractId", ctx.DryRun ? null : id.ToString(), "terms", terms, "nonstandard", J.Bool(a, "nonstandard_clauses")), "Deal Manager", true);
            return J.Obj("ok", true, "contract_id", ctx.DryRun ? null : id.ToString(), "review_task_id", task == Guid.Empty ? null : task.ToString());
        }

        private static object Milestone(AgentContext ctx, Dictionary<string, object> a)
        {
            if (ctx.Agent.SubjectTable != "gc_deal" || ctx.SubjectId == null) throw new ToolRefusal("create_milestone needs a deal subject.");
            var type = Choice.ValueOf(Choice.MilestoneTypes, J.Str(a, "type"));
            if (type < 0) throw new ToolRefusal("type must be one of: " + string.Join(", ", Choice.MilestoneTypes));
            var existing = ctx.Dv.Query("gc_milestone", new[] { "gc_milestoneid" }, 1,
                "gc_deal", ConditionOperator.Equal, ctx.SubjectId.Value, "gc_type", ConditionOperator.Equal, type).FirstOrDefault();
            if (existing != null) return J.Obj("ok", true, "milestone_id", existing.Id.ToString(), "note", "Already exists.");
            var m = new Entity("gc_milestone");
            m["gc_name"] = J.Str(a, "type");
            m["gc_deal"] = new EntityReference("gc_deal", ctx.SubjectId.Value);
            m["gc_type"] = new OptionSetValue(type);
            m["gc_status"] = new OptionSetValue(Choice.MilestoneStatus.Pending);
            DateTime due;
            if (DateTime.TryParse(J.Str(a, "due_on"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out due)) m["gc_dueon"] = due;
            var id = ctx.Create(m, "create_milestone", J.Obj("type", J.Str(a, "type"), "due_on", J.Str(a, "due_on")));
            return J.Obj("ok", true, "milestone_id", ctx.DryRun ? null : id.ToString());
        }

        public static Dictionary<string, object> ReleaseSchedule(AgentContext ctx, double contractValue, List<Dictionary<string, object>> tranches)
        {
            if (contractValue <= 0) throw new ToolRefusal("contract_value must be positive.");
            var pctSum = tranches.Sum(t => J.Num(t, "pct") ?? 0);
            if (tranches.Count == 0 || Math.Abs(pctSum - 100) > 0.01) throw new ToolRefusal("Tranche percentages must sum to 100 (got " + pctSum + ").");
            var plan = ctx.Scratch.ContainsKey("commission_plan") ? (Dictionary<string, object>)ctx.Scratch["commission_plan"] : null;
            var rate = (decimal)(J.Num(plan, "rate_pct") ?? 0);
            var cv = (decimal)contractValue;
            var commission = Math.Round(cv * rate / 100m, 2);
            var min = J.Num(plan, "min_amount");
            var max = J.Num(plan, "max_amount");
            if (min != null && commission < (decimal)min.Value) commission = (decimal)min.Value;
            if (max != null && max.Value > 0 && commission > (decimal)max.Value) commission = (decimal)max.Value;
            var payer = J.Str(plan, "payer") ?? "Seller";
            var sellerShare = payer == "Split" ? (decimal)(J.Num(plan, "seller_share_pct") ?? 50) / 100m : payer == "Seller" ? 1m : 0m;
            var sellerCommission = Math.Round(commission * sellerShare, 2);

            var rows = new List<object>();
            decimal grossSum = 0, commSum = 0;
            for (var i = 0; i < tranches.Count; i++)
            {
                var pct = (decimal)(J.Num(tranches[i], "pct") ?? 0);
                var last = i == tranches.Count - 1;
                var gross = last ? cv - grossSum : Math.Round(cv * pct / 100m, 2);
                var comm = last ? sellerCommission - commSum : Math.Round(sellerCommission * pct / 100m, 2);
                grossSum += gross;
                commSum += comm;
                rows.Add(J.Obj("sequence", i + 1, "pct", pct, "condition", J.Str(tranches[i], "condition"), "gross", gross, "commission_deducted", comm, "net_to_seller", gross - comm));
            }
            return J.Obj("contract_value", cv, "commission_total", commission, "commission_payer", payer,
                         "commission_rate_pct", rate, "plan", plan == null ? "none on deal – commission 0" : J.Str(plan, "name"),
                         "buyer_commission", commission - sellerCommission, "tranches", rows,
                         "note", "Commission base = contract value. If the plan base is FOB and the contract is CIF, adjust after freight is known.");
        }

        // ---------- helpers ----------

        private static string Readable(AgentContext ctx, string table)
        {
            if (string.IsNullOrWhiteSpace(table)) throw new ToolRefusal("table is required.");
            table = table.Trim().ToLowerInvariant();
            if (table == "gc_secret" || !ctx.Agent.ReadTables.Contains(table))
                throw new ToolRefusal("Table '" + table + "' is not readable by this agent. Allowed: " + string.Join(", ", ctx.Agent.ReadTables));
            return table;
        }

        private static Guid RequireId(Dictionary<string, object> a, string key)
        {
            var id = J.Id(a, key);
            if (id == null) throw new ToolRefusal(key + " must be a GUID.");
            return id.Value;
        }

        private static void Map(Dictionary<string, object> a, OrganizationRequest req, string from, string to)
        {
            var v = J.Num(a, from);
            if (v != null) req[to] = (decimal)v.Value;
        }

        private static ConditionOperator Operator(string op)
        {
            switch (op)
            {
                case "eq": return ConditionOperator.Equal;
                case "ne": return ConditionOperator.NotEqual;
                case "gt": return ConditionOperator.GreaterThan;
                case "ge": return ConditionOperator.GreaterEqual;
                case "lt": return ConditionOperator.LessThan;
                case "le": return ConditionOperator.LessEqual;
                case "like": return ConditionOperator.Like;
                case "null": return ConditionOperator.Null;
                case "notnull": return ConditionOperator.NotNull;
                default: throw new ToolRefusal("Unsupported operator: " + op);
            }
        }

        /// <summary>Converts a filter value to the column's type; refuses rather than letting Dataverse throw.</summary>
        private static object Coerce(string v, Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode? type, string col)
        {
            if (v == null) throw new ToolRefusal("A value is required for column " + col + ".");
            Guid g; int i; decimal d; bool b; DateTime dt;
            switch (type)
            {
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Lookup:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Customer:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Owner:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Uniqueidentifier:
                    if (Guid.TryParse(v, out g)) return g;
                    break;
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Picklist:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.State:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Status:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Integer:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.BigInt:
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
                    break;
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Decimal:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Double:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Money:
                    if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                    break;
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Boolean:
                    if (bool.TryParse(v, out b)) return b;
                    break;
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.DateTime:
                    if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt)) return dt;
                    break;
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.String:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Memo:
                    return v;
                default:
                    throw new ToolRefusal("Column " + col + " (" + type + ") cannot be filtered with query_records.");
            }
            throw new ToolRefusal("Value '" + v + "' is not valid for column " + col + " (" + type + ").");
        }

        private static readonly Dictionary<int, string> SubjectTables = new Dictionary<int, string>
        {
            { Choice.SubjectType.Party, "account" }, { Choice.SubjectType.Contact, "contact" }, { Choice.SubjectType.Asset, "gc_asset" },
            { Choice.SubjectType.Licence, "gc_licence" }, { Choice.SubjectType.Listing, "gc_listing" }, { Choice.SubjectType.Lot, "gc_lot" },
            { Choice.SubjectType.Deal, "gc_deal" }, { Choice.SubjectType.Document, "gc_document" }
        };

        private static Guid RequireSubject(AgentContext ctx, Dictionary<string, object> a)
        {
            var type = Evidence.SubjectTypeOfLabel(J.Str(a, "subject_type"));
            var id = RequireId(a, "subject_id");
            if (!ctx.Dv.Exists(SubjectTables[type], id)) throw new ToolRefusal("No " + J.Str(a, "subject_type") + " with id " + id + ".");
            return id;
        }

        public static EntityReference SubjectLink(AgentContext ctx)
        {
            var r = ctx.SubjectRef;
            if (r == null) return null;
            return r.LogicalName == "gc_listing" || r.LogicalName == "gc_deal" || r.LogicalName == "account" ? r : null;
        }

        private static void SetLink(Entity e, EntityReference link)
        {
            if (link == null) return;
            if (link.LogicalName == "gc_listing") e["gc_listing"] = link;
            else if (link.LogicalName == "gc_deal") e["gc_deal"] = link;
        }

        public static List<FactRow> SubjectFacts(AgentContext ctx)
        {
            object cached;
            if (ctx.Scratch.TryGetValue("facts", out cached)) return (List<FactRow>)cached;
            if (ctx.SubjectId == null || ctx.Agent.SubjectTable == null) return new List<FactRow>();
            var facts = Evidence.Facts(ctx, Evidence.SubjectTypeOf(ctx.Agent.SubjectTable), ctx.SubjectId.Value);
            ctx.Scratch["facts"] = facts;
            return facts;
        }

        private static IEnumerable<string> ScratchList(AgentContext ctx, string key)
        {
            object v;
            return ctx.Scratch.TryGetValue(key, out v) && v is List<string> ? (List<string>)v : new List<string>();
        }

        private static Guid? ScratchId(AgentContext ctx, string key)
        {
            object v;
            return ctx.Scratch.TryGetValue(key, out v) && v is Guid ? (Guid?)v : null;
        }
    }
}

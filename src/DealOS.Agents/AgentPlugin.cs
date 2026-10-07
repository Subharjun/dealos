using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Agents;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using DealOS.Agents.Tools;
using Microsoft.Xrm.Sdk;

namespace DealOS.Agents
{
    public static class AgentCatalog
    {
        public static readonly IReadOnlyList<AgentDefinition> All = new AgentDefinition[]
        {
            new DocumentIntelligenceAgent(),
            new ListingVerificationAgent(),
            new PartyVerificationAgent(),
            new BuyerVerificationAgent(),
            new ComplianceAgent(),
            new MatchingAgent(),
            new PricingAgent(),
            new NegotiationAgent(),
            new ContractAgent(),
            new PaymentAgent(),
            new LogisticsAgent(),
            new AdminSupervisorAgent(),
            new BuyerConciergeAgent(),
            new SellerAssistantAgent(),
            new MailTriageAgent(),
            new TradeDeskAgent()
        };

        public static AgentDefinition Find(string messageName)
        {
            return All.FirstOrDefault(a => string.Equals(a.ApiName, messageName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Implementation of every gc_Agent_* Custom API.
    /// Inputs: SubjectId (string, optional for agents without a subject), Input (JSON string, optional), DryRun (bool, optional).
    /// Outputs: AgentRunId, Status (Succeeded | Failed), Summary, NeedsHuman, Result (JSON).
    /// Agent failures are returned, not thrown, so the run log survives the transaction.
    /// </summary>
    public sealed class AgentPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            var def = AgentCatalog.Find(context.MessageName);
            if (def == null) throw new InvalidPluginExecutionException("No DealOS agent is registered for message " + context.MessageName + ".");

            var input = new Dictionary<string, object>();
            var inputText = Get<string>(context, "Input");
            if (!string.IsNullOrWhiteSpace(inputText))
            {
                try { input = Json.ParseObject(inputText); }
                catch (FormatException ex) { throw new InvalidPluginExecutionException("Input must be a JSON object: " + ex.Message); }
            }

            Guid? subjectId = null;
            var subject = Get<string>(context, "SubjectId");
            if (def.SubjectTable != null)
            {
                Guid id;
                if (string.IsNullOrWhiteSpace(subject) || !Guid.TryParse(subject, out id))
                    throw new InvalidPluginExecutionException(def.ApiName + " requires SubjectId: the GUID of a " + def.SubjectTable + " record.");
                subjectId = id;
            }

            var dv = new Dv(factory.CreateOrganizationService(context.UserId), factory.CreateOrganizationService(null));
            var run = AgentHost.Run(def, dv, trace, subjectId, input, Get<bool?>(context, "DryRun") ?? false, "custom-api:" + def.ApiName);

            context.OutputParameters["AgentRunId"] = run.Context.RunId.ToString();
            context.OutputParameters["Status"] = run.Outcome.Status;
            context.OutputParameters["Summary"] = run.Outcome.Summary ?? run.Outcome.Error ?? "";
            context.OutputParameters["NeedsHuman"] = run.Outcome.Status != "Succeeded" || run.Outcome.NeedsHuman;
            context.OutputParameters["Result"] = Json.Serialize(run.Result);
        }

        private static T Get<T>(IPluginExecutionContext context, string name)
        {
            object v;
            return context.InputParameters.TryGetValue(name, out v) && v is T ? (T)v : default(T);
        }
    }

    /// <summary>Runs one agent with run logging; shared by the Custom APIs and the chat trigger.</summary>
    public static class AgentHost
    {
        public sealed class RunResult
        {
            public AgentContext Context;
            public AgentOutcome Outcome;
            public Dictionary<string, object> Result;
        }

        /// <summary>Throws InvalidPluginExecutionException when Dataverse rejected an operation (the transaction is lost anyway).</summary>
        public static RunResult Run(AgentDefinition def, Dv dv, ITracingService trace, Guid? subjectId, Dictionary<string, object> input, bool dryRun, string trigger)
        {
            var started = DateTime.UtcNow;
            var ctx = new AgentContext
            {
                Agent = def,
                Dv = dv,
                Trace = trace,
                DryRun = dryRun,
                SubjectId = subjectId,
                Input = input ?? new Dictionary<string, object>(),
                Deadline = started.AddSeconds(Math.Max(30, Math.Min(110, dv.SettingInt("agents.time_budget_seconds", 100)))),
                Model = dv.Setting(def.ModelSettingKey) ?? dv.Setting("agents.model.default") ?? "gemini-3.5-flash"
            };

            var log = new RunLogger(dv);
            ctx.RunId = log.StartRun(ctx, J.Str(ctx.Input, "trigger") ?? trigger);
            var outcome = new AgentOutcome();
            try
            {
                if (ctx.SubjectId != null) ctx.Subject = dv.Retrieve(def.SubjectTable, ctx.SubjectId.Value);
                var apiKey = dv.Secret("gemini.api_key");
                if (string.IsNullOrWhiteSpace(apiKey)) throw new ToolRefusal("Gemini API key is not configured (gc_secret 'gemini.api_key').");
                var gemini = new GeminiClient(apiKey, dv.Setting("agents.gemini_base_url")) { CallTimeout = TimeSpan.FromSeconds(Math.Max(10, dv.SettingInt("agents.call_timeout_seconds", 45))) };
                var runtime = new AgentRuntime(gemini, log);
                outcome = runtime.Run(ctx);
            }
            catch (ToolRefusal ex)
            {
                outcome.Error = ex.Message;
            }
            catch (System.ServiceModel.FaultException<OrganizationServiceFault> ex)
            {
                ctx.Poisoned = ex.Message;
            }
            catch (InvalidPluginExecutionException ex)
            {
                ctx.Poisoned = ex.Message;
            }
            catch (Exception ex)
            {
                trace.Trace("Agent {0} failed: {1}", def.ApiName, ex);
                outcome.Error = ex.GetType().Name + ": " + ex.Message;
            }

            if (ctx.Poisoned != null)
                throw new InvalidPluginExecutionException(def.ApiName + " stopped because Dataverse rejected an operation: " + ctx.Poisoned +
                    " No changes from this run were saved (run log rolled back too).");

            try
            {
                log.FinishRun(ctx, outcome);
                log.Audit(ctx, outcome);
            }
            catch (Exception ex)
            {
                trace.Trace("Agent run logging failed: {0}", ex);
            }

            var result = J.Obj(
                "agent", def.ApiName,
                "version", def.Version,
                "run_id", ctx.RunId.ToString(),
                "status", outcome.Status,
                "dry_run", ctx.DryRun,
                "model", ctx.Model,
                "steps", outcome.Steps,
                "tokens_in", outcome.TokensIn,
                "tokens_out", outcome.TokensOut,
                "seconds", Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
                "error", outcome.Error,
                "result", outcome.Result,
                "actions", ctx.Actions);
            return new RunResult { Context = ctx, Outcome = outcome, Result = result };
        }
    }
}

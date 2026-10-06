using System;
using System.Collections.Generic;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;

namespace DealOS.Agents.Runtime
{
    /// <summary>
    /// One DealOS agent: who it is, what it may see and do, and what it must return.
    /// Each agent is exposed as the Dataverse Custom API "gc_Agent_{Name}".
    /// </summary>
    public abstract class AgentDefinition
    {
        /// <summary>Short PascalCase name, e.g. "ListingVerification".</summary>
        public abstract string Name { get; }

        public string ApiName { get { return "gc_Agent_" + Name; } }

        public abstract string DisplayName { get; }

        public abstract string Description { get; }

        /// <summary>Value of the global choice gc_agent used on gc_agentrun.</summary>
        public abstract int AgentChoice { get; }

        public virtual string Version { get { return "1.0.0"; } }

        /// <summary>Logical name of the table SubjectId points at; null when the agent has no subject.</summary>
        public abstract string SubjectTable { get; }

        /// <summary>gc_platformsetting key holding the model name for this agent.</summary>
        public virtual string ModelSettingKey { get { return "agents.model.default"; } }

        public abstract string Instructions { get; }

        /// <summary>Schema of the result passed to finish (or returned in structured mode).</summary>
        public abstract Dictionary<string, object> OutputSchema { get; }

        /// <summary>Tools the model may call, besides finish.</summary>
        public virtual string[] Tools { get { return new string[0]; } }

        /// <summary>Tables the read tools may touch for this agent.</summary>
        public virtual string[] ReadTables { get { return new string[0]; } }

        public virtual int MaxSteps { get { return 8; } }

        /// <summary>True: one structured call without tools (cheaper, used for extraction).</summary>
        public virtual bool StructuredOnly { get { return false; } }

        /// <summary>Deterministic prefetch: everything the model needs, so it rarely has to call read tools.</summary>
        public abstract Dictionary<string, object> BuildContext(AgentContext ctx);

        /// <summary>Extra content parts for the first user turn (e.g. an inline PDF).</summary>
        public virtual List<object> ExtraParts(AgentContext ctx) { return null; }

        /// <summary>Deterministic post-processing and guards after the model has finished.</summary>
        public virtual void AfterFinish(AgentContext ctx, Dictionary<string, object> result) { }

        /// <summary>Common result fields every agent returns.</summary>
        protected static Dictionary<string, object> Output(string description, params object[] props)
        {
            var all = new List<object>(props)
            {
                "summary", S.Str("Two or three plain sentences for a human operator: what you found and what happens next."),
                "needs_human", S.Bool("True when a person must decide or act before the process can continue."),
                "confidence", S.Enum("How confident you are in this result.", "High", "Medium", "Low")
            };
            return S.Obj(description, all.ToArray());
        }

        /// <summary>Links the run record to the subject (gc_agentrun has listing, deal and account lookups).</summary>
        public virtual void LinkRun(Entity run, AgentContext ctx)
        {
            if (ctx.SubjectId == null) return;
            switch (SubjectTable)
            {
                case "gc_listing": run["gc_listing"] = new EntityReference("gc_listing", ctx.SubjectId.Value); break;
                case "gc_deal": run["gc_deal"] = new EntityReference("gc_deal", ctx.SubjectId.Value); break;
                case "account": run["gc_account"] = new EntityReference("account", ctx.SubjectId.Value); break;
            }
        }

        public override string ToString() { return ApiName; }

        protected static Guid? RefId(Entity e, string column)
        {
            var r = e == null ? null : e.GetAttributeValue<EntityReference>(column);
            return r == null ? (Guid?)null : r.Id;
        }
    }
}

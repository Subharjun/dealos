using System;
using System.Collections.Generic;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Tools;
using Microsoft.Xrm.Sdk;

namespace DealOS.Agents.Runtime
{
    /// <summary>State of one agent run, shared by the runtime, the tools and the agent definition.</summary>
    public sealed class AgentContext
    {
        public AgentDefinition Agent;
        public Dv Dv;
        public ITracingService Trace;
        public Guid RunId;
        public Guid? SubjectId;
        public Entity Subject;
        public Dictionary<string, object> Input = new Dictionary<string, object>();
        public bool DryRun;
        public DateTime Deadline;
        public string Model;

        /// <summary>Writes performed (or, in a dry run, proposed) by tools and guards.</summary>
        public readonly List<object> Actions = new List<object>();

        /// <summary>Per-run cache for agent-specific data (e.g. matching candidates, fact sheet).</summary>
        public readonly Dictionary<string, object> Scratch = new Dictionary<string, object>();

        /// <summary>Every number the model was shown; drafted messages may only use these.</summary>
        public readonly HashSet<string> KnownNumbers = new HashSet<string>();

        public readonly List<Guid> QuestionIds = new List<Guid>();

        /// <summary>Set when a Dataverse call failed inside the transaction; the run must stop and the plug-in must throw.</summary>
        public string Poisoned;

        public void Log(string message)
        {
            if (Trace != null) Trace.Trace("[{0}] {1}", Agent == null ? "agent" : Agent.Name, message);
        }

        /// <summary>Creates a record unless this is a dry run; always records the action.</summary>
        public Guid Create(Entity e, string action, object detail = null)
        {
            var id = Guid.Empty;
            if (!DryRun) id = Dv.Svc.Create(e);
            Actions.Add(J.Obj("action", action, "table", e.LogicalName, "id", DryRun ? null : id.ToString(), "dry_run", DryRun, "detail", detail));
            return id;
        }

        public void Update(Entity e, string action, object detail = null)
        {
            if (!DryRun) Dv.Svc.Update(e);
            Actions.Add(J.Obj("action", action, "table", e.LogicalName, "id", e.Id.ToString(), "dry_run", DryRun, "detail", detail));
        }

        public EntityReference SubjectRef
        {
            get { return SubjectId == null || Agent.SubjectTable == null ? null : new EntityReference(Agent.SubjectTable, SubjectId.Value); }
        }

        /// <summary>Remembers numbers in data shown to the model (used by the message validator).</summary>
        public void Remember(object data)
        {
            MessageValidator.CollectNumbers(Json.Serialize(data), KnownNumbers);
        }
    }
}

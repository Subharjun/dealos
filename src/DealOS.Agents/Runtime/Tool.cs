using System;
using System.Collections.Generic;

namespace DealOS.Agents.Runtime
{
    /// <summary>A function the model may call. Errors are returned to the model as {"error": "..."} so it can adapt.</summary>
    public sealed class Tool
    {
        public string Name;
        public string Description;
        public Dictionary<string, object> Parameters;
        public bool Writes;
        public Func<AgentContext, Dictionary<string, object>, object> Run;
    }

    /// <summary>Thrown by a tool when the call is refused; the message goes back to the model.</summary>
    public sealed class ToolRefusal : Exception
    {
        public ToolRefusal(string message) : base(message) { }
    }
}

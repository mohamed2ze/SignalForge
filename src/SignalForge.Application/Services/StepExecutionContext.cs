using System.Text.Json.Nodes;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Runtime context handed to step processors for the current workflow execution.
    /// <c>Root</c> is a JSON object built by the orchestrator, e.g.:
    /// { "event": { "type", "externalEventId", "occurredAt", "receivedAt", "payload" }, "output": { "&lt;stepNumber&gt;": {...} } }.
    /// Conditional steps evaluate JSON-path expressions (e.g. <c>$.event.type</c>, <c>$.output.2.status</c>)
    /// against this root.
    /// </summary>
    public sealed class StepExecutionContext
    {
        public StepExecutionContext(JsonNode? root = null)
        {
            Root = root;
        }

        public JsonNode? Root { get; }
    }
}
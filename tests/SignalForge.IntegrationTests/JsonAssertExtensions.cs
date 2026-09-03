using System.Text.Json.Nodes;

namespace SignalForge.IntegrationTests;

internal static class GuidJsonExtensions
{
    public static Guid AsGuid(this JsonNode? node) => Guid.Parse((string)node!);
}
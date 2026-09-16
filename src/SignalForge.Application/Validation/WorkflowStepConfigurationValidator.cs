using System.Text.Json;

namespace SignalForge.Application.Validation;

/// <summary>
/// Validates a workflow step's <c>Configuration</c> JSON against the schema the matching step
/// processor actually consumes. Each step type lists the properties its processor reads with
/// <see cref="JsonElement.GetProperty(string)"/> — a step whose config is missing one of those
/// would fail at execution time, so the authoring surface rejects it up front. Unknown step types
/// are rejected because no processor could ever run them.
/// </summary>
public static class WorkflowStepConfigurationValidator
{
    private sealed record RequiredField(string Display, JsonValueKind Kind);

    private static readonly IReadOnlyDictionary<string, RequiredField[]> Schemas =
        new Dictionary<string, RequiredField[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["HttpWebhook"] = [new RequiredField("url", JsonValueKind.String)],
            ["Delay"] = [new RequiredField("seconds", JsonValueKind.Number)],
            ["Conditional"] = [new RequiredField("expression", JsonValueKind.String)],
            ["LogAudit"] = [new RequiredField("message", JsonValueKind.String)],
            ["NotificationSimulation"] =
            [
                new RequiredField("recipient", JsonValueKind.String)
            ],
            ["EventEmission"] = [new RequiredField("eventType", JsonValueKind.String)],
            ["RetryableOperation"] = [new RequiredField("operationType", JsonValueKind.String)]
        };

    public static string? Validate(string stepType, string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
            return "Step configuration must be non-empty JSON.";

        if (!Schemas.TryGetValue(stepType, out var required))
            return $"Unknown step type '{stepType}'. Valid types: {string.Join(", ", Schemas.Keys)}.";

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(configurationJson);
        }
        catch (JsonException ex)
        {
            return $"Step configuration is not valid JSON: {ex.Message}";
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "Step configuration must be a JSON object.";

            foreach (var field in required)
            {
                if (!root.TryGetProperty(field.Display, out var value))
                    return $"Step configuration for '{stepType}' is missing required field '{field.Display}'.";

                // Number fields allow any numeric kind; strings must actually be numeric.
                if (field.Kind == JsonValueKind.Number)
                {
                    if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
                        !TryReadNonNegativeNumber(value))
                        return $"Step configuration field '{field.Display}' must be a non-negative number.";
                }
                else if (value.ValueKind != JsonValueKind.String || value.GetString() is null)
                {
                    return $"Step configuration field '{field.Display}' must be a string.";
                }
            }
        }

        return null;
    }

    private static bool TryReadNonNegativeNumber(JsonElement value)
    {
        try
        {
            if (!value.TryGetDecimal(out var number))
                return false;
            return number >= 0;
        }
        catch (InvalidOperationException)
        {
            // Wrong element kind (e.g. a string without a valid numeric representation).
            return false;
        }
    }
}
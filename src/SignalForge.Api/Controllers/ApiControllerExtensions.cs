using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SignalForge.Api.Controllers;

/// <summary>
/// Shared controller helpers (leftover cleanup pass): tenant-claim extraction, the canonical
/// tenant 401, ISO-8601 window parsing, and the common 500 error response — previously
/// copy-pasted across Events/DeadLetter/Executions/Workflows controllers.
/// </summary>
internal static class ApiControllerExtensions
{
    /// <summary>
    /// Resolves the caller's tenant from the <c>tenant_id</c> claim set by the API-key/bearer
    /// authentication handler. Returns false when the claim is absent or not a valid Guid.
    /// </summary>
    internal static bool TryGetTenantId(this ControllerBase controller, out Guid tenantId)
    {
        var tenantIdClaim = controller.User.FindFirst("tenant_id");
        if (tenantIdClaim == null || !Guid.TryParse(tenantIdClaim.Value, out tenantId))
        {
            tenantId = Guid.Empty;
            return false;
        }
        return true;
    }

    internal static ProblemDetails TenantProblem() => new ProblemDetails
    {
        Title = "Invalid tenant information",
        Status = StatusCodes.Status401Unauthorized,
        Detail = "Unable to determine tenant from authentication token"
    };

    internal static ObjectResult InternalServerError(this ControllerBase controller, string detail)
        => controller.StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
        {
            Title = "Internal server error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = detail
        });

    /// <summary>
    /// Parses an optional ISO-8601 instant. Null means "no bound"; a non-null invalid value parses
    /// to false so the caller can reject the request with 400.
    /// </summary>
    internal static bool TryParseUtcInstant(string? value, out DateTime? utc)
    {
        utc = null;
        if (value == null)
            return true;

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }

        return false;
    }

    internal static bool TryParseWindow(
        string? from,
        string? to,
        out DateTime? fromUtc,
        out DateTime? toUtc)
    {
        fromUtc = null;
        toUtc = null;

        if (from != null)
        {
            if (!TryParseUtcInstant(from, out var parsedFrom))
                return false;
            fromUtc = parsedFrom;
        }

        if (to != null)
        {
            if (!TryParseUtcInstant(to, out var parsedTo))
                return false;
            toUtc = parsedTo;
        }

        return true;
    }
}
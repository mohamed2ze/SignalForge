using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;
using SignalForge.Application.Security;

namespace SignalForge.Api.Middleware;

/// <summary>
/// Enforces the webhook signing scheme (Decision #23) on every <c>POST /api/events</c> request.
/// Runs after authentication (so the tenant claim exists) and before the controller/model
/// binding, using the RAW request body bytes that the signature was computed over. Any failure —
/// missing/invalid headers, tampered body, wrong secret, stale timestamp — returns 401 before a
/// single DB write. Never logs the secret or echoes the signature/timestamp.
/// </summary>
public class EventsSignatureMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISignalForgeDbContext _dbContext;
    private readonly ILogger<EventsSignatureMiddleware> _logger;

    public EventsSignatureMiddleware(
        RequestDelegate next,
        ISignalForgeDbContext dbContext,
        ILogger<EventsSignatureMiddleware> logger)
    {
        _next = next;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var isEventPost =
            HttpMethods.IsPost(request.Method) &&
            request.Path.StartsWithSegments("/api/events");

        if (!isEventPost)
        {
            await _next(context);
            return;
        }

        var tenantIdClaim = context.User.FindFirst("tenant_id");
        if (tenantIdClaim == null || !Guid.TryParse(tenantIdClaim.Value, out var tenantId))
        {
            await RejectAsync(context,
                "Unable to determine tenant from authentication token");
            return;
        }

        if (!request.Headers.TryGetValue(EventSignatureVerifier.TimestampHeader, out var timestampValue) ||
            string.IsNullOrWhiteSpace(timestampValue.ToString()) ||
            !EventSignatureVerifier.TryParseUnixSeconds(timestampValue.ToString(), out var timestampUnixSeconds))
        {
            await RejectAsync(context,
                $"Missing or invalid '{EventSignatureVerifier.TimestampHeader}' header");
            return;
        }

        if (!request.Headers.TryGetValue(EventSignatureVerifier.SignatureHeader, out var signatureValue) ||
            string.IsNullOrWhiteSpace(signatureValue.ToString()))
        {
            await RejectAsync(context,
                $"Missing '{EventSignatureVerifier.SignatureHeader}' header");
            return;
        }

        // Read the raw body bytes exactly as transmitted, then rewind so model binding sees them.
        request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(
                   request.Body,
                   Encoding.UTF8,
                   detectEncodingFromByteOrderMarks: true,
                   bufferSize: 4096,
                   leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(context.RequestAborted);
        }
        request.Body.Position = 0;

        var signingSetting = await _dbContext.TenantWebhookSigningSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, context.RequestAborted);

        if (signingSetting is null)
        {
            _logger.LogWarning(
                "Event POST rejected for tenant {TenantId}: no webhook signing secret configured",
                tenantId);
            await RejectAsync(context, "Webhook signing is not configured for this tenant");
            return;
        }

        var signatureHeader = signatureValue.ToString();
        var valid = EventSignatureVerifier.Verify(
            signingSetting.SigningSecret,
            timestampUnixSeconds,
            rawBody,
            signatureHeader);

        if (!valid)
        {
            _logger.LogWarning(
                "Event POST rejected for tenant {TenantId}: invalid signature body/timestamp/secret",
                tenantId);
            await RejectAsync(context, "Invalid signature");
            return;
        }

        await _next(context);
    }

    private static async Task RejectAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Signature verification failed",
            Status = StatusCodes.Status401Unauthorized,
            Detail = detail
        });
    }
}
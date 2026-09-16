using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SignalForge.Application.Services;
using SignalForge.Domain.ValueObjects;

namespace SignalForge.Api.Middleware;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly IApiKeyValidationService _apiKeyValidationService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,

        IApiKeyValidationService apiKeyValidationService) : base(options, logger, encoder)
    {
        _apiKeyValidationService = apiKeyValidationService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for API key in header
        if (!Request.Headers.TryGetValue(Options.HeaderName, out var apiKeyHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var providedApiKey = apiKeyHeaderValues.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(providedApiKey))
        {
            return AuthenticateResult.NoResult();
        }

        try
        {
            // Validate the API key
            var validationResult = await _apiKeyValidationService.ValidateApiKeyAsync(providedApiKey);

            if (!validationResult.IsValid)
            {
                return AuthenticateResult.Fail("Invalid API key.");
            }

            // A valid validation result is guaranteed to carry the tenant and key ids.
            var tenantId = validationResult.TenantId!.Value.ToString();
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, tenantId),
                new Claim("tenant_id", tenantId),
                new Claim("api_key_id", validationResult.ApiKeyId!.Value.ToString()),
                new Claim(ClaimTypes.Name, validationResult.ApiKeyName ?? string.Empty)
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            // Surface the tenant's webhook signing secret to the events pipeline without another
            // DB round-trip. It is sensitive, so it travels only via the ephemeral HttpContext item.
            if (!string.IsNullOrEmpty(validationResult.TenantWebhookSigningSecret))
                Context.Items[EventsSignatureMiddleware.SigningSecretContextItem] =
                    validationResult.TenantWebhookSigningSecret;

            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error validating API key");
            return AuthenticateResult.Fail("Internal error during authentication");
        }
    }
}

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public string HeaderName { get; set; } = "X-API-Key";
}

public static class ApiKeyAuthenticationExtensions
{
    public static AuthenticationBuilder AddApiKeyAuthentication(
        this AuthenticationBuilder builder,
        Action<ApiKeyAuthenticationOptions>? configureOptions = null)
    {
        return builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationDefaults.AuthenticationScheme,
            ApiKeyAuthenticationDefaults.DisplayName,
            configureOptions);
    }
}

public static class ApiKeyAuthenticationDefaults
{
    public const string AuthenticationScheme = "ApiKey";
    public const string DisplayName = "API Key Authentication";
}
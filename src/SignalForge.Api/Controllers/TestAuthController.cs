using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignalForge.Application.Services;

namespace SignalForge.Api.Controllers;

/// <summary>
/// Test controller for API key authentication.
/// </summary>
[ApiController]
[Route("[controller]")]
[Authorize] // Requires authentication
public class TestAuthController : ControllerBase
{
    private readonly IApiKeyValidationService _apiKeyValidationService;

    public TestAuthController(IApiKeyValidationService apiKeyValidationService)
    {
        _apiKeyValidationService = apiKeyValidationService;
    }

    /// <summary>
    /// Gets the current user information from the validated API key.
    /// </summary>
    [HttpGet("me")]
    public IActionResult GetCurrentUserInfo()
    {
        // The user information is available from HttpContext.User after authentication
        var tenantId = User.FindFirst("tenant_id")?.Value;
        var apiKeyId = User.FindFirst("api_key_id")?.Value;
        var apiKeyName = User.FindFirst(ClaimTypes.Name)?.Value;

        return Ok(new
        {
            TenantId = tenantId,
            ApiKeyId = apiKeyId,
            ApiKeyName = apiKeyName,
            Authenticated = true
        });
    }

    /// <summary>
    /// Public endpoint that doesn't require authentication.
    /// </summary>
    [HttpGet("public")]
    [AllowAnonymous]
    public IActionResult GetPublicInfo()
    {
        return Ok(new { Message = "This is a public endpoint", Timestamp = DateTime.UtcNow });
    }
}
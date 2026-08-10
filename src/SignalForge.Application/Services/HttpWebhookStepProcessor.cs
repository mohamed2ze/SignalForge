using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Processes HTTP webhook workflow steps.
    /// </summary>
    public class HttpWebhookStepProcessor : IStepProcessor
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<HttpWebhookStepProcessor> _logger;

        public HttpWebhookStepProcessor(
            HttpClient httpClient,
            ILogger<HttpWebhookStepProcessor> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<bool> ProcessAsync(
            WorkflowStepExecution stepExecution,
            StepExecutionContext? context,
            CancellationToken cancellationToken = default)
        {
            // Parse configuration
            var config = JsonDocument.Parse(stepExecution.WorkflowStep.Configuration).RootElement;

            var url = config.GetProperty("url").GetString()!;
            var method = config.GetProperty("method").GetString()?.ToUpper() ?? "POST";
            var headersJson = config.GetProperty("headers").GetRawText();
            var bodyJson = config.GetProperty("body").GetRawText();

            // Create HTTP request
            var request = new HttpRequestMessage(new HttpMethod(method), url);

            // Add headers
            if (!string.IsNullOrEmpty(headersJson))
            {
                var headers = JsonDocument.Parse(headersJson).RootElement;
                foreach (var header in headers.EnumerateObject())
                {
                    request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()!);
                }
            }

            // Add body if present
            if (!string.IsNullOrEmpty(bodyJson) && bodyJson.Trim() != "" && bodyJson.Trim() != "{}")
            {
                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            }

            try
            {
                _logger.LogInformation("Executing HTTP webhook step {StepId} to {Url}", stepExecution.Id, url);

                // Send request
                var response = await _httpClient.SendAsync(request, cancellationToken);

                // Read response
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Check if successful (2xx status code)
                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                {
                    _logger.LogInformation("HTTP webhook step {StepId} succeeded with status {StatusCode}",
                        stepExecution.Id, (int)response.StatusCode);

                    // Store response in step execution output
                    stepExecution.Succeed(responseBody);
                    return true; // Continue to next step
                }
                else
                {
                    var errorMsg = $"HTTP webhook step failed with status {(int)response.StatusCode}: {responseBody}";
                    _logger.LogWarning("HTTP webhook step {StepId} {ErrorMsg}", stepExecution.Id, errorMsg);

                    stepExecution.Fail(errorMsg);
                    return false; // Will trigger retry logic
                }
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
            {
                var errorMsg = $"Network error calling webhook: {ex.Message}";
                _logger.LogError(ex, "HTTP webhook step {StepId} network error", stepExecution.Id);

                stepExecution.Fail(errorMsg);
                return false; // Will trigger retry logic (network errors are typically retryable)
            }
            catch (TaskCanceledException)
            {
                var errorMsg = "HTTP webhook request timed out";
                _logger.LogWarning("HTTP webhook step {StepId} timed out", stepExecution.Id);

                stepExecution.Fail(errorMsg);
                return false; // Will trigger retry logic
            }
            catch (Exception ex)
            {
                var errorMsg = $"Unexpected error processing webhook: {ex.Message}";
                _logger.LogError(ex, "HTTP webhook step {StepId} unexpected error", stepExecution.Id);

                stepExecution.Fail(errorMsg);
                return false; // Will trigger retry logic
            }
        }
    }
}
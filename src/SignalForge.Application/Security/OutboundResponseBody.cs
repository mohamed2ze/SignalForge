using System.Text;

namespace SignalForge.Application.Security;

/// <summary>
/// Reads an outbound webhook response body up to a size cap so a malicious or misconfigured target
/// cannot force unbounded memory use. Exceeding the cap throws so the delivery is treated as failed
/// rather than silently truncated.
/// </summary>
public static class OutboundResponseBody
{
    public static async Task<string> ReadCappedAsync(
        HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (buffer.Length + read > maxBytes)
                throw new InvalidOperationException(
                    $"Outbound webhook response exceeds the {maxBytes}-byte limit.");

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
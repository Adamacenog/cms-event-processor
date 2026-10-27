using System.Net;
using System.Net.Http.Headers;

namespace CmsEventProcessor.IntegrationTests.Webhook;

/// <summary>
/// Sends a body without a computed Content-Length so size enforcement must inspect actual bytes.
/// </summary>
/// <param name="bytes">The synthetic request bytes.</param>
internal sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    /// <summary>
    /// Copies synthetic bytes to the request transport.
    /// </summary>
    /// <param name="stream">The outgoing HTTP body stream.</param>
    /// <param name="context">Unused transport context.</param>
    /// <returns>A task completing when all bytes have been sent.</returns>
    /// <exception cref="IOException">The transport cannot be written.</exception>
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

    /// <summary>
    /// Deliberately withholds length metadata to exercise bounded streaming reads.
    /// </summary>
    /// <param name="length">Zero because the transport must discover the body length.</param>
    /// <returns>False to avoid sending a computed Content-Length.</returns>
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

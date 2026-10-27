namespace CmsEventProcessor.Api.Diagnostics;

/// <summary>
/// Suppresses framework diagnostics that print confidential URL identifiers/cursors or action values while retaining safe application logs.
/// </summary>
internal static class PrivacyLogging
{
    /// <summary>
    /// Disables raw request, routing, and MVC diagnostics independently of general debug/trace logging settings.
    /// </summary>
    /// <param name="logging">The host logging builder.</param>
    internal static void Configure(ILoggingBuilder logging)
    {
        logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
        logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.None);
        logging.AddFilter("Microsoft.AspNetCore.Mvc", LogLevel.None);
    }
}

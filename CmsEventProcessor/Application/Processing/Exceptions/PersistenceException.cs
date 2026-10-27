namespace CmsEventProcessor.Application.Processing.Exceptions;

/// <summary>
/// Wraps provider-specific update failures without making application services depend on EF exception types.
/// </summary>
/// <param name="inner">The original provider failure, retained for controlled diagnostics and never serialized or logged.</param>
internal sealed class PersistenceException(Exception inner) : Exception("A persistence operation failed.", inner);

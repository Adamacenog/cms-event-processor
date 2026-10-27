namespace CmsEventProcessor.Application.Entities.Exceptions;

/// <summary>
/// Communicates a safe read-failure classification while keeping provider details out of HTTP responses and logs.
/// </summary>
/// <param name="timedOut">Whether the total cooperative read budget expired.</param>
/// <param name="inner">The original provider/cancellation failure, retained only for diagnostics and never logged as content.</param>
internal sealed class EntityReadException(bool timedOut, Exception inner) : Exception("Entity reading could not complete.", inner)
{
    public bool TimedOut { get; } = timedOut;
}

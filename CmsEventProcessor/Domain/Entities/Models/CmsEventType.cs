namespace CmsEventProcessor.Domain.Entities.Models;

/// <summary>
/// Identifies the supported source lifecycle actions independently of their HTTP spelling.
/// </summary>
internal enum CmsEventType
{
    Unknown,
    Publish,
    Unpublish,
    Delete
}

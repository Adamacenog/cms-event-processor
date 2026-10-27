using System.Reflection;
using CmsEventProcessor.Api.Cms.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CmsEventProcessor.Api;

/// <summary>
/// Explicitly discovers production controllers whose internal application dependencies need not become public API types.
/// </summary>
internal sealed class ApiControllerFeatureProvider : ControllerFeatureProvider
{
    /// <summary>
    /// Includes the internal CMS controller alongside the framework's normal public-controller discovery.
    /// </summary>
    /// <param name="typeInfo">The controller candidate inspected by MVC.</param>
    /// <returns>True for the explicitly registered CMS controller or a normally discoverable controller.</returns>
    protected override bool IsController(TypeInfo typeInfo) =>
        typeInfo.AsType() == typeof(CmsEventsController) || base.IsController(typeInfo);
}

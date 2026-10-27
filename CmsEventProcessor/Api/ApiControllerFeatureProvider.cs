using System.Reflection;
using CmsEventProcessor.Api.Cms.Controllers;
using CmsEventProcessor.Api.Entities.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CmsEventProcessor.Api;

/// <summary>
/// Explicitly discovers production controllers whose internal application dependencies need not become public API types.
/// </summary>
internal sealed class ApiControllerFeatureProvider : ControllerFeatureProvider
{
    /// <summary>
    /// Includes internal CMS/consumer controllers alongside the framework's normal public-controller discovery.
    /// </summary>
    /// <param name="typeInfo">The controller candidate inspected by MVC.</param>
    /// <returns>True for an explicitly registered production controller or a normally discoverable controller.</returns>
    protected override bool IsController(TypeInfo typeInfo) =>
        typeInfo.AsType() == typeof(CmsEventsController) || typeInfo.AsType() == typeof(EntitiesController) || base.IsController(typeInfo);
}

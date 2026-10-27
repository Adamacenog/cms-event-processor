using System.Net;
using CmsEventProcessor.Api.Authentication;
using CmsEventProcessor.Api.Authentication.Options;
using CmsEventProcessor.Api.ErrorHandling;
using CmsEventProcessor.Api.Provisioning;
using CmsEventProcessor.Application.Identity.Contracts;
using CmsEventProcessor.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor;

/// <summary>
/// Starts either explicit credential provisioning or the authenticated API foundation.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Dispatches account commands or configures and runs the HTTP application.
    /// </summary>
    /// <param name="args">Account-command arguments or ASP.NET Core host configuration overrides.</param>
    /// <returns>A task containing zero after successful provisioning or orderly host shutdown, or one for a rejected provisioning command.</returns>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">Required authentication accounts are missing or invalid.</exception>
    /// <exception cref="System.IO.IOException">Configuration cannot be read or the HTTP listener cannot bind.</exception>
    /// <exception cref="System.IO.InvalidDataException">A loaded application configuration file is malformed.</exception>
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "accounts")
        {
            var commandHost = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            commandHost.Services.AddIdentityInfrastructure();
            using var host = commandHost.Build();
            return CredentialCommand.Run(args.Skip(1).ToArray(),
                host.Services.GetRequiredService<IAccountProvisioner>(), Console.Out, Console.Error);
        }

        var builder = WebApplication.CreateBuilder(args);
        // Local hashes are optional; deployment environment variables/arguments take precedence.
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: false);
        }
        builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);

        builder.Services.AddIdentityInfrastructure();
        builder.Services.AddConfiguredAuthentication();
        builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                // Framework model-binding messages may echo attempted values. Keep only field names.
                var errors = context.ModelState.Where(item => item.Value?.Errors.Count > 0)
                    .ToDictionary(item => item.Key, _ => new[] { "The field is invalid." });
                var problem = new ValidationProblemDetails(errors) { Status = 400, Title = "Request validation failed." };
                problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                var result = new BadRequestObjectResult(problem);
                result.ContentTypes.Add("application/problem+json");
                return result;
            };
        });
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        });
        builder.Services.AddExceptionHandler<SafeExceptionHandler>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
        app.UseStatusCodePages(async status =>
        {
            await ProblemResponses.WriteAsync(status.HttpContext);
        });
        app.Use(async (context, next) =>
        {
            var settings = context.RequestServices.GetRequiredService<IOptions<TransportSecurityOptions>>().Value;
            var remote = context.Connection.RemoteIpAddress;
            var loopback = remote is not null && IPAddress.IsLoopback(remote);
            if (!context.Request.IsHttps && !(settings.AllowInsecureLoopbackHttp && loopback))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ProblemResponses.WriteAsync(context, new ProblemDetails { Status = 400, Title = "HTTPS is required." });
                return;
            }
            await next(context);
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.RunAsync();
        return 0;
    }
}

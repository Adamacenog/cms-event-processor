using CmsEventProcessor.Application.Identity.Contracts;
using CmsEventProcessor.Application.Identity.Models;

namespace CmsEventProcessor.UnitTests;

/// <summary>
/// Verifies the shared identity boundaries without coupling application contracts to web or storage details.
/// </summary>
public sealed class IdentityBoundaryTests
{
    /// <summary>
    /// Authentication and provisioning contracts expose only shared Application identities and runtime types,
    /// keeping HTTP and stored credential models out of the adapter-facing signatures in the single assembly.
    /// </summary>
    /// <param name="contract">The authentication or provisioning contract whose method signatures are checked.</param>
    [Theory]
    [InlineData(typeof(IAccountAuthenticator))]
    [InlineData(typeof(IAccountProvisioner))]
    public void ApplicationContractsExposeOnlySharedIdentityAndRuntimeTypes(Type contract)
    {
        foreach (var method in contract.GetMethods())
        {
            AssertApplicationOrRuntimeType(method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                AssertApplicationOrRuntimeType(parameter.ParameterType);
            }
        }
    }

    /// <summary>
    /// Authentication and provisioning share a safe identity model containing no stored hash or plaintext password.
    /// </summary>
    [Fact]
    public void AuthenticationResultsExposeOnlyIdentityAndPermissions()
    {
        Assert.Equal(new[] { "Kind", "Username" },
            typeof(AccountIdentity).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(typeof(AccountIdentity), typeof(GeneratedCredential).GetProperty(nameof(GeneratedCredential.Account))!.PropertyType);
        Assert.Equal(typeof(AccountIdentity), typeof(IAccountAuthenticator).GetMethod(nameof(IAccountAuthenticator.Authenticate))!.ReturnType);
        Assert.Equal(typeof(IReadOnlyList<GeneratedCredential>),
            typeof(IAccountProvisioner).GetMethod(nameof(IAccountProvisioner.Initialize))!.ReturnType);
    }

    /// <summary>
    /// Rejects adapter or storage types in contract signatures, including types nested inside generic results.
    /// </summary>
    /// <param name="type">The return, parameter, or generic argument type to check recursively.</param>
    private static void AssertApplicationOrRuntimeType(Type type)
    {
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        Assert.True(definition.Assembly == typeof(string).Assembly ||
            definition.Namespace?.StartsWith("CmsEventProcessor.Application.", StringComparison.Ordinal) == true,
            $"Shared identity contract exposes an adapter or storage type: {type.FullName}");
        foreach (var argument in type.GenericTypeArguments)
        {
            AssertApplicationOrRuntimeType(argument);
        }
    }
}

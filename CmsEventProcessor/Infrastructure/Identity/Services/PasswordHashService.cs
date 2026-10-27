using System.Buffers.Binary;
using CmsEventProcessor.Infrastructure.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CmsEventProcessor.Infrastructure.Identity.Services;

/// <summary>
/// Generates and verifies bounded Identity V3 PBKDF2 password hashes.
/// </summary>
internal sealed class PasswordHashService
{
    private readonly PasswordHasher<AccountDefinition> hasher = new(Microsoft.Extensions.Options.Options.Create(
        new PasswordHasherOptions { IterationCount = 100_000 }));

    /// <summary>
    /// Hashes a newly generated password with a fresh salt for configuration storage.
    /// </summary>
    /// <param name="account">The identity definition supplied to the Identity password hasher.</param>
    /// <param name="password">The newly generated plaintext password to hash without retaining it.</param>
    /// <returns>A salted Identity V3 PBKDF2 hash suitable for the account configuration.</returns>
    /// <exception cref="System.ArgumentNullException">The password is null.</exception>
    public string Hash(AccountDefinition account, string password) => hasher.HashPassword(account, password);

    /// <summary>
    /// Verifies a supplied password against the account's validated stored hash.
    /// </summary>
    /// <param name="account">The identity containing the stored hash, normally validated during startup.</param>
    /// <param name="password">The incoming plaintext password to compare against the stored hash.</param>
    /// <returns>True when verification succeeds, including hashes eligible for a future rehash; otherwise false.</returns>
    /// <exception cref="System.ArgumentNullException">The password or stored hash is null.</exception>
    /// <exception cref="System.FormatException">The stored hash is invalid Base64; normal startup validation rejects this before requests are served.</exception>
    public bool Verify(AccountDefinition account, string password) =>
        hasher.VerifyHashedPassword(account, account.PasswordHash, password) != PasswordVerificationResult.Failed;

    /// <summary>
    /// Rejects malformed or unsupported hashes and excessive work factors before authentication begins.
    /// </summary>
    /// <param name="hash">The configured hash to inspect, or null when configuration is incomplete.</param>
    /// <returns>True for a supported Identity V3 hash with bounded work and size; false for missing, malformed, or unsupported input.</returns>
    public static bool IsSupportedHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length > 256)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[192];
        if (!Convert.TryFromBase64String(hash, bytes, out var count) || count < 61 || bytes[0] != 1)
        {
            return false;
        }

        var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes[1..5]);
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes[5..9]);
        var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[9..13]);
        return prf == 2 && iterations is >= 100_000 and <= 1_000_000 &&
            saltLength is >= 16 and <= 64 && count - 13 - saltLength >= 32;
    }
}

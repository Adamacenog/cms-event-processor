namespace CmsEventProcessor.IntegrationTests.Acceptance;

/// <summary>
/// Keeps newly generated test credentials in memory solely for the owned acceptance host and runner.
/// </summary>
/// <param name="Username">The generated test-only username.</param>
/// <param name="Password">The one-time test-only GUID password.</param>
internal sealed record AcceptanceAccount(string Username, string Password)
{
    /// <summary>
    /// Reads the actual provisioning command's one-time output without printing or persisting plaintext credentials.
    /// </summary>
    /// <param name="output">The privately captured account initialization output.</param>
    /// <returns>The three generated role credentials.</returns>
    /// <exception cref="InvalidOperationException">The command did not return complete valid credentials.</exception>
    internal static IReadOnlyDictionary<string, AcceptanceAccount> Parse(string output)
    {
        var accounts = new Dictionary<string, AcceptanceAccount>(StringComparer.Ordinal);
        string? kind = null;
        string? username = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Cms: ", StringComparison.Ordinal) || line.StartsWith("Reader: ", StringComparison.Ordinal) ||
                line.StartsWith("Admin: ", StringComparison.Ordinal))
            {
                var parts = line.Split(": ", 2);
                kind = parts[0];
                username = parts[1];
            }
            else if (line.StartsWith("Password: ", StringComparison.Ordinal) && kind is not null && username is not null)
            {
                var password = line[10..];
                if (!Guid.TryParse(password, out _) || username.Length is < 10 or > 20)
                    throw new InvalidOperationException("Provisioning returned invalid test credentials.");
                accounts.Add(kind, new AcceptanceAccount(username, password));
            }
        }
        if (accounts.Count != 3) throw new InvalidOperationException("Provisioning did not return all three test accounts.");
        return accounts;
    }
}

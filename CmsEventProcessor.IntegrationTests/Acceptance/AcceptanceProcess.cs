using System.Collections.Concurrent;
using System.Diagnostics;

namespace CmsEventProcessor.IntegrationTests.Acceptance;

/// <summary>
/// Owns child processes and captures private diagnostics in memory without publishing arguments, credentials, or raw output on failure.
/// </summary>
internal sealed class AcceptanceProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentQueue<string> output = new();
    private readonly ConcurrentQueue<string> error = new();
    private readonly Task outputReader;
    private readonly Task errorReader;
    public string Output => string.Join('\n', output);
    public string Error => string.Join('\n', error);
    public bool HasExited => process.HasExited;

    /// <summary>
    /// Starts capture immediately so redirected output cannot block the owned child.
    /// </summary>
    /// <param name="process">The newly started child process owned by this wrapper.</param>
    private AcceptanceProcess(Process process)
    {
        this.process = process;
        outputReader = CaptureAsync(process.StandardOutput, output);
        errorReader = CaptureAsync(process.StandardError, error);
    }

    /// <summary>
    /// Starts a shell-free child with isolated application options and loopback proxy bypass, preserving tool/runtime discovery.
    /// </summary>
    /// <param name="file">The executable name.</param>
    /// <param name="arguments">Separate nonsecret arguments.</param>
    /// <param name="directory">The explicit child working directory.</param>
    /// <param name="environment">Private process-local overrides, never passed as arguments or printed.</param>
    /// <returns>The asynchronously disposable child owner.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable cannot start.</exception>
    /// <exception cref="InvalidOperationException">The child does not start.</exception>
    internal static AcceptanceProcess Start(string file, IEnumerable<string> arguments, string directory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.ToArray())
        {
            if (new[] { "Authentication__", "Persistence__", "Processing__", "Operations__", "Observability__", "Logging__", "OTEL_", "ASPNETCORE_" }
                .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                key.Equals("CMS_MIGRATION_CONNECTION", StringComparison.OrdinalIgnoreCase) || key.Equals("CMS_TEST_SQL_CONNECTION", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) || key.Equals("DOTNET_URLS", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(key);
        }
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Acceptance";
        start.Environment["DOTNET_ENVIRONMENT"] = "Acceptance";
        start.Environment["NO_PROXY"] = "127.0.0.1,localhost,::1";
        if (environment is not null)
            foreach (var (key, value) in environment) start.Environment[key] = value;
        return new AcceptanceProcess(Process.Start(start) ?? throw new InvalidOperationException("The isolated child could not start."));
    }

    /// <summary>
    /// Captures complete output lines privately while the process is running.
    /// </summary>
    /// <param name="reader">The redirected output or diagnostic stream.</param>
    /// <param name="destination">The corresponding private in-memory buffer.</param>
    /// <returns>A task completing when the child closes the stream.</returns>
    /// <exception cref="IOException">The owned process stream fails.</exception>
    private static async Task CaptureAsync(StreamReader reader, ConcurrentQueue<string> destination)
    {
        while (await reader.ReadLineAsync() is { } line) destination.Enqueue(line);
    }

    /// <summary>
    /// Runs a bounded command and passes any ephemeral secrets through standard input instead of arguments or report files.
    /// </summary>
    /// <param name="file">The executable name.</param>
    /// <param name="arguments">The nonsecret command arguments.</param>
    /// <param name="directory">The explicit working directory.</param>
    /// <param name="environment">Optional private process-local configuration.</param>
    /// <param name="input">Optional ephemeral input, suppressed from diagnostics.</param>
    /// <returns>The captured standard output after a successful exit.</returns>
    /// <exception cref="InvalidOperationException">The command fails or exceeds its two-minute budget.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable is unavailable.</exception>
    /// <exception cref="IOException">A child stream fails.</exception>
    internal static async Task<string> RunAsync(string file, IEnumerable<string> arguments, string directory,
        IReadOnlyDictionary<string, string>? environment = null, string? input = null)
    {
        await using var child = Start(file, arguments, directory, environment);
        if (input is not null) await child.process.StandardInput.WriteAsync(input);
        child.process.StandardInput.Close();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await child.process.WaitForExitAsync(budget.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException("The isolated command exceeded its budget; private diagnostics were suppressed."); }
        await Task.WhenAll(child.outputReader, child.errorReader);
        if (child.process.ExitCode != 0) throw new InvalidOperationException("The isolated command failed; private diagnostics were suppressed.");
        return child.Output;
    }

    /// <summary>
    /// Stops only this owned child tree and drains private output before releasing process handles.
    /// </summary>
    /// <returns>A value task completing after process termination and stream capture.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await Task.WhenAll(outputReader, errorReader);
        process.Dispose();
    }
}

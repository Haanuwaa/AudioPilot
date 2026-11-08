using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Platform;

namespace AudioPilot.Benchmarks;

internal sealed class IpcBenchmarkSession : IDisposable
{
    internal const string ExpectedOutput = """{"benchmark":"isolated-ipc","status":"ok"}""";
    internal static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private readonly Dictionary<string, string?> _environment = [];
    private readonly string _scratch;
    private SingleInstanceHelper? _peer;

    internal IpcBenchmarkSession(string outputDirectory)
    {
        string session = Guid.NewGuid().ToString("N");
        _scratch = Path.Combine(Path.GetFullPath(outputDirectory), $"session-{session}");
        Directory.CreateDirectory(_scratch);
        foreach ((string name, string value) in new[] {
            ("AUDIOPILOT_DIAGNOSTIC_SESSION", session), ("AUDIOPILOT_DISABLE_CONSOLE_LOGGING", "1"),
            ("TEMP", _scratch), ("TMP", _scratch) })
        {
            _environment.Add(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
        try
        {
            if (!DiagnosticSession.IsActive || DiagnosticSession.Id != session)
                throw new InvalidOperationException("The benchmark IPC session did not initialize.");
            _peer = new SingleInstanceHelper();
            _peer.CommandRequested += (payload, _) => Task.FromResult(
                CliCommand.TryFromPipePayload(payload, out var command) && command.Action == CliAction.Status && command.JsonOutput
                    ? new SingleInstanceCommandResult(0, ExpectedOutput)
                    : new SingleInstanceCommandResult(2, ErrorCode: "benchmark-unexpected-command"));
            if (!_peer.TryAcquire()) throw new InvalidOperationException("Could not start the isolated benchmark pipe.");
            _peer.ActivationListenerReadyForTests.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch { Dispose(); throw; }
    }

    internal static double Percentile(double[] sorted, double percentile)
    {
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)position;
        return sorted[lower] + (sorted[(int)Math.Ceiling(position)] - sorted[lower]) * (position - lower);
    }

    public void Dispose()
    {
        try { Interlocked.Exchange(ref _peer, null)?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        finally
        {
            try { Logger.DisposeInstanceIfCreated(); }
            finally
            {
                try { Directory.Delete(_scratch, recursive: true); }
                finally
                {
                    foreach ((string name, string? value) in _environment) Environment.SetEnvironmentVariable(name, value);
                }
            }
        }
    }
}

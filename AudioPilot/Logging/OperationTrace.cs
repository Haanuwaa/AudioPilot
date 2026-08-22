using System.Diagnostics;

namespace AudioPilot.Logging;

/// <summary>
/// Correlates asynchronous stages without retaining request payloads or device identities.
/// Explicit worker queues must capture CurrentId and attach it while executing their item.
/// Stage completion describes that stage only; deferred restoration can finish after command admission.
/// </summary>
internal sealed class OperationTrace : IDisposable
{
    private static readonly AsyncLocal<string?> Current = new();
    private readonly string? _previous;
    private readonly Logger? _logger;
    private readonly string? _stage;
    private readonly long _started = Stopwatch.GetTimestamp();
    private string _outcome = "incomplete";
    private bool _disposed;

    internal static string? CurrentId => Current.Value;
    internal string Id { get; }

    private OperationTrace(string? id, string? stage, Logger? logger)
    {
        _previous = Current.Value;
        Id = id ?? _previous ?? (stage == null ? string.Empty : Guid.NewGuid().ToString("N"));
        Current.Value = id ?? (stage == null ? null : Id);
        _stage = stage;
        _logger = logger;
        if (stage != null) logger!.Debug("Operation", () => $"operation-stage-start | stage={stage}");
    }

    internal static OperationTrace Start(string stage, Logger? logger = null, string? id = null) => new(id, stage, logger ?? Logger.Instance);
    internal static OperationTrace? Attach(string? id) => id == Current.Value ? null : new(id, null, null);
    internal void Complete(string outcome = "success") => _outcome = outcome;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_stage != null)
                _logger!.Debug("Operation", () => $"operation-stage-end | stage={_stage} outcome={_outcome} durationMs={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F2}");
        }
        finally { Current.Value = _previous; }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.Platform;

internal interface ISingleInstanceRecoveryTarget : IDisposable
{
    bool HasExited { get; }
    bool RequestClose();
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
    void Terminate();
}

/// <summary>Keeps the verified endpoint owner's process object alive through confirmation and recovery.</summary>
internal sealed partial class SingleInstanceProcessRecoveryHelper : ISingleInstanceRecoveryTarget
{
    private readonly Process _process;
    private readonly SafeProcessHandle _handle;

    private SingleInstanceProcessRecoveryHelper(Process process, SafeProcessHandle handle)
    {
        _process = process;
        _handle = handle;
    }

    internal static ISingleInstanceRecoveryTarget? TryOpen(SingleInstanceOwnerIdentity owner, Logger logger)
    {
        if (owner.ProcessId <= 0 || owner.ProcessId == Environment.ProcessId) return null;

        Process? process = null;
        try
        {
            process = Process.GetProcessById(owner.ProcessId);
            SafeProcessHandle handle = process.SafeHandle;
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != owner.StartTimeUtcTicks
                || SingleInstanceScope.Read(process) != SingleInstanceScope.Current)
                return null;

            string expectedPath = RoutineTriggerPathHelper.NormalizeExecutablePath(Environment.ProcessPath);
            string actualPath = RoutineTriggerPathHelper.NormalizeExecutablePath(process.MainModule?.FileName);
            if (string.IsNullOrEmpty(expectedPath) || !string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase)
                || process.HasExited)
                return null;

            var target = new SingleInstanceProcessRecoveryHelper(process, handle);
            process = null;
            return target;
        }
        catch (Exception ex)
        {
            logger.Debug("SingleInstanceProcessRecoveryHelper", () => $"recovery-owner-unavailable | error={ex.GetType().Name}");
            return null;
        }
        finally { process?.Dispose(); }
    }

    public bool HasExited => _process.HasExited;

    public bool RequestClose() => !HasExited && _process.CloseMainWindow();

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HasExited;
        }
    }

    public void Terminate()
    {
        if (HasExited || TerminateProcess(_handle, 1)) return;
        int error = Marshal.GetLastPInvokeError();
        if (!HasExited) throw new Win32Exception(error);
    }

    public void Dispose() => _process.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}

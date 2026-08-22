using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Audio;

internal readonly record struct ForegroundProcessTarget(int ProcessId, long StartedUtcTicks, string Name, long CapturedUtcTicks = 0, bool IncludeDescendants = true);
internal readonly record struct ForegroundAudioResult(string Code, string Name, int Changed = 0, int Failed = 0, float Volume = 0, bool Muted = false);

/// <summary>Controls fresh playback sessions belonging to the captured foreground process and its descendants.</summary>
internal sealed partial class ForegroundAudioControlService(Logger logger)
{
    private int _busy;
    [ThreadStatic] private static (bool Captured, ForegroundProcessTarget? Target) s_dispatchTarget;

    internal static ForegroundProcessTarget? DispatchTarget => s_dispatchTarget.Captured ? s_dispatchTarget.Target : CaptureTarget();

    internal static Action CaptureDispatch(Action? callback)
    {
        var target = CaptureTarget();
        return () =>
        {
            var previous = s_dispatchTarget;
            s_dispatchTarget = (true, target);
            try { callback?.Invoke(); }
            finally { s_dispatchTarget = previous; }
        };
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    internal static ForegroundProcessTarget? CaptureTarget()
    {
        nint window = GetForegroundWindow();
        nint shell = GetShellWindow();
        if (window == 0 || window == shell || window == GetDesktopWindow() ||
            IsShellSurface(AudioDeviceHelper.TryGetWindowClassName(window))) return null;
        _ = GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        _ = GetWindowThreadProcessId(shell, out uint shellPid);
        return pid > int.MaxValue ? null : new((int)pid, 0, "Foreground app", DateTime.UtcNow.Ticks, IncludeDescendants: pid != shellPid);
    }

    public ForegroundAudioResult Apply(ForegroundProcessTarget? target, int? volumeDeltaPercent, CancellationToken cancellationToken = default)
    {
        if (target is not { } captured) return new("no-foreground-app", string.Empty);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new("busy", captured.Name);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ComThreadingHelper.RunOnCoreAudioThread(() => ApplyCore(captured, volumeDeltaPercent, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("cancelled", captured.Name); }
        catch (Exception ex)
        {
            logger.Warning("ForegroundAudio", "foreground-audio-failed", nameof(Apply), ex);
            return new("failed", captured.Name, Failed: 1);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private ForegroundAudioResult ApplyCore(ForegroundProcessTarget target, int? delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.StartedUtcTicks == 0)
        {
            try
            {
                using var process = Process.GetProcessById(target.ProcessId);
                long started = process.StartTime.ToUniversalTime().Ticks;
                if (started > target.CapturedUtcTicks) return new("app-exited", target.Name);
                target = target with { StartedUtcTicks = started, Name = process.ProcessName };
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                return new("access-denied", target.Name);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                return new("app-exited", target.Name);
            }
        }
        if (!IsSameProcess(target.ProcessId, target.StartedUtcTicks)) return new("app-exited", target.Name);
        var matches = new List<AudioSessionControl>();
        var processMatches = new Dictionary<int, bool>();
        var parentIds = new Dictionary<int, int>();
        var startTimes = new Dictionary<int, long?>();
        int Parent(int pid)
        {
            if (!parentIds.TryGetValue(pid, out int parent)) parentIds[pid] = parent = AudioDeviceHelper.GetParentPid(pid);
            return parent;
        }
        long? Started(int pid)
        {
            if (!startTimes.TryGetValue(pid, out long? started)) startTimes[pid] = started = ReadStartTime(pid);
            return started;
        }
        int failed = 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < devices.Count; d++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var device = devices[d];
                try
                {
                    using var manager = device.AudioSessionManager;
                    using var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AudioSessionControl? session = null;
                        try
                        {
                            session = sessions[i];
                            int pid = checked((int)session.GetProcessID);
                            if (pid == 0 || session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired) continue;
                            if (!processMatches.TryGetValue(pid, out bool matchesTarget))
                            {
                                matchesTarget = BelongsToTarget(pid, target, Parent, Started);
                                processMatches[pid] = matchesTarget;
                            }
                            if (!matchesTarget) continue;
                            matches.Add(session);
                            session = null;
                        }
                        catch (Exception ex)
                        {
                            logger.Debug("ForegroundAudio", $"foreground-session-unavailable | hresult=0x{ex.HResult:X8}");
                        }
                        finally { session?.Dispose(); }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    logger.Warning("ForegroundAudio", "foreground-endpoint-unavailable", nameof(ApplyCore), ex);
                }
            }

            target = target with { Name = ResolveDisplayName(target, matches) };
            if (!IsSameProcess(target.ProcessId, target.StartedUtcTicks)) return new("app-exited", target.Name);
            var readable = new List<(AudioSessionControl Session, float Volume, bool Muted)>();
            foreach (var session in matches)
            {
                if (AudioDeviceHelper.TryGetSessionVolumeAndMute(logger, session, out float volume, out bool muted))
                    readable.Add((session, volume, muted));
                else failed++;
            }
            bool targetMute = !readable.All(static s => s.Muted);
            int changed = 0;
            float resultingVolume = 0;
            bool allMuted = true;
            foreach (var (Session, Volume, Muted) in readable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float volume = delta.HasValue ? AdjustVolume(Volume, delta.Value) : Volume;
                bool muted = delta.HasValue ? Muted : targetMute;
                bool applied = delta.HasValue
                    ? AudioDeviceHelper.TrySetSessionVolume(logger, Session, volume)
                    : AudioDeviceHelper.TrySetSessionMute(logger, Session, muted);
                if (!applied) { failed++; continue; }
                changed++;
                resultingVolume = Math.Max(resultingVolume, volume);
                allMuted &= muted;
            }
            string code = changed > 0 ? failed > 0 ? "partial" : "applied" : failed > 0 ? "failed" : "no-audio-session";
            logger.Debug("ForegroundAudio", $"foreground-audio-result | code={code} pid={LogPrivacy.Id(target.ProcessId.ToString())} changed={changed} failed={failed} delta={delta?.ToString() ?? "mute"}");
            return new(code, target.Name, changed, failed, resultingVolume, allMuted);
        }
        finally
        {
            foreach (var session in matches)
            {
                try { session.Dispose(); }
                catch (Exception ex) { logger.Warning("ForegroundAudio", "foreground-session-dispose-failed", nameof(ApplyCore), ex); }
            }
        }
    }

    internal static float AdjustVolume(float volume, int deltaPercent) => Math.Clamp(volume + Math.Clamp(deltaPercent, -100, 100) / 100f, 0, 1);

    internal static bool IsShellSurface(string? windowClass) => windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private string ResolveDisplayName(ForegroundProcessTarget target, List<AudioSessionControl> sessions)
    {
        foreach (var session in sessions)
        {
            try
            {
                string name = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Exception ex) { logger.Trace("ForegroundAudio", $"foreground-session-name-unavailable | hresult=0x{ex.HResult:X8}"); }
        }
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            string title = process.MainWindowTitle;
            if (!string.IsNullOrWhiteSpace(title) && !AudioDeviceHelper.IsInternalWindowTitle(title)) return title;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            logger.Trace("ForegroundAudio", $"foreground-window-title-unavailable | hresult=0x{ex.HResult:X8}");
        }
        string? description = AudioDeviceHelper.GetFileDescription(target.ProcessId);
        return !string.IsNullOrWhiteSpace(description) ? description : AudioDeviceHelper.CapitalizeName(AudioDeviceHelper.SanitizeProcessName(target.Name));
    }

    internal static bool BelongsToTarget(int pid, ForegroundProcessTarget target, Func<int, int> parent, Func<int, long?> started)
    {
        if (!target.IncludeDescendants && pid != target.ProcessId) return false;
        var visited = new HashSet<int>();
        long? childStarted = null;
        for (int depth = 0; pid > 0 && depth < 16 && visited.Add(pid); depth++)
        {
            long? currentStarted = started(pid);
            if (!currentStarted.HasValue || (childStarted.HasValue && currentStarted > childStarted)) return false;
            if (pid == target.ProcessId) return currentStarted == target.StartedUtcTicks;
            childStarted = currentStarted;
            pid = parent(pid);
        }
        return false;
    }

    private static bool IsSameProcess(int pid, long started) => ReadStartTime(pid) == started;

    private static long? ReadStartTime(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { return null; }
    }
}

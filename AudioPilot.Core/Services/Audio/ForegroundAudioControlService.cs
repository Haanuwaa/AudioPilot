using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Audio;

internal readonly record struct ForegroundProcessTarget(
    int ProcessId,
    long StartedUtcTicks,
    string Name,
    long CapturedUtcTicks = 0,
    bool IncludeDescendants = true,
    nint WindowHandle = 0,
    bool ProtectedProcess = false);
internal readonly record struct ForegroundAudioResult(string Code, string Name, int Changed = 0, int Failed = 0, float Volume = 0,
    bool Muted = false, ForegroundProcessTarget? AppliedTarget = null);

/// <summary>Controls fresh playback sessions belonging to the captured foreground process and its descendants.</summary>
internal sealed partial class ForegroundAudioControlService(
    Logger logger,
    Action<string?, string?, float, uint>? onSessionVolumeApplied = null)
{
    private const string ApplicationFrameWindowClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";
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

    private delegate bool EnumWindowsCallback(nint hWnd, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(nint parentHandle, EnumWindowsCallback callback, nint lParam);

    internal static ForegroundProcessTarget? CaptureTarget()
    {
        nint window = GetForegroundWindow();
        nint shell = GetShellWindow();
        if (window == 0 || window == shell || window == GetDesktopWindow()) return null;
        string? windowClass = AudioDeviceHelper.TryGetWindowClassName(window);
        if (IsShellSurface(windowClass)) return null;
        _ = GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;

        nint targetWindow = window;
        if (string.Equals(windowClass, ApplicationFrameWindowClass, StringComparison.Ordinal)
            && TryFindHostedCoreWindow(window, pid, out uint hostedPid, out nint hostedWindow))
        {
            pid = hostedPid;
            targetWindow = hostedWindow;
            if (pid == 0 || pid == Environment.ProcessId) return null;
        }

        _ = GetWindowThreadProcessId(shell, out uint shellPid);
        return pid > int.MaxValue
            ? null
            : new((int)pid, 0, string.Empty, DateTime.UtcNow.Ticks, IncludeDescendants: pid != shellPid, WindowHandle: targetWindow);
    }

    private static bool TryFindHostedCoreWindow(nint frameWindow, uint framePid, out uint hostedPid, out nint hostedWindow)
    {
        uint foundPid = 0;
        nint foundWindow = 0;
        uint fallbackPid = 0;
        nint fallbackWindow = 0;

        _ = EnumChildWindows(frameWindow, (child, lParam) =>
        {
            _ = lParam;
            _ = GetWindowThreadProcessId(child, out uint childPid);
            if (childPid == 0 || childPid == framePid) return true;
            if (string.Equals(AudioDeviceHelper.TryGetWindowClassName(child), CoreWindowClass, StringComparison.Ordinal))
            {
                foundPid = childPid;
                foundWindow = child;
                return false;
            }
            if (fallbackPid == 0)
            {
                fallbackPid = childPid;
                fallbackWindow = child;
            }
            return true;
        }, 0);

        if (foundPid != 0)
        {
            hostedPid = foundPid;
            hostedWindow = foundWindow;
            return true;
        }
        if (fallbackPid != 0)
        {
            hostedPid = fallbackPid;
            hostedWindow = fallbackWindow;
            return true;
        }

        hostedPid = 0;
        hostedWindow = 0;
        return false;
    }

    public async Task<ForegroundAudioResult> ApplyAsync(ForegroundProcessTarget? target, decimal? volumeDeltaPercent,
        Func<bool>? canApply = null, CancellationToken cancellationToken = default)
    {
        if (target is not { } captured) return new("no-foreground-app", string.Empty);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => canApply?.Invoke() == false ? new("cancelled", captured.Name) :
                    ApplyCore(captured, volumeDeltaPercent, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("cancelled", captured.Name); }
        catch (Exception ex)
        {
            logger.Warning("ForegroundAudio", "foreground-audio-failed", nameof(ApplyAsync), ex);
            return new("failed", captured.Name, Failed: 1);
        }
    }

    internal static bool TryResolveTarget(ref ForegroundProcessTarget target, out string? failureCode)
    {
        failureCode = null;
        if (target.ProtectedProcess)
        {
            if (target.ProcessId <= 0 || !IsWindowOwnedByPid(target.WindowHandle, target.ProcessId))
            {
                failureCode = "app-exited";
                return false;
            }
            return true;
        }

        if (target.StartedUtcTicks == 0)
        {
            if (target.ProcessId <= 0)
            {
                failureCode = "app-exited";
                return false;
            }

            if (!ProcessEnumerationHelper.TryGetRunningStartTimeUtcTicks((uint)target.ProcessId, out long started, out bool accessDenied))
            {
                if (accessDenied && target.WindowHandle != 0 && IsWindowOwnedByPid(target.WindowHandle, target.ProcessId))
                {
                    // Protected / anti-cheat processes can reject OpenProcess while still exposing WASAPI sessions by PID.
                    string protectedName = target.Name;
                    if (string.IsNullOrWhiteSpace(protectedName))
                    {
                        protectedName = TryResolveFallbackProcessName(target.ProcessId, target.WindowHandle);
                    }

                    target = target with { Name = protectedName, IncludeDescendants = false, ProtectedProcess = true };
                    return true;
                }

                failureCode = accessDenied ? "access-denied" : "app-exited";
                return false;
            }

            if (started > target.CapturedUtcTicks)
            {
                failureCode = "app-exited";
                return false;
            }

            string name = target.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = TryResolveFallbackProcessName(target.ProcessId, target.WindowHandle);
            }

            target = target with { StartedUtcTicks = started, Name = name };
        }
        return true;
    }

    private static string TryResolveFallbackProcessName(int pid, nint windowHandle)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!string.IsNullOrWhiteSpace(process.ProcessName)) return process.ProcessName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
        }

        (string? imagePath, _) = ProcessEnumerationHelper.TryGetImageIdentity(pid);
        if (!string.IsNullOrWhiteSpace(imagePath)) return Path.GetFileNameWithoutExtension(imagePath);

        if (windowHandle != 0)
        {
            string? title = AudioDeviceHelper.TryGetWindowTitle(windowHandle);
            if (!string.IsNullOrWhiteSpace(title) && !AudioDeviceHelper.IsInternalWindowTitle(title)) return title;
        }

        return string.Empty;
    }

    private ForegroundAudioResult ApplyCore(ForegroundProcessTarget target, decimal? delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveTarget(ref target, out string? failureCode)) return new(failureCode!, target.Name);
        if (!IsTargetStillValid(target)) return new("app-exited", target.Name);
        var matches = new List<(AudioSessionControl Session, int ProcessId)>();
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
        int sessionFailures = 0;
        int endpointFailures = 0;
        int readableEndpoints = 0;
        MMDeviceEnumerator? ownedEnumerator = null;
        try
        {
            MMDeviceEnumerator enumerator;
            if (ComThreadingHelper.TryGetWorkerDeviceEnumerator(out MMDeviceEnumerator? workerEnumerator) && workerEnumerator != null)
            {
                enumerator = workerEnumerator;
            }
            else
            {
                ownedEnumerator = new MMDeviceEnumerator();
                enumerator = ownedEnumerator;
            }

            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < devices.Count; d++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var device = devices[d];
                try
                {
                    using var manager = device.AudioSessionManager;
                    using var sessions = manager.Sessions;
                    readableEndpoints++;
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
                            matches.Add((session, pid));
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
                    endpointFailures++;
                    logger.Warning("ForegroundAudio", "foreground-endpoint-unavailable", nameof(ApplyCore), ex);
                }
            }

            target = target with { Name = ResolveDisplayName(target, matches) };
            if (!IsTargetStillValid(target)) return new("app-exited", target.Name);
            var readable = new List<(AudioSessionControl Session, int ProcessId, bool IsActive, float Volume, bool Muted)>();
            foreach (var (session, pid) in matches)
            {
                if (AudioDeviceHelper.TryGetSessionVolumeAndMute(logger, session, out float volume, out bool muted))
                {
                    bool isActive = false;
                    try { isActive = session.State == AudioSessionState.AudioSessionStateActive; }
                    catch { }
                    readable.Add((session, pid, isActive, volume, muted));
                }
                else sessionFailures++;
            }
            bool targetMute = !readable.All(static s => s.Muted);
            int changed = 0;
            bool hasActiveApplied = false;
            float activeResultingVolume = 0;
            float anyResultingVolume = 0;
            bool allMuted = true;
            foreach (var (Session, ProcessId, IsActive, Volume, Muted) in readable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float volume = delta.HasValue ? AdjustVolume(Volume, delta.Value) : Volume;
                bool muted = delta.HasValue ? Muted : targetMute;
                bool applied = delta.HasValue
                    ? AudioDeviceHelper.TrySetSessionVolume(logger, Session, volume)
                    : AudioDeviceHelper.TrySetSessionMute(logger, Session, muted);
                if (!applied) { sessionFailures++; continue; }
                changed++;
                if (IsActive)
                {
                    hasActiveApplied = true;
                    activeResultingVolume = Math.Max(activeResultingVolume, volume);
                }
                anyResultingVolume = Math.Max(anyResultingVolume, volume);
                allMuted &= muted;
                if (delta.HasValue && onSessionVolumeApplied != null)
                {
                    string? processName = AudioDeviceHelper.TryGetProcessName(ProcessId, out string? resolvedProcessName) ? resolvedProcessName : null;
                    onSessionVolumeApplied(target.Name, processName, volume * 100f, (uint)ProcessId);
                }
            }

            int reportedFailed = changed > 0
                ? sessionFailures
                : sessionFailures > 0 ? sessionFailures : (readableEndpoints == 0 ? endpointFailures : 0);
            string code = changed > 0
                ? sessionFailures > 0 ? "partial" : "applied"
                : reportedFailed > 0 ? "failed" : "no-audio-session";
            float resultingVolume = hasActiveApplied ? activeResultingVolume : anyResultingVolume;
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug("ForegroundAudio", $"foreground-audio-result | code={code} pid={LogPrivacy.Id(target.ProcessId.ToString())} changed={changed} failed={reportedFailed} endpointFailures={endpointFailures} delta={delta?.ToString() ?? "mute"}");
            }
            return new(code, target.Name, changed, reportedFailed, resultingVolume, allMuted, changed > 0 ? target : null);
        }
        finally
        {
            ownedEnumerator?.Dispose();
            foreach (var (session, _) in matches)
            {
                try { session.Dispose(); }
                catch (Exception ex) { logger.Warning("ForegroundAudio", "foreground-session-dispose-failed", nameof(ApplyCore), ex); }
            }
        }
    }

    internal static float AdjustVolume(float volume, decimal deltaPercent) => Math.Clamp(volume + (float)Math.Clamp(deltaPercent, -100, 100) / 100f, 0, 1);

    internal static bool IsShellSurface(string? windowClass) => windowClass is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
        or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
        or "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow";

    private string ResolveDisplayName(ForegroundProcessTarget target, List<(AudioSessionControl Session, int ProcessId)> sessions)
    {
        foreach (var (session, _) in sessions)
        {
            try
            {
                string name = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Exception ex) { logger.Trace("ForegroundAudio", $"foreground-session-name-unavailable | hresult=0x{ex.HResult:X8}"); }
        }

        if (target.WindowHandle != 0)
        {
            string? capturedTitle = AudioDeviceHelper.TryGetWindowTitle(target.WindowHandle);
            if (!string.IsNullOrWhiteSpace(capturedTitle) && !AudioDeviceHelper.IsInternalWindowTitle(capturedTitle))
            {
                return capturedTitle;
            }
        }
        else
        {
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
        }

        string? description = AudioDeviceHelper.GetFileDescription(target.ProcessId);
        if (!string.IsNullOrWhiteSpace(description)) return description;
        return string.IsNullOrWhiteSpace(target.Name)
            ? string.Empty
            : AudioDeviceHelper.CapitalizeName(AudioDeviceHelper.SanitizeProcessName(target.Name));
    }

    internal static bool BelongsToTarget(int pid, ForegroundProcessTarget target, Func<int, int> parent, Func<int, long?> started)
    {
        if (target.ProtectedProcess) return pid == target.ProcessId;
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

    private static bool IsTargetStillValid(ForegroundProcessTarget target) =>
        target.ProtectedProcess
            ? IsWindowOwnedByPid(target.WindowHandle, target.ProcessId)
            : IsSameProcess(target.ProcessId, target.StartedUtcTicks);

    private static bool IsWindowOwnedByPid(nint windowHandle, int expectedPid)
    {
        if (windowHandle == 0 || expectedPid <= 0 || !IsWindow(windowHandle)) return false;
        _ = GetWindowThreadProcessId(windowHandle, out uint currentPid);
        return currentPid == (uint)expectedPid;
    }

    private static bool IsSameProcess(int pid, long started) => ReadStartTime(pid) == started;

    private static long? ReadStartTime(int pid) =>
        pid > 0 ? ProcessEnumerationHelper.TryGetStartTimeUtcTicks((uint)pid) : null;
}

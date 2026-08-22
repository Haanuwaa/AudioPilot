using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal readonly record struct WindowFocusMonitorStartResult(bool Success, bool TitleChangesAvailable = true, string? FailureReason = null);

    internal interface IWindowFocusMonitor : IDisposable
    {
        event EventHandler<WindowFocusEventArgs>? WindowFocused;
        WindowFocusMonitorStartResult Start();
        void Stop();
    }

    internal sealed class WindowFocusEventArgs(int processId, string processName, string executablePath, string windowTitle, bool isTitleChange = false, nint windowHandle = default) : EventArgs
    {
        public int ProcessId { get; } = processId;
        public string ProcessName { get; } = processName;
        public string ExecutablePath { get; } = executablePath;
        public string WindowTitle { get; } = windowTitle;
        public bool IsTitleChange { get; } = isTitleChange;
        public nint WindowHandle { get; } = windowHandle;
    }

    internal sealed partial class WinEventHookWindowFocusMonitor : IWindowFocusMonitor
    {
        private readonly Logger _logger;
        private GCHandle _hookDelegateHandle;
        private IntPtr _hookHandle;
        private IntPtr _titleHookHandle;
        private bool _started;
        private readonly Lock _lock = new();

        public event EventHandler<WindowFocusEventArgs>? WindowFocused;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        [LibraryImport("user32.dll")]
        private static partial IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWinEvent(IntPtr hWinEventHook);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
        private static unsafe partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        private static partial int GetWindowTextLength(IntPtr hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("user32.dll")]
        private static partial nint GetForegroundWindow();

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        private readonly WinEventDelegate _delegate;

        public WinEventHookWindowFocusMonitor(Logger logger)
        {
            _logger = logger;
            _delegate = OnWinEvent;
        }

        public WindowFocusMonitorStartResult Start()
        {
            lock (_lock)
            {
                if (_started)
                {
                    return new(true, _titleHookHandle != IntPtr.Zero);
                }

                _hookDelegateHandle = GCHandle.Alloc(_delegate);
                _hookHandle = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND,
                    EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _delegate,
                    0, 0,
                    WINEVENT_OUTOFCONTEXT);

                if (_hookHandle == IntPtr.Zero)
                {
                    _hookDelegateHandle.Free();
                    _logger.Error("WinEventHookWindowFocusMonitor", "Failed to set WinEventHook");
                    return new(false, false, "foreground-hook-unavailable");
                }

                _titleHookHandle = SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE,
                    IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);
                if (_titleHookHandle == IntPtr.Zero)
                    _logger.Warning("WinEventHookWindowFocusMonitor", "Window-title monitoring unavailable; focus changes remain monitored.");
                _started = true;
                _logger.Debug("WinEventHookWindowFocusMonitor", "Window focus monitoring started");
                return new(true, _titleHookHandle != IntPtr.Zero, _titleHookHandle == IntPtr.Zero ? "title-hook-unavailable" : null);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_started)
                {
                    return;
                }

                if (_hookHandle != IntPtr.Zero)
                {
                    UnhookWinEvent(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }

                if (_titleHookHandle != IntPtr.Zero)
                {
                    UnhookWinEvent(_titleHookHandle);
                    _titleHookHandle = IntPtr.Zero;
                }

                if (_hookDelegateHandle.IsAllocated)
                {
                    _hookDelegateHandle.Free();
                }

                _started = false;
                _logger.Debug("WinEventHookWindowFocusMonitor", "Window focus monitoring stopped");
            }
        }

        internal unsafe void ReevaluateCurrent() => OnWinEvent(0, EVENT_SYSTEM_FOREGROUND, GetForegroundWindow(), 0, 0, 0, 0);

        private unsafe void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || (eventType != EVENT_SYSTEM_FOREGROUND &&
                (eventType != EVENT_OBJECT_NAMECHANGE || idObject != 0 || idChild != 0 || hwnd != GetForegroundWindow())))
            {
                return;
            }

            try
            {
                if (GetWindowThreadProcessId(hwnd, out uint processId) == 0)
                {
                    return;
                }

                string windowTitle = ReadWindowTitle(hwnd);

                (string processName, string executablePath) = GetProcessInfo((int)processId);

                WindowFocused?.Invoke(this, new WindowFocusEventArgs((int)processId, processName, executablePath, windowTitle,
                    isTitleChange: eventType == EVENT_OBJECT_NAMECHANGE, windowHandle: hwnd));
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Debug, "WinEventHookWindowFocusMonitor", () => $"focus-event-failed | reason={ex.GetType().Name}", nameof(OnWinEvent), ex);
            }
        }

        internal static unsafe string ReadWindowTitle(nint hwnd)
        {
            const int maximumCharacters = 32768;
            Span<char> shortTitle = stackalloc char[256];
            int length;
            fixed (char* buffer = shortTitle) length = GetWindowText(hwnd, buffer, shortTitle.Length);
            if (length < shortTitle.Length - 1) return length > 0 ? new string(shortTitle[..length]) : string.Empty;

            int capacity = (int)Math.Clamp(GetWindowTextLength(hwnd) + 1L, 512, maximumCharacters);
            while (true)
            {
                char[] rented = ArrayPool<char>.Shared.Rent(capacity);
                try
                {
                    fixed (char* buffer = rented) length = GetWindowText(hwnd, buffer, capacity);
                    if (length < capacity - 1 || capacity == maximumCharacters)
                        return length > 0 ? new string(rented, 0, length) : string.Empty;
                }
                finally { ArrayPool<char>.Shared.Return(rented); }
                capacity = Math.Min(capacity * 2, maximumCharacters);
            }
        }

        private static (string processName, string executablePath) GetProcessInfo(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return (process.ProcessName, process.MainModule?.FileName ?? string.Empty);
            }
            catch
            {
                return (string.Empty, string.Empty);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }

    internal sealed class ApplicationTriggerCoordinator : IDisposable
    {
        private readonly IWindowFocusMonitor _focusMonitor;
        private readonly Logger _logger;
        private readonly Lock _lock = new();
        private static readonly TimeSpan TitleRegexTimeout = TimeSpan.FromMilliseconds(100);

        private readonly Dictionary<string, Regex> _compiledRegexCache = new(StringComparer.Ordinal);
        private WindowFocusEventArgs? _pendingFocusEvent;
        private CancellationTokenSource? _focusCancellation;
        private DispatcherTimer? _monitorRetryTimer;
        private int _monitorRetries;
        private bool _monitorRequested;
        private readonly List<(AudioRoutine Routine, int ProcessId)> _activeFocusMatches = [];
        private readonly Func<IReadOnlyList<AudioRoutine>> _routineSnapshotProvider;
        private readonly Func<AudioRoutine, int, Task>? _deactivateRoutine;
        private readonly Func<nint, bool>? _ignoreFocusWindow;
        private int _lastFocusedProcessId;
        private nint _lastFocusedWindowHandle;
        private string _lastFocusedWindowTitle = string.Empty;
        private bool _focusWorkerRunning;
        private int _monitorGeneration;
        private bool _started;
        private bool _disposed;

        public ApplicationTriggerCoordinator(
            IEnumerable<AudioRoutine> routines,
            Func<AudioRoutine, int, CancellationToken, Task> executeRoutine,
            Logger logger,
            IWindowFocusMonitor? focusMonitor = null,
            Func<AudioRoutine, int, Task>? deactivateRoutine = null,
            Func<IReadOnlyList<AudioRoutine>>? routineSnapshotProvider = null,
            Func<nint, bool>? ignoreFocusWindow = null,
            TimeSpan? monitorRetryDelay = null,
            Func<bool>? canActivate = null,
            Func<AudioRoutine, int, bool>? isActive = null)
        {
            _canActivate = canActivate ?? (() => true);
            _isActive = isActive;
            Routines = routines ?? throw new ArgumentNullException(nameof(routines));
            ExecuteRoutine = executeRoutine ?? throw new ArgumentNullException(nameof(executeRoutine));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _focusMonitor = focusMonitor ?? new WinEventHookWindowFocusMonitor(logger);
            _deactivateRoutine = deactivateRoutine;
            _ignoreFocusWindow = ignoreFocusWindow;
            _monitorRetryDelay = monitorRetryDelay ?? TimeSpan.FromSeconds(5);
            _routineSnapshotProvider = routineSnapshotProvider ?? (() => [.. Routines]);
            _focusMonitor.WindowFocused += OnWindowFocused;
        }

        public IEnumerable<AudioRoutine> Routines { get; }
        private readonly Func<bool> _canActivate;
        private readonly Func<AudioRoutine, int, bool>? _isActive;

        internal void ReevaluateCurrent()
        {
            InvalidatePendingActivations();
            lock (_lock)
            {
                if (_disposed || !_started) return;
                _lastFocusedProcessId = 0;
                _lastFocusedWindowHandle = 0;
                _lastFocusedWindowTitle = string.Empty;
            }
            if (_focusMonitor is WinEventHookWindowFocusMonitor monitor) monitor.ReevaluateCurrent();
        }

        internal void InvalidatePendingActivations()
        {
            lock (_lock)
            {
                _pendingFocusEvent = null;
                _monitorGeneration++;
            }
        }
        private readonly TimeSpan _monitorRetryDelay;
        public Func<AudioRoutine, int, CancellationToken, Task> ExecuteRoutine { get; }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed || _started)
                {
                    _logger.Debug("ApplicationTriggerCoordinator", $"Start() skipped: disposed={_disposed}, started={_started}");
                    return;
                }

                bool hasRoutines = HasProcessFocusRoutines();
                _logger.Debug("ApplicationTriggerCoordinator", $"Start() checking routines: hasProcessFocusRoutines={hasRoutines}, totalRoutines={Routines.Count()}");

                if (!hasRoutines)
                {
                    _logger.Debug("ApplicationTriggerCoordinator", "No ProcessFocus routines, not starting focus monitor");
                    return;
                }

                _monitorRequested = true;
                TryStartMonitor();
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _monitorRequested = false;
                _monitorRetryTimer?.Stop();
                _monitorRetryTimer = null;
                _monitorRetries = 0;
                _focusMonitor.Stop();
                _focusCancellation?.Cancel();
                _pendingFocusEvent = null;
                _activeFocusMatches.Clear();
                _compiledRegexCache.Clear();
                _lastFocusedProcessId = 0;
                _lastFocusedWindowHandle = 0;
                _lastFocusedWindowTitle = string.Empty;
                _monitorGeneration++;
                _started = false;
                _logger.Info("ApplicationTriggerCoordinator", "Stopped monitoring");
            }
        }

        public void RefreshRoutines()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingFocusEvent = null;
                _compiledRegexCache.Clear();
                _lastFocusedProcessId = 0;
                _lastFocusedWindowHandle = 0;
                _lastFocusedWindowTitle = string.Empty;
                _monitorGeneration++;

                IReadOnlyList<AudioRoutine> configured = GetRoutineSnapshot();
                var fingerprints = configured.ToDictionary(static routine => routine.RuntimeTriggerKey,
                    AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint, StringComparer.OrdinalIgnoreCase);
                _activeFocusMatches.RemoveAll(active => !fingerprints.TryGetValue(active.Routine.RuntimeTriggerKey, out string? fingerprint) ||
                    fingerprint != AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(active.Routine));
                bool hasProcessFocusRoutines = configured.Count > 0;

                if (!hasProcessFocusRoutines)
                {
                    Stop();
                }
                else if (!_started && hasProcessFocusRoutines)
                {
                    _monitorRequested = true;
                    _monitorRetries = 0;
                    TryStartMonitor();
                }
            }
        }

        private void TryStartMonitor()
        {
            _monitorRetryTimer?.Stop();
            WindowFocusMonitorStartResult result;
            try { result = _focusMonitor.Start(); }
            catch (Exception ex)
            {
                result = new(false, false, ex.GetType().Name);
                _logger.Warning("ApplicationTriggerCoordinator", "focus-monitor-start-failed", nameof(TryStartMonitor), ex);
            }
            _started = result.Success;
            if (result.Success)
            {
                _monitorGeneration++;
                _monitorRetries = 0;
                _logger.Info("ApplicationTriggerCoordinator", () => $"focus-monitor-started | titleChangesAvailable={result.TitleChangesAvailable}");
                return;
            }
            _logger.Warning("ApplicationTriggerCoordinator", () => $"focus-monitor-unavailable | reason={result.FailureReason ?? "unknown"}");
            if (!_monitorRequested || _monitorRetries++ >= 3) return;
            _monitorRetryTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = _monitorRetryDelay };
            _monitorRetryTimer.Tick -= RetryMonitor;
            _monitorRetryTimer.Tick += RetryMonitor;
            _monitorRetryTimer.Start();
        }

        private void RetryMonitor(object? sender, EventArgs e)
        {
            lock (_lock)
            {
                _monitorRetryTimer?.Stop();
                if (_disposed || !_monitorRequested || _started) return;
                TryStartMonitor();
            }
        }

        private bool HasProcessFocusRoutines()
        {
            return GetRoutineSnapshot().Any(r =>
                r.Enabled &&
                r.TriggerKind == RoutineTriggerKind.Application &&
                r.ApplicationTriggerMode == ApplicationTriggerMode.ProcessFocus &&
                !string.IsNullOrWhiteSpace(r.TriggerAppPath));
        }

        private void OnWindowFocused(object? sender, WindowFocusEventArgs e)
        {
            if (e.ProcessId <= 0 || (e.WindowHandle != 0 && _ignoreFocusWindow?.Invoke(e.WindowHandle) == true))
            {
                return;
            }

            _logger.Debug(
                "ApplicationTriggerCoordinator",
                () => $"window-focused | titleChange={e.IsTitleChange} processId={LogPrivacy.Id(e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))} processName={LogPrivacy.Process(e.ProcessName)} title={LogPrivacy.Label(e.WindowTitle)}");

            int workerGeneration;
            lock (_lock)
            {
                if (_disposed || !_started)
                {
                    return;
                }

                if (_lastFocusedProcessId == e.ProcessId && _lastFocusedWindowHandle == e.WindowHandle &&
                    string.Equals(_lastFocusedWindowTitle, e.WindowTitle, StringComparison.Ordinal))
                {
                    _logger.Debug(
                        "ApplicationTriggerCoordinator",
                        () => $"window-focus-skipped | reason=duplicate-focus processId={LogPrivacy.Id(e.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
                    return;
                }

                _lastFocusedProcessId = e.ProcessId;
                _lastFocusedWindowHandle = e.WindowHandle;
                _lastFocusedWindowTitle = e.WindowTitle;
                _pendingFocusEvent = e;
                if (!e.IsTitleChange) _focusCancellation?.Cancel();

                if (_focusWorkerRunning)
                {
                    return;
                }

                _focusWorkerRunning = true;
                workerGeneration = _monitorGeneration;
            }

            _ = Task.Run(() => ProcessFocusEventsAsync(workerGeneration));
        }

        private async Task ProcessFocusEventsAsync(int workerGeneration)
        {
            try
            {
                while (true)
                {
                    WindowFocusEventArgs focusEvent;
                    lock (_lock)
                    {
                        if (_disposed || !_started || workerGeneration != _monitorGeneration || _pendingFocusEvent == null)
                        {
                            return;
                        }

                        focusEvent = _pendingFocusEvent;
                        _pendingFocusEvent = null;
                    }

                    using var cancellation = new CancellationTokenSource();
                    lock (_lock)
                    {
                        _focusCancellation = cancellation;
                        if (_pendingFocusEvent != null || !IsCurrentWorker(workerGeneration)) cancellation.Cancel();
                    }
                    try { await ProcessFocusEventAsync(focusEvent, workerGeneration, cancellation.Token); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    finally { lock (_lock) if (ReferenceEquals(_focusCancellation, cancellation)) _focusCancellation = null; }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("ApplicationTriggerCoordinator", "window-focus-routine-worker-failed", nameof(ProcessFocusEventsAsync), ex);
            }
            finally
            {
                lock (_lock)
                {
                    _focusWorkerRunning = false;
                    if (!_disposed && _started && _pendingFocusEvent != null)
                    {
                        _focusWorkerRunning = true;
                        int nextGeneration = _monitorGeneration;
                        _ = Task.Run(() => ProcessFocusEventsAsync(nextGeneration));
                    }
                }
            }
        }

        private async Task ProcessFocusEventAsync(WindowFocusEventArgs focusEvent, int workerGeneration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<AudioRoutine> processFocusRoutines = GetRoutineSnapshot(focusEvent);

            _logger.Debug("ApplicationTriggerCoordinator", $"Found {processFocusRoutines.Count} routines for focused application");

            List<AudioRoutine> matchingRoutines = [.. processFocusRoutines.Where(routine =>
                        MatchesTitlePattern(routine, focusEvent.WindowTitle))];
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<(AudioRoutine Routine, int ProcessId)> previousMatches;
            lock (_lock)
            {
                if (_disposed || !_started || workerGeneration != _monitorGeneration)
                {
                    return;
                }

                previousMatches = [.. _activeFocusMatches];
            }

            foreach (AudioRoutine next in matchingRoutines)
            {
                if (previousMatches.Any(previous => previous.ProcessId == focusEvent.ProcessId &&
                    string.Equals(previous.Routine.RuntimeTriggerKey, next.RuntimeTriggerKey, StringComparison.OrdinalIgnoreCase))) continue;
                if (!previousMatches.Any(previous => string.Equals(previous.Routine.Id, next.Id, StringComparison.OrdinalIgnoreCase))) continue;
                if (!IsCurrentWorker(workerGeneration)) return;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_canActivate()) continue;
                    await ExecuteRoutine(next, focusEvent.ProcessId, cancellationToken);
                    TrackFocusActivation(next, focusEvent.ProcessId, workerGeneration);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentWorker(workerGeneration)) return;
                    retained.Add(next.RuntimeTriggerKey);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.Error("ApplicationTriggerCoordinator", "window-focus-trigger-handoff-failed", nameof(ProcessFocusEventsAsync), ex);
                }
            }

            for (int index = previousMatches.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentWorker(workerGeneration))
                {
                    return;
                }

                (AudioRoutine routine, int processId) = previousMatches[index];
                if (processId == focusEvent.ProcessId && matchingRoutines.Any(candidate =>
                    string.Equals(candidate.RuntimeTriggerKey, routine.RuntimeTriggerKey, StringComparison.OrdinalIgnoreCase) &&
                    AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(candidate) ==
                    AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine)))
                {
                    retained.Add(routine.RuntimeTriggerKey);
                    continue;
                }
                lock (_lock) _activeFocusMatches.Remove((routine, processId));
                if (_deactivateRoutine == null)
                {
                    continue;
                }

                try
                {
                    await _deactivateRoutine(routine, processId);
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        "ApplicationTriggerCoordinator",
                        () => $"window-focus-routine-deactivation-failed | routineId={LogPrivacy.Id(routine.Id)} processId={LogPrivacy.Id(processId.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
                        nameof(ProcessFocusEventsAsync),
                        ex);
                }
            }

            foreach (AudioRoutine routine in matchingRoutines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentWorker(workerGeneration))
                {
                    return;
                }

                if (retained.Contains(routine.RuntimeTriggerKey)) continue;

                try
                {
                    if (!_canActivate()) continue;
                    await ExecuteRoutine(routine, focusEvent.ProcessId, cancellationToken);
                    TrackFocusActivation(routine, focusEvent.ProcessId, workerGeneration);
                    _logger.Info(
                        "ApplicationTriggerCoordinator",
                        () => $"window-focus-routine-executed | routineId={LogPrivacy.Id(routine.Id)} processId={LogPrivacy.Id(focusEvent.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))} processName={LogPrivacy.Process(focusEvent.ProcessName)}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.Error(
                        "ApplicationTriggerCoordinator",
                        () => $"window-focus-routine-failed | routineId={LogPrivacy.Id(routine.Id)} processId={LogPrivacy.Id(focusEvent.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
                        nameof(ProcessFocusEventsAsync),
                        ex);
                }
            }
        }

        private void TrackFocusActivation(AudioRoutine routine, int processId, int generation)
        {
            if (_isActive?.Invoke(routine, processId) == false) return;
            lock (_lock)
            {
                if (_disposed || !_started) return;
                if (generation != _monitorGeneration && !GetRoutineSnapshot().Any(current =>
                    string.Equals(current.RuntimeTriggerKey, routine.RuntimeTriggerKey, StringComparison.OrdinalIgnoreCase) &&
                    AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(current) ==
                    AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine))) return;
                if (!_activeFocusMatches.Any(active => active.ProcessId == processId &&
                    string.Equals(active.Routine.RuntimeTriggerKey, routine.RuntimeTriggerKey, StringComparison.OrdinalIgnoreCase)))
                    _activeFocusMatches.Add((routine, processId));
            }
        }

        private bool IsCurrentWorker(int generation)
        {
            lock (_lock)
            {
                return !_disposed && _started && generation == _monitorGeneration;
            }
        }

        private IReadOnlyList<AudioRoutine> GetRoutineSnapshot(WindowFocusEventArgs? focusEvent = null)
        {
            try
            {
                Dictionary<string, bool>? processMatches = focusEvent == null ? null : new(StringComparer.Ordinal);
                bool IncludeTrigger(RoutineTrigger trigger)
                {
                    if (trigger.Kind != RoutineTriggerKind.Application || trigger.ApplicationMode != ApplicationTriggerMode.ProcessFocus) return false;
                    if (focusEvent == null) return true;
                    string target = trigger.AppPath ?? string.Empty;
                    if (!processMatches!.TryGetValue(target, out bool matches))
                        processMatches[target] = matches = MatchesProcess(target, focusEvent);
                    return matches;
                }
                return [.. _routineSnapshotProvider().Where(static routine => routine.Enabled)
                    .SelectMany(routine => routine.ExpandAutomaticTriggers(IncludeTrigger))];
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "ApplicationTriggerCoordinator",
                    () => $"window-focus-routine-snapshot-failed | reason={ex.GetType().Name}",
                    nameof(GetRoutineSnapshot),
                    ex);
                return [];
            }
        }

        private static bool MatchesProcess(string targetPath, WindowFocusEventArgs e)
        {
            targetPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(targetPath);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            if (RoutineTriggerPathHelper.LooksLikeExecutablePath(targetPath))
            {
                if (RoutineTriggerPathHelper.IsExecutableProcessMatch(e.ExecutablePath, targetPath, e.ProcessName))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(e.ExecutablePath))
                {
                    return false;
                }

                string targetName = Path.GetFileNameWithoutExtension(targetPath);
                return string.Equals(targetName, e.ProcessName, StringComparison.OrdinalIgnoreCase);
            }

            if (RoutineTriggerPathHelper.LooksLikePackagedAppId(targetPath))
            {
                return !string.IsNullOrWhiteSpace(e.ExecutablePath) &&
                    RoutineTriggerPathHelper.IsPackagedAppExecutablePathMatch(targetPath, e.ExecutablePath);
            }

            string fallbackTargetName = Path.GetFileNameWithoutExtension(targetPath);
            return string.Equals(fallbackTargetName, e.ProcessName, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesTitlePattern(AudioRoutine routine, string windowTitle)
        {
            string pattern = routine.ApplicationTriggerTitlePattern;

            if (string.IsNullOrWhiteSpace(pattern))
            {
                return true;
            }

            return routine.ApplicationTriggerTitleMatchMode switch
            {
                ApplicationTriggerTitleMatchMode.Exact =>
                    string.Equals(windowTitle, pattern, StringComparison.OrdinalIgnoreCase),
                ApplicationTriggerTitleMatchMode.Contains =>
                    windowTitle.Contains(pattern, StringComparison.OrdinalIgnoreCase),
                ApplicationTriggerTitleMatchMode.Wildcard =>
                    WildcardMatch(windowTitle, pattern),
                ApplicationTriggerTitleMatchMode.Regex =>
                    RegexMatchWithCache(windowTitle, pattern),
                _ => true
            };
        }

        private bool WildcardMatch(string input, string pattern)
        {
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            try
            {
                return Regex.IsMatch(
                    input,
                    regexPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TitleRegexTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                LogTitlePatternTimeout("wildcard", pattern);
                return false;
            }
        }

        private bool RegexMatchWithCache(string input, string pattern)
        {
            try
            {
                Regex regex;
                lock (_lock)
                {
                    if (!_compiledRegexCache.TryGetValue(pattern, out regex!))
                    {
                        regex = new Regex(
                            pattern,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                            TitleRegexTimeout);
                        _compiledRegexCache[pattern] = regex;
                    }
                }

                return regex.IsMatch(input);
            }
            catch (RegexMatchTimeoutException)
            {
                LogTitlePatternTimeout("regex", pattern);
                return false;
            }
            catch (ArgumentException ex)
            {
                _logger.Debug(
                    "ApplicationTriggerCoordinator",
                    () => $"window-title-pattern-invalid | mode=regex pattern={LogPrivacy.Label(pattern)} reason={ex.GetType().Name}");
                return false;
            }
        }

        private void LogTitlePatternTimeout(string mode, string pattern)
        {
            _logger.Warning(
                "ApplicationTriggerCoordinator",
                () => $"window-title-pattern-timeout | mode={mode} timeoutMs={TitleRegexTimeout.TotalMilliseconds:F0} pattern={LogPrivacy.Label(pattern)}");
        }

        internal bool MatchesTitlePatternForTests(AudioRoutine routine, string windowTitle)
        {
            return MatchesTitlePattern(routine, windowTitle);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                Stop();
                _focusMonitor.Dispose();
                _disposed = true;
            }
        }
    }
}

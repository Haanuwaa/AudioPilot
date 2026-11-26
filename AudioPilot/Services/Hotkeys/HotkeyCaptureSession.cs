using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AudioPilot.Services.Hotkeys;

/// <summary>
/// Suspends global shortcuts while editors capture input and until input received by those editors is released.
/// Capture ownership and release checks run on the editor's dispatcher.
/// </summary>
internal sealed partial class HotkeyCaptureSession(Func<int, bool>? isKeyDown = null)
{
    internal static HotkeyCaptureSession Shared { get; } = new();
    private readonly Func<int, bool> _isKeyDown = isKeyDown ?? (key => (GetAsyncKeyState(key) & 0x8000) != 0);
    private readonly List<int> _pendingKeys = [];
    private DispatcherTimer? _releaseTimer;
    private int _owners;
    private int _active;
    private long _generation;

    internal bool IsActive => Volatile.Read(ref _active) != 0;
    internal long Generation => Volatile.Read(ref _generation);
    internal event Action? Changed;

    internal IDisposable Acquire()
    {
        StopReleaseTimer();
        _pendingKeys.RemoveAll(key => !_isKeyDown(key));
        _owners++;
        if (Interlocked.Exchange(ref _active, 1) == 0)
        {
            Interlocked.Increment(ref _generation);
            Changed?.Invoke();
        }
        return new CaptureLease(this);
    }

    internal void TrackKeyDown(int virtualKey)
    {
        if (_owners > 0 && virtualKey is > 0 and < 255 && !_pendingKeys.Contains(virtualKey))
            _pendingKeys.Add(virtualKey);
    }

    internal void TrackKeyUp(int virtualKey) => _pendingKeys.Remove(virtualKey);

    private void Release()
    {
        if (--_owners != 0) return;
        if (TryResume()) return;
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        _releaseTimer.Tick += OnReleaseTick;
        _releaseTimer.Start();
    }

    internal bool TryResume()
    {
        if (_owners != 0) return false;
        _pendingKeys.RemoveAll(key => !_isKeyDown(key));
        if (_pendingKeys.Count != 0) return false;
        StopReleaseTimer();
        if (Interlocked.Exchange(ref _active, 0) != 0) Changed?.Invoke();
        return true;
    }

    private void OnReleaseTick(object? sender, EventArgs e) => TryResume();

    private void StopReleaseTimer()
    {
        if (_releaseTimer == null) return;
        _releaseTimer.Stop();
        _releaseTimer.Tick -= OnReleaseTick;
        _releaseTimer = null;
    }

    private sealed class CaptureLease(HotkeyCaptureSession owner) : IDisposable
    {
        private HotkeyCaptureSession? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);
}

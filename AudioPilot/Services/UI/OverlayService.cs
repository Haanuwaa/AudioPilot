using System.Windows;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.UI
{
    public enum OverlayDeviceKind
    {
        Output = 0,
        Input = 1,
        Error = 2
    }

    public enum OverlayActionStateKind
    {
        Enabled = 0,
        Disabled = 1,
    }

    public class OverlayService : IDisposable
    {
        public readonly record struct OverlayStackItem(OverlayDeviceKind Kind, string Header, string DeviceName);

        internal interface IOverlayPresenter : IDisposable
        {
            int StackAdvancePixels { get; }
            void UpdateContent(string message);
            void UpdateContent(OverlayActionStateKind stateKind, string message);
            void UpdateContent(OverlayDeviceKind kind, string header, string deviceName);
            void UpdateRoutineContent(string header, string? outputDeviceName, string? inputDeviceName);
            void UpdateRoutinePartialContent(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName);
            void UpdateContent(string header, string title, string? artist);
            void ApplyDisplayOptions(OverlayPosition position, double durationSeconds, int stackIndex, int? stackOffsetPixels = null);
            void ShowOverlay();
        }

        private sealed class OverlayWindowPresenter(string initialMessage) : IOverlayPresenter
        {
            private readonly OverlayWindow _window = new(initialMessage);

            public int StackAdvancePixels => _window.StackAdvancePixels;
            public void UpdateContent(string message) => _window.UpdateContent(message);
            public void UpdateContent(OverlayActionStateKind stateKind, string message) => _window.UpdateContent(stateKind, message);
            public void UpdateContent(OverlayDeviceKind kind, string header, string deviceName) => _window.UpdateContent(kind, header, deviceName);
            public void UpdateRoutineContent(string header, string? outputDeviceName, string? inputDeviceName) => _window.UpdateRoutineContent(header, outputDeviceName, inputDeviceName);
            public void UpdateRoutinePartialContent(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName) => _window.UpdateRoutinePartialContent(header, outputDeviceName, inputDeviceName, failedOutputDeviceName, failedInputDeviceName);
            public void UpdateContent(string header, string title, string? artist) => _window.UpdateContent(header, title, artist);
            public void ApplyDisplayOptions(OverlayPosition position, double durationSeconds, int stackIndex, int? stackOffsetPixels = null) => _window.ApplyDisplayOptions(position, durationSeconds, stackIndex, stackOffsetPixels);
            public void ShowOverlay() => _window.ShowOverlay();

            public void Dispose()
            {
                try
                {
                    if (AppDispatcherHelper.IsDispatcherUnavailable(_window.Dispatcher))
                    {
                        _window.Cleanup();
                        return;
                    }

                    if (_window.Dispatcher.CheckAccess())
                    {
                        CloseWindow();
                    }
                    else
                    {
                        _window.Dispatcher.Invoke(CloseWindow);
                    }
                }
                catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(_window.Dispatcher))
                {
                    _window.Cleanup();
                }
            }

            private void CloseWindow()
            {
                if (_window.IsLoaded)
                {
                    _window.Close();
                    return;
                }

                _window.Cleanup();
            }
        }

        private readonly Logger _logger;
        private readonly Action<Action> _dispatch;
        private readonly Func<string, IOverlayPresenter> _presenterFactory;
        private readonly List<IOverlayPresenter> _overlayPresenters = [];
        private long _overlayEnabledState = 1;
        private int _disposed;
        private OverlayPosition _overlayPosition = OverlayPosition.BottomRight;
        private double _overlayDurationSeconds = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds;

        public OverlayService()
            : this(DispatchOnCurrentApplication, initialMessage => new OverlayWindowPresenter(initialMessage))
        {
        }

        internal OverlayService(Action<Action> dispatch, Func<string, IOverlayPresenter> presenterFactory)
        {
            _logger = Logger.Instance;
            _dispatch = dispatch;
            _presenterFactory = presenterFactory;
        }

        internal bool HasPresenterForTests => _overlayPresenters.Count > 0;

        public void UpdateDisplayOptions(OverlayPosition position, double durationSeconds)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _overlayPosition = position;
            _overlayDurationSeconds = double.IsFinite(durationSeconds)
                ? Math.Clamp(durationSeconds, 0.5, 10.0)
                : AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds;
            int stackOffsetPixels = 0;
            for (int i = 0; i < _overlayPresenters.Count; i++)
            {
                IOverlayPresenter presenter = _overlayPresenters[i];
                presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, i, stackOffsetPixels);
                stackOffsetPixels = AddStackAdvance(stackOffsetPixels, presenter.StackAdvancePixels);
            }
        }

        public void UpdateEnabled(bool enabled)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            long enabledValue = enabled ? 1L : 0L;
            while (true)
            {
                long currentState = Volatile.Read(ref _overlayEnabledState);
                if ((currentState & 1L) == enabledValue)
                {
                    return;
                }

                long nextState = unchecked(((currentState & ~1L) + 2L) | enabledValue);
                if (Interlocked.CompareExchange(ref _overlayEnabledState, nextState, currentState) == currentState)
                {
                    if (!enabled)
                    {
                        ReleasePresentersAfterDisable(nextState);
                    }

                    return;
                }
            }
        }

        private void ReleasePresentersAfterDisable(long disabledState)
        {
            try
            {
                _dispatch(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0 &&
                        Volatile.Read(ref _overlayEnabledState) == disabledState)
                    {
                        ReleaseUnusedPresenters(0);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Debug("OverlayService", () => $"overlay-disable-release-dispatch-failed | error={ex.GetType().Name}", nameof(UpdateEnabled));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (IOverlayPresenter presenter in _overlayPresenters)
            {
                try
                {
                    presenter.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Warning("OverlayService", () => $"overlay-presenter-dispose-failed | scope=all error={ex.GetType().Name}", nameof(Dispose), ex);
                }
            }

            _overlayPresenters.Clear();
            GC.SuppressFinalize(this);
        }

        public void Show(string message)
        {
            if (!IsOverlayEnabled())
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    IOverlayPresenter presenter = EnsurePresenter(0, message);
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);

                    presenter.UpdateContent(message);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=plain error={ex.GetType().Name}", nameof(Show), ex);
                }
            }))
            {
                return;
            }
        }

        public void Show(OverlayActionStateKind stateKind, string message)
        {
            if (!IsOverlayEnabled())
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    IOverlayPresenter presenter = EnsurePresenter(0, message);
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);

                    presenter.UpdateContent(stateKind, message);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=action stateKind={stateKind} error={ex.GetType().Name}", nameof(Show), ex);
                }
            }))
            {
                return;
            }
        }

        public void Show(string header, string deviceName)
        {
            Show(OverlayDeviceKind.Output, header, deviceName);
        }

        public void Show(OverlayDeviceKind kind, string header, string deviceName)
        {
            if (!IsOverlayEnabled())
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    IOverlayPresenter presenter = EnsurePresenter(0, $"{header}\n{deviceName}");
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);

                    presenter.UpdateContent(kind, header, deviceName);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=device kind={kind} error={ex.GetType().Name}", nameof(Show), ex);
                }
            }))
            {
                return;
            }
        }

        public void ShowRoutine(string header, string? outputDeviceName, string? inputDeviceName)
        {
            if (!IsOverlayEnabled())
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    string initialMessage = string.Join(
                        "\n",
                        new[] { header, outputDeviceName, inputDeviceName }.Where(static value => !string.IsNullOrWhiteSpace(value)));

                    IOverlayPresenter presenter = EnsurePresenter(0, initialMessage);
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);
                    presenter.UpdateRoutineContent(header, outputDeviceName, inputDeviceName);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=routine error={ex.GetType().Name}", nameof(ShowRoutine), ex);
                }
            }))
            {
                return;
            }
        }

        public void ShowRoutinePartial(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName)
        {
            if (!IsOverlayEnabled())
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    string initialMessage = string.Join(
                        "\n",
                        new[] { header, outputDeviceName, inputDeviceName, failedOutputDeviceName, failedInputDeviceName }.Where(static value => !string.IsNullOrWhiteSpace(value)));

                    IOverlayPresenter presenter = EnsurePresenter(0, initialMessage);
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);
                    presenter.UpdateRoutinePartialContent(header, outputDeviceName, inputDeviceName, failedOutputDeviceName, failedInputDeviceName);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=routine-partial error={ex.GetType().Name}", nameof(ShowRoutinePartial), ex);
                }
            }))
            {
                return;
            }
        }

        public void ShowMediaTrack(string header, string title, string? artist)
        {
            if (!IsOverlayEnabled())
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    IOverlayPresenter presenter = EnsurePresenter(0, $"{header}\n{title}");
                    presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, 0);

                    presenter.UpdateContent(header, title, artist);
                    presenter.ShowOverlay();
                    ReleaseUnusedPresenters(1);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=media-track error={ex.GetType().Name}", nameof(ShowMediaTrack), ex);
                }
            }))
            {
                return;
            }
        }

        public void ShowStacked(IReadOnlyList<OverlayStackItem> items)
        {
            if (!IsOverlayEnabled())
            {
                return;
            }

            OverlayStackItem[] itemSnapshot = [.. items];
            if (itemSnapshot.Length == 0)
            {
                return;
            }

            if (!TryDispatch(() =>
            {
                try
                {
                    int stackOffsetPixels = 0;
                    for (int index = 0; index < itemSnapshot.Length; index++)
                    {
                        OverlayStackItem item = itemSnapshot[index];
                        IOverlayPresenter presenter = EnsurePresenter(index, $"{item.Header}\n{item.DeviceName}");
                        presenter.ApplyDisplayOptions(_overlayPosition, _overlayDurationSeconds, index, stackOffsetPixels);
                        presenter.UpdateContent(item.Kind, item.Header, item.DeviceName);
                        presenter.ShowOverlay();
                        stackOffsetPixels = AddStackAdvance(stackOffsetPixels, presenter.StackAdvancePixels);
                    }

                    ReleaseUnusedPresenters(itemSnapshot.Length);
                }
                catch (Exception ex)
                {
                    _logger.Error("OverlayService", () => $"overlay-show-failed | mode=stacked count={itemSnapshot.Length} error={ex.GetType().Name}", nameof(ShowStacked), ex);
                }
            }))
            {
                return;
            }
        }

        private IOverlayPresenter EnsurePresenter(int index, string initialMessage)
        {
            while (_overlayPresenters.Count <= index)
            {
                _overlayPresenters.Add(_presenterFactory(initialMessage));
            }

            return _overlayPresenters[index];
        }

        private static int AddStackAdvance(int currentOffsetPixels, int stackAdvancePixels)
        {
            long nextOffset = (long)Math.Max(0, currentOffsetPixels) + Math.Max(1, stackAdvancePixels);
            return (int)Math.Min(int.MaxValue, nextOffset);
        }

        private void ReleaseUnusedPresenters(int keepCount)
        {
            while (_overlayPresenters.Count > keepCount)
            {
                int lastIndex = _overlayPresenters.Count - 1;
                IOverlayPresenter presenter = _overlayPresenters[lastIndex];
                _overlayPresenters.RemoveAt(lastIndex);

                try
                {
                    presenter.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Warning("OverlayService", () => $"overlay-presenter-dispose-failed | scope=unused error={ex.GetType().Name}", nameof(ReleaseUnusedPresenters), ex);
                }
            }
        }

        private bool TryDispatch(Action action)
        {
            long enabledState = Volatile.Read(ref _overlayEnabledState);
            if (Volatile.Read(ref _disposed) != 0 || !IsOverlayEnabled(enabledState))
            {
                return false;
            }

            try
            {
                _dispatch(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0 &&
                        Volatile.Read(ref _overlayEnabledState) == enabledState)
                    {
                        action();
                    }
                });
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug("OverlayService", () => $"overlay-dispatch-failed | error={ex.GetType().Name}", nameof(TryDispatch));
                return false;
            }
        }

        private bool IsOverlayEnabled() => IsOverlayEnabled(Volatile.Read(ref _overlayEnabledState));

        private static bool IsOverlayEnabled(long state) => (state & 1L) != 0;

        private static void DispatchOnCurrentApplication(Action action)
        {
            if (Application.Current?.Dispatcher == null)
            {
                throw new InvalidOperationException("Application dispatcher is not available.");
            }

            var dispatcher = Application.Current.Dispatcher;
            if (AppDispatcherHelper.IsDispatcherUnavailable(dispatcher))
            {
                throw new InvalidOperationException("Application dispatcher is shutting down.");
            }

            try
            {
                _ = dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(dispatcher))
            {
                throw new InvalidOperationException("Application dispatcher is shutting down.", ex);
            }
        }
    }
}

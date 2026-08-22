using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Models
{
    public class AudioSessionInfo(
        string displayName,
        float volume,
        bool isMuted = false,
        AudioSessionControl? sessionControl = null,
        Action<AudioSessionInfo>? onVolumeChanged = null,
        string deviceName = "",
        string? processName = null,
        string? mainWindowTitle = null,
        uint? processId = null) : INotifyPropertyChanged, IDisposable
    {
        private float _volume = volume;
        private bool _isMuted = isMuted;
        private readonly AudioSessionControl? _sessionControl = sessionControl;
        private readonly Action<AudioSessionInfo>? _onVolumeChanged = onVolumeChanged;
        private bool _disposed;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string DisplayName { get; set; } = displayName;
        public string DeviceName { get; set; } = deviceName;
        public string? ProcessName { get; set; } = processName;
        public string? MainWindowTitle { get; set; } = mainWindowTitle;
        public uint? ProcessId { get; set; } = processId;

        public float Volume
        {
            get => _volume;
            set
            {
                if (Math.Abs(_volume - value) < 0.01f)
                    return;

                _volume = value;
                OnPropertyChanged(nameof(Volume));
                _onVolumeChanged?.Invoke(this);
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted == value)
                {
                    return;
                }

                _isMuted = value;
                OnPropertyChanged(nameof(IsMuted));
            }
        }

        public AudioSessionControl? AudioSessionControl => _sessionControl;

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _sessionControl?.Dispose();
            }
            catch (Exception ex)
            {
                Logger logger = Logger.Instance;
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioSessionInfo", () => $"audio-session-dispose-failed | reason={ex.GetType().Name}", nameof(Dispose));
                }
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

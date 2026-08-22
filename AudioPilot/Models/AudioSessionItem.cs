using System.ComponentModel;

namespace AudioPilot.Models
{
    public class AudioSessionItem(
        string displayName,
        float volume,
        bool isMaster,
        bool isMic,
        bool isSystemSounds = false,
        uint? processId = null,
        bool isMuted = false,
        string? processName = null,
        string sessionInstanceId = "",
        string endpointId = "") : INotifyPropertyChanged
    {
        private float _volume = volume;
        private bool _isMuted = isMuted;
        private bool _suppressVolumeChanged;
        private bool _suppressMuteChanged;

        public string DisplayName { get; } = displayName;
        public bool IsMaster { get; } = isMaster;
        public bool IsMic { get; } = isMic;
        public bool IsSystemSounds { get; } = isSystemSounds;
        public uint? ProcessId { get; } = processId;
        public string? ProcessName { get; private set; } = processName;
        public string SessionInstanceId { get; private set; } = sessionInstanceId;
        public string EndpointId { get; private set; } = endpointId;

        public float Volume
        {
            get => _volume;
            set
            {
                if (Math.Abs(_volume - value) < 0.01f) return;
                _volume = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
                if (!_suppressVolumeChanged)
                {
                    VolumeChanged?.Invoke(this);
                }
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted == value) return;
                _isMuted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMuted)));
                if (!_suppressMuteChanged)
                {
                    MuteChanged?.Invoke(this);
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<AudioSessionItem>? VolumeChanged;
        public event Action<AudioSessionItem>? MuteChanged;

        public void UpdateRoutingMetadataFromSystem(string? processName, string sessionInstanceId, string endpointId)
        {
            ProcessName = processName;
            SessionInstanceId = sessionInstanceId;
            EndpointId = endpointId;
        }

        public void SetVolumeFromSystem(float value)
        {
            if (Math.Abs(_volume - value) < 0.01f)
            {
                return;
            }

            _suppressVolumeChanged = true;
            try
            {
                Volume = value;
            }
            finally
            {
                _suppressVolumeChanged = false;
            }
        }

        public void SetMuteFromSystem(bool value)
        {
            if (_isMuted == value)
            {
                return;
            }

            _suppressMuteChanged = true;
            try
            {
                IsMuted = value;
            }
            finally
            {
                _suppressMuteChanged = false;
            }
        }

        public void SetStateFromSystem(float volume, bool isMuted)
        {
            bool volumeChanged = Math.Abs(_volume - volume) >= 0.01f;
            bool muteChanged = _isMuted != isMuted;
            if (!volumeChanged && !muteChanged)
            {
                return;
            }

            _suppressVolumeChanged = true;
            _suppressMuteChanged = true;
            try
            {
                if (volumeChanged)
                {
                    Volume = volume;
                }

                if (muteChanged)
                {
                    IsMuted = isMuted;
                }
            }
            finally
            {
                _suppressMuteChanged = false;
                _suppressVolumeChanged = false;
            }
        }
    }
}

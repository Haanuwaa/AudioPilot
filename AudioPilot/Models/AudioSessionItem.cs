using System.ComponentModel;
using AudioPilot.Localization;

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
        string endpointId = "",
        int sessionCount = 1,
        string endpointName = "",
        NAudio.CoreAudioApi.Role? endpointRole = null) : INotifyPropertyChanged
    {
        private static readonly PropertyChangedEventArgs VolumeChangedArgs = new(nameof(Volume));
        private static readonly PropertyChangedEventArgs MuteChangedArgs = new(nameof(IsMuted));
        private float _volume = volume;
        private bool _isMuted = isMuted;
        private bool _suppressVolumeChanged;
        private bool _suppressMuteChanged;
        private long _volumeEditRevision;
        private long _muteEditRevision;
        private long _pendingVolumeRevision = -1;
        private long _pendingMuteRevision = -1;
        private bool _isGroupSelected;
        private ApplicationRoutingDetails? _routingDetails;

        public bool IsApplication => ProcessId.GetValueOrDefault() != 0 && !IsMaster && !IsMic && !IsSystemSounds;
        public bool IsGroupSelected { get => _isGroupSelected; internal set { if (_isGroupSelected == value) return; _isGroupSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsGroupSelected))); } }
        public bool IsRoutingExpanded => _routingDetails != null;
        public ApplicationRoutingDetails? RoutingDetails
        {
            get => _routingDetails;
            internal set { _routingDetails = value; PropertyChanged?.Invoke(this, new(nameof(RoutingDetails))); PropertyChanged?.Invoke(this, new(nameof(IsRoutingExpanded))); }
        }
        internal long? ProcessStartedTicks { get; set; }

        internal long VolumeEditRevision => Interlocked.Read(ref _volumeEditRevision);
        internal long MuteEditRevision => Interlocked.Read(ref _muteEditRevision);
        internal bool HasPendingVolumeEdit => Interlocked.Read(ref _pendingVolumeRevision) >= 0;
        internal bool HasPendingMuteEdit => Interlocked.Read(ref _pendingMuteRevision) >= 0;

        internal void BeginVolumeEdit(long revision) => Interlocked.Exchange(ref _pendingVolumeRevision, revision);
        internal void BeginMuteEdit(long revision) => Interlocked.Exchange(ref _pendingMuteRevision, revision);
        internal void CompleteVolumeEdit(long revision) => Interlocked.CompareExchange(ref _pendingVolumeRevision, -1, revision);
        internal void CompleteMuteEdit(long revision) => Interlocked.CompareExchange(ref _pendingMuteRevision, -1, revision);

        public string DisplayName { get; private set; } = displayName;
        public object DisplayLabel => DisplayName switch
        {
            "Master Volume" when IsMaster => new LocalizedText("Mixer_MasterVolume"),
            "Microphone Volume" when IsMic => new LocalizedText("Mixer_MicrophoneVolume"),
            "System Sounds" when IsSystemSounds => new LocalizedText("Mixer_SystemSounds"),
            _ => DisplayName,
        };
        public bool IsMaster { get; } = isMaster;
        public bool IsMic { get; } = isMic;
        public bool IsSystemSounds { get; } = isSystemSounds;
        public uint? ProcessId { get; } = processId;
        public string? ProcessName { get; private set; } = processName;
        public string SessionInstanceId { get; private set; } = sessionInstanceId;
        public string EndpointId { get; private set; } = endpointId;
        public int SessionCount { get; private set; } = sessionCount;
        public bool IsEndpoint => IsMaster || IsMic;
        public string EndpointName { get; private set; } = endpointName;
        public NAudio.CoreAudioApi.Role? EndpointRole { get; private set; } = endpointRole;
        public object EndpointLabel => string.IsNullOrWhiteSpace(EndpointName) ? new LocalizedText("Mixer_DeviceUnavailable") : EndpointName;
        public object EndpointDescription => !IsEndpoint ? DisplayLabel : EndpointRole is { } role
            ? new LocalizedText("Mixer_RoleDevice", role switch
            {
                NAudio.CoreAudioApi.Role.Communications => new LocalizedText("Ui_Communications"),
                NAudio.CoreAudioApi.Role.Console => new LocalizedText("Ui_Console"),
                _ => (object)"Multimedia",
            }, EndpointLabel) : EndpointLabel;

        public float Volume
        {
            get => _volume;
            set
            {
                if (Math.Abs(_volume - value) < 0.01f) return;
                if (!_suppressVolumeChanged) Interlocked.Increment(ref _volumeEditRevision);
                _volume = value;
                PropertyChanged?.Invoke(this, VolumeChangedArgs);
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
                if (!_suppressMuteChanged) Interlocked.Increment(ref _muteEditRevision);
                _isMuted = value;
                PropertyChanged?.Invoke(this, MuteChangedArgs);
                if (!_suppressMuteChanged)
                {
                    MuteChanged?.Invoke(this);
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<AudioSessionItem>? VolumeChanged;
        public event Action<AudioSessionItem>? MuteChanged;

        internal void SetDisplayNameFromSystem(string value)
        {
            if (string.Equals(DisplayName, value, StringComparison.Ordinal))
            {
                return;
            }

            DisplayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }

        public void UpdateRoutingMetadataFromSystem(string? processName, string sessionInstanceId, string endpointId, int sessionCount = 1,
            string? endpointName = null, NAudio.CoreAudioApi.Role? endpointRole = null)
        {
            if (endpointName != null || !string.Equals(EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            {
                string name = endpointName ?? string.Empty;
                if (!string.Equals(EndpointName, name, StringComparison.Ordinal) || EndpointRole != endpointRole)
                {
                    EndpointName = name;
                    EndpointRole = endpointRole;
                    PropertyChanged?.Invoke(this, new(nameof(EndpointLabel)));
                    PropertyChanged?.Invoke(this, new(nameof(EndpointDescription)));
                }
            }
            ProcessName = processName;
            SessionInstanceId = sessionInstanceId;
            EndpointId = endpointId;
            SessionCount = sessionCount;
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

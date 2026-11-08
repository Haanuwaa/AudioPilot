using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AudioPilot.Localization;

namespace AudioPilot.Models
{
    public class CycleDevice : INotifyPropertyChanged
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StableId { get; set; }

        private int _displayOrder = 1;

        [JsonIgnore]
        public int DisplayOrder
        {
            get => _displayOrder;
            set
            {
                if (_displayOrder == value) return;
                _displayOrder = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        [JsonIgnore]
        public string DisplayName => $"{DisplayOrder}. {Name}";

        [JsonIgnore]
        public object ChoiceLabel => Id.Length == 0 ? Name switch
        {
            "Leave unchanged" => new LocalizedText("RoutineChoice_LeaveUnchanged"),
            "Default output" => new LocalizedText("Device_DefaultOutput"),
            _ => Name,
        } : Name;

        public CycleDevice Clone()
        {
            return new CycleDevice
            {
                Id = Id,
                Name = Name,
                StableId = StableId,
                DisplayOrder = DisplayOrder
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace AudioPilot.Models
{
    public class CycleDevice : INotifyPropertyChanged
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

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

        public CycleDevice Clone()
        {
            return new CycleDevice
            {
                Id = Id,
                Name = Name,
                DisplayOrder = DisplayOrder
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

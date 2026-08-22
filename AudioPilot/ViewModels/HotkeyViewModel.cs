using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AudioPilot.Behaviors;

namespace AudioPilot.ViewModels
{
    public class HotkeyViewModel : IHotkeySink, INotifyPropertyChanged
    {
        private string _baseHoverText = string.Empty;
        private bool _hasDuplicateWarning;
        private HotkeyWarningKind _validationWarningKind;
        private string _validationWarningText = string.Empty;
        private string _rejectedHotkeyText = string.Empty;
        private string _rejectedHotkeyDisplayText = string.Empty;
        private HotkeyWarningKind _registrationWarningKind;
        private string _registrationWarningText = string.Empty;
        private HotkeyMainInput _savedStandaloneInput;
        private bool _isEditing;
        private bool _deferStandaloneAcknowledgement;

        private bool IsStandaloneBinding => Modifiers.Count == 0 && MainInput.AllowsStandaloneWithoutModifier;

        public List<Key> Modifiers { get; } = [];

        public HotkeyMainInput MainInput { get; private set; } = HotkeyMainInput.None;

        public Key MainKey => MainInput.Kind == HotkeyMainInputKind.Keyboard ? MainInput.Key : Key.None;

        public bool HasMainInput => MainInput.HasValue;
        public bool RequiresHeldInput { get; init; }

        public string BaseHoverText
        {
            get => _baseHoverText;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_baseHoverText, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _baseHoverText = normalized;
                NotifyWarningChanged();
            }
        }

        public HotkeyWarningKind WarningKind
        {
            get
            {
                if (_validationWarningKind != HotkeyWarningKind.None)
                {
                    return _validationWarningKind;
                }

                if (_hasDuplicateWarning)
                {
                    return HotkeyWarningKind.Duplicate;
                }

                if (_registrationWarningKind != HotkeyWarningKind.None)
                    return _registrationWarningKind;

                return IsStandaloneBinding && (_deferStandaloneAcknowledgement || MainInput != _savedStandaloneInput)
                    ? HotkeyWarningKind.Standalone
                    : HotkeyWarningKind.None;
            }
        }

        public bool HasWarning => WarningKind != HotkeyWarningKind.None;

        public string HoverText => GetEffectiveWarningText() ?? _baseHoverText;

        /// <summary>Records the persisted binding without acknowledging a newer, unsaved edit.</summary>
        public void AcknowledgeSavedBinding(string? persistedBinding, bool deferWhileEditing = false)
        {
            bool needsAcknowledgement = IsStandaloneBinding && (_deferStandaloneAcknowledgement || MainInput != _savedStandaloneInput);
            var parsed = new HotkeyParsingService().ParseHotkeyString(persistedBinding ?? string.Empty);
            _savedStandaloneInput = parsed.HasValue && parsed.Value.modifiers.Count == 0
                ? parsed.Value.mainInput : HotkeyMainInput.None;
            _deferStandaloneAcknowledgement = deferWhileEditing && _isEditing && needsAcknowledgement
                && MainInput == _savedStandaloneInput;
            NotifyWarningChanged();
        }

        public void SetEditing(bool isEditing)
        {
            _isEditing = isEditing;
            if (!isEditing && _deferStandaloneAcknowledgement)
            {
                _deferStandaloneAcknowledgement = false;
                NotifyWarningChanged();
            }
        }

        public void Reset()
        {
            Modifiers.Clear();
            MainInput = HotkeyMainInput.None;
            ClearRejectedHotkeyState();
            ClearRegistrationWarning();
            SetDuplicateWarning(false);
            NotifyHotkeyChanged();
        }

        public void SetMain(HotkeyMainInput input)
        {
            ClearRejectedHotkeyState();
            ClearRegistrationWarning();
            MainInput = input;
            NotifyHotkeyChanged();
        }

        public void AddModifier(Key key)
        {
            if (!Modifiers.Contains(key) && Modifiers.Count < 4)
            {
                ClearRejectedHotkeyState();
                ClearRegistrationWarning();
                Modifiers.Add(key);
                NotifyHotkeyChanged();
            }
        }

        public void RemoveModifier(Key key)
        {
            if (Modifiers.Remove(key))
            {
                ClearRejectedHotkeyState();
                ClearRegistrationWarning();
                NotifyHotkeyChanged();
            }
        }

        public bool IsSupportedHotkey(HotkeyMainInput input, IReadOnlyList<Key> modifiers)
            => HotkeyReservedShortcutPolicy.IsSupported(input, modifiers) && (!RequiresHeldInput || MicrophoneHoldService.IsHoldable(input));

        public void SetRejectedHotkey(HotkeyMainInput input, IReadOnlyList<Key> modifiers)
        {
            bool reserved = HotkeyReservedShortcutPolicy.IsReserved(input, modifiers, out string reservedShortcutName);
            bool cannotHold = RequiresHeldInput && !MicrophoneHoldService.IsHoldable(input);
            if (!input.HasValue || (!reserved && !cannotHold && input.IsSupportedModifierCount(modifiers.Count)))
            {
                return;
            }

            string serialization = HotkeyParsingService.BuildComboKey(modifiers, input);
            Reset();
            SetRejectedHotkeyState(
                serialization,
                FormatHotkeyDisplayText(serialization),
                reserved ? HotkeyWarningKind.Reserved : cannotHold ? HotkeyWarningKind.HoldRequired : HotkeyWarningKind.ModifierRequired,
                reserved
                    ? $"Reserved by Windows ({reservedShortcutName}). Choose a different hotkey."
                    : cannotHold ? "Choose a key or mouse button that can be held. Wheel input, Pause, and Print Screen cannot be used here."
                    : "Hold Ctrl, Alt, Shift, or Win while choosing this hotkey.");
        }

        public void SetDuplicateWarning(bool hasDuplicateWarning)
        {
            if (_hasDuplicateWarning == hasDuplicateWarning)
            {
                return;
            }

            _hasDuplicateWarning = hasDuplicateWarning;
            NotifyWarningChanged();
        }

        public void SetRegistrationWarning(HotkeyWarningKind warningKind, string warningText)
        {
            HotkeyWarningKind normalizedKind = warningKind is HotkeyWarningKind.ExternalConflict or HotkeyWarningKind.Fallback
                ? warningKind
                : HotkeyWarningKind.None;
            string normalizedText = normalizedKind == HotkeyWarningKind.None
                ? string.Empty
                : warningText?.Trim() ?? string.Empty;

            if (_registrationWarningKind == normalizedKind && string.Equals(_registrationWarningText, normalizedText, StringComparison.Ordinal))
            {
                return;
            }

            _registrationWarningKind = normalizedKind;
            _registrationWarningText = normalizedText;
            NotifyWarningChanged();
        }

        public void ClearRegistrationWarning()
        {
            if (_registrationWarningKind == HotkeyWarningKind.None && _registrationWarningText.Length == 0)
            {
                return;
            }

            _registrationWarningKind = HotkeyWarningKind.None;
            _registrationWarningText = string.Empty;
            NotifyWarningChanged();
        }

        public string DisplayText
        {
            get
            {
                if (!MainInput.HasValue && _rejectedHotkeyDisplayText.Length > 0)
                {
                    return _rejectedHotkeyDisplayText;
                }

                var parts = new List<string>();

                foreach (Key modifier in Modifiers.Distinct())
                {
                    parts.Add(modifier switch
                    {
                        Key.LeftCtrl => "Ctrl",
                        Key.LeftShift => "Shift",
                        Key.LeftAlt => "Alt",
                        Key.LWin => "Win",
                        _ => modifier.ToString()
                    });
                }

                if (MainInput.HasValue)
                {
                    parts.Add(MainInput.DisplayText);
                }

                return string.Join(" + ", parts);
            }
        }

        public void RefreshDisplayText()
        {
            if (_rejectedHotkeyText.Length > 0)
            {
                _rejectedHotkeyDisplayText = FormatHotkeyDisplayText(_rejectedHotkeyText);
            }

            OnPropertyChanged(nameof(DisplayText));
        }

        public string ToHotkeyString()
        {
            if (!MainInput.HasValue)
            {
                return _rejectedHotkeyText;
            }

            var parts = new List<string>();
            foreach (Key modifier in Modifiers.Distinct())
            {
                parts.Add(modifier switch
                {
                    Key.LeftCtrl or Key.RightCtrl => "Ctrl",
                    Key.LeftShift or Key.RightShift => "Shift",
                    Key.LeftAlt or Key.RightAlt => "Alt",
                    Key.LWin or Key.RWin => "Win",
                    _ => modifier.ToString()
                });
            }

            parts.Add(MainInput.SerializationToken);
            return string.Join("+", parts);
        }

        public bool LoadFromString(string? hotkeyString)
        {
            Reset();

            if (string.IsNullOrWhiteSpace(hotkeyString))
            {
                return false;
            }

            var parsed = new HotkeyParsingService().ParseHotkeyString(hotkeyString);
            if (!parsed.HasValue)
            {
                return false;
            }

            if (!IsSupportedHotkey(parsed.Value.mainInput, parsed.Value.modifiers))
            {
                SetRejectedHotkey(parsed.Value.mainInput, parsed.Value.modifiers);
                return false;
            }

            foreach (Key modifier in parsed.Value.modifiers)
            {
                AddModifier(NormalizeModifier(modifier));
            }

            MainInput = parsed.Value.mainInput;
            NotifyHotkeyChanged();
            return MainInput.HasValue;
        }

        private static Key NormalizeModifier(Key key) => key switch
        {
            Key.RightCtrl => Key.LeftCtrl,
            Key.RightShift => Key.LeftShift,
            Key.RightAlt => Key.LeftAlt,
            Key.RWin => Key.LWin,
            _ => key
        };

        public uint MainVirtual => MainInput.KeyboardVirtualKey;

        public List<uint> ModVirtual
        {
            get
            {
                var result = new List<uint>(Modifiers.Count);
                var seen = new HashSet<uint>();

                for (int index = 0; index < Modifiers.Count; index++)
                {
                    Key normalized = NormalizeModifier(Modifiers[index]);
                    uint vk = (uint)KeyInterop.VirtualKeyFromKey(normalized);
                    if (seen.Add(vk))
                    {
                        result.Add(vk);
                    }
                }

                return result;
            }
        }

        public void Load(uint mainVk, List<uint>? modsVk)
        {
            Reset();

            if (mainVk != 0)
            {
                MainInput = HotkeyMainInput.FromKeyboard(KeyInterop.KeyFromVirtualKey((int)mainVk));
            }

            if (modsVk != null)
            {
                foreach (uint vk in modsVk)
                {
                    Key key = KeyInterop.KeyFromVirtualKey((int)vk);
                    AddModifier(NormalizeModifier(key));
                }
            }

            NotifyHotkeyChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string FormatHotkeyDisplayText(string serialization)
        {
            return HotkeyDisplayFormatter.FormatSpaced(serialization);
        }

        private string? GetEffectiveWarningText()
        {
            if (_validationWarningKind != HotkeyWarningKind.None)
            {
                return _validationWarningText;
            }

            if (_hasDuplicateWarning)
            {
                return "Conflicts with another configured AudioPilot hotkey. Choose a different hotkey.";
            }

            if (_registrationWarningKind != HotkeyWarningKind.None)
            {
                return _registrationWarningText;
            }

            if (!IsStandaloneBinding)
                return null;

            return MainInput.Kind == HotkeyMainInputKind.MouseButton
                ? "This mouse button will act as a global shortcut and replace its usual Back/Forward action while assigned."
                : "This key will act as a global shortcut and may override its usual action.";
        }

        private void SetRejectedHotkeyState(string rejectedText, string rejectedDisplayText, HotkeyWarningKind warningKind, string warningText)
        {
            _rejectedHotkeyText = rejectedText;
            _rejectedHotkeyDisplayText = rejectedDisplayText;
            _validationWarningKind = warningKind;
            _validationWarningText = warningText;
            NotifyHotkeyChanged();
            NotifyWarningChanged();
        }

        private void ClearRejectedHotkeyState()
        {
            if (_rejectedHotkeyText.Length == 0 &&
                _rejectedHotkeyDisplayText.Length == 0 &&
                _validationWarningKind == HotkeyWarningKind.None &&
                _validationWarningText.Length == 0)
            {
                return;
            }

            _rejectedHotkeyText = string.Empty;
            _rejectedHotkeyDisplayText = string.Empty;
            _validationWarningKind = HotkeyWarningKind.None;
            _validationWarningText = string.Empty;
            NotifyWarningChanged();
        }

        private void NotifyHotkeyChanged()
        {
            OnPropertyChanged(nameof(MainInput));
            OnPropertyChanged(nameof(MainKey));
            OnPropertyChanged(nameof(MainVirtual));
            OnPropertyChanged(nameof(HasMainInput));
            OnPropertyChanged(nameof(DisplayText));
            NotifyWarningChanged();
        }

        private void NotifyWarningChanged()
        {
            OnPropertyChanged(nameof(WarningKind));
            OnPropertyChanged(nameof(HasWarning));
            OnPropertyChanged(nameof(HoverText));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

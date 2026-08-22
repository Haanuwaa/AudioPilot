using System.Windows.Input;
using AudioPilot.Services.Hotkeys;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public class HotkeyViewModelTests
{
    [Fact]
    public void LoadFromString_ParsesAliases_AndRoundTripsToHotkeyString()
    {
        var vm = new HotkeyViewModel();

        bool loaded = vm.LoadFromString("Ctrl+Alt+.");

        Assert.True(loaded);
        Assert.Equal(Key.OemPeriod, vm.MainKey);
        Assert.Contains(Key.LeftCtrl, vm.Modifiers);
        Assert.Contains(Key.LeftAlt, vm.Modifiers);
        Assert.Equal("Ctrl+Alt+.", vm.ToHotkeyString());
        Assert.Equal(HotkeyDisplayFormatter.FormatSpaced("Ctrl+Alt+."), vm.DisplayText);
    }

    [Fact]
    public void LoadFromString_NormalizesRightSideModifiers()
    {
        var vm = new HotkeyViewModel();

        bool loaded = vm.LoadFromString("RightCtrl+RightAlt+F8");

        Assert.True(loaded);
        Assert.DoesNotContain(Key.RightCtrl, vm.Modifiers);
        Assert.DoesNotContain(Key.RightAlt, vm.Modifiers);
        Assert.Contains(Key.LeftCtrl, vm.Modifiers);
        Assert.Contains(Key.LeftAlt, vm.Modifiers);
        Assert.Equal("Ctrl+Alt+F8", vm.ToHotkeyString());
    }

    [Fact]
    public void LoadFromString_InvalidMainKey_ReturnsFalse_AndLeavesMainKeyUnset()
    {
        var vm = new HotkeyViewModel();
        vm.LoadFromString("Ctrl+P");

        bool loaded = vm.LoadFromString("Ctrl+NotARealKey");

        Assert.False(loaded);
        Assert.Equal(Key.None, vm.MainKey);
        Assert.Empty(vm.Modifiers);
        Assert.Equal(string.Empty, vm.ToHotkeyString());
    }

    [Fact]
    public void LoadFromString_ParsesMouseButtonHotkey()
    {
        var vm = new HotkeyViewModel();

        bool loaded = vm.LoadFromString("Ctrl+MouseX1");

        Assert.True(loaded);
        Assert.True(vm.HasMainInput);
        Assert.Equal(Key.None, vm.MainKey);
        Assert.Equal("Ctrl+MouseX1", vm.ToHotkeyString());
        Assert.Equal("Ctrl + Mouse 4", vm.DisplayText);
    }

    [Fact]
    public void LoadFromString_ParsesWheelHotkey()
    {
        var vm = new HotkeyViewModel();

        bool loaded = vm.LoadFromString("Alt+WheelDown");

        Assert.True(loaded);
        Assert.True(vm.HasMainInput);
        Assert.Contains(Key.LeftAlt, vm.Modifiers);
        Assert.Equal("Alt+WheelDown", vm.ToHotkeyString());
        Assert.Equal("Alt + Wheel Down", vm.DisplayText);
    }

    [Theory]
    [InlineData("Ctrl+WheelLeft", "Ctrl + Wheel Left")]
    [InlineData("Ctrl+WheelRight", "Ctrl + Wheel Right")]
    public void LoadFromString_ParsesHorizontalWheelHotkeys(string serialized, string expectedDisplay)
    {
        var vm = new HotkeyViewModel();

        Assert.True(vm.LoadFromString(serialized));
        Assert.Equal(serialized, vm.ToHotkeyString());
        Assert.Equal(expectedDisplay, vm.DisplayText);
    }

    [Fact]
    public void LoadFromString_RejectsBareTextKey()
    {
        var vm = new HotkeyViewModel();

        bool loaded = vm.LoadFromString("A");

        Assert.False(loaded);
        Assert.False(vm.HasMainInput);
        Assert.Equal(string.Empty, vm.ToHotkeyString());
    }

    [Fact]
    public void LoadFromString_AllowsStandaloneFunctionKey()
    {
        var vm = new HotkeyViewModel();

        bool loaded = vm.LoadFromString("F8");

        Assert.True(loaded);
        Assert.True(vm.HasMainInput);
        Assert.Equal(Key.F8, vm.MainKey);
        Assert.Equal("F8", vm.ToHotkeyString());
    }

    [Theory]
    [InlineData("Alt+Tab", "Alt+Tab")]
    [InlineData("Ctrl+Esc", "Ctrl+Escape")]
    [InlineData("Ctrl+Shift+Esc", "Ctrl+Shift+Escape")]
    [InlineData("Win+R", "Win+R")]
    [InlineData("Win+Shift+S", "Shift+Win+S")]
    [InlineData("Win+Space", "Win+Space")]
    [InlineData("Win+P", "Win+P")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("Alt+Space", "Alt+Space")]
    [InlineData("Win+K", "Win+K")]
    [InlineData("Win+S", "Win+S")]
    [InlineData("Win+A", "Win+A")]
    [InlineData("Win+V", "Win+V")]
    [InlineData("Win+G", "Win+G")]
    [InlineData("Win+Left", "Win+Left")]
    [InlineData("Win+Shift+Right", "Shift+Win+Right")]
    [InlineData("Ctrl+Win+Down", "Ctrl+Win+Down")]
    [InlineData("Win+Tab", "Win+Tab")]
    [InlineData("Ctrl+Win+Tab", "Ctrl+Win+Tab")]
    [InlineData("Win+Shift+Tab", "Shift+Win+Tab")]
    public void LoadFromString_RejectsReservedWindowsShortcuts(string hotkey, string expectedSerialization)
    {
        var vm = new HotkeyViewModel();

        bool loaded = vm.LoadFromString(hotkey);

        Assert.False(loaded);
        Assert.False(vm.HasMainInput);
        Assert.Equal(expectedSerialization, vm.ToHotkeyString());
        Assert.Equal(HotkeyWarningKind.Reserved, vm.WarningKind);
    }

    [Theory]
    [InlineData("Ctrl+Win+E", "Ctrl+Win+E")]
    [InlineData("Shift+Win+R", "Shift+Win+R")]
    [InlineData("Alt+Win+X", "Alt+Win+X")]
    [InlineData("Ctrl+Shift+Win+Tab", "Ctrl+Shift+Win+Tab")]
    public void LoadFromString_AllowsModifiedWinShortcutsOutsideReservedSet(string hotkey, string expectedSerialization)
    {
        var vm = new HotkeyViewModel();

        bool loaded = vm.LoadFromString(hotkey);

        Assert.True(loaded);
        Assert.True(vm.HasMainInput);
        Assert.Equal(expectedSerialization, vm.ToHotkeyString());
        Assert.Equal(HotkeyWarningKind.None, vm.WarningKind);
    }

    [Fact]
    public void LoadFromString_ReservedShortcut_PreservesRejectedDisplayAndWarning()
    {
        var vm = new HotkeyViewModel
        {
            BaseHoverText = "Base help",
        };

        bool loaded = vm.LoadFromString("Win+R");

        Assert.False(loaded);
        Assert.Equal("Win + R", vm.DisplayText);
        Assert.Equal("Win+R", vm.ToHotkeyString());
        Assert.Equal(HotkeyWarningKind.Reserved, vm.WarningKind);
        Assert.True(vm.HasWarning);
        Assert.Contains("Reserved by Windows", vm.HoverText, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshDisplayText_RaisesDisplayTextChangedWithoutChangingSerialization()
    {
        var vm = new HotkeyViewModel();
        Assert.True(vm.LoadFromString("Ctrl+OemPlus"));
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.RefreshDisplayText();

        Assert.Contains(nameof(HotkeyViewModel.DisplayText), changedProperties);
        Assert.Equal("Ctrl++", vm.ToHotkeyString());
    }

    [Fact]
    public void HoverText_UsesDuplicateWarningBeforeRegistrationWarning()
    {
        var vm = new HotkeyViewModel
        {
            BaseHoverText = "Base help",
        };

        vm.SetRegistrationWarning(HotkeyWarningKind.ExternalConflict, "External conflict");
        vm.SetDuplicateWarning(true);

        Assert.Equal(HotkeyWarningKind.Duplicate, vm.WarningKind);
        Assert.Contains("Conflicts with another configured AudioPilot hotkey", vm.HoverText, StringComparison.Ordinal);

        vm.SetDuplicateWarning(false);

        Assert.Equal(HotkeyWarningKind.ExternalConflict, vm.WarningKind);
        Assert.Equal("External conflict", vm.HoverText);
    }

    [Fact]
    public void HoverText_FallsBackToBaseTextWhenWarningsClear()
    {
        var vm = new HotkeyViewModel
        {
            BaseHoverText = "Base help",
        };

        vm.SetRegistrationWarning(HotkeyWarningKind.Fallback, "Fallback warning");
        Assert.Equal("Fallback warning", vm.HoverText);

        vm.ClearRegistrationWarning();

        Assert.Equal(HotkeyWarningKind.None, vm.WarningKind);
        Assert.False(vm.HasWarning);
        Assert.Equal("Base help", vm.HoverText);
    }
}


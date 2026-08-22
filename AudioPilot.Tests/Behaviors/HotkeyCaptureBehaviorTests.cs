using System.Windows.Input;
using AudioPilot.Behaviors;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Behaviors;

public class HotkeyCaptureBehaviorTests
{
    [Fact]
    public void ShouldResetOnDelete_ReturnsTrue_ForPlainDelete()
    {
        bool shouldReset = HotkeyCaptureBehavior.ShouldResetOnDelete(Key.Delete, ModifierKeys.None);

        Assert.True(shouldReset);
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Windows)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void ShouldResetOnDelete_ReturnsFalse_ForModifiedDelete(ModifierKeys modifiers)
    {
        bool shouldReset = HotkeyCaptureBehavior.ShouldResetOnDelete(Key.Delete, modifiers);

        Assert.False(shouldReset);
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Windows)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void ShouldSuppressContextMenuAfterMouseCapture_ReturnsTrue_ForModifiedRightClick(ModifierKeys modifiers)
    {
        bool shouldSuppress = HotkeyCaptureBehavior.ShouldSuppressContextMenuAfterMouseCapture(MouseButton.Right, modifiers);

        Assert.True(shouldSuppress);
    }

    [Theory]
    [InlineData(MouseButton.Right, ModifierKeys.None)]
    [InlineData(MouseButton.Left, ModifierKeys.Control)]
    [InlineData(MouseButton.Middle, ModifierKeys.Control)]
    [InlineData(MouseButton.XButton1, ModifierKeys.Control)]
    public void ShouldSuppressContextMenuAfterMouseCapture_ReturnsFalse_Otherwise(MouseButton button, ModifierKeys modifiers)
    {
        bool shouldSuppress = HotkeyCaptureBehavior.ShouldSuppressContextMenuAfterMouseCapture(button, modifiers);

        Assert.False(shouldSuppress);
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(2, 0, 5, true)]
    [InlineData(5, 1, 5, true)]
    [InlineData(0, 3, 0, true)]
    public void ShouldMoveCaretToEnd_ReflectsWhetherSelectionAlreadyEndsAtTextEnd(int selectionStart, int selectionLength, int textLength, bool expected)
    {
        bool shouldMove = HotkeyCaptureBehavior.ShouldMoveCaretToEnd(selectionStart, selectionLength, textLength);

        Assert.Equal(expected, shouldMove);
    }

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    [InlineData(Key.PageUp)]
    [InlineData(Key.PageDown)]
    public void ShouldLeaveKeyDownForTextboxNavigation_ReturnsTrue_ForPlainNavigationKeys(Key key)
    {
        bool shouldLeave = HotkeyCaptureBehavior.ShouldLeaveKeyDownForTextboxNavigation(key, ModifierKeys.None);

        Assert.True(shouldLeave);
    }

    [Theory]
    [InlineData(Key.Left, ModifierKeys.Control)]
    [InlineData(Key.Right, ModifierKeys.Alt)]
    [InlineData(Key.Up, ModifierKeys.Shift)]
    [InlineData(Key.Down, ModifierKeys.Windows)]
    [InlineData(Key.Home, ModifierKeys.Control)]
    [InlineData(Key.End, ModifierKeys.Alt)]
    [InlineData(Key.PageUp, ModifierKeys.Shift)]
    [InlineData(Key.PageDown, ModifierKeys.Control | ModifierKeys.Alt)]
    public void ShouldLeaveKeyDownForTextboxNavigation_ReturnsFalse_ForModifiedNavigationKeys(Key key, ModifierKeys modifiers)
    {
        bool shouldLeave = HotkeyCaptureBehavior.ShouldLeaveKeyDownForTextboxNavigation(key, modifiers);

        Assert.False(shouldLeave);
    }

    [Fact]
    public void ShouldLeaveKeyDownForTextboxNavigation_ReturnsTrue_ForCtrlA()
    {
        bool shouldLeave = HotkeyCaptureBehavior.ShouldLeaveKeyDownForTextboxNavigation(Key.A, ModifierKeys.Control);

        Assert.True(shouldLeave);
    }

    [Theory]
    [InlineData(120, "WheelRight")]
    [InlineData(-120, "WheelLeft")]
    [InlineData(0, "")]
    public void ResolveHorizontalWheelInput_UsesWindowsDeltaDirection(short delta, string expectedToken)
    {
        long packedWParam = unchecked((long)(ushort)delta << 16);

        HotkeyMainInput input = HotkeyCaptureBehavior.ResolveHorizontalWheelInput(new IntPtr(packedWParam));

        Assert.Equal(expectedToken, input.SerializationToken);
    }

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void ShouldLeaveKeyDownForTextboxNavigation_ReturnsFalse_ForNonCtrlACombinations(ModifierKeys modifiers)
    {
        bool shouldLeave = HotkeyCaptureBehavior.ShouldLeaveKeyDownForTextboxNavigation(Key.A, modifiers);

        Assert.False(shouldLeave);
    }

    [Fact]
    public void HotkeyMainInput_RejectsStandaloneTextKeyWithoutModifier()
    {
        bool supported = HotkeyMainInput.FromKeyboard(Key.A).IsSupportedModifierCount(0);

        Assert.False(supported);
    }

    [Fact]
    public void HotkeyMainInput_AllowsStandaloneFunctionKeyWithoutModifier()
    {
        bool supported = HotkeyMainInput.FromKeyboard(Key.F8).IsSupportedModifierCount(0);

        Assert.True(supported);
    }

    [Fact]
    public void HotkeyMainInput_AllowsStandaloneMediaKeyWithoutModifier()
    {
        bool supported = HotkeyMainInput.FromKeyboard(Key.MediaPlayPause).IsSupportedModifierCount(0);

        Assert.True(supported);
    }

    [Fact]
    public void HotkeyMainInput_RejectsStandaloneNavigationKeyWithoutModifierByDefault()
    {
        bool supported = HotkeyMainInput.FromKeyboard(Key.Home).IsSupportedModifierCount(0);

        Assert.False(supported);
    }

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    [InlineData(Key.PageUp)]
    [InlineData(Key.PageDown)]
    public void HotkeyMainInput_AllowsNavigationKeysWithModifier(Key key)
    {
        bool supported = HotkeyMainInput.FromKeyboard(key).IsSupportedModifierCount(1);

        Assert.True(supported);
    }

    [Theory]
    [InlineData(Key.Tab, ModifierKeys.Alt)]
    [InlineData(Key.Escape, ModifierKeys.Control)]
    [InlineData(Key.Delete, ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(Key.R, ModifierKeys.Windows)]
    [InlineData(Key.S, ModifierKeys.Windows | ModifierKeys.Shift)]
    [InlineData(Key.Space, ModifierKeys.Windows)]
    [InlineData(Key.P, ModifierKeys.Windows)]
    [InlineData(Key.F4, ModifierKeys.Alt)]
    [InlineData(Key.Space, ModifierKeys.Alt)]
    [InlineData(Key.K, ModifierKeys.Windows)]
    [InlineData(Key.S, ModifierKeys.Windows)]
    [InlineData(Key.A, ModifierKeys.Windows)]
    [InlineData(Key.V, ModifierKeys.Windows)]
    [InlineData(Key.G, ModifierKeys.Windows)]
    [InlineData(Key.Left, ModifierKeys.Windows)]
    [InlineData(Key.Right, ModifierKeys.Windows | ModifierKeys.Shift)]
    [InlineData(Key.Up, ModifierKeys.Windows)]
    [InlineData(Key.Down, ModifierKeys.Windows | ModifierKeys.Control)]
    [InlineData(Key.Tab, ModifierKeys.Windows)]
    public void HotkeyViewModel_RejectsReservedWindowsShortcutsInCaptureSupport(Key key, ModifierKeys modifiers)
    {
        var target = new HotkeyViewModel();
        List<Key> activeModifiers = [];

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            activeModifiers.Add(Key.LeftCtrl);
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            activeModifiers.Add(Key.LeftAlt);
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            activeModifiers.Add(Key.LeftShift);
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            activeModifiers.Add(Key.LWin);
        }

        bool supported = target.IsSupportedHotkey(HotkeyMainInput.FromKeyboard(key), activeModifiers);

        Assert.False(supported);
    }

    [Theory]
    [InlineData(Key.E, ModifierKeys.Control | ModifierKeys.Windows)]
    [InlineData(Key.R, ModifierKeys.Shift | ModifierKeys.Windows)]
    [InlineData(Key.X, ModifierKeys.Alt | ModifierKeys.Windows)]
    [InlineData(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows)]
    public void HotkeyViewModel_AllowsModifiedWinShortcutsOutsideReservedSetInCaptureSupport(Key key, ModifierKeys modifiers)
    {
        var target = new HotkeyViewModel();
        List<Key> activeModifiers = [];

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            activeModifiers.Add(Key.LeftCtrl);
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            activeModifiers.Add(Key.LeftAlt);
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            activeModifiers.Add(Key.LeftShift);
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            activeModifiers.Add(Key.LWin);
        }

        bool supported = target.IsSupportedHotkey(HotkeyMainInput.FromKeyboard(key), activeModifiers);

        Assert.True(supported);
    }
}

using System.Windows.Input;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Hotkeys;

public sealed class HotkeyNormalizationTests
{
    [Fact]
    public void CoreHotkeyKeyMatchesWpfKeyNamesAndValues()
    {
        string[] wpfNames = Enum.GetNames<Key>();
        string[] coreNames = Enum.GetNames<HotkeyKey>();

        Assert.Equal(wpfNames, coreNames);
        foreach (string name in wpfNames)
        {
            Assert.Equal((int)Enum.Parse<Key>(name), (int)Enum.Parse<HotkeyKey>(name));
        }
    }

    [Theory]
    [InlineData("Ctrl + Alt + O", "Ctrl+Alt+O")]
    [InlineData("Alt+Ctrl+O", "Alt+Ctrl+O")]
    [InlineData("RightCtrl+LeftCtrl+O", "Ctrl+O")]
    [InlineData("Control+Alt+O", "Ctrl+Alt+O")]
    [InlineData("Ctrl+Alt++", "Ctrl+Alt++")]
    [InlineData("Alt+MouseX1", "Alt+MouseX1")]
    [InlineData("Ctrl+WheelUp", "Ctrl+WheelUp")]
    [InlineData("Insert", "Insert")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Ctrl+NotAKey", null)]
    [InlineData("Win+L", null)]
    [InlineData("Ctrl+Alt+Delete", null)]
    public void NormalizationPreservesSerializationAndRejectsUnavailableBindings(string? input, string? expected)
    {
        bool normalized = HotkeyParsingService.TryNormalizeHotkey(input, out string actual);

        Assert.Equal(expected != null, normalized);
        Assert.Equal(expected ?? string.Empty, actual);
    }
}

using System.Windows.Input;

namespace AudioPilot.Tests.Services.Hotkeys;

public sealed class KeyboardLayoutKeyNameResolverTests
{
    [Theory]
    [InlineData(Key.OemPlus, '=', "=")]
    [InlineData(Key.OemMinus, '-', "-")]
    [InlineData(Key.Oem102, '§', "§")]
    [InlineData(Key.D1, '&', "&")]
    [InlineData(Key.Decimal, ',', "Num ,")]
    public void Resolve_UsesUnshiftedCharacterFromSelectedLayout(Key key, char character, string expected)
    {
        var keyboardLayout = new IntPtr(42);

        string result = KeyboardLayoutKeyNameResolver.Resolve(
            key,
            "fallback",
            keyboardLayout,
            (virtualKey, mapType, layout) =>
            {
                Assert.NotEqual(0u, virtualKey);
                Assert.Equal(2u, mapType);
                Assert.Equal(keyboardLayout, layout);
                return character;
            });

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_PreservesDeadKeyCharacterWithoutMutatingKeyboardState()
    {
        string result = KeyboardLayoutKeyNameResolver.Resolve(
            Key.OemTilde,
            "Backtick",
            new IntPtr(1),
            static (_, _, _) => 0x8000005E);

        Assert.Equal("^", result);
    }

    [Theory]
    [InlineData(Key.OemPlus, "Equals/Plus")]
    [InlineData(Key.Oem102, "International Key")]
    [InlineData(Key.AbntC1, "ABNT C1")]
    public void Resolve_UsesDescriptiveFallbackWhenLayoutHasNoCharacter(Key key, string expected)
    {
        string result = KeyboardLayoutKeyNameResolver.Resolve(
            key,
            "raw-token",
            new IntPtr(1),
            static (_, _, _) => 0);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_RejectsInvisibleLayoutCharacters()
    {
        string result = KeyboardLayoutKeyNameResolver.Resolve(
            Key.OemPlus,
            "raw-token",
            new IntPtr(1),
            static (_, _, _) => ' ');

        Assert.Equal("Equals/Plus", result);
    }

    [Fact]
    public void Resolve_DoesNotCallNativeMappingForLayoutIndependentKey()
    {
        string result = KeyboardLayoutKeyNameResolver.Resolve(
            Key.F8,
            "F8",
            new IntPtr(1),
            static (_, _, _) => throw new InvalidOperationException("Mapping should not be called."));

        Assert.Equal("F8", result);
    }
}

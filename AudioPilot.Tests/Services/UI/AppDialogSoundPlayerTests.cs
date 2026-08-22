using System.Media;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppDialogSoundPlayerTests
{
    [Fact]
    public void Play_MapsEveryDialogKindToItsWindowsSystemSound()
    {
        SystemSound? played = null;
        var player = new WindowsAppDialogSoundPlayer(sound => played = sound);
        (AppDialogKind Kind, SystemSound Expected)[] cases =
        [
            (AppDialogKind.Information, SystemSounds.Asterisk),
            (AppDialogKind.Success, SystemSounds.Asterisk),
            (AppDialogKind.Warning, SystemSounds.Exclamation),
            (AppDialogKind.Error, SystemSounds.Hand),
            (AppDialogKind.Question, SystemSounds.Question),
        ];

        foreach ((AppDialogKind kind, SystemSound expected) in cases)
        {
            played = null;
            player.Play(kind);
            Assert.Same(expected, played);
        }
    }
}

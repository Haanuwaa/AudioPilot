using System.Windows;
using AudioPilot.Services.UI;

namespace AudioPilot.Updater.Ui;

internal sealed record UpdateConsent(string InstalledVersion, string Version, long Bytes, bool PackageReady)
{
    internal AppDialogRequest CreateDialog(Window owner) => new(
        $"Update AudioPilot to {Version} and restart?\n\nInstalled: {InstalledVersion}\n{(PackageReady ? "Package verified" : $"Download: {Bytes / 1048576d:0.0} MB")}\n\n" +
            (PackageReady ? "" : "AudioPilot stays running while the update downloads and is verified. Then it will install and restart.\n\n") +
            "Saved settings and installation options will be kept. Pending autosave must finish, and manually unsaved changes must be resolved first. Active microphone control may delay the update.",
        "Update and restart", AppDialogKind.Question,
        [new("Update and restart", AppDialogResult.Confirmed, AppDialogActionStyle.Primary),
         new("Later", AppDialogResult.Cancelled, isDefault: true, isCancel: true)], owner);
}

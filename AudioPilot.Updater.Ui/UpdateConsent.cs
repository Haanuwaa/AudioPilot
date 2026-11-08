using System.Windows;
using AudioPilot.Services.UI;

namespace AudioPilot.Updater.Ui;

internal enum UpdateConsentKind { Download, Install }
internal sealed record UpdateConsent(UpdateConsentKind Kind, string InstalledVersion, string Version, long Bytes)
{
    internal AppDialogRequest CreateDialog(Window owner) => new(
        Kind == UpdateConsentKind.Download
            ? $"Download AudioPilot {Version}?\n\nInstalled: {InstalledVersion}\nDownload: {Bytes / 1048576d:0.0} MB\n\nDownloading does not install the update or close AudioPilot."
            : $"Install AudioPilot {Version} and restart?\n\nSaved settings and installation options will be kept. Pending autosave must finish, and manually unsaved changes must be resolved first. Active microphone control may delay the update.",
        Kind == UpdateConsentKind.Download ? "Download update" : "Install update", AppDialogKind.Question,
        [new(Kind == UpdateConsentKind.Download ? "Download update" : "Install and restart", AppDialogResult.Confirmed, AppDialogActionStyle.Primary),
         new("Later", AppDialogResult.Cancelled, isDefault: true, isCancel: true)], owner);
}

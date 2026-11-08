using System.Windows;
using AudioPilot.Localization;
using AudioPilot.Services.UI;

namespace AudioPilot.Updater.Ui;

internal sealed record UpdateConsent(string InstalledVersion, string Version, long Bytes, bool PackageReady)
{
    internal AppDialogRequest CreateDialog(Window owner)
    {
        var localization = LocalizationContext.GetService(owner);
        LocalizedText package = PackageReady ? new("Update_ConsentVerified") : new("Update_ConsentDownload", Bytes / 1048576d);
        LocalizedText message = new("Update_Consent", Version, InstalledVersion, package,
            PackageReady ? string.Empty : new LocalizedText("Update_ConsentTransfer"));
        LocalizedText caption = new("Update_ConsentTitle");
        return new(localization.Format(message), localization.Format(caption), AppDialogKind.Question,
        [
            new(localization.Format(caption), AppDialogResult.Confirmed, AppDialogActionStyle.Primary) { LabelText = caption },
            new(localization["Update_Later"], AppDialogResult.Cancelled, isDefault: true, isCancel: true) { LabelText = new("Update_Later") },
        ], owner)
        { MessageResource = message, CaptionResource = caption };
    }
}

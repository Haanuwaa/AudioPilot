using System.Windows;
using AudioPilot.Localization;
using AudioPilot.Services.UI;

namespace AudioPilot.Updater.Ui;

internal sealed record UpdateConsent(string InstalledVersion, string Version, long Bytes, bool PackageReady, long? DeltaBytes = null)
{
    internal AppDialogRequest CreateDialog(Window owner)
    {
        var localization = LocalizationContext.GetService(owner);
        double fullSize = Bytes / 1048576d;
        LocalizedText package = PackageReady ? new("Update_ConsentVerified")
            : DeltaBytes is long delta ? new("Update_ConsentDeltaDownload", delta / 1048576d, fullSize)
            : new("Update_ConsentDownload", fullSize);
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

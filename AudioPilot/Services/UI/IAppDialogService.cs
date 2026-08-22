using System.Windows;

namespace AudioPilot.Services.UI
{
    internal interface IAppDialogService : IAsyncDisposable
    {
        void SetSoundsEnabled(bool enabled);

        Task<AppDialogResult> ShowAsync(AppDialogRequest request, CancellationToken cancellationToken = default);

        Task<AppDialogResult> ShowInformationAsync(string message, string caption = DialogText.Captions.Information, Window? owner = null, CancellationToken cancellationToken = default);
        Task<AppDialogResult> ShowSuccessAsync(string message, string caption = DialogText.Captions.Success, Window? owner = null, CancellationToken cancellationToken = default);
        Task<AppDialogResult> ShowWarningAsync(string message, string caption = DialogText.Captions.Warning, Window? owner = null, CancellationToken cancellationToken = default);
        Task<AppDialogResult> ShowErrorAsync(string message, string caption = DialogText.Captions.Error, Window? owner = null, CancellationToken cancellationToken = default);
        Task<bool> ConfirmAsync(
            string message,
            string caption,
            AppDialogKind kind,
            string confirmLabel,
            string declineLabel,
            AppDialogActionStyle confirmStyle = AppDialogActionStyle.Primary,
            Window? owner = null,
            CancellationToken cancellationToken = default);
    }
}

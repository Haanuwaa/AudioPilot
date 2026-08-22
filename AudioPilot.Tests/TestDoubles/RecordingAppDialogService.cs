using System.Windows;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class RecordingAppDialogService : IAppDialogService
{
    public AppDialogResult YesNoResponse { get; set; } = AppDialogResult.Declined;
    public AppDialogResult DefaultResponse { get; set; } = AppDialogResult.Acknowledged;
    public int ShowCallCount { get; private set; }
    public List<AppDialogRequest> Requests { get; } = [];
    public List<(string message, string caption)> YesNoMessages { get; } = [];
    public List<(string message, string caption)> SuccessMessages { get; } = [];
    public List<(string message, string caption)> WarningMessages { get; } = [];
    public List<(string message, string caption)> ErrorMessages { get; } = [];
    public List<(string message, string caption)> InformationMessages { get; } = [];
    public bool SoundsEnabled { get; private set; } = true;

    public void SetSoundsEnabled(bool enabled) => SoundsEnabled = enabled;

    public Task<AppDialogResult> ShowAsync(AppDialogRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(AppDialogResult.Cancelled);
        }

        ShowCallCount++;
        Requests.Add(request);
        if (!request.IsAcknowledgement)
        {
            YesNoMessages.Add((request.Message, request.Caption));
            return Task.FromResult(YesNoResponse);
        }

        RecordAcknowledgement(request.Message, request.Caption, request.Kind);
        return Task.FromResult(DefaultResponse);
    }

    public Task<AppDialogResult> ShowInformationAsync(string message, string caption = DialogText.Captions.Information, Window? owner = null, CancellationToken cancellationToken = default)
        => ShowAsync(AppDialogRequest.Acknowledge(message, caption, AppDialogKind.Information, owner), cancellationToken);

    public Task<AppDialogResult> ShowSuccessAsync(string message, string caption = DialogText.Captions.Success, Window? owner = null, CancellationToken cancellationToken = default)
        => ShowAsync(AppDialogRequest.Acknowledge(message, caption, AppDialogKind.Success, owner), cancellationToken);

    public Task<AppDialogResult> ShowWarningAsync(string message, string caption = DialogText.Captions.Warning, Window? owner = null, CancellationToken cancellationToken = default)
        => ShowAsync(AppDialogRequest.Acknowledge(message, caption, AppDialogKind.Warning, owner), cancellationToken);

    public Task<AppDialogResult> ShowErrorAsync(string message, string caption = DialogText.Captions.Error, Window? owner = null, CancellationToken cancellationToken = default)
        => ShowAsync(AppDialogRequest.Acknowledge(message, caption, AppDialogKind.Error, owner), cancellationToken);

    public async Task<bool> ConfirmAsync(
        string message,
        string caption,
        AppDialogKind kind,
        string confirmLabel,
        string declineLabel,
        AppDialogActionStyle confirmStyle = AppDialogActionStyle.Primary,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        AppDialogResult result = await ShowAsync(
            AppDialogRequest.Confirm(message, caption, kind, confirmLabel, declineLabel, confirmStyle, owner),
            cancellationToken);
        return result == AppDialogResult.Confirmed;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void RecordAcknowledgement(string message, string caption, AppDialogKind kind)
    {
        switch (kind)
        {
            case AppDialogKind.Success:
                SuccessMessages.Add((message, caption));
                break;
            case AppDialogKind.Warning:
                WarningMessages.Add((message, caption));
                break;
            case AppDialogKind.Error:
                ErrorMessages.Add((message, caption));
                break;
            default:
                InformationMessages.Add((message, caption));
                break;
        }
    }
}

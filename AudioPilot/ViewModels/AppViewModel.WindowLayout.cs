using System.Windows.Input;
using AudioPilot.Helpers;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    public ICommand ResetWindowLayoutCommand { get; private set; } = null!;

    private void InitializeWindowLayoutCommand()
    {
        ResetWindowLayoutCommand = TrackCommand(new RelayCommand(async () =>
        {
            if (_isCleaningUp || ShutdownToken.IsCancellationRequested) return;
            var result = await _shell.ResetWindowLayoutAsync(ShutdownToken);
            if (!result.Persisted && !_isCleaningUp && !ShutdownToken.IsCancellationRequested)
                await _dialogs.ShowWarningAsync("Window size and position changed, but could not be saved. Try again.", "Window layout", cancellationToken: ShutdownToken);
        }, () => !_isCleaningUp, ex => HandleAsyncCommandException("reset-window-layout", ex)));
    }
}

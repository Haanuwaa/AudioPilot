using System.Windows;

namespace AudioPilot.Services.UI
{
    internal interface INativeAppDialogFallback
    {
        AppDialogResult Show(AppDialogRequest request, string reason);
    }

    internal sealed class NativeAppDialogFallback(Logging.Logger logger) : INativeAppDialogFallback
    {
        private readonly Logging.Logger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public AppDialogResult Show(AppDialogRequest request, string reason)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (Application.Current?.Dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false } dispatcher
                && !dispatcher.CheckAccess())
            {
                return dispatcher.Invoke(() => Show(request, reason));
            }

            Window? owner = AppDialogWindowPresenter.ResolveOwnerForCurrentApplication(request.Owner);
            string ownership = owner == null
                ? "standalone"
                : ReferenceEquals(owner, request.Owner) ? "explicit" : "automatic";
            _logger.Warning(
                "AppDialog",
                () => $"native-fallback | reason={reason} kind={request.Kind} actions={request.Actions.Count} owner={ownership}");

            MessageBoxButton buttons = GetButtons(request);
            MessageBoxImage icon = request.Kind switch
            {
                AppDialogKind.Warning => MessageBoxImage.Warning,
                AppDialogKind.Error => MessageBoxImage.Error,
                AppDialogKind.Question => MessageBoxImage.Question,
                _ => MessageBoxImage.Information,
            };

            MessageBoxResult result = owner == null
                ? MessageBox.Show(request.Message, request.Caption, buttons, icon)
                : MessageBox.Show(owner, request.Message, request.Caption, buttons, icon);
            return MapResult(request, result);
        }

        internal static MessageBoxButton GetButtons(AppDialogRequest request)
        {
            if (request.IsAcknowledgement)
            {
                return MessageBoxButton.OK;
            }

            return request.Actions.Count > 2 ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo;
        }

        internal static AppDialogResult MapResult(AppDialogRequest request, MessageBoxResult result)
        {
            return result switch
            {
                MessageBoxResult.OK => request.Actions[0].Result,
                MessageBoxResult.Yes => request.Actions[0].Result,
                MessageBoxResult.No when request.Actions.Count > 1 => request.Actions[1].Result,
                MessageBoxResult.Cancel when request.Actions.Count > 2 => request.Actions[2].Result,
                _ => request.SafeCloseResult,
            };
        }
    }
}

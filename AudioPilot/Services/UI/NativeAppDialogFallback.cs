using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioPilot.Localization;

namespace AudioPilot.Services.UI
{
    internal interface INativeAppDialogFallback
    {
        AppDialogResult Show(AppDialogRequest request, string reason);
    }

    internal sealed partial class NativeAppDialogFallback(
        Logging.Logger logger,
        Func<bool>? soundsEnabled = null,
        Func<NativeAppDialogFallback.MessageBoxParameters, int>? showMessageBox = null) : INativeAppDialogFallback
    {
        internal static void ShowStartupError(string message) =>
            MessageBox.Show(AppMessages.Translate(message, LocalizationService.Default),
                LocalizationService.Default["Dialog_StartupError"], MessageBoxButton.OK, MessageBoxImage.Error);

        private readonly Logging.Logger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        private readonly Func<bool> _soundsEnabled = soundsEnabled ?? (static () => true);
        private readonly Func<MessageBoxParameters, int> _showMessageBox = showMessageBox ?? (static parameters => MessageBoxIndirect(ref parameters));
        private const MessageBoxButton RetryCancelButtons = (MessageBoxButton)5;
        private const int RetryResult = 4;

        [StructLayout(LayoutKind.Sequential)]
        internal struct MessageBoxParameters
        {
            public uint Size;
            public nint Owner;
            public nint Instance;
            public nint Text;
            public nint Caption;
            public uint Style;
            public nint Icon;
            public nuint ContextHelpId;
            public nint Callback;
            public uint LanguageId;
        }

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxIndirectW", SetLastError = true)]
        private static partial int MessageBoxIndirect(ref MessageBoxParameters parameters);

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

            return ShowMessageBox(request.Localize(owner == null
                ? AudioPilot.Localization.LocalizationService.Default
                : AudioPilot.Localization.LocalizationContext.GetService(owner)), owner);
        }

        private unsafe AppDialogResult ShowMessageBox(AppDialogRequest request, Window? owner)
        {
            bool playSound = _soundsEnabled();
            uint icon = request.Kind switch
            {
                AppDialogKind.Warning => 32515,
                AppDialogKind.Error => 32513,
                AppDialogKind.Question => 32514,
                _ => 32516,
            };

            MessageBoxButton buttons = GetButtons(request);
            string message = buttons == MessageBoxButton.OK && !request.IsAcknowledgement
                ? $"{request.FullMessage}\n\nThe action dialog could not be opened. Click OK to dismiss this message."
                : request.FullMessage;
            fixed (char* text = message, caption = request.Caption)
            {
                var parameters = new MessageBoxParameters
                {
                    Size = (uint)sizeof(MessageBoxParameters),
                    Owner = owner == null ? 0 : new WindowInteropHelper(owner).Handle,
                    Text = (nint)text,
                    Caption = (nint)caption,
                    Style = GetStyle(request, buttons, playSound),
                    Icon = (nint)icon,
                };
                int result = _showMessageBox(parameters);
                if (result == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (buttons == MessageBoxButton.YesNo)
                {
                    return (MessageBoxResult)result == MessageBoxResult.Yes ? AppDialogResult.Confirmed : request.Actions[1].Result;
                }
                if (buttons == RetryCancelButtons && result == RetryResult) return AppDialogResult.Retry;

                return request.IsAcknowledgement ? AppDialogResult.Acknowledged : AppDialogResult.Cancelled;
            }
        }

        /// <summary>MB_USERICON keeps the system icon without Windows' automatic alert sound.</summary>
        private static uint GetStyle(AppDialogRequest request, MessageBoxButton buttons, bool playSound)
        {
            uint icon = playSound ? request.Kind switch
            {
                AppDialogKind.Warning => (uint)MessageBoxImage.Warning,
                AppDialogKind.Error => (uint)MessageBoxImage.Error,
                AppDialogKind.Question => (uint)MessageBoxImage.Question,
                _ => (uint)MessageBoxImage.Information,
            } : 0x80u;
            uint defaultButton = buttons is MessageBoxButton.YesNo or RetryCancelButtons && request.Actions[1].IsDefault ? 0x100u : 0u;
            return (uint)buttons | icon | defaultButton;
        }

        private static MessageBoxButton GetButtons(AppDialogRequest request) => request.Actions switch
        {
            [{ Result: AppDialogResult.Confirmed }, { Result: AppDialogResult.Declined or AppDialogResult.Cancelled }] => MessageBoxButton.YesNo,
            [{ Result: AppDialogResult.Retry }, { Result: AppDialogResult.Cancelled }] => RetryCancelButtons,
            _ => MessageBoxButton.OK,
        };
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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
        private readonly Logging.Logger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        private readonly Func<bool> _soundsEnabled = soundsEnabled ?? (static () => true);
        private readonly Func<MessageBoxParameters, int> _showMessageBox = showMessageBox ?? (static parameters => MessageBoxIndirect(ref parameters));

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

            return ShowMessageBox(request, owner);
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

            fixed (char* text = request.Message, caption = request.Caption)
            {
                var parameters = new MessageBoxParameters
                {
                    Size = (uint)sizeof(MessageBoxParameters),
                    Owner = owner == null ? 0 : new WindowInteropHelper(owner).Handle,
                    Text = (nint)text,
                    Caption = (nint)caption,
                    Style = GetStyle(request, playSound),
                    Icon = (nint)icon,
                };
                int result = _showMessageBox(parameters);
                if (result == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                return MapResult(request, (MessageBoxResult)result);
            }
        }

        /// <summary>MB_USERICON keeps the system icon without Windows' automatic alert sound.</summary>
        internal static uint GetStyle(AppDialogRequest request, bool playSound)
        {
            uint icon = playSound ? request.Kind switch
            {
                AppDialogKind.Warning => (uint)MessageBoxImage.Warning,
                AppDialogKind.Error => (uint)MessageBoxImage.Error,
                AppDialogKind.Question => (uint)MessageBoxImage.Question,
                _ => (uint)MessageBoxImage.Information,
            } : 0x80u;
            int defaultIndex = 0;
            while (defaultIndex < request.Actions.Count - 1 && !request.Actions[defaultIndex].IsDefault) defaultIndex++;
            return (uint)GetButtons(request) | icon | ((uint)Math.Clamp(defaultIndex, 0, 2) << 8);
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

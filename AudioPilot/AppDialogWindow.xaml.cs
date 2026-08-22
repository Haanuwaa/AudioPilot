using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioPilot.Helpers;

namespace AudioPilot
{
    public partial class AppDialogWindow : Window
    {
        private static readonly Geometry InformationIcon = Geometry.Parse("M10,1 A9,9 0 1 1 9.99,1 M9,5 H11 V7 H9 Z M9,9 H11 V15 H9 Z");
        private static readonly Geometry SuccessIcon = Geometry.Parse("M2,10 L7,15 L18,4 L16,2 L7,11 L4,8 Z");
        private static readonly Geometry WarningIcon = Geometry.Parse("M10,1 L19,18 H1 Z M9,7 H11 V12 H9 Z M9,14 H11 V16 H9 Z");
        private static readonly Geometry ErrorIcon = Geometry.Parse("M10,1 A9,9 0 1 1 9.99,1 M6,6 L14,14 M14,6 L6,14");
        private static readonly Geometry QuestionIcon = Geometry.Parse("M10,1 A9,9 0 1 1 9.99,1 M7,7 A3,3 0 0 1 13,7 C13,9 10,9 10,12 M9,14 H11 V16 H9 Z");

        private bool _allowClose;
        private bool _firstPresentationRaised;
        private IInputElement? _previousFocus;

        internal AppDialogWindow(AppDialogRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Result = request.SafeCloseResult;
            InitializeComponent();
            ApplyRequest(request, repetitionCount: 1);
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        internal AppDialogRequest Request { get; private set; }
        internal AppDialogResult Result { get; private set; }
        internal event Action<AppDialogResult>? Completed;
        internal event Action? FirstPresented;

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (_firstPresentationRaised)
            {
                return;
            }

            _firstPresentationRaised = true;
            FirstPresented?.Invoke();
        }

        internal void UpdateAcknowledgement(AppDialogRequest request, int repetitionCount)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueDispatcherUpdate(() => UpdateAcknowledgement(request, repetitionCount));
                return;
            }

            bool restoreActionFocus = ActionsPanel.IsKeyboardFocusWithin;
            Request = request;
            ApplyRequest(request, repetitionCount);
            RaiseLiveRegionChanged(MessageText);
            if (RepeatText.Visibility == Visibility.Visible)
            {
                RaiseLiveRegionChanged(RepeatText);
            }

            if (restoreActionFocus)
            {
                FocusDefaultAction();
            }
        }

        internal void Complete(AppDialogResult result)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueDispatcherUpdate(() => Complete(result));
                return;
            }

            if (_allowClose)
            {
                return;
            }

            Result = result;
            _allowClose = true;
            Completed?.Invoke(result);
            Close();
        }

        private void QueueDispatcherUpdate(Action update)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                _ = ObserveDispatcherUpdateAsync(Dispatcher.InvokeAsync(update).Task);
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
            }
        }

        private static async Task ObserveDispatcherUpdateAsync(Task dispatcherTask)
        {
            try
            {
                await dispatcherTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                try
                {
                    Logging.Logger.Instance.Warning(
                        "AppDialogWindow",
                        () => $"dialog-dispatch-update-failed | exceptionType={ex.GetType().Name}");
                }
                catch (Exception)
                {
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                Result = Request.SafeCloseResult;
                _allowClose = true;
                Completed?.Invoke(Result);
            }

            base.OnClosing(e);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Complete(Request.SafeCloseResult);
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        private void ApplyRequest(AppDialogRequest request, int repetitionCount)
        {
            Title = request.Caption;
            CaptionText.Text = request.Caption;
            MessageText.Text = request.Message;
            AutomationProperties.SetName(this, $"{request.Kind} dialog: {request.Caption}");
            AutomationProperties.SetName(MessageText, request.Message);
            AutomationProperties.SetHelpText(MessageText, request.AutomationHelpText ?? string.Empty);
            KindIcon.Data = GetIcon(request.Kind);
            KindIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, GetKindBrushKey(request.Kind));
            CopyButton.Visibility = request.AllowCopy ? Visibility.Visible : Visibility.Collapsed;
            CopyStatusText.Visibility = Visibility.Collapsed;
            RepeatText.Text = repetitionCount > 1 ? $"Repeated {repetitionCount} times" : string.Empty;
            RepeatText.Visibility = repetitionCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            BuildActions(request.Actions);
        }

        private void BuildActions(IEnumerable<AppDialogAction> actions)
        {
            ActionsPanel.Children.Clear();
            foreach (AppDialogAction action in actions)
            {
                var button = new Button
                {
                    Content = action.Label,
                    IsDefault = action.IsDefault,
                    IsCancel = false,
                    Tag = action.Result,
                    Style = (Style)FindResource(action.Style switch
                    {
                        AppDialogActionStyle.Primary => "DialogPrimaryButtonStyle",
                        AppDialogActionStyle.Destructive => "DialogDestructiveButtonStyle",
                        _ => "DialogActionButtonStyle",
                    }),
                };
                AutomationProperties.SetName(button, action.Label.Replace("_", string.Empty, StringComparison.Ordinal));
                button.Click += ActionButton_Click;
                ActionsPanel.Children.Add(button);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _previousFocus = Owner == null ? null : FocusManager.GetFocusedElement(Owner);
            FocusDefaultAction();
            Activate();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_previousFocus is UIElement element && element.IsVisible && element.IsEnabled)
            {
                _ = element.Focus();
            }
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: AppDialogResult result })
            {
                Complete(result);
            }
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(MessageText.Text);
                CopyStatusText.Text = "Message copied.";
                CopyStatusText.SetResourceReference(TextBlock.ForegroundProperty, "DialogSuccessBrush");
            }
            catch (Exception)
            {
                CopyStatusText.Text = "The message could not be copied.";
                CopyStatusText.SetResourceReference(TextBlock.ForegroundProperty, "DialogErrorBrush");
            }

            CopyStatusText.Visibility = Visibility.Visible;
            RaiseLiveRegionChanged(CopyStatusText);
        }

        private void FocusDefaultAction()
        {
            Button? defaultButton = ActionsPanel.Children.OfType<Button>().FirstOrDefault(static button => button.IsDefault);
            _ = defaultButton?.Focus();
        }

        private static void RaiseLiveRegionChanged(UIElement element)
        {
            UIElementAutomationPeer? peer = (UIElementAutomationPeer?)(UIElementAutomationPeer.FromElement(element)
                ?? UIElementAutomationPeer.CreatePeerForElement(element));
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private static Geometry GetIcon(AppDialogKind kind) => kind switch
        {
            AppDialogKind.Success => SuccessIcon,
            AppDialogKind.Warning => WarningIcon,
            AppDialogKind.Error => ErrorIcon,
            AppDialogKind.Question => QuestionIcon,
            _ => InformationIcon,
        };

        private static string GetKindBrushKey(AppDialogKind kind) => kind switch
        {
            AppDialogKind.Success => "DialogSuccessBrush",
            AppDialogKind.Warning => "DialogWarningBrush",
            AppDialogKind.Error => "DialogErrorBrush",
            AppDialogKind.Question => "DialogQuestionBrush",
            _ => "DialogInformationBrush",
        };
    }
}

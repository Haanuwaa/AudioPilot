using System.Collections.ObjectModel;
using System.Windows;
using AudioPilot.Localization;

namespace AudioPilot.Services.UI
{
    internal enum AppDialogKind
    {
        Information,
        Success,
        Warning,
        Error,
        Question,
    }

    internal enum AppDialogResult
    {
        Acknowledged,
        Confirmed,
        Declined,
        Cancelled,
        Retry,
        TerminateExisting,
    }

    internal enum AppDialogActionStyle
    {
        Primary,
        Secondary,
        Destructive,
    }

    internal sealed record AppDialogAction
    {
        public AppDialogAction(
            string label,
            AppDialogResult result,
            AppDialogActionStyle style = AppDialogActionStyle.Secondary,
            bool isDefault = false,
            bool isCancel = false)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException("A dialog action label is required.", nameof(label));
            }

            Label = label;
            Result = result;
            Style = style;
            IsDefault = isDefault;
            IsCancel = isCancel;
        }

        public string Label { get; }
        internal LocalizedText? LabelText { get; init; }
        public AppDialogResult Result { get; }
        public AppDialogActionStyle Style { get; }
        public bool IsDefault { get; }
        public bool IsCancel { get; }
    }

    internal sealed record AppDialogRequest
    {
        public AppDialogRequest(
            string message,
            string caption,
            AppDialogKind kind,
            IEnumerable<AppDialogAction> actions,
            Window? owner = null,
            bool allowCopy = false,
            string? automationHelpText = null,
            string? summary = null)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("Dialog content is required.", nameof(message));
            }

            if (string.IsNullOrWhiteSpace(caption))
            {
                throw new ArgumentException("A dialog caption is required.", nameof(caption));
            }

            ArgumentNullException.ThrowIfNull(actions);
            AppDialogAction[] validatedActions = [.. actions];
            if (validatedActions.Length == 0)
            {
                throw new ArgumentException("At least one dialog action is required.", nameof(actions));
            }

            if (validatedActions.Select(static action => action.Result).Distinct().Count() != validatedActions.Length)
            {
                throw new ArgumentException("Dialog action results must be unique.", nameof(actions));
            }

            if (validatedActions.Count(static action => action.IsDefault) != 1)
            {
                throw new ArgumentException("Exactly one dialog action must be the default.", nameof(actions));
            }

            if (validatedActions.Count(static action => action.IsCancel) > 1)
            {
                throw new ArgumentException("At most one dialog action may be the cancel action.", nameof(actions));
            }

            Message = message;
            Caption = caption;
            Kind = kind;
            Actions = new ReadOnlyCollection<AppDialogAction>(validatedActions);
            Owner = owner;
            AllowCopy = allowCopy || kind == AppDialogKind.Error;
            AutomationHelpText = automationHelpText;
            Summary = summary;
        }

        public string Message { get; }
        internal LocalizedText? MessageResource { get; init; }
        internal Func<LocalizationService, string>? MessagePresentation { get; init; }
        internal LocalizedText? CaptionResource { get; init; }
        internal LocalizedText? SummaryResource { get; init; }
        public string? Summary { get; }
        public string FullMessage => string.IsNullOrWhiteSpace(Summary) ? Message : $"{Summary}\n\n{Message}";
        public string Caption { get; }
        public AppDialogKind Kind { get; }
        public IReadOnlyList<AppDialogAction> Actions { get; }
        public Window? Owner { get; }
        public bool AllowCopy { get; }
        public string? AutomationHelpText { get; }

        public bool IsAcknowledgement => Actions.Count == 1 && Actions[0].Result == AppDialogResult.Acknowledged;

        internal AppDialogRequest Localize(LocalizationService localization) => new(
            MessagePresentation?.Invoke(localization)
                ?? (MessageResource is { } message ? localization.Format(message) : AppMessages.Translate(Message, localization)),
            CaptionResource is { } caption ? localization.Format(caption) : DialogText.LocalizeLabel(Caption, localization),
            Kind,
            Actions.Select(action => new AppDialogAction(
                action.LabelText is { } label ? localization.Format(label) : DialogText.LocalizeLabel(action.Label, localization),
                action.Result, action.Style, action.IsDefault, action.IsCancel)),
            Owner, AllowCopy,
            AutomationHelpText == null ? null : AppMessages.Translate(AutomationHelpText, localization),
            SummaryResource is { } summary ? localization.Format(summary)
                : Summary == null ? null : AppMessages.Translate(Summary, localization));

        internal static AppDialogRequest ConfirmResetPerAppAudio()
        {
            var english = LocalizationService.English;
            return new AppDialogRequest(english["Dialog_ResetAudioMessage"], english["Dialog_ResetAudioCaption"], AppDialogKind.Warning,
            [
                new AppDialogAction(english["Dialog_ClearAssignments"], AppDialogResult.Confirmed, AppDialogActionStyle.Destructive)
                    { LabelText = new("Dialog_ClearAssignments") },
                new AppDialogAction(english["Dialog_Cancel"], AppDialogResult.Declined, isDefault: true, isCancel: true)
                    { LabelText = new("Dialog_Cancel") },
            ])
            {
                MessageResource = new("Dialog_ResetAudioMessage"),
                CaptionResource = new("Dialog_ResetAudioCaption"),
            };
        }

        public AppDialogResult SafeCloseResult =>
            Actions.FirstOrDefault(static action => action.IsCancel)?.Result
            ?? Actions.FirstOrDefault(static action => action.Result is AppDialogResult.Declined or AppDialogResult.Cancelled)?.Result
            ?? (IsAcknowledgement ? AppDialogResult.Acknowledged : AppDialogResult.Cancelled);

        public static AppDialogRequest Acknowledge(
            string message,
            string caption,
            AppDialogKind kind,
            Window? owner = null,
            bool allowCopy = false,
            string? summary = null)
        {
            return new AppDialogRequest(
                message,
                caption,
                kind,
                [new AppDialogAction("_OK", AppDialogResult.Acknowledged, AppDialogActionStyle.Primary, isDefault: true, isCancel: true)],
                owner,
                allowCopy,
                summary: summary);
        }

        internal static AppDialogRequest Acknowledge(LocalizedText message, string caption, AppDialogKind kind, Window? owner = null)
            => Acknowledge(LocalizationService.English.Format(message), caption, kind, owner) with { MessageResource = message };

        internal static AppDialogRequest Acknowledge(Func<LocalizationService, string> message, string caption, AppDialogKind kind)
            => Acknowledge(message(LocalizationService.English), caption, kind) with { MessagePresentation = message };

        public static AppDialogRequest Confirm(
            string message,
            string caption,
            AppDialogKind kind,
            string confirmLabel,
            string declineLabel,
            AppDialogActionStyle confirmStyle = AppDialogActionStyle.Primary,
            Window? owner = null)
        {
            return new AppDialogRequest(
                message,
                caption,
                kind,
                [
                    new AppDialogAction(confirmLabel, AppDialogResult.Confirmed, confirmStyle, isDefault: confirmStyle != AppDialogActionStyle.Destructive),
                    new AppDialogAction(declineLabel, AppDialogResult.Declined, AppDialogActionStyle.Secondary,
                        isDefault: confirmStyle == AppDialogActionStyle.Destructive, isCancel: true),
                ],
                owner);
        }
    }
}

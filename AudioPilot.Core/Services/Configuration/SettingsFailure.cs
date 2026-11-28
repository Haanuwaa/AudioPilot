using AudioPilot.Localization;

namespace AudioPilot.Services.Configuration;

internal static class SettingsFailure
{
    private static readonly object PresentationKey = new();

    // Keep the original exception type and message for logs and CLI callers.
    internal static T WithPresentation<T>(T error, string key, params object?[] arguments) where T : Exception
    {
        error.Data[PresentationKey] = new LocalizedText(key, arguments);
        return error;
    }

    internal static T WithPresentation<T>(T error, Func<LocalizationService, string> presentation) where T : Exception
    {
        error.Data[PresentationKey] = presentation;
        return error;
    }

    internal static Func<LocalizationService, string> ForDialog(Exception error, LocalizedText guidance, bool includeContext = false)
    {
        object? presentation = error.Data[PresentationKey];
        Func<LocalizationService, string> present = presentation is Func<LocalizationService, string> format ? format
            : language => language.Format(presentation as LocalizedText
                ?? new LocalizedText("Dialog_GuidanceAndDetails", guidance, error.Message));
        return includeContext && presentation != null
            ? language => language.Format(guidance) + "\n\n" + present(language) : present;
    }
}

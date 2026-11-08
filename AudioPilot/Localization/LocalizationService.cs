using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace AudioPilot.Localization;

/// <summary>A display message whose arguments can be rendered in the language current at presentation time.</summary>
public sealed record LocalizedText(string Key, params object?[] Arguments);

public sealed record LanguageOption(string Code, LocalizedText Label);

/// <summary>Resolves bundled translations without changing regional formatting or process-wide culture.</summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("AudioPilot.Localization.Strings", typeof(LocalizationService).Assembly);
    private readonly CultureInfo _systemCulture;
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    public static IReadOnlyList<LanguageOption> Languages { get; } = Array.AsReadOnly(
    [
        new LanguageOption("system", new("Language_System")),
        new LanguageOption("en", new("Language_English")),
        new LanguageOption("es", new("Language_Spanish")),
    ]);
    public static LocalizationService Default { get; } = new();
    internal static LocalizationService English { get; } = new("en");

    public LocalizationService(string language = "system", CultureInfo? systemCulture = null)
    {
        _systemCulture = systemCulture ?? CultureInfo.CurrentUICulture;
        SetLanguage(language);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Language { get; private set; } = string.Empty;
    public string EffectiveLanguage => _culture.Name;
    public int Revision { get; private set; }
    public string this[string key] => GetText(key, _culture);

    private static string GetText(string key, CultureInfo culture) => Resources.GetString(key, culture)
        ?? Resources.GetString(key, CultureInfo.InvariantCulture) ?? "Text unavailable.";

    public static string NormalizeLanguage(string? language)
    {
        string requested = language?.Trim() ?? string.Empty;
        if (Languages.FirstOrDefault(option => option.Code.Equals(requested, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact.Code;
        if (requested.Length == 0) return "system";
        try { return ResolveSupportedCulture(CultureInfo.GetCultureInfo(requested)) ?? "system"; }
        catch (CultureNotFoundException) { return "system"; }
    }

    private static string? ResolveSupportedCulture(CultureInfo culture)
    {
        for (CultureInfo current = culture; current.Name.Length != 0; current = current.Parent)
            if (Languages.FirstOrDefault(option => option.Code.Equals(current.Name, StringComparison.OrdinalIgnoreCase)) is { } match) return match.Code;
        return null;
    }

    public void SetLanguage(string? language)
    {
        string normalized = NormalizeLanguage(language);
        string effective = normalized == "system" ? ResolveSupportedCulture(_systemCulture) ?? "en" : normalized;
        if (Language == normalized && _culture.Name == effective) return;
        Language = normalized;
        _culture = CultureInfo.GetCultureInfo(effective);
        Revision++;
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        PropertyChanged?.Invoke(this, new(nameof(EffectiveLanguage)));
        PropertyChanged?.Invoke(this, new(nameof(Revision)));
        PropertyChanged?.Invoke(this, new("Item[]"));
    }

    public string Format(LocalizedText text) => Format(text, _culture);

    public string FormatKey(string key, params object?[] arguments) => Format(new LocalizedText(key, arguments));

    private static string Format(LocalizedText text, CultureInfo culture)
    {
        if (text.Arguments.Length == 0) return GetText(text.Key, culture);
        object?[] arguments = Array.ConvertAll(text.Arguments, argument => argument is LocalizedText nested ? Format(nested, culture) : argument);
        return string.Format(CultureInfo.CurrentCulture, GetText(text.Key, culture), arguments);
    }

    public string FormatValue(object? value) => value switch
    {
        LocalizedText text => Format(text),
        Func<LocalizationService, string> projection => projection(this),
        Enum option => Resources.GetString($"Enum_{option.GetType().Name}_{option}", _culture) ?? option.ToString(),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty,
    };
}

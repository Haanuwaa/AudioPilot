using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace AudioPilot.Localization;

/// <summary>Provides an inherited, replaceable localization scope for windows and isolated tests.</summary>
public static class LocalizationContext
{
    public static readonly DependencyProperty ServiceProperty = DependencyProperty.RegisterAttached(
        "Service", typeof(LocalizationService), typeof(LocalizationContext),
        new FrameworkPropertyMetadata(LocalizationService.Default, FrameworkPropertyMetadataOptions.Inherits));

    public static LocalizationService GetService(DependencyObject target) => (LocalizationService)target.GetValue(ServiceProperty);
    public static void SetService(DependencyObject target, LocalizationService value) => target.SetValue(ServiceProperty, value);

    public static void SetText(DependencyObject target, DependencyProperty property, LocalizedText text) =>
        BindingOperations.SetBinding(target, property, LocalizedBinding.Create(new Binding { Source = text }));

    internal static void SetMessage(DependencyObject target, DependencyProperty property, string message) =>
        BindingOperations.SetBinding(target, property, LocalizedBinding.Create(new Binding { Source = message }, message: true));

    internal static void SetPresentation(DependencyObject target, DependencyProperty property, Func<LocalizationService, string> format) =>
        BindingOperations.SetBinding(target, property, LocalizedBinding.Create(new Binding { Source = format }));
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        LocalizedBinding.Create(new Binding { Source = new LocalizedText(key) }).ProvideValue(serviceProvider);
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocBindingExtension(string path) : MarkupExtension
{
    public string? Format { get; set; }
    public bool Choice { get; set; }
    public bool Message { get; set; }
    public string? Observe { get; set; }
    public object? Source { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(path);
        if (Source != null) binding.Source = Source;
        return LocalizedBinding.Create(binding, Format, Choice, Observe, Message).ProvideValue(serviceProvider);
    }
}

internal sealed class LocalizedBinding : IMultiValueConverter
{
    private static readonly LocalizedBinding Converter = new();
    private static readonly object ChoiceParameter = new();
    private static readonly object MessageParameter = new();

    internal static MultiBinding Create(Binding text, string? format = null, bool choice = false, string? observe = null, bool message = false)
    {
        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = Converter, ConverterParameter = message ? MessageParameter : choice ? ChoiceParameter : format };
        binding.Bindings.Add(text);
        binding.Bindings.Add(new Binding { RelativeSource = RelativeSource.Self, Path = new PropertyPath("(0)", LocalizationContext.ServiceProperty) });
        binding.Bindings.Add(new Binding { RelativeSource = RelativeSource.Self, Path = new PropertyPath("(0).Revision", LocalizationContext.ServiceProperty) });
        if (observe != null) binding.Bindings.Add(new Binding(observe));
        return binding;
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not LocalizationService localization || values[0] == DependencyProperty.UnsetValue)
            return string.Empty;
        if (ReferenceEquals(parameter, ChoiceParameter) && values[0] is string choice)
            return RoutineChoiceLabels.Format(choice, localization);
        if (ReferenceEquals(parameter, MessageParameter) && values[0] is string message)
            return AppMessages.Translate(message, localization);
        string text = localization.FormatValue(values[0]);
        return parameter is string format ? localization.FormatKey(format, text) : text;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

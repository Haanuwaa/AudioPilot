using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AudioPilot.Localization;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Localization;

public sealed partial class LocalizationServiceTests
{
    private static readonly string[] LocalizedProjects = ["AudioPilot", "AudioPilot.Updater"];

    [Fact]
    public void BundledCatalogsHaveMatchingKeysAndFormatArguments_AndResolveThroughThePublishedResources()
    {
        string root = Path.Combine(TestRepository.Root, "AudioPilot", "Localization");
        var english = Read(Path.Combine(root, "Strings.resx"));
        Assert.Equal(english.Count, english.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var en = new LocalizationService("en");
        foreach (LanguageOption option in LocalizationService.Languages.Where(option => option.Code != "system"))
        {
            var translated = Read(Path.Combine(root, option.Code == "en" ? "Strings.resx" : $"Strings.{option.Code}.resx"));
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            var localization = new LocalizationService(option.Code);
            foreach (string key in english.Keys)
            {
                Assert.Equal(english[key], en[key]);
                Assert.Equal(translated[key], localization[key]);
                Assert.Equal(CompositeFormat.Parse(english[key]).MinimumArgumentCount, CompositeFormat.Parse(translated[key]).MinimumArgumentCount);
                object[] arguments = [.. Enumerable.Range(0, CompositeFormat.Parse(english[key]).MinimumArgumentCount).Select(index => (object)$"⟦argument-{index}⟧")];
                string original = string.Format(CultureInfo.InvariantCulture, english[key], arguments);
                string formatted = string.Format(CultureInfo.InvariantCulture, translated[key], arguments);
                foreach (string argument in arguments.Cast<string>().Where(argument => original.Contains(argument, StringComparison.Ordinal)))
                    Assert.Contains(argument, formatted, StringComparison.Ordinal);
                if (key != "Common_Empty") Assert.False(string.IsNullOrWhiteSpace(translated[key]), key);
            }
        }
        foreach (string path in LocalizedProjects.SelectMany(project => Directory.EnumerateFiles(Path.Combine(TestRepository.Root, project), "*.xaml", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            foreach (Match reference in MyRegex().Matches(File.ReadAllText(path)))
                Assert.True(english.ContainsKey(reference.Groups[1].Value), $"Unknown resource in {path}: {reference.Value}");

        static Dictionary<string, string> Read(string path) => XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(element => (string)element.Attribute("name")!, element => (string)element.Element("value")!);
    }

    [Theory]
    [InlineData("es-BO", "es", "Salida")]
    [InlineData("en-GB", "en", "Output")]
    [InlineData("fr-FR", "en", "Output")]
    public void SystemLanguageUsesTheSupportedParent_AndSwitchingPreservesRegionalCulture(string system, string expected, string output)
    {
        CultureInfo regional = CultureInfo.CurrentCulture;
        CultureInfo ui = CultureInfo.CurrentUICulture;
        var localization = new LocalizationService(systemCulture: CultureInfo.GetCultureInfo(system));
        Assert.Equal(expected, localization.EffectiveLanguage);
        Assert.Equal(output, localization["Common_Output"]);
        localization.SetLanguage("es");
        Assert.Equal("Audífonos · Disponible", localization.Format(new("Picker_Help", new LocalizedText("DeviceType_Headphones"), new LocalizedText("Picker_Available"))));
        Assert.Same(regional, CultureInfo.CurrentCulture);
        Assert.Same(ui, CultureInfo.CurrentUICulture);
        int revision = localization.Revision;
        localization.SetLanguage(" ES ");
        Assert.Equal(revision, localization.Revision);
    }

    [GeneratedRegex(@"(?:\{loc:Loc |\bFormat=)(\w+)[},]")]
    private static partial Regex MyRegex();
}

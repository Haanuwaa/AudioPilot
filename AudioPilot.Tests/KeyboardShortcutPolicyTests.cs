using System.Xml.Linq;

namespace AudioPilot.Tests;

public sealed class KeyboardShortcutPolicyTests
{
    [Fact]
    public void MainWindow_DeclaresHelpTabsNewRoutineAndRoutineEditShortcuts()
    {
        XDocument document = LoadXaml("AudioPilot", "MainWindow.xaml");
        XElement[] bindings = GetKeyBindings(document);

        AssertKeyBinding(bindings, "F1", "ApplicationCommands.Help");
        AssertKeyBinding(bindings, "D1", "{Binding SelectSettingsTabCommand}", "Control", "0");
        AssertKeyBinding(bindings, "D2", "{Binding SelectSettingsTabCommand}", "Control", "1");
        AssertKeyBinding(bindings, "D3", "{Binding SelectSettingsTabCommand}", "Control", "2");
        AssertKeyBinding(bindings, "D4", "{Binding SelectSettingsTabCommand}", "Control", "3");
        AssertKeyBinding(bindings, "N", "{Binding AddRoutineCommand}", "Control");
        AssertKeyBinding(bindings, "Enter", "{Binding EditRoutineCommand}");
    }

    [Fact]
    public void PackagedAppPicker_DeclaresSearchAndHelpShortcuts()
    {
        XDocument document = LoadXaml("AudioPilot", "PackagedAppPickerWindow.xaml");
        XElement[] bindings = GetKeyBindings(document);

        AssertKeyBinding(bindings, "F", "ApplicationCommands.Find", "Control");
        AssertKeyBinding(bindings, "F1", "ApplicationCommands.Help");
    }

    [Fact]
    public void RoutineEditor_DeclaresHelpShortcut()
    {
        XDocument document = LoadXaml("AudioPilot", "RoutineEditorWindow.xaml");

        AssertKeyBinding(GetKeyBindings(document), "F1", "ApplicationCommands.Help");
    }

    private static void AssertKeyBinding(
        IEnumerable<XElement> bindings,
        string key,
        string command,
        string? modifiers = null,
        string? commandParameter = null)
    {
        Assert.Contains(
            bindings,
            binding =>
                GetAttribute(binding, "Key") == key &&
                GetAttribute(binding, "Command") == command &&
                GetAttribute(binding, "Modifiers") == modifiers &&
                GetAttribute(binding, "CommandParameter") == commandParameter);
    }

    private static XElement[] GetKeyBindings(XDocument document)
    {
        return
        [
            .. document.Descendants().Where(static element => element.Name.LocalName == "KeyBinding"),
        ];
    }

    private static string? GetAttribute(XElement element, string name)
    {
        return element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == name)?.Value;
    }

    private static XDocument LoadXaml(params string[] pathParts)
    {
        return XDocument.Load(Path.Combine([ResolveRepositoryRoot(), .. pathParts]));
    }

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AudioPilot.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the AudioPilot repository root.");
    }
}

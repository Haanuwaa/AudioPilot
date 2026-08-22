using System.Xml.Linq;
using AudioPilot.Models;
using AudioPilot.Services.Hotkeys;

namespace AudioPilot.Tests;

public sealed class AboutTextPolicyTests
{
    [Fact]
    public void ReleaseMetadata_UsesVersionPropsAndMatchesChangelog()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        XDocument versionDocument = XDocument.Load(Path.Combine(repositoryRoot, "Version.props"));
        string version = GetProperty(versionDocument, "AudioPilotVersion");
        string releaseDate = GetProperty(versionDocument, "AudioPilotReleaseDate");
        string changelog = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "CHANGELOG.md"));

        Assert.True(DateOnly.TryParseExact(releaseDate, "yyyy-MM-dd", out _));
        Assert.Contains($"## [{version}] - {releaseDate}", changelog, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutText_IsGeneratedForReleaseOutputsAndPublishPackages()
    {
        string repositoryRoot = ResolveRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(repositoryRoot, "AudioPilot", "AudioPilot.csproj"));
        string releaseValidator = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "validate-release-integrity.ps1"));

        Assert.Contains("Name=\"GenerateAboutText\"", project, StringComparison.Ordinal);
        Assert.Contains("BeforeTargets=\"PrepareForBuild\"", project, StringComparison.Ordinal);
        Assert.Contains("Include=\"$(IntermediateOutputPath)ABOUT.txt\"", project, StringComparison.Ordinal);
        Assert.Contains("CopyToOutputDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
        Assert.Contains("CopyToPublishDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
        Assert.Contains("Release package is missing ABOUT.txt", releaseValidator, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutText_ListsEveryCurrentGlobalHotkeyActionAndDefaultBinding()
    {
        IReadOnlyList<string> lines = ReadAboutLines();
        var defaults = new Settings();

        string[] expectedLines =
        [
            $"- Show or hide AudioPilot: {HotkeyDisplayFormatter.FormatCompact(defaults.Hotkeys.App.ToggleAppVisibility)}",
            "- Switch to next output device: Unassigned",
            "- Switch to previous output device: Unassigned",
            "- Switch to next input device: Unassigned",
            "- Switch to previous input device: Unassigned",
            "- Show current track: Unassigned",
            $"- Play or pause media: {HotkeyDisplayFormatter.FormatCompact(defaults.Hotkeys.Media.PlayPause)}",
            $"- Next track: {HotkeyDisplayFormatter.FormatCompact(defaults.Hotkeys.Media.NextTrack)}",
            $"- Previous track: {HotkeyDisplayFormatter.FormatCompact(defaults.Hotkeys.Media.PreviousTrack)}",
            "- Mute or unmute microphone: Unassigned",
            "- Mute or unmute output: Unassigned",
            "- Deafen or undeafen: Unassigned",
            "- Toggle Listen to this device: Unassigned",
            "- Raise master output volume: Unassigned",
            "- Lower master output volume: Unassigned",
            "- Raise microphone volume: Unassigned",
            "- Lower microphone volume: Unassigned",
            "- Run a routine: Assigned separately for each hotkey-triggered routine",
        ];

        foreach (string expectedLine in expectedLines)
        {
            Assert.Contains(expectedLine, lines);
        }
    }

    [Fact]
    public void AboutText_ExplainsFriendlyDisplayNamesWithoutChangingCanonicalConfiguration()
    {
        IReadOnlyList<string> lines = ReadAboutLines();

        Assert.Contains(
            "Display labels use familiar key names such as Num *, Num +, Enter, Page Up, Backspace, Print Screen, and Next Track. Runtime punctuation, number-row, international-key, and numpad-decimal labels follow the active Windows keyboard layout; saved and CLI values remain canonical.",
            lines);
    }

    [Fact]
    public void AboutText_ListsImplementedInWindowKeyboardCommands()
    {
        IReadOnlyList<string> lines = ReadAboutLines();
        string[] expectedGestures =
        [
            "- F1:",
            "- Ctrl+1, Ctrl+2, Ctrl+3, or Ctrl+4:",
            "- Esc:",
            "- Ctrl+Tab:",
            "- Ctrl+S:",
            "- Ctrl+N:",
            "- Enter on the focused routine list:",
            "- Ctrl+F in a searchable picker:",
            "- F5:",
            "- Ctrl+A:",
            "- Ctrl+W:",
            "- Alt+Up or Alt+Down:",
            "- Left or Right:",
            "- Space:",
            "- Delete:",
            "- Shift+F10 or the Menu key:",
            "- Enter or Esc in an editor or picker:",
        ];

        foreach (string gesture in expectedGestures)
        {
            Assert.Contains(lines, line => line.StartsWith(gesture, StringComparison.Ordinal));
        }
    }

    private static string[] ReadAboutLines()
    {
        string projectPath = Path.Combine(ResolveRepositoryRoot(), "AudioPilot", "AudioPilot.csproj");
        XDocument projectDocument = XDocument.Load(projectPath);
        return
        [
            .. projectDocument
            .Descendants()
            .Where(element => element.Name.LocalName == "_AudioPilotAboutLine")
            .Select(element => ((string?)element.Attribute("Include") ?? string.Empty)
                .Replace("%2A", "*", StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static string GetProperty(XDocument document, string propertyName)
    {
        return document
            .Descendants()
            .Single(element => element.Name.LocalName == propertyName)
            .Value;
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

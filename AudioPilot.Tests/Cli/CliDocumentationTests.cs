using AudioPilot.Cli;
using AudioPilot.CliHost;

namespace AudioPilot.Tests.Cli;

public sealed class CliDocumentationTests
{
    public static TheoryData<string> GeneratedHelpTopics { get; } = [.. ExtractSupportedHelpTopics()];

    [Fact]
    public void CommandsSection_UsesCliExecutablePrefix()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        IReadOnlyList<string> documentedCommands = ExtractCommandsSection(markdown);

        Assert.All(documentedCommands, static line => Assert.StartsWith("AudioPilot.Cli.exe ", line, StringComparison.Ordinal));
    }

    [Fact]
    public void CommandsSection_MatchesGeneratedMarkdownExactly()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));
        string expectedSection = NormalizeLineEndings(CliDocumentationMarkdownGenerator.GenerateCommandsSection());
        string documentedSection = NormalizeLineEndings(ExtractGeneratedCommandsReference(markdown));

        Assert.Equal(expectedSection, documentedSection);
    }

    [Fact]
    public void HelpTopicsSection_MatchesGeneratedMarkdownExactly()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));
        string expectedSection = NormalizeLineEndings(CliDocumentationMarkdownGenerator.GenerateHelpTopicsSection());
        string documentedSection = NormalizeLineEndings(ExtractMarkdownSection(markdown, "## Help Topics"));

        Assert.Equal(expectedSection, documentedSection);
    }

    [Fact]
    public void QuickReferenceSection_MatchesGeneratedMarkdownExactly()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));
        string expectedSection = NormalizeLineEndings(CliDocumentationMarkdownGenerator.GenerateQuickReferenceSection());
        string documentedSection = NormalizeLineEndings(ExtractGeneratedQuickReference(markdown));

        Assert.Equal(expectedSection, documentedSection);
    }

    [Fact]
    public void AutomationRecipesSection_MatchesGeneratedMarkdownExactly()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));
        string expectedSection = NormalizeLineEndings(CliDocumentationMarkdownGenerator.GenerateAutomationRecipesSection());
        string documentedSection = NormalizeLineEndings(ExtractMarkdownSection(markdown, "## Common Automation Recipes"));

        Assert.Equal(expectedSection, documentedSection);
    }

    [Fact]
    public void RuntimeSection_DoesNotDocumentRemovedVisibleMixerPollKey()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        Assert.DoesNotContain("mixer-visible-refresh-poll-interval-ms", markdown, StringComparison.Ordinal);
        Assert.Contains("mixer-session-refresh-debounce-ms", markdown, StringComparison.Ordinal);
        Assert.Contains("output-switch-debounce-ms", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GeneratedHelpTopics))]
    public void CommandsSection_HelpLine_ListsSupportedHelpTopics(string topic)
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        Assert.Contains(topic, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandsSection_HelpLine_MatchesGeneratedHelpTopicListExactly()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        string expectedHelpLine = ToDocumentedCommand(
            ExtractUsageLines(CliCommand.UsageText)
                .Single(static line => line.StartsWith("audio-pilot help [", StringComparison.Ordinal)));

        IReadOnlyList<string> documentedCommands = ExtractCommandsSection(markdown);

        Assert.Contains(expectedHelpLine, documentedCommands, StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GeneratedHelpTopics))]
    public void CommandsSection_CoversGeneratedTopicHelpUsage(string topic)
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        IReadOnlyList<string> documentedCommands = ExtractCommandsSection(markdown);
        IReadOnlyList<string> expectedCommands = [.. ExtractUsageLines(CliCommand.GetHelpText(topic)).Select(ToDocumentedCommand)];

        Assert.All(expectedCommands, command => Assert.Contains(command, documentedCommands, StringComparer.Ordinal));
    }

    private static List<string> ExtractCommandsSection(string markdown)
    {
        const string codeFence = "```powershell";
        string commandsSection = ExtractMarkdownSection(markdown, "## Commands");

        List<string> documentedCommands = [];
        int searchIndex = 0;
        while (true)
        {
            int blockStart = commandsSection.IndexOf(codeFence, searchIndex, StringComparison.Ordinal);
            if (blockStart < 0)
            {
                break;
            }

            int contentStart = blockStart + codeFence.Length;
            int blockEnd = commandsSection.IndexOf("```", contentStart, StringComparison.Ordinal);
            Assert.True(blockEnd >= 0, "CLI commands code block end not found.");

            string blockContent = commandsSection[contentStart..blockEnd];
            documentedCommands.AddRange(blockContent
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim('\r', '\n', ' '))
                .Where(static line => !string.IsNullOrWhiteSpace(line)));

            searchIndex = blockEnd + 3;
        }

        Assert.NotEmpty(documentedCommands);
        return documentedCommands;
    }

    private static string ExtractMarkdownSection(string markdown, string heading)
    {
        int headingIndex = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(headingIndex >= 0, $"Markdown section '{heading}' not found.");

        int nextHeadingIndex = markdown.IndexOf("\n## ", headingIndex + heading.Length, StringComparison.Ordinal);
        int sectionEnd = nextHeadingIndex >= 0 ? nextHeadingIndex : markdown.Length;
        return markdown[headingIndex..sectionEnd].TrimEnd();
    }

    private static string ExtractGeneratedCommandsReference(string markdown)
    {
        string commandsSection = ExtractMarkdownSection(markdown, "## Commands");
        int selectorNotesIndex = FindGeneratedBlockBoundary(commandsSection, "Selector notes:");
        return selectorNotesIndex >= 0
            ? commandsSection[..selectorNotesIndex].TrimEnd()
            : commandsSection;
    }

    private static string ExtractGeneratedQuickReference(string markdown)
    {
        string quickReferenceSection = ExtractMarkdownSection(markdown, "## Quick Reference");
        int useCasesIndex = FindGeneratedBlockBoundary(quickReferenceSection, "Use cases:");
        return useCasesIndex >= 0
            ? quickReferenceSection[..useCasesIndex].TrimEnd()
            : quickReferenceSection;
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static int FindGeneratedBlockBoundary(string section, string boundaryHeading)
    {
        int boundaryIndex = section.IndexOf($"\n\n{boundaryHeading}", StringComparison.Ordinal);
        if (boundaryIndex >= 0)
        {
            return boundaryIndex;
        }

        return section.IndexOf($"\n{boundaryHeading}", StringComparison.Ordinal);
    }

    private static string[] ExtractSupportedHelpTopics()
    {
        string helpLine = ExtractUsageLines(CliCommand.UsageText)
            .Single(static line => line.StartsWith("audio-pilot help [", StringComparison.Ordinal));

        int start = helpLine.IndexOf('[', StringComparison.Ordinal);
        int end = helpLine.IndexOf(']', start + 1);
        Assert.True(start >= 0 && end > start, "Help topic list was not found in CLI usage text.");

        return helpLine[(start + 1)..end]
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static List<string> ExtractUsageLines(string helpText)
    {
        string[] lines = helpText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int usageIndex = Array.FindIndex(lines, static line => string.Equals(line, "Usage:", StringComparison.Ordinal));
        Assert.True(usageIndex >= 0, "Usage section not found in CLI help text.");

        List<string> usageLines = [];
        for (int index = usageIndex + 1; index < lines.Length; index++)
        {
            if (string.Equals(lines[index], "Notes:", StringComparison.Ordinal))
            {
                break;
            }

            usageLines.Add(lines[index].Trim());
        }

        Assert.NotEmpty(usageLines);
        return usageLines;
    }

    private static string ToDocumentedCommand(string cliCommand)
    {
        return cliCommand.Replace("audio-pilot", "AudioPilot.Cli.exe", StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        Assert.True(
            RepositoryRootLocator.TryFind([AppContext.BaseDirectory], out string repositoryRoot),
            "Could not locate the AudioPilot repository root from the test output directory.");
        return repositoryRoot;
    }
}

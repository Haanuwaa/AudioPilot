using System.Text;
using System.Text.Json;
using AudioPilot.Cli;

namespace AudioPilot.Tests.Cli;

public sealed class CliHostUtilitiesTests
{
    [Fact]
    public void PrefersJson_ReturnsTrue_WhenJsonFlagPresent()
    {
        bool prefersJson = CliHostUtilities.PrefersJson(["status", "--json"]);

        Assert.True(prefersJson);
    }

    [Fact]
    public void PrefersJson_ReturnsFalse_WhenJsonFlagAbsent()
    {
        bool prefersJson = CliHostUtilities.PrefersJson(["status"]);

        Assert.False(prefersJson);
    }

    [Fact]
    public void IsUiOnlyAction_ReturnsTrue_ForUiOnlyCommands()
    {
        Assert.True(CliHostUtilities.IsUiOnlyAction(CliAction.Show));
        Assert.True(CliHostUtilities.IsUiOnlyAction(CliAction.Hide));
        Assert.True(CliHostUtilities.IsUiOnlyAction(CliAction.StartupOpen));
    }

    [Fact]
    public void IsUiOnlyAction_ReturnsFalse_ForHeadlessSupportedCommand()
    {
        Assert.False(CliHostUtilities.IsUiOnlyAction(CliAction.Status));
        Assert.False(CliHostUtilities.IsUiOnlyAction(CliAction.RoutineCreate));
        Assert.False(CliHostUtilities.IsUiOnlyAction(CliAction.RoutineDelete));
        Assert.False(CliHostUtilities.IsUiOnlyAction(CliAction.RoutineImport));
    }

    [Fact]
    public void WriteCliError_WritesJsonEnvelope_WhenJsonRequested()
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);

        CliHostUtilities.WriteCliError(writer, 2, "invalid-usage", "Invalid CLI usage.", jsonOutput: true, includeUsage: true);

        string output = builder.ToString();
        using JsonDocument document = JsonDocument.Parse(output);

        JsonElement root = document.RootElement;
        Assert.Equal("1.0.0", root.GetProperty("schemaVersion").GetString());

        JsonElement error = root.GetProperty("data").GetProperty("error");
        Assert.Equal("invalid-usage", error.GetProperty("code").GetString());
        Assert.Equal("Invalid CLI usage.", error.GetProperty("message").GetString());
        Assert.Equal(2, error.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void WriteCliError_WritesPlainMessageAndUsage_WhenRequested()
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);

        CliHostUtilities.WriteCliError(writer, 2, "invalid-usage", "Invalid CLI usage.", jsonOutput: false, includeUsage: true);

        string output = builder.ToString();
        Assert.Contains("[diag-code:invalid-usage] Invalid CLI usage.", output, StringComparison.Ordinal);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteCliError_WritesSuggestedHelpTopic_WhenProvided()
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);

        CliHostUtilities.WriteCliError(
            writer,
            2,
            "invalid-usage",
            "Missing devices arguments.",
            jsonOutput: false,
            includeUsage: false,
            suggestedHelpTopic: "devices",
            helpExecutablePathOrName: "AudioPilot.Cli.exe");

        string output = builder.ToString();
        Assert.Contains("[diag-code:invalid-usage] Missing devices arguments.", output, StringComparison.Ordinal);
        Assert.Contains("AudioPilot.Cli.exe help devices", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteCliError_UsesProvidedExecutableName_ForSuggestedHelpTopic()
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);

        CliHostUtilities.WriteCliError(writer, 2, "invalid-usage", "Missing devices arguments.", jsonOutput: false, includeUsage: false, suggestedHelpTopic: "devices", helpExecutablePathOrName: @"C:\tools\AudioPilot.Custom.exe");

        string output = builder.ToString();
        Assert.Contains("AudioPilot.Custom.exe help devices", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTextError_UsesDiagCodePrefix()
    {
        string output = CliHostUtilities.FormatTextError("forwarding-failed", "Command forwarding failed.");

        Assert.Equal("[diag-code:forwarding-failed] Command forwarding failed.", output);
    }

    [Theory]
    [InlineData("devices", "devices")]
    [InlineData("VOLUME", "volume")]
    [InlineData("startup", "startup")]
    [InlineData("help", null)]
    [InlineData("unknown", null)]
    public void InferHelpTopic_ReturnsGroupedCommandTopic(string firstArg, string? expectedTopic)
    {
        string? actual = CliHostUtilities.InferHelpTopic([firstArg]);

        Assert.Equal(expectedTopic, actual);
    }
}

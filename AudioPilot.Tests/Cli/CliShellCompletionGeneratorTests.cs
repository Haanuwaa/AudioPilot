using AudioPilot.Cli;

namespace AudioPilot.Tests.Cli;

public sealed class CliShellCompletionGeneratorTests
{
    [Fact]
    public void GetScript_PowerShell_IncludesRegistrationAndCoreCommands()
    {
        string script = CliShellCompletionGenerator.GetScript("powershell");

        Assert.Contains("Register-ArgumentCompleter -Native", script, StringComparison.Ordinal);
        Assert.Contains("AudioPilot.Cli.exe", script, StringComparison.Ordinal);
        Assert.Contains("'completion'", script, StringComparison.Ordinal);
        Assert.Contains("'diagnostics'", script, StringComparison.Ordinal);
        Assert.Contains("'--json'", script, StringComparison.Ordinal);
        Assert.Contains("summary", script, StringComparison.Ordinal);
        Assert.Contains("manifest", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GetScript_Bash_IncludesCompletionFunctionAndCoreCommands()
    {
        string script = CliShellCompletionGenerator.GetScript("bash");

        Assert.Contains("_audiopilot_complete()", script, StringComparison.Ordinal);
        Assert.Contains("complete -F _audiopilot_complete AudioPilot.Cli.exe audio-pilot", script, StringComparison.Ordinal);
        Assert.Contains("completion", script, StringComparison.Ordinal);
        Assert.Contains("diagnostics", script, StringComparison.Ordinal);
        Assert.Contains("--detail", script, StringComparison.Ordinal);
        Assert.Contains("summary manifest", script, StringComparison.Ordinal);
    }
}

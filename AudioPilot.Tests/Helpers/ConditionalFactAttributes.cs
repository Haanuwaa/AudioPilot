using System.Runtime.CompilerServices;

namespace AudioPilot.Tests.Helpers;

internal sealed class AudioHardwareFactAttribute : FactAttribute
{
    public AudioHardwareFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!TestExecutionGuards.ShouldRunAudioHardware())
        {
            Skip = TestExecutionGuards.GetAudioHardwareSkipReason();
        }
    }
}

internal sealed class VisualIntegrationTheoryAttribute : TheoryAttribute
{
    public VisualIntegrationTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!TestExecutionGuards.ShouldRunVisualWpfIntegration())
        {
            Skip = TestExecutionGuards.GetVisualWpfSkipReason();
        }
    }
}

internal sealed class StressFactAttribute : FactAttribute
{
    public StressFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!TestExecutionGuards.ShouldRunStress())
        {
            Skip = TestExecutionGuards.GetStressSkipReason();
        }
    }
}

internal sealed class HardwareSoakFactAttribute : FactAttribute
{
    public HardwareSoakFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!TestExecutionGuards.ShouldRunHardwareSoak())
        {
            Skip = TestExecutionGuards.GetHardwareSoakSkipReason();
        }
    }
}

internal sealed class VisualIntegrationFactAttribute : FactAttribute
{
    public VisualIntegrationFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!TestExecutionGuards.ShouldRunVisualWpfIntegration())
        {
            Skip = TestExecutionGuards.GetVisualWpfSkipReason();
        }
    }
}

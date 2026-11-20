using AudioPilot.Models;

namespace AudioPilot.Cli;

public static partial class CliOutputFormatter
{
    public static CliExecutionResult FormatWindowLayout(WindowLayoutSnapshot layout, CliAction action, bool jsonOutput)
    {
        bool changed = action != CliAction.WindowStatus;
        bool applied = changed && (layout.Source == "live" || layout.Persisted);
        bool success = !changed || layout.Persisted;
        string? error = success ? null : applied ? "Window layout was applied, but could not be saved." : "Window layout could not be saved.";
        string output = jsonOutput ? SerializeCliJson(new
        {
            Success = success,
            Applied = applied,
            DiagCode = success ? null : "window-layout-save-failed",
            Error = error,
            Layout = layout
        })
            : $"Window ({layout.Source}, {layout.State}): x={layout.X}, y={layout.Y}, width={layout.Width}, height={layout.Height} physical pixels; DPI={layout.Dpi}; visible={layout.Visible}; saved={layout.Persisted}."
                + (error == null ? string.Empty : $"{Environment.NewLine}[diag-code:window-layout-save-failed] {error}");
        return new(success ? 0 : 7, output);
    }
}

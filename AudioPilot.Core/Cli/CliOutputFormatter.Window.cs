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
        string output = jsonOutput
            ? (success
                ? SerializeCliJson(new
                {
                    Success = true,
                    Applied = applied,
                    DiagCode = (string?)null,
                    Layout = layout
                })
                : SerializeCliErrorJson(
                    new
                    {
                        Success = false,
                        Applied = applied,
                        DiagCode = "window-layout-save-failed",
                        Layout = layout
                    },
                    7,
                    "window-layout-save-failed",
                    error!))
            : $"Window ({layout.Source}, {layout.State}): x={layout.X}, y={layout.Y}, width={layout.Width}, height={layout.Height} physical pixels; DPI={layout.Dpi}; visible={layout.Visible}; saved={layout.Persisted}."
                + (error == null ? string.Empty : $"{Environment.NewLine}[diag-code:window-layout-save-failed] {error}");
        return new(success ? 0 : 7, output);
    }
}

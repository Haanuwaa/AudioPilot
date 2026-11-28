using AudioPilot.Cli;
using AudioPilot.Services.UI;

namespace AudioPilot.CliHost;

internal sealed partial class LocalHeadlessCommandRunner
{
    private readonly Func<WindowPlacementStore>? _createWindowPlacement;
    private WindowPlacementStore? _windowPlacement;

    public async Task<CliExecutionResult> ExecuteWindowLayoutAsync(CliCommand command)
    {
        WindowPlacementStore placement = _windowPlacement ??= _createWindowPlacement?.Invoke()
            ?? new WindowPlacementStore(AppDataPaths.GetWindowPlacementPath(), _logger);
        var layout = command.Action == CliAction.WindowStatus
            ? await placement.GetHeadlessLayoutAsync(cancellationToken: _lifetimeCts.Token)
            : await placement.ChangeHeadlessLayoutAsync(command.Action == CliAction.WindowReset ? new(Reset: true) : command.WindowLayout ?? new(), cancellationToken: _lifetimeCts.Token);
        return CliOutputFormatter.FormatWindowLayout(layout, command.Action, command.JsonOutput);
    }
}

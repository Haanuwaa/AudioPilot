using AudioPilot.Cli;
using AudioPilot.Services.UI;

namespace AudioPilot.CliHost;

internal sealed partial class LocalHeadlessCommandRunner
{
    private readonly Func<MainWindowPlacementService>? _createWindowPlacement;
    private MainWindowPlacementService? _windowPlacement;

    public async Task<CliExecutionResult> ExecuteWindowLayoutAsync(CliCommand command)
    {
        MainWindowPlacementService placement = _windowPlacement ??= _createWindowPlacement?.Invoke()
            ?? new MainWindowPlacementService(AppDataPaths.GetWindowPlacementPath(), _logger);
        var layout = command.Action == CliAction.WindowStatus
            ? await placement.GetLayoutAsync(cancellationToken: _lifetimeCts.Token)
            : await placement.ChangeLayoutAsync(command.Action == CliAction.WindowReset ? new(Reset: true) : command.WindowLayout ?? new(), cancellationToken: _lifetimeCts.Token);
        return CliOutputFormatter.FormatWindowLayout(layout, command.Action, command.JsonOutput);
    }
}

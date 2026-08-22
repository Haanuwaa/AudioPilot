using AudioPilot.Models;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class RecordingOverlayPresenter : OverlayService.IOverlayPresenter
{
    public int StackAdvancePixels { get; set; } = 100;
    public int DisposeCount { get; private set; }
    public int MessageUpdateCount { get; private set; }
    public int ActionUpdateCount { get; private set; }
    public int DeviceUpdateCount { get; private set; }
    public int RoutineUpdateCount { get; private set; }
    public int RoutinePartialUpdateCount { get; private set; }
    public int MediaUpdateCount { get; private set; }
    public int ShowCount { get; private set; }
    public int ApplyDisplayOptionsCount { get; private set; }
    public List<int?> StackOffsets { get; } = [];
    public List<(OverlayDeviceKind? kind, string header, string? deviceName)> Messages { get; } = [];
    public List<(OverlayActionStateKind stateKind, string message)> ActionMessages { get; } = [];

    public void UpdateContent(string message)
    {
        MessageUpdateCount++;
        Messages.Add((null, message, null));
    }

    public void UpdateContent(OverlayActionStateKind stateKind, string message)
    {
        ActionUpdateCount++;
        ActionMessages.Add((stateKind, message));
    }

    public void UpdateContent(OverlayDeviceKind kind, string header, string deviceName)
    {
        DeviceUpdateCount++;
        Messages.Add((kind, header, deviceName));
    }

    public void UpdateRoutineContent(string header, string? outputDeviceName, string? inputDeviceName)
    {
        RoutineUpdateCount++;
        Messages.Add((null, header, outputDeviceName ?? inputDeviceName));
    }

    public void UpdateRoutinePartialContent(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName)
    {
        RoutinePartialUpdateCount++;
        Messages.Add((null, header, outputDeviceName ?? inputDeviceName ?? failedOutputDeviceName ?? failedInputDeviceName));
    }

    public void UpdateContent(string header, string title, string? artist)
    {
        MediaUpdateCount++;
        Messages.Add((null, header, title));
    }

    public void ApplyDisplayOptions(OverlayPosition position, double durationSeconds, int stackIndex, int? stackOffsetPixels = null)
    {
        ApplyDisplayOptionsCount++;
        StackOffsets.Add(stackOffsetPixels);
    }

    public void ShowOverlay()
    {
        ShowCount++;
    }

    public void Dispose()
    {
        DisposeCount++;
    }
}

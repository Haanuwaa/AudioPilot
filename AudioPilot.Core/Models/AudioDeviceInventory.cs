using System.Collections.ObjectModel;

namespace AudioPilot.Models;

/// <summary>Preserves discovery completeness alongside a read-only snapshot of the endpoints that could be read.</summary>
internal sealed class AudioDeviceInventory(IList<CycleDevice> devices, bool isComplete = true) : ReadOnlyCollection<CycleDevice>(devices)
{
    internal bool IsComplete { get; } = isComplete;

    internal static AudioDeviceInventory Capture(IReadOnlyList<CycleDevice> devices) =>
        new([.. devices], devices is not AudioDeviceInventory { IsComplete: false });
}

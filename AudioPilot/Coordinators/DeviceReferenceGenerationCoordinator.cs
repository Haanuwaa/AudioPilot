using AudioPilot.Models;

namespace AudioPilot.Coordinators;

/// <summary>Queues fresh inventory exports and prevents superseded work from committing after settings change or shutdown.</summary>
internal sealed class DeviceReferenceGenerationCoordinator(
    Action<Func<CancellationToken, Task>> queueBackgroundWork,
    Func<CancellationToken, Task<(List<CycleDevice> OutputDevices, List<CycleDevice> InputDevices)>> readInventory,
    Func<DeviceReferenceFileMode> getMode,
    Action<List<CycleDevice>, List<CycleDevice>, bool> writeReference,
    SemaphoreSlim settingsWriteGate,
    Func<bool> isStopping)
{
    private long _revision;

    internal void Request()
    {
        long revision = Interlocked.Increment(ref _revision);
        if (isStopping() || !IsEnabled(getMode())) return;
        queueBackgroundWork(token => GenerateAsync(revision, token));
    }

    private async Task GenerateAsync(long revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (isStopping() || revision != Volatile.Read(ref _revision) || !IsEnabled(getMode())) return;
        var (outputDevices, inputDevices) = await readInventory(token).ConfigureAwait(false);
        await settingsWriteGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (isStopping() || revision != Volatile.Read(ref _revision)) return;
            DeviceReferenceFileMode mode = getMode();
            if (!IsEnabled(mode)) return;
            writeReference(outputDevices, inputDevices, mode == DeviceReferenceFileMode.Hashed);
        }
        finally { settingsWriteGate.Release(); }
    }

    private static bool IsEnabled(DeviceReferenceFileMode mode) => mode is DeviceReferenceFileMode.Plaintext or DeviceReferenceFileMode.Hashed;
}

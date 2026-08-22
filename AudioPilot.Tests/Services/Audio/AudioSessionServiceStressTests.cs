using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Stress)]
public sealed class AudioSessionServiceStressTests
{
    private sealed class StubEnumerator : IAudioDeviceEnumerator
    {
        public MMDeviceCollection GetActivePlaybackDevices() => throw new NotSupportedException();
        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds) => throw new NotSupportedException();
        public MMDevice GetDefaultPlaybackDevice() => throw new NotSupportedException();
        public MMDevice? GetDefaultRecordingDevice() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultPlaybackDevices() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultRecordingDevices() => throw new NotSupportedException();
    }

    [StressFact]
    public void ProcessCacheChurn_TrimKeepsBoundedSize_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(ProcessCacheChurn_TrimKeepsBoundedSize_WhenStressEnabled)))
        {
            return;
        }

        using var service = new AudioSessionService(new StubEnumerator());

        long freshTicks = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;
        const int injectedEntries = 20000;
        for (uint i = 1; i <= injectedEntries; i++)
        {
            service.AddProcessCacheEntryForTests(
                i,
                $"Process-{i}",
                $"Display-{i}",
                (string?)null,
                freshTicks);
        }

        service.TrimProcessCacheForTests();

        int countAfterTrim = service.ProcessCacheCountForTests;
        Assert.True(
            countAfterTrim <= AppConstants.Limits.MaxProcessCacheEntries,
            $"Process cache exceeded limit after trim: {countAfterTrim}");

        service.ClearCaches();

        int countAfterClear = service.ProcessCacheCountForTests;
        Assert.Equal(0, countAfterClear);
    }
}


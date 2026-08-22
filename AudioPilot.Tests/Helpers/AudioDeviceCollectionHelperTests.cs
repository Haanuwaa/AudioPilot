using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public sealed class AudioDeviceCollectionHelperTests
{
    [Fact]
    public void DisposeDevices_ReleasesUnvisitedDevices_AfterAnEarlierDisposeFails()
    {
        var alreadyReleased = new TestDevice();
        var failed = new TestDevice { FailDispose = true };
        var remaining = new TestDevice();
        List<TestDevice?> devices = [alreadyReleased, failed, remaining];
        alreadyReleased.Dispose();
        devices[0] = null;
        int failures = 0;

        AudioDeviceCollectionHelper.DisposeDevices(devices, (_, _) => failures++);

        Assert.Equal(1, alreadyReleased.DisposeCalls);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.Equal(1, remaining.DisposeCalls);
        Assert.Equal(1, failures);
    }

    private sealed class TestDevice : IDisposable
    {
        public int DisposeCalls { get; private set; }
        public bool FailDispose { get; init; }

        public void Dispose()
        {
            DisposeCalls++;
            if (FailDispose) throw new InvalidOperationException("Simulated disposal failure");
        }
    }
}

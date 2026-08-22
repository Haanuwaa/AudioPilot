using System.Runtime.InteropServices;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceActiveCycleResolverHelperTests
{
    [Fact]
    public void TryResolve_ReturnsNull_WhenDisposed()
    {
        CycleDevice? result = AudioDeviceActiveCycleResolverHelper.TryResolve(
            "out-1",
            "Fallback",
            isDisposed: true,
            static _ => new FakeDevice("Speakers"),
            static device => device.IsActive,
            static (deviceId, fallbackName, getFriendlyName) => CreateCycleDevice(deviceId, fallbackName, getFriendlyName),
            static device => device.FriendlyName,
            static device => device.Dispose(),
            static _ => { });

        Assert.Null(result);
    }

    [Fact]
    public void TryResolve_ReturnsProjectedDevice_AndDisposesSource()
    {
        FakeDevice? capturedDevice = null;

        CycleDevice? result = AudioDeviceActiveCycleResolverHelper.TryResolve(
            "out-1",
            "Fallback",
            isDisposed: false,
            _ => capturedDevice = new FakeDevice("Speakers"),
            static device => device.IsActive,
            static (deviceId, fallbackName, getFriendlyName) => CreateCycleDevice(deviceId, fallbackName, getFriendlyName),
            static device => device.FriendlyName,
            static device => device.Dispose(),
            static _ => { });

        Assert.NotNull(result);
        Assert.Equal("out-1", result.Id);
        Assert.Equal("Speakers", result.Name);
        Assert.NotNull(capturedDevice);
        Assert.True(capturedDevice!.Disposed);
    }

    [Fact]
    public void TryResolve_ReturnsNull_ForMissingDeviceComException()
    {
        int traceCount = 0;

        CycleDevice? result = AudioDeviceActiveCycleResolverHelper.TryResolve<FakeDevice, CycleDevice>(
            "out-1",
            "Fallback",
            isDisposed: false,
            static _ => throw new COMException("missing", unchecked((int)0x80070490)),
            static device => device.IsActive,
            static (deviceId, fallbackName, getFriendlyName) => CreateCycleDevice(deviceId, fallbackName, getFriendlyName),
            static device => device.FriendlyName,
            static device => device.Dispose(),
            _ => traceCount++);

        Assert.Null(result);
        Assert.Equal(0, traceCount);
    }

    [Fact]
    public void TryResolve_LogsTrace_ForUnexpectedException()
    {
        string? traceMessage = null;

        CycleDevice? result = AudioDeviceActiveCycleResolverHelper.TryResolve<FakeDevice, CycleDevice>(
            "out-1",
            "Fallback",
            isDisposed: false,
            static _ => throw new InvalidOperationException("boom"),
            static device => device.IsActive,
            static (deviceId, fallbackName, getFriendlyName) => CreateCycleDevice(deviceId, fallbackName, getFriendlyName),
            static device => device.FriendlyName,
            static device => device.Dispose(),
            message => traceMessage = message);

        Assert.Null(result);
        Assert.Equal("Active cycle device lookup failed: InvalidOperationException", traceMessage);
    }

    private static CycleDevice CreateCycleDevice(
        string deviceId,
        string fallbackName,
        Func<string?> getFriendlyName)
    {
        string? friendlyName = getFriendlyName();

        return new CycleDevice
        {
            Id = deviceId,
            Name = string.IsNullOrWhiteSpace(friendlyName) ? fallbackName : friendlyName,
        };
    }

    private sealed class FakeDevice(string friendlyName)
    {
        public string FriendlyName { get; } = friendlyName;
        public bool IsActive { get; init; } = true;
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}

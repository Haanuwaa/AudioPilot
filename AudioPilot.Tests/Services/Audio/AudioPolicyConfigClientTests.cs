using System.Runtime.InteropServices;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioPolicyConfigClientTests
{
    [Fact]
    public void SetDefaultDevice_WhenWin10Available_CachesDetectedClsid()
    {
        var detectionHandle = new FakePolicyConfigHandle();
        var firstCallHandle = new FakePolicyConfigHandle();
        var secondCallHandle = new FakePolicyConfigHandle();
        var activator = new FakePolicyConfigActivator();
        activator.ResultsByClsid[AudioPolicyConfigClient.Win10Clsid] = new Queue<(bool, int, FakePolicyConfigHandle?)>([
            (true, 0, detectionHandle),
            (true, 0, firstCallHandle),
            (true, 0, secondCallHandle),
        ]);

        var client = new AudioPolicyConfigClient(activator, Logger.Instance, _ => { });

        client.SetDefaultDevice("device-a", Role.Console);
        client.SetDefaultDevice("device-a", Role.Console);

        Assert.Equal([
            AudioPolicyConfigClient.Win10Clsid,
            AudioPolicyConfigClient.Win10Clsid,
            AudioPolicyConfigClient.Win10Clsid,
        ], activator.Calls);
        Assert.Equal(1, detectionHandle.DisposeCalls);
        Assert.Equal(1, firstCallHandle.SetDefaultEndpointCalls);
        Assert.Equal(1, secondCallHandle.SetDefaultEndpointCalls);
    }

    [Fact]
    public void DetectClsid_WhenWin10Unavailable_FallsBackToWin11()
    {
        var detectionHandle = new FakePolicyConfigHandle();
        var activator = new FakePolicyConfigActivator();
        activator.ResultsByClsid[AudioPolicyConfigClient.Win10Clsid] = new Queue<(bool, int, FakePolicyConfigHandle?)>([
            (false, unchecked((int)0x80040154), null),
        ]);
        activator.ResultsByClsid[AudioPolicyConfigClient.Win11Clsid] = new Queue<(bool, int, FakePolicyConfigHandle?)>([
            (true, 0, detectionHandle),
        ]);

        var client = new AudioPolicyConfigClient(activator, Logger.Instance, _ => { });

        Guid result = client.DetectClsid();

        Assert.Equal(AudioPolicyConfigClient.Win11Clsid, result);
        Assert.Equal(1, detectionHandle.DisposeCalls);
    }

    [Fact]
    public void SetDefaultDevice_WhenSetDefaultEndpointFails_InvalidatesCachedClsid()
    {
        var firstDetectionHandle = new FakePolicyConfigHandle();
        var failedCallHandle = new FakePolicyConfigHandle
        {
            SetDefaultEndpointResult = unchecked((int)0x80004005)
        };
        var secondDetectionHandle = new FakePolicyConfigHandle();
        var successCallHandle = new FakePolicyConfigHandle();
        var activator = new FakePolicyConfigActivator();
        activator.ResultsByClsid[AudioPolicyConfigClient.Win10Clsid] = new Queue<(bool, int, FakePolicyConfigHandle?)>([
            (true, 0, firstDetectionHandle),
            (true, 0, failedCallHandle),
            (true, 0, secondDetectionHandle),
            (true, 0, successCallHandle),
        ]);

        var client = new AudioPolicyConfigClient(activator, Logger.Instance, _ => { });

        COMException failure = Assert.Throws<COMException>(() => client.SetDefaultDevice("device-a", Role.Console));
        client.SetDefaultDevice("device-a", Role.Console);

        Assert.Equal(unchecked((int)0x80004005), failure.HResult);
        Assert.Equal([
            AudioPolicyConfigClient.Win10Clsid,
            AudioPolicyConfigClient.Win10Clsid,
            AudioPolicyConfigClient.Win10Clsid,
            AudioPolicyConfigClient.Win10Clsid,
        ], activator.Calls);
        Assert.Equal(1, failedCallHandle.SetDefaultEndpointCalls);
        Assert.Equal(1, successCallHandle.SetDefaultEndpointCalls);
    }

    [Fact]
    public void TrySetProcessDefaultDevice_PacksRenderDeviceId_AndAppliesEachRole()
    {
        var handle = new FakeAudioRoutingPolicyHandle();
        var activator = new FakeAudioRoutingPolicyActivator();
        activator.Results.Enqueue((true, 0, handle));

        var client = new AudioRoutingPolicyClient(activator, Logger.Instance, _ => { });

        ProcessAudioRoutingResult result = client.TrySetProcessDefaultDevice(42, DataFlow.Render, [Role.Multimedia, Role.Communications], "device-a");

        Assert.Equal(ProcessAudioRoutingResult.Applied, result);
        Assert.Equal(2, handle.Calls.Count);
        Assert.All(handle.Calls, static call => Assert.Equal(42u, call.ProcessId));
        Assert.All(handle.Calls, static call => Assert.Equal(DataFlow.Render, call.Flow));
        Assert.All(handle.Calls, static call => Assert.Equal(@"\\?\SWD#MMDEVAPI#device-a#{e6327cad-dcec-4949-ae8a-991e976a79d2}", call.DeviceId));
    }

    [Fact]
    public void TrySetProcessDefaultDevice_WhenProcessHasNoAudio_ReturnsDeferredNoAudio()
    {
        var handle = new FakeAudioRoutingPolicyHandle
        {
            Result = AudioRoutingPolicyClient.ProcessNoAudioHResult
        };
        var activator = new FakeAudioRoutingPolicyActivator();
        activator.Results.Enqueue((true, 0, handle));

        var client = new AudioRoutingPolicyClient(activator, Logger.Instance, _ => { });

        ProcessAudioRoutingResult result = client.TrySetProcessDefaultDevice(42, DataFlow.Render, [Role.Multimedia], "device-a");

        Assert.Equal(ProcessAudioRoutingResult.DeferredNoAudio, result);
        Assert.Single(handle.Calls);
    }

    [Fact]
    public void TrySetProcessDefaultDevice_WhenComInitializationFails_ReturnsFailed()
    {
        var activator = new FakeAudioRoutingPolicyActivator();
        var client = new AudioRoutingPolicyClient(
            activator,
            Logger.Instance,
            _ => throw new COMException("init failed", unchecked((int)0x80004005)));

        ProcessAudioRoutingResult result = client.TrySetProcessDefaultDevice(42, DataFlow.Render, [Role.Multimedia], "device-a");

        Assert.Equal(ProcessAudioRoutingResult.Failed, result);
        Assert.Empty(activator.Results);
    }

    [Fact]
    public void TrySetProcessDefaultDevice_WhenActivatorCreationFails_ReturnsFailed()
    {
        var activator = new FakeAudioRoutingPolicyActivator();
        activator.Results.Enqueue((false, unchecked((int)0x80040154), null));

        var client = new AudioRoutingPolicyClient(activator, Logger.Instance, _ => { });

        ProcessAudioRoutingResult result = client.TrySetProcessDefaultDevice(42, DataFlow.Render, [Role.Multimedia], "device-a");

        Assert.Equal(ProcessAudioRoutingResult.Failed, result);
    }

    [Fact]
    public void TrySetProcessDefaultDevice_WhenEndpointAssignmentFails_ReturnsFailed()
    {
        var handle = new FakeAudioRoutingPolicyHandle
        {
            Result = unchecked((int)0x80004005)
        };
        var activator = new FakeAudioRoutingPolicyActivator();
        activator.Results.Enqueue((true, 0, handle));

        var client = new AudioRoutingPolicyClient(activator, Logger.Instance, _ => { });

        ProcessAudioRoutingResult result = client.TrySetProcessDefaultDevice(42, DataFlow.Render, [Role.Multimedia], "device-a");

        Assert.Equal(ProcessAudioRoutingResult.Failed, result);
        Assert.Single(handle.Calls);
    }

    [Fact]
    public void TrySetProcessDefaultDevice_RedactsProcessId_InFailureLogs()
    {
        using var loggerScope = new TestLoggerScope(nameof(TrySetProcessDefaultDevice_RedactsProcessId_InFailureLogs), "audio-policy-routing.log");
        var handle = new FakeAudioRoutingPolicyHandle
        {
            Result = unchecked((int)0x80004005)
        };
        var activator = new FakeAudioRoutingPolicyActivator();
        activator.Results.Enqueue((true, 0, handle));

        var client = new AudioRoutingPolicyClient(activator, loggerScope.Logger, _ => { });

        ProcessAudioRoutingResult result = client.TrySetProcessDefaultDevice(42, DataFlow.Render, [Role.Multimedia], "device-a");

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(ProcessAudioRoutingResult.Failed, result);
        Assert.Contains("SetProcessDefaultDevice failed pid=id[", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("pid=42", logText, StringComparison.Ordinal);
        Assert.Contains("pid=id[len=2 hash=", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void TryClearProcessDefaultDevice_UsesEmptyDeviceId_ForEachRole()
    {
        var handle = new FakeAudioRoutingPolicyHandle();
        var activator = new FakeAudioRoutingPolicyActivator();
        activator.Results.Enqueue((true, 0, handle));

        var client = new AudioRoutingPolicyClient(activator, Logger.Instance, _ => { });

        ProcessAudioRoutingResult result = client.TryClearProcessDefaultDevice(42, DataFlow.Render, [Role.Multimedia, Role.Communications]);

        Assert.Equal(ProcessAudioRoutingResult.Applied, result);
        Assert.Equal(2, handle.Calls.Count);
        Assert.All(handle.Calls, static call => Assert.Equal(string.Empty, call.DeviceId));
    }

    [Fact]
    public void PackPersistedDeviceId_UsesCaptureSuffix_ForCaptureFlows()
    {
        string packed = AudioRoutingPolicyClient.PackPersistedDeviceId("device-a", DataFlow.Capture);

        Assert.Equal(@"\\?\SWD#MMDEVAPI#device-a#{2eef81be-33fa-4800-9670-1cd474972c3f}", packed);
    }
}

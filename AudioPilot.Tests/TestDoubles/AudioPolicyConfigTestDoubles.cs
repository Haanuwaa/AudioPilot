using System.Diagnostics.CodeAnalysis;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakePolicyConfigActivator : IPolicyConfigActivator
{
    public Dictionary<Guid, Queue<(bool Success, int HResult, FakePolicyConfigHandle? Handle)>> ResultsByClsid { get; } = [];
    public List<Guid> Calls { get; } = [];

    public bool TryCreate(Guid clsid, [NotNullWhen(true)] out IPolicyConfigHandle? handle, out int hresult)
    {
        Calls.Add(clsid);

        if (!ResultsByClsid.TryGetValue(clsid, out Queue<(bool Success, int HResult, FakePolicyConfigHandle? Handle)>? results)
            || results.Count == 0)
        {
            handle = null;
            hresult = unchecked((int)0x80040154);
            return false;
        }

        (bool success, int resultHResult, FakePolicyConfigHandle? resultHandle) = results.Dequeue();
        handle = resultHandle;
        hresult = resultHResult;
        return success;
    }
}

internal sealed class FakePolicyConfigHandle : IPolicyConfigHandle
{
    public int SetDefaultEndpointResult { get; set; }
    public int SetDefaultEndpointCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    public int SetDefaultEndpoint(string deviceId, Role role)
    {
        SetDefaultEndpointCalls++;
        return SetDefaultEndpointResult;
    }

    public void Dispose()
    {
        DisposeCalls++;
    }
}

internal sealed class FakeAudioRoutingPolicyActivator : IAudioRoutingPolicyActivator
{
    public Queue<(bool Success, int HResult, FakeAudioRoutingPolicyHandle? Handle)> Results { get; } = [];

    public bool TryCreate([NotNullWhen(true)] out IAudioRoutingPolicyHandle? handle, out int hresult)
    {
        if (Results.Count == 0)
        {
            handle = null;
            hresult = unchecked((int)0x80040154);
            return false;
        }

        (bool success, int resultHResult, FakeAudioRoutingPolicyHandle? resultHandle) = Results.Dequeue();
        handle = resultHandle;
        hresult = resultHResult;
        return success;
    }
}

internal sealed class FakeAudioRoutingPolicyHandle : IAudioRoutingPolicyHandle
{
    public List<(uint ProcessId, DataFlow Flow, Role Role, string DeviceId)> Calls { get; } = [];
    public int Result { get; set; }

    public int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, string deviceId)
    {
        Calls.Add((processId, flow, role, deviceId));
        return Result;
    }

    public void Dispose()
    {
    }
}

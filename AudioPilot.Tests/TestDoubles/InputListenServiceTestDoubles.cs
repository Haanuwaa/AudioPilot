using AudioPilot.Models;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeInputListenAudioDeviceResolver : IInputListenAudioDeviceResolver
{
    public IAudioEndpointInfo? DefaultRecordingEndpoint { get; set; }
    public IAudioEndpointInfo? DefaultPlaybackEndpoint { get; set; }
    public Queue<IAudioEndpointInfo?> DefaultRecordingEndpoints { get; } = new();
    public Queue<IAudioEndpointInfo?> DefaultPlaybackEndpoints { get; } = new();
    public Dictionary<string, IAudioEndpointInfo> PlaybackEndpointsById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CycleDevice> ActivePlaybackDeviceInfos { get; } = [];
    public int GetDefaultRecordingEndpointCalls { get; private set; }
    public int GetDefaultPlaybackEndpointCalls { get; private set; }

    public IAudioEndpointInfo? GetDefaultRecordingEndpoint()
    {
        GetDefaultRecordingEndpointCalls++;
        return DefaultRecordingEndpoints.Count > 0 ? DefaultRecordingEndpoints.Dequeue() : DefaultRecordingEndpoint;
    }

    public IAudioEndpointInfo? GetDefaultPlaybackEndpoint()
    {
        GetDefaultPlaybackEndpointCalls++;
        return DefaultPlaybackEndpoints.Count > 0 ? DefaultPlaybackEndpoints.Dequeue() : DefaultPlaybackEndpoint;
    }

    public IAudioEndpointInfo? TryGetPlaybackEndpoint(string deviceId)
    {
        return PlaybackEndpointsById.TryGetValue(deviceId, out IAudioEndpointInfo? endpoint)
            ? endpoint
            : null;
    }

    public IReadOnlyList<CycleDevice> GetActivePlaybackDeviceInfos() => ActivePlaybackDeviceInfos;
}

internal sealed class QueueInputListenPropertyReader : IInputListenPropertyReader
{
    public Queue<(bool Success, bool Enabled, string? Error)> ListenEnabledResults { get; } = new();
    public Queue<(bool Success, string? RenderTargetId, string? Error)> RenderTargetResults { get; } = new();
    public int ListenEnabledCalls { get; private set; }
    public int RenderTargetCalls { get; private set; }

    public bool TryGetListenEnabled(IAudioEndpointInfo inputEndpoint, out bool enabled, out string? error)
    {
        ListenEnabledCalls++;
        if (ListenEnabledResults.Count == 0)
        {
            enabled = false;
            error = null;
            return true;
        }

        var (success, dequeuedEnabled, dequeuedError) = ListenEnabledResults.Dequeue();
        enabled = dequeuedEnabled;
        error = dequeuedError;
        return success;
    }

    public bool TryGetListenRenderTargetId(IAudioEndpointInfo inputEndpoint, out string? renderTargetId, out string? error)
    {
        RenderTargetCalls++;
        if (RenderTargetResults.Count == 0)
        {
            renderTargetId = null;
            error = null;
            return true;
        }

        var (success, dequeuedRenderTargetId, dequeuedError) = RenderTargetResults.Dequeue();
        renderTargetId = dequeuedRenderTargetId;
        error = dequeuedError;
        return success;
    }
}

internal sealed class FakeInputListenPropertyWriter : IInputListenPropertyWriter
{
    public bool Result { get; set; } = true;
    public string? Error { get; set; }
    public int Calls { get; private set; }
    public bool? LastEnabled { get; private set; }
    public string? LastInputDeviceId { get; private set; }
    public string? LastRenderDeviceId { get; private set; }

    public bool TrySetListenProperties(IAudioEndpointInfo inputEndpoint, string? renderDeviceId, bool enabled, out string? error)
    {
        Calls++;
        LastEnabled = enabled;
        LastInputDeviceId = inputEndpoint.Id;
        LastRenderDeviceId = renderDeviceId;
        error = Error;
        return Result;
    }
}

using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Audio.Testing;

internal static class AudioEndpointTestFailureClassifier
{
    public static AudioEndpointTestException Classify(
        AudioEndpointReference endpoint,
        Exception exception,
        bool duringActivation = true)
    {
        int errorCode = exception.HResult;
        AudioEndpointTestFailureKind kind = errorCode switch
        {
            AudioClientErrorCode.DeviceInUse => AudioEndpointTestFailureKind.ExclusiveUse,
            AudioClientErrorCode.DeviceInvalidated or AudioClientErrorCode.ResourcesInvalidated or
                AudioClientErrorCode.EndpointCreateFailed or unchecked((int)0x80070490) => AudioEndpointTestFailureKind.Unavailable,
            AudioClientErrorCode.UnsupportedFormat => AudioEndpointTestFailureKind.UnsupportedFormat,
            AudioClientErrorCode.ServiceNotRunning => AudioEndpointTestFailureKind.AudioServiceUnavailable,
            unchecked((int)0x80070005) => AudioEndpointTestFailureKind.AccessDenied,
            _ => duringActivation ? AudioEndpointTestFailureKind.ActivationFailed : AudioEndpointTestFailureKind.Unexpected,
        };

        string message = kind switch
        {
            AudioEndpointTestFailureKind.ExclusiveUse => $"{endpoint.Name} is in exclusive use by another application.",
            AudioEndpointTestFailureKind.Unavailable => $"{endpoint.Name} became unavailable. Reconnect or re-enable the device and try again.",
            AudioEndpointTestFailureKind.UnsupportedFormat => $"{endpoint.Name} rejected the test audio format.",
            AudioEndpointTestFailureKind.AudioServiceUnavailable => "The Windows audio service is not running. Start it or restart Windows, then try again.",
            AudioEndpointTestFailureKind.AccessDenied => $"Windows denied access to {endpoint.Name}. Check device permissions and, for a microphone, Windows microphone access settings.",
            _ => $"AudioPilot could not use {endpoint.Name} for testing.",
        };

        return new AudioEndpointTestException(kind, message, exception);
    }
}

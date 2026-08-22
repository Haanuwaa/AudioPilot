using System.Runtime.InteropServices;

namespace AudioPilot.Services.Audio.Testing;

internal static class AudioEndpointTestFailureClassifier
{
    public static AudioEndpointTestException ClassifyActivation(
        AudioEndpointReference endpoint,
        Exception exception)
    {
        int errorCode = exception is COMException comException ? comException.HResult : exception.HResult;
        AudioEndpointTestFailureKind kind = errorCode switch
        {
            unchecked((int)0x8889000A) => AudioEndpointTestFailureKind.ExclusiveUse,
            unchecked((int)0x88890004) => AudioEndpointTestFailureKind.Unavailable,
            unchecked((int)0x88890008) => AudioEndpointTestFailureKind.UnsupportedFormat,
            _ => AudioEndpointTestFailureKind.ActivationFailed,
        };

        string message = kind switch
        {
            AudioEndpointTestFailureKind.ExclusiveUse => $"{endpoint.Name} is in exclusive use by another application.",
            AudioEndpointTestFailureKind.Unavailable => $"{endpoint.Name} was disconnected while the test was starting.",
            AudioEndpointTestFailureKind.UnsupportedFormat => $"{endpoint.Name} rejected the test audio format.",
            _ => $"AudioPilot could not open {endpoint.Name} for testing.",
        };

        return new AudioEndpointTestException(kind, message, exception);
    }
}

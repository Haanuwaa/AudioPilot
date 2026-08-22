using System.Runtime.InteropServices;
using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioEndpointTestFailureClassifierTests
{
    private static readonly AudioEndpointReference Endpoint = new("output", "Test output");

    [Theory]
    [InlineData(unchecked((int)0x8889000A), (int)AudioEndpointTestFailureKind.ExclusiveUse, "exclusive use")]
    [InlineData(unchecked((int)0x88890004), (int)AudioEndpointTestFailureKind.Unavailable, "disconnected")]
    [InlineData(unchecked((int)0x88890008), (int)AudioEndpointTestFailureKind.UnsupportedFormat, "rejected")]
    [InlineData(unchecked((int)0x80004005), (int)AudioEndpointTestFailureKind.ActivationFailed, "could not open")]
    public void ClassifyActivation_MapsCoreAudioFailureWithoutLosingCause(
        int errorCode,
        int expectedKind,
        string expectedMessageFragment)
    {
        var cause = new COMException("Synthetic Core Audio failure", errorCode);

        AudioEndpointTestException result = AudioEndpointTestFailureClassifier.ClassifyActivation(Endpoint, cause);

        Assert.Equal((AudioEndpointTestFailureKind)expectedKind, result.FailureKind);
        Assert.Contains(expectedMessageFragment, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(cause, result.InnerException);
    }
}

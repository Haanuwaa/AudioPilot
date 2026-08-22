namespace AudioPilot.Tests.Platform;

public sealed class DiagnosticSessionTests
{
    [Fact]
    public void CurrentProcessScope_MatchesTransportIdentity()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(SingleInstanceScope.Current, SingleInstanceScope.Read(process));
        Assert.DoesNotContain(SingleInstanceScope.Current.UserSid, SingleInstanceScope.Current.Qualify("AudioPilot"));
    }

    [Fact]
    public void InstanceIdentity_IsStableWithinUserSessionAndDistinctAcrossUsersSessionsAndDiagnostics()
    {
        var scope = new SingleInstanceScope(1, "S-1-5-21-100");
        string identity = scope.Qualify("AudioPilot");
        Assert.Equal(identity, new SingleInstanceScope(1, "S-1-5-21-100").Qualify("AudioPilot"));
        Assert.NotEqual(identity, new SingleInstanceScope(2, scope.UserSid).Qualify("AudioPilot"));
        Assert.NotEqual(identity, new SingleInstanceScope(1, "S-1-5-21-200").Qualify("AudioPilot"));
        Assert.NotEqual(identity, scope.Qualify($"AudioPilot.Diagnostics.{Guid.NewGuid():N}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../AudioPilot")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-a-session")]
    public void InvalidOptInCannotFallBackToNormalInstance(string value) =>
        Assert.Throws<InvalidOperationException>(() => DiagnosticSession.Parse(value));

    [Fact]
    public void OptInIsCanonicalAndAbsenceUsesNormalInstance()
    {
        Assert.Null(DiagnosticSession.Parse(null));
        Guid id = Guid.NewGuid();
        Assert.Equal(id.ToString("N"), DiagnosticSession.Parse(id.ToString("N").ToUpperInvariant()));
    }
}

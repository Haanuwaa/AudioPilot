namespace AudioPilot.Tests.Platform;

public sealed class DiagnosticSessionTests
{
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

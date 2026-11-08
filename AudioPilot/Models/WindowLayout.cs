namespace AudioPilot.Models;

public sealed record WindowLayoutChange(int? X = null, int? Y = null, int? Width = null, int? Height = null, bool Reset = false);

public sealed record WindowLayoutSnapshot(int X, int Y, int Width, int Height, int Dpi,
    string State, bool Visible, string Source, bool Persisted)
{
    public string Units { get; } = "physical-pixels";
}

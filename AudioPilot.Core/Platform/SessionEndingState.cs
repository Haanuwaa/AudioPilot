namespace AudioPilot.Platform;

/// <summary>Tracks process-wide Windows session ending state shared across host apps and UI services.</summary>
internal static class SessionEndingState
{
    private static int _isEnding;

    internal static bool IsEnding => Volatile.Read(ref _isEnding) != 0;

    internal static void MarkEnding() => Volatile.Write(ref _isEnding, 1);

    internal static void ResetForTests() => Volatile.Write(ref _isEnding, 0);
}

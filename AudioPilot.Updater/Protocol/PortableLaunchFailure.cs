using System.ComponentModel;
using System.Text.Json;

namespace AudioPilot.UpdateProtocol;

/// <summary>Safe startup failure codes understood by the native portable launcher.</summary>
public static class PortableLaunchFailure
{
    public static int ExitCode(Exception error) => error switch
    {
        IOException io when (io.HResult & 0xffff) is 32 or 33 => 73,
        Win32Exception native when native.NativeErrorCode is 32 or 33 => 73,
        UnauthorizedAccessException => 75,
        Win32Exception native when native.NativeErrorCode == 5 => 75,
        IOException io when (io.HResult & 0xffff) is 2 or 3 => 74,
        Win32Exception native when native.NativeErrorCode is 2 or 3 or 193 => 74,
        InvalidDataException or JsonException or FileNotFoundException or DirectoryNotFoundException => 74,
        _ => 71,
    };
}

namespace AudioPilot.Models
{
    internal readonly record struct RoutineExecutionResult(
        bool Success,
        string? OutputDeviceName,
        string? InputDeviceName,
        bool AwaitingAppCompletion = false,
        bool AppOutputApplied = false,
        bool AppInputApplied = false,
        bool? OutputSucceeded = null,
        bool? InputSucceeded = null,
        bool? MasterVolumeSucceeded = null,
        bool? MicVolumeSucceeded = null,
        bool Skipped = false,
        string? OutputFailureDetail = null,
        string? InputFailureDetail = null,
        double? ElapsedMs = null,
        bool OutputReconnectAttempted = false,
        bool OutputReconnectSucceeded = false,
        bool InputReconnectAttempted = false,
        bool InputReconnectSucceeded = false)
    {
        public bool HasPerAppRoutingContinuation => AwaitingAppCompletion || AppOutputApplied || AppInputApplied;
        public bool HasPartialSuccess => (OutputSucceeded == true && InputSucceeded == false) || (OutputSucceeded == false && InputSucceeded == true);
    }
}

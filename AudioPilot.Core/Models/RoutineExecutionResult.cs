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
        bool InputReconnectSucceeded = false,
        int? RoutingProcessId = null,
        string? SkipCode = null,
        string? SkipReason = null,
        bool? OutputMuteSucceeded = null,
        bool? InputMuteSucceeded = null,
        string? MuteFailureDetail = null,
        Services.Routines.RoutineAudioRestoration? AudioRestoration = null,
        bool? CommunicationsOutputSucceeded = null,
        bool? CommunicationsInputSucceeded = null,
        string? CommunicationsFailureDetail = null,
        bool OwnsRoleRestoration = false,
        bool OwnsVolumeRestoration = false,
        bool OnlyExpectedActionsFailed = false,
        bool AudioChanged = false)
    {
        public bool HasPerAppRoutingContinuation => AwaitingAppCompletion || AppOutputApplied || AppInputApplied;
        internal bool IsExpectedAutomaticSkip => Skipped || SkipCode != null || OnlyExpectedActionsFailed;
        internal bool CanPlayCompletionSound => Success && !Skipped && SkipCode == null && !AwaitingAppCompletion && AudioChanged;
        public bool HasPartialSuccess =>
            (CommunicationsOutputSucceeded == true || CommunicationsInputSucceeded == true || OutputSucceeded == true || InputSucceeded == true || MasterVolumeSucceeded == true || MicVolumeSucceeded == true || OutputMuteSucceeded == true || InputMuteSucceeded == true) &&
            (CommunicationsOutputSucceeded == false || CommunicationsInputSucceeded == false || OutputSucceeded == false || InputSucceeded == false || MasterVolumeSucceeded == false || MicVolumeSucceeded == false || OutputMuteSucceeded == false || InputMuteSucceeded == false);
    }
}

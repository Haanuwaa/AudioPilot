namespace AudioPilot.Services.UI
{
    public sealed class MediaOverlayCommandService
    {
        private readonly MediaOverlayEngine _engine;

        public MediaOverlayCommandService()
            : this(new MediaOverlayEngine())
        {
        }

        internal MediaOverlayCommandService(MediaOverlayEngine engine)
        {
            _engine = engine;
        }

        internal Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(
            MediaOverlayCommand command,
            Func<Task<bool>> sendCommandAsync,
            Func<string?>? commandTargetSourceProvider,
            CancellationToken cancellationToken,
            Func<MediaKeyHelper.MediaCommandSendOutcome?>? commandOutcomeProvider = null)
        {
            return _engine.SendWithDetailedResultAsync(command, sendCommandAsync, commandTargetSourceProvider, cancellationToken, commandOutcomeProvider);
        }

        public Task<MediaOverlaySessionSnapshot> GetCurrentMediaSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return _engine.GetCurrentMediaSnapshotAsync(cancellationToken);
        }

        internal Task<MediaOverlaySessionSnapshot> GetCurrentMediaSnapshotAsync(string? commandTargetSource, CancellationToken cancellationToken)
        {
            return _engine.GetCurrentMediaSnapshotAsync(commandTargetSource, cancellationToken);
        }
    }
}

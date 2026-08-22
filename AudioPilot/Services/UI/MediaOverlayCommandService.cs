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

        public Task<MediaOverlayResult> SendWithBestEffortOverlayAsync(MediaOverlayCommand command, Func<bool> sendCommand)
        {
            return _engine.SendWithBestEffortOverlayAsync(command, sendCommand);
        }

        public Task<MediaOverlayResult> SendWithBestEffortOverlayAsync(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync)
        {
            return _engine.SendWithBestEffortOverlayAsync(command, sendCommandAsync);
        }

        internal Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(MediaOverlayCommand command, Func<bool> sendCommand)
        {
            return _engine.SendWithDetailedResultAsync(command, sendCommand);
        }

        internal Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync)
        {
            return _engine.SendWithDetailedResultAsync(command, sendCommandAsync);
        }

        internal Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync, Func<string?>? commandTargetSourceProvider)
        {
            return _engine.SendWithDetailedResultAsync(command, sendCommandAsync, commandTargetSourceProvider);
        }

        internal Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(
            MediaOverlayCommand command,
            Func<Task<bool>> sendCommandAsync,
            Func<string?>? commandTargetSourceProvider,
            CancellationToken cancellationToken)
        {
            return _engine.SendWithDetailedResultAsync(command, sendCommandAsync, commandTargetSourceProvider, cancellationToken);
        }

        public Task<MediaOverlaySessionSnapshot> GetCurrentMediaSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return _engine.GetCurrentMediaSnapshotAsync(cancellationToken);
        }
    }
}

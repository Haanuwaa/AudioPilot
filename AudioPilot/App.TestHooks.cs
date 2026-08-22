namespace AudioPilot
{
    public partial class App
    {
        internal bool IsUnobservedTaskExceptionHandlerRegisteredForTests => _unobservedTaskExceptionHandlerRegistered;

        internal SingleInstanceHelper? AttachedSingleInstanceHelperForTests => _singleInstanceWithLifetimeHandlers;

        internal void AttachSingleInstanceLifetimeHandlersForTests(SingleInstanceHelper singleInstance)
            => AttachSingleInstanceLifetimeHandlers(singleInstance);

        internal void DetachLifetimeEventHandlersForTests() => DetachLifetimeEventHandlers();
    }
}

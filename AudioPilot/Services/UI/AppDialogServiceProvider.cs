using System.Windows;

namespace AudioPilot.Services.UI
{
    internal static class AppDialogServiceProvider
    {
        private static readonly Lazy<IAppDialogService> Fallback = new(
            static () => new AppDialogService(),
            LazyThreadSafetyMode.ExecutionAndPublication);

        public static IAppDialogService Current =>
            (Application.Current as App)?.DialogService ?? Fallback.Value;

        internal static async ValueTask DisposeFallbackAsync()
        {
            if (Fallback.IsValueCreated)
            {
                await Fallback.Value.DisposeAsync();
            }
        }
    }
}

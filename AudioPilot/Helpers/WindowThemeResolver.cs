using System.Windows;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Helpers
{
    internal static class WindowThemeResolver
    {
        private static Func<AppTheme>? _applicationThemeProvider;

        internal static void SetApplicationThemeProvider(Func<AppTheme>? themeProvider)
        {
            Volatile.Write(ref _applicationThemeProvider, themeProvider);
        }

        public static void ApplyWindowTheme(Window window, AppTheme theme)
        {
            ArgumentNullException.ThrowIfNull(window);

            if (AppDispatcherHelper.IsDispatcherUnavailable(window.Dispatcher))
            {
                return;
            }

            if (window.Dispatcher.CheckAccess())
            {
                WindowThemeHelper.ApplyTheme(window, theme);
                _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(window);
                return;
            }

            try
            {
                window.Dispatcher.Invoke(() => ApplyWindowTheme(window, theme));
            }
            catch (OperationCanceledException) when (AppDispatcherHelper.IsDispatcherUnavailable(window.Dispatcher))
            {
            }
            catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(window.Dispatcher))
            {
            }
        }

        public static void ApplyApplicationTheme(AppTheme theme)
        {
            Application? application = Application.Current;
            if (application == null)
            {
                return;
            }

            if (application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (application.Dispatcher.CheckAccess())
            {
                ApplyApplicationThemeOnDispatcher(application, theme);
                return;
            }

            try
            {
                application.Dispatcher.Invoke(() => ApplyApplicationTheme(theme));
            }
            catch (OperationCanceledException) when (AppDispatcherHelper.IsDispatcherUnavailable(application.Dispatcher))
            {
            }
            catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(application.Dispatcher))
            {
            }
        }

        public static void ApplyOwnerOrMainWindowTheme(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);

            AppTheme theme = AppTheme.System;
            if (window.Owner?.DataContext is AppViewModel ownerViewModel)
            {
                theme = ownerViewModel.Theme;
            }
            else if (TryGetApplicationMainWindowTheme(out AppTheme mainWindowTheme))
            {
                theme = mainWindowTheme;
            }
            else if (Volatile.Read(ref _applicationThemeProvider) is { } themeProvider)
            {
                try
                {
                    theme = themeProvider();
                }
                catch (Exception)
                {
                    theme = AppTheme.System;
                }
            }

            ApplyWindowTheme(window, theme);
        }

        private static void ApplyApplicationThemeOnDispatcher(Application application, AppTheme theme)
        {
            AppTheme effectiveTheme = WindowThemeHelper.ApplyApplicationThemeResources(theme);
            bool highContrast = SystemParameters.HighContrast;
            foreach (Window window in application.Windows)
            {
                if (window.AllowsTransparency)
                {
                    continue;
                }

                WindowThemeHelper.ApplyWindowChrome(window, effectiveTheme, highContrast);
                _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(window);
            }
        }

        private static bool TryGetApplicationMainWindowTheme(out AppTheme theme)
        {
            theme = AppTheme.System;

            Application? application = Application.Current;
            if (application == null)
            {
                return false;
            }

            if (application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
            {
                return false;
            }

            if (application.Dispatcher.CheckAccess())
            {
                return TryGetApplicationMainWindowThemeOnDispatcher(application, out theme);
            }

            return false;
        }

        private static bool TryGetApplicationMainWindowThemeOnDispatcher(Application application, out AppTheme theme)
        {
            theme = AppTheme.System;

            if (application.MainWindow?.DataContext is not AppViewModel mainWindowViewModel)
            {
                return false;
            }

            theme = mainWindowViewModel.Theme;
            return true;
        }
    }
}

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

            if (window.Dispatcher.CheckAccess())
            {
                WindowThemeHelper.ApplyTheme(window, theme);
                _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(window);
                return;
            }

            window.Dispatcher.Invoke(() =>
            {
                WindowThemeHelper.ApplyTheme(window, theme);
                _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(window);
            });
        }

        public static void ApplyApplicationMainWindowTheme(AppTheme theme)
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
                ApplyApplicationMainWindowThemeOnDispatcher(application, theme);
                return;
            }

            application.Dispatcher.Invoke(() => ApplyApplicationMainWindowThemeOnDispatcher(application, theme));
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

        private static void ApplyApplicationMainWindowThemeOnDispatcher(Application application, AppTheme theme)
        {
            WindowThemeHelper.ApplyApplicationThemeResources(theme);
            if (application.MainWindow != null)
            {
                ApplyWindowTheme(application.MainWindow, theme);
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

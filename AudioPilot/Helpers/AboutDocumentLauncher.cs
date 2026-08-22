using System.Diagnostics;
using System.IO;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Helpers
{
    internal static class AboutDocumentLauncher
    {
        internal static string ResolveTarget(
            string baseDirectory,
            Func<string, bool>? fileExists = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

            string localPath = Path.Combine(baseDirectory, AppConstants.Files.AboutFileName);
            return (fileExists ?? File.Exists)(localPath)
                ? localPath
                : AppConstants.Links.UserGuideUrl;
        }

        public static bool TryOpen(ILogger logger, string owner)
        {
            ArgumentNullException.ThrowIfNull(logger);

            string target = ResolveTarget(AppContext.BaseDirectory);
            string targetKind = Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && !uri.IsFile
                ? "online-user-guide"
                : "local-about";

            try
            {
                Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true
                });
                logger.Debug(owner, () => $"about-opened | target={targetKind}", nameof(TryOpen));
                return true;
            }
            catch (Exception ex)
            {
                logger.Warning(
                    owner,
                    () => $"about-open-failed | target={targetKind} reason={ex.GetType().Name}",
                    nameof(TryOpen),
                    ex);
                return false;
            }
        }
    }
}

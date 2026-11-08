using System.IO;
using System.Text.Json;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Updates;

/// <summary>Remembers the newest notified release. The update worker owns and serializes all calls and disk access.</summary>
internal sealed class UpdateNotificationService(Logger logger, Func<Version, CancellationToken, Task<bool>> deliver, string? path = null)
{
    private Version? _notifiedVersion;
    private bool _loaded;

    /// <summary>Returns true for accepted or previously delivered alerts; false permits a bounded retry.</summary>
    internal async Task<bool> NotifyAsync(Version version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool reserved = false;
        bool accepted = false;
        try
        {
            path ??= Path.Combine(AppDataPaths.GetLocalStateRoot(), AppConstants.Files.UpdateNotificationFileName);
            if (!_loaded)
            {
                _notifiedVersion = ReadVersion(path);
                _loaded = true;
            }
            if (_notifiedVersion != null && version <= _notifiedVersion) return true;

            cancellationToken.ThrowIfCancellationRequested();
            // Reserve durably before showing: a crash immediately after delivery must not repeat the alert at restart.
            AtomicFileWriter.WriteAllText(path, JsonSerializer.Serialize(version.ToString()));
            reserved = true;
            cancellationToken.ThrowIfCancellationRequested();
            accepted = await deliver(version, cancellationToken).ConfigureAwait(false);
            if (accepted) _notifiedVersion = version;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Debug("UpdateCheck", () => $"update-notification-unavailable | error={ex.GetType().Name}");
        }
        finally
        {
            if (reserved && !accepted)
            {
                try
                {
                    if (_notifiedVersion == null) File.Delete(path!);
                    else AtomicFileWriter.WriteAllText(path!, JsonSerializer.Serialize(_notifiedVersion.ToString()));
                }
                catch (Exception ex)
                {
                    logger.Debug("UpdateCheck", () => $"update-notification-reservation-release-failed | error={ex.GetType().Name}");
                }
            }
        }
        return accepted;
    }

    private static Version? ReadVersion(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[128];
            int length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (length == buffer.Length) return null;
            return Version.TryParse(JsonSerializer.Deserialize<string>(buffer[..length]), out Version? version) ? version : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }
}

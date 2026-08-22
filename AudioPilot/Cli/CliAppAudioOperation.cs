using System.Diagnostics;
using System.Globalization;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Cli;

/// <summary>Runs process-scoped playback commands identically in the GUI and headless CLI.</summary>
internal static class CliAppAudioOperation
{
    internal interface ISession : IDisposable
    {
        int ProcessId { get; }
        string ProcessName { get; }
        bool IsCurrent { get; }
        (float Volume, bool Muted) Read();
        void SetVolume(float scalar);
        void SetMute(bool muted);
    }

    private sealed class NativeSession(AudioSessionControl session, int pid, string name, long started) : ISession
    {
        public int ProcessId => pid;
        public string ProcessName => name;
        public bool IsCurrent
        {
            get
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return process.StartTime.ToUniversalTime().Ticks == started
                        && session.State != AudioSessionState.AudioSessionStateExpired;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            }
        }
        public (float Volume, bool Muted) Read() => (session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute);
        public void SetVolume(float scalar) => session.SimpleAudioVolume.Volume = scalar;
        public void SetMute(bool muted) => session.SimpleAudioVolume.Mute = muted;
        public void Dispose() => session.Dispose();
    }

    internal static Task<CliExecutionResult> ExecuteAsync(CliCommand command, CancellationToken token) =>
        ComThreadingHelper.RunOnCoreAudioThreadAsync(() => Execute(command, cancellation => CollectSessions(command.Key, cancellation), token), token);

    internal static CliExecutionResult Execute(CliCommand command, Func<CancellationToken, IReadOnlyList<ISession>> collect, CancellationToken token = default)
    {
        if (!TryValidate(command, out float amount))
            return Error(command, 2, "app-audio-invalid-command", "Invalid application audio command. See help app.");

        IReadOnlyList<ISession> sessions = [];
        try
        {
            token.ThrowIfCancellationRequested();
            sessions = collect(token);
            ISession[] selected;
            if (command.Action == CliAction.AppList) selected = [.. sessions];
            else
            {
                bool byPid = command.Key!.StartsWith("pid:", StringComparison.Ordinal);
                int processId = byPid ? int.Parse(command.Key.AsSpan(4), CultureInfo.InvariantCulture) : 0;
                selected = [.. sessions.Where(s => byPid
                    ? s.ProcessId == processId
                    : string.Equals(s.ProcessName, command.Key[5..], StringComparison.OrdinalIgnoreCase))];
                if (selected.Select(s => s.ProcessId).Distinct().Take(2).Count() > 1)
                    return Error(command, 5, "app-audio-ambiguous-process", "Several playback processes match. Use app list and select one --pid.");
                if (selected.Length == 0)
                    return Error(command, 5, "app-audio-no-session", "No playback sessions match. Use app list to find current playback processes.");
            }

            bool readOnly = command.Action is CliAction.AppList or CliAction.AppVolumeGet;
            int applied = 0, failed = 0;
            var initial = new List<(ISession Session, float Volume, bool Muted)>();
            foreach (ISession session in selected)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!session.IsCurrent)
                    {
                        if (!readOnly) return Error(command, 5, "app-audio-session-changed", "A playback process or session ended. List the sessions again.");
                        throw new InvalidOperationException("Playback process or session ended.");
                    }
                    var (Volume, Muted) = session.Read();
                    if (!float.IsFinite(Volume) || Volume is < 0 or > 1)
                        throw new InvalidOperationException("Invalid session volume.");
                    initial.Add((session, Volume, Muted));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (readOnly)
                {
                    failed++;
                    Logger.Instance.Debug("AppAudio", $"app-audio-read-failed | action={command.Action} hresult=0x{ex.HResult:X8}");
                }
            }

            bool mute = command.Action == CliAction.AppMuteOn
                || (command.Action == CliAction.AppMuteToggle && !initial.All(s => s.Muted));
            var final = new List<(ISession Session, float Volume, bool Muted)>();
            foreach (var (Session, Volume, Muted) in initial)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!readOnly)
                    {
                        if (!Session.IsCurrent) throw new InvalidOperationException("Playback process or session ended.");
                        var latest = Session.Read();
                        if (!float.IsFinite(latest.Volume) || latest.Volume is < 0 or > 1)
                            throw new InvalidOperationException("Invalid session volume.");
                        token.ThrowIfCancellationRequested();
                        if (command.Action is CliAction.AppVolumeSet or CliAction.AppVolumeAdjust)
                        {
                            float target = command.Action == CliAction.AppVolumeSet ? amount / 100f : Math.Clamp(latest.Volume + amount / 100f, 0f, 1f);
                            if (target != latest.Volume) Session.SetVolume(target);
                        }
                        else if (latest.Muted != mute) Session.SetMute(mute);
                        applied++;
                    }
                    var current = readOnly ? (Volume, Muted) : Session.Read();
                    if (!float.IsFinite(current.Volume) || current.Volume is < 0 or > 1)
                        throw new InvalidOperationException("Invalid session volume.");
                    final.Add((Session, current.Volume, current.Muted));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    Logger.Instance.Debug("AppAudio", $"app-audio-session-failed | action={command.Action} hresult=0x{ex.HResult:X8}");
                }
            }
            string code = failed == 0 ? "app-audio-success" : applied > 0 || final.Count > 0 ? "app-audio-partial" : "app-audio-failed";
            var apps = final.GroupBy(s => s.Session.ProcessId).OrderBy(g => g.Key).Select(g => new
            {
                ProcessId = command.RedactOutput ? (int?)null : g.Key,
                ProcessName = command.RedactOutput ? "[redacted]" : g.First().Session.ProcessName,
                SessionCount = g.Count(),
                Percent = g.Max(s => s.Volume) * 100f,
                Muted = g.All(s => s.Muted),
            }).ToArray();
            Logger.Instance.Debug("AppAudio", $"app-audio-result | action={command.Action} code={code} sessions={selected.Length} applied={applied} failed={failed}");
            string output = command.JsonOutput
                ? CliOutputFormatter.SerializeCliJson(new { Success = failed == 0, DiagCode = code, AppliedSessions = applied, FailedSessions = failed, Apps = apps })
                : (apps.Length == 0 ? (failed == 0 ? "No playback sessions." : "No readable playback sessions.") : string.Join(Environment.NewLine, apps.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.ProcessName} (PID {a.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "redacted"}): {a.Percent:0.##}%, {(a.Muted ? "muted" : "unmuted")}, {a.SessionCount} session(s)"))))
                    + (failed == 0 ? string.Empty : $"{Environment.NewLine}{code}: {failed} session(s) failed; " + (readOnly ? "results may be incomplete." : "some changes may already have applied."));
            return new(failed == 0 ? 0 : 3, output);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.Instance.Warning("AppAudio", "app-audio-failed", nameof(Execute), ex);
            return Error(command, 3, "app-audio-failed", "Could not inspect playback sessions. No application volume command was completed.");
        }
        finally
        {
            DisposeSessions(sessions);
        }
    }

    private static bool TryValidate(CliCommand command, out float amount)
    {
        amount = 0;
        string[] prefix = command.Action switch
        {
            CliAction.AppList => ["app", "list"],
            CliAction.AppVolumeGet => ["app", "volume", "get"],
            CliAction.AppVolumeSet => ["app", "volume", "set", command.Value ?? string.Empty],
            CliAction.AppVolumeAdjust => ["app", "volume", "adjust", command.Value ?? string.Empty],
            CliAction.AppMuteOn => ["app", "mute", "on"],
            CliAction.AppMuteOff => ["app", "mute", "off"],
            CliAction.AppMuteToggle => ["app", "mute", "toggle"],
            _ => [],
        };
        if (prefix.Length == 0) return false;
        if (command.Action != CliAction.AppList)
        {
            if (command.Key?.StartsWith("pid:", StringComparison.Ordinal) == true) prefix = [.. prefix, "--pid", command.Key[4..]];
            else if (command.Key?.StartsWith("name:", StringComparison.Ordinal) == true) prefix = [.. prefix, "--process", command.Key[5..]];
            else return false;
        }
        else if (command.Key != null) return false;
        if (!CliCommand.TryParse(prefix, out var parsed, out _) || parsed.Key != command.Key) return false;
        if (parsed.Value != null) amount = float.Parse(parsed.Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static CliExecutionResult Error(CliCommand command, int exitCode, string code, string message) =>
        CliCommandExecutor.BuildExecutionFailureResult(exitCode, code, message, command.JsonOutput);

    private static List<ISession> CollectSessions(string? selector, CancellationToken token)
    {
        var result = new List<ISession>();
        int? selectedPid = selector?.StartsWith("pid:", StringComparison.Ordinal) == true
            ? int.Parse(selector.AsSpan(4), CultureInfo.InvariantCulture) : null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < devices.Count; d++)
            {
                token.ThrowIfCancellationRequested();
                using var device = devices[d];
                using var manager = device.AudioSessionManager;
                using var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    AudioSessionControl? session = sessions[i];
                    try
                    {
                        uint pid = session.GetProcessID;
                        if (pid == 0 || pid > int.MaxValue) continue;
                        if (selectedPid.HasValue && pid != selectedPid.Value) continue;
                        if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired) continue;
                        using var process = Process.GetProcessById((int)pid);
                        result.Add(new NativeSession(session, (int)pid, process.ProcessName, process.StartTime.ToUniversalTime().Ticks));
                        session = null;
                    }
                    catch (ArgumentException) { }
                    finally { session?.Dispose(); }
                }
            }
            return result;
        }
        catch
        {
            DisposeSessions(result);
            throw;
        }
    }

    private static void DisposeSessions(IEnumerable<ISession> sessions)
    {
        foreach (ISession session in sessions)
        {
            try { session.Dispose(); }
            catch (Exception ex) { Logger.Instance.Debug("AppAudio", $"app-audio-dispose-failed | hresult=0x{ex.HResult:X8}"); }
        }
    }
}

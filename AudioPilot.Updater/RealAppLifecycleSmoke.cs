using System.Diagnostics;
using System.Text.Json.Nodes;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Updater;

/// <summary>Exercises actual AudioPilot startup/shutdown in a disposable guest, without audio input or user configuration.</summary>
internal static class RealAppLifecycleSmoke
{
    public static async Task RunAsync(string appPath, string fixturePath, OwnedWorkspace workspace)
    {
        MsiDeployment.RequireSandbox();
        appPath = Path.GetFullPath(appPath);
        string diagnosticId = Guid.NewGuid().ToString("N");
        string dataRoot = Path.Combine(Path.GetTempPath(), "AudioPilot.Diagnostics", diagnosticId);
        Directory.CreateDirectory(dataRoot);
        await PrepareSettingsAsync(fixturePath, dataRoot);
        string settingsPath = Path.Combine(dataRoot, "settings.json");
        var results = new List<object>();
        try
        {
            bool previousVisible;
            await using (var original = await SupervisedApp.LaunchAsync(appPath, diagnosticId, restart: false, visible: false))
            {
                var ready = await original.ReadAsync();
                Require(ready.Kind == "ready", "Original app did not become ready.");
                previousVisible = ready.Visible;
                Require(previousVisible, "The unconfigured original did not present its real main window.");
                await original.PrepareAsync();
                await original.SendAsync("cancel");
                Require((await original.ReadAsync()).Kind == "cancelled", "Preparation cancellation failed.");
                await original.SendAsync("exercise-autosave");
                Require((await original.ReadAsync()).Kind == "edited", "The real UI edit did not complete.");
                await original.StopNormallyAsync();
                string? theme = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))?["Theme"]?.GetValue<string>();
                Require(theme == "Light", "Final autosave did not persist the accepted edit.");
                results.Add(new { stage = "original", preparedAndCancelled = true, finalAutosave = true, previousVisible });
            }
            string committedSettings = await File.ReadAllTextAsync(settingsPath);
            await using (var failed = await SupervisedApp.LaunchAsync(appPath, diagnosticId, restart: true, previousVisible))
            {
                Require((await failed.ReadAsync()).Kind == "staged-ready", "Candidate did not reach the real startup gate.");
                await failed.SendAsync("abort");
                await failed.Process.WaitForExitAsync(failed.Token);
                Require(failed.Process.ExitCode != 0, "Aborted candidate reported successful startup.");
                Require(await File.ReadAllTextAsync(settingsPath) == committedSettings, "Uncommitted candidate changed saved settings.");
                results.Add(new { stage = "failed-candidate", rejected = true, configurationUnchanged = true });
            }
            await using (var recovered = await SupervisedApp.LaunchAsync(appPath, diagnosticId, restart: true, previousVisible))
            {
                Require((await recovered.ReadAsync()).Kind == "staged-ready", "Recovery did not reach the real startup gate.");
                var ready = await recovered.CommitAsync();
                Require(ready.Kind == "ready" && ready.Visible == previousVisible, "Recovery did not preserve visibility.");
                await recovered.StopNormallyAsync();
                Require(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))?["Theme"]?.GetValue<string>() == "Light", "Recovery lost saved edits.");
                results.Add(new { stage = "recovery", committed = true, visibilityRestored = true, settingsPreserved = true });
            }
            await using (var disconnected = await SupervisedApp.LaunchAsync(appPath, diagnosticId, restart: true, previousVisible))
            {
                Require((await disconnected.ReadAsync()).Kind == "staged-ready", "Disconnect probe did not reach the startup gate.");
                await disconnected.CommitAsync();
                await disconnected.PrepareAsync();
                disconnected.Disconnect();
                bool resumed = false;
                for (int attempt = 0; attempt < 20 && !resumed; attempt++)
                {
                    resumed = await RunCliAsync(appPath, diagnosticId, "status", disconnected.Token);
                    if (!resumed) await Task.Delay(100, disconnected.Token);
                }
                Require(resumed, "Helper disconnection did not release update admission.");
                Require(await RunCliAsync(appPath, diagnosticId, "exit", disconnected.Token), "Normal exit was not accepted after disconnection.");
                await disconnected.Process.WaitForExitAsync(disconnected.Token);
                Require(disconnected.Process.ExitCode == 0, "Disconnected app did not shut down normally.");
                results.Add(new { stage = "disconnected-helper", admissionRestored = true, normalExit = true });
            }
            await using (var hidden = await SupervisedApp.LaunchAsync(appPath, diagnosticId, restart: true, visible: false))
            {
                await hidden.WaitStagedAsync();
                var ready = await hidden.CommitAsync();
                Require(!ready.Visible, "A tray-mode restart unexpectedly revealed its main window.");
                await hidden.StopNormallyAsync();
                results.Add(new { stage = "hidden-restart", visibilityRestored = true, normalExit = true });
            }
            string earlyExit = workspace.Resolve("exits-before-connect");
            Directory.CreateDirectory(earlyExit);
            foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(earlyExit, Path.GetFileName(file)));
            File.Copy(Path.Combine(earlyExit, "AudioPilot.Updater.dll"), Path.Combine(earlyExit, "AudioPilot.dll"));
            bool exitedBeforeConnection = false;
            try
            {
                await using var unexpected = await SupervisedApp.LaunchAsync(Path.Combine(earlyExit, "AudioPilot.Updater.exe"), diagnosticId, true, false);
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { exitedBeforeConnection = true; }
            Require(exitedBeforeConnection, "An exited child was accepted without a lifecycle connection.");
            results.Add(new { stage = "helper-fixture-exits-before-connect", rejected = true });
            OwnedWorkspace.WriteJson(workspace.Resolve("real-app-results.json"), results);
        }
        finally
        {
            foreach (string name in new[] { "AudioPilot.log", "settings.json", "window-placement.json" })
            {
                string source = Path.Combine(dataRoot, name);
                if (File.Exists(source)) File.Copy(source, workspace.Resolve(name), overwrite: true);
            }
        }
    }

    internal static async Task PrepareSettingsAsync(string fixturePath, string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))!.AsObject();
        settings["Theme"] = "Dark";
        settings["RunAtStartup"] = false;
        ClearStrings(settings["Hotkeys"]!);
        settings["Hotkeys"]!["App"]!["ShowAudioStatus"] = "";
        settings["Hotkeys"]!["App"]!["QuickDevicePicker"] = "";
        settings["Hotkeys"]!["Mute"]!["PushToTalkEnabled"] = false;
        foreach (string direction in new[] { "Output", "Input" })
        {
            settings["DeviceSwitching"]![direction]!["CycleDevices"] = new JsonArray();
            settings["DeviceSwitching"]![direction]!["HotkeysEnabled"] = false;
            settings["DeviceSwitching"]![direction]!["SwitchHotkey"] = "";
            settings["DeviceSwitching"]![direction]!["ReverseSwitchHotkey"] = "";
        }
        settings["Routines"]!["Items"] = new JsonArray();
        settings["Miscellaneous"]!["CheckForUpdates"] = false;
        settings["Miscellaneous"]!["AutoSaveEnabled"] = true;
        settings["Miscellaneous"]!["PlayAppSounds"] = false;
        settings["AdvancedTuning"]!["AutoSaveDebounceMs"] = 5000;
        string settingsPath = Path.Combine(dataRoot, "settings.json");
        await File.WriteAllTextAsync(settingsPath, settings.ToJsonString());
    }

    private static void ClearStrings(JsonNode node)
    {
        if (node is not JsonObject obj) return;
        foreach (string key in obj.Select(pair => pair.Key).ToArray())
        {
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out _)) obj[key] = "";
            else if (obj[key] is { } child) ClearStrings(child);
        }
    }

    private static async Task<bool> RunCliAsync(string app, string diagnosticId, string action, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(app)!, "AudioPilot.Cli.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(action);
        start.ArgumentList.Add("--json");
        start.Environment["AUDIOPILOT_DIAGNOSTIC_SESSION"] = diagnosticId;
        start.Environment.Remove(UpdateLifecycleWire.EnvironmentVariable);
        using var cli = Process.Start(start) ?? throw new InvalidOperationException("Lifecycle CLI probe did not start.");
        var output = cli.StandardOutput.ReadToEndAsync(token);
        var errors = cli.StandardError.ReadToEndAsync(token);
        await cli.WaitForExitAsync(token);
        await Task.WhenAll(output, errors);
        return cli.ExitCode == 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

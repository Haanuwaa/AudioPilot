using System.IO;

namespace AudioPilot.Updater.Ui;

/// <summary>Removes only this launch's temporary UI log after its writer has actually drained.</summary>
internal sealed class UpdaterTemporaryDiagnostics
{
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string Root { get; }

    internal UpdaterTemporaryDiagnostics(string? parent = null)
    {
        Root = Path.Combine(parent ?? Path.Combine(Path.GetTempPath(), "AudioPilot.Diagnostics"), Id);
        OwnedWorkspace.CheckAncestors(Root);
        if (Path.Exists(Root)) throw new IOException("The temporary diagnostic directory already exists.");
    }

    internal void Cleanup(bool writerDrained)
    {
        if (!writerDrained) return;
        try
        {
            OwnedWorkspace.CheckAncestors(Root);
            if (!Directory.Exists(Root)) return;
            string backups = Path.Combine(Root, "backups");
            var files = new List<string>();
            foreach (string path in Directory.EnumerateFileSystemEntries(Root))
            {
                OwnedWorkspace.CheckAncestors(path);
                if (path == backups && Directory.Exists(path))
                {
                    foreach (string backup in Directory.EnumerateFileSystemEntries(path))
                    {
                        OwnedWorkspace.CheckAncestors(backup);
                        if (Directory.Exists(backup) || !IsLogBackup(Path.GetFileName(backup))) return;
                        files.Add(backup);
                    }
                }
                else if (!Directory.Exists(path) && Path.GetFileName(path) == "AudioPilot.log") files.Add(path);
                else return;
            }
            foreach (string file in files) { OwnedWorkspace.CheckAncestors(file); File.Delete(file); }
            if (Directory.Exists(backups)) Directory.Delete(backups);
            Directory.Delete(Root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { UpdateStorage.ReportDeferred(error); }
    }

    private static bool IsLogBackup(string name) => name == "AudioPilot.log.bak" ||
        name.StartsWith("AudioPilot.log.bak.", StringComparison.Ordinal) &&
        int.TryParse(name.AsSpan(19), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) && index > 0;
}

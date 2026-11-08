function Expand-AudioPilotMsiPayload {
    param([string]$Path, [string]$Destination, [Collections.Generic.Dictionary[string, string]]$FileIds, [hashtable]$FileSizes)
    if (-not ('AudioPilot.Packaging.MsiCabinetReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
namespace AudioPilot.Packaging {
    public static class MsiCabinetReader {
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint MsiOpenDatabaseW(string path, IntPtr mode, out uint database);
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
        [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewExecute(uint view, uint record);
        [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewFetch(uint view, out uint record);
        [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiRecordReadStream(uint record, uint field, byte[] buffer, ref uint count);
        [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiCloseHandle(uint handle);
        private static void Check(uint error) { if (error != 0) throw new IOException("MSI cabinet read failed: " + error); }
        public static void Export(string path, string cabinet, string destination) {
            uint database = 0, view = 0, record = 0;
            try {
                Check(MsiOpenDatabaseW(path, IntPtr.Zero, out database));
                Check(MsiDatabaseOpenViewW(database, "SELECT `Data` FROM `_Streams` WHERE `Name`='" + cabinet.Replace("'", "''") + "'", out view));
                Check(MsiViewExecute(view, 0));
                Check(MsiViewFetch(view, out record));
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) {
                    var buffer = new byte[65536];
                    while (true) {
                        uint count = (uint)buffer.Length;
                        Check(MsiRecordReadStream(record, 1, buffer, ref count));
                        if (count == 0) break;
                        output.Write(buffer, 0, checked((int)count));
                    }
                }
            } finally {
                if (record != 0) MsiCloseHandle(record);
                if (view != 0) MsiCloseHandle(view);
                if (database != 0) MsiCloseHandle(database);
            }
        }
    }
}
'@
    }
    $media = @(Invoke-AudioPilotMsiQuery -Path $Path -Query 'SELECT `Cabinet` FROM `Media`' -Columns @('Cabinet'))
    if ($media.Count -eq 0) { throw 'MSI payload has no cabinets.' }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $extracted = @{}
    for ($index = 0; $index -lt $media.Count; $index++) {
        $cabinet = $media[$index].Cabinet
        if ($cabinet -notmatch '^#[A-Za-z_][A-Za-z0-9_.]*$') { throw 'Release MSI payload requires embedded cabinets.' }
        $cabPath = Join-Path $Destination "$index.cab"
        [AudioPilot.Packaging.MsiCabinetReader]::Export([IO.Path]::GetFullPath($Path), $cabinet.Substring(1), $cabPath)
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($cabPath))
        $ids = [Collections.Generic.List[string]]::new()
        try {
            if ($reader.ReadUInt32() -ne 0x4643534D) { throw 'Invalid MSI cabinet signature.' }
            $reader.BaseStream.Position = 16
            $fileOffset = $reader.ReadUInt32()
            $reader.BaseStream.Position = 28
            $fileCount = $reader.ReadUInt16()
            if ($fileOffset -lt 36 -or $fileOffset -ge $reader.BaseStream.Length -or $fileCount -gt $FileIds.Count) { throw 'Invalid MSI cabinet file table.' }
            $reader.BaseStream.Position = $fileOffset
            for ($fileIndex = 0; $fileIndex -lt $fileCount; $fileIndex++) {
                $size = $reader.ReadUInt32()
                $reader.ReadBytes(12) | Out-Null
                $name = [Collections.Generic.List[byte]]::new()
                while (($value = $reader.ReadByte()) -ne 0) {
                    if ($name.Count -ge 72) { throw 'Invalid MSI cabinet file identifier.' }
                    $name.Add($value)
                }
                $id = [Text.Encoding]::ASCII.GetString($name.ToArray())
                if ($id -cnotmatch '^[A-Za-z_][A-Za-z0-9_.]*$' -or -not $FileIds.ContainsKey($id) -or -not $seen.Add($id)) {
                    throw 'MSI cabinet contains an unexpected, duplicate, or unsafe file identifier.'
                }
                if ($size -ne $FileSizes[$FileIds[$id]]) { throw 'MSI cabinet file size differs from its payload metadata.' }
                $ids.Add($id)
            }
        }
        finally { $reader.Dispose() }
        $directory = Join-Path $Destination "$index"
        New-Item -ItemType Directory -Path $directory | Out-Null
        $output = & (Join-Path $env:SystemRoot 'System32/expand.exe') '-R' '-F:*' $cabPath $directory 2>&1
        if ($LASTEXITCODE -ne 0) { throw "MSI cabinet extraction failed: $output" }
        foreach ($id in $ids) {
            $file = Join-Path $directory $id
            if (-not [IO.File]::Exists($file)) { throw 'MSI cabinet extraction omitted a payload file.' }
            $extracted[$FileIds[$id]] = $file
        }
    }
    if ($seen.Count -ne $FileIds.Count) { throw 'MSI cabinets do not contain every payload file.' }
    return $extracted
}

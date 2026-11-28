using System.Text;

namespace AudioPilot.Services.Configuration
{
    internal static class AtomicFileWriter
    {
        public static void WriteAllText(string path, string content)
        {
            Write(path, tempPath =>
            {
                using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            });
        }

        public static void Write(string path, Action<string> writeTemporaryFile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(writeTemporaryFile);

            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string fileName = Path.GetFileName(fullPath);
            // Keep the temporary file beside the destination so replacement stays on the same volume.
            string tempPath = Path.Combine(directory ?? string.Empty, $"{fileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                writeTemporaryFile(tempPath);
                try
                {
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (FileNotFoundException)
                {
                    File.Move(tempPath, fullPath, overwrite: true);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}

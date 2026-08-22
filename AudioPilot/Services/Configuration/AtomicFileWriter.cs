using System.IO;
using System.Text;

namespace AudioPilot.Services.Configuration
{
    internal static class AtomicFileWriter
    {
        public static void WriteAllText(string path, string content)
        {
            Write(path, tempPath => File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
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
            string tempPath = Path.Combine(directory ?? string.Empty, $"{fileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                writeTemporaryFile(tempPath);
                if (File.Exists(fullPath))
                {
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, fullPath);
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

using System.Diagnostics;

namespace AudioPilot.Platform
{
    internal static class ProcessEnumerationHelper
    {
        internal static void EnumerateProcesses(Action<Process> visitor)
        {
            ArgumentNullException.ThrowIfNull(visitor);

            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        visitor(process);
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static HashSet<int> CaptureRunningProcessIds()
        {
            var processIds = new HashSet<int>();
            EnumerateProcesses(process => processIds.Add(process.Id));
            return processIds;
        }
    }
}

using System.ComponentModel;

namespace AudioPilot.Tests.Platform;

public sealed class ProcessEnumerationHelperTests
{
    [Fact]
    public void CaptureRunningProcessIds_IncludesCurrentProcess()
    {
        Assert.Contains(Environment.ProcessId, ProcessEnumerationHelper.CaptureRunningProcessIds());
    }

    [Fact]
    public void CaptureRunningProcessIds_RetriesFullBufferWithoutPublishingTruncatedIds()
    {
        int calls = 0;
        int firstCapacity = 0;
        HashSet<int> result = ProcessEnumerationHelper.CaptureRunningProcessIds(buffer =>
        {
            calls++;
            if (calls == 1)
            {
                firstCapacity = buffer.Length;
                buffer.Fill(999);
                return buffer.Length;
            }

            Assert.True(buffer.Length > firstCapacity);
            buffer[0] = 0;
            buffer[1] = 42;
            buffer[2] = 42;
            return 3;
        });

        Assert.Equal(2, calls);
        Assert.True(result.SetEquals([0, 42]));
    }

    [Fact]
    public void CaptureRunningProcessIds_PropagatesFailureInsteadOfReportingAllProcessesExited()
    {
        var failure = new Win32Exception(5);
        Assert.Same(failure, Assert.Throws<Win32Exception>(() =>
            ProcessEnumerationHelper.CaptureRunningProcessIds(_ => throw failure)));
    }
}

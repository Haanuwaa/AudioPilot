using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater;

namespace AudioPilot.Tests.Services;

public sealed class SupervisedAppTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedCandidateSetupSettlesTheChildAndJournalsOnlyAfterContainment(bool jobFailure)
    {
        using var child = new SleepingChild();
        var launch = Launch(restart: true);
        using var server = Pipe(launch);
        Exception failure = jobFailure ? new Win32Exception(5, "Injected job assignment failure.") : new IOException("Injected recovery journal failure.");
        CandidateOwner? recorded = null;
        bool attemptedJob = false;
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => SupervisedApp.SuperviseStartedProcessAsync(
            server, child.Process, launch, "1.0.0", child.Path, owner =>
            {
                recorded = owner;
                Assert.True(attemptedJob);
                throw failure;
            }, process =>
            {
                attemptedJob = true;
                Assert.Null(recorded);
                Assert.Equal(child.Owner.Pid, process.Id);
                if (jobFailure) throw failure;
                return new CandidateJob(process);
            }).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
        Assert.True(attemptedJob);
        Assert.Equal(jobFailure ? null : child.Owner, recorded);
        Assert.True(child.Observer.HasExited);
    }

    [Fact]
    public async Task FailedOriginalProcessSetupDoesNotTerminateTheOriginal()
    {
        using var child = new SleepingChild();
        var launch = Launch(restart: false);
        using var server = Pipe(launch);
        var failure = new IOException("Injected attachment setup failure.");
        Exception error = await Assert.ThrowsAsync<IOException>(() => SupervisedApp.SuperviseStartedProcessAsync(
            server, child.Process, launch, "1.0.0", child.Path, _ => throw failure,
            _ => throw new InvalidOperationException("An original process must not be contained.")));
        Assert.Same(failure, error);
        Assert.False(child.Observer.HasExited);
    }

    [Fact]
    public async Task RecoveryRejectsUnrelatedLiveCandidateEvenWhenItsPidAndStartTimeMatch()
    {
        using var child = new SleepingChild();
        await Assert.ThrowsAsync<InvalidDataException>(() => child.Owner.WaitForExitAsync(
            TestContext.Current.CancellationToken, static _ => false));
        Assert.False(child.Observer.HasExited);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UncontainedTerminationRejectsMismatchedProcessIdentity(bool mismatchedPid)
    {
        using var child = new SleepingChild();
        CandidateOwner incorrect = mismatchedPid ? child.Owner with { Pid = child.Owner.Pid + 1 }
            : child.Owner with { StartedUtcTicks = child.Owner.StartedUtcTicks + 1 };
        Assert.Throws<InvalidDataException>(() => CandidateJob.TerminateUncontained(child.Process, incorrect));
        Assert.False(child.Observer.HasExited);
    }

    private static UpdateLaunch Launch(bool restart)
    {
        using var self = Process.GetCurrentProcess();
        string nonce = Guid.NewGuid().ToString("N");
        return new(1, nonce, "AudioPilot.Tests." + nonce, self.Id, self.StartTime.ToUniversalTime().Ticks,
            Environment.ProcessPath!, Guid.NewGuid().ToString("N"), restart, false);
    }

    private static NamedPipeServerStream Pipe(UpdateLaunch launch) => new(launch.PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

    private sealed class SleepingChild : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        internal Process Process { get; }
        internal Process Observer { get; }
        internal CandidateOwner Owner { get; }

        internal SleepingChild()
        {
            var start = new ProcessStartInfo(Path) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" })
                start.ArgumentList.Add(argument);
            Process = Process.Start(start) ?? throw new InvalidOperationException("The harmless test child did not start.");
            Process? observer = null;
            try
            {
                Owner = CandidateOwner.Capture(Process, Path);
                observer = Process.GetProcessById(Process.Id);
                _ = observer.SafeHandle;
                Observer = observer;
            }
            catch
            {
                try { if (!Process.HasExited) { Process.Kill(); Process.WaitForExit(10000); } }
                finally { observer?.Dispose(); Process.Dispose(); }
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                if (!Observer.HasExited) Observer.Kill();
                if (!Observer.WaitForExit(10000)) throw new TimeoutException("The harmless test child did not exit.");
            }
            finally { Observer.Dispose(); Process.Dispose(); }
        }
    }
}

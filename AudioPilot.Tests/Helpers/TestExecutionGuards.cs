using System.Runtime.ExceptionServices;
using System.Windows;

namespace AudioPilot.Tests.Helpers;

internal static class TestCategories
{
    internal const string Name = "Category";
    internal const string Integration = "Integration";
    internal const string VisualWpf = "VisualWpf";
    internal const string Stress = "Stress";
    internal const string HardwareSoak = "HardwareSoak";
}

internal static class TestExecutionGuards
{
    private const string IntegrationEnvVar = "AUDIOPILOT_RUN_INTEGRATION";
    private const string StressEnvVar = "AUDIOPILOT_RUN_STRESS";
    private const string VisualWpfEnvVar = "AUDIOPILOT_RUN_VISUAL_WPF";
    private const string RequireIntegrationHardwareEnvVar = "AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE";
    private const string HardwareSoakEnvVar = "AUDIOPILOT_RUN_HARDWARE_SOAK";
    private const string ShowWindowsEnvVar = "AUDIOPILOT_TEST_SHOW_WINDOWS";

    internal static bool ShouldRunIntegration() =>
        OperatingSystem.IsWindows() && IsTruthyEnvironmentVariable(IntegrationEnvVar);

    internal static bool ShouldRunStress() =>
        OperatingSystem.IsWindows() && IsTruthyEnvironmentVariable(StressEnvVar);

    internal static bool ShouldRunHardwareSoak() =>
        ShouldRunIntegration() && ShouldRunStress() && IsTruthyEnvironmentVariable(HardwareSoakEnvVar);

    internal static bool ShouldRunVisualWpfIntegration() =>
        ShouldRunIntegration() && IsTruthyEnvironmentVariable(VisualWpfEnvVar);

    internal static bool ShouldRequireIntegrationHardware() =>
        OperatingSystem.IsWindows() && IsTruthyEnvironmentVariable(RequireIntegrationHardwareEnvVar);

    internal static bool ShouldShowTestWindows() =>
        OperatingSystem.IsWindows() && IsTruthyEnvironmentVariable(ShowWindowsEnvVar);

    internal static string GetIntegrationSkipReason() =>
        $"Integration test disabled. Set {IntegrationEnvVar}=1 to enable.";

    internal static string GetStressSkipReason() =>
        $"Stress test disabled. Set {StressEnvVar}=1 to enable.";

    internal static string GetHardwareSoakSkipReason() =>
        $"Hardware soak disabled. Set {IntegrationEnvVar}=1, {StressEnvVar}=1, and {HardwareSoakEnvVar}=1 to enable.";

    internal static string GetVisualWpfSkipReason() =>
        $"Visual WPF integration test disabled. Set {IntegrationEnvVar}=1 and {VisualWpfEnvVar}=1 to enable.";

    internal static bool RequireStressEnabled(string scopeName)
    {
        if (ShouldRunStress())
        {
            return true;
        }

        Console.WriteLine($"[{scopeName}] {GetStressSkipReason()}");
        return false;
    }

    internal static bool RequireIntegrationEnabled(string scopeName)
    {
        if (ShouldRunIntegration())
        {
            return true;
        }

        Console.WriteLine($"[{scopeName}] {GetIntegrationSkipReason()}");
        return false;
    }

    internal static bool RequireVisualWpfIntegrationEnabled(string scopeName)
    {
        if (ShouldRunVisualWpfIntegration())
        {
            return true;
        }

        Console.WriteLine($"[{scopeName}] {GetVisualWpfSkipReason()}");
        return false;
    }

    internal static bool ReportOptionalIntegrationPrerequisite(string scopeName, string message)
    {
        Assert.Skip($"[{scopeName}] {message} Optional integration hardware is unavailable on this environment.");
        return false;
    }

    internal static InvalidOperationException CreateRequiredIntegrationPrerequisiteException(string scopeName, string message, Exception? innerException = null)
    {
        string fullMessage =
            $"[{scopeName}] {message} Strict hardware mode is enabled via {RequireIntegrationHardwareEnvVar}=1, so this is treated as a hard failure.";
        return innerException is null
            ? new InvalidOperationException(fullMessage)
            : new InvalidOperationException(fullMessage, innerException);
    }

    internal static bool TryGetNonEmptyEnvironmentVariable(string name, out string value)
    {
        value = Environment.GetEnvironmentVariable(name) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    internal static void RunSta(Action action, TimeSpan? timeout = null)
        => RunIsolatedSta(action, timeout);

    internal static void RunIsolatedSta(Action action, TimeSpan? timeout = null)
    {
        Exception? capturedException = null;
        using var completed = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        TimeSpan waitTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (!completed.Wait(waitTimeout))
        {
            throw new TimeoutException("Timed out while waiting for STA test thread to complete.");
        }

        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("STA test thread signaled completion but did not terminate.");
        }

        if (capturedException != null)
        {
            ExceptionDispatchInfo.Capture(capturedException).Throw();
        }
    }

    internal static void RunOnSharedSta(Action action, TimeSpan? timeout = null)
        => SharedStaDispatcherHost.Run(action, timeout);

    internal static Task RunOnSharedStaAsync(Func<Task> action, TimeSpan? timeout = null)
        => SharedStaDispatcherHost.RunAsync(action, timeout);

    internal static void EnsureSharedWpfApplication()
    {
        RunOnSharedSta(static () =>
        {
            if (Application.Current == null)
            {
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };
            }
        });
    }

    internal static async Task WaitUntilAsync(
        Func<bool> condition,
        string failureMessage,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(2);
        TimeSpan effectivePollInterval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        long deadline = Environment.TickCount64 + (long)Math.Max(0, effectiveTimeout.TotalMilliseconds);

        while (true)
        {
            if (condition())
            {
                return;
            }

            int remainingMs = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (remainingMs == 0)
            {
                Assert.True(condition(), failureMessage);
                return;
            }

            int waitMs = Math.Min(remainingMs, (int)Math.Max(1, effectivePollInterval.TotalMilliseconds));
            await Task.Delay(waitMs, TestContext.Current.CancellationToken);
        }
    }

    internal static async Task AssertDoesNotCompleteWithinAsync(Task task, TimeSpan timeout, string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(task);

        Task completed = await Task.WhenAny(
            task,
            Task.Delay(timeout, TestContext.Current.CancellationToken));
        if (ReferenceEquals(completed, task))
        {
            await task;
            Assert.Fail(failureMessage);
        }
    }

    private static bool IsTruthyEnvironmentVariable(params string[] names)
    {
        foreach (string name in names)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}


using System.Xml.Linq;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class StartupScheduledTaskTests
{
    private const string RegistryPath = "Software\\AudioPilot.Tests\\ScheduledStartup";
    private const string ValueName = "AudioPilotScheduledStartupTest";
    private readonly InMemoryUserRegistryAccessor _registry = new();
    private readonly InMemoryStartupTaskStore _tasks = new();
    private readonly string _executable = Path.Combine(AppContext.BaseDirectory, "AudioPilot.exe");

    private StartupService CreateService() => new(RegistryPath, ValueName, logger: null, _registry, _executable, _tasks);

    [Theory]
    [InlineData("Settings")]
    [InlineData("Triggers")]
    public void NumericDisabledBooleanSurvivesPathRepair(string section)
    {
        XElement task = XElement.Parse(StartupTaskDefinition.Create(@"C:\Old\AudioPilot.exe", _tasks.UserSid));
        task.Element(task.Name.Namespace + section)!.Descendants(task.Name.Namespace + "Enabled").Single().Value = "0";
        _tasks.Xml = task.ToString();
        StartupService service = CreateService();
        service.ValidateAndUpdateStartupPath(useScheduledTask: true);
        Assert.False(service.IsInStartupWithValidPath(useScheduledTask: true));
        Assert.True(service.IsInStartupWithValidPath(useScheduledTask: true, includeDisabled: true));
    }

    [Fact]
    public void EquivalentNumericBooleansDoNotRewriteTheTask()
    {
        XElement task = XElement.Parse(StartupTaskDefinition.Create(_executable, _tasks.UserSid));
        foreach (XElement element in task.Descendants().Where(element => !element.HasElements && element.Value is "true" or "false"))
        {
            element.Value = element.Value == "true" ? "1" : "0";
        }
        _tasks.Xml = task.ToString();
        CreateService().ValidateAndUpdateStartupPath(useScheduledTask: true);
        Assert.Equal(0, _tasks.WriteCount);
    }

    [Fact]
    public void SwitchingModesReplacesRegistration_AndDisableRemovesBoth()
    {
        StartupService service = CreateService();
        service.AddToStartup();
        service.AddToStartup(useScheduledTask: true);
        Assert.Null(_registry.GetValue(RegistryPath, ValueName));
        Assert.True(service.IsInStartupWithValidPath());
        Assert.True(StartupTaskDefinition.Matches(_tasks.Xml, _executable, _tasks.UserSid, false));

        service.AddToStartup();
        Assert.Null(_tasks.Xml);
        Assert.Equal($"\"{_executable}\" -startup", _registry.GetValue(RegistryPath, ValueName));
        _tasks.Xml = StartupTaskDefinition.Create(_executable, _tasks.UserSid);
        service.RemoveFromStartup();
        Assert.Null(_tasks.Xml);
        Assert.Null(_registry.GetValue(RegistryPath, ValueName));
    }

    [Fact]
    public void RegistrationFailureKeepsPreviousStartupAndDoesNotPersistSettings()
    {
        StartupService service = CreateService();
        service.AddToStartup();
        _tasks.FailNextWrite = true;
        bool persisted = false;
        Assert.Throws<UnauthorizedAccessException>(() => service.ApplyRegistration(true, true, () => persisted = true));
        Assert.False(persisted);
        Assert.Null(_tasks.Xml);
        Assert.Equal($"\"{_executable}\" -startup", _registry.GetValue(RegistryPath, ValueName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupRepairRemovesReintroducedRunEntryWithoutRewritingScheduledTask(bool taskEnabled)
    {
        string originalTask = StartupTaskDefinition.Create(_executable, _tasks.UserSid, taskEnabled);
        _tasks.Xml = originalTask;
        _registry.SetValue(RegistryPath, ValueName, $"\"{_executable}\" -startup");
        StartupService service = CreateService();

        Assert.False(service.IsInStartupWithValidPath(useScheduledTask: true, includeDisabled: true));
        service.ValidateAndUpdateStartupPath(useScheduledTask: true);

        Assert.Null(_registry.GetValue(RegistryPath, ValueName));
        Assert.Equal(originalTask, _tasks.Xml);
        Assert.Equal(0, _tasks.WriteCount);
        Assert.True(service.IsInStartupWithValidPath(useScheduledTask: true, includeDisabled: true));
        Assert.Equal(taskEnabled, service.IsInStartupWithValidPath(useScheduledTask: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SettingsWriteFailureRestoresPreviousMechanism(bool previousScheduled, bool nextScheduled)
    {
        StartupService service = CreateService();
        service.AddToStartup(useScheduledTask: previousScheduled);
        string? originalTask = _tasks.Xml;
        object? originalRun = _registry.GetValue(RegistryPath, ValueName);
        Assert.Throws<IOException>(() => service.ApplyRegistration(true, nextScheduled, () => throw new IOException("Disk full")));
        Assert.Equal(originalTask, _tasks.Xml);
        Assert.Equal(originalRun, _registry.GetValue(RegistryPath, ValueName));
    }

    [Fact]
    public void StartupValidationPreservesManuallyDisabledTask_ButExplicitEnableRestoresIt()
    {
        StartupService service = CreateService();
        _tasks.Xml = StartupTaskDefinition.Create(_executable, _tasks.UserSid, enabled: false);
        Assert.True(service.IsInStartup());
        Assert.False(service.IsInStartupWithValidPath(useScheduledTask: true));
        Assert.True(service.IsInStartupWithValidPath(useScheduledTask: true, includeDisabled: true));
        service.ValidateAndUpdateStartupPath(useScheduledTask: true);
        Assert.Equal(0, _tasks.WriteCount);
        service.AddToStartup(useScheduledTask: true);
        Assert.True(service.IsInStartupWithValidPath(useScheduledTask: true));
    }

    [Fact]
    public void PathRepairPreservesDisabledTask()
    {
        _tasks.Xml = StartupTaskDefinition.Create(@"C:\Old install\AudioPilot.exe", _tasks.UserSid, enabled: false);
        StartupService service = CreateService();
        service.AddToStartup(useScheduledTask: true, preserveDisabledTask: true);
        Assert.False(service.IsInStartupWithValidPath(useScheduledTask: true));
        Assert.True(service.IsInStartupWithValidPath(useScheduledTask: true, includeDisabled: true));
        service.AddToStartup(useScheduledTask: true);
        Assert.True(service.IsInStartupWithValidPath(useScheduledTask: true));
    }

    [Fact]
    public void DisabledTriggerIsRespectedUntilExplicitlyEnabled()
    {
        XElement task = XElement.Parse(StartupTaskDefinition.Create(_executable, _tasks.UserSid));
        task.Element(task.Name.Namespace + "Triggers")!.Descendants(task.Name.Namespace + "Enabled").Single().Value = "false";
        _tasks.Xml = task.ToString();
        StartupService service = CreateService();
        service.ValidateAndUpdateStartupPath(useScheduledTask: true);
        Assert.Equal(0, _tasks.WriteCount);
        Assert.False(service.IsInStartupWithValidPath(useScheduledTask: true));
        service.AddToStartup(useScheduledTask: true);
        Assert.True(service.IsInStartupWithValidPath(useScheduledTask: true));
    }

    [Theory]
    [InlineData("Triggers")]
    [InlineData("Principals")]
    [InlineData("Actions")]
    public void ValidationRejectsAdditionalTaskEntries(string section)
    {
        XElement task = XElement.Parse(StartupTaskDefinition.Create(_executable, _tasks.UserSid));
        XElement entry = task.Element(task.Name.Namespace + section)!;
        entry.Add(new XElement(entry.Elements().Single()));
        Assert.False(StartupTaskDefinition.Matches(task.ToString(), _executable, _tasks.UserSid, false));
    }

    [Fact]
    public void HealthyRunKeyStartupDoesNotConnectToTaskScheduler()
    {
        _registry.SetValue(RegistryPath, ValueName, $"\"{_executable}\" -startup");
        StartupService service = CreateService();
        Assert.True(service.IsInStartup());
        Assert.True(service.IsInStartupWithValidPath(useScheduledTask: false));
        service.ValidateAndUpdateStartupPath(useScheduledTask: false);
        Assert.Equal(0, _tasks.ReadCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StartupReconciliationUsesOneTaskReadAndAdoptsItsEnabledStateOnFirstRun(bool enabled, bool firstRun)
    {
        StartupService service = CreateService();
        string originalTask = StartupTaskDefinition.Create(_executable, _tasks.UserSid, enabled);
        _tasks.Xml = originalTask;

        StartupInitPlan plan = AppStartupRegistryCoordinator.BuildInitPlan(firstRun,
            new() { Enabled = !firstRun, UseTaskScheduler = !firstRun },
            service.ReadRegistration(skipTaskIfRunMatches: firstRun, startupRegistryOpId: null));
        AppStartupRegistryCoordinator.ExecuteInitPlan(plan, firstRun,
            () => service.AddToStartup(useScheduledTask: plan.UseTaskScheduler), () => service.RemoveFromStartup(),
            startupRegistryOpId: "startup-test", Logger.Instance);

        Assert.True(plan.UseTaskScheduler);
        Assert.Equal(!firstRun || enabled, plan.RunAtStartupValue);
        Assert.Equal(originalTask, _tasks.Xml);
        Assert.Equal(1, _tasks.ReadCount);
        Assert.Equal(0, _tasks.WriteCount);
        Assert.Null(_registry.GetValue(RegistryPath, ValueName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitDisableDuringSchedulerOutageOnlyRemovesVerifiedRunEntry(bool failSettingsWrite)
    {
        StartupService service = CreateService();
        service.AddToStartup();
        _tasks.IsUnavailable = true;
        bool persisted = false;
        StartupRegistrationOutcome Disable() => service.ApplyRegistration(false, false, () =>
        {
            if (failSettingsWrite) throw new IOException("Disk full");
            persisted = true;
        }, allowIncompleteDisable: true);

        if (failSettingsWrite)
        {
            Assert.Throws<IOException>(() => Disable());
            Assert.NotNull(_registry.GetValue(RegistryPath, ValueName));
            Assert.False(persisted);
        }
        else
        {
            Assert.Equal(StartupRegistrationOutcome.SchedulerUnavailable, Disable());
            Assert.Null(_registry.GetValue(RegistryPath, ValueName));
            Assert.True(persisted);
        }

        const string otherCommand = @"C:\Other\AudioPilot.exe -startup";
        _registry.SetValue(RegistryPath, ValueName, otherCommand);
        Assert.Throws<InvalidOperationException>(() => Disable());
        Assert.Equal(otherCommand, _registry.GetValue(RegistryPath, ValueName));
    }

    [Theory]
    [InlineData("PT0S", true)]
    [InlineData("PT1M", false)]
    public void TriggerTimeLimitMustAllowTheAppToRunForTheSession(string limit, bool valid)
    {
        XElement root = XElement.Parse(StartupTaskDefinition.Create(_executable, _tasks.UserSid));
        XNamespace ns = root.Name.Namespace;
        root.Descendants(ns + "LogonTrigger").Single().Add(new XElement(ns + "ExecutionTimeLimit", limit));

        Assert.Equal(valid, StartupTaskDefinition.Matches(root.ToString(), _executable, _tasks.UserSid, false));
    }

    [Fact]
    public void DefinitionEscapesPathsAndUsesAnInteractiveLogonWithNormalPriority()
    {
        const string path = "C:\\Apps & tools\\AudioPilot\\AudioPilot.exe";
        string xml = StartupTaskDefinition.Create(path, _tasks.UserSid);
        XElement root = XElement.Parse(xml);
        XNamespace ns = root.Name.Namespace;
        Assert.Equal(path, root.Descendants(ns + "Command").Single().Value);
        Assert.Equal("-startup", root.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("PT0S", root.Descendants(ns + "Delay").Single().Value);
        Assert.Equal("4", root.Descendants(ns + "Priority").Single().Value);
        Assert.Equal("InteractiveToken", root.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", root.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("PT0S", root.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.True(StartupTaskDefinition.Matches(xml, path, _tasks.UserSid, false));
        root.Descendants(ns + "Delay").Single().Remove();
        Assert.True(StartupTaskDefinition.Matches(root.ToString(), path, _tasks.UserSid, false));
    }

    [Theory]
    [InlineData("Priority", "7")]
    [InlineData("LogonType", "S4U")]
    [InlineData("RunLevel", "HighestAvailable")]
    [InlineData("Delay", "PT30S")]
    [InlineData("Delay", "PT3S")]
    [InlineData("DisallowStartIfOnBatteries", "true")]
    [InlineData("ExecutionTimeLimit", "PT72H")]
    [InlineData("UserId", "S-1-5-18")]
    public void ValidationRejectsChangedSchedulingOrSecurity(string element, string value)
    {
        XElement root = XElement.Parse(StartupTaskDefinition.Create(_executable, _tasks.UserSid));
        root.Descendants(root.Name.Namespace + element).First().Value = value;
        Assert.False(StartupTaskDefinition.Matches(root.ToString(), _executable, _tasks.UserSid, false));
    }
}

using System.Xml.Linq;
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

    [Fact]
    public void DefinitionEscapesPathsAndUsesAnInteractiveLogonWithNormalPriority()
    {
        const string path = "C:\\Apps & tools\\AudioPilot\\AudioPilot.exe";
        string xml = StartupTaskDefinition.Create(path, _tasks.UserSid);
        XElement root = XElement.Parse(xml);
        XNamespace ns = root.Name.Namespace;
        Assert.Equal(path, root.Descendants(ns + "Command").Single().Value);
        Assert.Equal("-startup", root.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("PT3S", root.Descendants(ns + "Delay").Single().Value);
        Assert.Equal("4", root.Descendants(ns + "Priority").Single().Value);
        Assert.Equal("InteractiveToken", root.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", root.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("PT0S", root.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.True(StartupTaskDefinition.Matches(xml, path, _tasks.UserSid, false));
    }

    [Theory]
    [InlineData("Priority", "7")]
    [InlineData("LogonType", "S4U")]
    [InlineData("RunLevel", "HighestAvailable")]
    [InlineData("Delay", "PT30S")]
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

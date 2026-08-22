using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppShellIntegrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Haanuwaa.AudioPilot")]
    public void NotificationDisplayName_RepairsBrandingWithoutReplacingOtherShellMetadata(string? previousName)
    {
        var registry = new InMemoryUserRegistryAccessor();
        const string path = @"Software\Classes\AppUserModelId\Haanuwaa.AudioPilot";
        const string icon = @"C:\Another copy\sender.png";
        const string activator = "{461D66A6-A065-4B3E-8E53-16E47A7DC5DB}";
        registry.SetValue(path, "IconUri", icon);
        registry.SetValue(path, "CustomActivator", activator);
        if (previousName != null) registry.SetValue(path, "DisplayName", previousName);
        int originalWrites = registry.SetValueCallCount;

        AppShellIntegration.RegisterNotificationDisplayName(registry);
        AppShellIntegration.RegisterNotificationDisplayName(registry);

        Assert.Equal("AudioPilot", registry.GetValue(path, "DisplayName"));
        Assert.Equal(icon, registry.GetValue(path, "IconUri"));
        Assert.Equal(activator, registry.GetValue(path, "CustomActivator"));
        Assert.Equal(originalWrites + 1, registry.SetValueCallCount);
    }

    [Fact]
    public void NotificationDisplayName_AlreadyCorrectRequiresNoRegistryWritePermission()
    {
        var registry = new InMemoryUserRegistryAccessor();
        registry.SetValue(AppShellIntegration.NotificationRegistryPath, "DisplayName", "AudioPilot");
        int originalWrites = registry.SetValueCallCount;
        registry.ThrowOnSetValue = true;

        AppShellIntegration.RegisterNotificationDisplayName(registry);

        Assert.Equal(originalWrites, registry.SetValueCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotificationDisplayName_RegistryReadOrWriteDenialDoesNotPreventStartup(bool denyWrite)
    {
        var registry = new InMemoryUserRegistryAccessor
        {
            ThrowOnAccess = !denyWrite,
            ThrowOnSetValue = denyWrite,
        };

        AppShellIntegration.RegisterNotificationDisplayName(registry);

        registry.ThrowOnAccess = false;
        Assert.Null(registry.GetValue(AppShellIntegration.NotificationRegistryPath, "DisplayName"));
    }

    [Theory]
    [InlineData("picker", 1)]
    [InlineData("mixer", 2)]
    [InlineData("routines", 3)]
    public void ShellActions_RoundTripOnlyRecognizedCommands(string name, int expected)
    {
        Assert.True(ShellActivation.TryParseArguments(["--shell-action", name], out ShellAction action));
        Assert.Equal((ShellAction)expected, action);
        Assert.True(ShellActivation.TryParsePayload(ShellActivation.ToPayload(action), out ShellAction forwarded));
        Assert.Equal((ShellAction)expected, forwarded);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("picker --anything")]
    [InlineData("../picker")]
    [InlineData("Picker")]
    [InlineData("")]
    public void ShellActions_RejectMalformedOrUnsupportedInput(string name)
    {
        Assert.False(ShellActivation.TryParseArguments(["--shell-action", name], out _));
        Assert.False(ShellActivation.TryParsePayload("AudioPilot.Shell.v1:" + name, out _));
        Assert.False(ShellActivation.TryParseArguments(["--shell-action", "picker", name], out _));
    }

    [Fact]
    public void JumpTasks_UseTheAppExecutableAndDoNotDependOnItsWorkingDirectory()
    {
        const string executable = @"C:\Audio utilities\AudioPilot.exe";
        var tasks = AppShellIntegration.CreateTasks(executable);
        Assert.Equal(3, tasks.Count);
        Assert.Equal(["Switch audio device", "Open volume mixer", "Open routines"], tasks.Select(task => task.Title));
        foreach (var task in tasks)
        {
            Assert.Equal(executable, task.ApplicationPath);
            Assert.Equal(@"C:\Audio utilities", task.WorkingDirectory);
            Assert.True(ShellActivation.TryParseArguments(task.Arguments.Split(' '), out _));
        }
    }
}

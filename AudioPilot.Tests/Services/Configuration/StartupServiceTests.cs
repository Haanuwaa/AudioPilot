using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class StartupServiceTests
{
    private const string RegistryPath = @"SOFTWARE\AudioPilot.Tests\Startup";
    private const string ValueName = "AudioPilotTest";
    private readonly InMemoryUserRegistryAccessor _registry = new();

    [Fact]
    public void AddToStartup_WritesRegistryValue_AndIsInStartupReturnsTrue()
    {
        StartupService service = CreateService();

        service.AddToStartup();

        string? stored = _registry.GetValue(RegistryPath, ValueName)?.ToString();

        Assert.False(string.IsNullOrWhiteSpace(stored));
        Assert.Contains("-startup", stored, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.IsInStartup());
    }

    [Fact]
    public void AddToStartup_WithExplicitExecutable_WritesTheGuiStartupCommand()
    {
        string guiExecutablePath = Path.Combine(AppContext.BaseDirectory, "AudioPilot.exe");
        StartupService service = CreateService(startupExecutablePath: guiExecutablePath);

        service.AddToStartup();

        Assert.Equal($"\"{guiExecutablePath}\" -startup", _registry.GetValue(RegistryPath, ValueName));
        Assert.True(service.IsInStartupWithValidPath());
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\AudioPilot\\AudioPilot.exe\"")]
    [InlineData("\"C:\\Program Files\\AudioPilot\\AudioPilot.exe\" -startup unexpected")]
    [InlineData("\"\" -startup")]
    public void IsInStartupWithValidPath_RejectsMalformedStartupCommands(string registryValue)
    {
        string guiExecutablePath = Path.Combine(AppContext.BaseDirectory, "AudioPilot.exe");
        _registry.SetValue(RegistryPath, ValueName, registryValue);
        StartupService service = CreateService(startupExecutablePath: guiExecutablePath);

        Assert.False(service.IsInStartupWithValidPath());
    }

    [Fact]
    public void AddToStartup_WhenExplicitExecutableIsMissing_DoesNotWriteADeadEntry()
    {
        string missingExecutablePath = Path.Combine(AppContext.BaseDirectory, $"missing-{Guid.NewGuid():N}.exe");
        StartupService service = CreateService(startupExecutablePath: missingExecutablePath);

        Assert.Throws<FileNotFoundException>(() => service.AddToStartup());
        Assert.Null(_registry.GetValue(RegistryPath, ValueName));
    }

    [Fact]
    public void IsInStartup_EmitsCorrelatedProbeLog()
    {
        _registry.SetValue(RegistryPath, ValueName, "dummy");

        using var logger = Logger.CreateInMemoryForTests("startup-service-probe.log");
        logger.MinimumLevel = LogLevel.Debug;

        StartupService service = CreateService(logger);

        bool inStartup = service.IsInStartup("startup-registry:test-probe");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.True(inStartup);
        Assert.True(string.IsNullOrWhiteSpace(logText), $"Did not expect success-path startup probe logs, but found:{Environment.NewLine}{logText}");
    }

    [Fact]
    public void RemoveFromStartup_DeletesExistingRegistryValue()
    {
        _registry.SetValue(RegistryPath, ValueName, "dummy");

        StartupService service = CreateService();
        service.RemoveFromStartup();

        Assert.Null(_registry.GetValue(RegistryPath, ValueName));
        Assert.False(service.IsInStartup());
    }

    [Fact]
    public void ValidateAndUpdateStartupPath_RewritesMismatchedValue()
    {
        _registry.SetValue(RegistryPath, ValueName, "\"C:\\path\\to\\missing.exe\" -startup");

        StartupService service = CreateService();
        service.ValidateAndUpdateStartupPath();

        string? updated = _registry.GetValue(RegistryPath, ValueName)?.ToString();

        Assert.False(string.IsNullOrWhiteSpace(updated));
        Assert.Contains("-startup", updated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing.exe", updated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsInStartupWithValidPath_EmitsCorrelatedProbeLogs()
    {
        _registry.SetValue(RegistryPath, ValueName, "\"C:\\path\\to\\missing.exe\" -startup");

        using var logger = Logger.CreateInMemoryForTests("startup-service-validity.log");
        logger.MinimumLevel = LogLevel.Trace;

        StartupService service = CreateService(logger);

        bool valid = service.IsInStartupWithValidPath("startup-registry:test-validity");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.False(valid);
        Assert.Contains($"{AppConstants.Audio.LogEvents.Startup.IsInStartupValidPath} | opId=startup-registry:test-validity result=false reason=path-mismatch", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(AppConstants.Audio.LogEvents.Startup.IsInStartupValidPathValues, logText, StringComparison.Ordinal);
    }

    [Fact]
    public void AddToStartup_EmitsCorrelatedLifecycleLogs()
    {
        using var logger = Logger.CreateInMemoryForTests("startup-service-add.log");
        logger.MinimumLevel = LogLevel.Trace;

        StartupService service = CreateService(logger);

        service.AddToStartup("startup-registry:test-add");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.Contains("add-startup-start | opId=startup-registry:test-add", logText, StringComparison.Ordinal);
        Assert.Contains($"{AppConstants.Audio.LogEvents.Startup.AddStartupPath} | opId=startup-registry:test-add", logText, StringComparison.Ordinal);
        Assert.Contains("opId=startup-registry:test-add", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAndUpdateStartupPath_EmitsCorrelatedLifecycleLogs()
    {
        _registry.SetValue(RegistryPath, ValueName, "\"C:\\path\\to\\missing.exe\" -startup");

        using var logger = Logger.CreateInMemoryForTests("startup-service-validate.log");
        logger.MinimumLevel = LogLevel.Trace;

        StartupService service = CreateService(logger);

        service.ValidateAndUpdateStartupPath("startup-registry:test-validate");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.Contains("validate-startup-path-update | opId=startup-registry:test-validate", logText, StringComparison.Ordinal);
        Assert.Contains($"{AppConstants.Audio.LogEvents.Startup.ValidateStartupPathValues} | opId=startup-registry:test-validate", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void AddToStartup_WhenRegistryAccessIsDenied_PropagatesTheFailure()
    {
        _registry.ThrowOnAccess = true;
        StartupService service = CreateService();

        Assert.Throws<UnauthorizedAccessException>(() => service.AddToStartup());
    }

    private StartupService CreateService(Logger? logger = null, string? startupExecutablePath = null)
    {
        return new StartupService(RegistryPath, ValueName, logger, _registry, startupExecutablePath);
    }
}


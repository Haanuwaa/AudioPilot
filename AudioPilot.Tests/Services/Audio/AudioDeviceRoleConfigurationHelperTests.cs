using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceRoleConfigurationHelperTests
{
    [Fact]
    public void UpdateConfiguration_NormalizesAndStoresSnapshots()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceRoleConfigurationHelperTests), "role-config-helper.log");
        var configuration = new AudioRoleConfiguration([Role.Multimedia, Role.Console], [Role.Console]);
        var helper = new AudioDeviceRoleConfigurationHelper(configuration, loggerScope.Logger);

        var (outputRoles, inputRoles) = helper.UpdateConfiguration(
            ["communications", "console"],
            ["multimedia"],
            [Role.Multimedia, Role.Console],
            [Role.Console]);

        Assert.Equal([Role.Communications, Role.Console], outputRoles);
        Assert.Equal([Role.Multimedia], inputRoles);
        Assert.Equal(outputRoles, helper.GetOutputRolesSnapshot());
        Assert.Equal(inputRoles, helper.GetInputRolesSnapshot());
    }
}

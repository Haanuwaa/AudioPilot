using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

public sealed class RegistryPerAppAudioRoutingResetterTests : IDisposable
{
    private readonly TestScopedDirectory _scope;
    private readonly Logger _logger;
    private readonly InMemoryUserRegistryAccessor _registry = new();
    private const string PropertyStorePath = @"PolicyConfig\PropertyStore";

    public RegistryPerAppAudioRoutingResetterTests()
    {
        _scope = new TestScopedDirectory(nameof(RegistryPerAppAudioRoutingResetterTests));
        _logger = new Logger(_scope.Root, "registry-per-app-reset.log");
    }

    [Fact]
    public void TryResetAll_WhenNoAssignmentsExist_ReturnsSuccessWithoutAssignments()
    {
        var resetter = new RegistryPerAppAudioRoutingResetter(_registry, PropertyStorePath, _logger);

        PerAppAudioRoutingResetResult result = resetter.TryResetAll();

        Assert.True(result.Success);
        Assert.False(result.HadAssignments);
    }

    [Fact]
    public void TryResetAll_WhenAssignmentsExist_DeletesPropertyStore()
    {
        _registry.SetValue(PropertyStorePath, "value-1", "assigned");
        _registry.SetValue(PropertyStorePath + "\\child", "value-2", 1);

        var resetter = new RegistryPerAppAudioRoutingResetter(_registry, PropertyStorePath, _logger);

        PerAppAudioRoutingResetResult result = resetter.TryResetAll();

        Assert.True(result.Success);
        Assert.True(result.HadAssignments);
        Assert.False(_registry.HasValuesOrSubKeys(PropertyStorePath));
    }

    [Fact]
    public void TryResetAll_WhenRegistryAccessFails_ReturnsFailure()
    {
        _registry.ThrowOnAccess = true;
        var resetter = new RegistryPerAppAudioRoutingResetter(_registry, PropertyStorePath, _logger);

        PerAppAudioRoutingResetResult result = resetter.TryResetAll();

        Assert.False(result.Success);
        Assert.False(result.HadAssignments);
    }

    [Fact]
    public void TryResetAll_WhenAssignmentsExistButDeletionFails_PreservesAssignmentDiscovery()
    {
        _registry.SetValue(PropertyStorePath, "value-1", "assigned");
        _registry.ThrowOnDeleteSubKeyTree = true;
        var resetter = new RegistryPerAppAudioRoutingResetter(_registry, PropertyStorePath, _logger);

        PerAppAudioRoutingResetResult result = resetter.TryResetAll();

        Assert.False(result.Success);
        Assert.True(result.HadAssignments);
        _registry.ThrowOnDeleteSubKeyTree = false;
        Assert.True(_registry.HasValuesOrSubKeys(PropertyStorePath));
    }

    public void Dispose()
    {
        _logger.Dispose();
        _scope.Dispose();
    }
}

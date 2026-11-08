using System.Windows.Controls;
using AudioPilot.Constants;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public sealed class AppHelpTests
{
    [Theory]
    [InlineData(AppHelpTopic.Updates, "install-an-update")]
    [InlineData(AppHelpTopic.Mixer, "mixer-view")]
    [InlineData(AppHelpTopic.RoutineConditions, "routine-conditions")]
    [InlineData(AppHelpTopic.ApplicationRouting, "app-only-routing-vs-default-switching")]
    public void ContextGuideComesFirst_WithPackagedReferenceAsLaunchFallback(AppHelpTopic topic, string anchor)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AudioPilot", "help-test");
        string[] targets = AppHelp.ResolveTargets(directory, topic, _ => true);
        Assert.Equal([AppConstants.Links.UserGuideUrl + "#" + anchor, Path.Combine(directory, AppConstants.Files.AboutFileName)], targets);
        Assert.Equal(targets[0], Assert.Single(AppHelp.ResolveTargets(directory, topic, _ => false)));
    }

    [Fact]
    public void FocusedChildrenInheritTheMostSpecificHelpTopic() => TestExecutionGuards.RunOnSharedSta(() =>
    {
        var field = new TextBox();
        var section = new StackPanel();
        section.Children.Add(field);
        AppHelp.SetTopic(section, AppHelpTopic.Settings);
        Assert.Equal(AppHelpTopic.Settings, AppHelp.GetTopic(field));
        AppHelp.SetTopic(field, AppHelpTopic.Updates);
        Assert.Equal(AppHelpTopic.Updates, AppHelp.GetTopic(field));
    });
}

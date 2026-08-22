using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRemovedRoutingTargetHelperTests
{
    [Fact]
    public void CalculateRemovedPerAppRoutingTargets_ClearsOnlyFlowsThatWereRemoved()
    {
        List<AudioRoutine> previousRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new AudioRoutine
            {
                Id = "routine-2",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                InputDeviceId = "in-1",
                InputDeviceName = "Microphone",
            },
            new AudioRoutine
            {
                Id = "routine-3",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];
        List<AudioRoutine> nextRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-2",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                InputDeviceId = "in-1",
                InputDeviceName = "Microphone",
            }
        ];

        IReadOnlyList<AppViewModel.RemovedPerAppRoutingTarget> result = AppViewModelRemovedRoutingTargetHelper.CalculateRemovedPerAppRoutingTargets(previousRoutines, nextRoutines);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal(@"C:\Apps\Discord\Discord.exe", first.NormalizedTriggerPath);
                Assert.True(first.ResetOutput);
                Assert.False(first.ResetInput);
            },
            second =>
            {
                Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", second.NormalizedTriggerPath);
                Assert.True(second.ResetOutput);
                Assert.False(second.ResetInput);
            });
    }

    [Fact]
    public void CalculateRemovedPerAppRoutingTargets_ClearsRemovedPackagedOutputWhileKeepingRemainingInput()
    {
        List<AudioRoutine> previousRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new AudioRoutine
            {
                Id = "routine-2",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
                SwitchOutputPerApp = true,
                InputDeviceId = "in-1",
                InputDeviceName = "Microphone",
            }
        ];
        List<AudioRoutine> nextRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-2",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
                SwitchOutputPerApp = true,
                InputDeviceId = "in-1",
                InputDeviceName = "Microphone",
            }
        ];

        IReadOnlyList<AppViewModel.RemovedPerAppRoutingTarget> result = AppViewModelRemovedRoutingTargetHelper.CalculateRemovedPerAppRoutingTargets(previousRoutines, nextRoutines);

        AppViewModel.RemovedPerAppRoutingTarget target = Assert.Single(result);
        Assert.Equal("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", target.NormalizedTriggerPath);
        Assert.True(target.ResetOutput);
        Assert.False(target.ResetInput);
    }
}

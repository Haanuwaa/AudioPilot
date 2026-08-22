using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using AudioPilot.Controls;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Controls;

public sealed class AudioLevelMeterAutomationPeerTests
{
    [Fact]
    public void Meter_ExposesItsPercentageThroughReadOnlyRangeValuePattern()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var meter = new AudioLevelMeter { Level = 42.5d };
            AutomationProperties.SetName(meter, "Microphone level");
            var peer = new AudioLevelMeterAutomationPeer(meter);

            var provider = Assert.IsType<IRangeValueProvider>(
                peer.GetPattern(PatternInterface.RangeValue), exactMatch: false);

            Assert.Equal(AutomationControlType.ProgressBar, peer.GetAutomationControlType());
            Assert.Equal("Microphone level", peer.GetName());
            Assert.True(provider.IsReadOnly);
            Assert.Equal(0d, provider.Minimum);
            Assert.Equal(100d, provider.Maximum);
            Assert.Equal(42.5d, provider.Value);
            Assert.Throws<InvalidOperationException>(() => provider.SetValue(50d));
        });
    }
}

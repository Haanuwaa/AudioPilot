using System.Reflection;
using AudioPilot.Models;
using AudioPilot.ViewModels;
using BenchmarkDotNet.Attributes;

namespace AudioPilot.Benchmarks;

[MemoryDiagnoser]
public class SettingsComparisonBenchmarks
{
    public sealed record ComparisonCase(int Count, bool Changed)
    {
        public override string ToString() => $"{Count}-{(Changed ? "LastChanged" : "Equal")}";
    }

    public static IEnumerable<ComparisonCase> Cases => [new(0, false), new(10, false), new(10, true), new(100, false), new(100, true)];
    [ParamsSource(nameof(Cases))]
    public ComparisonCase Case { get; set; } = new(0, false);
    private Settings _saved = null!;
    private Settings _draft = null!;
    private Func<Settings?, Settings, SettingsFeedbackTabs> _settings = null!;
    private Func<IEnumerable<AudioRoutine>?, IEnumerable<AudioRoutine>?, bool> _routines = null!;

    [GlobalSetup]
    public void SetUp()
    {
        // Bind existing private comparisons once; reflection and fixture cloning are outside measurement.
        _settings = typeof(AppViewModel).GetMethod("GetChangedDeviceSaveTabs", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Settings?, Settings, SettingsFeedbackTabs>>();
        _routines = typeof(AppViewModel).GetMethod("AreRoutineListsEquivalent", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IEnumerable<AudioRoutine>?, IEnumerable<AudioRoutine>?, bool>>();
        _saved = new Settings();
        _saved.DeviceSwitching.Output.SwitchHotkey = "Ctrl+Alt+O";
        for (int index = 0; index < Case.Count; index++)
        {
            _saved.DeviceSwitching.Output.CycleDevices.Add(new CycleDevice { Id = $"output-{index}", Name = $"Output {index}", StableId = $"stable-output-{index}" });
            _saved.DeviceSwitching.Input.CycleDevices.Add(new CycleDevice { Id = $"input-{index}", Name = $"Input {index}", StableId = $"stable-input-{index}" });
            _saved.Routines.Items.Add(new AudioRoutine
            {
                Id = $"routine-{index}",
                Name = $"Routine {index}",
                Hotkey = "Ctrl+Alt+R",
                OutputDeviceId = $"output-{index}",
                MasterVolumePercent = 75,
                Triggers = [new RoutineTrigger { Id = $"trigger-{index}", Kind = RoutineTriggerKind.Application, AppPath = $"example-{index}.exe" }]
            });
        }
        _draft = _saved.Clone();
        if (Case.Changed)
        {
            _draft.DeviceSwitching.Output.CycleDevices[^1].Id = "changed-output";
            _draft.Routines.Items[^1].MasterVolumePercent = 50;
        }
        if (DeviceSettingsEqual() != !Case.Changed || RoutinesEqual() != !Case.Changed)
            throw new InvalidOperationException("The settings comparison benchmark fixture has the wrong result.");
    }

    [Benchmark]
    public bool DeviceSettingsEqual() => _settings(_saved, _draft) == SettingsFeedbackTabs.None;

    [Benchmark]
    public bool RoutinesEqual() => _routines(_saved.Routines.Items, _draft.Routines.Items);

    internal static void ValidateCases()
    {
        var benchmark = new SettingsComparisonBenchmarks();
        foreach (ComparisonCase fixture in Cases) { benchmark.Case = fixture; benchmark.SetUp(); }
    }
}

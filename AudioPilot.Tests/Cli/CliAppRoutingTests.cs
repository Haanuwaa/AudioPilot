using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliAppRoutingTests
{
    [Theory]
    [InlineData("get", "output", CliAction.AppRouteGetOutput)]
    [InlineData("get", "input", CliAction.AppRouteGetInput)]
    [InlineData("set", "output", CliAction.AppRouteSetOutput)]
    [InlineData("set", "input", CliAction.AppRouteSetInput)]
    [InlineData("clear", "output", CliAction.AppRouteClearOutput)]
    [InlineData("clear", "input", CliAction.AppRouteClearInput)]
    public async Task RouteCommandsRoundTripAndDispatchToRouting(string operation, string direction, CliAction action)
    {
        string[] args = ["app", "route", operation, direction, "--process", "Player.EXE", "--json", "--redact"];
        if (operation == "set") args = [.. args, "--device", "Private headphones"];
        Assert.True(CliCommand.TryParse(args, out var parsed, out string? error), error);
        Assert.True(CliCommand.TryFromPipePayload(parsed.ToPipePayload(), out var forwarded));
        Assert.Equal(action, forwarded.Action);
        Assert.Equal("name:Player", forwarded.Key);
        Assert.Equal(operation == "set" ? "[name]Private headphones" : null, forwarded.Value);
        Assert.True(forwarded.JsonOutput);
        Assert.True(forwarded.RedactOutput);
        var runtime = new FakeRuntime
        {
            AppRoutingOverride = command =>
            {
                Assert.Same(forwarded, command);
                return Task.FromResult(new CliExecutionResult(5, "routing-result"));
            },
            AppAudioOverride = _ => throw new InvalidOperationException("Routing must not collect playback sessions."),
        };
        Assert.Equal(new CliExecutionResult(5, "routing-result"), await CliCommandExecutor.ExecuteAsync(forwarded, runtime));
    }

    [Theory]
    [InlineData("app route")]
    [InlineData("app route set output --pid 1")]
    [InlineData("app route set output --pid 1 --device-id --json")]
    [InlineData("app route set output --process --device Headphones")]
    [InlineData("app route set output --pid 1 --device Headphones --device-id out")]
    [InlineData("app route get output --pid 1 --device Headphones")]
    [InlineData("app route clear input --pid 1 --device-id in")]
    [InlineData("app route get output --pid 0")]
    [InlineData("app route get output --pid 1 --process player")]
    [InlineData("app route get output --process player*")]
    [InlineData("app route get output --pid 1 --json --json")]
    [InlineData("app route get both --pid 1")]
    [InlineData("app route reset output --pid 1")]
    public void InvalidRequestsAreRejectedWithHelp(string input)
    {
        Assert.False(CliCommand.TryParse(input.Split(' '), out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal("app", CliHostUtilities.InferHelpTopic(input.Split(' ')));
    }

    [Theory]
    [InlineData("get", "output")]
    [InlineData("get", "input")]
    [InlineData("set", "output")]
    [InlineData("set", "input")]
    [InlineData("clear", "output")]
    [InlineData("clear", "input")]
    public async Task BothDirectionsUseIdentityChecksAndOnlyWritesAcquireManualOwnership(string operation, string direction)
    {
        var fixture = new RoutingFixture();
        CliCommand command = Command(operation, direction);
        int scopes = 0;
        async Task Scope(uint pid, bool output, Func<Task> apply, CancellationToken token)
        {
            Assert.Equal(123u, pid);
            Assert.Equal(direction == "output", output);
            token.ThrowIfCancellationRequested();
            scopes++;
            await apply();
        }
        var result = await CliAppRoutingOperation.ExecuteAsync(command, fixture.Operations, TestContext.Current.CancellationToken, Scope, _ => fixture.Target);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal((123u, direction == "output", (long?)42), Assert.Single(fixture.Reads));
        Assert.Equal(operation == "get" ? 0 : 1, scopes);
        if (operation == "get") Assert.Empty(fixture.Writes);
        else Assert.Equal((123u, direction == "output", 42L, operation == "set" ? "device-private" : null), Assert.Single(fixture.Writes));
    }

    [Theory]
    [InlineData(true, false, null, "default", 0)]
    [InlineData(true, true, null, "mixed", 0)]
    [InlineData(false, false, null, "unknown", 3)]
    [InlineData(true, false, "disconnected-private", "device", 0)]
    public async Task ReadsDistinguishDefaultMixedUnavailableAndDisconnectedPreferences(bool available, bool mixed, string? id, string state, int exitCode)
    {
        var fixture = new RoutingFixture { Preference = new(available, mixed, id) };
        var result = await fixture.Execute(Command("get", "input"));
        Assert.Equal(exitCode, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output!);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal(state, data.GetProperty("preferenceState").GetString());
        Assert.Equal(id, data.GetProperty("deviceId").GetString());
        if (id != null) Assert.False(data.GetProperty("deviceAvailable").GetBoolean());
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData((int)ProcessAudioRoutingResult.Applied, true, false, "device-private", 0, "app-route-saved")]
    [InlineData((int)ProcessAudioRoutingResult.DeferredNoAudio, true, false, "device-private", 0, "app-route-saved")]
    [InlineData((int)ProcessAudioRoutingResult.Failed, true, false, "device-private", 3, "app-route-partial")]
    [InlineData((int)ProcessAudioRoutingResult.Applied, false, false, null, 3, "app-route-unconfirmed")]
    [InlineData((int)ProcessAudioRoutingResult.Applied, true, true, null, 3, "app-route-unconfirmed")]
    [InlineData((int)ProcessAudioRoutingResult.Applied, true, false, "another-device", 3, "app-route-unconfirmed")]
    public async Task WritesReportSavedPreferencesAndPartialOrUnconfirmedOutcomes(int outcome, bool available, bool mixed, string? id, int exitCode, string code)
    {
        var fixture = new RoutingFixture { Outcome = (ProcessAudioRoutingResult)outcome, WrittenPreference = new(available, mixed, id) };
        var result = await fixture.Execute(Command("set", "output"));
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains(code, result.Output);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrAmbiguousTargetsDoNotWriteOrAcquireOwnership(bool ambiguous)
    {
        var fixture = new RoutingFixture();
        if (ambiguous) fixture.Devices.Add(new() { Id = "another-device", Name = "Private headphones" });
        else fixture.Devices.Clear();
        int scopes = 0;
        Task Scope(uint _, bool __, Func<Task> ___, CancellationToken ____) { scopes++; return Task.CompletedTask; }
        var result = await CliAppRoutingOperation.ExecuteAsync(Command("set", "input"), fixture.Operations,
            TestContext.Current.CancellationToken, Scope, _ => fixture.Target);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains(ambiguous ? "app-route-device-ambiguous" : "app-route-device-unavailable", result.Output);
        Assert.Empty(fixture.Writes);
        Assert.Equal(0, scopes);
    }

    [Fact]
    public async Task CancellationWhileWaitingForManualOwnershipDoesNotSend()
    {
        var fixture = new RoutingFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task Scope(uint _, bool __, Func<Task> ___, CancellationToken token) { cancellation.Cancel(); return Task.FromCanceled(token); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliAppRoutingOperation.ExecuteAsync(Command("clear", "output"),
            fixture.Operations, cancellation.Token, Scope, _ => fixture.Target));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task MalformedForwardedCommandIsRejectedBeforeDiscovery()
    {
        var fixture = new RoutingFixture();
        var command = new CliCommand { Action = CliAction.AppRouteClearInput, Key = "pid:123", Value = "[id]unexpected" };
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out var forwarded));
        var result = await fixture.Execute(forwarded);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task FaultAfterSubmissionIsUnknownAndIsNeverRetried()
    {
        var fixture = new RoutingFixture { ChangeError = new InvalidOperationException("private error") };
        var result = await fixture.Execute(Command("clear", "input"));
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("app-route-result-unknown", result.Output);
        Assert.DoesNotContain("private error", result.Output);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableOrAmbiguousProcessesDoNotReadOrWrite(bool ambiguous)
    {
        var fixture = new RoutingFixture();
        var result = await CliAppRoutingOperation.ExecuteAsync(Command("get", "output"), fixture.Operations,
            TestContext.Current.CancellationToken, resolveProcess: selector => throw (ambiguous ? (Exception)new AmbiguousMatchException() : new ArgumentException("Process not found.", nameof(selector))));
        Assert.Equal(5, result.ExitCode);
        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedactionCoversPreferencesInTextAndJson(bool json)
    {
        var fixture = new RoutingFixture();
        string[] args = ["app", "route", "get", "output", "--pid", "123", "--redact"];
        if (json) args = [.. args, "--json"];
        Assert.True(CliCommand.TryParse(args, out var command, out _));
        var result = await fixture.Execute(command);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("private-player", result.Output);
        Assert.DoesNotContain("device-private", result.Output);
        Assert.DoesNotContain("Private headphones", result.Output);
        if (json)
        {
            using var parsed = JsonDocument.Parse(result.Output!);
            Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("data").GetProperty("processId").ValueKind);
        }
    }

    [Theory]
    [InlineData("get", "output")]
    [InlineData("set", "input")]
    [InlineData("clear", "output")]
    public async Task HeadlessHostUsesTheSameRoutingPathWithoutCreatingMixers(string operation, string direction)
    {
        using var process = Process.GetCurrentProcess();
        var fixture = new RoutingFixture();
        var factories = new LocalHeadlessCommandRunner.RuntimeServiceFactories(
            CreateAudioService: () => throw new InvalidOperationException("The test must not create native audio services."));
        using var runner = new LocalHeadlessCommandRunner(factories,
            new LocalHeadlessCommandRunner.AudioOverrides(ApplicationRouting: fixture.Operations));
        string[] args = ["app", "route", operation, direction, "--pid", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json"];
        if (operation == "set") args = [.. args, "--device-id", "device-private"];
        Assert.True(CliCommand.TryParse(args, out var command, out _));
        var result = await CliCommandExecutor.ExecuteAsync(command, runner);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal((checked((uint)process.Id), direction == "output", (long?)process.StartTime.ToUniversalTime().Ticks), Assert.Single(fixture.Reads));
        Assert.Equal(operation == "get" ? 0 : 1, fixture.Writes.Count);
        runner.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runner.ExecuteAppRoutingAsync(command));
    }

    private static CliCommand Command(string operation, string direction)
    {
        string[] args = ["app", "route", operation, direction, "--pid", "123", "--json"];
        if (operation == "set") args = [.. args, "--device", "Private headphones"];
        Assert.True(CliCommand.TryParse(args, out var command, out string? error), error);
        return command;
    }

    private sealed class RoutingFixture
    {
        internal CliAppRoutingOperation.Target Target { get; } = new(123, "private-player", 42);
        internal List<CycleDevice> Devices { get; } = [new() { Id = "device-private", Name = "Private headphones" }];
        internal ApplicationDevicePreference Preference { get; init; } = new(true, false, "device-private");
        internal ApplicationDevicePreference? WrittenPreference { get; init; }
        internal ProcessAudioRoutingResult Outcome { get; init; } = ProcessAudioRoutingResult.Applied;
        internal Exception? ChangeError { get; init; }
        internal List<(uint, bool, long?)> Reads { get; } = [];
        internal List<(uint, bool, long, string?)> Writes { get; } = [];
        internal ApplicationRoutingOperations Operations => new((pid, output, started, token) =>
        {
            token.ThrowIfCancellationRequested();
            Reads.Add((pid, output, started));
            return Task.FromResult(new ApplicationRoutingInventory(Preference, Devices, started ?? 42));
        }, (pid, output, started, device, token) =>
        {
            token.ThrowIfCancellationRequested();
            Writes.Add((pid, output, started, device));
            return ChangeError == null ? Task.FromResult(new ApplicationRoutingChange(Outcome, WrittenPreference ?? new(true, false, device)))
                : Task.FromException<ApplicationRoutingChange>(ChangeError);
        });
        internal Task<CliExecutionResult> Execute(CliCommand command) => CliAppRoutingOperation.ExecuteAsync(command, Operations,
            TestContext.Current.CancellationToken, resolveProcess: _ => Target);
    }
}

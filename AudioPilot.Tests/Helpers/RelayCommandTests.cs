using System.ComponentModel;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public class RelayCommandTests
{
    [Fact]
    public void AddObservedSource_RaisesCanExecuteChanged_OnSourcePropertyChanged()
    {
        var observed = new TestObservable();
        var command = new RelayCommand(() => { }, () => true);
        int raiseCount = 0;
        command.CanExecuteChanged += (_, _) => raiseCount++;

        command.AddObservedSource(observed);
        observed.RaisePropertyChanged(nameof(TestObservable.Value));

        Assert.True(raiseCount >= 2);
    }

    [Fact]
    public void RemoveObservedSource_StopsCanExecuteChangedNotifications()
    {
        var observed = new TestObservable();
        var command = new RelayCommand(() => { }, () => true);
        int raiseCount = 0;
        command.CanExecuteChanged += (_, _) => raiseCount++;

        command.AddObservedSource(observed);
        observed.RaisePropertyChanged(nameof(TestObservable.Value));
        int countAfterObserved = raiseCount;

        command.RemoveObservedSource(observed);
        observed.RaisePropertyChanged(nameof(TestObservable.Value));

        Assert.Equal(countAfterObserved, raiseCount);
    }

    [Fact]
    public void Dispose_UnsubscribesFromAllObservedSources()
    {
        var observedA = new TestObservable();
        var observedB = new TestObservable();
        var command = new RelayCommand(() => { }, () => true);
        int raiseCount = 0;
        command.CanExecuteChanged += (_, _) => raiseCount++;

        command.AddObservedSource(observedA);
        command.AddObservedSource(observedB);
        command.Dispose();

        int countAfterDispose = raiseCount;
        observedA.RaisePropertyChanged(nameof(TestObservable.Value));
        observedB.RaisePropertyChanged(nameof(TestObservable.Value));

        Assert.Equal(countAfterDispose, raiseCount);
    }

    [Fact]
    public async Task AsyncExecute_DisablesReentryUntilCompletion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;
        var command = new RelayCommand(async () =>
        {
            Interlocked.Increment(ref executionCount);
            started.TrySetResult();
            await release.Task;
        });

        command.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.False(command.CanExecute(null));
        command.Execute(null);
        release.TrySetResult();
        await command.LastExecutionTaskForTests.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, executionCount);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AsyncExecute_ReportsExceptionsWithoutFaultingCommandTask()
    {
        Exception? reportedException = null;
        var command = new RelayCommand(
            () => Task.FromException(new InvalidOperationException("test failure")),
            ex => reportedException = ex);

        command.Execute(null);
        await command.LastExecutionTaskForTests.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.IsType<InvalidOperationException>(reportedException);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ParameterizedAsyncExecute_PassesClickedRowAndHonorsCanExecute()
    {
        object expected = new();
        object? received = null;
        var command = new RelayCommand(
            parameter =>
            {
                received = parameter;
                return Task.CompletedTask;
            },
            parameter => ReferenceEquals(parameter, expected));

        Assert.False(command.CanExecute(new object()));
        Assert.True(command.CanExecute(expected));
        command.Execute(expected);
        await command.LastExecutionTaskForTests.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Same(expected, received);
    }

    private sealed class TestObservable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public int Value { get; set; }

        public void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

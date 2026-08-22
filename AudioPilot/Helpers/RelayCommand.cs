using System.ComponentModel;
using System.Windows.Input;

namespace AudioPilot.Helpers
{
    public class RelayCommand : ICommand, IDisposable
    {
        private readonly Action<object?>? _execute;
        private readonly Func<Task>? _executeAsync;
        private readonly Func<object?, Task>? _executeAsyncWithParameter;
        private readonly Func<object?, bool>? _canExecute;
        private readonly Action<Exception>? _exceptionHandler;
        private HashSet<INotifyPropertyChanged>? _observedSources;
        private readonly Lock _lock = new();
        private int _isExecuting;

        internal Task LastExecutionTaskForTests { get; private set; } = Task.CompletedTask;

        public RelayCommand(Action execute)
            : this(_ => execute(), _ => true) { }

        public RelayCommand(Action execute, Func<bool> canExecute)
            : this(_ => execute(), _ => canExecute()) { }

        public RelayCommand(Func<Task> executeAsync, Action<Exception>? exceptionHandler = null)
            : this(executeAsync, static () => true, exceptionHandler)
        {
        }

        public RelayCommand(
            Func<Task> executeAsync,
            Func<bool> canExecute,
            Action<Exception>? exceptionHandler = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            ArgumentNullException.ThrowIfNull(canExecute);
            _canExecute = _ => canExecute();
            _exceptionHandler = exceptionHandler;
        }

        public RelayCommand(
            Func<object?, Task> executeAsync,
            Func<object?, bool>? canExecute = null,
            Action<Exception>? exceptionHandler = null)
        {
            _executeAsyncWithParameter = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
            _exceptionHandler = exceptionHandler;
        }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            : this(execute, canExecute, null) { }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute, INotifyPropertyChanged? observedSource)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;

            if (observedSource != null)
            {
                _observedSources = [observedSource];
                observedSource.PropertyChanged += OnObservedPropertyChanged;
            }
        }

        public bool CanExecute(object? parameter)
        {
            return Volatile.Read(ref _isExecuting) == 0 &&
                (_canExecute?.Invoke(parameter) ?? true);
        }

        public void Execute(object? parameter)
        {
            if (_executeAsync == null && _executeAsyncWithParameter == null)
            {
                _execute!(parameter);
                return;
            }

            if ((_canExecute?.Invoke(parameter) ?? true) == false ||
                Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
            {
                return;
            }

            LastExecutionTaskForTests = ExecuteAsyncCore(parameter);
        }

        private async Task ExecuteAsyncCore(object? parameter)
        {
            RaiseCanExecuteChanged();
            try
            {
                if (_executeAsyncWithParameter != null)
                {
                    await _executeAsyncWithParameter(parameter);
                }
                else
                {
                    await _executeAsync!();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                try
                {
                    _exceptionHandler?.Invoke(ex);
                }
                catch
                {
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        private EventHandler? _canExecuteChanged;

        public event EventHandler? CanExecuteChanged
        {
            add
            {
                lock (_lock)
                {
                    _canExecuteChanged += value;
                }
            }
            remove
            {
                lock (_lock)
                {
                    _canExecuteChanged -= value;
                }
            }
        }

        private void OnObservedPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            RaiseCanExecuteChanged();
        }

        public void RaiseCanExecuteChanged()
        {
            EventHandler? handler;
            lock (_lock)
            {
                handler = _canExecuteChanged;
            }
            handler?.Invoke(this, EventArgs.Empty);
        }

        public void AddObservedSource(INotifyPropertyChanged source)
        {
            _observedSources ??= [];

            if (_observedSources.Add(source))
            {
                source.PropertyChanged += OnObservedPropertyChanged;
                RaiseCanExecuteChanged();
            }
        }

        public void RemoveObservedSource(INotifyPropertyChanged source)
        {
            if (_observedSources == null)
            {
                return;
            }

            if (_observedSources.Remove(source))
            {
                source.PropertyChanged -= OnObservedPropertyChanged;
            }
        }

        public void Dispose()
        {
            if (_observedSources == null)
            {
                GC.SuppressFinalize(this);
                return;
            }

            foreach (var source in _observedSources)
            {
                source.PropertyChanged -= OnObservedPropertyChanged;
            }

            _observedSources.Clear();
            _observedSources = null;
            GC.SuppressFinalize(this);
        }
    }
}

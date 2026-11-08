using System.Collections.Concurrent;
using System.Reflection;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceHelperCacheConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryAndTrimming_DoNotRemoveAConcurrentlyRefreshedEntry(bool trim)
    {
        Type entryType = typeof(AudioDeviceHelper).GetNestedType("CacheEntry", BindingFlags.NonPublic)!;
        Type dictionaryType = typeof(ConcurrentDictionary<,>).MakeGenericType(typeof(GatedKey), entryType);
        object cache = Activator.CreateInstance(dictionaryType)!;
        PropertyInfo indexer = dictionaryType.GetProperty("Item")!;
        object Entry(string value, long expiry) => Activator.CreateInstance(entryType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [value, expiry, null], null)!;
        using var key = new GatedKey(42, trim ? 1 : 2);
        using var other = new GatedKey(43, int.MaxValue);
        indexer.SetValue(cache, Entry("expired", 99), [key]);
        if (trim) indexer.SetValue(cache, Entry("older fresh entry", 20_000), [other]);
        MethodInfo operation = typeof(AudioDeviceHelper).GetMethod(trim ? "TrimCache" : "TryGetCachedValue",
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(GatedKey));
        Task pending = Task.Run(() =>
        {
            key.ReaderThread = Environment.CurrentManagedThreadId;
            operation.Invoke(null, trim ? [cache, 1, 100L] : [cache, key, 100L, null, null]);
        }, TestContext.Current.CancellationToken);
        try
        {
            await key.ReachedRemoval.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            indexer.SetValue(cache, Entry("newly refreshed", 30_000), [key]);
        }
        finally
        {
            key.Release.Set();
            await pending;
        }

        object remaining = indexer.GetValue(cache, [key])!;
        Assert.Equal("newly refreshed", entryType.GetProperty("Value")!.GetValue(remaining));
        Assert.Equal(1, dictionaryType.GetProperty("Count")!.GetValue(cache));
    }

    /// <summary>Pauses removal after an expired entry was observed, allowing a real concurrent dictionary update.</summary>
    private sealed class GatedKey(int hash, int gateAtHash) : IDisposable
    {
        internal int ReaderThread;
        private int _readerHashCount;
        internal TaskCompletionSource ReachedRemoval { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();

        public override int GetHashCode()
        {
            if (Environment.CurrentManagedThreadId == ReaderThread && Interlocked.Increment(ref _readerHashCount) == gateAtHash)
            {
                ReachedRemoval.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                    throw new TimeoutException("Concurrent refresh did not release cache removal.");
            }
            return hash;
        }

        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public void Dispose() => Release.Dispose();
    }
}

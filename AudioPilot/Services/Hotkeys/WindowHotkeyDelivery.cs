namespace AudioPilot.Services.Hotkeys;

/// <summary>Arbitrates native and hook delivery for one registered window shortcut.</summary>
internal sealed class WindowHotkeyDelivery
{
    internal const int NativeWaitMs = 80;
    private const int MatchWindowMs = 20;
    private const int MaxMessageAgeMs = 2000;
    private const int DebounceMs = (int)(Constants.AppConstants.Timing.HotkeyDebounceTicks / TimeSpan.TicksPerMillisecond);
    private readonly Lock _gate = new();
    private readonly List<Press> _recent = [];
    private long _generation;

    internal sealed class Press(uint messageTime, bool observedByHook)
    {
        internal uint MessageTime { get; } = messageTime;
        internal bool ObservedByHook { get; } = observedByHook;
        internal uint? ReleasedAt { get; set; }
        internal uint? NativeTime { get; set; }
        internal bool Claimed { get; set; }
        internal bool Dispatched { get; set; }
    }

    internal Press? ObserveHook(uint messageTime, uint now, long generation)
    {
        lock (_gate)
        {
            if (!Prepare(messageTime, now, generation)) return null;
            if (_recent.Exists(press => !press.ObservedByHook && IsNear(messageTime, press.MessageTime))) return null;
            var press = new Press(messageTime, observedByHook: true);
            _recent.Add(press);
            return press;
        }
    }

    internal void ObserveRelease(uint pressTime, uint now, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            Press? press = _recent.FindLast(candidate => candidate.ObservedByHook && candidate.MessageTime == pressTime);
            press?.ReleasedAt = now;
        }
    }

    internal bool ClaimNative(uint messageTime, uint now, long generation)
    {
        lock (_gate)
        {
            if (!Prepare(messageTime, now, generation)) return false;
            if (_recent.Exists(press => press.NativeTime == messageTime)) return false;
            Press? press = FindHookPress(messageTime);
            if (press == null)
            {
                press = new Press(messageTime, observedByHook: false);
                _recent.Add(press);
            }
            press.NativeTime = messageTime;
            return Claim(press);
        }
    }

    internal bool ClaimFallback(Press press, uint now, long generation)
    {
        lock (_gate)
        {
            return generation == _generation && !IsStale(now, press.MessageTime) && Claim(press);
        }
    }

    private bool Claim(Press press)
    {
        if (press.Claimed) return false;
        press.Claimed = true;
        foreach (Press other in _recent)
        {
            int interval = unchecked((int)(press.MessageTime - other.MessageTime));
            if (other.Dispatched && interval is > -DebounceMs and < DebounceMs) return false;
        }
        return press.Dispatched = true;
    }

    private bool Prepare(uint messageTime, uint now, long generation)
    {
        if (generation < _generation || IsStale(now, messageTime)) return false;
        if (generation != _generation)
        {
            _generation = generation;
            _recent.Clear();
        }
        // Keep the extra matching interval so a still-admissible native message can find its earlier hook press.
        _recent.RemoveAll(press => IsStale(unchecked(now - MatchWindowMs), press.MessageTime));
        return true;
    }

    private Press? FindHookPress(uint messageTime)
    {
        Press? closest = null, openAtPosting = null;
        int closestDistance = MatchWindowMs + 1;
        foreach (var press in _recent)
        {
            if (!press.ObservedByHook || press.NativeTime.HasValue) continue;
            int delta = unchecked((int)(messageTime - press.MessageTime));
            if (delta is >= -MatchWindowMs and <= MatchWindowMs && Math.Abs(delta) < closestDistance)
            {
                closest = press;
                closestDistance = Math.Abs(delta);
            }
            // A native message posted late can still belong to this key-down cycle. The
            // observed release bounds that match so it cannot swallow a later native press.
            if (delta >= 0 && (press.ReleasedAt is not uint released || unchecked((int)(messageTime - released)) <= 0))
                openAtPosting = press;
        }
        return openAtPosting ?? closest;
    }

    private static bool IsNear(uint left, uint right) => unchecked((int)(left - right)) is >= -MatchWindowMs and <= MatchWindowMs;

    private static bool IsStale(uint now, uint messageTime) => unchecked((int)(now - messageTime)) > MaxMessageAgeMs;
}

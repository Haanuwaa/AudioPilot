using System.Collections.Concurrent;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal sealed class MixerSharedSessionCoordinator(AudioMixerMode preferredOwnerMode)
    {
        private sealed class SharedSessionSubscriptionState
        {
            public readonly Lock Lock = new();
            public readonly HashSet<MixerViewModel> PresentMixers = [];
            public MixerViewModel? Owner;
        }

        private readonly ConcurrentDictionary<string, AudioSessionItem> _sharedItems = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SharedSessionSubscriptionState> _subscriptionStates = new(StringComparer.OrdinalIgnoreCase);

        public AudioMixerMode PreferredOwnerMode { get; } = preferredOwnerMode;

        public AudioSessionItem GetOrAdd(string sessionId, Func<AudioSessionItem> itemFactory)
        {
            return _sharedItems.GetOrAdd(sessionId, static (_, factory) => factory(), itemFactory);
        }

        public void AttachVolumeChangedHandler(string sessionId, AudioSessionItem item, MixerViewModel mixer)
        {
            SharedSessionSubscriptionState subscriptionState = _subscriptionStates.GetOrAdd(
                sessionId,
                static _ => new SharedSessionSubscriptionState());

            lock (subscriptionState.Lock)
            {
                subscriptionState.PresentMixers.Add(mixer);

                MixerViewModel? desiredOwner = ResolvePreferredOwner(
                    subscriptionState.PresentMixers,
                    PreferredOwnerMode);
                if (ReferenceEquals(desiredOwner, subscriptionState.Owner))
                {
                    return;
                }

                subscriptionState.Owner?.DetachOwnedVolumeChangedHandler(sessionId, item);
                desiredOwner?.AttachOwnedVolumeChangedHandler(sessionId, item);
                subscriptionState.Owner = desiredOwner;
            }
        }

        public void DetachVolumeChangedHandler(string sessionId, AudioSessionItem item, MixerViewModel mixer)
        {
            if (!_subscriptionStates.TryGetValue(sessionId, out SharedSessionSubscriptionState? subscriptionState))
            {
                mixer.DetachOwnedVolumeChangedHandler(sessionId, item);
                return;
            }

            lock (subscriptionState.Lock)
            {
                subscriptionState.PresentMixers.Remove(mixer);

                if (ReferenceEquals(subscriptionState.Owner, mixer))
                {
                    mixer.DetachOwnedVolumeChangedHandler(sessionId, item);
                    MixerViewModel? nextOwner = ResolvePreferredOwner(
                        subscriptionState.PresentMixers,
                        PreferredOwnerMode);
                    nextOwner?.AttachOwnedVolumeChangedHandler(sessionId, item);
                    subscriptionState.Owner = nextOwner;
                }

                if (subscriptionState.PresentMixers.Count == 0)
                {
                    _subscriptionStates.TryRemove(sessionId, out _);
                }
            }
        }

        private static MixerViewModel? ResolvePreferredOwner(
            IEnumerable<MixerViewModel> mixers,
            AudioMixerMode preferredOwnerMode)
        {
            MixerViewModel? fallbackOwner = null;
            foreach (MixerViewModel mixer in mixers)
            {
                if (mixer.IsDisposed)
                {
                    continue;
                }

                if (mixer.MixerMode == preferredOwnerMode)
                {
                    return mixer;
                }

                fallbackOwner ??= mixer;
            }

            return fallbackOwner;
        }
    }
}

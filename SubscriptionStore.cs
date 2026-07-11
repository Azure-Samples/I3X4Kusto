namespace I3X4Kusto
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    /// <summary>
    /// In-memory store for i3X subscriptions. Holds each subscription's monitored elements, the
    /// per-element high-water-mark timestamp already delivered, a monotonic sequence counter, and a
    /// bounded staging queue of pending <see cref="SyncBatch"/> updates.
    ///
    /// State is process-local. The adapter is deployed as a single replica (ACA min/maxReplicas = 1),
    /// so this is sufficient; a multi-replica deployment would need a shared store (e.g. a Kusto or
    /// Redis backing store) and sticky routing.
    /// </summary>
    public sealed class SubscriptionStore
    {
        /// <summary>Maximum number of pending batches retained per subscription before overflow.</summary>
        public const int MaxPendingBatches = 1000;

        private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

        public Subscription Create(string clientId, string displayName)
        {
            var sub = new Subscription
            {
                SubscriptionId = Guid.NewGuid().ToString(),
                ClientId = clientId,
                DisplayName = displayName
            };

            _subscriptions[sub.SubscriptionId] = sub;
            return sub;
        }

        public bool TryGet(string subscriptionId, out Subscription subscription) =>
            _subscriptions.TryGetValue(subscriptionId ?? string.Empty, out subscription);

        public bool Delete(string subscriptionId) =>
            _subscriptions.TryRemove(subscriptionId ?? string.Empty, out _);

        /// <summary>Represents a single subscription's mutable state.</summary>
        public sealed class Subscription
        {
            private readonly object _gate = new();
            private long _nextSequence = 1;
            private readonly List<SyncBatch> _pending = new();

            public string SubscriptionId { get; init; }

            public string ClientId { get; init; }

            public string DisplayName { get; init; }

            /// <summary>Monitored element id -> requested maxDepth.</summary>
            public ConcurrentDictionary<string, int> MonitoredElements { get; } = new();

            /// <summary>Element id -> last delivered value timestamp (UTC), so only newer values are sent.</summary>
            public ConcurrentDictionary<string, DateTime> LastSeen { get; } = new();

            public void AddElements(IEnumerable<string> elementIds, int maxDepth)
            {
                foreach (var id in elementIds ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    MonitoredElements[id] = maxDepth;
                }
            }

            public void RemoveElements(IEnumerable<string> elementIds)
            {
                foreach (var id in elementIds ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    MonitoredElements.TryRemove(id, out _);
                    LastSeen.TryRemove(id, out _);
                }
            }

            public IReadOnlyList<string> Elements => MonitoredElements.Keys.ToList();

            /// <summary>
            /// Stage a new batch of updates. Returns false if the pending queue overflowed
            /// (oldest batches dropped), which the caller surfaces as HTTP 206.
            /// </summary>
            public bool StageBatch(IReadOnlyList<SyncUpdateEntry> updates)
            {
                if (updates == null || updates.Count == 0)
                {
                    return true;
                }

                lock (_gate)
                {
                    _pending.Add(new SyncBatch(_nextSequence++, updates));

                    bool overflowed = false;
                    while (_pending.Count > MaxPendingBatches)
                    {
                        _pending.RemoveAt(0);
                        overflowed = true;
                    }

                    return !overflowed;
                }
            }

            /// <summary>Acknowledge (remove) all pending batches with sequenceNumber &lt;= lastSequenceNumber.</summary>
            public void Acknowledge(long lastSequenceNumber)
            {
                lock (_gate)
                {
                    _pending.RemoveAll(b => b.SequenceNumber <= lastSequenceNumber);
                }
            }

            /// <summary>Snapshot of the currently pending batches, oldest first.</summary>
            public IReadOnlyList<SyncBatch> PendingSnapshot()
            {
                lock (_gate)
                {
                    return _pending.ToList();
                }
            }
        }
    }
}

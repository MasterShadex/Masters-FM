using System.Collections.Generic;

namespace MastersFM.Server;

/// <summary>
/// Thread-safe LRU (Least Recently Used) cache with configurable capacity.
///
/// CRITICAL: Implemented as a single synchronized class using LinkedList + Dictionary.
/// Do NOT refactor into separate Queue + Dictionary structures -- they can drift out of
/// sync under concurrent access, corrupting the eviction order (V14 plan risk).
///
/// Registered as a singleton: <c>builder.Services.AddSingleton(new LruCache&lt;string, string&gt;(200))</c>
/// Used by the art cascade (4.9b-e) to avoid redundant lookups for the same track.
/// Cache key: "artist|||track" (both lowercased). Cache value: resolved art URL or "".
///
/// Preserved behavior from v11.0.0 (200-entry LRU pattern in the original Node server).
/// </summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    // Doubly-linked list maintains MRU-to-LRU order. Head = most recently used.
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    // Dictionary maps key -> node for O(1) lookup and O(1) removal from the linked list.
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map = new();
    private readonly object _lock = new();

    public LruCache(int capacity)
    {
        _capacity = capacity;
    }

    /// <summary>
    /// Returns the number of entries currently in the cache.
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    /// <summary>
    /// Looks up a key. On hit: promotes to MRU position and returns true.
    /// On miss: sets <paramref name="value"/> to default and returns false.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // Promote to head (most recently used)
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Inserts or updates a key-value pair. Promotes to MRU position.
    /// If adding this entry would exceed capacity, the LRU entry (tail) is evicted.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            // Remove existing entry for this key (update case)
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }

            // Insert at head (most recently used)
            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(
                new KeyValuePair<TKey, TValue>(key, value));
            _order.AddFirst(node);
            _map[key] = node;

            // Evict LRU entries until within capacity
            while (_order.Count > _capacity)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _map.Remove(lru.Value.Key);
            }
        }
    }
}

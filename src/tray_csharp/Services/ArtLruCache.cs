// Stage 7.5: ArtLruCache. Bounded LRU for art URIs. Cap 200 entries
// (matches PS S14). Thread-safe via single lock; LinkedList for O(1)
// move-to-front + Dictionary for O(1) lookup.

namespace MastersFM.Tray.Services;

public sealed class ArtLruCache
{
    private const int Capacity = 200;
    private const string Component = "ArtCache";

    private readonly ILogger _logger;
    private readonly ITelemetry _telemetry;
    private readonly object _lock = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _map = new(Capacity);

    public ArtLruCache(ILogger logger, ITelemetry telemetry)
    {
        _logger = logger;
        _telemetry = telemetry;
    }

    /// <summary>Records access of `key`. Inserts on miss, moves to MRU on hit. Returns true if the key was already present (cache hit).</summary>
    public bool Touch(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                _telemetry.IncrementCounter("art_cache_hits");
                return true;
            }

            // Miss: insert; evict LRU if needed
            var newNode = new LinkedListNode<string>(key);
            _order.AddFirst(newNode);
            _map[key] = newNode;
            _telemetry.IncrementCounter("art_cache_misses");

            if (_map.Count > Capacity)
            {
                var lru = _order.Last;
                if (lru != null)
                {
                    _order.RemoveLast();
                    _map.Remove(lru.Value);
                    _telemetry.IncrementCounter("art_cache_evictions");
                }
            }
            return false;
        }
    }

    /// <summary>Current cache size. Diagnostic only.</summary>
    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }
}

using System.Text.Json;

namespace Qalam.Core;

/// <summary>
/// Learns which word the user usually writes after which ("дорогие" → "друзья"), so completion
/// and next-word hints follow the user's own phrases. Only pairs of ordinary dictionary words are
/// stored (never passwords, numbers or names), only on this computer, in phrases.json.
/// </summary>
public sealed class UserPhrases
{
    private readonly Dictionary<string, Dictionary<string, int>> _next = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private int _pairs;
    private bool _dirty;
    private DateTime _lastSave = DateTime.UtcNow;
    public string? FilePath { get; set; }
    public int MaxPairs { get; set; } = 40000;

    public int PairCount { get { lock (_lock) return _pairs; } }

    public void Learn(string prev, string next)
    {
        if (prev.Length == 0 || next.Length == 0) return;
        lock (_lock)
        {
            if (!_next.TryGetValue(prev, out var d)) _next[prev] = d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (d.TryGetValue(next, out var n)) d[next] = Math.Min(n + 1, 10000);
            else { d[next] = 1; _pairs++; }
            _dirty = true;
            if (_pairs > MaxPairs) Prune();
        }
        if ((DateTime.UtcNow - _lastSave).TotalSeconds > 60) Save();
    }

    public int Count(string prev, string next)
    {
        lock (_lock) return _next.TryGetValue(prev, out var d) && d.TryGetValue(next, out var n) ? n : 0;
    }

    /// <summary>Words seen after <paramref name="prev"/>, most frequent first.</summary>
    public List<(string Word, int Count)> After(string prev, int max = 8)
    {
        lock (_lock)
            return _next.TryGetValue(prev, out var d)
                ? d.OrderByDescending(kv => kv.Value).Take(max).Select(kv => (kv.Key, kv.Value)).ToList()
                : new();
    }

    public void Clear()
    {
        lock (_lock) { _next.Clear(); _pairs = 0; _dirty = true; }
        Save();
    }

    private void Prune()
    {
        // forget pairs seen only once (the oldest noise), then halve the counts
        foreach (var (prev, d) in _next.ToList())
        {
            foreach (var k in d.Where(kv => kv.Value <= 1).Select(kv => kv.Key).ToList()) { d.Remove(k); _pairs--; }
            foreach (var k in d.Keys.ToList()) d[k] = Math.Max(1, d[k] / 2);
            if (d.Count == 0) _next.Remove(prev);
        }
    }

    public static UserPhrases Load(string path)
    {
        var p = new UserPhrases { FilePath = path };
        try
        {
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(path));
                if (dto != null)
                    foreach (var (k, v) in dto) { p._next[k] = new Dictionary<string, int>(v, StringComparer.Ordinal); p._pairs += v.Count; }
            }
        }
        catch { /* corrupted → start clean */ }
        return p;
    }

    public void Save()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty || FilePath == null) return;
            json = JsonSerializer.Serialize(_next);
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}

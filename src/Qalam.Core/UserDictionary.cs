using System.Text.Json;

namespace Qalam.Core;

/// <summary>
/// Learns from the user: words that must never be converted (exceptions) and own words
/// (names, terms) that count as real words. Persisted as JSON next to the settings.
/// </summary>
public sealed class UserDictionary
{
    private readonly HashSet<string> _exceptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    public string? FilePath { get; set; }

    public IReadOnlyCollection<string> Exceptions => _exceptions;
    public IReadOnlyCollection<string> KnownWords => _known;

    public bool IsException(string token) => _exceptions.Contains(Detector.Core(token));
    public bool IsKnown(string word) => _known.Contains(word);

    public void AddException(string token) { var c = Detector.Core(token); if (c.Length > 0 && _exceptions.Add(c)) Save(); }
    public void RemoveException(string token) { if (_exceptions.Remove(Detector.Core(token))) Save(); }
    public void AddKnown(string word) { if (_known.Add(Detector.Core(word))) Save(); }
    public void RemoveKnown(string word) { if (_known.Remove(Detector.Core(word))) Save(); }

    private sealed record Dto(List<string> Exceptions, List<string> Known);

    public static UserDictionary Load(string path)
    {
        var d = new UserDictionary { FilePath = path };
        try
        {
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
                if (dto != null) { d._exceptions.UnionWith(dto.Exceptions); d._known.UnionWith(dto.Known); }
            }
        }
        catch { /* corrupted file → start clean */ }
        return d;
    }

    public void Save()
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dto(_exceptions.Order().ToList(), _known.Order().ToList()),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

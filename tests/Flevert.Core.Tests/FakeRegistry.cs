using Flevert.Core.Menu;

namespace Flevert.Core.Tests;

/// <summary>In-memory registry. Keys exist only while they hold a value or a descendant does, like a pruned tree.</summary>
public sealed class FakeRegistry : IRegistry
{
    private readonly SortedDictionary<string, SortedDictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public void SetValue(string key, string name, string value)
    {
        if (!_keys.TryGetValue(key, out var values))
            _keys[key] = values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        values[name] = value;
    }

    public void DeleteTree(string key)
    {
        foreach (var k in _keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            _keys.Remove(k);
    }

    public IReadOnlyList<string> SubKeyNames(string key)
    {
        var prefix = key.Length == 0 ? "" : key + "\\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? GetValue(string key, string name) =>
        _keys.TryGetValue(key, out var values) && values.TryGetValue(name, out var v) ? v : null;

    /// <summary>Full paths of direct child keys, in registry (ordinal, case-insensitive) order.</summary>
    public IReadOnlyList<string> KeysUnder(string key) =>
        SubKeyNames(key).Order(StringComparer.OrdinalIgnoreCase).Select(n => $@"{key}\{n}").ToList();

    public IReadOnlyDictionary<string, string> Snapshot() =>
        _keys.ToDictionary(k => k.Key, k => string.Join(";", k.Value.Select(v => $"{v.Key}={v.Value}")), StringComparer.OrdinalIgnoreCase);
}

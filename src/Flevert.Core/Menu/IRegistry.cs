namespace Flevert.Core.Menu;

/// <summary>Registry access relative to a root such as HKCU\Software\Classes. An empty value name is the key's default value.</summary>
public interface IRegistry
{
    void SetValue(string key, string name, string value);

    /// <summary>The value as a string, or null when the key or the value is missing.</summary>
    string? GetValue(string key, string name);

    /// <summary>Deletes the key and its subtree, then removes ancestors left empty. Missing keys are ignored.</summary>
    void DeleteTree(string key);

    IReadOnlyList<string> SubKeyNames(string key);
}

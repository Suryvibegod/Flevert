using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flevert.Core.Updates;

/// <param name="Enabled">Whether to ask GitHub for new versions at all (setup's check box, update-check on|off).</param>
/// <param name="LastCheck">When GitHub was last asked; at most once a day.</param>
/// <param name="Skipped">A version the user chose "Skip this version" for.</param>
public sealed record UpdateState(bool Enabled, DateTimeOffset? LastCheck, string? Skipped)
{
    public static UpdateState Default { get; } = new(true, null, null);
}

/// <summary>
/// %LOCALAPPDATA%\Flevert\update\state.json. Anything unreadable counts as the defaults. Save throws on failure; the
/// update check ignores that, because it must never get in the way of converting.
/// </summary>
public sealed class UpdateStateStore(string path)
{
    public static string DefaultPath { get; } = Path.Combine(AppData.Root, AppData.UpdateFolder, "state.json");

    public UpdateState Load()
    {
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdateStateJson.Default.StoredState);
            return file is null ? UpdateState.Default : new UpdateState(file.Enabled ?? true, file.LastCheck, file.Skipped);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return UpdateState.Default;
        }
    }

    /// <summary>Writes a temporary file and moves it over the old one, so a crash never leaves half a file.</summary>
    public void Save(UpdateState state)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".state-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(
                new StoredState(state.Enabled, state.LastCheck, state.Skipped), UpdateStateJson.Default.StoredState));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal sealed record StoredState(
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("lastCheck")] DateTimeOffset? LastCheck,
        [property: JsonPropertyName("skipped")] string? Skipped);
}

[JsonSerializable(typeof(UpdateStateStore.StoredState))]
internal sealed partial class UpdateStateJson : JsonSerializerContext;

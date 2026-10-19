using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flevert.Core.Updates;

/// <param name="Enabled">Whether to ask GitHub for new versions at all (setup's check box, update-check on|off).</param>
/// <param name="LastCheck">When GitHub was last asked; at most once a day.</param>
/// <param name="Skipped">A version the user chose "Skip this version" for.</param>
public sealed record UpdateState(bool Enabled, DateTimeOffset? LastCheck, string? Skipped)
{
    public static UpdateState Default { get; } = new(true, null, null);

    /// <summary>A state file that exists but cannot be read right now: check nothing and write nothing this time.</summary>
    public static UpdateState Unreadable { get; } = new(false, null, null);
}

/// <summary>
/// %LOCALAPPDATA%\Flevert\update\state.json. Anything unreadable counts as the defaults. Save throws on failure; the
/// update check ignores that, because it must never get in the way of converting.
/// </summary>
public sealed class UpdateStateStore(string path)
{
    public static string DefaultPath { get; } = Path.Combine(AppData.Root, AppData.UpdateFolder, "state.json");

    /// <summary>How a turned-off check appears in the file; setup looks for it to keep the choice on an upgrade.</summary>
    public const string TurnedOffMarker = "\"enabled\":false";

    /// <summary>
    /// No file, or one whose content makes no sense: the defaults (on). Save flushes to disk before replacing the file,
    /// so damaged content does not come from Flevert, and keeping the check off for good would leave the user on an
    /// old version. A file that exists but cannot be read now (another program holds it): <see cref="UpdateState.Unreadable"/>,
    /// which checks nothing and writes nothing, so a turned-off check is never turned back on by accident.
    /// </summary>
    public UpdateState Load()
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return UpdateState.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return UpdateState.Unreadable;
        }

        try
        {
            var file = JsonSerializer.Deserialize(content, UpdateStateJson.Default.StoredState);
            return file is null ? UpdateState.Default : new UpdateState(file.Enabled ?? true, file.LastCheck, file.Skipped);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return UpdateState.Default;
        }
    }

    /// <summary>
    /// Writes a temporary file, flushes it to disk and moves it over the old one, so neither a crash nor a power cut leaves
    /// half a file.
    /// </summary>
    public void Save(UpdateState state)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".state-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(
                    new StoredState(state.Enabled, state.LastCheck, state.Skipped), UpdateStateJson.Default.StoredState));
                file.Flush(flushToDisk: true);
            }

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

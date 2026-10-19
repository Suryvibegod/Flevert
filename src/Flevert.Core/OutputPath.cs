namespace Flevert.Core;

public static class OutputPath
{
    private const int MaxFileNameLength = 255;
    private const int MaxAttempts = 10_000;

    /// <summary>
    /// Returns <paramref name="desiredPath"/> or the first free "name (n).ext" beside it. Anything with that name, file or
    /// directory, counts as taken, and the stem is shortened so the name always fits in 255 characters.
    /// </summary>
    public static string Reserve(string desiredPath)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        for (var n = 0; n < MaxAttempts; n++)
        {
            var candidate = Path.Combine(dir, FitName(stem, n == 0 ? "" : $" ({n})", ext));
            if (!IsTaken(candidate))
                return candidate;
        }

        throw new IOException($"No free output name next to {desiredPath}.");
    }

    public static async Task<string> WriteAtomicallyAsync(
        string desiredPath,
        Func<string, CancellationToken, Task> write,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        // Fixed-length name: embedding the output name could push a long file name past the 255-character limit.
        var temp = Path.Combine(dir, $".flevert-{Guid.NewGuid():N}.partial");

        try
        {
            await write(temp, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return MoveToFreeName(temp, desiredPath);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    // Another process can claim the reserved name between Reserve and Move, so retry on collision.
    private static string MoveToFreeName(string temp, string desiredPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            var final = Reserve(desiredPath);
            try
            {
                File.Move(temp, final, overwrite: false);
                return final;
            }
            catch (IOException) when (IsTaken(final) && attempt < MaxAttempts)
            {
            }
        }
    }

    // A dangling link occupies its name. Path.Exists already reports a dangling junction as present (checked with the
    // test's fallback removed); the attribute probe is a backstop for link kinds the tests cannot create without admin.
    private static bool IsTaken(string path)
    {
        if (Path.Exists(path))
            return true;
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string FitName(string stem, string suffix, string ext)
    {
        var room = MaxFileNameLength - suffix.Length - ext.Length;
        if (room < 1)
            throw new PathTooLongException($"The extension {ext[..Math.Min(ext.Length, 20)]}… leaves no room for a file name.");
        if (stem.Length > room)
        {
            stem = stem[..room];
            if (char.IsHighSurrogate(stem[^1]))
                stem = stem[..^1];
        }

        return stem + suffix + ext;
    }

    // A failed cleanup (e.g. antivirus holding the file) must not hide the original error.
    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

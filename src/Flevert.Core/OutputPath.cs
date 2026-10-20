namespace Flevert.Core;

public static class OutputPath
{
    private const int MaxFileNameLength = 255;
    private const int MaxAttempts = 10_000;

    /// <summary>
    /// Returns <paramref name="desiredPath"/> or the first free "name (n).ext" beside it. Anything with that name, file or
    /// directory, counts as taken, and the stem is shortened so the name always fits in 255 characters.
    /// </summary>
    public static string Reserve(string desiredPath) =>
        ReserveName(desiredPath, Path.GetFileNameWithoutExtension(desiredPath), "", Path.GetExtension(desiredPath));

    // A folder has no extension: "report.v2 (pages)" becomes "report.v2 (pages) (1)", not "report (1).v2 (pages)". The
    // suffix stays whole like an extension, so a long name loses characters of the stem, never "(pages)".
    private static string ReserveDirectory(string desiredDirectory, string keepSuffix)
    {
        var name = Path.GetFileName(desiredDirectory);
        return ReserveName(desiredDirectory, name[..^keepSuffix.Length], keepSuffix, "");
    }

    // "<stem><beforeCounter> (n)<afterCounter>", with only the stem shortened.
    private static string ReserveName(string desiredPath, string stem, string beforeCounter, string afterCounter)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        for (var n = 0; n < MaxAttempts; n++)
        {
            var candidate = Path.Combine(dir, FitName(stem, beforeCounter + (n == 0 ? "" : $" ({n})") + afterCounter));
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

    /// <summary>
    /// For an operation with many outputs (one file per page): <paramref name="write"/> fills an empty temporary folder
    /// beside <paramref name="desiredDirectory"/>, which is then renamed once to the first free name, "name" or
    /// "name (n)", by the rules of <see cref="Reserve"/>. An existing folder is never merged into or replaced. If the
    /// write throws or is cancelled, the temporary folder and everything in it is deleted.
    /// </summary>
    /// <returns>The folder created.</returns>
    public static Task<string> WriteDirectoryAtomicallyAsync(
        string desiredDirectory,
        Func<string, CancellationToken, Task> write,
        CancellationToken ct) =>
        WriteDirectoryAtomicallyAsync(desiredDirectory, "", write, ct);

    /// <inheritdoc cref="WriteDirectoryAtomicallyAsync(string, Func{string, CancellationToken, Task}, CancellationToken)"/>
    /// <param name="desiredDirectory">The folder wanted; its name ends with <paramref name="keepSuffix"/>.</param>
    /// <param name="keepSuffix">
    /// The end of the name, such as <c>" (pages)"</c>, kept whole when the name is shortened to 255 characters: only the
    /// part before it is cut, and a counter goes after it.
    /// </param>
    /// <param name="write">Fills the temporary folder it is given.</param>
    /// <param name="ct">Stops the write; the temporary folder is then deleted.</param>
    public static async Task<string> WriteDirectoryAtomicallyAsync(
        string desiredDirectory,
        string keepSuffix,
        Func<string, CancellationToken, Task> write,
        CancellationToken ct)
    {
        if (!Path.GetFileName(desiredDirectory).EndsWith(keepSuffix, StringComparison.Ordinal))
            throw new ArgumentException($"The folder name does not end with \"{keepSuffix}\".", nameof(keepSuffix));

        var dir = Path.GetDirectoryName(desiredDirectory)!;
        var temp = Path.Combine(dir, $".flevert-{Guid.NewGuid():N}.partial");
        // CreateDirectory succeeds on an existing folder; a GUID name is never one, and an empty folder must be handed over.
        Directory.CreateDirectory(temp);

        try
        {
            await write(temp, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return MoveToFreeName(temp, desiredDirectory, d => ReserveDirectory(d, keepSuffix), static (from, to) => Directory.Move(from, to));
        }
        finally
        {
            DeleteDirectoryQuietly(temp);
        }
    }

    private static string MoveToFreeName(string temp, string desiredPath) =>
        MoveToFreeName(temp, desiredPath, Reserve, static (from, to) => File.Move(from, to, overwrite: false));

    // Another process can claim the reserved name between Reserve and Move, so retry on collision. Neither move replaces
    // what is already there: both fail with IOException when the name exists, as a file or as a folder.
    private static string MoveToFreeName(string temp, string desiredPath, Func<string, string> reserve, Action<string, string> move)
    {
        for (var attempt = 0; ; attempt++)
        {
            var final = reserve(desiredPath);
            try
            {
                move(temp, final);
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

    private static string FitName(string stem, string tail)
    {
        var room = MaxFileNameLength - tail.Length;
        if (room < 1)
            throw new PathTooLongException($"The ending {tail[..Math.Min(tail.Length, 20)]}… leaves no room for a name.");
        if (stem.Length > room)
        {
            stem = stem[..room];
            if (char.IsHighSurrogate(stem[^1]))
                stem = stem[..^1];
        }

        return stem + tail;
    }

    // A failed cleanup (e.g. antivirus holding the file) must not hide the original error.
    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Gone after a successful move (DirectoryNotFoundException).
    private static void DeleteDirectoryQuietly(string path)
    {
        try { DeleteTree(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Not Directory.Delete(recursive): it calls DeleteVolumeMountPoint on a junction, which needs admin rights, and then
    // throws with the folder left behind. A link inside is removed itself, never followed, so whatever it points to is kept.
    private static void DeleteTree(string path)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo sub && !isLink)
            {
                DeleteTree(sub.FullName);
                continue;
            }

            // DeleteFile refuses a read-only file; a link is left as it is, since setting attributes could reach its target.
            if (!isLink && entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete(); // a directory link: RemoveDirectory on the link alone
        }

        Directory.Delete(path);
    }
}

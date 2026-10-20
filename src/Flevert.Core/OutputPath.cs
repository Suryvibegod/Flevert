using System.ComponentModel;

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
        return ReserveName(desiredDirectory, name[..^keepSuffix.Length], keepSuffix, "", folder: true);
    }

    // "<stem><beforeCounter> (n)<afterCounter>", with only the stem shortened.
    private static string ReserveName(string desiredPath, string stem, string beforeCounter, string afterCounter, bool folder = false)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        for (var n = 0; n < MaxAttempts; n++)
        {
            var name = FitName(stem, beforeCounter + (n == 0 ? "" : $" ({n})") + afterCounter);
            // Win32 drops a trailing dot or space from a path, and a shortened name can end in one; a folder is renamed
            // through its handle, which would keep it and make a folder Explorer cannot open.
            if (folder)
                name = name.TrimEnd('.', ' ');
            var candidate = Path.Combine(dir, name);
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
    /// For an operation with many outputs (one file per page): <paramref name="write"/> fills a temporary folder beside
    /// <paramref name="desiredDirectory"/>, which is then renamed once to the first free name, "name" or "name (n)", by
    /// the rules of <see cref="Reserve"/>. An existing folder is never merged into or replaced. If the write throws or is
    /// cancelled, the temporary folder and what the writer put in it is deleted.
    /// </summary>
    /// <remarks>
    /// The folder is held open until then, so nobody can rename or replace it (ADR-0007). It holds nothing but a hidden
    /// <c>.flevert.lock</c> when handed over; the writer creates each file with <see cref="FileMode.CreateNew"/>, since
    /// other accounts that can write beside the input can also add entries to it, and never makes subfolders.
    /// </remarks>
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

        desiredDirectory = Path.GetFullPath(desiredDirectory);
        var dir = Path.GetDirectoryName(desiredDirectory)!;
        using var folder = PartialFolder.Create(Path.Combine(dir, $".flevert-{Guid.NewGuid():N}.partial"));
        var renamed = false;
        try
        {
            await write(folder.Path, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var final = RenameToFreeName(folder, desiredDirectory, keepSuffix);
            renamed = true;
            // Emptied by someone else and turned into a junction after the anchor went: the junction is what was renamed.
            // Only the link is removed, and nothing is handed back.
            if (folder.IsLink())
            {
                folder.DeleteQuietly();
                throw new IOException("The folder for the output was replaced by a link before it got its name.");
            }

            return final;
        }
        finally
        {
            // Nothing is deleted by name after a rename: the temporary name is free then, and could be someone else's.
            if (!renamed)
                folder.DeleteQuietly();
        }
    }

    // Another process can claim the reserved name between Reserve and the rename, so retry on collision. The rename never
    // replaces what is already there: it fails when the name exists, as a file or as a folder.
    private static string RenameToFreeName(PartialFolder folder, string desiredDirectory, string keepSuffix)
    {
        for (var attempt = 0; ; attempt++)
        {
            var final = ReserveDirectory(desiredDirectory, keepSuffix);
            // The rename takes the name as it is, where Win32 would read some names as something else (a device such as
            // NUL on Windows 10): only a name that a path keeps unchanged is used.
            var plain = @"C:\" + Path.GetFileName(final);
            if (!string.Equals(Path.GetFullPath(plain), plain, StringComparison.Ordinal))
                throw new IOException($"\"{Path.GetFileName(final)}\" cannot be used as a folder name.");
            var error = folder.TryRenameTo(final);
            if (error == 0)
                return final;

            if (!(IsTaken(final) && attempt < MaxAttempts))
                throw new IOException($"The finished folder could not be named {Path.GetFileName(final)}: {new Win32Exception(error).Message}");
        }
    }

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
}

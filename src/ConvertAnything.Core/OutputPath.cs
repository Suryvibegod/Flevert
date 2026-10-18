namespace ConvertAnything.Core;

public static class OutputPath
{
    public static string Reserve(string desiredPath)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        var candidate = desiredPath;
        for (var n = 1; File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");

        return candidate;
    }

    public static async Task<string> WriteAtomicallyAsync(
        string desiredPath,
        Func<string, CancellationToken, Task> write,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        // Fixed-length name: embedding the output name could push a long file name past the 255-character limit.
        var temp = Path.Combine(dir, $".ca-{Guid.NewGuid():N}.partial");

        try
        {
            await write(temp, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return MoveToFreeName(temp, desiredPath);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // Another process can claim the reserved name between Reserve and Move, so retry on collision.
    private static string MoveToFreeName(string temp, string desiredPath)
    {
        while (true)
        {
            var final = Reserve(desiredPath);
            try
            {
                File.Move(temp, final, overwrite: false);
                return final;
            }
            catch (IOException) when (File.Exists(final))
            {
            }
        }
    }
}

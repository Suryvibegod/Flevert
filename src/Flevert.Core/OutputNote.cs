namespace Flevert.Core;

/// <summary>
/// A remark an operation makes about the output it is writing, which the user should read beside "Done": for example that
/// a signed PDF's digital signature is not valid in the copy (T18). The runner gives each file it runs a place for its
/// notes; the notification shows the note of a single output and counts them for several, and the log line of the file
/// ends with it.
/// </summary>
/// <remarks>
/// An ambient value rather than a change to <see cref="IOperation"/>: the place is set by the runner for the run of one
/// file and flows with it into every task and thread the operation awaits or starts (<see cref="AsyncLocal{T}"/>), so
/// files converted in parallel never see each other's notes, and operations that note nothing change in no way.
/// </remarks>
public static class OutputNote
{
    private static readonly AsyncLocal<Notes?> Current = new();

    /// <summary>
    /// Adds <paramref name="note"/>, one short English sentence, to the file the calling operation is working on; the same
    /// note twice counts once. Outside a run (a test calling an operation directly) it does nothing.
    /// </summary>
    public static void Add(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        Current.Value?.Add(note.Trim());
    }

    /// <summary>
    /// Records how large the input was and how large the output written for it is, which the notification shows: "Done:
    /// clip (under 25 MB).mp4 (24.3 MB, was 412 MB)", or the totals for several files. For operations whose point is
    /// size (compress). Outside a run it does nothing.
    /// </summary>
    public static void AddSizes(long originalBytes, long outputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(outputBytes);
        Current.Value?.SetSizes(new SizeChange(originalBytes, outputBytes));
    }

    /// <summary>
    /// Gives the calling flow a fresh place for notes and returns it. Called by the runner at the start of an async method
    /// that runs one file: the value then flows into that run only, and is gone when the method returns.
    /// </summary>
    internal static Notes Begin()
    {
        var notes = new Notes();
        Current.Value = notes;
        return notes;
    }

    internal sealed class Notes
    {
        private readonly Lock _gate = new();
        private readonly List<string> _notes = [];

        private SizeChange? _sizes;

        public SizeChange? Sizes
        {
            get
            {
                lock (_gate)
                    return _sizes;
            }
        }

        public void SetSizes(SizeChange sizes)
        {
            lock (_gate)
                _sizes = sizes;
        }

        public void Add(string note)
        {
            lock (_gate)
            {
                if (!_notes.Contains(note))
                    _notes.Add(note);
            }
        }

        /// <summary>Every note, in the order made, as one text; <c>null</c> when there is none.</summary>
        public string? Text
        {
            get
            {
                lock (_gate)
                    return _notes.Count == 0 ? null : string.Join(' ', _notes);
            }
        }
    }
}

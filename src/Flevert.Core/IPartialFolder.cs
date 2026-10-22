namespace Flevert.Core;

/// <summary>
/// The temporary folder <see cref="OutputPath"/> hands to an operation with many outputs: <see cref="PartialFolder"/> on
/// Windows, <see cref="UnixPartialFolder"/> on Linux.
/// </summary>
internal interface IPartialFolder : IDisposable
{
    /// <summary>The folder's real path; every path used afterwards is built from it.</summary>
    string Path { get; }

    /// <summary>Where the writer writes into the folder: <see cref="Path"/>, or on Linux the handle held, through which a
    /// link swapped in for the folder's name is never written through.</summary>
    string WritePath => Path;

    /// <summary>Readies the written folder for its rename.</summary>
    void PrepareForRename();

    /// <summary>After the rename: the folder looks like any other. Best effort.</summary>
    void Unhide();

    /// <summary>Renames the folder to <paramref name="destination"/> beside it, never replacing anything there.</summary>
    /// <returns>0, or the system's error code.</returns>
    int TryRenameTo(string destination);

    /// <summary>Whether what the folder's name leads to is no longer the folder held.</summary>
    bool IsLink();

    /// <summary>Deletes the folder and what the writer left in it, never following a link. Never throws.</summary>
    void DeleteQuietly();
}

namespace Flevert.Core;

/// <summary>
/// Where flevert.exe keeps its own files: %LOCALAPPDATA%\Flevert. The uninstaller deletes each subfolder by name
/// rather than the whole folder, so every subfolder flevert.exe writes must be listed here (InstallerScriptTests).
/// </summary>
public static class AppData
{
    public const string FolderName = "Flevert";
    public const string LogsFolder = "logs";
    public const string PipesFolder = "pipes";
    public const string MagickFolder = "magick";
    public const string UpdateFolder = "update";

    public static IReadOnlyList<string> Subfolders { get; } = [LogsFolder, PipesFolder, MagickFolder, UpdateFolder];

    public static string Root { get; } = Path.Combine(UserFolder(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>
    /// One of the user's folders. On Linux it may not exist yet (~/.local/share on a new account, or an XDG_DATA_HOME not
    /// made yet), and <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> then gives "", which would make
    /// every path built on it relative to the folder flevert was started in.
    /// </summary>
    public static string UserFolder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, OperatingSystem.IsWindows() ? Environment.SpecialFolderOption.None : Environment.SpecialFolderOption.DoNotVerify);
}

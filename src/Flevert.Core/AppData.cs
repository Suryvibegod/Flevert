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

    public static IReadOnlyList<string> Subfolders { get; } = [LogsFolder, PipesFolder, MagickFolder];

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
}

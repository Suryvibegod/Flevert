namespace Flevert.Core;

/// <summary>
/// flevert.exe's exit codes. The installer tells them apart (InstallerScriptTests): 1-3 are flevert.exe's own failures and come
/// with a reason on stderr; anything else means flevert.exe did not run, for example because Windows blocked it.
/// </summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>Some files failed, or a setup command failed for a reason it printed.</summary>
    public const int Failed = 1;

    public const int BadUsage = 2;

    /// <summary>The install folder can be changed by other accounts or is reached through a link: setup again cannot fix it.</summary>
    public const int RefusedLocation = 3;
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Flevert.Core.Updates;

namespace Flevert.Cli;

/// <summary>
/// The check for a new version that runs alongside a batch with UI, and the question shown after it.
/// docs/specs/core.md "Báo bản mới", ADR-0006.
/// </summary>
internal static partial class UpdateOffer
{
    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    public static UpdateChecker CreateChecker()
    {
        // FLEVERT_UPDATE_URL points at a fake GitHub for tests; only a loopback address is accepted.
        var endpoint = UpdateChecker.TryTestEndpoint(Environment.GetEnvironmentVariable("FLEVERT_UPDATE_URL"), out var test)
            ? test!
            : UpdateChecker.LatestReleaseApi;
        var handler = new SocketsHttpHandler { UseCookies = false, PooledConnectionLifetime = TimeSpan.FromSeconds(30) };
        return new UpdateChecker(handler, new UpdateStateStore(UpdateStateStore.DefaultPath), TimeProvider.System,
            ReleaseVersion.Current, endpoint);
    }

    /// <summary>Asks the user: Download opens the releases page, Skip this version stops offering this one, Later does nothing.</summary>
    public static void Ask(UpdateChecker checker, ReleaseVersion available)
    {
        var download = new TaskDialogButton("Download");
        var later = new TaskDialogButton("Later");
        var skip = new TaskDialogButton("Skip this version");
        var page = new TaskDialogPage
        {
            Caption = "Flevert",
            Heading = $"Flevert {available} is available",
            Text = $"You have {ReleaseVersion.Current}. Download the new setup from GitHub and run it; it installs over this version.",
            Icon = TaskDialogIcon.Information,
            Buttons = { download, later, skip },
            // Not Download: the question can come up while the user types in another window, and a stray Enter or
            // Space must not open a browser. Later is harmless; it asks again tomorrow.
            DefaultButton = later,
            AllowCancel = true,
        };

        // By the time the batch ends another window usually has the focus, and Windows opens a new window behind it. Made
        // top-most once created, the question stays in sight; it is asked at most once a day.
        page.Created += (_, _) =>
        {
            if (page.BoundDialog is { } dialog)
                SetWindowPos(dialog.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        };
        var answer = TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        if (answer == download)
            Process.Start(new ProcessStartInfo(UpdateChecker.DownloadPage.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        else if (answer == skip)
            checker.Skip(available);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}

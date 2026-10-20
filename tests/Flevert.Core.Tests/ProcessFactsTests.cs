using System.Diagnostics;

namespace Flevert.Core.Tests;

public sealed class ProcessFactsTests
{
    [Fact]
    public void This_process_was_created_when_the_system_says_it_started()
    {
        using var self = Process.GetCurrentProcess();

        var created = ProcessFacts.CreationTimeUtc((uint)Environment.ProcessId);

        Assert.NotNull(created);
        Assert.Equal(DateTimeKind.Utc, created.Value.Kind);
        Assert.InRange(created.Value - self.StartTime.ToUniversalTime(), TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Another_process_shows_its_command_line_and_creation_time_from_the_moment_it_exists()
    {
        // Explorer's processes must be visible to the batch leader before they have run any of their own code.
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -n 3 127.0.0.1 >nul & rem ảnh 1.txt");
        var before = DateTime.UtcNow;
        using var child = Process.Start(start)!;
        try
        {
            var arguments = ProcessFacts.Arguments((uint)child.Id);
            var created = ProcessFacts.CreationTimeUtc((uint)child.Id);

            Assert.NotNull(arguments);
            Assert.Equal(["/d", "/c", "ping -n 3 127.0.0.1 >nul & rem ảnh 1.txt"], arguments.Skip(1));
            Assert.Equal("cmd.exe", Path.GetFileName(arguments[0]), StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(created);
            Assert.InRange(created.Value, before - TimeSpan.FromSeconds(1), DateTime.UtcNow);
        }
        finally
        {
            child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task A_process_that_has_exited_has_no_facts()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit") { UseShellExecute = false, CreateNoWindow = true })!;
        await child.WaitForExitAsync();
        var pid = (uint)child.Id;
        var exited = child.ExitTime.ToUniversalTime();
        child.Dispose(); // our handle would keep the process object, and its facts, alive

        // Windows may already have given the id to a new process; that one was created after the child exited.
        var created = ProcessFacts.CreationTimeUtc(pid);
        Assert.True(created is null || created > exited, $"facts of an exited process: created {created:o}, exited {exited:o}");
    }
}

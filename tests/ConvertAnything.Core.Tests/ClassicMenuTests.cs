using ConvertAnything.Core.Menu;

namespace ConvertAnything.Core.Tests;

public sealed class ClassicMenuTests
{
    private const string Clsid = @"CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string Server = Clsid + @"\InprocServer32";

    private readonly FakeRegistry _registry = new();

    [Fact]
    public void On_creates_the_empty_server_key_and_marks_it_as_ours()
    {
        Assert.True(new ClassicMenu(_registry).On());

        Assert.Equal("", _registry.GetValue(Server, ""));
        Assert.NotNull(_registry.GetValue(Clsid, ClassicMenu.Marker));
    }

    [Fact]
    public void On_leaves_a_key_the_user_created_alone_and_unmarked()
    {
        _registry.SetValue(Server, "", "");

        Assert.True(new ClassicMenu(_registry).On());

        Assert.Null(_registry.GetValue(Clsid, ClassicMenu.Marker));
        Assert.Equal("", _registry.GetValue(Server, ""));
    }

    [Theory]
    [InlineData(null)] // InprocServer32 missing: only the CLSID key is there
    [InlineData(@"C:\tools\shell.dll")]
    public void On_reports_an_existing_key_that_does_not_switch_the_menu_and_leaves_it(string? server)
    {
        if (server is null)
            _registry.SetValue(Clsid, "Note", "someone else's");
        else
            _registry.SetValue(Server, "", server);
        var before = _registry.Snapshot();

        Assert.False(new ClassicMenu(_registry).On());

        Assert.Equal(before, _registry.Snapshot());
    }

    [Fact]
    public void On_repairs_its_own_key_left_without_the_server_key()
    {
        // An earlier On that stopped between its two writes.
        _registry.SetValue(Clsid, ClassicMenu.Marker, "1");

        Assert.True(new ClassicMenu(_registry).On());

        Assert.Equal("", _registry.GetValue(Server, ""));
    }

    [Fact]
    public void Off_removes_the_key_it_created()
    {
        var menu = new ClassicMenu(_registry);
        menu.On();

        menu.Off();

        Assert.Empty(_registry.SubKeyNames("CLSID"));
    }

    [Fact]
    public void Off_keeps_a_key_the_user_created()
    {
        _registry.SetValue(Server, "", "");
        var menu = new ClassicMenu(_registry);
        menu.On();

        menu.Off();

        Assert.Equal("", _registry.GetValue(Server, ""));
    }

    [Fact]
    public void Repeating_on_and_off_is_harmless()
    {
        var menu = new ClassicMenu(_registry);
        menu.Off();
        menu.On();
        menu.On();
        Assert.NotNull(_registry.GetValue(Clsid, ClassicMenu.Marker));

        menu.Off();
        menu.Off();
        Assert.Empty(_registry.SubKeyNames("CLSID"));
    }
}

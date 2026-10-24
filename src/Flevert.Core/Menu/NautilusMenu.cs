using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flevert.Core.Menu;

/// <summary>
/// menu.json, what the Nautilus extension (src/Flevert.Linux/nautilus/flevert_menu.py) shows: for each extension the tree
/// the Windows menu has for it, built by the same rules (<see cref="MenuInstaller.MenuNode"/>), so the entries come in
/// the same order. Nautilus has no limit of 16 entries and no Shift-only entry: a cascade of its own on Windows (the PDF
/// tools) is a submenu at the end of Flevert's. docs/specs/linux.md.
/// </summary>
public static class NautilusMenu
{
    public const int Version = 1;

    /// <param name="flevert">The absolute path of flevert, which the extension starts.</param>
    /// <param name="maxFiles">The most files one run takes.</param>
    /// <exception cref="ArgumentException">Two entries at one place, or a label that is both an entry and a submenu.</exception>
    public static string Build(IEnumerable<IMenuOperation> operations, string flevert, int maxFiles)
    {
        ArgumentNullException.ThrowIfNull(operations);
        // An operation without a menu path is run by its id only: nothing of it goes in the menu.
        var ops = operations.Where(op => op.MenuPath.Length > 0).ToList();
        var menus = new JsonObject();
        foreach (var extension in ops.SelectMany(op => op.Extensions).Select(e => e.ToLowerInvariant()).Distinct(StringComparer.Ordinal))
        {
            var entries = new JsonArray();
            foreach (var cascade in MenuCascade.All)
            {
                var nodes = MenuInstaller.MenuNode.Build(ops.Where(op =>
                    CascadeOf(op) == cascade && op.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)));
                if (nodes.Count == 0)
                    continue;
                if (cascade == MenuCascade.Main)
                {
                    foreach (var node in nodes)
                        entries.Add((JsonNode)ToJson(node));
                }
                else
                {
                    entries.Add((JsonNode)new JsonObject { ["label"] = SubmenuLabel(cascade), ["children"] = new JsonArray([.. nodes.Select(ToJson)]) });
                }
            }

            menus[extension] = entries;
        }

        var menu = new JsonObject
        {
            ["version"] = Version,
            ["flevert"] = flevert,
            ["maxFiles"] = maxFiles,
            ["operations"] = new JsonObject(ops.Select(op => KeyValuePair.Create<string, JsonNode?>(
                op.Id, new JsonArray([.. op.Extensions.Select(e => (JsonNode)e.ToLowerInvariant())])))),
            ["menus"] = menus,
        };
        return menu.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static MenuCascade CascadeOf(IMenuOperation op) => op is IMenuPlacement placed ? placed.Cascade : MenuCascade.Main;

    // "Flevert: more PDF tools" on Windows, a top-level entry; "More PDF tools" inside Flevert's menu here.
    private static string SubmenuLabel(MenuCascade cascade)
    {
        var title = cascade.Title.StartsWith(MenuInstaller.Title + ": ", StringComparison.Ordinal)
            ? cascade.Title[(MenuInstaller.Title.Length + 2)..]
            : cascade.Title;
        return title.Length == 0 ? title : char.ToUpperInvariant(title[0]) + title[1..];
    }

    private static JsonObject ToJson(MenuInstaller.MenuNode node) => node.Operation is { } op
        ? new JsonObject { ["label"] = node.Label, ["op"] = op.Id }
        : new JsonObject { ["label"] = node.Label, ["children"] = new JsonArray([.. node.Children.Select(ToJson)]) };
}

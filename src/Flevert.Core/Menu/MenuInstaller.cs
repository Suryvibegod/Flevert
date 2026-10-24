using System.Text.RegularExpressions;

namespace Flevert.Core.Menu;

/// <summary>
/// Writes the classic right-click menu with ExtendedSubCommandsKey cascades, which work from HKCU without admin rights.
/// See docs/adr/0001-registry-extendedsubcommandskey.md.
/// </summary>
/// <param name="iconExists">Whether an icon file is there; the real file system unless a test says otherwise. A missing
/// icon (a development build without the icons folder) leaves its entry without one.</param>
public sealed partial class MenuInstaller(IRegistry registry, string exePath, Func<string, bool>? iconExists = null)
{
    internal const string Title = "Flevert";
    internal const string MenuKeyPrefix = Title + ".Menu";
    internal const string Associations = "SystemFileAssociations";

    /// <summary>The folder beside flevert.exe that holds the menu icons, <c>&lt;name&gt;.ico</c> (the installer puts them there).</summary>
    public const string IconsFolder = "icons";

    // ECF_SEPARATORBEFORE. Measured with MenuProbe on Windows 11: separators do not count toward the 16 entries Explorer
    // shows of a cascade (16 commands and 8 separators all shown; a 17th command dropped). docs/specs/pdf.md, v0.2.2.
    private const int SeparatorBefore = 0x20;

    private readonly Func<string, bool> _iconExists = iconExists ?? File.Exists;

    /// <param name="looks">The icon and group of entries at the top of the menu, by label; an entry not listed has neither.
    /// A separator goes between two entries of different groups.</param>
    public void Install(IEnumerable<IMenuOperation> operations, IReadOnlyDictionary<string, MenuEntryLook>? looks = null)
    {
        // An operation without a menu path is run by its id only: nothing of it goes in the menu.
        var ops = operations.Where(op => op.MenuPath.Length > 0).ToList();
        looks ??= new Dictionary<string, MenuEntryLook>();
        foreach (var op in ops)
            Validate(op);
        foreach (var look in looks.Values)
        {
            // The name becomes part of a file path.
            if (!IconName().IsMatch(look.Icon))
                throw new ArgumentException($"Icon name '{look.Icon}' must match {IconName()}.", nameof(looks));
        }

        foreach (var op in ops)
        {
            if (!MenuCascade.All.Contains(CascadeOf(op)))
                throw new ArgumentException($"'{op.Id}' is in a menu Flevert does not know, so uninstall would not remove it.", nameof(operations));
        }

        foreach (var cascade in MenuCascade.All)
        {
            var inCascade = ops.Where(op => CascadeOf(op) == cascade).ToList();
            foreach (var ext in inCascade.SelectMany(op => op.Extensions).Distinct(StringComparer.OrdinalIgnoreCase))
                MenuNode.Build(inCascade.Where(op => op.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)));
        }

        Uninstall();

        foreach (var cascade in MenuCascade.All)
        {
            var inCascade = ops.Where(op => CascadeOf(op) == cascade).ToList();
            foreach (var ext in inCascade.SelectMany(op => op.Extensions).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var menuKey = MenuKeyPrefix + ext + cascade.KeySuffix;
                var entry = $@"{Associations}\{ext}\shell\{cascade.Verb}";
                registry.SetValue(entry, "MUIVerb", cascade.Title);
                registry.SetValue(entry, "Icon", cascade.Icon is { } name && IconPath(name) is { } icon ? $"\"{icon}\",0" : $"\"{exePath}\",0");
                registry.SetValue(entry, "ExtendedSubCommandsKey", menuKey);
                registry.SetValue(entry, "MultiSelectModel", "Player");
                if (cascade.ShiftOnly)
                    registry.SetValue(entry, "Extended", "");

                var nodes = MenuNode.Build(inCascade.Where(op => op.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)));
                WriteMenu(menuKey, nodes);
                WriteLooks(menuKey, nodes, looks);
            }
        }
    }

    private static MenuCascade CascadeOf(IMenuOperation op) => op is IMenuPlacement placed ? placed.Cascade : MenuCascade.Main;

    // The icon file beside flevert.exe, or null when it is missing (a development build without icons).
    private string? IconPath(string name)
    {
        var icon = Path.Combine(Path.GetDirectoryName(exePath) ?? "", IconsFolder, name + ".ico");
        return _iconExists(icon) ? icon : null;
    }

    // Only the top of the menu: a format in Convert to (JPG, PNG…) has no icon of its own, which would only clutter a long list.
    private void WriteLooks(string menuKey, IReadOnlyList<MenuNode> nodes, IReadOnlyDictionary<string, MenuEntryLook> looks)
    {
        int? previousGroup = null;
        for (var i = 0; i < nodes.Count; i++)
        {
            var verb = $@"{menuKey}\shell\{i:D3}";
            if (!looks.TryGetValue(nodes[i].Label, out var look))
            {
                previousGroup = null;
                continue;
            }

            if (IconPath(look.Icon) is { } icon)
                registry.SetValue(verb, "Icon", $"\"{icon}\",0");
            if (previousGroup is { } group && group != look.Group)
                registry.SetDword(verb, "CommandFlags", SeparatorBefore);
            previousGroup = look.Group;
        }
    }

    public void Uninstall()
    {
        foreach (var ext in registry.SubKeyNames(Associations))
        {
            foreach (var cascade in MenuCascade.All)
            {
                if (registry.SubKeyNames($@"{Associations}\{ext}\shell").Contains(cascade.Verb, StringComparer.OrdinalIgnoreCase))
                    registry.DeleteTree($@"{Associations}\{ext}\shell\{cascade.Verb}");
            }
        }

        foreach (var key in registry.SubKeyNames(""))
        {
            if (key.StartsWith(MenuKeyPrefix, StringComparison.OrdinalIgnoreCase))
                registry.DeleteTree(key);
        }
    }

    private void WriteMenu(string menuKey, IReadOnlyList<MenuNode> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            // Explorer lists verbs in key-name order; the index prefix preserves the declared order.
            var verb = $@"{menuKey}\shell\{i:D3}";
            registry.SetValue(verb, "MUIVerb", node.Label);
            registry.SetValue(verb, "MultiSelectModel", "Player");

            if (node.Operation is { } op)
            {
                registry.SetValue($@"{verb}\command", "", $"\"{exePath}\" run {op.Id} \"%1\"");
            }
            else
            {
                var submenu = $@"{menuKey}\{node.Label}";
                registry.SetValue(verb, "ExtendedSubCommandsKey", submenu);
                WriteMenu(submenu, node.Children);
            }
        }
    }

    private static void Validate(IMenuOperation op)
    {
        // The id is placed unquoted in the registry command line.
        if (!OperationId().IsMatch(op.Id))
            throw new ArgumentException($"Operation id '{op.Id}' must match {OperationId()}.", nameof(op));

        foreach (var ext in op.Extensions)
        {
            // Extensions become registry key names under SystemFileAssociations.
            if (!Extension().IsMatch(ext))
                throw new ArgumentException($"Extension '{ext}' of '{op.Id}' must match {Extension()}.", nameof(op));
        }

        foreach (var label in op.MenuPath.Split('/'))
        {
            if (label.Length == 0 || label.Contains('\\', StringComparison.Ordinal) || label.Equals("shell", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Menu path '{op.MenuPath}' of '{op.Id}' has an invalid label '{label}'.", nameof(op));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$")]
    private static partial Regex OperationId();

    [GeneratedRegex(@"^\.[a-z0-9]+$")]
    private static partial Regex Extension();

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex IconName();

    internal sealed class MenuNode(string label)
    {
        private readonly List<MenuNode> _children = [];

        public string Label { get; } = label;

        public IMenuOperation? Operation { get; private set; }

        public IReadOnlyList<MenuNode> Children => _children;

        public static IReadOnlyList<MenuNode> Build(IEnumerable<IMenuOperation> operations)
        {
            var root = new MenuNode("");
            foreach (var op in operations)
            {
                var node = root;
                foreach (var segment in op.MenuPath.Split('/'))
                {
                    if (node.Operation is not null)
                        throw new ArgumentException($"'{node.Label}' is both a command and a submenu.", nameof(operations));

                    // Labels become registry key names, which are case-insensitive.
                    var child = node._children.FirstOrDefault(c => c.Label.Equals(segment, StringComparison.OrdinalIgnoreCase));
                    if (child is not null && child.Label != segment)
                        throw new ArgumentException($"Menu labels '{child.Label}' and '{segment}' differ only in case.", nameof(operations));
                    if (child is null)
                        node._children.Add(child = new MenuNode(segment));
                    node = child;
                }

                if (node.Operation is not null || node._children.Count > 0)
                    throw new ArgumentException($"'{op.MenuPath}' is used by more than one entry.", nameof(operations));
                node.Operation = op;
            }

            return root.Children;
        }
    }
}

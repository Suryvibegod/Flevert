using System.Text.RegularExpressions;

namespace ConvertAnything.Core.Menu;

/// <summary>
/// Writes the classic right-click menu with ExtendedSubCommandsKey cascades, which work from HKCU without admin rights.
/// See docs/adr/0001-registry-extendedsubcommandskey.md.
/// </summary>
public sealed partial class MenuInstaller(IRegistry registry, string exePath)
{
    internal const string Title = "ConvertAnything";
    internal const string MenuKeyPrefix = Title + ".Menu";
    internal const string Associations = "SystemFileAssociations";

    public void Install(IEnumerable<IOperation> operations)
    {
        var ops = operations.ToList();
        foreach (var op in ops)
            Validate(op);
        foreach (var ext in ops.SelectMany(op => op.Extensions).Distinct(StringComparer.OrdinalIgnoreCase))
            MenuNode.Build(ops.Where(op => op.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)));

        Uninstall();

        foreach (var ext in ops.SelectMany(op => op.Extensions).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var menuKey = MenuKeyPrefix + ext;
            var entry = $@"{Associations}\{ext}\shell\{Title}";
            registry.SetValue(entry, "MUIVerb", Title);
            registry.SetValue(entry, "Icon", $"\"{exePath}\",0");
            registry.SetValue(entry, "ExtendedSubCommandsKey", menuKey);
            registry.SetValue(entry, "MultiSelectModel", "Player");

            WriteMenu(menuKey, MenuNode.Build(ops.Where(op => op.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))));
        }
    }

    public void Uninstall()
    {
        foreach (var ext in registry.SubKeyNames(Associations))
        {
            if (registry.SubKeyNames($@"{Associations}\{ext}\shell").Contains(Title, StringComparer.OrdinalIgnoreCase))
                registry.DeleteTree($@"{Associations}\{ext}\shell\{Title}");
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

    private static void Validate(IOperation op)
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

    private sealed class MenuNode(string label)
    {
        private readonly List<MenuNode> _children = [];

        public string Label { get; } = label;

        public IOperation? Operation { get; private set; }

        public IReadOnlyList<MenuNode> Children => _children;

        public static IReadOnlyList<MenuNode> Build(IEnumerable<IOperation> operations)
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

namespace ConvertAnything.Core.Menu;

/// <summary>
/// Writes the classic right-click menu with ExtendedSubCommandsKey cascades, which work from HKCU without admin rights.
/// See docs/adr/0001-registry-extendedsubcommandskey.md.
/// </summary>
public sealed class MenuInstaller(IRegistry registry, string exePath)
{
    private const string Title = "ConvertAnything";
    private const string MenuKeyPrefix = "ConvertAnything.Menu";
    private const string Associations = "SystemFileAssociations";

    public void Install(IEnumerable<IOperation> operations)
    {
        Uninstall();

        var ops = operations.ToList();
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
            var verb = $@"{menuKey}\shell\{i:D2}";
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
                    var child = node._children.FirstOrDefault(c => c.Label == segment);
                    if (child is null)
                        node._children.Add(child = new MenuNode(segment));
                    node = child;
                }

                node.Operation = op;
            }

            return root.Children;
        }
    }
}

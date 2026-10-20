using System.Runtime.InteropServices;

// Usage:
//   MenuProbe list <file>...                   prints the context menu tree Explorer builds for the selection
//   MenuProbe list --shift <file>...           the same with Shift held (CMF_EXTENDEDVERBS): verbs marked Extended too
//   MenuProbe invoke "A/B/C" <file>...         clicks the item at that label path, as a user would
// Exit codes: 0 ok, 3 item not found, 2 bad usage.

if (args.Length < 2 || args[0] is not ("list" or "invoke") || (args[0] == "invoke" && args.Length < 3))
{
    Console.Error.WriteLine("usage: MenuProbe list <file>... | MenuProbe invoke \"A/B/C\" <file>...");
    return 2;
}

int result = 0;
var shift = args[0] == "list" && args[1] == "--shift";
if (shift)
    Probe.Flags = 0x100; // CMF_EXTENDEDVERBS, as when Shift is held
var thread = new Thread(() => result = args[0] == "list" ? Probe.List(args[(shift ? 2 : 1)..]) : Probe.Invoke(args[1], args[2..]));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return result;

static class Probe
{
    public static uint Flags;

    const uint FirstId = 1, LastId = 0x7FFF;

    public static int List(string[] files)
    {
        using var menu = new ShellMenu(files);
        menu.Walk((depth, label, _) => Console.WriteLine(new string(' ', depth * 2) + label));
        return 0;
    }

    public static int Invoke(string path, string[] files)
    {
        using var menu = new ShellMenu(files);
        var wanted = path.Split('/');
        uint? id = null;
        var trail = new List<string>();
        menu.Walk((depth, label, cmdId) =>
        {
            if (trail.Count > depth) trail.RemoveRange(depth, trail.Count - depth);
            trail.Add(label);
            if (cmdId is { } c && trail.SequenceEqual(wanted)) id = c;
        });
        if (id is null)
        {
            Console.Error.WriteLine($"not found: {path}");
            return 3;
        }
        menu.InvokeCommand(id.Value);
        Console.WriteLine($"invoked: {path}");
        return 0;
    }

    sealed class ShellMenu : IDisposable
    {
        readonly IContextMenu _menu;
        readonly IContextMenu3? _menu3;
        readonly nint _hmenu;

        public ShellMenu(string[] files)
        {
            var pidls = files.Select(f =>
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetFullPath(f), 0, out var pidl, 0, out _));
                return pidl;
            }).ToArray();
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var items));
            var bhid = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5"); // BHID_SFUIObject
            var iid = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(items.BindToHandler(0, ref bhid, ref iid, out var ppv));
            _menu = (IContextMenu)Marshal.GetObjectForIUnknown(ppv);
            Marshal.Release(ppv);
            _menu3 = _menu as IContextMenu3;
            _hmenu = CreatePopupMenu();
            Marshal.ThrowExceptionForHR(_menu.QueryContextMenu(_hmenu, 0, FirstId, LastId, Probe.Flags /* CMF_NORMAL, or CMF_EXTENDEDVERBS */));
        }

        public void Walk(Action<int, string, uint?> visit) => Walk(_hmenu, 0, visit);

        void Walk(nint hmenu, int depth, Action<int, string, uint?> visit)
        {
            var count = GetMenuItemCount(hmenu);
            for (var i = 0; i < count; i++)
            {
                const int chars = 512;
                var buffer = Marshal.AllocHGlobal(chars * 2);
                string label;
                var info = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = 0x1 | 0x2 | 0x4 | 0x40 | 0x100, dwTypeData = buffer, cch = chars };
                try
                {
                    if (!GetMenuItemInfo(hmenu, (uint)i, true, ref info)) continue;
                    if ((info.fType & 0x800) != 0)
                    {
                        // A separator (CommandFlags ECF_SEPARATORBEFORE/AFTER): shown, never invoked.
                        visit(depth, "----", null);
                        continue;
                    }
                    label = (Marshal.PtrToStringUni(buffer) ?? "").Replace("&", "");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                if (info.hSubMenu != 0)
                {
                    visit(depth, label, null);
                    // Static cascades are filled lazily, like Explorer does when a submenu opens.
                    _menu3?.HandleMenuMsg2(0x0117 /* WM_INITMENUPOPUP */, info.hSubMenu, i, out _);
                    Walk(info.hSubMenu, depth + 1, visit);
                }
                else
                {
                    visit(depth, label, info.wID);
                }
            }
        }

        public void InvokeCommand(uint id)
        {
            var ci = new CMINVOKECOMMANDINFO
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                lpVerb = (nint)(id - FirstId),
                nShow = 1,
            };
            Marshal.ThrowExceptionForHR(_menu.InvokeCommand(ref ci));
        }

        public void Dispose() => DestroyMenu(_hmenu);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, nint bindingContext, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    static extern int SHCreateShellItemArrayFromIDLists(uint count, nint[] pidls, out IShellItemArray items);

    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(nint hmenu);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(nint hmenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMenuItemInfo(nint hmenu, uint item, bool byPosition, ref MENUITEMINFO info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public nint hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
        public nint dwTypeData;
        public uint cch;
        public nint hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CMINVOKECOMMANDINFO
    {
        public int cbSize, fMask;
        public nint hwnd, lpVerb, lpParameters, lpDirectory;
        public int nShow, dwHotKey;
        public nint hIcon;
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO info);
        [PreserveSig] int GetCommandString(nuint idCmd, uint type, nint reserved, nint name, uint cchMax);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO info);
        [PreserveSig] int GetCommandString(nuint idCmd, uint type, nint reserved, nint name, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, nint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, nint wParam, nint lParam, out nint result);
    }
}

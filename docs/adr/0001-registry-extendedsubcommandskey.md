# ADR-0001: Register the right-click menu with ExtendedSubCommandsKey under HKCU

## Status

Accepted

## Date

2026-10-19

## Context

ConvertAnything adds a cascading `ConvertAnything >` entry to the Explorer right-click menu, showing only the operations that apply to the clicked file type. Requirements:

- Installable per user, without administrator rights.
- Nested submenus (e.g. `Convert > PNG`).
- Works when the user selects many files at once.
- Uninstall must remove everything it added.
- Windows 11's new context menu requires a signed MSIX package (`IExplorerCommand`), which is out of scope; the classic menu ("Show more options") is the target.

## Decision

Write static verbs under `HKCU\Software\Classes`:

- `SystemFileAssociations\<.ext>\shell\ConvertAnything` with `MUIVerb`, `Icon` and `ExtendedSubCommandsKey=ConvertAnything.Menu<.ext>`.
- Each menu level lives at `ConvertAnything.Menu<.ext>[\<submenu>]\shell\NNN`, where `NNN` is a three-digit index that preserves declared order (Explorer sorts verbs by key name).
- Leaf verbs run `"<exe>" run <op-id> "%1"`.
- Every verb sets `MultiSelectModel=Player`.

Uninstall deletes every `shell\ConvertAnything` key under `SystemFileAssociations` and every top-level key starting with `ConvertAnything.Menu`, then prunes parents left empty (never `SystemFileAssociations` itself).

## Alternatives Considered

### SubCommands

- Pros: simplest syntax for a cascade.
- Cons: custom verbs listed in `SubCommands` must be registered in the `CommandStore` under HKLM.
- Rejected: needs administrator rights.

### Shell extension DLL (IContextMenu / IExplorerCommand)

- Pros: dynamic menus, no 100-item limit, receives all selected items in one call.
- Cons: in-process COM DLL loaded into Explorer (crashes take Explorer down), registration and signing burden; `IExplorerCommand` for the Windows 11 menu needs MSIX.
- Rejected for v1: too much risk and packaging cost for a free tool. Revisit if the 100-file limit becomes a real complaint.

## Consequences

- No admin rights; install and uninstall are plain registry writes.
- Verified with `tools/MenuProbe` against the real Explorer menu: the cascade appears, `Dev > Copy` runs `ca.exe`, the entry is shown for 100 selected files and hidden for 101. This matches Microsoft's documented limits for command-line verbs (Document: 15 items, Player: 100 items).
- Explorer starts one process per selected file, so batching has to happen in our process (see ADR-0002).
- For paths longer than MAX_PATH Explorer passes 8.3 short names in `%1`. `Path.GetFullPath` expands them (through `GetLongPathNameW`, which touches the path before the runner's checks; accepted, since no route to a pipe was found), and outputs are named from the pinned file's real path, so a name that stays short (a `\\?\` argument) still works; its file type is judged after pinning.

Source: [Creating Shortcut Menu Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/context-menu-handlers), Microsoft Learn.

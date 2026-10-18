# ADR-0003: Ship self-contained ReadyToRun with WinForms instead of NativeAOT

## Status

Accepted

## Date

2026-10-19

## Context

Every click in the right-click menu starts `ca.exe` once per selected file, so start-up time is paid on every click. Users should not have to install a .NET runtime. The tool needs a small amount of UI: a progress window with Cancel for long batches and a notification when a batch ends.

The original spec targeted NativeAOT for start-up speed and a P/Invoke `Shell_NotifyIcon` balloon for UI.

## Decision

Publish `ca.exe` self-contained for `win-x64` with ReadyToRun, and build the UI with WinForms (`NotifyIcon` balloon, a small `Form`). Only the batch leader touches WinForms; follower processes exit before any WinForms type is loaded.

## Alternatives Considered

### NativeAOT

- Pros: fastest start-up, smaller output.
- Cons: needs the Visual Studio C++ build tools (several GB) on every build machine; WinForms does not support NativeAOT, so all UI would be hand-written Win32.
- Rejected: build-machine cost and UI cost are both high, and the measured start-up without it is already well under target.

### Framework-dependent build

- Pros: much smaller download.
- Cons: users must install the .NET Desktop Runtime first.
- Rejected: an extra prerequisite is a real barrier for non-technical users.

## Consequences

- Measured with `scripts/measure-startup.ps1`: about 70 ms process start-to-exit (median of 10) on the published build, against a 300 ms target.
- The published folder is about 110 MB before compression; the installer compresses it.
- `ConvertAnything.Core` stays free of WinForms so it remains testable and AOT-compatible should NativeAOT be revisited.

# ConvertAnything: instructions for Claude

Windows right-click converter for images, video/audio and PDF (C#/.NET 10). Read [tasks/HANDOFF.md](tasks/HANDOFF.md) at the start of every session: it holds the goal, the current state, the decisions and the next task.

## When the user says "làm tiếp", "làm tiếp đi", "tiếp tục", "continue" (or similar)

Continue the project without asking questions:

1. Run `powershell -ExecutionPolicy Bypass -File scripts/setup-dev.ps1` once per machine. It installs what is missing (.NET 10 SDK, GitHub CLI, the agent-skills plugin), sets the commit identity, builds and runs the tests. If a step needs the user (for example `gh auth login`), tell them the exact command and continue with everything else.
2. Read `tasks/HANDOFF.md` and do the next unfinished item listed there.
3. Keep `tasks/HANDOFF.md` up to date as you go: state, next task, anything the next session must know. Commit and push after every finished task so another machine can pick up.

## Mandatory process: Addy Osmani's agent-skills

The user requires this process for every change, with no exceptions. The skills come from the plugin `agent-skills@addy-agent-skills`. If they are not loaded in this session (a plugin installed during a session only loads after a restart or `/reload-plugins`), read the skill files directly from the plugin cache, e.g. `~/.claude/plugins/cache/addy-agent-skills/agent-skills/<version>/skills/<skill>/SKILL.md`, and follow them.

Per module and per task: spec → plan/tasks → test first (see it fail, then pass) → thin vertical slices → verify at runtime → three fresh-context, read-only reviews (code-reviewer, security-auditor, doubt-driven) → reconcile every finding → re-review until no Critical/High/Required remain → docs/ADRs → ship. Details and the Definition of Done are in `tasks/HANDOFF.md`.

## Conventions

- UI, README, commit messages: English. Specs and plans: Vietnamese.
- One logical change per commit; the message explains why. Commit identity is set by the setup script.
- Check `dotnet format ConvertAnything.sln --verify-no-changes` by its exit code before committing (never through a pipe that hides it).
- Never commit binaries (ffmpeg, qpdf, pdfium); they are fetched by scripts into `deps/`.
- After trying the context menu on a real machine, run `ca.exe uninstall-menu`.

## Commands

```
dotnet build ConvertAnything.sln
dotnet test ConvertAnything.sln
dotnet format ConvertAnything.sln --verify-no-changes
dotnet publish src/ConvertAnything.Cli -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o artifacts/publish
powershell -File scripts/measure-startup.ps1
dotnet run --project tools/MenuProbe -- list "C:\path\file.txt"
```

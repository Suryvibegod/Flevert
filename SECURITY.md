# Security policy

ConvertAnything opens files people download from anywhere, and its right-click menu runs with the rights of whoever clicks it. Security reports are welcome and taken seriously.

## Reporting a vulnerability

Please report privately through [GitHub's private vulnerability reporting](https://github.com/Suryvibegod/ConvertAnything/security/advisories/new), not in a public issue. Include the ConvertAnything version, your Windows version, and the steps or a sample file that shows the problem.

You can expect a first reply within a week. Once a fix is released, the advisory is published with credit to you unless you prefer otherwise.

## Supported versions

Only the latest release receives fixes.

## Scope

In scope, for example:

- A crafted image (or, later, video, audio or PDF) that makes ConvertAnything run code, read or write files other than its input and output, or exhaust the machine.
- Ways for another local account or a sandboxed process to make the right-click menu run code of its choice.
- The installer or uninstaller modifying anything outside what it installed.

Out of scope: Windows SmartScreen or Smart App Control warnings about the unsigned installer (see the README), and issues in ImageMagick or .NET that have a fix already shipped upstream but not yet picked up (please still tell us).

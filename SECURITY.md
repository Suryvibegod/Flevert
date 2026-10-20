# Security policy

Flevert opens files that come from anywhere, and its right-click menu runs with the rights of whoever clicks it. If you find a way to abuse either, please tell us privately.

## Reporting a vulnerability

Please report privately through [GitHub's private vulnerability reporting](https://github.com/Suryvibegod/Flevert/security/advisories/new), not in a public issue. Include the Flevert version, your Windows version, and the steps or a sample file that shows the problem.

You can expect a first reply within a week. Once a fix is released, the advisory is published with credit to you unless you prefer otherwise.

## Supported versions

Only the latest release receives fixes.

## Scope

In scope, for example:

- A crafted image or PDF (or, later, video or audio file) that makes Flevert run code, read or write files other than its input and output, or exhaust the machine.
- A PDF password that ends up on disk, in a log or on a command line.
- Ways for another local account or a sandboxed process to make the right-click menu run code of its choice.
- The installer or uninstaller modifying anything outside what it installed.

Out of scope: Windows SmartScreen or Smart App Control warnings about the unsigned installer (see the README), and issues in ImageMagick, pdfium, qpdf or .NET that are already fixed upstream but not yet picked up by Flevert (please still tell us).

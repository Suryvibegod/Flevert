# Installing and removing Flevert

The short version is in the [README](../README.md#install). This page has the details: how to check a download, what to do when the menu is missing, why setup can refuse its folder, and how to remove Flevert by hand.

## Check your download

Flevert is not code-signed yet: a signing certificate costs money and this project is free (free signing for open-source projects is planned). Until then Windows shows **Windows protected your PC** for a new download, with **More info** and then **Run anyway**.

**Before** you click **Run anyway**, make sure the file came from this repository's [Releases page](https://github.com/Suryvibegod/Flevert/releases) and is intact. The notes of each release list the exact commands for that version; they look like this for 0.2.0:

1. In PowerShell, in your Downloads folder: `Get-FileHash .\Flevert-Setup-0.2.0.exe`. The hash must match the line in `SHA256SUMS.txt` on the release page (upper or lower case does not matter). This shows the download is complete and unchanged.
2. To check that GitHub built it from this repository's source code, verify its build provenance with the [GitHub CLI](https://cli.github.com/) (it needs a free GitHub account: run `gh auth login` once first). The command in the release notes also pins the exact commit.

   ```
   gh attestation verify Flevert-Setup-0.2.0.exe --repo Suryvibegod/Flevert --signer-workflow Suryvibegod/Flevert/.github/workflows/release.yml --source-ref refs/tags/v0.2.0 --deny-self-hosted-runners
   ```

A matching hash alone does not prove where the file came from: anyone who replaced the file could replace the hash beside it. The provenance check shows that this repository's release workflow built the file from the version's tag on GitHub's own machines.

## If the menu is missing

- On Windows 11 the entry is under **Show more options** (or Shift+F10), unless you picked the classic menu in setup.
- Explorer shows the entry for up to **100** selected files; select fewer.
- If setup reported a problem with the menu, run it again. If the same problem comes back, [open an issue](https://github.com/Suryvibegod/Flevert/issues/new/choose) with its message.
- If setup said the classic-menu key already exists in another form, another program changed that setting; Flevert leaves it alone and stays under **Show more options**.
- Smart App Control can block setup or parts of Flevert, during setup or later; a work or school PC's policy against programs in the temporary folder can stop setup's check of the install folder. When this happens during setup, its message links to the [Smart App Control](../README.md#smart-app-control) section. Keep Smart App Control on; on a managed PC, ask your IT department.
- Logs are in `%LOCALAPPDATA%\Flevert\logs` (kept 7 days). They contain file names and your Windows user name: replace anything private before posting them.

## Setup refused the install folder

Flevert always installs into `%LOCALAPPDATA%\Programs\Flevert`, and only if no other Windows account can change that folder, any folder above it, or the files in it, and none of them is a junction or symbolic link to somewhere else. Otherwise another account could replace the program, and the next time you used the menu, their code would run as you. Setup checks this before it copies anything, so a refused setup leaves nothing to uninstall. If setup instead said Flevert was installed but the menu failed with this message, the files are there: see below. Running setup again does not change the result.

It usually happens when:

- your `AppData` folder was moved to another drive (for example with a junction), or lives on a drive whose permissions let every user change files, as many second drives do;
- someone changed the permissions of `%LOCALAPPDATA%\Programs` or of the folder itself;
- `%LOCALAPPDATA%\Programs\Flevert` already holds files from something else.

Put the folder back where Windows keeps it with its default permissions (or delete `%LOCALAPPDATA%\Programs\Flevert` if it holds leftover files), then run setup again. If Flevert is already installed in a folder that setup now refuses (for example when updating), do not use its uninstaller and do not just repair the permissions: another account may have changed the files there. [Remove it by hand](#remove-flevert-by-hand), then run setup again. If you are not sure what changed, [open an issue](https://github.com/Suryvibegod/Flevert/issues/new/choose) with the message setup showed.

## Remove Flevert by hand

The uninstaller in **Settings > Apps > Installed apps** removes the program, its menu, logs and settings, and the classic menu if setup turned it on; your converted files stay. Use the steps below only if Windows blocks the uninstaller (Smart App Control can) or setup told you to. These PowerShell commands run nothing of Flevert's; they only delete what it created:

```
# The right-click menu
Get-ChildItem HKCU:\Software\Classes\SystemFileAssociations -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath "$($_.PSPath)\shell\Flevert" -Recurse -ErrorAction SilentlyContinue }
Get-ChildItem HKCU:\Software\Classes | Where-Object PSChildName -like 'Flevert.Menu*' | Remove-Item -Recurse
# The classic menu, only if setup turned it on (it marks the key; a key you created yourself is left alone)
$k = 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}'
if (Get-ItemProperty -LiteralPath $k -Name Flevert -ErrorAction SilentlyContinue) { Remove-Item -LiteralPath $k -Recurse }
# The entry in Installed apps
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B7A3F52-2C64-4F0B-9E3D-5A1C7E2B9D40}_is1' -ErrorAction SilentlyContinue
# The program, its logs and settings (rmdir removes a junction itself, never what it points to)
cmd /d /c 'rmdir /s /q "%LOCALAPPDATA%\Programs\Flevert" 2>nul'
cmd /d /c 'for %d in (logs pipes magick update) do @rmdir /s /q "%LOCALAPPDATA%\Flevert\%d" 2>nul'
cmd /d /c 'rmdir "%LOCALAPPDATA%\Flevert" 2>nul'
if (Test-Path -LiteralPath "$env:LOCALAPPDATA\Programs\Flevert") { 'Not fully removed: wait for any conversion to finish, then run these lines again.' }
```

Then sign out and back in.

# Todo: installer

Plan: [plan.md](plan.md).

## Task 1: Version + ClassicMenu
- [x] `<Version>0.1.0</Version>` trong `Directory.Build.props`
- [x] `IRegistry.GetValue`; `ClassicMenuTests` (đỏ → xanh, đối chứng âm: bỏ kiểm đánh dấu thì test giữ khóa người dùng đỏ)
- [x] `ClassicMenu`; `CommandLine` nhận `classic-menu on|off`; nối vào `Program`; chạy thật on/off trên registry

## Task 2: Installer tối thiểu
- [x] `scripts/test-installer.ps1` (đỏ: chưa có installer)
- [x] `scripts/build-installer.ps1`, `installer/ConvertAnything.iss`
- [x] Kịch bản 1–3 xanh; đối chứng âm: bỏ `SweepRegistry` thì kịch bản 2 đỏ

## Task 3: Notices + icon
- [x] `THIRD-PARTY-NOTICES.txt` gom từ `ca.deps.json`; LICENSE trong bộ cài (test đỏ → xanh)
- [x] Icon app (`assets/make_icon.py`), `ApplicationIcon`, `SetupIconFile`
- [x] Sửa checkbox bị cắt ở 200% DPI (người dùng báo): trang riêng + `TNewCheckBox`, `/CLASSICMENU`

## Review vòng 1 (code-reviewer, security-auditor, doubt-driven): reconcile

Ký hiệu: CR = code review, SA = security audit, DD = doubt-driven.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| CR-M1, DD-2: menu lỗi mà setup im lặng vẫn thoát 0, trang cuối báo thành công, lý do bị mất | Required | Hợp lệ | `ExecAndCaptureOutput` lấy stderr vào hộp thoại; mã 3 riêng cho thư mục bị từ chối; setup thoát 10; trang cuối nói rõ; kịch bản 4 |
| CR-M2, DD-1, SA-1: link `#install` không tồn tại, README chưa có hướng dẫn, ADR-0005 thiếu | Required | Hợp lệ | README viết lại (Install, SmartScreen, kiểm SHA256/provenance, Smart App Control: đừng tắt, If the menu is missing, Uninstall); link trỏ `#smart-app-control`, `#if-the-menu-is-missing`; ADR-0005 |
| SA-2: pipeline release chưa đáng tin cho bản chưa ký | Required | Hợp lệ | Job build quyền đọc + `persist-credentials: false`, job release riêng chỉ đính kèm vào bản nháp; action pin SHA; attestation provenance; chặn gói có lỗ hổng; Inno cài từ release chính thức có kiểm SHA256; `global.json` |
| DD-4: menu cổ điển chưa kiểm ở runtime | Required (kiểm tay) | Hợp lệ | Nhờ người dùng kiểm trên máy thật (ghi trong spec Open Questions); đổi nhãn checkbox nói rõ "for all apps"; CAPABILITY-MAP sửa "toàn máy" |
| DD-3, CR-L1: Smart App Control chặn DLL thì thông báo chung chung | High/Low | Hợp lệ một phần | Mã ngoài 1–3 cũng kèm link Smart App Control; README nói thẳng v0.1 có thể không chạy khi SAC bật. Không bắt `FileLoadException` trong `ca.exe`: không tái hiện/kiểm được, link chung đã đủ chỉ đường |
| DD-5, CR-L9: gỡ khi `ca.exe` đang chạy để lại file | Medium | **Đã tái hiện** (29 file sót, mục gỡ đã mất) | Mutex `ConvertAnything.App` + `AppMutex`; test tích hợp mutex; kịch bản 6 |
| SA-3: `/DIR`, `UsePreviousAppDir` vượt được thư mục cố định | Low | Hợp lệ | `UsePreviousAppDir=no`, `PrepareToInstall` từ chối; kịch bản 5 (thoát 7) |
| SA-4: xóa đệ quy `%LOCALAPPDATA%\ConvertAnything` | Low | Hợp lệ | Xóa từng thư mục con + `dirifempty`; test khớp tên thư mục |
| SA-5: phiên bản Inno/SDK không pin | Low | Hợp lệ | `#if VER < 6.7.3` → lỗi; `global.json` |
| SA-11: test xóa log/menu dev có sẵn | Info | Hợp lệ | Test từ chối chạy khi có app data, menu, `ca.exe` đang chạy, hoặc quyền admin |
| CR-L2: không phân biệt "không chạy được" và mã thoát | Low | Hợp lệ | Log `started=… code=…` |
| CR-L3: Explorer không được báo khi chỉ lưới an toàn dọn | Low | Hợp lệ | `ChangesAssociations=yes` |
| CR-L4: khóa menu cổ điển dạng khác bị coi là "đã bật" | Low | Hợp lệ | `On()` trả false, `ca` thoát 1 kèm lý do; unit test 2 trường hợp |
| CR-L5: nhãn checkbox giấu ảnh hưởng toàn hệ thống | Low | Hợp lệ | "Use the classic (Windows 10 style) right-click menu for all apps" |
| CR-L6, DD-9: test có thể qua mà không kiểm xóa app data, JPEG, chờ uninstaller | Low | Hợp lệ | Assert log được ghi; JPEG 320×240; đợi tiến trình `_unins.tmp`; kiểm không-admin qua log; kịch bản 2 dùng `ca.exe` rác thay vì xóa |
| CR-L7: không kiểm khóa cha rỗng | Low | Hợp lệ | So tập `SystemFileAssociations\*` trước/sau |
| CR-L8: hằng số Pascal/C# có thể lệch | Low | Hợp lệ | `InstallerScriptTests` |
| CR-L10: lấy thư mục NuGet dễ vỡ | Low | Hợp lệ | `Select-String` lấy đúng dòng |
| CR-N1..N6 | Nit | Hợp lệ | `Off` bỏ `Exists()`; bỏ guard thừa; ghi chú 64-bit; `#error` khi thiếu `ca.exe`; câu hướng dẫn đặt trước "Click Finish"; spec/todo cập nhật |
| CR-N7: vòng lặp gradient chậm | Nit | Trade-off | Giữ: script chạy một lần |
| DD-6: chưa kiểm nâng cấp từ bản cũ, file cũ sót lại | Medium | Trade-off | v0.1 chưa có bản cũ; từ v0.2 giữ setup 0.1.0 làm fixture (spec tiêu chí 6, ADR-0005) |
| DD-7: kế hoạch ký mã chưa đủ | Medium | Hợp lệ | ADR-0005: SignPath có thể từ chối, DLL bên thứ ba chưa ký, uy tín SmartScreen, quét VirusTotal trước khi phát hành |
| DD-8: lý do bỏ style `windows11` dựa trên 2 mẫu | Low | Hợp lệ, **kết luận cũ sai** | Thử lại 9 bản: đều chạy; sửa spec/ghi chú (chặn ngẫu nhiên theo hash) |
| DD-10: "không còn gì" quá rộng; log setup trong `%TEMP%` | Low | Hợp lệ | Thu hẹp câu chữ trong spec; bỏ `SetupLogging` |
| DD-11: PDB, admin khác, ARM64, "restart Explorer", hậu tố prerelease, bằng sáng chế HEVC | Low | Hợp lệ | Bỏ `*.pdb`; README dặn không "Run as administrator"; ARM64 ghi "chưa kiểm"; câu chữ "sign out / restart your PC"; prerelease: chưa dùng, bỏ qua; HEVC: ADR-0004 |
| SA-6..10, SA-12 | Info | Không cần làm | Ghi nhận |

## Review vòng 2: đã sửa (2026-10-19)

Danh sách gốc ở mục 1 "Việc dở dang" của HANDOFF. Phần mutex/registry/email sửa trên máy trước; phần còn lại ở `e6d915a`..`e490910`: `.iss` (mã 3 riêng + mục README, câu dự phòng, dòng stderr đầu, bỏ 4551, `try/except` quanh `ca.exe`, không bật menu cổ điển khi menu lỗi, `[Messages]`, `PrepareToInstall` từ chối link và thư mục có file lạ), mutex static, hằng số tên thư mục, test junction `finally`, `test-installer.ps1` (≤ 60 MB, mã 7, kịch bản 6 chỉ nhận nhánh hủy, `finally`, mọi khóa `ConvertAnything*`, kịch bản 7–8), workflow (job attest riêng, tag trên `main`, `ISCC` theo đường dẫn, `10.0.x`, dependabot), `setup-dev.ps1` (chữ ký Authenticode), README/CONTRIBUTING/mẫu bug, spec/ADR-0005.

## Review vòng 3 (code-reviewer, security-auditor, doubt-driven): reconcile

Ký hiệu: CR, SA, DD (F = finding của doubt-driven).

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| SA H-1: setup chạy `ca.exe` từ `{app}` rồi `ca.exe` mới kiểm quyền; DLL người khác đặt vào đã chạy rồi; README bảo chạy uninstaller trong thư mục bị từ chối | High, Required | Hợp lệ | Lệnh mới `ca check-location <dir>` (cùng `InstallLocation`); `PrepareToInstall` tạo `{app}` nếu chưa có, `ExtractTemporaryFiles('{app}\*')` (Inno bung vào thư mục tên nguyên văn `{tmp}\{app}`, ~1 s), chạy kiểm từ đó, từ chối (mã 7) trước khi chép; không chạy được thì cũng từ chối. Kịch bản 4 đổi thành "từ chối, không chép gì" (đỏ với `.iss` cũ: mã 10). README: từ chối thì không có gì để gỡ; nếu đã cài mà menu bị từ chối thì gỡ tay, không chạy uninstaller. Trade-off: `{tmp}` nằm trong `%TEMP%`, nơi Inno vốn đã chạy `setup.tmp`; quyền bị nới **sau** khi cài cần chính người dùng hoặc admin, uninstall không kiểm lại |
| DD F1, CR-1, SA-L: `LinkOnPath` kiểm `C:` (thư mục hiện tại) thay vì `C:\` | Medium, Required (DD) | Hợp lệ | `RemoveBackslashUnlessRoot`; kịch bản 9 chạy setup từ junction. Không tái hiện được lỗi qua `Start-Process -WorkingDirectory` (setup không giữ thư mục làm việc của người gọi), nhưng gốc ổ trước đó không bao giờ được kiểm |
| CR-2, DD F9: test registry-error chạy `uninstall-menu` thật, xóa menu đã cài | Medium | Hợp lệ | Skip khi có khóa menu `ConvertAnything*` |
| CR-3: `setup-dev.ps1` chết khi `gh` ghi stderr (PS 5.1 + Stop) | Medium | Hợp lệ, đã tái hiện | `Invoke-Tolerant`; đối chứng: không bọc thì ném lỗi |
| CR-4, DD F8: kịch bản 6 có thể gỡ trước khi `ca.exe` giữ mutex / sau khi xong | Medium/Low | Hợp lệ | Chờ mutex tới 15 s, 20 file (~20 s), assert `ca.exe` vẫn chạy sau khi uninstaller xong |
| CR-5: test mutex rò handle khi skip | Medium | Hợp lệ | `before?.Dispose()` trước `Skip.If` |
| CR-6: không đọc được ACL → mã 1 thay vì 3 | Low | Hợp lệ | `InstallLocation` bắt `UnauthorizedAccessException`/`IOException` → không an toàn. Không có test: cần quyền admin để tạo thư mục mà chủ sở hữu không đọc được ACL |
| CR-7: mã thoát/link README trong `.iss` không được ràng | Low | Hợp lệ | `ExitCodes` trong Core; `InstallerScriptTests` kiểm mã và mọi `{#AppUrl}#anchor` có heading trong README |
| CR-8, DD F2: câu "file ConvertAnything không cài" sai khi là đồ sót của lần cài dở | Low/Medium | Hợp lệ | "perhaps left by an earlier ConvertAnything install that did not finish. Delete the folder…" |
| DD F3: "silent hủy" chỉ đúng với `/SUPPRESSMSGBOXES`; có cửa sổ tiến trình | Low | Hợp lệ | Sửa chú thích, spec, ADR; `[Messages]` nhắc Cancel trong cửa sổ tiến trình, bỏ "a few seconds" |
| DD F4: chú thích mutex static nói sai lý do | Low | Hợp lệ | Sửa: Inno cần mutex tới khi tiến trình kết thúc hẳn |
| DD F5, SA-L2: tag ngoài `main` chạy `release.yml` của chính nó; provenance nói quá | Low | Hợp lệ | README thêm `--source-ref refs/tags/v0.1.0 --deny-self-hosted-runners`, câu chữ chính xác hơn. Ruleset cho tag `v*` và `main`: làm ở bước public (HANDOFF mục 3) |
| DD F6: SDK trôi | Low | Trade-off | Hỏng thì hỏng kín; release in `dotnet --info` |
| DD F7, SA-L3: lệnh gỡ tay xóa khóa menu cổ điển không kiểm đánh dấu, đường dẫn wildcard, xóa đệ quy app data | Low | Hợp lệ | `-LiteralPath`, chỉ xóa khóa có đánh dấu, xóa từng thư mục con |
| DD F10: ngoại lệ ngoài bộ lọc; xung đột menu cổ điển trỏ sai mục | Low | Một phần | Mục README riêng cho xung đột menu cổ điển; giữ bộ lọc ngoại lệ (stderr đã vào log) |
| SA Info: mutex bị chiếm tên trong cùng phiên | Info | Trade-off | Đã ghi ở ADR-0005 |
| SA Info: NuGet không khóa (`packages.lock.json`) | Info | Hoãn | Ghi HANDOFF: làm trước v0.2 |
| SA Info: Dependabot không cooldown | Info | Hợp lệ | `cooldown: default-days: 7` |
| SA Info, CR-9: kiểm chữ ký chỉ theo tên chủ thể | Info/Low | Trade-off | Script chỉ cho máy dev; giữ |
| SA Info: SHA pin action chưa đối chiếu | Info | Hợp lệ | Đã đối chiếu bằng `gh api` (2026-10-19): cả 5 action khớp tag ghi trong chú thích |
| CR-10 nit: `AppMutex.cs` chú thích cũ, HANDOFF cũ, spec thiếu test tích hợp, `args[0]`, `winget` không kiểm mã thoát, cảnh báo scope sai với token không có dòng scope | Nit | Hợp lệ | Sửa hết |

Commit: `26ea77b` (check-location), `9d75744` (**chứa cả** bản sửa `.iss` + `test-installer.ps1` cho SA H-1 và F1, dù message chỉ nói `setup-dev.ps1`: commit riêng của chúng hỏng vì dấu `"` trong message qua PowerShell 5.1; đã push nên không viết lại lịch sử), `3faf0d1`, `ea145b9`. E2E 9 kịch bản xanh; test 182+204 xanh.

## Review vòng 4 (trên `478f3cb..6d63d00`): reconcile

Code-reviewer: APPROVE, chỉ Low/Nit. Security: không còn Critical/High/Required, H-1 vòng 3 đã đóng. Doubt-driven: 1 High + 1 Medium Required.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| DD#1: `IsSafe` bỏ qua ACE inherit-only; ACE "chỉ cho file" (`OI\|IO`) trên `{app}` rỗng (hoặc kế thừa từ `Programs`) lọt kiểm tra, file chép vào sau đó cho người khác quyền ghi | High, Required | Hợp lệ | Với thư mục cài và mọi thứ bên trong, ACE inherit-only cũng tính; thư mục cha giữ như cũ (`C:\` có `Authenticated Users (OI)(CI)(IO)M`). Test lý thuyết `OI` và `CI` (đỏ → xanh, có đối chứng), test cha có ACE inherit-only vẫn an toàn |
| DD#2, SA-L1, CR-L2: README `Remove-Item -Recurse` trên PS 5.1 đi theo junction | Medium, Required (DD) | Hợp lệ | `cmd /c rmdir /s /q`, không đi theo junction. Kiểm trong sandbox: junction bên trong và thư mục con là junction đều bị xóa mà đích còn nguyên |
| SA-L2, CR-L1, DD#10: lệnh menu cổ điển báo lỗi đỏ khi không có khóa (trường hợp thường gặp) | Low | Hợp lệ, đã tái hiện | `Get-ItemProperty -Name ConvertAnything -ErrorAction SilentlyContinue`; kiểm: không có khóa thì im lặng |
| SA-L3: README không cảnh báo khi bản nâng cấp bị từ chối | Low | Hợp lệ | README: đã cài trong thư mục bị từ chối thì gỡ tay, không dùng uninstaller, không chỉ sửa quyền |
| DD#3: `%TEMP%` cho tài khoản khác ghi (máy dev này: `CodexSandboxUsers` Modify) → tráo DLL trong `{tmp}\{app}` | Medium | Trade-off | Giống mô hình mối đe dọa của chính Inno (`setup.tmp` cũng ở đó), cửa sổ dài hơn. Ghi rõ giả định trong spec, ADR-0005 và chú thích `.iss`. Khóa ACL của `{tmp}\{app}` bằng `icacls` cần SID người dùng và vẫn có race với thư mục cha ghi được, nên không làm |
| DD#4, CR-L3, CR-L5, CR-N4: kiểm thư mục thất bại luôn đổ cho Smart App Control, mất lý do của `ca.exe`, lỗi giải nén bị nuốt | Medium/Low | Hợp lệ | Lỗi giải nén có thông báo riêng (dung lượng trống); mã 1–2 kèm lý do; mã khác nhắc cả chính sách bảo mật chặn chương trình trong thư mục tạm; lý do rỗng có câu dự phòng |
| CR-L3, DD#8: thêm 137 MB trong `{tmp}` không được tính dung lượng | Low | Hợp lệ | `ExtraDiskSpaceRequired=150000000`. Thời gian: 9–10 s trên SSD; chưa đo HDD (ghi nhận) |
| DD#5, CR-L7: kịch bản 9 không phải regression test cho F1 | Medium | Hợp lệ | `InstallerScriptTests.Link_check_walks_up_to_the_drive_root_itself` kiểm thân `LinkOnPath` (đối chứng âm: đổi lại `RemoveBackslash` thì đỏ); kịch bản 9 ghi là smoke test |
| DD#6, CR-L8: không có đối chứng dương cho `check-location` | Low | Hợp lệ | Test thư mục dưới `%LOCALAPPDATA%` → mã 0 (skip nếu máy không an toàn). Nhánh "setup tự tạo rồi bị từ chối" và "không chạy được" chưa có e2e: cần nới quyền `Programs` thật; ghi nhận |
| DD#7, SA I-1, CR-N3: `ForceDirectories` để lại `Programs` | Low | Hợp lệ | Xóa `Programs` nếu setup vừa tạo (chỉ khi rỗng) |
| DD#9: ngưỡng 5 s phụ thuộc tốc độ máy | Low | Hợp lệ | Ngưỡng = 1 s + 2 × thời gian decode đo ngay trong test (máy này: ngưỡng 1.6 s, thực tế 0.02–0.12 s sau khi hủy) |
| CR-L6: test anchor bắt cả comment `#` trong code block | Low | Hợp lệ | Bỏ code block trước khi lấy heading |
| CR-L4: không có tiến trình/timeout khi giải nén và kiểm | Low | Trade-off | ~1–2 s trên máy này; giống `install-menu` vốn không có timeout. Ghi nhận |
| CR-N1, DD#10: thông điệp từ chối không nói tới trường hợp không đọc được quyền / không phải NTFS cục bộ | Nit | Hợp lệ | Thêm vào cả `check-location` và `install-menu` |
| CR-N2: chú thích trỏ `Program.RefusedLocation` | Nit | Hợp lệ | Trỏ `ExitCodes.RefusedLocation` |
| SA I-3: Smart App Control giờ chặn cả bản cài (mã 7) thay vì cài thiếu menu (mã 10) | Info | Hợp lệ | Ghi trong spec, ADR-0005 |
| CI: `Processes_started_through_a_junction…` quá thời gian, `Twenty_processes…` 22.9 s | (CI) | Không phải lỗi sản phẩm | Log cho thấy đúng một lô, đủ file, nhưng `dev.slow-copy` 1 s mất 15 s: hai assembly test chạy song song trên runner. CI/release chạy từng project ở bước riêng |

## Checkpoint
- [x] Review vòng 1 → reconcile (bảng trên)
- [x] Review vòng 2 → đã sửa
- [x] Review vòng 3 → reconcile (bảng trên)
- [x] Review vòng 4 → reconcile (bảng trên)
- [ ] Review vòng 5 trên phần sửa vòng 4 → không còn Critical/High/Required
- [ ] HANDOFF; commit + push

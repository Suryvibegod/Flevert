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

## Checkpoint
- [x] Review vòng 1 → reconcile (bảng trên)
- [x] Review vòng 2 → đã sửa
- [x] Review vòng 3 → reconcile (bảng trên)
- [ ] Review vòng 4 trên phần sửa vòng 3 → không còn Critical/High/Required
- [ ] HANDOFF; commit + push

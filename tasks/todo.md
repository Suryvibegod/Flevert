# Todo: core

Spec: [SPEC-core.md](../SPEC-core.md) · Plan: [plan.md](plan.md)

Mỗi task chỉ xong khi đạt acceptance criteria **và** Definition of Done (test xanh, không hồi quy, đã chạy thật, tài liệu cập nhật).

---

## Task 1: Khung solution, license, repo ✅

**Description:** Tạo solution .NET 10 với 3 project, cấu hình chung (nullable, warning = error, analyzer AOT), MIT license, `.gitignore`, README tiếng Anh tối thiểu. Commit đầu tiên, tạo repo GitHub private, push.

**Acceptance criteria:**
- [ ] `dotnet build` và `dotnet test` chạy được (1 test mẫu xanh)
- [ ] `ca.exe` build ra là `WinExe` (chạy không mở console)
- [ ] Repo `ConvertAnything` private trên GitHub có commit đầu tiên

**Verification:**
- [ ] `dotnet build ConvertAnything.sln -c Debug` không warning
- [ ] `dotnet test ConvertAnything.sln` xanh
- [ ] `gh repo view ConvertAnything --json visibility` trả `PRIVATE`

**Dependencies:** None

**Files likely touched:** `ConvertAnything.sln`, `Directory.Build.props`, `src/ConvertAnything.Core/*.csproj`, `src/ConvertAnything.Cli/*.csproj`, `tests/ConvertAnything.Core.Tests/*.csproj`, `LICENSE`, `.gitignore`, `README.md`

**Estimated scope:** M

---

## Task 2: OutputPath ✅

**Description:** `OutputPath.Reserve` chọn tên không trùng (` (1)`, ` (2)`...). `OutputPath.WriteAtomically` ghi vào `.<tên>.partial` cùng thư mục rồi đổi tên; lỗi giữa chừng thì xóa file tạm.

**Acceptance criteria:**
- [ ] Trùng tên thì thêm hậu tố tăng dần, không bao giờ trả về đường dẫn đã tồn tại
- [ ] Lỗi khi ghi không để lại file `.partial` và không tạo file output
- [ ] Chạy đúng với tên tiếng Việt có dấu và dấu cách

**Verification:**
- [ ] Test viết trước, chạy đỏ rồi mới xanh: `dotnet test --filter OutputPath`

**Dependencies:** Task 1

**Files likely touched:** `src/ConvertAnything.Core/OutputPath.cs`, `tests/ConvertAnything.Core.Tests/OutputPathTests.cs`

**Estimated scope:** S

---

## Task 3: Lát dọc đầu tiên `ca.exe run dev.copy <file>` ✅

**Description:** Hợp đồng `IOperation`, danh mục thao tác, thao tác `dev.copy` (copy file thành `tên (copy).ext`), parse lệnh `run`, ghi log dòng có cấu trúc vào `%LOCALAPPDATA%\ConvertAnything\logs`. Một file mỗi lần, chưa gom lô.

**Acceptance criteria:**
- [ ] `ca.exe run dev.copy <file>` tạo `<tên> (copy).<ext>` cạnh file gốc; file gốc không đổi (hash giống)
- [ ] Mỗi lần chạy ghi 1 dòng log: thời gian, op-id, file, kết quả, ms
- [ ] op-id không tồn tại hoặc file không tồn tại → exit code khác 0, log ghi lý do

**Verification:**
- [ ] `dotnet test` xanh (test parse lệnh, `dev.copy`, logger)
- [ ] Chạy tay với file trong `D:\Ảnh Đà Lạt\` (bản copy thử), xem file output và log

**Dependencies:** Task 2

**Files likely touched:** `src/ConvertAnything.Core/IOperation.cs`, `src/ConvertAnything.Core/OperationCatalog.cs`, `src/ConvertAnything.Core/Log.cs`, `src/ConvertAnything.Cli/Program.cs`, `tests/ConvertAnything.Core.Tests/RunCommandTests.cs`

**Estimated scope:** M

---

## Checkpoint: Foundation
- [ ] `dotnet test` xanh, `dotnet format --verify-no-changes` sạch
- [ ] Chạy tay `ca.exe run dev.copy` với đường dẫn tiếng Việt ra đúng file và log
- [ ] Mày xem lại trước khi qua Phase 2

---

## Task 4: Menu chuột phải trong registry ✅

**Description:** Dựng mô hình menu từ danh mục thao tác (`MenuPath` "Dev/Copy" → menu lồng). `install-menu [--dev]` ghi vào HKCU theo cấu trúc `ExtendedSubCommandsKey` trong spec, mọi verb có `MultiSelectModel=Player`. `uninstall-menu` xóa mọi key tên bắt đầu `ConvertAnything`. Ghi registry qua interface `IRegistry`.

**Acceptance criteria:**
- [ ] Sau `install-menu --dev`, chuột phải `.txt` (menu cũ) thấy `ConvertAnything > Dev > Copy`; bấm vào ra file copy, không nháy console
- [ ] Chạy `install-menu` 2 lần không tạo key trùng; `uninstall-menu` xong không còn key nào
- [ ] Chọn 100 file menu vẫn hiện

**Verification:**
- [ ] Unit test mô hình menu (bản `IRegistry` giả) và integration test ghi/gỡ thật trong `HKCU\Software\Classes\ConvertAnything.Test`
- [ ] Bấm thật trong Explorer

**Dependencies:** Task 3

**Files likely touched:** `src/ConvertAnything.Core/Menu/MenuModel.cs`, `src/ConvertAnything.Core/Menu/MenuInstaller.cs`, `src/ConvertAnything.Cli/WindowsRegistry.cs`, `src/ConvertAnything.Cli/Program.cs`, `tests/ConvertAnything.Core.Tests/MenuTests.cs`

**Estimated scope:** M

---

## Task 5: Gom lô ✅

**Description:** `run` đầu tiên giữ named mutex `ConvertAnything.<op-id>.<session>` và mở named pipe; các tiến trình sau gửi đường dẫn qua pipe rồi thoát. Chủ lô chờ đến khi 400 ms không có đường dẫn mới, xử lý tuần tự, lỗi từng file ghi log và đi tiếp. Cửa sổ chờ dùng `TimeProvider`.

**Acceptance criteria:**
- [ ] 50 tiến trình `ca.exe run dev.copy` bắn song song → đúng 1 tiến trình xử lý, đủ 50 file output
- [ ] 1 file lỗi (bị khóa) không chặn các file còn lại
- [ ] Hai op-id khác nhau chạy cùng lúc không lẫn lô

**Verification:**
- [ ] Unit test logic chờ với `FakeTimeProvider`; integration test 50 tiến trình
- [ ] Chọn 20 file trong Explorer, bấm `Dev > Copy`

**Dependencies:** Task 4

**Files likely touched:** `src/ConvertAnything.Core/Batching/BatchCoordinator.cs`, `src/ConvertAnything.Core/Batching/BatchWindow.cs`, `src/ConvertAnything.Cli/Program.cs`, `tests/ConvertAnything.Core.Tests/BatchTests.cs`

**Estimated scope:** M

---

## Task 6: Thông báo cho cả lô ✅

**Description:** Chủ lô xong thì hiện balloon qua WinForms `NotifyIcon.ShowBalloonTip` (hiện thành toast trên Win10/11): `"Done: 20 files"` hoặc `"Done: 18 files, 2 failed. See log."`, rồi thoát. Lô chạy > 2 giây thì hiện cửa sổ tiến độ (file thứ mấy / tổng, nút Cancel); Cancel dừng sau file đang xử lý.

**Acceptance criteria:**
- [ ] Lô 20 file → đúng 1 thông báo
- [ ] Có lỗi → thông báo ghi số file lỗi
- [ ] Tiến trình thoát sau khi thông báo hiện, không để icon kẹt ở khay hệ thống
- [ ] Lô > 2 giây có cửa sổ tiến độ; Cancel không để lại file `.partial`

**Verification:**
- [ ] Unit test chuỗi nội dung thông báo
- [ ] Bấm thật: lô thành công và lô có 1 file bị khóa

**Dependencies:** Task 5

**Files likely touched:** `src/ConvertAnything.Core/BatchSummary.cs`, `src/ConvertAnything.Cli/BatchUi.cs`, `src/ConvertAnything.Cli/Program.cs`, `tests/ConvertAnything.Core.Tests/BatchSummaryTests.cs`

**Estimated scope:** M

---

## Checkpoint: Explorer integration
- [ ] Success criteria 1–6 trong spec đạt khi bấm thật
- [ ] Mày bấm thử trên máy và duyệt

---

## Task 7: Đường dẫn dài và Unicode ✅

**Description:** App manifest `longPathAware`; test đường dẫn > 260 ký tự và tên tiếng Việt / emoji qua toàn bộ luồng `run`.

**Acceptance criteria:**
- [ ] `dev.copy` chạy với file có đường dẫn 300 ký tự
- [ ] Tên `Ảnh Đà Lạt 🍊.txt` ra đúng `Ảnh Đà Lạt 🍊 (copy).txt`

**Verification:**
- [ ] `dotnet test --filter LongPath`
- [ ] Chạy tay qua Explorer với file đường dẫn dài

**Dependencies:** Task 6

**Files likely touched:** `src/ConvertAnything.Cli/app.manifest`, `src/ConvertAnything.Cli/ConvertAnything.Cli.csproj`, `tests/ConvertAnything.Core.Tests/LongPathTests.cs`

**Estimated scope:** S

---

## Task 8: Khởi động nhanh và log ✅

**Description:** Đo khởi động lạnh bản publish self-contained ReadyToRun. Xóa log cũ hơn 7 ngày khi khởi động.

**Acceptance criteria:**
- [ ] Khởi động lạnh `ca.exe run` < 300 ms (trung vị 10 lần `Measure-Command`)
- [ ] Log cũ hơn 7 ngày bị xóa

**Verification:**
- [ ] Script đo `scripts/measure-startup.ps1` in trung vị
- [ ] `dotnet test --filter LogRetention`

**Dependencies:** Task 7

**Files likely touched:** `src/ConvertAnything.Cli/ConvertAnything.Cli.csproj`, `src/ConvertAnything.Core/Log.cs`, `scripts/measure-startup.ps1`, `tests/ConvertAnything.Core.Tests/LogRetentionTests.cs`

**Estimated scope:** S

---

## Task 9: Review và tài liệu

**Description:** Chạy `code-review-and-quality`, `code-simplification`, `security-and-hardening` trên toàn bộ module; sửa những gì tìm ra. README tiếng Anh: tool là gì, build, cài menu dev, giới hạn 100 file. ADR `docs/adr/0001-registry-extendedsubcommandskey.md`.

**Acceptance criteria:**
- [ ] Không còn finding mức Critical/Important chưa xử lý
- [ ] README đủ để người lạ clone, build, bấm thử `dev.copy`
- [ ] ADR ghi lý do chọn `ExtendedSubCommandsKey` thay `SubCommands`

**Verification:**
- [ ] `dotnet test` xanh, `dotnet format --verify-no-changes` sạch
- [ ] Làm theo README từ clone mới thành công

**Dependencies:** Task 8

**Files likely touched:** `README.md`, `docs/adr/0001-registry-extendedsubcommandskey.md`, file sửa theo review

**Estimated scope:** M

---

## Checkpoint: Complete
- [ ] Đủ 9 success criteria trong spec
- [ ] Mày duyệt, rồi push lên GitHub

---

# Phase 4: Sửa theo review (code-review, security-audit, doubt-driven)

Nguồn: 3 reviewer bối cảnh trống, 2026-10-19. Cross-model bị bỏ qua (người dùng yêu cầu chạy không hỏi).

## R1: Log không bao giờ làm hỏng lô
- [x] `RunLog.Write` / `Prune` nuốt mọi exception (kể cả `UnauthorizedAccessException`)
- [x] Ghi log giữa nhiều tiến trình qua named mutex `Local\ConvertAnything.Log` (append không nguyên tử)
- [x] Mọi trường log qua `OneLine` (CR/LF/TAB không giả được dòng log)
- [x] `BatchUi`: work lỗi vẫn gọi `Finish`, tiến trình luôn thoát; exception chưa bắt ghi log rồi thoát
- Nguồn: C1, doubt#1, L3, O1

## R2: Giao thức gom lô v2
- [x] Chủ lô nhận tiếp file **trong lúc đang xử lý** (hàng đợi), đóng khi hết việc + 400 ms không có file mới → không tách lô vì tiến trình khởi động chậm, không tốn 400 ms trước file đầu
- [x] Timeout 2 s cho đọc/ghi/ack mỗi kết nối; `IOException` lúc chờ kết nối không làm chết chủ lô
- [x] Thứ tự: đọc → ack → flush → `WaitForPipeDrain` → mới thêm vào hàng đợi
- [x] Gói tin UTF-16 thô (giữ được surrogate lẻ); bỏ đường dẫn rỗng; tối đa 1000 file/lô
- [x] Khử trùng bằng so sánh `Ordinal`
- [x] Tên pipe gồm SID + bí mật ngẫu nhiên lưu trong `%LOCALAPPDATA%\ConvertAnything\pipe.key`; hai đầu kiểm tra tiến trình bên kia chạy cùng file exe
- [x] `UnauthorizedAccessException` → xử lý một mình ngay, không thử lại 40 s
- [x] Test: tiến trình `ca.exe` thật (20 cái), file đến trễ trong lúc xử lý vẫn vào lô; khôi phục `SetMinThreads`
- Nguồn: doubt#2–8, M1, L1, R2, R3, R4

## R3: OutputPath an toàn hơn
- [x] `Path.Exists` (thư mục trùng tên cũng tính là đã có); tối đa 10 000 lần thử
- [x] Cắt bớt stem để `tên (n).ext` không vượt 255 ký tự
- [x] Lỗi xóa file tạm không che lỗi gốc
- Nguồn: L2, O2, O3

## R4: Khởi động an toàn
- [x] `SetCurrentDirectory(AppContext.BaseDirectory)` + `SetDefaultDllDirectories` ngay đầu `Main`
- [x] Gọi `explorer.exe` bằng đường dẫn tuyệt đối trong `%WINDIR%`
- Nguồn: H2

## R5: install-menu an toàn
- [x] Từ chối đăng ký nếu thư mục exe cho người khác ngoài user/SYSTEM/Administrators quyền ghi (trừ `--dev`)
- [x] Từ chối nếu `ProcessPath` là `dotnet.exe`
- [x] Kiểm op-id `^[a-z0-9.-]+$`, nhãn menu không chứa `\`, không là `shell`, không trùng (không phân biệt hoa thường), không vừa lá vừa nhánh; chỉ số verb 3 chữ số
- Nguồn: H1, I1, O4, nit

## R6: Đầu vào
- [x] Từ chối file là symlink/junction (`LinkTarget != null`); file OneDrive (reparse cloud) vẫn chạy
- [x] Runner từ chối file có đuôi không thuộc `operation.Extensions`
- Nguồn: M2, I2

## R7: CLI và thông báo
- [x] `AttachConsole(ATTACH_PARENT_PROCESS)` cho lệnh khác `run` để `list-ops` / usage in ra terminal
- [x] Thông báo lỗi ghi chỗ xem log; balloon mở đúng file log của lô (không tính lại sau nửa đêm)
- Nguồn: R5, O5, nit

## R8: Đo khởi động đúng đường đi
- [x] Biến môi trường `CONVERTANYTHING_LOG_DIR` đổi thư mục log (cho test và script đo)
- [x] `measure-startup.ps1` đo tiến trình phụ gửi vào chủ lô đang chạy, log vào thư mục tạm
- Nguồn: R6

## R9: Spec, ADR
- [x] SPEC-core: Cancel dừng ngay file đang chạy (bỏ file dở); không mutex; tên tạm `.ca-<guid>.partial`; menu theo từng đuôi file
- [x] ADR-0002 gom lô (kèm trade-off: chủ lô chết sau ack thì mất file phụ; race junction thư mục cha)
- Nguồn: R1 (contract fix), nits, trade-offs

## Checkpoint: Review fixes
- [ ] Cả 3 reviewer chạy lại trên phần đã sửa, không còn Critical/High/Required

# Phase 5: Review vòng 4 (xác minh các bản sửa vòng 3)

Nguồn: 3 reviewer bối cảnh trống, chỉ đọc, 2026-10-19, trên diff `6498e3f..HEAD`. Không có Critical/High. Cross-model bỏ qua.
Viết tắt: CR = code-reviewer, SA = security-auditor, DD = doubt-driven.

## Đối chiếu (reconcile)

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| Xoay khóa ném lỗi khác IO → `TryComplete` không chạy, chủ lô treo (CR1, DD-R1) | Required | Hợp lệ | F1 |
| Junction trong đường dẫn cài đặt qua mặt kiểm tra ACL (SA1) | Required | Hợp lệ | F2 |
| Log thử lại mọi `IOException` tới 2 s mỗi file (CR3, SA7, DD-M4) | Medium | Hợp lệ | F3 |
| Trả `Full`/`Refused` rồi `Disconnect` ngay, client mất câu trả lời (DD-M1) | Medium | Hợp lệ | F4 |
| `ReplaceAsync` thả tên pipe; tiến trình mới thành chủ lô thứ hai rồi bị xoay mất khóa (DD-M2, SA6) | Medium | Hợp lệ | F5 |
| Kết nối lỗi/không tin cậy reset đồng hồ rảnh → giữ lô mở mãi; cửa sổ rảnh tụt còn 20 ms (SA2, DD-M3) | Medium | Hợp lệ | F6 |
| `TrySecure` chỉ bắt IO → chủ lô chết sau khi đã ack (DD-M5) | Medium | Hợp lệ | F1 |
| PipePeer so đường dẫn ảnh sau reparse với `ProcessPath` → qua junction/subst mọi peer bị từ chối (SA3) | Medium | Hợp lệ | F7 |
| `GetFinalPathNameByHandle` dạng DOS lỗi với volume không có ký tự ổ (CR2) | Medium | Hợp lệ (chưa kiểm thật) | F8 |
| Ký tự điều khiển BEL/BS trong `BatchTests.cs` (CR4, DD) | Medium (test) | Hợp lệ | F9 |
| Kiểm đuôi trên đường dẫn yêu cầu, operation nhận đường dẫn thật (SA4) | Low | Hợp lệ | F8 |
| Tên kết thúc bằng `.`/khoảng trắng bị chuẩn hóa thành file khác (SA5) | Low | Hợp lệ | F8 |
| `\host\pipe\…` lọt allow-list, CreateFileW không có SQOS (CR10) | Low | Hợp lệ | F8 |
| Giữ tiền tố `\?\UNC\` khi dài > 260 (CR nit, SA8, DD) | Low | Hợp lệ | F8 |
| Poll 20 ms suốt lúc chuyển đổi dài (CR7, DD) | Low | Hợp lệ | F6 |
| Gần hạn 10 s, timeout IO tụt còn 1 ms → trùng lặp (CR8, DD) | Low | Hợp lệ | F4 |
| Thử lại khi xoay khóa không bắt `UnauthorizedAccessException` (CR6, DD) | Low | Hợp lệ | F1 |
| `Full` làm bên gửi xoay khóa, ADR nói "tự xử lý" (CR5, DD) | Low | Hợp đồng sai → sửa ADR (giữ hành vi: lô >1000 file sang khóa mới, ít cửa sổ hơn) | F10 |
| Từ chối file nhiều hard link chặn cả file hợp lệ (wallpaper, pnpm) (CR9) | Low | Trade-off chấp nhận, ghi SPEC/ADR | F10 |
| README/SPEC còn lệnh `install-menu --dev` không có `--allow-insecure-location` (CR11) | Low | Hợp lệ | F10 |
| Test không chứng minh fix: dangling link, `\./C:`, long path, xoay khóa có điều kiện (DD) | — | Hợp lệ một phần | F9 |
| OneDrive placeholder, đổi tên thư mục cha khi đang pin (DD, SA) | — | Chưa kiểm | F9 (test thư mục cha); OneDrive: kiểm tay, ghi HANDOFF |
| Nit: summary trùng ở SecureDirectory, lỗi cũ khi buffer FinalPath thiếu | Nit | Hợp lệ | F10 |

## Việc
- [ ] F1: Đóng lô luôn hoàn tất: `onClosed` bọc catch-all, `TryComplete` trong finally; `TryRotateSecret`/`TrySecure` bắt mọi lỗi; thử lại xoay khóa cả khi access denied
- [ ] F2: `install-menu` từ chối khi thư mục cài hoặc thư mục cha là reparse point (trừ `--allow-insecure-location`)
- [ ] F3: Log chỉ thử lại khi sharing/lock violation; sau một lần hết hạn thì không chờ nữa trong tiến trình
- [ ] F4: Trả lời `Full`/`Refused` rồi chờ client đóng; chỉ cắt timeout cho Connect, IO giữ 2 s
- [ ] F5: Không tạo lại được pipe (mất tên) → không xoay khóa; bắt `UnauthorizedAccessException`
- [ ] F6: Đồng hồ rảnh chỉ reset sau tin nhắn tin cậy, tính từ lúc phục vụ xong; hết cửa sổ mà đang bận thì chờ cả cửa sổ
- [ ] F7: PipePeer so ảnh của peer với ảnh của chính mình (QueryFullProcessImageName cả hai); test: 2 ca.exe chạy qua junction gom một lô
- [ ] F8: Đầu vào: kiểm đuôi trên đường dẫn thật; từ chối tên kết thúc `.`/space; từ chối `\pipe\`, thêm SQOS; FinalPath dự phòng VOLUME_NAME_GUID; giữ `\?\UNC\` khi dài
- [ ] F9: Test: sửa ký tự điều khiển; xoay khóa có điều kiện; thư mục cha không đổi tên được khi pin; kiểm lại test dangling link bằng đối chứng âm
- [ ] F10: Tài liệu: ADR-0002 (`Full`, hard link), SPEC-core, README lệnh cài dev
- [ ] Review vòng 5 trên phần sửa

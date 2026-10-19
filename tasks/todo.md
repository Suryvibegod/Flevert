# Todo: core

> Nhật ký lịch sử: ghi lại công việc và các vòng review lúc làm. Tên cũ (`ca.exe`, `ConvertAnything`) và mã commit cũ trong đây giữ nguyên như lúc ghi; trạng thái hiện tại ở [HANDOFF.md](HANDOFF.md).

Spec: [docs/specs/core.md](../docs/specs/core.md) · Plan: [plan.md](plan.md)

Mỗi task chỉ xong khi đạt acceptance criteria **và** Definition of Done (test xanh, không hồi quy, đã chạy thật, tài liệu cập nhật).

---

## Task 1: Khung solution, license, repo ✅

**Description:** Tạo solution .NET 10 với 3 project, cấu hình chung (nullable, warning = error, analyzer AOT), MIT license, `.gitignore`, README tiếng Anh tối thiểu. Commit đầu tiên, tạo repo GitHub private, push.

**Acceptance criteria:**
- [ ] `dotnet build` và `dotnet test` chạy được (1 test mẫu xanh)
- [ ] `flevert.exe` build ra là `WinExe` (chạy không mở console)
- [ ] Repo `Flevert` private trên GitHub có commit đầu tiên

**Verification:**
- [ ] `dotnet build Flevert.sln -c Debug` không warning
- [ ] `dotnet test Flevert.sln` xanh
- [ ] `gh repo view Flevert --json visibility` trả `PRIVATE`

**Dependencies:** None

**Files likely touched:** `Flevert.sln`, `Directory.Build.props`, `src/Flevert.Core/*.csproj`, `src/Flevert.Cli/*.csproj`, `tests/Flevert.Core.Tests/*.csproj`, `LICENSE`, `.gitignore`, `README.md`

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

**Files likely touched:** `src/Flevert.Core/OutputPath.cs`, `tests/Flevert.Core.Tests/OutputPathTests.cs`

**Estimated scope:** S

---

## Task 3: Lát dọc đầu tiên `flevert.exe run dev.copy <file>` ✅

**Description:** Hợp đồng `IOperation`, danh mục thao tác, thao tác `dev.copy` (copy file thành `tên (copy).ext`), parse lệnh `run`, ghi log dòng có cấu trúc vào `%LOCALAPPDATA%\Flevert\logs`. Một file mỗi lần, chưa gom lô.

**Acceptance criteria:**
- [ ] `flevert.exe run dev.copy <file>` tạo `<tên> (copy).<ext>` cạnh file gốc; file gốc không đổi (hash giống)
- [ ] Mỗi lần chạy ghi 1 dòng log: thời gian, op-id, file, kết quả, ms
- [ ] op-id không tồn tại hoặc file không tồn tại → exit code khác 0, log ghi lý do

**Verification:**
- [ ] `dotnet test` xanh (test parse lệnh, `dev.copy`, logger)
- [ ] Chạy tay với file trong `D:\Ảnh Đà Lạt\` (bản copy thử), xem file output và log

**Dependencies:** Task 2

**Files likely touched:** `src/Flevert.Core/IOperation.cs`, `src/Flevert.Core/OperationCatalog.cs`, `src/Flevert.Core/Log.cs`, `src/Flevert.Cli/Program.cs`, `tests/Flevert.Core.Tests/RunCommandTests.cs`

**Estimated scope:** M

---

## Checkpoint: Foundation
- [ ] `dotnet test` xanh, `dotnet format --verify-no-changes` sạch
- [ ] Chạy tay `flevert.exe run dev.copy` với đường dẫn tiếng Việt ra đúng file và log
- [ ] Mày xem lại trước khi qua Phase 2

---

## Task 4: Menu chuột phải trong registry ✅

**Description:** Dựng mô hình menu từ danh mục thao tác (`MenuPath` "Dev/Copy" → menu lồng). `install-menu [--dev]` ghi vào HKCU theo cấu trúc `ExtendedSubCommandsKey` trong spec, mọi verb có `MultiSelectModel=Player`. `uninstall-menu` xóa mọi key tên bắt đầu `Flevert`. Ghi registry qua interface `IRegistry`.

**Acceptance criteria:**
- [ ] Sau `install-menu --dev`, chuột phải `.txt` (menu cũ) thấy `Flevert > Dev > Copy`; bấm vào ra file copy, không nháy console
- [ ] Chạy `install-menu` 2 lần không tạo key trùng; `uninstall-menu` xong không còn key nào
- [ ] Chọn 100 file menu vẫn hiện

**Verification:**
- [ ] Unit test mô hình menu (bản `IRegistry` giả) và integration test ghi/gỡ thật trong `HKCU\Software\Classes\Flevert.Test`
- [ ] Bấm thật trong Explorer

**Dependencies:** Task 3

**Files likely touched:** `src/Flevert.Core/Menu/MenuModel.cs`, `src/Flevert.Core/Menu/MenuInstaller.cs`, `src/Flevert.Cli/WindowsRegistry.cs`, `src/Flevert.Cli/Program.cs`, `tests/Flevert.Core.Tests/MenuTests.cs`

**Estimated scope:** M

---

## Task 5: Gom lô ✅

**Description:** `run` đầu tiên giữ named mutex `Flevert.<op-id>.<session>` và mở named pipe; các tiến trình sau gửi đường dẫn qua pipe rồi thoát. Chủ lô chờ đến khi 400 ms không có đường dẫn mới, xử lý tuần tự, lỗi từng file ghi log và đi tiếp. Cửa sổ chờ dùng `TimeProvider`.

**Acceptance criteria:**
- [ ] 50 tiến trình `flevert.exe run dev.copy` bắn song song → đúng 1 tiến trình xử lý, đủ 50 file output
- [ ] 1 file lỗi (bị khóa) không chặn các file còn lại
- [ ] Hai op-id khác nhau chạy cùng lúc không lẫn lô

**Verification:**
- [ ] Unit test logic chờ với `FakeTimeProvider`; integration test 50 tiến trình
- [ ] Chọn 20 file trong Explorer, bấm `Dev > Copy`

**Dependencies:** Task 4

**Files likely touched:** `src/Flevert.Core/Batching/BatchCoordinator.cs`, `src/Flevert.Core/Batching/BatchWindow.cs`, `src/Flevert.Cli/Program.cs`, `tests/Flevert.Core.Tests/BatchTests.cs`

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

**Files likely touched:** `src/Flevert.Core/BatchSummary.cs`, `src/Flevert.Cli/BatchUi.cs`, `src/Flevert.Cli/Program.cs`, `tests/Flevert.Core.Tests/BatchSummaryTests.cs`

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

**Files likely touched:** `src/Flevert.Cli/app.manifest`, `src/Flevert.Cli/Flevert.Cli.csproj`, `tests/Flevert.Core.Tests/LongPathTests.cs`

**Estimated scope:** S

---

## Task 8: Khởi động nhanh và log ✅

**Description:** Đo khởi động lạnh bản publish self-contained ReadyToRun. Xóa log cũ hơn 7 ngày khi khởi động.

**Acceptance criteria:**
- [ ] Khởi động lạnh `flevert.exe run` < 300 ms (trung vị 10 lần `Measure-Command`)
- [ ] Log cũ hơn 7 ngày bị xóa

**Verification:**
- [ ] Script đo `scripts/measure-startup.ps1` in trung vị
- [ ] `dotnet test --filter LogRetention`

**Dependencies:** Task 7

**Files likely touched:** `src/Flevert.Cli/Flevert.Cli.csproj`, `src/Flevert.Core/Log.cs`, `scripts/measure-startup.ps1`, `tests/Flevert.Core.Tests/LogRetentionTests.cs`

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
- [x] Ghi log giữa nhiều tiến trình qua named mutex `Local\Flevert.Log` (append không nguyên tử)
- [x] Mọi trường log qua `OneLine` (CR/LF/TAB không giả được dòng log)
- [x] `BatchUi`: work lỗi vẫn gọi `Finish`, tiến trình luôn thoát; exception chưa bắt ghi log rồi thoát
- Nguồn: C1, doubt#1, L3, O1

## R2: Giao thức gom lô v2
- [x] Chủ lô nhận tiếp file **trong lúc đang xử lý** (hàng đợi), đóng khi hết việc + 400 ms không có file mới → không tách lô vì tiến trình khởi động chậm, không tốn 400 ms trước file đầu
- [x] Timeout 2 s cho đọc/ghi/ack mỗi kết nối; `IOException` lúc chờ kết nối không làm chết chủ lô
- [x] Thứ tự: đọc → ack → flush → `WaitForPipeDrain` → mới thêm vào hàng đợi
- [x] Gói tin UTF-16 thô (giữ được surrogate lẻ); bỏ đường dẫn rỗng; tối đa 1000 file/lô
- [x] Khử trùng bằng so sánh `Ordinal`
- [x] Tên pipe gồm SID + bí mật ngẫu nhiên lưu trong `%LOCALAPPDATA%\Flevert\pipe.key`; hai đầu kiểm tra tiến trình bên kia chạy cùng file exe
- [x] `UnauthorizedAccessException` → xử lý một mình ngay, không thử lại 40 s
- [x] Test: tiến trình `flevert.exe` thật (20 cái), file đến trễ trong lúc xử lý vẫn vào lô; khôi phục `SetMinThreads`
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
- [x] Biến môi trường `FLEVERT_LOG_DIR` đổi thư mục log (cho test và script đo)
- [x] `measure-startup.ps1` đo tiến trình phụ gửi vào chủ lô đang chạy, log vào thư mục tạm
- Nguồn: R6

## R9: Spec, ADR
- [x] SPEC-core: Cancel dừng ngay file đang chạy (bỏ file dở); không mutex; tên tạm `.ca-<guid>.partial`; menu theo từng đuôi file
- [x] ADR-0002 gom lô (kèm trade-off: chủ lô chết sau ack thì mất file phụ; race junction thư mục cha)
- Nguồn: R1 (contract fix), nits, trade-offs

## Checkpoint: Review fixes
- [ ] Cả 3 reviewer chạy lại trên phần đã sửa, không còn Critical/High/Required

# Phase 5: Review vòng 4 (xác minh các bản sửa vòng 3)

Nguồn: 3 reviewer bối cảnh trống, chỉ đọc, 2026-10-19, trên diff `eb44cb3..HEAD`. Không có Critical/High. Cross-model bỏ qua.
Viết tắt: CR = code-reviewer, SA = security-auditor, DD = doubt-driven.

## Đối chiếu (reconcile)

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| Xoay khóa ném lỗi khác IO → `TryComplete` không chạy, chủ lô treo (CR1, DD-R1) | Required | Hợp lệ | F1 |
| Junction trong đường dẫn cài đặt qua mặt kiểm tra ACL (SA1) | Required | Hợp lệ | F2 |
| Log thử lại mọi `IOException` tới 2 s mỗi file (CR3, SA7, DD-M4) | Medium | Hợp lệ | F3 |
| Trả `Full`/`Refused` rồi `Disconnect` ngay, client mất câu trả lời (DD-M1) | Medium | Nhiễu với code hiện tại: pipe không buffer, Write chỉ xong khi client đã đọc (thí nghiệm). Thêm test chặn nếu ai thêm buffer | F4 |
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
- [x] F1: Đóng lô luôn hoàn tất: `onClosed` bọc catch-all, `TryComplete` trong finally; `TryRotateSecret`/`TrySecure` bắt mọi lỗi; thử lại xoay khóa cả khi access denied
- [x] F2: `install-menu` từ chối khi thư mục cài hoặc thư mục cha là reparse point (trừ `--allow-insecure-location`)
- [x] F3: Log chỉ thử lại khi sharing/lock violation; sau một lần hết hạn thì không chờ nữa trong tiến trình
- [x] F4: (DD-M1 là nhiễu, xem bảng; ghi rõ buffer 0 trong code + test chặn) chỉ cắt timeout cho Connect, IO giữ 2 s
- [x] F5: Không tạo lại được pipe (mất tên) → không xoay khóa; bắt `UnauthorizedAccessException`
- [x] F6: Đồng hồ rảnh chỉ reset sau tin nhắn tin cậy, tính từ lúc phục vụ xong; hết cửa sổ mà đang bận thì chờ cả cửa sổ
- [x] F7: PipePeer so ảnh của peer với ảnh của chính mình (QueryFullProcessImageName cả hai); test: 2 flevert.exe chạy qua junction gom một lô
- [x] F8: Đầu vào: kiểm đuôi trên đường dẫn thật; từ chối tên kết thúc `.`/space; từ chối `\pipe\`, thêm SQOS; FinalPath dự phòng VOLUME_NAME_GUID; giữ `\\?\UNC\` khi dài
- [x] F9: Test: sửa ký tự điều khiển; xoay khóa có điều kiện; thư mục cha không đổi tên được khi pin; kiểm lại test dangling link bằng đối chứng âm
- [x] F10: Tài liệu: ADR-0002 (`Full`, hard link), SPEC-core, README lệnh cài dev
- [ ] Review vòng 5 trên phần sửa

# Phase 6: Review vòng 5

Nguồn: 3 reviewer bối cảnh trống, 2026-10-19, trên `0e2e554..HEAD`. Không có Critical/High. CR = code-reviewer, SA = security-auditor, DD = doubt-driven.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| Thư mục (không chỉ tên file) kết thúc bằng `.` làm đọc/ghi nhầm thư mục khác: qua `GetFullPath` trong Program, và qua `FinalPath` bỏ `\\?\` sau khi pin (CR-R1, DD1 tái hiện bằng flevert.exe thật, SA3) | Required | Hợp lệ | G1 |
| Leader ghi `Refused` trước khi đọc yêu cầu; pipe không buffer nên hai bên chờ nhau tới timeout 2 s, follower không bao giờ nhận `Refused` (CR-M1) | Medium | Hợp lệ (bằng chứng mới, khác DD-M1 vòng 4) | G2 |
| Kiểm tra nơi cài tin vào chuỗi đường dẫn: ổ `subst`, ổ mạng, UNC qua mặt (SA1) | Medium | Hợp lệ | G3 |
| `\\;LanmanRedirector\host\pipe\…` qua mặt chặn pipe/IPC$; `FileInfo.Exists` trước khi pin đã kết nối tới pipe (SA2, DD2) | Low/Medium | Hợp lệ (SQOS và không ghi gì nên không mạo danh được) | G4 |
| Alternate data stream `a:s.png` được nhận (SA4) | Low | Hợp lệ | G4 |
| Link bên trong thư mục cài không bị kiểm (SA5) | Low | Hợp lệ | G3 |
| `IsLink` chặn cả reparse không phải link (OneDrive) (CR-L2, SA5) | Low | Trade-off chấp nhận: thư mục cài không nằm trong OneDrive; chặn rộng an toàn hơn | ghi chú |
| `SECURITY_SQOS_PRESENT` trùng bit `FILE_FLAG_OPEN_NO_RECALL` (CR-L1, DD3) | Low | Hợp lệ, chưa kiểm OneDrive | ghi chú code + kiểm tay OneDrive (HANDOFF) |
| Follower chậm dưới tải có thể tách lô (không mất, không trùng) (DD4) | Low | Trade-off chấp nhận | ghi ADR |
| Comment PipePeer nói 8.3 làm đổi đường dẫn kernel (DD5) | Nit | Hợp lệ | G4 |

## Việc
- [x] G1: Từ chối mọi đoạn đường dẫn kết thúc bằng `.` hoặc khoảng trắng (trừ `.`/`..`): trong Program trước `GetFullPath`, trong Runner, và trên đường dẫn thật sau khi pin
- [x] G2: Leader đọc (bỏ) yêu cầu của client không tin cậy rồi mới trả `Refused`; test qua seam tin cậy
- [x] G3: install-menu: thư mục phải ở ổ cố định cục bộ và đường dẫn thật (từ handle) trùng đường dẫn đưa vào; không reparse point bên trong thư mục
- [x] G4: Chặn tên server bắt đầu bằng `;`; sau khi pin yêu cầu `FILE_TYPE_DISK`; bỏ `FileInfo.Exists` trước pin; chặn `:` ngoài ký tự ổ; sửa comment PipePeer; ghi chú bit SQOS
- [x] G5: Tài liệu (ADR-0002 tách lô dưới tải, SPEC-core đầu vào)
- [x] Review vòng 6 trên phần sửa (`4b7b000..HEAD`, core + cli): không còn Critical/High/Required (Phase 7)
- [x] Test gom lô qua junction hỏng 1/6: DD vòng 6 xác định là CPU bị chiếm khi chạy cả bộ, follower lỡ cửa sổ 400 ms (trade-off đã ghi ở ADR-0002); sửa ở test (Phase 7)

# Phase 7: Review vòng 6 (core)

Nguồn: 3 reviewer bối cảnh trống, 2026-10-19, trên `4b7b000..HEAD`. **Không có Critical/High/Required**; mọi claim vòng 5 đứng vững (test hỏng khi gỡ fix). Còn Low/Nit:

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| `ToLongPath` (GetLongPathNameW) mở đường dẫn có `~` trước mọi kiểm tra, không SQOS; qua junction tới không gian pipe thì kết nối được pipe (SA1, CR3, DD) | Low | Hợp lệ | H1: bỏ khỏi Program; tên output đã lấy từ đường dẫn thật sau pin |
| `FinalPath` bỏ `\\?\` khi < 260 ký tự: tên thiết bị (`NUL.jpg`) trên Windows 10 (CR2, SA4) | Low | Hợp lệ | H2: chỉ bỏ khi `GetFullPath` không đổi đường dẫn |
| Kiểm `FILE_TYPE_DISK` chưa có test (CR1) | Low | Hợp lệ | H3 |
| Hai helper bỏ tiền tố khác nhau; hằng thông báo nằm ở Runner (CR4, CR8) | Nit | Hợp lệ | H3 |
| Đường dẫn tương đối có thư mục `x.` nhận thông báo "absolute" (CR5) | Nit | Hợp lệ | H3 |
| Test untrusted dùng đồng hồ < 1 s dễ hỏng khi máy bận; test subst hỏng thay vì skip khi ổ bị phiên khác chiếm (CR7, CR9) | Nit | Hợp lệ | H4 |
| Test gom lô qua junction hỏng 1/6 dưới tải (DD) | Low | Hợp lệ (ở test) | H4: cửa sổ rảnh rộng hơn cho test tích hợp qua biến môi trường |
| Ổ cứng gắn ngoài / VHD báo là Fixed (SA2) | Low | Trade-off chấp nhận: installer cài vào Program Files/LocalAppData; ghi SPEC | ghi chú |
| Volume FAT/exFAT không có ACL (SA3) | Low | Hợp lệ | H5: yêu cầu FILE_PERSISTENT_ACLS |
| Thông báo lỗi với thư mục mang đuôi ảnh đổi thành "Access is denied." (CR6) | Nit | Chấp nhận | — |

## Việc
- [x] H1: Bỏ `ToLongPath` trong Program (vòng 7: vô tác dụng vì `Path.GetFullPath` vẫn mở rộng 8.3; xem Phase 8 P1)
- [x] H2: `FinalPath` giữ `\\?\` khi bỏ nó làm đổi đường dẫn
- [x] H3: Test FILE_TYPE_DISK; một helper bỏ tiền tố; hằng thông báo ở InputPath; đường dẫn tương đối có tên kết thúc bằng dấu chấm nhận thông báo dấu chấm (kiểm stream vẫn sau kiểm absolute)
- [x] H4: Bỏ đồng hồ trong test untrusted; test subst skip khi `subst` lỗi; `FLEVERT_BATCH_IDLE_MS` cho test gom lô qua junction
- [x] H5: install-menu yêu cầu volume có ACL
- [ ] Review vòng 7 trên Phase 7 (bỏ ToLongPath, Shorten, ACL volume) cùng review vòng 2 module image

# Phase 8: Review vòng 7 (core)

Nguồn: 1 reviewer bối cảnh trống, 3 góc (code, bảo mật, doubt), 2026-10-19, trên `7e32df6..HEAD`.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| H1 không có tác dụng: `Path.GetFullPath` của .NET tự mở rộng tên 8.3 (GetLongPathNameW) nên vẫn mở đường dẫn trước kiểm tra; reviewer không tái hiện được kết nối pipe | Required (tác động Low) | Hợp đồng sai: ghi nhận sai trong commit/todo/comment | P1: giữ hành vi (chấp nhận, tác động Low), sửa comment, todo, ADR |
| Kiểm đuôi trước pin thấy đuôi 8.3 cắt cụt (.WEB, .JPE, .HEI) với đối số `\\?\` ngắn | Low | Hợp lệ (chỉ dòng lệnh) | P2 |
| SPEC thiếu `FLEVERT_BATCH_IDLE_MS`; comment test "well past 400 ms" sai; todo H3 nói thứ tự kiểm khác code | Nit | Hợp lệ | P3 |
| H5 không có test | Low | Chấp nhận: không có volume FAT; đối chứng đảo điều kiện đã chạy | — |

## Việc
- [x] P1: Sửa ghi nhận H1 (comment Program, todo, ADR-0001)
- [x] P2: Bỏ kiểm đuôi trước pin khi tên có `~` (kiểm lại trên đường dẫn thật); test với đuôi 4 chữ
- [x] P3: SPEC biến môi trường; comment test; todo H3
- [ ] Test gom lô qua junction hỏng một lần nữa khi chạy cả solution (bộ image mã hóa song song), log rỗng; không tái hiện trong 5 lần chạy sau; test giờ in mã thoát, file log, output khi hỏng

# Phase 9: Review vòng 8 (core)

Nguồn: 1 reviewer bối cảnh trống, 3 góc, 2026-10-19, trên `aa413ee..HEAD`. Không có Critical/High/Required; kết luận "ship". Medium/Low đã xử lý:

- [x] Quy tắc "không lỗi" (mã thoát) chuyển vào `BatchOutcome.NoFailures`, có test; CLI dùng chung
- [x] Cả lô bị bỏ qua: nói lý do chung, hoặc chỉ tới log (và bấm thông báo mở log) khi lý do khác nhau
- [x] `FileResult` bị bỏ qua không bao giờ tính là thành công; `OperationSkippedException()` có thông báo mặc định
- [x] Hợp đồng bỏ qua: "đầu vào đã đáp ứng yêu cầu"; yêu cầu không làm được là lỗi; ném trực tiếp, trước khi ghi output (doc + SPEC-core)
- [x] SPEC-core: trạng thái `skipped` trong log, cách thông báo, ý nghĩa mã thoát
- [x] Chẩn đoán của test gom lô qua junction chạy được cả khi assert trước đó hỏng
- [x] TIFF nhiều trang trong Compress chuyển từ "skipped" sang lỗi (`c7d5b9c`)

# Phase 10: Review vòng 9 (core)

1 reviewer, 3 góc, trên `9e10ae1..HEAD`. Không Critical/High/Required; "ship" sau khi sửa 2 Medium:

- [x] Lô bị hủy có file lỗi mất đường dẫn log và bấm-mở-log (hồi quy do `ShowsLog`): nay `Cancelled after X of Y files, N failed. Details: …`
- [x] SPEC-core: mã thoát chỉ do tiến trình chủ lô báo; tiến trình giao file thoát 0; file bị hủy ghi `failed`/`cancelled`; khi nào bấm mở log
- [x] Low: lý do bỏ qua rỗng → "Nothing to gain."; lô rỗng không thành "0 files skipped"; chẩn đoán test junction bắt cả timeout
- [x] Checkpoint core: không còn Critical/High/Required sau vòng 9

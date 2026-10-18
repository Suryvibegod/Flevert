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

## Task 5: Gom lô

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

## Task 6: Thông báo cho cả lô

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

## Task 7: Đường dẫn dài và Unicode

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

## Task 8: Khởi động nhanh và log

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

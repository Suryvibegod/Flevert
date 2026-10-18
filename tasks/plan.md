# Implementation Plan: core

Spec: [SPEC-core.md](../SPEC-core.md). Danh sách việc: [todo.md](todo.md).

## Overview

Dựng `ca.exe` và thư viện `ConvertAnything.Core`: thao tác cắm được (`IOperation`), đặt tên output an toàn, menu chuột phải trong HKCU, gom nhiều file thành một lô xử lý bởi một tiến trình, thông báo một lần cho cả lô, log lỗi. Kiểm chứng toàn bộ bằng thao tác thử `dev.copy` trước khi có module `image`.

## Architecture Decisions

- **Lát dọc trước, ngang sau.** Task 3 đã chạy được `ca.exe run dev.copy <file>` từ đầu đến cuối (một file, chưa gom lô). Các task sau thêm từng lớp: menu → gom lô → thông báo.
- **Phần rủi ro nhất làm sớm.** Menu registry (Task 4) và gom lô nhiều tiến trình (Task 5) là chỗ dễ sai nhất, đặt ở Phase 2 ngay sau khung tối thiểu.
- **Registry bọc sau interface `IRegistry`.** Unit test dùng bản giả; integration test ghi vào key riêng `ConvertAnything.Test` rồi xóa.
- **Đồng hồ bọc sau `TimeProvider`** (.NET có sẵn) để test cửa sổ chờ 400 ms không phải sleep thật.
- **Không NativeAOT.** Cần VS C++ Build Tools và WinForms không hỗ trợ AOT. Dùng self-contained + ReadyToRun; WinForms chỉ nạp khi hiện UI để `run` khởi động nhanh.

## Task List

### Phase 1: Foundation
- [x] Task 1: Khung solution, license, gitignore, CI build cục bộ, repo GitHub private
- [ ] Task 2: `OutputPath`: tên không đè + ghi file tạm rồi đổi tên
- [ ] Task 3: Lát dọc đầu tiên: `ca.exe run dev.copy <file>` + log

### Checkpoint: Foundation
- [ ] `dotnet test` xanh, `dotnet format --verify-no-changes` sạch
- [ ] Chạy `ca.exe run dev.copy "D:\...\Ảnh Đà Lạt\a b.txt"` ra file copy, có dòng log
- [ ] Mày xem lại trước khi qua Phase 2

### Phase 2: Explorer integration
- [ ] Task 4: `install-menu --dev` / `uninstall-menu` với `ExtendedSubCommandsKey` + `Player`
- [ ] Task 5: Gom lô: mutex + named pipe, cửa sổ chờ 400 ms, lỗi từng file không chặn lô
- [ ] Task 6: Thông báo một lần cho cả lô + cửa sổ tiến độ có Cancel

### Checkpoint: Explorer integration
- [ ] Success criteria 1–6 trong spec đạt khi bấm thật trong Explorer
- [ ] Mày bấm thử trên máy và duyệt

### Phase 3: Hardening
- [ ] Task 7: Đường dẫn dài > 260 ký tự + Unicode
- [ ] Task 8: Đo khởi động lạnh (publish ReadyToRun), xoay vòng log 7 ngày
- [ ] Task 9: Review (chất lượng, đơn giản hóa, bảo mật), README tiếng Anh, ADR registry

### Checkpoint: Complete
- [ ] Đủ 9 success criteria trong spec
- [ ] Mày duyệt, rồi push lên GitHub

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Khởi động JIT chậm hơn AOT. | Med | ReadyToRun; không nạp WinForms trên đường chạy của tiến trình phụ (chỉ gửi path qua pipe rồi thoát). Task 8 đo. |
| Explorer bật 100 tiến trình gần như cùng lúc; hai tiến trình cùng tưởng mình là "chủ lô". | High | Named mutex quyết định chủ; tiến trình thua kết nối pipe với retry; integration test bắn 50 tiến trình song song. |
| Chọn > 100 file thì menu biến mất (giới hạn Windows cho verb dòng lệnh). | Med | Ghi vào README. Verb COM để phiên bản sau. |
| Focus Assist / Do Not Disturb ẩn thông báo. | Low | Kết quả vẫn ghi log; không phụ thuộc thông báo để biết xong. |
| Exe chưa ký số bị SmartScreen / antivirus cảnh báo. | Med | Để module `installer` xử lý (hướng dẫn, cân nhắc ký số miễn phí cho open source như SignPath). |

## Decisions

- Commit bằng `Suryvibegod <79949229+Suryvibegod@users.noreply.github.com>`, repo `old-account/ConvertAnything` (private).

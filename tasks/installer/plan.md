# Implementation Plan: installer (v0.1) + phát hành cộng đồng

Spec: [SPEC-installer.md](../../SPEC-installer.md). Danh sách việc: [todo.md](todo.md).

## Overview

Đóng gói `ca.exe` self-contained thành `ConvertAnything-Setup-<ver>.exe` bằng Inno Setup (miễn phí), cài per-user không admin, đăng ký menu, tùy chọn bật menu cổ điển trên Windows 11, gỡ sạch. Sau đó chuẩn bị repo cho cộng đồng (người dùng chốt 2026-10-19: repo public cho cộng đồng, tự push + public sau khi đạt quy trình, làm repo "popular nhất có thể", đổi tên nếu đáng).

## Architecture Decisions

- **Logic registry ở C#, installer chỉ gọi `ca.exe`**: test được bằng `FakeRegistry`; Pascal chỉ là lưới an toàn khi gỡ (luôn chạy, idempotent).
- **Lát dọc đầu tiên: installer tối thiểu cài → `install-menu` → gỡ**, có `test-installer.ps1` kiểm đầu-cuối ngay từ đầu; rồi mới thêm menu cổ điển, notices, icon.
- **Version một nơi** (`Directory.Build.props`), ISCC đọc từ file version của `ca.exe`.
- **Không ký mã ở v0.1** (ADR-0005); workflow release trên GitHub Actions dựng sẵn để sau này cắm SignPath.

## Task List

### Phase 1: Lát dọc installer
- [ ] Task 1: Version 0.1.0 + `ClassicMenu` + lệnh `classic-menu on|off` (TDD với `FakeRegistry`)
- [ ] Task 2: `test-installer.ps1` (đỏ) → `build-installer.ps1` + `ConvertAnything.iss` (cài, install-menu, gỡ, lưới an toàn) → xanh
- [ ] Task 3: Notices bên thứ ba, LICENSE trong bộ cài, icon app

### Checkpoint: Installer
- [ ] Đủ 7 success criteria trong SPEC-installer, chạy thật trên máy này
- [ ] Review 3 góc (code, bảo mật, doubt) → sửa → không còn Critical/High/Required
- [ ] ADR-0005, README (cài đặt, SmartScreen/Smart App Control), HANDOFF

### Phase 2: Phát hành cộng đồng
- [ ] Task 4: CI (build + test + format trên mỗi push/PR) và workflow release (tag `v*` → build installer → đính kèm release)
- [ ] Task 5: Hồ sơ cộng đồng: CONTRIBUTING, CODE_OF_CONDUCT, SECURITY, mẫu issue/PR, README có ảnh demo
- [ ] Task 6: Quyết định tên repo; push; đổi repo sang public; description, topics, homepage; release v0.1.0 kèm installer; soạn bài giới thiệu

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Smart App Control chặn `ca.exe` chưa ký ngay trên máy dev | Không kiểm được đầu-cuối | Installer báo lỗi riêng cho mã 4551; nếu bị chặn khi test thì ghi lại và thử bản build khác hash; README hướng dẫn |
| `install-menu` từ chối thư mục cài | Cài xong không có menu | Thư mục cố định `%LOCALAPPDATA%\Programs`, đã kiểm ACL chuỗi cha; test đầu-cuối bắt |
| Gỡ cài để lại menu trỏ tới exe đã xóa | Menu chết, khó dọn tay | Lưới an toàn Pascal + kịch bản test đổi tên `ca.exe` |
| Test đầu-cuối đè bản cài thật của người dùng | Mất cài đặt | Script từ chối chạy khi đã có mục gỡ cài đặt của app (trừ `-Force`) |
| Thiếu notices khi phân phối lại .NET/Magick | Vi phạm giấy phép | Task 3 gom Notice.txt và ThirdPartyNotices của .NET |

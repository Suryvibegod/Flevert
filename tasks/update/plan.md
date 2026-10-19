# Plan: báo bản mới

Spec: [docs/specs/core.md](../../docs/specs/core.md), mục "Báo bản mới". Quyết định: [ADR-0006](../../docs/adr/0006-update-check-against-github-releases.md). Người dùng yêu cầu (2026-10-20): bản v0.1.0 phải mời người dùng cập nhật khi GitHub có bản mới; nên phải có trong v0.1.0.

## Thiết kế

- `Flevert.Core/Updates/ReleaseVersion.cs`: `TryParseTag` (chặt, ASCII), so sánh, `Current` từ assembly.
- `Flevert.Core/Updates/UpdateStateStore.cs`: đọc/ghi `state.json` (hỏng → mặc định; ghi file tạm rồi thay).
- `Flevert.Core/Updates/UpdateChecker.cs`: nhận `HttpMessageHandler`, `TimeProvider`, store, phiên bản hiện tại; `CheckAsync` trả bản mới cần hỏi hoặc null; `Skip(version)`; `SetEnabled(bool)`.
- `CommandLine`: `update-check on|off`.
- `AppData.UpdateFolder` + `[UninstallDelete]` trong `.iss`.
- `Flevert.Cli/BatchUi`: chạy kiểm song song lô; sau thông báo kết quả hiện `TaskDialog` (Download / Later / Skip this version); tiến trình không thoát giữa chừng hộp thoại; kết quả muộn thì bỏ.
- Installer: trang "New versions" với ô chọn, `/UPDATECHECK`, nhớ lựa chọn; ssPostInstall chạy `update-check on|off`.
- README (Privacy), CHANGELOG, RELEASING (bản rút không được mời).

## Thứ tự (lát mỏng, mỗi lát test trước)

1. `ReleaseVersion` (parse/so sánh) → test đỏ → xanh.
2. `UpdateStateStore` → test.
3. `UpdateChecker` với handler giả: throttle, tắt, lỗi HTTP, body lớn, JSON hỏng, skip, request đúng URL/header.
4. Lệnh `update-check on|off` + `AppData` + uninstall (`InstallerScriptTests` đỏ trước).
5. UI hộp thoại + nối vào `BatchUi`; chạy thật với server giả (biến môi trường chỉ cho test? không: dùng `FLEVERT_UPDATE_URL` chỉ chấp nhận `http://127.0.0.1`), chụp ảnh hộp thoại.
6. Installer: ô chọn + `/UPDATECHECK`; `test-installer.ps1` thêm kịch bản.
7. Tài liệu; 3 review; reconcile.

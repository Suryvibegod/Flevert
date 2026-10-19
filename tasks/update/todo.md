# Todo: báo bản mới

Plan: [plan.md](plan.md). Spec: core.md "Báo bản mới". ADR-0006.

- [x] `ReleaseVersion`: test (12 tag sai dạng, so sánh số, phiên bản build) đỏ → xanh
- [x] `UpdateStateStore`: thiếu/hỏng → mặc định; ghi file tạm rồi thay
- [x] `UpdateChecker` với handler giả: request đúng URL/UA không cookie, 1 lần/24 giờ kể cả khi lỗi, giờ ở tương lai, tắt = không request, skip, 404/403/JSON sai, > 1 MB, mất mạng, timeout, không ghi được trạng thái, endpoint test chỉ loopback IP (20 test đỏ trước)
- [x] Lệnh `update-check on|off`; thư mục `update` trong `AppData` và `[UninstallDelete]` (test installer đỏ trước)
- [x] Hộp thoại sau lô (TaskDialog: Download / Later / Skip this version)
- [x] Installer: trang "New versions", `/UPDATECHECK`, nhớ lựa chọn; test: mọi lệnh setup gọi đều hợp lệ, áp cả on và off
- [x] Tài liệu: README Privacy, CHANGELOG, RELEASING, spec env

## Kiểm chạy thật (2026-10-20)

- GitHub giả trên 127.0.0.1 trả `v0.2.0`: sau lô `image.to-jpg` có giao diện, hộp thoại hiện, top-most, vẫn mở sau 8 giây (quá thời gian thông báo 6 giây); request chỉ có `User-Agent: Flevert/0.1.0`, không cookie.
- Later → thoát 0, `lastCheck` ghi lại; chạy lại ngay: không request, không hỏi. Skip this version → `skipped: 0.2.0`.
- `update-check off` → lô sau không request; `on` bật lại.
- Cổng đóng (mất mạng) và GitHub thật (chưa có release: 404): không hỏi, thoát 0 trong thời gian như cũ (7,7 giây gồm 6 giây thông báo).
- `test-installer.ps1`: PASS cả 9 kịch bản, thêm: mặc định bật, `/UPDATECHECK=0` tắt, cài lại không tham số giữ tắt, `/UPDATECHECK=1` bật lại. Ảnh trang "New versions" của setup đã xem.

## Lỗi tìm thấy khi chạy thật

| Lỗi | Sửa |
|---|---|
| Hộp thoại mở sau cửa sổ đang dùng, dễ không thấy | `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` khi tạo |
| Cửa sổ chủ top-most ẩn: hộp thoại đóng ngay | Bỏ cửa sổ chủ |
| Hộp thoại nhận phím người dùng đang gõ ở cửa sổ khác: Enter/Space bấm Download, mở trình duyệt | Nút mặc định là **Later** (vô hại, mai hỏi lại) |

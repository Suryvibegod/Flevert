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

## Review bối cảnh trống (commit 9744720) và reconcile

| Finding | Nguồn | Mức | Xử lý |
|---|---|---|---|
| File trạng thái không đọc được coi như "bật", rồi ghi đè lựa chọn tắt | code (Required), security (M1) | Required | Thiếu/nội dung hỏng: mặc định (bật, vì yêu cầu của chủ repo là luôn được báo; file đã flush trước khi thay nên hỏng không do Flevert). Đang bị khóa: lần này không kiểm, không ghi; Skip cũng không ghi. Test: file bị giữ không request không ghi (đỏ trước), Skip không ghi đè |
| Cài đè làm bật lại kiểm tra đã tắt bằng lệnh; cài im lặng bật mà không báo | code (Required), security (M2), doubt (Required) | Required | Setup đọc `state.json` (dấu `"enabled":false`, test giữ dấu này khớp serializer) trước lựa chọn lần cài trước; test-installer: tắt bằng lệnh rồi cài đè vẫn tắt. Kịch bản này viết sau khi sửa: lỗi ở script cũ do reviewer chỉ ra bằng đọc code |
| Hướng dẫn gỡ tay thiếu thư mục `update` | doubt | Required | Sửa ở commit README; test đỏ trước |
| Không có test nối từ `Program` tới bộ kiểm | doubt | Required | `UpdateCheckIntegrationTests`: flevert.exe thật có UI, GitHub giả trên loopback; đối chứng âm: bỏ dòng nối → test đỏ |
| `update-check off` thất bại thì im lặng | security L1, code | Low/Suggestion | Setup báo lỗi kèm lệnh tự tắt |
| Hộp thoại không trả lời giữ tiến trình (và AppMutex) mãi | security L3, code, doubt | Low/Suggestion | Tự đóng sau 10 phút (= Later); thông báo AppMutex của setup nhắc tới câu hỏi |
| Thông báo đóng sớm làm mất kết quả kiểm (mất một ngày) | code, doubt | Suggestion | Chờ kết quả, tối đa thêm 10 giây sau thời gian thông báo |
| `/UPDATECHECK` giá trị lạ bật kiểm tra | code | Suggestion | Chỉ nhận 0/1; giá trị khác bị bỏ qua, ghi log; test-installer kiểm |
| Đổi tên/xóa tài khoản: người khác lấy tên và phát hành "bản mới" | security L2 | Low | ADR-0006 và RELEASING: không bao giờ đổi tên/xóa; "Set as latest" |
| Lọc exception hẹp hơn doc | code | Suggestion | Bắt mọi lỗi (CA1031), quan sát `Exception` của task |
| Dựng `SocketsHttpHandler` trên luồng UI | code | Suggestion | `UpdateOffer.Start()` dựng trong `Task.Run` |
| Endpoint test cho redirect | security I1 | Info | `AllowAutoRedirect = false` khi dùng endpoint test |
| Test phiên bản chép lại code; không ghim timeout 5 s; thiếu test stream > 1 MB, lưu giờ trước request | doubt, code | Suggestion | So với `Directory.Build.props`; test `DefaultTimeout`; thêm hai test |
| Văn bản: "offline", Accept header, "within a day", thời điểm bị lộ | doubt, code, security I3 | Nit | Sửa README, ADR, RELEASING, capability-map, trang setup |
| Hai leader cùng kiểm cùng lúc | security I2, code | Info | Chấp nhận (hiếm, chỉ là hỏi hai lần) |
| Trang setup trên Windows 10 chưa xem | code | Suggestion | Chưa có máy Windows 10; logic trang dùng chung, test-installer chạy đường im lặng |
| Người không convert gì thì không được hỏi | doubt | Suggestion (sản phẩm) | Theo spec: chỉ kiểm sau khi dùng, không có tiến trình nền |

## Review lại (commit 266cdc8)

Không còn Critical/High/Required trong code. Hai cổng quy trình: CI chạy test tích hợp có UI lần đầu khi push (CI 3 lần trước khi tag); `test-installer.ps1` với kịch bản mới đã chạy sau khi sửa: **PASS** (2026-10-20: tắt bằng lệnh rồi cài đè vẫn tắt; `/UPDATECHECK=yes` không đổi gì), chạy lại trên bản nháp theo RELEASING.

| Góp ý | Xử lý |
|---|---|
| S1: kết quả kiểm có thể mất vì race giữa task xong và continuation | Cờ `_checkAnswered` do continuation đặt thay cho `IsCompleted` |
| S2: setup không đọc được file rồi `update-check on` bật lại "off" | `SetEnabled` từ chối (IOException) khi file không đọc được; test đỏ trước |
| S3: test tích hợp không giết tiến trình con khi hết giờ | Giết trong finally |
| S4: thư mục/quyền tại đường dẫn trạng thái = tắt, chưa có test | Thêm test; spec ghi rõ |
| S5: endpoint test đi qua proxy hệ thống | `UseProxy = !test` |
| Nit: file sửa tay có dấu cách/BOM; rename chưa flush; icon khay còn tới 10 s | Chấp nhận (chỉ Flevert ghi file; không ảnh hưởng an toàn) |

# Sửa các lỗi chặn PR #5

## Phạm vi và tiêu chí chấp nhận

Yêu cầu của maintainer ngày 2026-10-23: sửa các lỗi đã xác nhận khi review PR #5 của long89kev, giữ lịch sử và ghi nhận contributor. Không trộn phần OCR đang làm trên nhánh khác.

- Windows nhả ghim đầu vào khi tác vụ bị bỏ sau thời gian hủy 2 s theo core spec hiện tại; Linux tiếp tục giữ descriptor đến khi thao tác ngừng đọc.
- `install-menu` trên Linux kiểm chủ sở hữu, quyền ghi và liên kết của toàn bộ cây ứng dụng, ngoài thư mục cha. File thường có nhiều hard link cũng bị từ chối.
- Bộ cài không sửa/xóa qua symlink hay đường dẫn cha không đáng tin. Không thay ứng dụng có sẵn nếu chưa có bằng chứng nó được Flevert cài. Manifest riêng ghi các file đã cài; gỡ chỉ xóa các file đó và dữ liệu runtime đã biết, giữ file lạ. Cập nhật từ chối nếu ứng dụng có file lạ. Không thêm dependency ngoài Python chuẩn vốn có cùng nautilus-python.
- Build và format trên Windows/Linux đạt; bốn suite Windows chạy lần lượt; ba suite Linux, Python và package smoke đạt trên Ubuntu CI.

## Kế hoạch

1. [ ] Tái hiện lỗi Windows bằng test có sẵn; sửa vòng đời pin, bổ sung test join Linux.
2. [ ] Viết test quyền file/thư mục con, symlink và hard link; sửa kiểm vị trí cài.
3. [ ] Viết test cài/gỡ với symlink, ứng dụng chưa được quản lý và file lạ; dùng manifest để giới hạn việc thay/xóa. Giữ shell entry point, phần xử lý file dùng Python chuẩn.
4. [x] Sửa lỗi format và cấu hình project gây lỗi khi formatter nạp solution trên Windows.
5. [ ] Kiểm runtime, ba review độc lập chỉ đọc (code/security/doubt), reconcile; không còn Critical/High/Required mới xét merge.
6. [ ] Cập nhật handoff/README/credit; giữ các commit của long89kev, không squash làm mất tác giả.

## Bằng chứng

- Review trước: Windows build đạt; Core 533 đạt / 1 lỗi, Image 245 đạt, PDF 1375 đạt, Media 225 đạt. Test hủy join chạy riêng vẫn lỗi. Format exit 2.
- Máy Windows hiện tại không hỗ trợ WSL2; test gọi libc và bộ cài GNOME sẽ được kiểm bằng Ubuntu CI. Bằng chứng RED/GREEN Linux phải thu trên Ubuntu, không coi test bị skip trên Windows là đạt.
- Windows RED: test hủy join có sẵn thất bại với file vẫn bị khóa. Sau sửa, test chạy riêng GREEN. Build/format mới đạt; Core 534 đạt, Image 245 đạt; PDF/Media đang chạy lần lượt.
- Lượt Windows đầy đủ sau sửa: Core 534 đạt/96 skip theo nền tảng, Image 245 đạt/5 skip, PDF 1375 đạt/27 skip, Media 225 đạt. Build 0 warning/error; format exit 0. Native Linux và Python integration vẫn cần Ubuntu CI.
- CI có chế độ manual `regression_baseline`: thay riêng menu/bộ cài bằng mã trước sửa, yêu cầu test mới thật sự thất bại, khôi phục mã sửa rồi chạy các suite và package smoke bình thường. Không dùng lỗi compile thay cho RED.

## Reconcile review độc lập của bản sửa

| Finding | Review | Quyết định và xử lý |
|---|---|---|
| Gỡ chưa kiểm ancestor của extension | code/security/doubt | Required, đã sửa: kiểm hierarchy trước gọi executable; test symlink vào dữ liệu ngoài phải từ chối trước khi chạy/xóa. |
| Bỏ umask riêng tư làm thư mục extension mới có thể ghi bởi group | code | Required, đã sửa: đặt umask 077 trước mọi tiến trình con; test khởi chạy từ umask 0002. |
| Đọc manifest trước lấy khóa | doubt | Required, đã sửa: đọc/kiểm trạng thái dưới khóa inode thư mục; test hai tiến trình với barrier. |
| File khóa tồn tại sau tiến trình bị kill | doubt | Required, đã sửa: dùng flock trên descriptor thư mục, hệ điều hành tự nhả khi chết; test kill rồi thử cài lại. |
| Rollback có thể xóa backup sau khi đã trả app nhưng chưa trả metadata | re-review gộp | Required, đã sửa: lưu snapshot metadata trước publish, chỉ dọn sau khi toàn bộ rollback đạt; test ENOSPC lúc phục hồi manifest. |

Re-review gộp cuối sau sửa snapshot/rollback: không còn Critical/High/Required; còn gate runtime Ubuntu CI.

Giới hạn đã đối chiếu từ review PR ban đầu: Linux hỗ trợ phiên GNOME; chạy qua SSH/không có desktop dùng `FLEVERT_HEADLESS=1`. Nautilus vẫn dùng argv, nên lô có tổng độ dài đường dẫn vượt giới hạn hệ điều hành phải chia nhỏ. Hai giới hạn không thay đổi trong đợt sửa lỗi an toàn này và được ghi ADR.

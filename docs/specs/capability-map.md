# Capability Map: Flevert

Tool Windows miễn phí: chuột phải vào file ảnh / video-audio / PDF để convert, nén, xử lý ngay tại chỗ. Xử lý hoàn toàn trên máy, không gửi file đi đâu; mạng chỉ dùng để kiểm phiên bản mới (tắt được, [ADR-0006](../adr/0006-update-check-against-github-releases.md)). File kết quả nằm cạnh file gốc.

| Module id | Trách nhiệm | Phụ thuộc |
|-----------|-------------|-----------|
| core | `flevert.exe <thao-tác> <files...>`; ghi/gỡ menu chuột phải (registry, lọc theo loại file); gom nhiều file chọn cùng lúc thành 1 lô; đặt tên output không đè (`x (1).jpg`); thông báo khi xong/lỗi; log lỗi | — |
| image | Convert JPG/PNG/WEBP/HEIC/AVIF/GIF/BMP/TIFF qua lại; nén 3 mức hoặc theo dung lượng mục tiêu; xóa metadata (EXIF, GPS) | core |
| installer | Bộ cài 1 click, không cần admin; tùy chọn bật menu chuột phải kiểu cũ (cho mọi app, theo từng tài khoản); gỡ sạch registry; kèm sẵn ffmpeg/pdfium khi có module cần | core |
| media | Nén video theo mức hoặc dung lượng mục tiêu (preset "vừa gửi Zalo/Messenger"); convert MP4/MOV/MKV/WEBM/AVI/WMV; tách MP3 từ video; convert audio MP3/WAV/M4A/FLAC/OGG/Opus | core |
| pdf | PDF sang ảnh; ảnh sang PDF (chọn nhiều ảnh bất kỳ); ghép/tách PDF; xoay/đảo trang; nén nhẹ; xóa metadata; khóa/mở khóa; rút ảnh/text; sửa lỗi | core, image |

Thứ tự làm:

```
core → image → installer   ⇒ v0.1 (chỉ có ảnh)
     → pdf                 ⇒ v0.2
     → media               ⇒ v0.3
```

Module id chốt một lần, không đổi. Mỗi module có spec riêng: `docs/specs/<module-id>.md`.

## Ngoài phạm vi

Menu chuột phải mới của Windows 11 (MSIX + ký số), PDF→DOCX, editor có giao diện (crop / thêm nền / ghi chú / che thông tin), macOS/Linux, xử lý trên cloud, tự cập nhật.

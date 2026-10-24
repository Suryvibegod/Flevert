# Capability Map: Flevert

Tool Windows miễn phí: chuột phải vào file ảnh / video-audio / PDF để convert, nén, xử lý ngay tại chỗ. Xử lý hoàn toàn trên máy, không gửi file đi đâu; mạng chỉ dùng để kiểm phiên bản mới (tắt được, [ADR-0006](../adr/0006-update-check-against-github-releases.md)). File kết quả nằm cạnh file gốc.

| Module id | Trách nhiệm | Phụ thuộc |
|-----------|-------------|-----------|
| core | `flevert.exe <thao-tác> <files...>`; ghi/gỡ menu chuột phải (registry, lọc theo loại file); gom nhiều file chọn cùng lúc thành 1 lô; đặt tên output không đè (`x (1).jpg`); thông báo khi xong/lỗi; log lỗi; báo bản mới (v0.1); thao tác N→1 và 1→N, tiến độ trong file, gọi công cụ ngoài (v0.2) | — |
| image | Convert JPG/PNG/WEBP/HEIC/AVIF/GIF/BMP/TIFF qua lại; nén 3 mức hoặc theo dung lượng mục tiêu; xóa metadata (EXIF, GPS) | core |
| installer | Bộ cài 1 click, không cần admin; tùy chọn bật menu chuột phải kiểu cũ (cho mọi app, theo từng tài khoản); gỡ sạch registry; kèm sẵn ffmpeg/pdfium khi có module cần | core |
| media | Nén video theo mức hoặc dưới 10/25/100 MB; convert MP4/MOV/MKV/WEBM/GIF; tách MP3/M4A từ video; convert audio MP3/M4A/WAV/FLAC/OGG/Opus ([spec](media.md)) | core |
| linux | Bản Linux: menu chuột phải của Nautilus, cửa sổ tiến độ/hộp thoại bằng zenity, phần nền tảng của core và pdf cho Linux, cài per-user từ tarball ([spec](linux.md), đề xuất của người đóng góp, chờ maintainer duyệt) | core, image, pdf |
| pdf | Nhiều ảnh thành một PDF; PDF sang ảnh; ghép/tách; xoay; nén; xóa metadata; đặt/gỡ mật khẩu; (nếu kịp) rút ảnh/text, đảo trang, sửa lỗi ([spec](pdf.md)) | core, image |
| ocr | Nhận chữ tiếng Việt/Anh bằng Tesseract (tiến trình riêng): PDF scan thành PDF tìm được chữ, PDF scan và ảnh ra .txt ([spec](ocr.md)) | core; pdf và image dùng nó |

Thứ tự làm:

```
core → image → installer   ⇒ v0.1 (ảnh, bộ cài, báo bản mới)
     → pdf                 ⇒ v0.2 (kèm thay đổi core: N→1, 1→N, công cụ ngoài)
     → media               ⇒ v1.3
     → ocr                 ⇒ v1.4 (PDF scan thành PDF tìm và chép được chữ, ảnh ra chữ, tiếng Việt; [spec](ocr.md))
```

Module id chốt một lần, không đổi. Mỗi module có spec riêng: `docs/specs/<module-id>.md`.

## Ngoài phạm vi

Menu chuột phải mới của Windows 11 (MSIX + ký số), PDF→DOCX, editor có giao diện (crop / thêm nền / ghi chú / che thông tin), macOS, Linux ngoài Nautilus (xem [linux.md](linux.md)), xử lý trên cloud, tự tải và cài bản mới (chỉ báo và mở trang tải, ADR-0006).

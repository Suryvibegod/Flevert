# Capability Map: ConvertAnything

Tool Windows miễn phí: chuột phải vào file ảnh / video-audio / PDF để convert, nén, xử lý ngay tại chỗ. Chạy offline 100%, file kết quả nằm cạnh file gốc.

| Module id | Trách nhiệm | Phụ thuộc |
|-----------|-------------|-----------|
| core | `ca.exe <thao-tác> <files...>`; ghi/gỡ menu chuột phải (registry, lọc theo loại file); gom nhiều file chọn cùng lúc thành 1 lô; đặt tên output không đè (`x (1).jpg`); thông báo khi xong/lỗi; log lỗi | — |
| image | Convert JPG/PNG/WEBP/HEIC/BMP/TIFF qua lại; nén 3 mức; xóa metadata (EXIF, GPS) | core |
| installer | Bộ cài 1 click; tùy chọn bật menu cũ cho toàn máy; gỡ sạch registry; kèm sẵn ffmpeg/pdfium | core |
| media | Nén video (preset "vừa gửi Zalo/Messenger"); convert MP4/MOV/MKV/WEBM; tách MP3 từ video; convert audio MP3/WAV/M4A/FLAC | core |
| pdf | PDF sang ảnh; ảnh sang PDF; ghép PDF; tách PDF; nén PDF nhẹ | core, image |

Thứ tự làm:

```
core → image → installer   ⇒ v0.1 (chỉ có ảnh)
     → media               ⇒ v0.2
     → pdf                 ⇒ v0.3
```

Module id chốt một lần, không đổi. Mỗi module có spec riêng: `SPEC-<module-id>.md`.

## Ngoài phạm vi

Menu chuột phải mới của Windows 11 (MSIX + ký số), PDF→DOCX, editor có giao diện (crop / thêm nền / ghi chú / che thông tin), macOS/Linux, xử lý trên cloud, tự cập nhật.

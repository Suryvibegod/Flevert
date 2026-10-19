# Spec: pdf

Module `pdf` trong [docs/specs/capability-map.md](capability-map.md) (**phiên bản v0.2**). Phụ thuộc `core`, `image`.

## Objective

Chuột phải file ảnh hoặc PDF trong Windows Explorer → `Flevert` → xử lý tức thì, 100% offline:

- **Ảnh → PDF (`pdf.from-images`)**: Chọn **bất kỳ ảnh nào**, **bất kỳ số lượng** (1 ảnh, 10 ảnh hay 100 ảnh JPG, PNG, WEBP, HEIC...) → đóng gói thành **một file PDF duy nhất** theo thứ tự tên file (A-Z / số thứ tự).
- **PDF → Ảnh (`pdf.to-jpg`, `pdf.to-png`)**: Render từng trang PDF ra file ảnh riêng biệt (150 DPI gửi chat / 300 DPI in ấn).
- **Ghép & Tách PDF**: Ghép nhiều file PDF thành một (`pdf.merge`); tách một file PDF thành từng trang độc lập (`pdf.split`).
- **Thao tác trang**: Xoay trang nhanh (`pdf.rotate`), đảo ngược thứ tự trang scan (`pdf.reverse-pages`), tự động lọc bỏ trang trắng sau khi scan (`pdf.remove-blank-pages`).
- **Bảo mật & Tẩy sạch metadata**: Xóa sạch tác giả, phần mềm tạo, metadata XMP/chú thích (`pdf.sanitize`), khóa mật khẩu AES-256 (`pdf.protect`), gỡ mật khẩu (`pdf.unlock`).
- **Trích xuất nội dung**: Rút toàn bộ ảnh gốc trong PDF không bị nén vỡ (`pdf.extract-images`), trích xuất văn bản sang file `.txt` (`pdf.extract-text`).
- **Nén & Sửa lỗi**: Nén lossless cấu trúc PDF (`pdf.compress`), nén tối ưu dung lượng ảnh scan (`pdf.compress-smart`), sửa lỗi bảng xref file PDF hỏng (`pdf.repair`).

## Lựa chọn thư viện (đã kiểm chứng giấy phép, 100% sạch)

| Nhu cầu | Lựa chọn | Giấy phép | Ghi chú |
|---|---|---|---|
| Render trang PDF → ảnh, trích xuất text, phát hiện trang trắng | pdfium qua `Docnet.Core` 2.6.0 (hoặc `PDFiumCore` 4688.0.0) | MIT / Apache-2.0 (pdfium: BSD-3 / Apache-2.0) | Đọc siêu nhanh, native từ Chromium |
| Ghép, tách, xoay, khóa/mở khóa, sửa lỗi, nén stream | `qpdf.exe` (tiến trình riêng, phiên bản cố định + SHA256) | Apache-2.0 | Tải vào `deps/`, gọi CLI qua `ProcessStartInfo` |
| Ảnh → PDF, nén ảnh nhúng | Magick.NET (đã có ở module `image`) | Apache-2.0 | Ghi PDF đa trang trực tiếp không cần Ghostscript |

**Tuyệt đối loại trừ (Vi phạm nguyên tắc bản quyền của repo):**
- **PDFsharp 6.5.0**: Giấy phép **AGPL-3.0-only**, không hợp với repo MIT.
- **Ghostscript**: Giấy phép **AGPL**, không phân phối kèm.
- **iText / iText7**: Giấy phép **AGPL**.
- Magick.NET **đọc** PDF cần Ghostscript, do đó **không dùng Magick.NET để đọc/render PDF** mà dùng `pdfium`.

## Thao tác (Operations)

| op-id | Menu chuột phải | Áp dụng trên | Kiểu | Output |
|---|---|---|---|---|
| `pdf.from-images` | Convert to PDF | File ảnh (`.jpg .png .webp .heic...`, chọn $\ge 1$) | Batch ($N \to 1$) | Một PDF duy nhất: `tên ảnh đầu (+N).pdf` |
| `pdf.to-jpg` | Convert/JPG (pages) | `.pdf` | $1 \to N$ | Thư mục `tên (pages)\tên-001.jpg` ... |
| `pdf.to-png` | Convert/PNG (pages) | `.pdf` | $1 \to N$ | Thư mục `tên (pages)\tên-001.png` ... |
| `pdf.merge` | Merge PDFs | `.pdf` (chọn $\ge 2$) | Batch ($N \to 1$) | Một file: `tên file đầu (merged).pdf` |
| `pdf.split` | Split pages | `.pdf` | $1 \to N$ | Thư mục `tên (pages)\tên-001.pdf` ... |
| `pdf.rotate-cw` / `-ccw` / `-180` | Rotate/90° CW, 90° CCW, 180° | `.pdf` | $1 \to 1$ | `tên (rotated).pdf` (dùng QPDF, cực nhanh) |
| `pdf.reverse-pages` | Reverse page order | `.pdf` | $1 \to 1$ | `tên (reversed).pdf` |
| `pdf.remove-blank-pages` | Remove blank pages | `.pdf` | $1 \to 1$ | `tên (clean).pdf` |
| `pdf.compress` | Compress (Lossless) | `.pdf` | $1 \to 1$ | `tên (compressed).pdf` (qpdf object streams) |
| `pdf.compress-smart` | Compress (Scan / Web) | `.pdf` | $1 \to 1$ | `tên (small).pdf` (downsample ảnh scan) |
| `pdf.sanitize` | Remove metadata | `.pdf` | $1 \to 1$ | `tên (sanitized).pdf` (tẩy tác giả, XMP) |
| `pdf.protect` | Protect with password | `.pdf` | $1 \to 1$ | `tên (protected).pdf` (nhập pass qua mini-dialog) |
| `pdf.unlock` | Remove password | `.pdf` | $1 \to 1$ | `tên (unlocked).pdf` (nhập pass giải mã) |
| `pdf.extract-images` | Extract images | `.pdf` | $1 \to N$ | Thư mục `tên (images)\image-001.png` ... |
| `pdf.extract-text` | Extract text (TXT) | `.pdf` | $1 \to 1$ | `tên.txt` (trích xuất text thuần qua PDFium) |
| `pdf.repair` | Repair PDF | `.pdf` | $1 \to 1$ | `tên (repaired).pdf` (khôi phục xref hỏng) |

## Yêu cầu kiến trúc Core

1. **Hỗ trợ Batch Operation ($N \to 1$)**:
   - `IOperation` trong core cần hỗ trợ hai phân loại:
     - `ISingleFileOperation`: Đầu vào 1 file $\to$ đầu ra 1 hoặc nhiều file (VD: `pdf.split`, `pdf.to-jpg`, `pdf.rotate`).
     - `IBatchOperation`: Đầu vào danh sách $N$ files $\to$ đầu ra 1 file duy nhất (VD: `pdf.from-images`, `pdf.merge`).
2. **Quy tắc sắp xếp trang của `pdf.from-images`**:
   - Tự động sắp xếp danh sách file ảnh theo Natural Sort (thứ tự tự nhiên: `img1`, `img2`, `img10` thay vì `img1`, `img10`, `img2`).
   - Xử lý hướng ảnh EXIF: Tự động quay ảnh đúng chiều trước khi đưa vào trang PDF.
3. **Bảo vệ tài nguyên**:
   - PDFium: Giới hạn tối đa 1.000 trang / file hoặc kích thước trang tối đa 10.000 px mỗi cạnh để tránh cạn kiệt bộ nhớ.
   - qpdf: Gọi bằng đường dẫn tuyệt đối trong `deps/`, dùng `ArgumentList` tách bạch đối số (chống injection), đặt timeout tiến trình cứng.

## Tham khảo & Kế thừa từ Stirling-PDF

Dự án tham khảo kiến trúc xử lý PDF ngoại tuyến, danh mục thao tác và ánh xạ trực tiếp từ dự án mã nguồn mở **[Stirling-PDF](https://github.com/Stirling-Tools/Stirling-PDF)** (MIT License):

| Tính năng trong Flevert | Stirling-PDF Controller / Service tham chiếu | Giải pháp kỹ thuật được kế thừa |
|---|---|---|
| `pdf.from-images` | `ConvertImgPDFController.java` | Gom lô ảnh nhiều định dạng, giữ tỉ lệ gốc, ghép vào 1 file PDF đa trang |
| `pdf.to-jpg` / `pdf.to-png` | `ConvertImgPDFController.java` | Render từng trang PDF ra file ảnh độc lập (DPI 150/300) |
| `pdf.merge` | `MergeController.java` | Nối tuần tự các luồng tài liệu PDF |
| `pdf.split` | `SplitPDFController.java` | Tách từng trang ra các file PDF đơn lẻ |
| `pdf.rotate-*` | `RotationController.java` | Điều chỉnh cờ Rotate trong page dictionary (không re-encode) |
| `pdf.reverse-pages` | `RearrangePagesPDFController.java` | Đảo ngược thứ tự chỉ mục trang từ $Z \to 1$ |
| `pdf.remove-blank-pages` | `BlankPageController.java` | Thuật toán quét ma trận pixel phát hiện trang trắng/trang rác |
| `pdf.sanitize` | `SanitizeController.java` | Tẩy sạch siêu dữ liệu nhạy cảm (tác giả, phần mềm, XMP, script ngầm) |
| `pdf.protect` / `pdf.unlock` | `PasswordController.java` | Mã hóa và giải mã chuẩn AES-256 |
| `pdf.extract-images` | `ExtractImagesController.java` | Trích xuất các luồng `XObject` hình ảnh thô nguyên bản từ PDF |
| `pdf.extract-text` | `DocParseController.java` | Trích xuất toàn bộ text theo cấu trúc layout |
| `pdf.repair` | `RepairController.java` | Khôi phục và tái tạo bảng xref bị hỏng |
| `pdf.compress` / `-smart` | `CompressController.java` | Nén cấu trúc đối tượng (lossless) và downsample ảnh scan |

> **Điểm khác biệt trong hiện thực**: Stirling-PDF chạy trên nền JVM (Java/Spring Boot) với giao diện Web/Docker; Flevert kế thừa ý tưởng và thuật toán nhưng chuyển thể sang **C#/.NET 10 chạy native dưới dạng menu chuột phải Windows (CLI không GUI)**, tối ưu tốc độ thực thi dưới 200ms bằng `qpdf.exe` và `pdfium`.

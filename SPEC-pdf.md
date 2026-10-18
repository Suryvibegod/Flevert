# Spec: pdf

Module `pdf` trong [CAPABILITY-MAP.md](CAPABILITY-MAP.md). Phụ thuộc `core`, `image`. Bản nháp: chốt sau khi `media` xong.

## Objective

- PDF → ảnh từng trang (JPG / PNG, 150 hoặc 300 DPI) để gửi qua chat.
- Ảnh → PDF (chọn nhiều ảnh → một PDF, theo thứ tự tên file).
- Ghép nhiều PDF thành một; tách PDF thành từng trang.
- Nén PDF nhẹ (lossless: nén lại stream, object stream).

## Lựa chọn thư viện (đã kiểm chứng giấy phép trên NuGet, 2026-10-19)

| Nhu cầu | Lựa chọn | Giấy phép |
|---|---|---|
| Render trang PDF → ảnh | pdfium qua `Docnet.Core` 2.6.0 (hoặc `PDFiumCore` 4688.0.0) | MIT / Apache-2.0 (pdfium: BSD-3 / Apache-2.0) |
| Ghép, tách, nén lossless | `qpdf.exe` (tiến trình riêng, phiên bản cố định + SHA256) | Apache-2.0 |
| Ảnh → PDF | Magick.NET (đã có ở module `image`; ghi PDF không cần Ghostscript) | Apache-2.0 |

**Loại:**
- **PDFsharp 6.5.0**: giấy phép **AGPL-3.0-only**, không hợp với dự án MIT.
- **Ghostscript**: AGPL.
- Magick.NET **đọc** PDF cần Ghostscript, nên không dùng Magick.NET để render PDF.

## Thao tác (nháp)

| op-id | Menu | Áp dụng | Output |
|---|---|---|---|
| `pdf.to-jpg` / `pdf.to-png` | Convert/JPG (pages), PNG (pages) | `.pdf` | thư mục `tên (pages)\tên-001.jpg` ... |
| `pdf.from-images` | Convert/PDF | ảnh | một PDF cho cả lô: `tên ảnh đầu (+N).pdf` |
| `pdf.merge` | Merge PDFs | `.pdf` (chọn ≥ 2) | `tên đầu (merged).pdf` |
| `pdf.split` | Split pages | `.pdf` | thư mục `tên (pages)\tên-001.pdf` ... |
| `pdf.compress` | Compress | `.pdf` | `tên (compressed).pdf` |

`pdf.from-images` và `pdf.merge` là thao tác **trên cả lô** (một output cho nhiều input), cần mở rộng `IOperation` trong core cho kiểu "batch operation".

## Bảo mật

- pdfium chạy trong tiến trình `ca.exe`: giới hạn số trang render (1000), kích thước trang render tối đa 10 000 px mỗi cạnh.
- qpdf: đường dẫn tuyệt đối, `ArgumentList`, timeout.

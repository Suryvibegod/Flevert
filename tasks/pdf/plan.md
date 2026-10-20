# Plan: pdf (v0.2.0)

Spec: [docs/specs/pdf.md](../../docs/specs/pdf.md). Todo: [todo.md](todo.md). Lập ngày 2026-10-20 theo `planning-and-task-breakdown`.

## Tổng quan

v0.2 thêm thao tác PDF vào menu chuột phải. Làm theo lát dọc: mỗi task giao một thao tác chạy được từ menu tới file output (kèm test và đóng gói), thay vì dựng hết core rồi mới tới PDF. Thay đổi core (N→1, 1→N, công cụ ngoài, tiến độ trong file) đi cùng thao tác đầu tiên cần nó, và có ADR riêng.

## Trả lời Open Questions của spec (đã kiểm bằng bản chính thức, 2026-10-20)

1. **Compress for sharing: giữ trong v0.2**, làm ở giai đoạn cuối, có bước thử (spike) trước, bỏ được nếu thử hỏng. Cách làm: pdfium `FPDFImageObj_GetImageMetadata` (DPI hiệu dụng) → `FPDFImageObj_GetBitmap` → Magick giảm còn 150 DPI, JPEG q80 → `FPDFImageObj_LoadJpegFileInline` thay vào chính đối tượng ảnh → `FPDF_SaveAsCopy` → qpdf nén cấu trúc. Không cần thư viện AGPL.
2. **qpdf 12.4.2 msvc64**: bản dựng đã kèm OpenSSL (`--show-crypto`: openssl, native) và runtime MSVC (`msvcp140*.dll`, `vcruntime140*.dll`, `concrt140.dll`); thư mục `bin` 8,9 MB. File zip **không có giấy phép**: `fetch-deps` tải `LICENSE.txt` và `NOTICE.md` của qpdf từ mã nguồn đúng tag `v12.4.2` (ghim SHA256), cộng giấy phép OpenSSL 3 (Apache-2.0), zlib, libjpeg-turbo, MSVC redistributable.
3. **Kiểm hash `qpdf.exe`/`pdfium.dll` lúc chạy: không.** Thư mục cài đã được setup kiểm quyền (chỉ chủ và admin ghi được), cùng mức tin cậy với `flevert.exe`.

## Quyết định kiến trúc

- **Ảnh → PDF dùng API tạo PDF của pdfium** (`FPDF_CreateNewDocument`, `FPDFPage_New`, `FPDFImageObj_LoadJpegFileInline`, `FPDFImageObj_SetBitmap`, `FPDF_SaveAsCopy`) thay cho bộ ghi PDF tự viết trong spec: ít mã định dạng tự viết hơn, cùng một thư viện đã kiểm. JPEG nhúng nguyên byte sau khi **bỏ các đoạn APP1 (EXIF, XMP), APP13 (IPTC) và COM** (cắt theo marker, không giải mã lại: không lộ GPS mà vẫn không mất chất lượng); hướng EXIF thực hiện bằng ma trận xoay của trang thay vì xoay điểm ảnh. Ảnh khác: Magick giải mã; ảnh chụp (HEIC/HEIF/AVIF/WebP) → JPEG q92; đồ họa (PNG/GIF/BMP/TIFF) → `SetBitmap` (pdfium nén Flate, không mất dữ liệu). Cập nhật spec trong task 7.
- **Hai engine**: qpdf (tiến trình riêng) cho cấu trúc và mã hóa: xoay, ghép, tách, nén, xóa metadata, mật khẩu, sửa lỗi. pdfium (trong tiến trình, khóa toàn cục, `MaxParallel = 1`) cho render, ảnh → PDF, rút text/ảnh, nén ảnh. pdfium **không** mã hóa được, nên đặt mật khẩu bắt buộc qua qpdf.
- **Mật khẩu chỉ qua stdin**: đặt mật khẩu bằng `@-` (đối số đọc từ stdin: `--encrypt --user-password=… --owner-password=… --bits=256 --`); gỡ mật khẩu bằng `--password-file=-`. Đã thử: mật khẩu có dấu cách chạy được; sai mật khẩu → qpdf thoát mã 2, stderr "invalid password".
- **Deps ghim theo hash**: `scripts/deps.json` (URL, SHA256, đường dẫn cần giữ) + `scripts/fetch-deps.ps1`. GitHub công bố digest cho từng asset release; đã đối chiếu: pdfium `chromium/8100` `pdfium-win-x64.tgz` sha256 `51621bcc…6d6e2848d6d`, qpdf `qpdf-12.4.2-msvc64.zip` sha256 `db87077e…9a5de25d`. CI không cache: tải vài giây và kiểm hash mỗi lần (ADR-0008).
- **Thao tác N→1** (`IBatchOperation`): runner ghim **tất cả** file của lô (như hiện nay từng file), sắp theo `StrCmpLogicalW`, gọi thao tác một lần. **Thao tác 1→N**: `OutputPath.WriteDirectoryAtomicallyAsync` (thư mục tạm `.flevert-<guid>.partial`, đổi tên một lần). **Tiến độ trong file**: thao tác nhận `IProgress<FileProgress>` (ví dụ "page 37 of 200"), cửa sổ tiến độ hiện thêm dòng này. ADR-0007.
- **Fixture PDF**: sinh bằng `make_pdf_fixtures.py` (chỉ thư viện chuẩn Python: viết PDF tối giản bằng tay) và qpdf (mã hóa, hỏng xref); có README nguồn gốc như fixture ảnh.

## Thứ tự và phụ thuộc

```
T1 fetch-deps ──┬── T2 ExternalTool ── T3 rotate (Qpdf, project, menu .pdf) ── T4 đóng gói (installer, notices)
                │                                     │
                │                                     ├── T5 N→1 core ── T6 merge
                │                                     ├── T7 1→N + tiến độ ── T8 split
                └── T9 Pdfium (P/Invoke, render) ── T10 to-jpg/png (dùng T7)
                                                  └── T11 ảnh → PDF (dùng T5)
T3 ── T12 strip-metadata ── T13 compress lossless ── T14 protect/unlock + hộp thoại
T9 ── T15 spike compress-sharing ── T16 compress-sharing (bỏ được)
T17 hardening (PDF độc, giới hạn) ── T18 nâng cấp từ 0.1.0 ── T19 review ×3 ── T20 docs + release
```

Rủi ro cao làm sớm: T2 (tiến trình con, hủy, job object), T5 (đổi runner cho cả lô), T9 (P/Invoke pdfium, callback, thread-safety).

## Rủi ro

| Rủi ro | Mức | Cách giảm |
|---|---|---|
| P/Invoke pdfium: callback bị GC thu hồi, rò bộ nhớ, crash do PDF độc | Cao | Giữ delegate sống suốt lời gọi; `SafeHandle` cho document/page/bitmap; khóa toàn cục; fixture PDF độc; giới hạn trang/px trước khi render |
| Đổi runner cho N→1 làm hỏng gom lô hiện có | Cao | Không đổi đường đi của `IOperation`; nhánh riêng cho `IBatchOperation`; toàn bộ test core + tích hợp tiến trình chạy lại |
| Smart App Control chặn `qpdf.exe`/`qpdf30.dll` chưa ký (như `flevert.exe`) | Trung | Báo lỗi rõ (mã như "Windows blocked"), README đã nói về SAC; theo dõi đơn SignPath |
| Bộ cài to thêm (~16 MB giải nén) | Thấp | Đo ở T4; giới hạn test-installer 80 MB |
| PDF có mật khẩu mở, hoặc chữ ký số: thao tác cấu trúc làm hỏng chữ ký | Trung | Thao tác (trừ unlock) từ chối PDF có mật khẩu; PDF có chữ ký: ghi chú trong thông báo rằng chữ ký không còn hợp lệ sau khi sửa (T17) |
| `StrCmpLogicalW` khác kết quả theo locale | Thấp | Test với chữ có dấu và số; đây là thứ tự Explorer dùng |

## Phạm vi mỗi task

Mỗi task: test trước (thấy đỏ), code, chạy thật (bản build + menu khi có menu), `dotnet format`, commit nguyên tử. Review ba góc sau mỗi giai đoạn (checkpoint), không chỉ cuối cùng. Chi tiết và tiêu chí nghiệm thu: [todo.md](todo.md).

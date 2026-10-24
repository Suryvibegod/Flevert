# Spec: ocr (v1.4.0)

Module `ocr` trong [capability-map.md](capability-map.md): nhận chữ trong ảnh và PDF scan, tiếng Việt trước, hoàn toàn trên máy. Phụ thuộc `core` (`ExternalTool`, job object, ngân sách bộ nhớ). `pdf` và `image` dùng nó. Phiên bản: **v1.4.0**.

## Objective

Người dùng có PDF scan (hợp đồng, hóa đơn, giấy tờ chụp lại) không tìm và không chép được chữ, hoặc ảnh chụp màn hình cần lấy chữ ra. Hôm nay họ tải ứng dụng OCR trên mạng, gửi file lên dịch vụ lạ. Flevert làm việc đó bằng chuột phải, không gửi file đi đâu.

### User stories

1. PDF scan 10 trang tiếng Việt → giữ Shift, chuột phải → **Flevert: more PDF tools > Make searchable (OCR)** → `<tên> (searchable).pdf`: nhìn y hệt bản gốc, Ctrl+F tìm được chữ, bôi đen chép ra đúng dấu tiếng Việt.
2. PDF scan → **Convert to > Text** (menu chính) → `<tên>.txt` có chữ của các trang scan (trước v1.4.0: skipped "This PDF has no text to extract").
3. Ảnh chụp màn hình/ảnh chụp giấy (JPG, PNG, HEIC, WEBP, …) → **Convert to > Text** → `<tên>.txt`.

## Đã quyết (người dùng 2026-10-21)

- PDF: `Make searchable (OCR)` nằm ở menu Shift `Flevert: more PDF tools` (menu chính đã đủ 16 mục). `Convert to > Text` tự OCR trang không có chữ.
- Ảnh: `Convert to > Text` thay `Convert to > BMP` (ít người cần BMP làm đích; BMP vẫn là nguồn). Menu ảnh giữ 16 mục.

## Icon

- `Make searchable (OCR)` dùng `scan_text` của Fluent (Filled, tô `#2F80ED` như các icon khác; ghim SHA256 các bản 16, 20, 24, 32, 48 px trong `scripts/make-menu-icons.py`): khung quét bao quanh dòng chữ, nghĩa là "tìm chữ trong trang scan". Trước đó mục này dùng chung `draw_text` với `Add watermark…`.
- Đã xét (cùng commit Fluent đã ghim, so trên menu sáng và tối ở 16–32 px):
  - `scan_type` (chữ T trong khung quét): cùng nghĩa, nhưng chỉ có 20 và 24 px, nên 16 px phải thu nhỏ từ 20 px, nhòe.
  - `document_text_extract`, `document_search`: hình trang giấy, dễ lẫn với `document_dismiss`, `document_page_number`, `document_pdf` cạnh nó ở 16 px; kính lúp của `document_search` gợi "tìm file" hơn là nhận chữ.
  - `text_box_settings`: bánh răng nói "cài đặt", sai nghĩa.

## Engine (source-driven, nghiên cứu 2026-10-21)

| Phương án | Kết luận |
|---|---|
| **Tesseract 5.5.3 tự dựng tĩnh bằng vcpkg** (tag `2026.10.16`) trên GitHub Actions, chạy `tesseract.exe` thành tiến trình riêng | **Chọn** (người dùng chốt 2026-10-22, [ADR-0011](../adr/0011-tesseract-static-build.md)). Một file exe, CRT tĩnh, chỉ thư viện MIT/BSD/Apache/zlib (leptonica 1.87.0, libpng, libjpeg-turbo, libwebp, openjpeg, libtiff, zlib, giflib, cùng libcurl và libarchive mà port bắt buộc); không jbigkit (GPL), không libiconv/libintl (LGPL), nên không có nghĩa vụ phát hành mã nguồn. |
| Bộ cài Windows UB Mannheim 5.5.3 (`bee9e343…`) | Loại: leptonica nhúng jbigkit (GPL v2) và kèm 4 DLL LGPL, phải phát hành mã nguồn tương ứng như ffmpeg và ghi giấy phép cho 33 DLL. Chỉ dùng tạm khi phát triển engine (2026-10-21). |
| NuGet `TesseractOCR` 5.5.2 | Loại: nhãn 5.5.2 nhưng `tesseract.exe` bên trong là 5.5.0, kèm libtiff 4.3.0, libpng 1.6.37, zlib 1.2.11 (cũ, có CVE). |
| `charlesw/tesseract` NuGet | Loại: không bảo trì từ 2022, chạy trong tiến trình. |
| Windows.Media.Ocr | Loại: không có gói OCR tiếng Việt. |
| PaddleOCR/RapidOCR (ONNX) | Loại cho v1.4.0: +100–150 MB, phải tự dựng lớp chữ PDF. |

- `.github/workflows/build-tesseract.yml` (chạy tay từ main) dựng bằng `scripts/build-tesseract.ps1`, kiểm `dumpbin` chỉ còn DLL hệ thống của Microsoft, kiểm phiên bản, chạy thử OCR tiếng Việt, attest, rồi đăng release `deps-tesseract-5.5.3` (pre-release, không phải latest). `scripts/deps.json` ghim zip đó bằng SHA256.
- Dữ liệu: `tessdata_fast` tag 4.1.0 (Apache-2.0): `vie` (531 275 byte, SHA256 `79df64caf7bcfb2a27df5042ecb6121e196eada34da774956995747636d5bfa1`), `eng` (`7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`), `osd` (`9cf5d576fcc47564f11265841e5ca839001e7e6f38ff7f7aacf46d15a96b00ff`); thêm `pdf.ttf` và `configs/pdf` từ tag 5.5.3 của tesseract-ocr/tesseract (lớp chữ PDF cần).
- Bố cục: `deps/tesseract/` (dev) và `{app}\tesseract\` (đã cài): `tesseract.exe`, `tessdata/{vie,eng,osd}.traineddata`, `tessdata/pdf.ttf`, `tessdata/configs/pdf`, `licenses/`.

## Cách làm

- Ngôn ngữ mặc định `-l vie+eng`, `--oem 1` (LSTM), `--psm 3`; `TESSDATA_PREFIX` trỏ đúng thư mục `tessdata` của Flevert (không dùng biến môi trường của người dùng). Gọi qua `ExternalTool` trong job object (giới hạn bộ nhớ, timeout, độ ưu tiên thấp) như ffmpeg/qpdf. Job object không chặn được mạng. Bảo đảm "không mạng" nằm ở đầu vào: Flevert chỉ đưa tesseract đường dẫn đầy đủ tới một file PNG trên máy do chính Flevert ghi trong thư mục tạm riêng, không bao giờ URL, danh sách file hay file của người lạ trực tiếp (bản hiện tại có libcurl nên đọc được `http(s)://`, và tesseract coi đầu vào không phải ảnh là danh sách file); `Tesseract.cs` từ chối đường dẫn không đầy đủ hoặc không phải file ảnh. Kế hoạch (chưa xong): dựng lại tesseract không có libcurl và libarchive.
- Ảnh nguồn: giải mã bằng Magick.NET (đã có giới hạn) thành PNG xám trong thư mục tạm rồi mới đưa tesseract.
- PDF: pdfium render từng trang 300 DPI (pdf-worker sẵn có). Trang đã có chữ (pdfium đếm > 10 ký tự) thì bỏ qua (`--skip-text` của OCRmyPDF). Trang cần OCR: tesseract `-c textonly_pdf=1 pdf` ra PDF chỉ có lớp chữ vô hình đúng khổ trang, rồi qpdf `--overlay` lên trang gốc (cách "sandwich" của OCRmyPDF, chỉ lấy ý tưởng, MPL-2.0 không chép mã). Trang gốc không bị mã hóa lại.
- Tự xoay: OSD (`--psm 0 -l osd`) chỉ dùng khi độ tin cậy ≥ 14 (mặc định `rotate_pages_threshold` của OCRmyPDF): xoay ảnh trước khi nhận chữ để chữ đúng chiều; trang PDF output giữ nguyên hướng nhìn.
- Song song theo trang trong giới hạn lõi và ngân sách bộ nhớ chung (ADR-0006); tiến độ trong file theo số trang.

## Thao tác

| op-id | Menu | Output |
|---|---|---|
| `pdf.make-searchable` | Shift: Flevert: more PDF tools > Make searchable (OCR) | `<tên> (searchable).pdf`. Mọi trang đã có chữ → skipped "This PDF already has text you can search." |
| `pdf.to-text` (đã có) | Convert to > Text | Trang có chữ: như cũ; trang scan: chữ OCR. |
| `image.to-text` | Convert to > Text (thay BMP) | `<tên>.txt` UTF-8. Không nhận ra chữ nào → skipped "No text was found in this picture." |

## Giới hạn đã biết

- Trang scan đã có hơn 10 ký tự chữ thật (dòng tiêu đề đánh máy, "Scanned with …", số Bates, dấu đóng trang) được coi là trang có chữ và không được OCR, cả ở `Convert to > Text` lẫn `Make searchable (OCR)`: `Convert to > Text` chỉ lấy mấy ký tự đó, `Make searchable` để nguyên trang. Bỏ qua trang có chữ như `--skip-text` của OCRmyPDF để không chồng lớp chữ thứ hai lên trang có chữ (success criterion 2); ngưỡng 10 ký tự là của Flevert.

## Success criteria

1. PDF scan tiếng Việt mẫu (tạo trong test, có dấu) → `Make searchable`: pdfium lấy lại chữ trên mỗi trang, khớp văn bản gốc ≥ 95 % ký tự; trang render ra giống hệt trang gốc (so pixel); số trang và kích thước trang không đổi.
2. PDF trộn trang có chữ và trang scan: trang có chữ không bị chồng lớp chữ thứ hai.
3. Ảnh PNG/JPG/HEIC tiếng Việt → `.txt` đúng ≥ 95 % ký tự. Trang xoay 90°/180° vẫn nhận đúng (OSD).
4. Hủy giữa chừng: tesseract dừng ngay, không file dở. File độc (ảnh/PDF trong bảng file độc hiện có) không treo, không làm sập flevert.exe.
5. Tesseract chỉ chạy từ đường dẫn của Flevert, `TESSDATA_PREFIX` của Flevert, và chỉ nhận đường dẫn đầy đủ tới file PNG trên máy do Flevert tự ghi (không URL, không danh sách file), nên không có gì khiến nó mở mạng. Job object không chặn mạng; bản tesseract không libcurl/libarchive là kế hoạch.
6. Bộ cài kèm tesseract và giấy phép của mọi file; `scripts/write-notices.ps1` không từ chối file nào; installer test đạt, gỡ sạch.
7. Năm bộ test (Core, Image, Pdf, Media, Ocr) xanh, CI ba lần cùng commit.

## Boundaries

- **Always:** test trước khi rẻ; binary chỉ lấy qua `scripts/deps.json` có SHA256; mọi file người lạ đi qua bộ giải mã có giới hạn trước khi tới tesseract.
- **Ask first:** thêm ngôn ngữ khác ngoài vie/eng; đổi menu chính.
- **Never:** gửi file ra mạng; đưa mã GPL/LGPL/MPL vào Flevert; commit binary hay traineddata.

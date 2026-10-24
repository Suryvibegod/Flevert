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
| **Tesseract 5.5.3 tự dựng tĩnh bằng vcpkg** (tag `2026.10.16`) trên GitHub Actions, chạy `tesseract.exe` thành tiến trình riêng | **Chọn** (người dùng chốt 2026-10-22, [ADR-0011](../adr/0011-tesseract-static-build.md)). Một file exe, CRT tĩnh, chỉ thư viện MIT/BSD/Apache/zlib (leptonica 1.87.0, libpng, libjpeg-turbo, libwebp, openjpeg, libtiff, zlib, giflib; libcurl và libarchive mà port bắt buộc được bỏ bằng overlay `DISABLE_CURL`/`DISABLE_ARCHIVE`, ScrollView bằng `GRAPHICS_DISABLED`, nên exe chỉ import `KERNEL32.dll`); không jbigkit (GPL), không libiconv/libintl (LGPL), nên không có nghĩa vụ phát hành mã nguồn. |
| Bộ cài Windows UB Mannheim 5.5.3 (`bee9e343…`) | Loại: leptonica nhúng jbigkit (GPL v2) và kèm 4 DLL LGPL, phải phát hành mã nguồn tương ứng như ffmpeg và ghi giấy phép cho 33 DLL. Chỉ dùng tạm khi phát triển engine (2026-10-21). |
| NuGet `TesseractOCR` 5.5.2 | Loại: nhãn 5.5.2 nhưng `tesseract.exe` bên trong là 5.5.0, kèm libtiff 4.3.0, libpng 1.6.37, zlib 1.2.11 (cũ, có CVE). |
| `charlesw/tesseract` NuGet | Loại: không bảo trì từ 2022, chạy trong tiến trình. |
| Windows.Media.Ocr | Loại: không có gói OCR tiếng Việt. |
| PaddleOCR/RapidOCR (ONNX) | Loại cho v1.4.0: +100–150 MB, phải tự dựng lớp chữ PDF. |

- `.github/workflows/build-tesseract.yml` (chạy tay từ main) dựng bằng `scripts/build-tesseract.ps1`, kiểm danh sách port vcpkg cho phép, nhúng manifest `activeCodePage=UTF-8` (đường dẫn có chữ Việt như `C:\Users\Dương\`), kiểm `dumpbin` chỉ còn DLL hệ thống của Microsoft và không DLL mạng, kiểm phiên bản, chạy thử OCR tiếng Việt trong thư mục tên Việt, attest (chỉ từ main), rồi đăng release `deps-tesseract-5.5.3-2` (pre-release, không phải latest). `scripts/deps.json` ghim zip đó bằng SHA256.
- Dữ liệu: `tessdata_fast` tag 4.1.0 (Apache-2.0): `vie` (531 275 byte, SHA256 `79df64caf7bcfb2a27df5042ecb6121e196eada34da774956995747636d5bfa1`), `eng` (`7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`), `osd` (`9cf5d576fcc47564f11265841e5ca839001e7e6f38ff7f7aacf46d15a96b00ff`); thêm `pdf.ttf` và `configs/pdf` từ tag 5.5.3 của tesseract-ocr/tesseract (lớp chữ PDF cần).
- Bố cục: `deps/tesseract/` (dev) và `{app}\tesseract\` (đã cài): `tesseract.exe`, `tessdata/{vie,eng,osd}.traineddata`, `tessdata/pdf.ttf`, `tessdata/configs/pdf`, `licenses/`.

## Cách làm

- Ngôn ngữ mặc định `-l vie+eng`, `--oem 1` (LSTM), `--psm 3`; `TESSDATA_PREFIX` trỏ đúng thư mục `tessdata` của Flevert (không dùng biến môi trường của người dùng). Gọi qua `ExternalTool` trong job object (giới hạn bộ nhớ, timeout, độ ưu tiên thấp) như ffmpeg/qpdf. Job object không chặn được mạng, nhưng bản dựng `deps-tesseract-5.5.3-2` không có libcurl, libarchive, ScrollView và chỉ import `KERNEL32.dll`, nên không có mã mạng. Flevert vẫn chỉ đưa tesseract đường dẫn đầy đủ tới một file PNG trên máy do chính Flevert ghi trong thư mục tạm riêng, không bao giờ danh sách file hay file của người lạ trực tiếp (tesseract coi đầu vào không phải ảnh là danh sách file); `Tesseract.cs` từ chối đường dẫn không đầy đủ hoặc không phải file ảnh.
- Ảnh nguồn: giải mã bằng Magick.NET (đã có giới hạn; khung đầu của GIF/TIFF nhiều trang, xoay theo EXIF, nền trong suốt thành trắng) thành PNG xám 8 bit, 300 DPI, trong thư mục tạm riêng rồi mới đưa tesseract. Cỡ ảnh đưa tesseract theo `ImageToText.OcrSize`, một hệ số chung cho hai chiều:
  - cạnh ngắn dưới 1000 px (chữ chụp màn hình quá nhỏ với tesseract) thì phóng 2×, nhưng chỉ tới mức còn vừa hai trần dưới đây;
  - trần diện tích 25 MP (`MaxOcrPixels`, khoảng trang 6000 × 4200 px: bộ nhớ và thời gian của tesseract tăng theo diện tích, ba trang A4 300 DPI vẫn trong trần 1 GB và 150 s) và trần cạnh dài 30000 px (`MaxOcrSide`, dưới 32767 px tesseract nhận và 40000 px của ImageMagick);
  - ảnh lớn hơn hai trần (ảnh 50–200 MP) thì thu nhỏ cho vừa. Trần đặt trên diện tích chứ không trên cạnh dài, nên ảnh chụp màn hình dài giữ nguyên bề ngang và vẫn đọc được.
  Trang PDF không qua quy tắc này: pdfium render 300 DPI (dưới đây) và ngân sách render OCR chỉ tính các trang sẽ đọc.
- PDF: pdfium render từng trang 300 DPI (pdf-worker sẵn có). Trang đã có chữ (pdfium đếm > 10 ký tự, không tính khoảng trắng, như Convert to > Text) thì bỏ qua (`--skip-text` của OCRmyPDF). Trang cần OCR: tesseract `-c textonly_pdf=1 pdf` ra PDF chỉ có lớp chữ vô hình đúng khổ trang, rồi qpdf `--overlay` lên trang gốc (cách "sandwich" của OCRmyPDF, chỉ lấy ý tưởng, MPL-2.0 không chép mã). Trang gốc không bị mã hóa lại.
- Tự xoay: OSD (`--psm 0 -l osd`) cho góc cần xoay và độ tin cậy.
  - Độ tin cậy ≥ 14 (mặc định `rotate_pages_threshold` của OCRmyPDF): tin luôn, xoay ảnh trước khi nhận chữ để chữ đúng chiều, như trước.
  - Từ 1,5 đến dưới 14, góc ≠ 0: chưa chắc. Nhận chữ cả ảnh như nó đang nằm lẫn ảnh xoay theo góc OSD, hai lần đọc chạy cùng lúc (vẫn qua cổng tesseract chung của tiến trình), mỗi lần đọc ghi luôn chữ và TSV (`-c tessedit_create_txt=1 -c tessedit_create_tsv=1`, cùng một lần chạy), rồi giữ bản đọc có độ tin cậy trung bình của từ cao hơn. Hai độ tin cậy cách nhau không quá 2 điểm thì coi như bằng nhau: giữ bản có nhiều từ nằm trên dòng chạy ngang ảnh hơn (khung dòng trong TSV rộng hơn cao); vẫn bằng nhau thì giữ nguyên. Chỉ tốn thêm một lần đọc, chỉ trên những trang này. Góc trả về là góc thật sự đã xoay (0 khi bản giữ nguyên thắng), Make searchable dùng nó cho `/Rotate`.
  - Dưới 1,5, góc 0, hoặc OSD lỗi (sập, quá bộ nhớ, quá giờ): đọc nguyên như ảnh đang nằm. Hủy vẫn hủy ngay.
  - Vì sao (đo 2026-10-25, bảng dưới): OSD luôn đúng góc khi nó trả lời, kể cả trang 1–2 dòng, nhưng với chữ không chân (Arial, Segoe UI, Tahoma: ảnh chụp màn hình, tài liệu hiện đại) nó ít chắc: 5 dòng chỉ được 8–9, 30 dòng 13,5–19, nên với ngưỡng 14 trang lộn ngược bị đọc ra rác mà không báo gì. Độ tin cậy của từ tách rõ hai trường hợp: đọc đúng chiều 95,4–96,6; đọc lộn ngược hoặc nằm ngang ngược chiều kim đồng hồ 52,8–73,5 (giống văn bản gốc 9–18 %). Trang xoay 90° theo chiều kim đồng hồ thì tesseract tự đọc đúng mà không cần xoay: hai bản đọc chênh nhau ≤ 0,03 điểm, chỉ khác ở chỗ dòng của bản đọc nguyên đứng dọc, nên dùng hướng dòng để phân xử (và trang thẳng bị OSD nói nhầm 90° cũng giữ nguyên nhờ đó). Sàn 1,5: ảnh không có chữ (12 hình vẽ ngẫu nhiên) được 0,01–0,85 với góc ngẫu nhiên; một dòng chữ 1,7–2,7, hai dòng 2,8–4,7, lần nào cũng đúng góc.
  - Trong Make searchable, trang PDF mà OSD (chắc, hoặc qua phép so ở trên) thấy đang hiện nằm ngang hoặc lộn ngược được dựng thẳng bằng cách tăng `/Rotate` của trang đúng góc đó (như `--rotate-pages` của OCRmyPDF; nội dung trang không bị mã hóa lại), và lớp chữ nằm đúng trên chữ, đọc đúng thứ tự từng dòng (người dùng chốt 2026-10-25, review R5: để trang lộn ngược thì Edge/Chrome đọc từ của mỗi dòng theo thứ tự ngược, Ctrl+F cụm từ không thấy). Trang đã hiện thẳng (kể cả nhờ `/Rotate` sẵn có) giữ nguyên.
- Song song theo trang trong giới hạn lõi và ngân sách bộ nhớ chung (ADR-0006); tiến độ trong file theo số trang.

### Đo OSD (2026-10-25)

Trang A4 PNG xám 8-bit 300 DPI (2480 × 3508, đúng ảnh Flevert đưa tesseract), chữ 12 pt, các dòng đầu của trang hóa đơn tiếng Việt trong test, xoay bằng pixel theo chiều kim đồng hồ; tesseract 5.5.3, `osd.traineddata` của tessdata_fast 4.1.0. Ô là `Orientation confidence`; `Rotate` của OSD đúng ở mọi ô có số (0, 270, 180, 90), "—" là OSD không trả lời (quá ít ký tự). Không trang thẳng nào bị OSD nói là xoay.

| Font | Dòng | 0° | 90° | 180° | 270° |
|---|---|---|---|---|---|
| Arial | 1 | — | 1,89 | — | 1,69 |
| Arial | 2 | 3,08 | 3,04 | 3,22 | 2,88 |
| Arial | 5 | 8,83 | 8,62 | 8,99 | 8,31 |
| Arial | 10 | 17,84 | 16,91 | 18,86 | 16,33 |
| Arial | 30 | 17,40 | 16,05 | 18,53 | 16,05 |
| Segoe UI | 1 | — | 1,88 | — | 2,02 |
| Segoe UI | 2 | 2,85 | 2,83 | 2,88 | 2,99 |
| Segoe UI | 5 | 8,27 | 7,95 | 8,30 | 8,29 |
| Segoe UI | 10 | 17,48 | 16,21 | 17,23 | 16,24 |
| Segoe UI | 30 | 16,13 | 13,49 | 17,98 | 18,89 |
| Tahoma | 1 | — | 1,87 | — | 1,90 |
| Tahoma | 2 | 2,81 | 3,00 | 3,03 | 3,07 |
| Tahoma | 5 | 8,07 | 8,74 | 8,55 | 8,68 |
| Tahoma | 10 | 16,20 | 15,74 | 15,99 | 16,41 |
| Tahoma | 30 | 16,49 | 17,08 | 15,92 | 15,69 |
| Times New Roman | 1 | 2,25 | 2,67 | 2,43 | 2,70 |
| Times New Roman | 2 | 4,13 | 4,58 | 4,34 | 4,65 |
| Times New Roman | 5 | 12,01 | 12,82 | 12,50 | 13,14 |
| Times New Roman | 10 | 25,47 | 25,49 | 26,64 | 25,39 |
| Times New Roman | 30 | 28,46 | 28,58 | 32,25 | 27,79 |

- Độ tin cậy trung bình của từ (TSV, `--oem 1 --psm 3 -l vie+eng`): đọc đúng chiều 95,4–96,6 ở mọi font và số dòng; trang xoay 180° hoặc 270° mà đọc nguyên 52,8–73,5. Trang xoay 90° đọc nguyên đúng như trang thẳng (giống văn bản ≥ 99 %, chênh ≤ 0,03 điểm), nhưng khung dòng trong TSV đứng dọc (ví dụ 49 × 814 px); trang thẳng xoay ép 90° cũng vậy.
- 12 hình vẽ không chữ (đường, elip, ô chữ nhật ngẫu nhiên): OSD 0,01–0,85 hoặc không trả lời, góc lung tung (0, 90, 180).
- Đo trên trang 30 dòng 2480 × 2480 (bản đo trước): Arial 11–18, 12,66 ở 180°; Times New Roman 23–28.
- Thời gian (máy dev, OMP_THREAD_LIMIT=1): OSD 0,3–1,2 s; một lần đọc 0,23 s (1–2 dòng) đến 1,4 s (30 dòng). Trang Arial 5 dòng xoay 180° (chưa chắc, đọc cả hai): 1,44–1,51 s cả OSD, xoay và hai lần đọc cùng lúc, so với 1,36–1,59 s nếu OSD chắc (xoay rồi đọc một lần) và 0,75–0,78 s cho trang thẳng (OSD và một lần đọc). Tức là trang chưa chắc tốn thêm khoảng 0,7 s so với trước (khi nó bị đọc ra rác), phần lớn là xoay ảnh; lần đọc thêm tốn thêm CPU bằng một lần đọc (0,3 s cho 5 dòng, tới 1,4 s cho trang đầy), và cả thời gian đó khi mọi lõi đang bận (PDF nhiều trang).

## Thao tác

| op-id | Menu | Output |
|---|---|---|
| `pdf.make-searchable` | Shift: Flevert: more PDF tools > Make searchable (OCR) | `<tên> (searchable).pdf`. Mọi trang đã có chữ → skipped "This PDF already has text you can search."; tesseract không thấy chữ nào trên các trang scan → skipped "No text was found in the scanned pages of this PDF." (như Convert to > Text của ảnh), không ghi file. |
| `pdf.to-text` (đã có) | Convert to > Text | Trang có chữ: như cũ; trang scan: chữ OCR. |
| `image.to-text` | Convert to > Text (thay BMP) | `<tên>.txt` UTF-8. Không nhận ra chữ nào → skipped "No text was found in this picture." |

## Giới hạn đã biết

- Trang scan đã có hơn 10 ký tự chữ thật (dòng tiêu đề đánh máy, "Scanned with …", số Bates, dấu đóng trang) được coi là trang có chữ và không được OCR, cả ở `Convert to > Text` lẫn `Make searchable (OCR)`: `Convert to > Text` chỉ lấy mấy ký tự đó, `Make searchable` để nguyên trang. Bỏ qua trang có chữ như `--skip-text` của OCRmyPDF để không chồng lớp chữ thứ hai lên trang có chữ (success criterion 2); ngưỡng 10 ký tự là của Flevert.
- Ảnh lớn hơn 25 MP bị thu nhỏ về 25 MP trước khi đọc (`OcrSize` ở trên), nên chữ rất nhỏ trong ảnh rất lớn có thể bị sót. Mỗi ảnh hoặc trang có 150 s và 1 GB cho tesseract.
- OSD chỉ chắc (≥ 14) khi trang đủ chữ, khoảng mười dòng; trang/ảnh xoay chỉ có một hai dòng được đọc nguyên trạng.

## Success criteria

1. PDF scan tiếng Việt mẫu (tạo trong test, có dấu) → `Make searchable`: pdfium lấy lại chữ trên mỗi trang, khớp văn bản gốc ≥ 95 % ký tự; trang render ra giống hệt trang gốc (so pixel), hoặc, với trang được dựng thẳng, giống hệt trang gốc xoay đúng góc đó; số trang và kích thước trang không đổi (trang dựng thẳng 90°/270° đổi chiều ngang/dọc khi hiện).
2. PDF trộn trang có chữ và trang scan: trang có chữ không bị chồng lớp chữ thứ hai.
3. Ảnh PNG/JPG/HEIC tiếng Việt → `.txt` đúng ≥ 95 % ký tự. Trang xoay 90°/180°/270° vẫn nhận đúng (OSD), kể cả trang chữ không chân hay trang ngắn mà OSD ít chắc; trang thẳng không bị xoay nhầm.
4. Hủy giữa chừng: tesseract dừng ngay, không file dở. File độc (ảnh/PDF trong bảng file độc hiện có) không treo, không làm sập flevert.exe.
5. Tesseract chỉ chạy từ đường dẫn của Flevert, `TESSDATA_PREFIX` của Flevert, và chỉ nhận đường dẫn đầy đủ tới file PNG trên máy do Flevert tự ghi (không URL, không danh sách file), và bản dựng không có mã mạng (không libcurl, libarchive, ScrollView; chỉ import `KERNEL32.dll`). Đường dẫn có chữ ngoài code page ANSI (tài khoản `Dương`) đọc được nhờ manifest `activeCodePage=UTF-8`.
6. Bộ cài kèm tesseract và giấy phép của mọi file; `scripts/write-notices.ps1` không từ chối file nào; installer test đạt, gỡ sạch.
7. Năm bộ test (Core, Image, Pdf, Media, Ocr) xanh, CI ba lần cùng commit.

## Boundaries

- **Always:** test trước khi rẻ; binary chỉ lấy qua `scripts/deps.json` có SHA256; mọi file người lạ đi qua bộ giải mã có giới hạn trước khi tới tesseract.
- **Ask first:** thêm ngôn ngữ khác ngoài vie/eng; đổi menu chính.
- **Never:** gửi file ra mạng; đưa mã GPL/LGPL/MPL vào Flevert; commit binary hay traineddata.

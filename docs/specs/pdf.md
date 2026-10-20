# Spec: pdf (v0.2)

Module `pdf` trong [capability-map.md](capability-map.md). Phụ thuộc `core`, `image`. Phiên bản: **v0.2.0**. Trạng thái: **đã chốt phạm vi** (2026-10-20); chi tiết thư viện và giới hạn được kiểm lại ở bước Plan trước khi code (`source-driven-development`).

## Objective

Người dùng Windows làm việc với giấy tờ: chụp hóa đơn, CMND, bài tập bằng điện thoại; nhận PDF qua Zalo, email; cần gửi lại đúng một file, đúng dung lượng, không lộ thông tin. Hôm nay họ phải upload lên web "PDF tools" (lộ giấy tờ cá nhân) hoặc cài phần mềm nặng. Flevert v0.2 làm các việc đó **bằng chuột phải, trên máy, không upload**.

### User stories (ưu tiên từ trên xuống)

1. Chọn 12 ảnh chụp giấy tờ (JPG/HEIC) → chuột phải → **Combine into PDF** → được **một** PDF, trang theo đúng thứ tự tên file, ảnh đúng chiều.
2. Chuột phải một PDF → **Convert to JPG/PNG** → mỗi trang thành một ảnh trong thư mục mới cạnh file.
3. Chọn 3 PDF → **Merge PDFs** → một PDF; chuột phải một PDF → **Split into pages** → mỗi trang một PDF.
4. PDF scan bị ngược/ngang → **Rotate** 90° phải, 90° trái, 180° (mọi trang).
5. PDF scan quá nặng để gửi → **Compress** (không mất chất lượng) hoặc **Compress for sharing** (ảnh scan giảm còn ~150 DPI).
6. Trước khi gửi → **Remove metadata** (tác giả, phần mềm tạo, ngày, XMP, tệp đính kèm).
7. **Protect with password** / **Remove password** (người dùng biết mật khẩu).
8. Ít dùng hơn, làm sau cùng nếu kịp: **Extract images**, **Extract text**, **Reverse page order**, **Repair**.

Ngoài phạm vi v0.2: sửa nội dung trang, ký số, OCR, điền form, chuyển Word/Excel ↔ PDF, nén PDF có mật khẩu mà không có mật khẩu, bỏ mật khẩu không biết (phá khóa), xóa trang trắng tự động (dễ xóa nhầm trang ít chữ; xem lại sau v0.2).

### Thao tác và tên output

Tên menu tiếng Anh, theo kiểu menu ảnh hiện có (`Flevert > <nhóm> > <mục>`). Output luôn nằm cạnh file gốc, không ghi đè (quy tắc `(1)`, `(2)` của core), file gốc không đổi.

| op-id | Menu | Áp dụng | Kiểu | Output |
|---|---|---|---|---|
| `pdf.from-images` | Combine into PDF | ảnh đọc được của module `image` (≥ 1 file) | N → 1 | `<tên đầu tiên> (+<N−1>).pdf`; một ảnh: `<tên>.pdf` |
| `pdf.to-jpg` | Convert > JPG pages | `.pdf` | 1 → N | thư mục `<tên> (pages)\<tên> 001.jpg` … |
| `pdf.to-png` | Convert > PNG pages | `.pdf` | 1 → N | thư mục `<tên> (pages)\<tên> 001.png` … |
| `pdf.merge` | Merge PDFs | `.pdf` (≥ 2 file) | N → 1 | `<tên đầu tiên> (merged).pdf` |
| `pdf.split` | Split into pages | `.pdf` | 1 → N | thư mục `<tên> (pages)\<tên> 001.pdf` … |
| `pdf.rotate-right` / `-left` / `-180` | Rotate > 90° right / 90° left / 180° | `.pdf` | 1 → 1 | `<tên> (rotated).pdf` |
| `pdf.compress` | Compress > Lossless | `.pdf` | 1 → 1 | `<tên> (compressed).pdf`; không nhỏ hơn thì bỏ qua (skipped) |
| `pdf.compress-sharing` | Compress > For sharing | `.pdf` | 1 → 1 | `<tên> (small).pdf`: ảnh > 150 DPI giảm còn 150 DPI, JPEG q80 |
| `pdf.strip-metadata` | Remove metadata | `.pdf` | 1 → 1 | `<tên> (no metadata).pdf` |
| `pdf.protect` | Protect with password | `.pdf` | 1 → 1 | `<tên> (protected).pdf`, AES-256 |
| `pdf.unlock` | Remove password | `.pdf` có mật khẩu | 1 → 1 | `<tên> (unlocked).pdf` |
| `pdf.extract-images` | Extract images *(nếu kịp)* | `.pdf` | 1 → N | thư mục `<tên> (images)\`; JPEG trong PDF ra `.jpg` nguyên byte, còn lại `.png` |
| `pdf.extract-text` | Extract text *(nếu kịp)* | `.pdf` | 1 → 1 | `<tên>.txt` (UTF-8, ngăn trang bằng form feed) |
| `pdf.reverse` | Reverse page order *(nếu kịp)* | `.pdf` | 1 → 1 | `<tên> (reversed).pdf` |
| `pdf.repair` | Repair *(nếu kịp)* | `.pdf` | 1 → 1 | `<tên> (repaired).pdf` (qpdf dựng lại bảng xref) |

Quy tắc chung:

- **Thứ tự** của thao tác N → 1: sắp tên file theo thứ tự tự nhiên như Explorer (`ảnh 2` trước `ảnh 10`; so sánh không phân biệt hoa thường, theo `StrCmpLogicalW`). Thứ tự Explorer gửi file không đáng tin (mỗi file một tiến trình, ADR-0002). File ở nhiều thư mục: output vào thư mục của file đứng đầu.
- **Ảnh → PDF**: mỗi ảnh một trang, kích thước trang theo ảnh ở 150 DPI nếu ảnh không ghi DPI (ảnh điện thoại 4000 px → trang ~68 cm là quá to: trang lớn hơn A4 thì thu về vừa A4 hoặc Letter theo chiều dài nhất, giữ tỉ lệ). Xoay đúng chiều theo EXIF như module `image`. JPEG nhúng **nguyên byte** (DCTDecode, không nén lại, không mất chất lượng; EXIF/GPS bị bỏ khỏi luồng nhúng). Ảnh khác: HEIC/HEIF/AVIF/WebP chụp → JPEG q92; PNG/GIF/BMP/TIFF → nén Flate không mất dữ liệu; nền trong suốt thành trắng. Metadata của PDF tạo ra: chỉ `Producer: Flevert`. Chi tiết đã quyết khi làm (T11, `src/Flevert.Pdf/ImagesToPdf.cs`):
  - **Đường đi theo định dạng thật** (chữ ký file, không theo đuôi): JPEG → `JpegSegments` bỏ metadata rồi nhúng, hướng EXIF xoay bằng ma trận trang. JPEG trình xem PDF không hiện được (`NotSupportedException`: arithmetic, lossless, 12-bit, CMYK thiếu APP14) → mã hóa lại như HEIC. JPEG **hỏng** (`InvalidDataException`: bị cắt giữa dữ liệu ảnh, thiếu bảng) → **từ chối**, không mã hóa lại phần ImageMagick đọc được (một trang thiếu nửa dưới trông như đủ). HEIC/HEIF/AVIF và WebP **lossy** → xoay thẳng, chuyển sang sRGB (pdfium ghi `DeviceRGB`, profile bị bỏ qua), nền trong suốt thành trắng, JPEG q92 4:2:0. PNG/GIF/BMP/TIFF và WebP **lossless** → điểm ảnh xoay thẳng, sRGB, Flate (WebP lossless giữ lossless).
  - **Nhiều khung**: ảnh động (GIF, WebP) lấy **khung đầu**, vẽ trên canvas của nó (khung là thời gian, không phải trang). TIFF nhiều trang → **mỗi trang một trang PDF**, bỏ thumbnail (NewSubfileType = 1): TIFF nhiều trang là tài liệu scan, bỏ trang là mất nội dung. Vì vậy số trang PDF có thể lớn hơn số file.
  - **DPI**: lấy từ ảnh khi đơn vị là inch hoặc cm (cm × 2,54) và X = Y; đơn vị không ghi (chỉ tỉ lệ) hoặc X ≠ Y → không biết, 150; ngoài 1–10 000 → 150 (`PdfBuilder`).
  - **Cạnh trang tối thiểu 3 pt** (kích thước trang nhỏ nhất PDF 1.7, phụ lục C, cho trình xem nhận): ảnh rất dẹt hoặc rất nhỏ (10 000 × 1 px, 1 × 1 px) có trang cạnh ngắn 3 pt, ảnh nằm giữa trên nền trắng, không kéo giãn. Trang khác vừa đúng ảnh.
  - **Thứ tự và tên**: theo thứ tự runner đưa (tự nhiên, như Explorer); output `<tên đầu> (+N−1).pdf`, một ảnh → `<tên>.pdf`, cạnh ảnh đầu, quy tắc `(1)` nếu trùng. Tiến độ "image 3 of 12" (theo file, TIFF nhiều trang vẫn tính một).
  - **Tất cả hoặc không**: đọc header **mọi** ảnh trước khi giải mã ảnh nào; quá 2 000 trang, ảnh quá 100 MP, hay bitmap cộng lại quá 1,5 GB → từ chối ngay. Một ảnh không dùng được (hỏng, rỗng, sai định dạng, quá lớn) → cả PDF lỗi, thông báo nêu file. Hủy → không còn gì (lúc lưu, luồng ghi báo hủy ở lần ghi kế tiếp để pdfium dừng).
  - **Bộ nhớ**: xin `MemoryBudget` chung với module ảnh: một phần cho mỗi lần giải mã (JPEG nhúng: 3 × file; mã hóa lại: 3 × điểm ảnh giải mã + 2 × file; bitmap: điểm ảnh giải mã + 10 byte/px + 2 × file), một phần cho mỗi trang **suốt lúc builder giữ nó** (byte JPEG, hoặc 3 byte/px), và lúc lưu một phần cỡ bitmap lớn nhất (bộ đệm Flate). Phần nào không bao giờ vừa bên cạnh những phần đang giữ → lỗi "Together the images need more memory than Flevert allows for images (…)" thay vì chờ mãi.
- **1 → N**: thư mục output có tên chưa tồn tại (quy tắc `(1)` của core áp cho thư mục); file đánh số đủ chữ số (`001` khi ≤ 999 trang).
- **Merge** (`pdf.merge`, T6, `src/Flevert.Pdf/PdfMerge.cs`): một tiến trình qpdf `--empty --pages <file>… -- <output>`, mọi trang của từng file theo thứ tự runner đưa (tự nhiên, như Explorer); qpdf chép trang nguyên vẹn, không mã hóa lại; output là PDF mới (không giữ bookmark và Info của file nào). Chọn một PDF → lỗi "Select at least two PDFs to merge." (không nêu file: lựa chọn sai chứ không phải file). Tất cả hoặc không: file cần mật khẩu, không phải PDF hay quá 2 GB → cả merge lỗi, thông báo nêu file đó (lỗi của qpdf nêu đường dẫn nó hỏng; không nêu file nào thì hỏi `--requires-password` từng file). qpdf giữ mọi input cùng lúc nên giới hạn 2 GB, trần bộ nhớ (`MaxMemoryFor`) và timeout tính theo **tổng** cỡ các file; tổng quá 2 GB → "Together these PDFs are larger than 2 GB…". Không có tiến độ trong file (qpdf không báo, một bước).
- **Split** (`pdf.split`, T8, `src/Flevert.Pdf/PdfSplit.cs`): qpdf `--show-npages` trước (0 trang → "This PDF has no pages."; quá 2 000 trang → từ chối trước khi ghi gì, như pdfium), rồi một lần `--split-pages`. PDF một trang vẫn ra thư mục một file (cùng hình dạng kết quả với mọi input). Tên `<tên> 001.pdf`, số chữ số = max(3, số chữ số của số trang), tên cắt phần stem cho vừa 255 ký tự (không cắt đôi cặp surrogate). qpdf tạo file trang theo tên đoán trước được và ghi xuyên qua thứ gì đã nằm ở tên đó, mà thư mục tạm cạnh input có thể cho tài khoản khác thêm mục (một link đặt sẵn sẽ làm qpdf ghi đè file nó trỏ tới): vì vậy qpdf ghi vào thư mục con tạo **kèm sẵn** quyền chỉ user, SYSTEM, Administrators, rồi từng trang được **chép** sang tên cuối bằng `CreateNew` (chép thì file nhận quyền của thư mục như mọi file mới; move giữ quyền riêng, đã probe). Đường dẫn có `%d` → từ chối (`UnsupportedFileName`): qpdf thay `%d` **đầu tiên** của đường dẫn output bằng số trang dù nằm ở thư mục nào (probe: thư mục `a%d` cạnh `a1` → trang 1 ghi vào `a1`). Tiến độ "page N of M" theo số file qpdf đã ghi (đếm thư mục 5 lần/giây; qpdf không báo gì tới khi thoát). Hủy → qpdf bị giết, không còn `.partial`.
- **Remove metadata** (`pdf.strip-metadata`, T12, `src/Flevert.Pdf/PdfStripMetadata.cs`, `MetadataRemoval.cs`): output `<tên> (no metadata).pdf` (cùng kiểu tên với thao tác ảnh). Bỏ, ở **mọi chỗ** trong file: Info (thay bằng chỉ `/Producer (Flevert)`, như PDF Flevert tạo; trailer không có Info hoặc Info viết thẳng trong trailer → Info mới); XMP (`/Metadata` của catalog, trang, ảnh, mọi object, và chính luồng XMP); dữ liệu riêng của ứng dụng (`/PieceInfo`, có thể chứa cả bản cũ của tài liệu); tệp đính kèm (cây tên `/EmbeddedFiles`, `/EF` của mọi file spec, luồng `/EmbeddedFile`, chú thích `/FileAttachment`); JavaScript (cây tên `/JavaScript`, mọi action `/S /JavaScript` ở bất cứ đâu: `/OpenAction`, `/AA` của tài liệu/trang/chú thích/trường form, link, bookmark, chuỗi `/Next`; và khóa `/JS` của action khác như Rendition). Giữ: nội dung trang (render giống hệt), bookmark, link (mất action JavaScript), form, cấu trúc. **Chữ ký số mất hiệu lực** (file đã đổi). Cách làm: qpdf không có tùy chọn đủ (`--remove-info` không giữ Producer, `--remove-metadata` chỉ bỏ XMP của catalog, không gì bỏ script), nên chạy qpdf hai lần: `--json-output=2 --json-stream-data=none` mô tả mọi object (không có dữ liệu luồng) ra file JSON; Flevert đọc từng mục một (`QpdfJsonFile`, bộ nhớ theo object lớn nhất, sâu tới 600 vì qpdf 12.4.2 từ chối lồng quá 500) và viết bản cập nhật chỉ gồm các object đổi (object bị bỏ → null, mọi tham chiếu tới nó bị gỡ khỏi dict/mảng); `--update-from-json=<file>` (đi qua `@-` như đường dẫn) ghi PDF mới. Hai file JSON lặp lại metadata nên nằm trong thư mục riêng tạo kèm quyền chỉ user, SYSTEM, Administrators dưới `%TEMP%` (tạo thất bại nếu tên đã có), **không bao giờ cạnh input**, xóa khi thao tác xong. PDF chỉ có mật khẩu owner: giữ mã hóa. Chưa bỏ: script trong form XFA, ảnh thu nhỏ trang (`/Thumb`), `/ID` của trailer.
- **PDF có mật khẩu mở**: mọi thao tác trừ `pdf.unlock` báo lỗi rõ "This PDF needs a password: use Remove password first" (không hỏi mật khẩu cho từng thao tác ở v0.2).
- **Render** (`to-jpg`, `to-png`): 150 DPI mặc định (đủ đọc trên điện thoại, nhỏ), giới hạn ở dưới. JPEG q90, 4:2:0 (file nhỏ hơn; ImageMagick tự để 4:4:4 từ q90); PNG không mất dữ liệu, không kênh alpha (trang vẽ trên nền trắng). Ảnh ghi DPI thật của trang (in đúng khổ). Một trang pdfium không đọc được → cả file lỗi, thông báo nêu số trang (thư mục ghi nguyên vẹn hoặc không có gì; thiếu một trang mà trông như đủ thì tệ hơn). PDF không có trang → lỗi "This PDF has no pages.".
- **Đọc PDF trong tiến trình worker** (T9b, ADR-0008 mục "Reading untrusted PDFs in a worker"): `pdf.to-jpg/png` (và mọi thao tác sau này mở PDF lạ bằng pdfium) chạy `flevert.exe pdf-worker` (lệnh ẩn, không có trong usage) qua `ExternalTool`: trần bộ nhớ 2,5 GB, không được tạo tiến trình khác, chết theo Flevert. Dòng lệnh chỉ có `pdf-worker`; yêu cầu (đường dẫn, định danh file, thư mục, định dạng, DPI) đi qua stdin dạng UTF-16 thô ngăn bằng U+0000 (tên có surrogate lẻ vẫn đúng). Worker mở lại đường dẫn đã ghim (cha giữ file, chỉ chia sẻ đọc) và từ chối nếu định danh file (`FILE_ID_INFO`) khác cái cha gửi. Worker render từng trang vào **một** bộ đệm dùng lại, mã hóa thẳng vào file trong thư mục `.partial` của cha (`CreateNew`), báo từng dòng: `pages N`, `page n rộng cao`, `busy n` (lúc mã hóa, tối đa 2 lần/giây), `wrote n`, `done N`, hoặc dòng cuối `error <mã> <thông báo>`. Cha đổi `page` thành "page n of N" cho cửa sổ tiến độ (thanh tiến độ chạy trong một file). **Watchdog**: một bước không có dấu hiệu tiến triển quá 10 s + 0,25 s mỗi megapixel của trang (A4 150 DPI: 10,5 s; trang 100 MP: 35 s; mở file: 10 s + 1 s mỗi 100 MB) → giết worker, lỗi "Page n of this PDF took too long to render: it is too complex, or made to stall PDF readers."; Cancel = giết worker rồi xóa thư mục tạm. Worker chết không nói gì → "Flevert's PDF reader stopped on page n of this PDF without finishing: the PDF may be damaged, or made to crash PDF readers."; vượt trần → thông báo "needs more memory" như qpdf (nêu 2,5 GB); mật khẩu, không phải PDF: thông báo chung. Chủ lô không nạp pdfium hay ImageMagick cho thao tác này nên không còn xin `MemoryBudget` theo trang (trần của worker giới hạn bộ nhớ). Ảnh → PDF (`PdfBuilder`) vẫn chạy pdfium trong tiến trình: input là ảnh đã qua Magick.

### Mật khẩu (protect / unlock)

- Hộp thoại WinForms nhỏ: ô mật khẩu che ký tự, ô nhập lại (protect), nút OK/Cancel; Cancel = file đó bị bỏ qua. Một lô nhiều file: hỏi **một lần** cho cả lô ("Use this password for all 5 files").
- Mật khẩu **không bao giờ** nằm trên dòng lệnh, trong log, trong biến môi trường: truyền cho qpdf qua stdin (`@-` hoặc `--password-file=-`), xóa khỏi bộ nhớ khi xong (mảng `char[]`, ghi đè 0).
- Protect: AES-256 (`--encrypt <user> <owner> 256`), owner password = user password; không đặt hạn chế in/sao chép (dễ bị hiểu sai là bảo mật).
- Unlock sai mật khẩu: lỗi "Wrong password" cho file đó, các file khác vẫn chạy.

## Lựa chọn thư viện

Đã kiểm ở nguồn chính thức ngày 2026-10-20; kiểm lại phiên bản và hash ở bước Plan.

| Nhu cầu | Chọn | Giấy phép | Lý do / ghi chú |
|---|---|---|---|
| Render trang, đếm trang, đọc text | **pdfium** bản dựng sẵn của `bblanchon/pdfium-binaries` (`pdfium-win-x64.tgz`, chromium/8100 ngày 2026-10-12), gọi bằng P/Invoke mỏng tự viết | pdfium: BSD-3-Clause (+ thư viện kèm: FreeType, libjpeg-turbo, OpenJPEG, lcms2, zlib, mỗi cái giấy phép riêng); bản dựng: MIT | PDF là **input không tin cậy**: phải dùng pdfium mới. **Không** dùng `Docnet.Core` (bản cuối 09/2023) hay `PDFiumCore` (kèm pdfium khoảng 2021): kèm pdfium cũ chưa vá lỗi bảo mật |
| Ghép, tách, xoay, mã hóa, gỡ mật khẩu, nén cấu trúc, sửa xref, xóa metadata | **qpdf** 12.4.2 `qpdf-12.4.2-msvc64.zip` chạy như tiến trình riêng | Apache-2.0 | Chuẩn, nhanh, giữ nguyên nội dung trang; tiến trình riêng nên lỗi của qpdf không làm sập Flevert |
| Ảnh → PDF | **pdfium** (API tạo tài liệu `fpdf_edit.h`, `PdfBuilder`) thay cho bộ ghi PDF tự viết dự định ban đầu (T11a, ADR-0008); bỏ metadata JPEG ở mức marker bằng `JpegSegments`; giải mã ảnh khác bằng Magick.NET có sẵn | — | Nhúng JPEG nguyên byte; pdfium đã lo trang, XObject ảnh, luồng nội dung, bảng xref và được bảo trì. Magick.NET **đọc** PDF cần Ghostscript (AGPL) nên không dùng Magick cho PDF ở chiều đọc; chiều ghi của Magick nén lại JPEG nên cũng không dùng |
| Compress for sharing | pdfium (đọc ảnh, DPI hiệu dụng) + Magick (giảm ảnh) + qpdf (thay luồng ảnh)? | — | **Câu hỏi mở**: cách thay ảnh trong PDF không cần thư viện AGPL. Nếu không làm được gọn ở bước Plan thì bỏ khỏi v0.2 (Compress > Lossless vẫn có) |

Loại trừ: Ghostscript, iText, MuPDF (AGPL); PDFsharp **6.5 trở lên** (đổi sang AGPL-3.0; 6.2.4 trở xuống là MIT nhưng không còn được vá). Mọi binary (`pdfium.dll`, `qpdf.exe` + DLL) **không commit**: `scripts/fetch-deps.ps1` tải bản ghim theo URL + SHA256 vào `deps/`, CI và release dùng đúng script đó.

## Thay đổi ở core (làm trước, có ADR)

1. **Thao tác nhiều file → một output** (`IBatchOperation`): leader gom hết file của lô (như hiện nay), sắp theo thứ tự tự nhiên, gọi thao tác **một lần** với cả danh sách. Thông báo cuối: `Done: IMG_0001 (+11).pdf`. Lỗi một file đầu vào (ảnh hỏng) → cả output thất bại, thông báo nêu file nào (không tạo PDF thiếu trang âm thầm). Explorer giới hạn 100 file được chọn cho menu (đã ghi ở README).
2. **Một file → nhiều output** (1 → N): thao tác trả về thư mục; ghi vào thư mục tạm `.flevert-<guid>.partial` rồi đổi tên một lần khi xong, như file đơn (hủy giữa chừng không để lại thư mục dở).
3. **Tiến độ trong một file**: `IProgress<FileProgress>` cho thao tác dài (render 200 trang); cửa sổ tiến độ hiện "page 37 of 200".
4. **Gọi tiến trình ngoài** (`ExternalTool`): đường dẫn tuyệt đối trong thư mục cài; `ArgumentList` (không ghép chuỗi); stdin cho dữ liệu nhạy cảm; stdout/stderr đọc giới hạn (1 MB); tùy chọn nhận từng dòng stdout ngay khi tool ghi (cắt ở 64 K ký tự; callback ném lỗi → giết tool, ném lại lỗi đó), cho worker PDF báo tiến độ; timeout cứng; Cancel giết cả cây tiến trình; job object để tiến trình con chết theo Flevert; giới hạn tùy chọn theo lần gọi trên job đó (bộ nhớ mỗi tiến trình, không tạo tiến trình khác; Windows từ chối giới hạn một tiến trình thì chạy lại không có nó và ghi cảnh báo); thay đổi môi trường kế thừa theo lần gọi (đặt hoặc xóa biến; tên có `=`/`\0`, giá trị có `\0` bị từ chối; không dùng cho bí mật); kiểm hash của exe khi khởi động lần đầu sau cài? (**câu hỏi mở**, xem Boundaries).
5. **Giới hạn tài nguyên**: tối đa 2 000 trang/file; trang render tối đa 10 000 px mỗi cạnh **sau khi nhân DPI** (trang lớn thì giảm DPI cho trang đó); bộ nhớ theo `MemoryBudget` hiện có; file PDF tối đa 2 GB.
6. **pdfium không thread-safe**: mọi lời gọi pdfium qua một khóa toàn tiến trình; thao tác pdfium đặt `MaxParallel = 1` (đọc PDF: mỗi file một worker, mỗi worker có trần 2,5 GB, nên một lúc một worker); qpdf chạy song song được (mỗi file một tiến trình): `MaxParallel = 4` (runner còn giới hạn theo số nhân − 1). Mỗi qpdf một nhân, việc chủ yếu là đọc và ghi lại file nên quá vài tiến trình thì đĩa là giới hạn; bộ nhớ của qpdf tăng theo PDF và nằm ngoài `MemoryBudget`, nên mỗi qpdf có trần riêng trong job của nó: 256 MB + 2,5 × kích thước input, tối thiểu 1 GB, tối đa 3 GB (đo với 12.4.2: cấu trúc object của 500 000 trang nhỏ, file 50 MB, tốn 830 MB; một luồng qpdf chép nguyên tốn khoảng gấp đôi cỡ của nó), và không được tạo tiến trình khác (ADR-0008). Bốn qpdf cùng lúc với PDF thường (≤ 300 MB) ở trong 4 GB; trường hợp xấu nhất bốn file gần 2 GB là 12 GB, khi đó Windows từ chối cấp phát và các file đó lỗi rõ.
7. **Menu**: `.pdf` thêm vào danh sách đuôi; ảnh có thêm mục `Combine into PDF`. Thứ tự nhóm trong menu ảnh: Convert, Compress, Combine into PDF, Remove metadata (`MenuOperations.All`, danh sách chung của `Program.cs` và test menu). Menu `.pdf` theo thứ tự bảng thao tác: Convert, Merge PDFs, Split into pages, Rotate, Remove metadata (10 mục, dưới giới hạn 16). Explorer chỉ hiện 16 mục đầu của menu (core.md, mục Menu): để `.heic`/`.heif` không mất `Remove metadata`, ảnh HEIC/HEIF không còn `Convert > GIF` (image.md).
8. **Gọi qpdf** (đã kiểm với qpdf 12.4.2, T3): tùy chọn (hằng của Flevert) trên dòng lệnh, **hai đường dẫn qua stdin bằng `@-`** (mỗi dòng một đối số). Lý do: C runtime của qpdf mở rộng ký tự đại diện trong đối số dòng lệnh (`r*.pdf` thành nhiều file), nên dấu `?` của đường dẫn `\\?\…` (runner dùng cho đường dẫn dài hơn 260 ký tự) biến thành tên khác; dòng đọc từ `@-` giữ nguyên, và qpdf mở được `\\?\` dài, có Unicode. Cả hai đường dẫn **luôn** ở dạng `\\?\` (`\\?\UNC\` cho thư mục chia sẻ; `\\.\`, `//?/`, `\??\` đổi về `\\?\`), chuẩn hóa bằng `Path.GetFullPath` trước (tiền tố tắt chuẩn hóa của Windows), vì qpdf không mở được đường dẫn quá 260 ký tự ở dạng khác, và output tạm dài hơn input 51 ký tự; thông báo của qpdf trong log bỏ tiền tố. Dòng `@-` mã hóa UTF-8 nghiêm ngặt: tên có surrogate lẻ (NTFS cho phép) → lỗi "rename it" trước khi chạy qpdf, không bao giờ thay bằng U+FFFD (sẽ là tên file khác). qpdf không có dấu kết thúc tùy chọn (`--` không phải), nên đường dẫn phải tuyệt đối (không bắt đầu bằng `-` hay `@`) và không chứa xuống dòng; tùy chọn có `*`/`?` hoặc bắt đầu bằng `@` (file tùy chọn) bị từ chối. Môi trường của qpdf: `OPENSSL_CONF` trỏ tới `openssl.cnf` rỗng cạnh `qpdf.exe`, `OPENSSL_MODULES`/`OPENSSL_ENGINES` trỏ thư mục đó, không có `OPENSSL_CONF_INCLUDE` và mọi biến `QPDF_*` (OpenSSL trong qpdf đọc cấu hình kể cả khi chỉ xoay, cấu hình nạp được DLL). Mã thoát: 0 xong; 3 (cảnh báo: qpdf đã sửa gì đó, ví dụ trang thiếu `/Resources` mà mọi trình xem vẫn mở) **tính là xong**, cảnh báo không hiện cho người dùng; 2 lỗi: hỏi lại `qpdf --requires-password` (mã thoát có tài liệu, chữ "invalid password" thì có thể đổi) → thông báo mật khẩu; stderr nhắc file output → lỗi ghi file; còn lại "This file is not a PDF, or it is damaged." (chi tiết của qpdf nằm trong log). Cấp phát bị từ chối (job báo vượt trần, hoặc stderr có `: bad allocation`/`: std::bad_alloc`) → "This PDF needs more memory than Flevert allows for it (<trần>): it is too large or too complex to process." **bất kể mã thoát** (qpdf thiếu bộ nhớ có thể coi object là null rồi thoát 3 với output thiếu nội dung); thành công mà stderr quá 1 MB (phần sau không đọc) → không tin, lỗi "not a PDF, or damaged". Timeout: 2 phút + 1 giây mỗi MB. File > 2 GB bị từ chối trước khi chạy qpdf; giới hạn số trang cho thao tác qpdf để T18 (riêng split đã đếm trang nên áp 2 000 trang). Nhiều input (merge): mọi đường dẫn và dấu `--` kết thúc `--pages` đi qua cùng luồng `@-`; giới hạn cỡ, trần bộ nhớ và timeout tính theo tổng; lỗi do một input → `BatchInputException` nêu nó, lỗi bên trong là `PdfException` như khi chạy file đó một mình.

## Đóng gói và phát hành

- `build-installer.ps1` chép `deps/pdfium/bin/pdfium.dll` và `deps/qpdf/bin/*` vào bộ cài; gom giấy phép của pdfium (và thư viện kèm), qpdf (và thư viện kèm: zlib, libjpeg-turbo, OpenSSL hoặc GnuTLS tùy bản dựng) vào `THIRD-PARTY-NOTICES.txt`; test-installer kiểm các mục đó có mặt.
- Bộ cài lớn thêm khoảng 15 MB (đo ở bước Plan; giới hạn test-installer nâng từ 60 MB lên 80 MB nếu cần).
- Check-location của setup chỉ giải nén `flevert.exe` và DLL nó cần (không giải nén `qpdf.exe` vào thư mục tạm).
- Nâng cấp từ 0.1.0: test-installer có kịch bản cài 0.1.0 (file setup lưu làm fixture ngoài git, tải từ Releases theo hash) rồi cài 0.2.0 đè lên.
- Bật `RestorePackagesWithLockFile` + `dotnet restore --locked-mode` trên CI.

## Commands

```
powershell -File scripts/fetch-deps.ps1           # tải pdfium, qpdf (ghim URL + SHA256) vào deps/
dotnet build Flevert.sln
dotnet test tests/Flevert.Core.Tests
dotnet test tests/Flevert.Image.Tests
dotnet test tests/Flevert.Pdf.Tests
dotnet format Flevert.sln --verify-no-changes
powershell -File scripts/build-installer.ps1
powershell -File scripts/test-installer.ps1
```

## Project Structure

```
src/Flevert.Pdf/             → thao tác PDF (IOperation: PdfRotate, PdfSplit, PdfToImages, PdfStripMetadata; IBatchOperation: ImagesToPdf, PdfMerge), PdfBuilder + JpegSegments (ảnh → PDF), MetadataRemoval + QpdfJsonFile (bản cập nhật JSON cho qpdf), PrivateFolder, Pdfium (P/Invoke), Qpdf (ExternalTool), PdfWorker + PdfRenderWorker (đọc PDF trong `flevert.exe pdf-worker`), MenuOperations (thứ tự menu ảnh + PDF)
src/Flevert.Core/            → IBatchOperation, output thư mục, ExternalTool, tiến độ trong file
tests/Flevert.Pdf.Tests/     → test; fixtures/ sinh bằng script (qpdf/Python), có README nguồn gốc
scripts/fetch-deps.ps1        → tải deps có hash
deps/                         → (gitignore) pdfium, qpdf
docs/adr/0007-…               → IBatchOperation và output nhiều file
docs/adr/0008-…               → pdfium + qpdf, không AGPL
```

## Code Style

Như các module hiện có (xem `src/Flevert.Image`): thao tác là class nhỏ, không trạng thái, nhận đường dẫn đã ghim, ghi qua `OutputPath`; lỗi người dùng hiểu được là exception có message tiếng Anh ngắn.

```csharp
// src/Flevert.Pdf/PdfRotate.cs (rút gọn). Qpdf.TransformAsync đưa input và output cho qpdf qua stdin (@-), xem mục 8 ở trên.
public Task<string> RunAsync(string inputPath, CancellationToken ct)
{
    var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (rotated).pdf");
    var rotate = string.Create(CultureInfo.InvariantCulture, $"--rotate=+{_degrees}:1-z");
    return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _qpdf.TransformAsync(inputPath, temp, [rotate], token), ct);
}
```

## Testing Strategy

- **Unit** (`Flevert.Pdf.Tests`): thứ tự tự nhiên (`ảnh 2` < `ảnh 10`, chữ có dấu, số rất dài); kích thước trang từ DPI; PdfBuilder tạo file mà qpdf `--check` báo hợp lệ và pdfium mở được; JPEG nhúng đúng byte (so hash luồng); tên output; giới hạn trang/px.
- **Tích hợp với pdfium/qpdf thật** (cần `fetch-deps`): mỗi thao tác trên fixture: PDF 3 trang có chữ; PDF xoay sẵn; PDF có mật khẩu; PDF có metadata + XMP + tệp đính kèm; PDF hỏng xref; PDF ảnh scan lớn; PDF độc (bom nén, vòng lặp object, 100 000 trang rỗng) phải bị từ chối trong giới hạn thời gian/bộ nhớ.
- **Kiểm nội dung, không chỉ tồn tại**: render trang 1 của output so với kỳ vọng (màu tại điểm mẫu, hướng xoay); metadata thật sự mất (đọc bằng qpdf `--json`); mật khẩu: mở không được khi thiếu, mở được khi có.
- **Mật khẩu không rò**: test chạy protect với mật khẩu đánh dấu, rồi tìm chuỗi đó trong log, dòng lệnh tiến trình con (WMI), file tạm: không có.
- **Tiến trình thật**: chọn 12 ảnh qua 12 tiến trình `flevert.exe` → đúng một PDF 12 trang đúng thứ tự.
- **Runtime thủ công** trước khi đóng module: bấm thật trong Explorer cho từng nhóm menu; ảnh chụp hộp thoại mật khẩu; UI Automation kiểm tên trợ năng.
- Test viết trước, thấy đỏ rồi mới code.

## Boundaries

- **Always:** input PDF là không tin cậy: giới hạn trang/px/thời gian/bộ nhớ; chạy qpdf như tiến trình riêng với `ArgumentList`; mật khẩu chỉ qua stdin; giữ file gốc; mọi binary tải bằng hash.
- **Ask first:** thêm package NuGet; đổi op-id đã phát hành; thêm thao tác ngoài bảng trên; nâng giới hạn kích thước bộ cài quá 80 MB.
- **Never:** thư viện AGPL/GPL liên kết vào tiến trình; commit binary; gửi PDF ra mạng; ghi mật khẩu ở bất cứ đâu; phá mật khẩu.

## Success Criteria

1. 12 ảnh (JPG + HEIC, có ảnh dọc EXIF 6) chọn cùng lúc → **một** PDF 12 trang, đúng thứ tự tự nhiên, đúng chiều; JPEG trong PDF trùng byte với ảnh gốc (trừ EXIF đã bỏ); qpdf `--check` không lỗi.
2. PDF 200 trang → JPG pages: 200 ảnh trong thư mục mới, cửa sổ tiến độ đếm trang, Cancel giữa chừng không để lại thư mục dở.
3. Merge 3 PDF → số trang bằng tổng; Split → mỗi trang một PDF; Rotate → trang 1 render đúng hướng.
4. Remove metadata → qpdf `--json` không còn Info (trừ Producer), XMP, tệp đính kèm, JavaScript.
5. Protect → không mở được khi không có mật khẩu, mở được với mật khẩu; mật khẩu không xuất hiện trong log, dòng lệnh, file tạm. Unlock sai mật khẩu → lỗi rõ, file khác vẫn xong.
6. Fixture PDF độc bị từ chối trong ≤ 10 giây (trang treo pdfium: ~10,5 s ở A4, T9b); bộ nhớ không vượt trần job của worker pdfium (2,5 GB), không vượt trần job của qpdf (256 MB + 2,5 × input, 1–3 GB), và PDF cần quá trần đó lỗi rõ chứ không ra output thiếu nội dung.
7. Bộ cài: có `pdfium.dll`, `qpdf.exe`, notices đủ; nâng cấp từ 0.1.0 giữ lựa chọn menu và kiểm cập nhật; gỡ sạch.
8. Khởi động lạnh `flevert.exe run` cho thao tác ảnh không chậm hơn v0.1.0 quá 10% (pdfium chỉ nạp khi cần).
9. Ba review bối cảnh trống không còn Critical/High/Required.

## Open Questions

1. Compress for sharing: thay ảnh trong PDF gọn bằng pdfium + qpdf (qpdf có API thay luồng qua JSON `--json-input`/`--update-from-json` từ v11) hay bỏ khỏi v0.2? Quyết ở bước Plan sau khi thử.
2. qpdf bản msvc64 kèm thư viện crypto nào (OpenSSL hay native)? Ảnh hưởng notices và kích thước. **Đã trả lời (F4):** OpenSSL 3.6.4 (libcrypto), cùng zlib 1.3.2 và libjpeg-turbo 3.2.0, liên kết tĩnh trong `qpdf30.dll` (phiên bản đọc trong binary và trong bản build v12.4.2 của qpdf); DLL runtime MSVC 14.51 là file riêng. Notices: ADR-0008.
3. Kiểm hash `qpdf.exe`/`pdfium.dll` lúc chạy có đáng không, khi thư mục cài đã được setup kiểm quyền (chỉ chủ và admin ghi được)? Mặc định: không, vì cùng mức tin cậy với `flevert.exe`.

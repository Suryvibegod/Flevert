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
| `pdf.split` | Split into pages | `.pdf` | 1 → N | thư mục `<tên> (split)\<tên> 001.pdf` … (tới 0.2.0: `(pages)`) |
| `pdf.rotate-right` / `-left` / `-180` | Rotate > 90° right / 90° left / 180° | `.pdf` | 1 → 1 | `<tên> (rotated).pdf` |
| `pdf.compress` | Compress > Lossless (tới 0.2.0: Compress) | `.pdf` | 1 → 1 | `<tên> (compressed).pdf`; không nhỏ hơn thì bỏ qua (skipped) |
| `pdf.compress-sharing` | Compress > For sharing (0.2.1) | `.pdf` | 1 → 1 | `<tên> (small).pdf`: ảnh > 150 DPI giảm còn 150 DPI, JPEG q80 |
| `pdf.strip-metadata` | Remove metadata | `.pdf` | 1 → 1 | `<tên> (no metadata).pdf` |
| `pdf.protect` | Protect with password | `.pdf` | 1 → 1 | `<tên> (protected).pdf`, AES-256 |
| `pdf.unlock` | Remove password | `.pdf` có mật khẩu | 1 → 1 | `<tên> (unlocked).pdf` |
| `pdf.extract-text` | Convert > Text (0.2.1) | `.pdf` | 1 → 1 | `<tên>.txt` (UTF-8 có BOM, CRLF, ngăn trang bằng form feed) |
| ~~`pdf.extract-images`~~, ~~`pdf.reverse`~~, ~~`pdf.repair`~~ | không có (v0.2.1, "Bố cục menu") | | | Repair thành ghi chú tự động (P4) |

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
- **Compress** (`pdf.compress`, T13, `src/Flevert.Pdf/PdfCompress.cs`): không mất dữ liệu, một lần qpdf `--object-streams=generate --compress-streams=y --recompress-flate --compression-level=9`: object không phải luồng gom vào object stream nén (PDF 1.5, qpdf tự nâng phiên bản), luồng chưa nén được nén Flate, luồng Flate được giải rồi nén lại ở mức 9 (thiếu `--recompress-flate` thì qpdf chép nguyên luồng Flate; đo trên nội dung trang 327 KB: mặc định của qpdf (mức 6) 62 450 byte, mức 9 59 906), luồng LZW/ASCII85/RunLength thành Flate (mức giải mã mặc định của qpdf). Ảnh JPEG không đổi (việc của Compress for sharing). Output không nhỏ hơn input (byte) → `OperationSkippedException` "This PDF is already compact: compressing it would not make it smaller.", không để lại gì (bỏ qua, không phải lỗi; ngưỡng đúng "không nhỏ hơn" của spec chứ không phải 5% như ảnh: không mất chất lượng nên nhỏ hơn chút nào cũng là lợi). Menu là `Compress` một mục (tiết kiệm một mục trong giới hạn 16) cho tới khi có `pdf.compress-sharing` (T15/T16), khi đó thành `Compress > Lossless` / `For sharing`. **Chữ ký số mất hiệu lực** (file viết lại).
- **Remove metadata** (`pdf.strip-metadata`, T12, `src/Flevert.Pdf/PdfStripMetadata.cs`, `MetadataRemoval.cs`): output `<tên> (no metadata).pdf` (cùng kiểu tên với thao tác ảnh). Bỏ, ở **mọi chỗ** trong file: Info (thay bằng chỉ `/Producer (Flevert)`, như PDF Flevert tạo; trailer không có Info hoặc Info viết thẳng trong trailer → Info mới); XMP (`/Metadata` của catalog, trang, ảnh, mọi object, và chính luồng XMP); dữ liệu riêng của ứng dụng (`/PieceInfo`, có thể chứa cả bản cũ của tài liệu); tệp đính kèm (cây tên `/EmbeddedFiles`, `/EF` của mọi file spec, luồng `/EmbeddedFile`, chú thích `/FileAttachment`); JavaScript (cây tên `/JavaScript`, mọi action `/S /JavaScript` ở bất cứ đâu: `/OpenAction`, `/AA` của tài liệu/trang/chú thích/trường form, link, bookmark, chuỗi `/Next`; và khóa `/JS` của action khác như Rendition). Giữ: nội dung trang (render giống hệt), bookmark, link (mất action JavaScript), form, cấu trúc. **Chữ ký số mất hiệu lực** (file đã đổi). Cách làm: qpdf không có tùy chọn đủ (`--remove-info` không giữ Producer, `--remove-metadata` chỉ bỏ XMP của catalog, không gì bỏ script), nên chạy qpdf hai lần: `--json-output=2 --json-stream-data=none` mô tả mọi object (không có dữ liệu luồng) ra file JSON; Flevert đọc từng mục một (`QpdfJsonFile`, bộ nhớ theo object lớn nhất, sâu tới 600 vì qpdf 12.4.2 từ chối lồng quá 500) và viết bản cập nhật chỉ gồm các object đổi (object bị bỏ → null, mọi tham chiếu tới nó bị gỡ khỏi dict/mảng); `--update-from-json=<file>` (đi qua `@-` như đường dẫn) ghi PDF mới. Hai file JSON lặp lại metadata nên nằm trong thư mục riêng tạo kèm quyền chỉ user, SYSTEM, Administrators dưới `%TEMP%` (tạo thất bại nếu tên đã có), **không bao giờ cạnh input**, xóa khi thao tác xong. PDF chỉ có mật khẩu owner: giữ mã hóa. Chưa bỏ: script trong form XFA, ảnh thu nhỏ trang (`/Thumb`), `/ID` của trailer.
- **PDF có chữ ký số** (T18): xoay, merge, split ghi PDF mới nên chữ ký (phủ đúng từng byte của file gốc, `/ByteRange`) không còn hợp lệ. Thao tác vẫn chạy, và thông báo nói: "Done: <tên>. Signed PDF: the digital signature is not valid in the output." (một output; nhiều output: "Done: N files, K with a note. Details: <log>"); dòng log của file có thêm cột ghi chú. Nhận biết bằng khóa `/ByteRange` trong byte của file (`PdfSignature`; từ điển chữ ký không thể nằm trong object stream nén); đây là ghi chú, không phải quyết định bảo mật. Core: `OutputNote.Add` (ADR-0007 không đổi hợp đồng `IOperation`). PDF → ảnh không có ghi chú.
- **PDF có mật khẩu mở**: mọi thao tác trừ `pdf.unlock` báo lỗi rõ "This PDF needs a password: use Remove password first" (không hỏi mật khẩu cho từng thao tác ở v0.2).
- **Render** (`to-jpg`, `to-png`): 150 DPI mặc định (đủ đọc trên điện thoại, nhỏ), giới hạn ở dưới. JPEG q90, 4:2:0 (file nhỏ hơn; ImageMagick tự để 4:4:4 từ q90); PNG không mất dữ liệu, không kênh alpha (trang vẽ trên nền trắng). Ảnh ghi DPI thật của trang (in đúng khổ). Một trang pdfium không đọc được → cả file lỗi, thông báo nêu số trang (thư mục ghi nguyên vẹn hoặc không có gì; thiếu một trang mà trông như đủ thì tệ hơn). PDF không có trang → lỗi "This PDF has no pages.".
- **Đọc PDF trong tiến trình worker** (T9b, ADR-0008 mục "Reading untrusted PDFs in a worker"): `pdf.to-jpg/png` (và mọi thao tác sau này mở PDF lạ bằng pdfium) chạy `flevert.exe pdf-worker` (lệnh ẩn, không có trong usage) qua `ExternalTool`: trần bộ nhớ 2,5 GB, không được tạo tiến trình khác, chết theo Flevert. Dòng lệnh chỉ có `pdf-worker`; yêu cầu (đường dẫn, định danh file, thư mục, định dạng, DPI) đi qua stdin dạng UTF-16 thô ngăn bằng U+0000 (tên có surrogate lẻ vẫn đúng). Worker mở lại đường dẫn đã ghim (cha giữ file, chỉ chia sẻ đọc) và từ chối nếu định danh file (`FILE_ID_INFO`) khác cái cha gửi. Worker render từng trang vào **một** bộ đệm dùng lại, mã hóa thẳng vào file trong thư mục `.partial` của cha (`CreateNew`), báo từng dòng: `pages N`, `page n rộng cao`, `busy n` (lúc mã hóa, tối đa 2 lần/giây), `wrote n`, `done N`, hoặc dòng cuối `error <mã> <thông báo>`. Cha đổi `page` thành "page n of N" cho cửa sổ tiến độ (thanh tiến độ chạy trong một file). **Watchdog**: một bước không có dấu hiệu tiến triển quá 10 s + 0,25 s mỗi megapixel của trang (A4 150 DPI: 10,5 s; trang 100 MP: 35 s; mở file: 10 s + 1 s mỗi 100 MB) → giết worker, lỗi "Page n of this PDF took too long to render: it is too complex, or made to stall PDF readers."; Cancel = giết worker rồi xóa thư mục tạm. **Ngân sách cả file** (T18, audit bảo mật cuối): khi worker báo `pages N`, cả lần chạy được tối đa 60 s + 2 s mỗi trang (2 000 trang: 68 phút; trang trung thực ở 150 DPI < 0,1 s, trang 100 MP ~2,5 s); quá → giết worker, lỗi `TooSlow` "This PDF takes too long to convert: its pages are too complex, or made to stall PDF readers." (trước đó 2 000 trang, mỗi trang vừa dưới cửa sổ ~10 s, giữ worker ~5,5 giờ). Worker chết không nói gì → "Flevert's PDF reader stopped on page n of this PDF without finishing: the PDF may be damaged, or made to crash PDF readers."; vượt trần → thông báo "needs more memory" như qpdf (nêu 2,5 GB); mật khẩu, không phải PDF: thông báo chung. Chủ lô không nạp pdfium hay ImageMagick cho thao tác này nên không còn xin `MemoryBudget` theo trang (trần của worker giới hạn bộ nhớ). Ảnh → PDF (`PdfBuilder`) vẫn chạy pdfium trong tiến trình: input là ảnh đã qua Magick.

### Mật khẩu (protect / unlock)

- Hộp thoại WinForms nhỏ: ô mật khẩu che ký tự, ô nhập lại (protect), nút OK/Cancel; Cancel = file đó bị bỏ qua. Một lô nhiều file: hỏi **một lần** cho cả lô ("Use this password for all 5 files").
- Mật khẩu **không bao giờ** nằm trên dòng lệnh, trong log, trong biến môi trường: truyền cho qpdf qua stdin (`@-` hoặc `--password-file=-`), xóa khỏi bộ nhớ khi xong (mảng `char[]`, ghi đè 0).
- Protect: AES-256 (`--encrypt <user> <owner> 256`), owner password = user password; không đặt hạn chế in/sao chép (dễ bị hiểu sai là bảo mật).
- Unlock sai mật khẩu: lỗi "Wrong password" cho file đó, các file khác vẫn chạy.

Đã quyết khi làm (T14, `src/Flevert.Pdf/PdfPassword.cs`, `PdfProtect.cs`, `src/Flevert.Cli/PasswordDialog.cs`; probe với qpdf 12.4.2):

- **Hộp thoại** "Flevert — Protect with password" / "Flevert — Remove password": câu nêu tên file, "Password:" + ô che ký tự, "Type it again:" + ô thứ hai (chỉ protect), ô đánh dấu sẵn "Use this password for all N files" (chỉ hiện khi N > 1), nút "Protect"/"Remove password" (Enter) và "Cancel" (Esc); nút không có phím tắt chữ (chữ cái trên nút đang focus sẽ bấm nó). Luôn trên cùng như cửa sổ tiến độ; cửa sổ tiến độ không hiện khi hộp thoại đang mở, và hiện 2 s sau câu trả lời nếu lô còn chạy. Tên trợ năng: "Password", "Password again", "Actions" (UI Automation đọc được, kiểm thật).
- **Kiểm trước khi nhận** (`PasswordRules`, test không cần UI): rỗng → "Type a password."; hai ô khác nhau → "The two passwords are not the same."; quá **127 byte UTF-8** → từ chối (AES-256 cắt ở 127 byte; probe: qpdf ghi được PDF với mật khẩu dài hơn nhưng sau đó **không** mở được bằng chính mật khẩu đó hay 127 byte đầu); xuống dòng, NUL, surrogate lẻ → từ chối (một đối số mỗi dòng `@-`; surrogate lẻ không có dạng UTF-8). Dấu cách đầu/cuối **là** một phần của mật khẩu (probe: qpdf không cắt dòng `@-`). Lỗi hiện dưới ô, đọc ra bằng live region, focus về ô cần sửa.
- **Một câu hỏi cho cả lô** (`BatchPasswords`): mỗi lần một câu hỏi, file khác chờ. N = số file của lô chưa xong và chưa có mật khẩu (tăng khi hộp thoại đang mở nếu file của lựa chọn tới thêm). Giữ dấu → câu trả lời dùng cho N file: file được hỏi và N−1 file kế tiếp cần mật khẩu; file tới sau đó (lựa chọn khác nhập vào lô đang chạy) được **hỏi lại**, không dùng ngầm mật khẩu cũ. Bỏ dấu → hỏi từng file. **Cancel** → file đó bị bỏ qua (skipped, "No password was given."); Cancel khi dấu đang giữ → bỏ qua cả N file. Chấp nhận: file được tính trong N nhưng lỗi trước khi hỏi (đã mã hóa, không phải PDF) nhường lượt cho một file tới sau.
- **Kiểm PDF trước khi hỏi** (`Qpdf.GetEncryptionAsync`, `qpdf --requires-password`, mã thoát có tài liệu: 0 cần mật khẩu, 2 không mã hóa, 3 mã hóa nhưng mở được; mã 2 kèm dòng lỗi `qpdf: …` = không đọc được): không ai gõ mật khẩu cho một file rồi mới thấy lỗi. Protect: cần mật khẩu → "This PDF needs a password: use Remove password first"; mở được nhưng có mã hóa (tác giả hạn chế in/sao chép) → lỗi "This PDF is already encrypted: its author limited what may be done with it, and Flevert leaves that as it is." (protect lại sẽ bỏ các hạn chế đó). Unlock: không mã hóa → skipped "This PDF has no password."; mở được không cần mật khẩu → skipped "This PDF opens without a password." (gỡ hạn chế của tác giả mà không biết mật khẩu owner là phá khóa, ngoài phạm vi). Không phải PDF → lỗi, không hỏi.
- **Đường đi của mật khẩu**: ô nhập (edit control `ES_PASSWORD`) → `GetWindowTextW` vào `char[]` (không qua `Control.Text`, không bao giờ thành `string` trong mã của Flevert) → `PdfPassword` (giữ `char[]`, `Dispose` ghi 0) → `Qpdf` mã hóa UTF-8 **thẳng vào** mảng byte của stdin: các dòng `--user-password=<mk>`, `--owner-password=<mk>`, `--bits=256`, `--` (protect; mật khẩu phải đứng **trước** `--bits`, probe) hoặc `--password=<mk>` (unlock, và lần hỏi `--requires-password` sau lỗi), rồi hai đường dẫn, tất cả trong cùng luồng `@-`; dòng lệnh chỉ có `--encrypt @-` / `--decrypt @-` → `ExternalTool` ghi mảng thẳng vào pipe stdin (không bản sao) → mảng ghi 0 khi qpdf thoát. `char[]` ghi 0 khi file xong (mật khẩu một file), khi file cuối của N xong (mật khẩu chung), hoặc khi lô kết thúc (flevert.exe dispose thao tác). Ô nhập được xóa khi hộp thoại đóng; bộ nhớ riêng của edit control và của Windows nằm ngoài tầm (best effort). `--password-file=-` **không** dùng được: qpdf đọc `@-` tới hết stdin, dòng mật khẩu thành đối số vị trí (probe: "no output file may be given for this option").
- **qpdf lặp lại mật khẩu**: đối số qpdf không nhận ra được in lại trong lỗi (probe: `--user-password` sau `--bits=256` → `unrecognized argument --user-password=<mk>`). Output nào của qpdf chứa mật khẩu được thay **cả khối** bằng "(qpdf's message was not kept: it could hold the password)" trước khi thành ngoại lệ hay log (thay riêng mật khẩu sẽ lộ vị trí của nó, tức lộ mật khẩu); `--show-encryption` (in mật khẩu user ra stdout) không bao giờ được dùng.
- **Protect** ra R6, AESv3 cho stream, string và file, owner = user, mọi quyền "allowed" (kiểm bằng `qpdf --show-encryption` trong test). Mật khẩu là byte UTF-8 như đã gõ (không SASLprep, giống qpdf nhận từ dòng lệnh); tiếng Việt, emoji, dấu nháy, `@`/`-` ở đầu, `=`, `\`, tab mở được bằng qpdf và pdfium (test). **Unlock** sai mật khẩu → `PdfError.WrongPassword` "Wrong password: it does not open this PDF." cho file đó (sau lỗi, `--requires-password` với chính mật khẩu đó trả 0), file khác vẫn chạy. Unlock bằng mật khẩu user (khác owner) cũng bỏ mọi hạn chế: qpdf không áp hạn chế, và người mở được file đã đọc được nội dung.
- **Không có cửa sổ** (`FLEVERT_HEADLESS=1`, cũng là thao tác trong catalog khi chưa được gán hộp thoại): mỗi file lỗi **ngay** "Flevert asks for a PDF password only in a window, and it runs without windows here (FLEVERT_HEADLESS), so this PDF was left as it is.", mã thoát 1; không đọc stdin, không chờ. Lý do: headless là móc cho test và tự động hóa; đọc mật khẩu từ stdin sẽ là kênh bí mật thứ hai cần bảo vệ và tài liệu hóa, khuyến khích để mật khẩu trong script, và trong một lô chỉ stdin của leader được đọc (follower thì không), nên kết quả phụ thuộc tiến trình nào làm leader; lỗi rõ thì không bao giờ treo.
- `MaxParallel = 4` như xoay; qpdf chạy với giới hạn job, môi trường và `\\?\` như mọi lần gọi qpdf (mục 8).

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
7. **Menu**: `.pdf` thêm vào danh sách đuôi; ảnh có thêm mục `Combine into PDF`. Thứ tự nhóm trong menu ảnh: Convert, Compress, Combine into PDF, Remove metadata (`MenuOperations.All`, danh sách chung của `Program.cs` và test menu). Menu `.pdf` theo thứ tự bảng thao tác: Convert, Merge PDFs, Split into pages, Rotate, Compress, Remove metadata, Protect with password, Remove password (13 mục, dưới giới hạn 16). Explorer chỉ hiện 16 mục đầu của menu (core.md, mục Menu): để `.heic`/`.heif` không mất `Remove metadata`, ảnh HEIC/HEIF không còn `Convert > GIF` (image.md).
8. **Gọi qpdf** (đã kiểm với qpdf 12.4.2, T3): tùy chọn (hằng của Flevert) trên dòng lệnh, **hai đường dẫn qua stdin bằng `@-`** (mỗi dòng một đối số). Lý do: C runtime của qpdf mở rộng ký tự đại diện trong đối số dòng lệnh (`r*.pdf` thành nhiều file), nên dấu `?` của đường dẫn `\\?\…` (runner dùng cho đường dẫn dài hơn 260 ký tự) biến thành tên khác; dòng đọc từ `@-` giữ nguyên, và qpdf mở được `\\?\` dài, có Unicode. Cả hai đường dẫn **luôn** ở dạng `\\?\` (`\\?\UNC\` cho thư mục chia sẻ; `\\.\`, `//?/`, `\??\` đổi về `\\?\`), chuẩn hóa bằng `Path.GetFullPath` trước (tiền tố tắt chuẩn hóa của Windows), vì qpdf không mở được đường dẫn quá 260 ký tự ở dạng khác, và output tạm dài hơn input 51 ký tự; thông báo của qpdf trong log bỏ tiền tố. Dòng `@-` mã hóa UTF-8 nghiêm ngặt: tên có surrogate lẻ (NTFS cho phép) → lỗi "rename it" trước khi chạy qpdf, không bao giờ thay bằng U+FFFD (sẽ là tên file khác). qpdf không có dấu kết thúc tùy chọn (`--` không phải), nên đường dẫn phải tuyệt đối (không bắt đầu bằng `-` hay `@`) và không chứa xuống dòng; tùy chọn có `*`/`?` hoặc bắt đầu bằng `@` (file tùy chọn) bị từ chối. Môi trường của qpdf: `OPENSSL_CONF` trỏ tới `openssl.cnf` rỗng cạnh `qpdf.exe`, `OPENSSL_MODULES`/`OPENSSL_ENGINES` trỏ thư mục đó, không có `OPENSSL_CONF_INCLUDE` và mọi biến `QPDF_*` (OpenSSL trong qpdf đọc cấu hình kể cả khi chỉ xoay, cấu hình nạp được DLL). Mã thoát: 0 xong; 3 (cảnh báo: qpdf đã sửa gì đó, ví dụ trang thiếu `/Resources` mà mọi trình xem vẫn mở) **tính là xong**, cảnh báo không hiện cho người dùng; 2 lỗi: hỏi lại `qpdf --requires-password` (mã thoát có tài liệu, chữ "invalid password" thì có thể đổi) → thông báo mật khẩu; stderr nhắc file output → lỗi ghi file; còn lại "This file is not a PDF, or it is damaged." (chi tiết của qpdf nằm trong log). Cấp phát bị từ chối (job báo vượt trần, hoặc stderr có `: bad allocation`/`: std::bad_alloc`) → "This PDF needs more memory than Flevert allows for it (<trần>): it is too large or too complex to process." **bất kể mã thoát** (qpdf thiếu bộ nhớ có thể coi object là null rồi thoát 3 với output thiếu nội dung); thành công mà stderr quá 1 MB (phần sau không đọc) → không tin, lỗi "not a PDF, or damaged". Timeout: 2 phút + 1 giây mỗi MB. File > 2 GB bị từ chối trước khi chạy qpdf. **Giới hạn 2 000 trang (T18)** cho mọi thao tác qpdf: `Qpdf.CountPagesAsync` chạy `--show-pages` (đi hết cây trang như mọi thao tác qpdf; `--show-npages` chỉ in `/Count` của gốc, PDF có thể ghi sai: 100 000 trang dưới `/Count 1` từng lọt qua và split ghi 100 000 file trong 2 phút), dừng qpdf ở trang thứ 2 001 → "This PDF has more than 2000 pages; Flevert works with at most 2000."; xoay và split đếm trước, merge đếm từng input (lỗi nêu file) và giới hạn **tổng** 2 000 trang ("Together these PDFs have N pages; Flevert makes PDFs of at most 2000."). Xoay, merge, split chạy với `--stream-data=preserve` (luồng chép nguyên, không giải nén hay nén lại: luồng 10 KB hai lớp Flate nở ra 4 GB từng làm xoay mất 15–20 s; `--decode-level=none` một mình vẫn 16 s). qpdf hết timeout → `TooSlow` "This PDF took too long to process: …"; mã thoát qpdf không bao giờ trả (crash) → `ReaderFailed` "Flevert's PDF tool stopped on this PDF without finishing: …"; bộ xử lý mã hóa lạ ("encryption dictionary"/"unsupported encryption" trong lỗi) → `UnsupportedEncryption`, cùng lời với pdfium. Nhiều input (merge): mọi đường dẫn và dấu `--` kết thúc `--pages` đi qua cùng luồng `@-`; giới hạn cỡ, trần bộ nhớ và timeout tính theo tổng; lỗi do một input → `BatchInputException` nêu nó, lỗi bên trong là `PdfException` như khi chạy file đó một mình. Mật khẩu (T14): các dòng `--…password=<mk>` đi trong cùng luồng `@-`, trước đường dẫn, mã hóa thẳng từ `char[]`; output của qpdf có mật khẩu bị thay cả khối (mục "Mật khẩu").

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
src/Flevert.Pdf/             → thao tác PDF (IOperation: PdfRotate, PdfSplit, PdfToImages, PdfCompress, PdfStripMetadata; IBatchOperation: ImagesToPdf, PdfMerge), PdfBuilder + JpegSegments (ảnh → PDF), MetadataRemoval + QpdfJsonFile (bản cập nhật JSON cho qpdf), PrivateFolder, Pdfium (P/Invoke), Qpdf (ExternalTool), PdfWorker + PdfRenderWorker (đọc PDF trong `flevert.exe pdf-worker`), MenuOperations (thứ tự menu ảnh + PDF)
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
- **Mật khẩu không rò**: test chạy protect với mật khẩu đánh dấu, rồi tìm chuỗi đó trong log, dòng lệnh tiến trình con (WMI), file tạm: không có. Đã làm (T14, `PasswordLeakTests`): protect PDF 100 000 trang rồi unlock nó, và unlock sai mật khẩu, qua `Runner` + `RunLog` như flevert.exe; trong lúc chạy đọc dòng lệnh **và khối môi trường** của mọi qpdf con từ PEB của nó (`NtQueryInformationProcess` + `ReadProcessMemory`, thứ WMI và Task Manager đọc), quét file `.partial`; sau đó quét log, thư mục output và mọi file mới trong `%TEMP%` (byte UTF-8 và UTF-16 của phần ASCII của dấu). Test khẳng định đã thấy qpdf `--encrypt` và `--decrypt` (không xanh vì không nhìn thấy gì). Đối chứng âm: đưa mật khẩu lên dòng lệnh, vào môi trường, vào thông báo lỗi, bỏ thay thế output của qpdf → mỗi cái làm test tương ứng đỏ.
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
6. Fixture PDF độc bị từ chối hoặc xử lý trong ≤ 10 giây (bảng 31 fixture × 5 thao tác ở T18, `PdfHardeningTests`; trang treo pdfium: ~10,5 s ở A4, T9b; PDF một triệu object 53 MB: xoay ~13 s, việc thật tỉ lệ với cỡ, dưới timeout 2 phút + 1 s/MB); bộ nhớ không vượt trần job của worker pdfium (2,5 GB), không vượt trần job của qpdf (256 MB + 2,5 × input, 1–3 GB), và PDF cần quá trần đó lỗi rõ chứ không ra output thiếu nội dung.
7. Bộ cài: có `pdfium.dll`, `qpdf.exe`, notices đủ; nâng cấp từ 0.1.0 giữ lựa chọn menu và kiểm cập nhật; gỡ sạch.
8. Khởi động lạnh `flevert.exe run` cho thao tác ảnh không chậm hơn v0.1.0 quá 10% (pdfium chỉ nạp khi cần).
9. Ba review bối cảnh trống không còn Critical/High/Required.

## Open Questions

1. Compress for sharing: thay ảnh trong PDF gọn bằng pdfium + qpdf (qpdf có API thay luồng qua JSON `--json-input`/`--update-from-json` từ v11) hay bỏ khỏi v0.2? Quyết ở bước Plan sau khi thử.
2. qpdf bản msvc64 kèm thư viện crypto nào (OpenSSL hay native)? Ảnh hưởng notices và kích thước. **Đã trả lời (F4):** OpenSSL 3.6.4 (libcrypto), cùng zlib 1.3.2 và libjpeg-turbo 3.2.0, liên kết tĩnh trong `qpdf30.dll` (phiên bản đọc trong binary và trong bản build v12.4.2 của qpdf); DLL runtime MSVC 14.51 là file riêng. Notices: ADR-0008.
3. Kiểm hash `qpdf.exe`/`pdfium.dll` lúc chạy có đáng không, khi thư mục cài đã được setup kiểm quyền (chỉ chủ và admin ghi được)? Mặc định: không, vì cùng mức tin cậy với `flevert.exe`.

---

# v0.2.1: phần còn lại của module pdf

Trạng thái: **đã duyệt** (2026-10-20); **đã làm** trên nhánh `feat/v0.2.1-pdf` (2026-10-20, tiến độ và kết quả trong `tasks/pdf/todo.md`, mục v0.2.1), chờ maintainer review rồi gộp. Làm trên nhánh `feat/v0.2.1-pdf` từ `main` sau v0.2.0; phát hành **trước** v0.3.0. Mọi quy tắc ở trên (giới hạn, qpdf qua `@-`, worker pdfium, mật khẩu, tên output, `OutputPath`) vẫn áp dụng; mục này chỉ nói phần thêm và phần đổi.

## Objective (v0.2.1)

Làm xong những gì v0.2.0 hoãn lại để người dùng gửi được PDF scan nặng mà không lên web, lấy được chữ trong PDF, và đóng các mục review còn mở của v0.2.0. Không thêm module mới, không đổi hành vi đã phát hành trừ những chỗ ghi ở "Thay đổi hành vi".

## Bố cục menu `.pdf` (bắt buộc ≤ 16 mục)

Explorer chỉ hiện **16 mục đầu** của cây menu dưới `Flevert` (tính cả mục mở submenu) và lặng lẽ bỏ phần còn lại (core.md, mục Menu). v0.2.0 đã dùng 13. Không thể thêm đủ 5 thao tác dự định (Compress for sharing, Reverse, Repair, Extract text, Extract images), nên v0.2.1 dùng bố cục sau (16 mục, người dùng duyệt 2026-10-20):

```
Convert ▸ JPG pages · PNG pages · Text          (4)
Compress ▸ Lossless · For sharing               (3)
Rotate ▸ 90° right · 90° left · 180°            (4)
Merge PDFs · Split into pages · Remove metadata
Protect with password · Remove password         (5)
```

- **Repair** không có mục riêng: mọi thao tác ghi PDF mới bằng qpdf đã dựng lại bảng xref; thay vào đó Flevert **báo** khi đã phải sửa file (yêu cầu P4), và README nói "Compress > Lossless cũng sửa PDF hỏng nhẹ".
- **Reverse page order**: chỉ có nếu spike Compress for sharing thất bại (khi đó `Compress` vẫn là một mục, còn chỗ cho `Reverse page order` ngay sau `Split into pages`, tổng 15).
- **Extract images**: ra ngoài v0.2.1 (không đủ chỗ; ít người dùng). Ghi lại để xét ở bản sau, có thể bằng một cây menu thứ hai.
- Test menu (`MenuOperations`, test số mục) phải khẳng định menu `.pdf` ≤ 16 mục, như đã làm cho ảnh.

## Yêu cầu

### P1. Compress for sharing (`pdf.compress-sharing`, menu `Compress > For sharing`), có spike trước

Người dùng: PDF scan 20 trang 25 MB, cần gửi qua Zalo/email.

- Output `<tên> (small).pdf`. Ảnh nào có **độ phân giải hiệu dụng** (số pixel ÷ kích thước hiển thị trên trang, tính bằng inch) > 150 DPI được thu về 150 DPI và mã hóa lại JPEG q80 (màu hoặc xám giữ nguyên số kênh). Ảnh ≤ 150 DPI, ảnh 1-bit (CCITT, JBIG2, `/ImageMask`), chữ, hình vector: **không đổi**. Ảnh có `/SMask` (độ trong suốt) giữ mask; mask cũng thu theo cùng tỉ lệ nếu lớn hơn 150 DPI.
- Một ảnh dùng ở nhiều chỗ với kích thước khác nhau: lấy kích thước hiển thị **lớn nhất** (không làm mờ chỗ hiển thị lớn).
- Kết quả không nhỏ hơn input ít nhất 5% → `OperationSkippedException` "This PDF is already small: compressing it for sharing would not make it much smaller." (như ảnh), không để lại gì.
- **Không thư viện AGPL/GPL** (Boundaries). Hướng làm cho spike: worker pdfium (đã có, `flevert.exe pdf-worker`) liệt kê object ảnh của từng trang và vị trí hiển thị (`FPDFPage_GetObject`, `FPDFPageObj_GetBounds`/ma trận, `FPDFImageObj_GetImagePixelSize`), giải mã ảnh **trong worker** (`FPDFImageObj_GetBitmap`; dữ liệu ảnh từ PDF lạ không được giải mã trong tiến trình chính), thu nhỏ và mã hóa JPEG trong worker, ghi file vào thư mục tạm riêng; tiến trình chính dựng bản cập nhật JSON cho qpdf (`--update-from-json`, như Remove metadata) thay luồng ảnh: `/Filter /DCTDecode`, `/Width`, `/Height`, `/BitsPerComponent 8`, `/ColorSpace` `DeviceRGB`/`DeviceGray`, bỏ `/DecodeParms` và `/Decode` cũ.
- **Spike (task đầu tiên, tối đa một ngày làm):** chứng minh trên fixture "scan 20 trang, JPEG 300 DPI, A4" rằng: output ≤ 40% cỡ input; render trang 1 của input và output ở 150 DPI khác nhau trung bình ≤ 3/255 mỗi kênh; ≤ 2 s/trang trên máy dev; PDF có ảnh dùng lại, ảnh có SMask, ảnh Flate, ảnh CMYK render giống input. **Không đạt → bỏ P1**, ghi kết quả spike vào `tasks/pdf/todo.md` và spec, `Compress` giữ một mục, thêm P2.
- Giới hạn như mọi thao tác: 2 000 trang, 2 GB, worker 2,5 GB, ngân sách thời gian cả file; PDF có mật khẩu mở → lỗi như hiện có; chữ ký số → ghi chú "not valid in the output".

### P2. Reverse page order (`pdf.reverse`), chỉ khi P1 bị bỏ

- qpdf `--empty --pages <input> z-1 -- <output>` qua `@-`; `--stream-data=preserve`; output `<tên> (reversed).pdf`; đếm trang trước (2 000); chữ ký số → ghi chú. PDF một trang → vẫn ghi output (cùng hình dạng kết quả).

### P3. Extract text (`pdf.extract-text`, menu `Convert > Text`)

- pdfium trong worker (`FPDFText_LoadPage`, `FPDFText_GetText`), output `<tên>.txt`, UTF-8 **có BOM** (Notepad cũ đọc đúng tiếng Việt), xuống dòng `\r\n`, giữa các trang một ký tự form feed (`\f`). Thứ tự chữ là thứ tự pdfium trả về.
- PDF không có chữ nào (scan) → skipped "This PDF has no text to extract: it may be a scanned document (Flevert does not recognise text in pictures)." Không làm OCR.
- Giới hạn: output tối đa 100 MB (quá → lỗi rõ, không ghi file dở); ngân sách thời gian của worker như render. Tiến độ "page N of M".

### P4. Báo khi PDF đã phải sửa (thay cho Repair; review DD-M7)

- Mọi thao tác ghi PDF bằng qpdf: mã thoát 3 (qpdf đã sửa gì đó khi đọc) → thao tác vẫn xong và thêm `OutputNote` "Flevert had to repair this PDF while reading it: check that the pages look right." **Và** kiểm output đã sửa: không mở được, hoặc số trang khác số qpdf đã đếm ở input → **lỗi**, không giữ output ("This PDF is damaged: the result would be missing pages.").
- Giới hạn (review trước phát hành, maintainer quyết 2026-10-20): số trang input cũng do qpdf đếm (`--show-pages`), qua **cùng** lần sửa, nên trang mà chính lần sửa làm mất **không phát hiện được** (mọi trình đọc sửa theo cùng cách). Không tự đếm trang độc lập (dễ vỡ); ghi chú yêu cầu người dùng xem lại.
- Không áp dụng cho Remove password và Protect (vẫn ghi chú, không so trang vì đã đếm qua mật khẩu).

### P5. Áp giới hạn T18 cho mọi thao tác ghi PDF (mục còn mở của T18)

- `pdf.compress`, `pdf.strip-metadata`, `pdf.protect`, `pdf.unlock` (sau khi có mật khẩu) đếm trang bằng `Qpdf.CountPagesAsync` (2 000) và thêm ghi chú chữ ký số (`PdfSignature`, `OutputNote`) như xoay/merge/split.
- `pdf.compress` cố ý giải nén luồng: fixture bom nén đôi phải cho kết quả ≤ 10 s **hoặc** lỗi `TooSlow`/`NeedsTooMuchMemory` rõ; không treo tới timeout 2 phút.

### P6. Sửa các mục review còn mở của v0.2.0 (`tasks/pdf/todo.md`)

1. **S3:** tiến trình của lựa chọn còn chạy nhưng tới muộn → thông báo "did not reach Flevert in time", không nói "its process ended".
2. **S4:** hủy một lần ghép (Combine/Merge) rồi file của lô đó tới muộn → không hiện thêm balloon "left out" (ghi dấu "cancelled" vào bản ghi đóng lô).
3. **Split** ra thư mục `<tên> (split)\<tên> 001.pdf` thay cho `<tên> (pages)` (trùng tên với Convert > JPG/PNG pages). Ghi ở CHANGELOG mục **Changed**.
4. **Merge**: lỗi quá 2 000 trang sau khi đếm lại output nói "Together these PDFs have more than 2000 pages…" (không phải lời của một file); probe mật khẩu hết giờ trong merge nêu file.
5. **Test còn thiếu**: đếm lại trang của merge; guard `secret` + `outputLine` của `Qpdf.RunWithLinesAsync`; lỗi I/O của bước kiểm chữ ký trong merge nêu file.
6. **SA-L1:** PDF → ảnh: trước khi render, ước lượng output (tổng pixel các trang ở DPI đã chọn × 0,5 byte cho JPG, × 1 byte cho PNG, cộng 64 MB để dành) và so với chỗ trống ổ đích; thiếu → lỗi "There is not enough free space…"; tổng pixel > 5 000 MP → lỗi "too large to convert". (Ban đầu 3 byte/px cho PNG và trần 4 000 MP; review trước phát hành: từ chối cả chuyển đổi vừa đĩa và PDF A4 ~1 835–2 000 trang mà 0.2.0 chuyển được; maintainer đổi 2026-10-20: 2 000 trang A4 ở 150 DPI là 4 350 MP.)

### P7. Bảo trì

1. Hai PR Dependabot (`Microsoft.NET.Test.Sdk` 18, `xunit.runner.visualstudio` 4): thử trên nhánh; gộp nếu ba project test xanh trên CI, không thì đóng kèm lý do.
2. Binary phát hành không chứa đường dẫn của máy build: bật `ContinuousIntegrationBuild` trên CI (đường dẫn thành `/_/…`); test hoặc bước CI kiểm `flevert.dll` không chứa `D:\a\`.

## Thay đổi hành vi so với v0.2.0

- Menu `.pdf` theo bố cục ở trên (`Compress` thành submenu nếu P1 đạt; thêm `Convert > Text`).
- Split ghi vào `<tên> (split)`.
- Thao tác ghi PDF có thể thêm ghi chú "had to repair" và từ chối kết quả thiếu trang.

## Commands, Project Structure, Code Style

Như phần v0.2 ở trên. Test chạy **lần lượt** từng project (`dotnet test tests/Flevert.Core.Tests`, rồi `Flevert.Image.Tests`, rồi `Flevert.Pdf.Tests`), không chạy `Flevert.sln` một lần. File mới: `src/Flevert.Pdf/PdfCompressSharing.cs`, `PdfExtractText.cs` (và `PdfReverse.cs` nếu P2); việc của worker thêm vào `PdfRenderWorker`/`PdfWorker` theo cùng giao thức dòng (thêm loại yêu cầu, không đổi dòng cũ).

## Testing Strategy (v0.2.1)

- Test viết trước, thấy đỏ rồi mới code (ghi lỗi đỏ vào todo).
- Fixture mới sinh bằng script (Python thư viện chuẩn, như `make_pdf_fixtures.py`) và ghi nguồn gốc trong `fixtures/README.md`: scan 20 trang JPEG 300 DPI; ảnh dùng lại hai cỡ; ảnh có SMask; ảnh Flate; ảnh CMYK; PDF có chữ tiếng Việt (font nhúng); PDF chỉ ảnh (không chữ); PDF hỏng xref nhẹ (qpdf sửa được, mã 3).
- Mọi thao tác mới chạy qua bảng PDF độc `MaliciousPdf` (31 fixture) như T18: ≤ 10 s hoặc lỗi rõ, không còn file tạm.
- Kiểm nội dung: render so màu tại điểm mẫu; text đúng thứ tự trang, có `\f`; số trang; cỡ file.
- Tiến trình thật: `flevert.exe run pdf.compress-sharing <file>` và `pdf.extract-text` headless; menu bằng MenuProbe (≤ 16 mục, đúng thứ tự).

## Boundaries (v0.2.1)

- **Always:** như v0.2; dữ liệu ảnh của PDF lạ chỉ giải mã trong worker; mọi thao tác ghi PDF đếm trang và ghi chú chữ ký; mỗi commit một thay đổi logic, build + format + ba project test xanh trước commit.
- **Ask first:** thêm NuGet hoặc binary mới; đổi op-id đã phát hành; menu khác bố cục đã duyệt; bỏ P1 khi spike chưa chạy xong.
- **Never:** đẩy thẳng vào `main`; sửa `Directory.Build.props` (version), `tasks/HANDOFF.md`, mục đã phát hành của CHANGELOG (chỉ thêm dòng vào `[Unreleased]`); thư viện AGPL/GPL; OCR hay gửi file ra mạng.

## Success Criteria (v0.2.1)

1. Menu `.pdf` thật (MenuProbe) đúng bố cục đã duyệt, ≤ 16 mục.
2. P1 (nếu giữ): fixture scan 20 trang → output ≤ 40% cỡ, khác biệt render ≤ 3/255, ≤ 2 s/trang; PDF đã nhỏ → skipped. Nếu bỏ: kết quả spike ghi lại, P2 xong.
3. P3: PDF có chữ tiếng Việt → `.txt` UTF-8 BOM đúng chữ, đúng thứ tự trang, `\f` giữa trang; PDF scan → skipped.
4. P4: PDF hỏng nhẹ → output + ghi chú "had to repair"; output đã sửa không mở được hoặc khác số trang qpdf đếm ở input → lỗi, không có output (trang mất do chính lần sửa thì không phát hiện được, xem P4).
5. P5, P6: mỗi mục có test đỏ trước rồi xanh; Split ra `(split)`.
6. P7: Dependabot xử lý xong; `flevert.dll` của bản CI không chứa `D:\a\`.
7. CI xanh 3 lần; `test-installer.ps1` đạt, kể cả kịch bản nâng cấp từ 0.2.0 (đã ghim).
8. Ba review bối cảnh trống (do maintainer chạy khi nhận PR) không còn Critical/High/Required.

## Đã quyết (v0.2.1, người dùng duyệt 2026-10-20)

1. **Bố cục menu** 16 mục như trên: Extract images ra ngoài v0.2.1; Repair thành thông báo tự động (P4), không có mục riêng; Reverse chỉ khi P1 bị bỏ; không làm cây menu thứ hai. Lý do: Rotate 180° (scan lộn ngược) dùng nhiều hơn Reverse; người dùng không biết khi nào cần Repair; Convert > JPG pages đáp ứng phần lớn nhu cầu lấy ảnh.
2. **For sharing một mức**: 150 DPI + JPEG q80 (mức "ebook" quen thuộc của công cụ nén PDF: chữ nhỏ vẫn đọc rõ trên điện thoại và khi in A4). Không thêm mức thấp hơn: chữ hóa đơn nhòe, và vượt 16 mục.

## Kết quả spike P1 (2026-10-20): đạt, giữ P1, không làm P2

Đo bằng Python với bản Linux của pdfium chromium/8100 và qpdf 12.4.2 (cùng phiên bản bản Windows), đúng hướng làm ở P1:

| Fixture | Cỡ vào → ra | Khác biệt render 150 DPI (trung bình /255) | Thời gian |
|---|---|---|---|
| scan 20 trang A4, JPEG 300 DPI (q85) | 23,82 → 8,68 MB (36%) | trang 1: 2,09; trang 2: 2,67 | 0,22 s/trang |
| ảnh dùng lại hai cỡ (300 và 600 DPI) | 1,01 → 0,38 MB | 2,09; 0,32 | |
| ảnh Flate có `/SMask` (333 DPI) trên scan | 1,57 → 0,50 MB | 2,02 | |
| ảnh Flate RGB 300 DPI | 4,52 → 0,47 MB | 2,69 | |
| JPEG CMYK có `/Decode [1 0 …]` | 3,77 → 0,42 MB | 2,73 | |

Bản C# (worker `flevert.exe pdf-worker`, ImageMagick thu nhỏ) chạy trên Linux qua reflection: 20 ảnh 2480×3508 → 1240×1754, 0,5 s/trang. Test trên CI Windows dùng fixture sinh lúc chạy (`SharingPdf`), cùng các tiêu chí.

## Đã quyết khi làm (v0.2.1)

1. **Tìm lại ảnh giữa pdfium và qpdf**: pdfium không cho số object của ảnh, nên worker báo SHA-256 của dữ liệu ảnh *như lưu trong file* (`FPDFImageObj_GetImageDataRaw`, không giải mã), và tiến trình chính băm dữ liệu từng luồng do `qpdf --json-output --json-stream-data=file --decode-level=none` ghi ra thư mục riêng. Không có dữ liệu ảnh nào được giải mã trong tiến trình chính. Hai object cùng dữ liệu nhưng khác từ điển (ví dụ một cái có `/Decode`) → không thay cái nào.
2. **Độ phân giải hiệu dụng** lấy từ `FPDFImageObj_GetImageMetadata` (pdfium tính từ ma trận vẽ), lấy giá trị nhỏ hơn giữa ngang và dọc, nhỏ nhất qua mọi chỗ vẽ. Ảnh trong form XObject: pdfium cho kích thước theo không gian của form, nên **không đổi** (an toàn: không bao giờ làm mờ hơn 150 DPI).
3. **`/SMask` giữ nguyên cỡ của nó** (PDF cho phép mask khác cỡ ảnh); không thu mask theo (khác P1 ban đầu): mask nằm ngoài các object pdfium liệt kê, và render vẫn giống (2,02/255). Ảnh có `/Mask` (khóa màu hay stencil), `/ImageMask`, `/SMaskInData`, dữ liệu ngoài file (`/F`), JBIG2/CCITT, ≤ 1 bit, > 100 MP, hoặc JPEG mới không nhỏ hơn bản cũ: không đổi.
4. **P5, Compress lossless**: lần chạy đầy đủ (giải nén và nén lại) có 10 s + 1 s/MB (ban đầu 3 s + 1 s/MB; review trước phát hành: bốn file chạy song song trên laptop hai nhân bận có thể chạm 3 s với PDF thường, maintainer nâng 2026-10-20); quá thời gian hoặc quá trần bộ nhớ thì ghi lại **không giải mã** (chỉ gói object vào object streams, `--stream-data=preserve`, luồng giữ nguyên) và ghi chú "Flevert compressed this PDF only in part: it packed its objects but left its streams as they were, since compressing them took too long." Bảng PDF độc cho Compress giới hạn 10 s + ngân sách đó. Mọi thao tác ghi PDF khác (Remove metadata, Protect, Remove password) giờ dùng `--stream-data=preserve`.
5. **P4**: qpdf 12.4.2 sửa PDF không có cả bảng xref lẫn trailer nhưng ghi output thiếu `/Root` và `/Size`; ghi thường thì qpdf đọc lại được (tìm catalog), gói vào object streams (Compress) thì không. Compress khi đã sửa mà output không mở được thì chạy lại qpdf với `--object-streams=disable` (vẫn nén lại luồng) rồi kiểm lại; một PDF đã sửa được ghi kể cả khi không nhỏ hơn (chỉ PDF lành không nhỏ hơn mới bị bỏ qua). Kiểm lại output khi đã sửa: vẫn không mở được → lỗi `RepairFailed` "This PDF is too damaged: Flevert could not write a copy of it that opens."; ít trang hơn → "This PDF is damaged: the result would be missing pages.", nhiều hơn → "This PDF is damaged: the result would not have the same pages." (Merge cũng vậy: mọi khác biệt, không chỉ ít hơn)
6. **P3**: các khoảng trắng trống của Unicode (U+00A0, U+2000–U+200A, U+202F, U+205F, U+3000) ghi thành dấu cách thường: Arial dùng một glyph cho dấu cách và NBSP, ToUnicode do pdfium ghi chỉ trả về một trong hai. Không đổi mọi ký tự loại Zs: U+1680 (Ogham) là một nét nhìn thấy được (review trước phát hành, maintainer quyết 2026-10-20).
7. **Bảng PDF độc**: Remove metadata đọc mọi object hai lần qua JSON nên giới hạn của nó là 10 s + 1 s/MB (300 000 object, 16 MB: 13 s trên CI).

---

# v0.2.2: thêm công cụ PDF (tham khảo Stirling-PDF)

Trạng thái: **đã duyệt** (2026-10-20); nhãn và icon cụ thể được duyệt bằng ảnh chụp trong pull request. Làm trên nhánh `feat/v0.2.2-pdf`, tạo từ `main` **sau khi v0.2.1 được gộp** (cùng đụng menu PDF, worker và `Qpdf`); phát hành sau v0.2.1. Mọi quy tắc của v0.2 và v0.2.1 vẫn áp dụng.

## Objective (v0.2.2)

Người dùng giấy tờ ở Việt Nam hay cần thêm vài việc trước khi nộp hoặc in:
- bỏ trang trắng của bản scan hai mặt;
- đóng chữ "Chỉ dùng để nộp hồ sơ…" lên bản scan CCCD;
- đánh số trang;
- in 2 hoặc 4 trang trên một tờ;
- đưa mọi trang về khổ A4;
- khóa form đã điền;
- chia file cho vừa giới hạn email;
- lấy ảnh ra khỏi PDF.

Hôm nay họ làm các việc này trên web, tức là để lộ giấy tờ. v0.2.2 thêm các việc đó, chạy trên máy, và làm lại menu chuột phải (ảnh và PDF) cho người dùng thật: sắp theo việc hay làm, tên dễ hiểu, có icon.

## Nguồn tham khảo: Stirling-PDF

[Stirling-Tools/Stirling-PDF](https://github.com/Stirling-Tools/Stirling-PDF): khoảng 95 000 sao, Java/Spring + PDFBox, giao diện web.

Giấy phép: phần lõi là **MIT**. Các thư mục `app/proprietary`, `app/saas`, `engine/` và vài thư mục frontend có giấy phép riêng: **không đọc, không lấy gì từ đó**.

Khảo sát 2026-10-20 trên `app/core` (khoảng 60 controller), đối chiếu với Flevert v0.2.1:

| Nhóm của Stirling | Flevert đã có (v0.2.1) | Chọn cho v0.2.2 | Không làm, lý do |
|---|---|---|---|
| Trang | Merge, Split, Rotate | Bỏ trang trắng, Nhiều trang trên một tờ, Đưa về A4, Chia theo dung lượng | Sắp xếp/xóa trang tùy ý, booklet, poster, cắt lề, gộp thành một trang dài: cần giao diện chọn trang hoặc ít người dùng |
| Chuyển đổi | Ảnh ↔ PDF, PDF → chữ | Lấy ảnh ra (Extract images) | Office ↔ PDF: cần LibreOffice (~300 MB). PDF → Word/Excel/EPUB/video: ngoài phạm vi |
| Chỉnh nội dung | — | Đánh số trang, Watermark chữ, Làm phẳng form và chú thích | Sửa chữ, chú thích, mục lục. Đổi/đảo màu: đổi màu đúng nghĩa phải render trang thành ảnh, mất chữ chọn được |
| Bảo mật | Mật khẩu, Remove metadata | — | Che thông tin: redact thật cần xóa nội dung dưới vùng che, mà pdfium không có API. Ký số, kiểm chữ ký: ngoài phạm vi. Gỡ hạn chế không có mật khẩu: là phá khóa |
| Khác | Nén, sửa tự động | — | **OCR** (Tesseract, Apache-2.0): làm ở **v0.4.0** (người dùng chốt 2026-10-20). Tự xoay theo chữ: cần OCR, xét cùng v0.4.0. So sánh, pipeline: ngoài phạm vi |

**Không chép code Java.** Flevert là C# với pdfium + qpdf. Chỉ lấy **ý tưởng và tham số mặc định** đã được kiểm chứng (ví dụ ngưỡng trang trắng), và ghi nguồn trong spec và commit message (quy tắc "Reuse before writing" của CLAUDE.md).

## Menu: sắp lại theo người dùng thật, có icon, thêm "More PDF tools…" (bắt buộc ≤ 16 mục)

Người dùng duyệt (2026-10-20): v0.2.2 thêm cửa sổ PDF tools **và** làm lại menu ảnh lẫn menu PDF.

Mục tiêu: một người lần đầu chuột phải vào file thấy ngay việc mình cần, đọc tên là hiểu, nhìn icon là nhận ra. Không cần biết chữ "metadata" hay "lossless".

### Nguyên tắc

1. **Việc hay làm nhất đứng trên.**
   - Thứ tự lấy theo mức phổ biến của các công cụ PDF/ảnh trên web (Smallpdf, iLovePDF, Stirling-PDF đều xếp nén, ghép, tách, chuyển ảnh lên đầu).
   - Với ảnh: đổi định dạng (HEIC → JPG) là việc số một.
2. **Tên là lời thường, nói kết quả.**
   - "Remove metadata" thành **"Remove hidden info"**. Tooltip/mô tả nói rõ: GPS, máy ảnh, tác giả, ngày.
   - Nhóm đổi định dạng là **"Convert to ▸"**. Mức nén nói tác dụng, không nói kỹ thuật.
   - **op-id không đổi** (chỉ đổi nhãn), nên README, docs và test menu cập nhật theo.
3. **Mỗi nhóm và mỗi việc có icon:**
   - nhóm Convert, Compress, Rotate có icon riêng;
   - mỗi việc đơn lẻ (Merge, Split, Protect, Remove password, Remove hidden info, Combine into PDF, More PDF tools) có icon;
   - mục định dạng con (JPG, PNG…) không cần icon. Danh sách định dạng dài, thêm icon chỉ rối.
4. **Nhóm bằng đường kẻ (separator)** giữa các cụm việc (đổi / sắp trang / bảo vệ), nếu đường kẻ **không** tính vào giới hạn 16 mục. Task đầu tiên đo điều này bằng MenuProbe trên Explorer thật (`CommandFlags` `ECF_SEPARATORBEFORE`). Nếu đường kẻ tính vào giới hạn thì bỏ đường kẻ, giữ thứ tự.
5. **≤ 16 mục mỗi đuôi**, tính cả mục mở submenu (core.md, mục Menu). Test menu khẳng định điều này cho mọi đuôi.

### Menu PDF (16 mục)

```
[icon] Compress ▸        Smaller, same quality · Smallest, for sharing     (3)
[icon] Merge PDFs                                                           (1)
[icon] Split into pages                                                     (1)
[icon] Convert to ▸      JPG images · PNG images                            (3)
[icon] Rotate ▸          Right 90° · Left 90° · 180°                        (4)
──────
[icon] Protect with password                                                (1)
[icon] Remove password                                                      (1)
[icon] Remove hidden info                                                   (1)
──────
[icon] More PDF tools…                                                      (1)   = 16
```

### Menu ảnh (≤ 16 mục; ví dụ `.jpg`)

```
[icon] Convert to ▸      PNG · WebP · AVIF · GIF · BMP · TIFF (bỏ đích trùng nguồn)          (7)
[icon] Compress ▸        Light (best quality) · Balanced · Strong (smallest)
                         · Under 500 KB · Under 1 MB · Under 5 MB                             (7)
──────
[icon] Remove hidden info                                                                     (1)
[icon] Combine into PDF                                                                       (1)   = 16
```

- Thứ tự mới đưa **Remove hidden info** lên trước **Combine into PDF**: xóa GPS trước khi gửi ảnh là việc thường ngày, gộp ảnh thành PDF thì ít hơn.
- HEIC/HEIF vẫn không có `Convert to > GIF` (giới hạn 16, image.md).

### Icon

- **Nguồn:** [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons), giấy phép MIT, cùng phong cách với Windows 11.
  - Dùng biến thể **Color** khi có (đọc rõ trên menu sáng lẫn tối).
  - Không có biến thể Color thì dùng **Filled**, tô một màu nhấn đủ tương phản trên cả nền sáng `#F9F9F9` và nền tối `#2C2C2C` (≥ 3:1, WCAG cho đồ họa).
- **Cách tạo:**
  - Script `scripts/make-menu-icons.py` (hoặc `.ps1`) đọc SVG ở commit đã ghim của repo Fluent (URL + SHA256 trong script). Script xuất `.ico` nhiều cỡ (16, 20, 24, 32, 40, 48 px, cho mọi mức DPI) vào `assets/menu/`.
  - `.ico` được **commit**: là tài sản nhỏ như `assets/app.ico`, không phải binary phụ thuộc.
  - Thêm giấy phép MIT của Fluent vào `THIRD-PARTY-NOTICES.txt` (`write-notices.ps1` kiểm).
- **Cài đặt:**
  - Installer chép icon vào `{app}\icons\`.
  - `install-menu` ghi giá trị `Icon` = `"<thư mục cài>\icons\<tên>.ico"` cho từng verb (top-level `Flevert` giữ icon của exe).
  - Gỡ cài thì icon đi theo thư mục cài.
  - Icon thiếu (bản dev) → menu vẫn ghi, không có icon; không lỗi.
- **Bộ icon đề xuất** (tên Fluent; agent chọn biến thể đúng ở bước Plan và đưa ảnh chụp để duyệt):

  | Mục | Icon Fluent |
  |---|---|
  | Convert to | `arrow_swap` |
  | Compress | `arrow_minimize` (hoặc `archive`) |
  | Merge PDFs | `merge` (hoặc `document_add`) |
  | Split into pages | `split_vertical` (hoặc `cut`) |
  | Rotate | `arrow_rotate_clockwise` |
  | Protect with password | `lock_closed` |
  | Remove password | `lock_open` |
  | Remove hidden info | `shield` / `eye_off` |
  | Combine into PDF | `document_pdf` |
  | More PDF tools | `toolbox` (hoặc `apps`) |

### Cửa sổ "Flevert — PDF tools" (mở từ `More PDF tools…`)

- WinForms, cùng kiểu với hộp thoại mật khẩu: luôn trên cùng, có tên trợ năng, Enter/Esc, phím Tab đi đúng thứ tự.
- Câu trên cùng nêu số file đang chọn ("3 PDFs selected").
- Danh sách công cụ, mỗi dòng có:
  - **icon** (cùng nguồn Fluent);
  - **tên** ngắn;
  - **một câu** nói kết quả (ví dụ "Remove blank pages: drops empty pages left by a two-sided scan.").
- Các dòng xếp theo nhóm có tiêu đề (Pages · Add to pages · Extract), việc hay dùng trước:
  - **Pages:** Remove blank pages · 2 pages per sheet · 4 pages per sheet · Fit pages to A4 · Split into parts under 10 MB / 25 MB
  - **Add to pages:** Add watermark · Add page numbers · Flatten forms and comments
  - **Extract:** Extract text · Extract images
- Chọn công cụ có tùy chọn thì tùy chọn hiện ngay dưới (chữ watermark, vị trí và kiểu số trang). Mặc định là lựa chọn phổ biến, để người dùng chỉ cần bấm **Run**.
- Hai nút: **Run** (nút mặc định) và **Cancel**.
- Cửa sổ ghi nhớ công cụ chọn lần trước (thiết lập của người dùng trong `%LOCALAPPDATA%\Flevert`, không có dữ liệu file).

**Nhiều file:**
- Explorer mở một tiến trình cho mỗi file như mọi thao tác (ADR-0002); lô được gom như hiện nay.
- Cửa sổ chỉ hỏi **một lần** cho cả lô (như "Use this password for all N files").
- Công cụ chạy cho từng file (v0.2.2 không có công cụ N → 1).

**Cancel và chạy không cửa sổ:**
- Cancel → không file nào bị đổi, không thông báo lỗi.
- Không có cửa sổ (`FLEVERT_HEADLESS=1`): `flevert.exe run <op-id> <file>` vẫn chạy từng công cụ với **tùy chọn mặc định** (để test và tự động hóa).
- Công cụ cần chữ do người dùng nhập (watermark) thì lỗi rõ khi không có cửa sổ, như thao tác mật khẩu.

`Convert > Text` (v0.2.1) chuyển thành dòng **Extract text** trong cửa sổ; op-id `pdf.extract-text` **không đổi**.

### Kiểm như người dùng thật

- **Ảnh chụp:** chụp menu thật (classic menu, Windows 11) của `.jpg`, `.heic`, `.pdf` ở chế độ **sáng và tối**, và cửa sổ PDF tools, ở 100% và 150% DPI. Ảnh đặt vào mô tả PR.
- **Duyệt:** người dùng duyệt ảnh chụp **trước khi gộp**.
- **Kịch bản tìm việc:** mỗi việc tới được trong ≤ 2 lần di chuột từ `Flevert`, tên đọc là hiểu không cần README. Các kịch bản:
  - "gửi ảnh iPhone cho người dùng Windows";
  - "giảm dung lượng PDF scan để gửi Zalo";
  - "xóa vị trí GPS khỏi ảnh";
  - "ghép 3 PDF";
  - "đóng chữ lên bản scan CCCD";
  - "bỏ trang trắng".

  Reviewer doubt chạy các kịch bản này như một người dùng mới và ghi chỗ khó hiểu.
- **Trợ năng:** UI Automation đọc được tên mọi mục của cửa sổ; cửa sổ dùng được chỉ bằng bàn phím.
- Báo người dùng trước khi chạy test bật cửa sổ trên màn hình.

## Yêu cầu

Quy tắc chung cho mọi công cụ:
- op-id riêng, gọi được bằng `flevert.exe run`.
- Output cạnh file gốc theo quy tắc `(1)` của core; file gốc không đổi.
- Đếm trang (tối đa 2 000) và giới hạn 2 GB như hiện có.
- PDF có mật khẩu mở → lỗi như hiện có; chữ ký số → ghi chú.
- Dữ liệu PDF lạ chỉ được pdfium đọc **trong worker** (`flevert.exe pdf-worker`, trần 2,5 GB, ngân sách thời gian).

Việc mới của worker: công cụ ghi PDF bằng pdfium cần worker **lưu** được tài liệu (`FPDF_SaveAsCopy` vào file `.partial` của cha). Thêm loại yêu cầu mới theo giao thức dòng hiện có, không đổi các dòng cũ.

| op-id | Tên trong cửa sổ | Output | Cách làm |
|---|---|---|---|
| `pdf.remove-blank-pages` | Remove blank pages | `<tên> (no blank pages).pdf` | Xem mục "Remove blank pages" ngay dưới bảng. |
| `pdf.page-numbers` | Add page numbers | `<tên> (numbered).pdf` | Tùy chọn vị trí: dưới giữa (mặc định), dưới phải, trên phải. Tùy chọn kiểu: `1` (mặc định), `1 / 12`, `Page 1 of 12`. Chữ đen Helvetica (font chuẩn của PDF, không nhúng) 10 pt, cách mép 28 pt (~1 cm), theo **trang đã xoay** để số nằm đúng chỗ khi xem. pdfium trong worker (`FPDFPageObj_NewTextObj`, `FPDFPage_GenerateContent`). |
| `pdf.watermark` | Add watermark | `<tên> (watermarked).pdf` | Xem mục "Add watermark" ngay dưới bảng. |
| `pdf.n-up-2` / `pdf.n-up-4` | 2 pages per sheet / 4 pages per sheet | `<tên> (2 per sheet).pdf` | A4 ngang cho 2 trang, A4 dọc cho 4 trang, giữ tỉ lệ, theo thứ tự đọc. pdfium `FPDF_ImportNPagesToOne` trong worker. |
| `pdf.fit-a4` | Fit pages to A4 | `<tên> (A4).pdf` | Mỗi trang thu/phóng vừa A4 (dọc hoặc ngang theo trang gốc), giữ tỉ lệ, căn giữa. Trang đã đúng A4 (±1 pt) giữ nguyên; mọi trang đã là A4 → skipped. pdfium `FPDF_ImportNPagesToOne` 1×1 trong worker. |
| `pdf.flatten` | Flatten forms and comments | `<tên> (flattened).pdf` | pdfium `FPDFPage_Flatten`: giữ vector và chữ chọn được, **không** render thành ảnh như Stirling. Không có form hay chú thích → skipped. |
| `pdf.split-10mb` / `pdf.split-25mb` | Split into parts under 10 MB / 25 MB | thư mục `<tên> (parts)\<tên> part 1.pdf` … | Xem mục "Split into parts" ngay dưới bảng. |
| `pdf.extract-images` | Extract images | thư mục `<tên> (images)\<tên> 001.jpg` … | Xem mục "Extract images" ngay dưới bảng. |
| `pdf.extract-text` | Extract text | như v0.2.1 | Chỉ đổi chỗ trong menu. |

**Remove blank pages** (`pdf.remove-blank-pages`):
- Worker render từng trang ở 150 DPI xám.
- Một trang là trắng khi cả hai điều kiện đúng:
  - ≥ 99,9% điểm ảnh có độ sáng ≥ 245 (ngưỡng 10 trên 255). Ngưỡng lấy từ `BlankPageController` của Stirling-PDF (MIT).
  - Trang không có ký tự nào (`FPDFText_CountChars` = 0). Điều kiện này Flevert thêm vào, để không bỏ nhầm trang chỉ có một dòng chữ nhỏ.
- Giữ các trang còn lại bằng qpdf `--pages`: chép nguyên, không mã hóa lại.
- Thông báo nêu số trang đã bỏ: "Removed 3 blank pages: 2, 8, 9."
- Không trang nào trắng → skipped "This PDF has no blank pages."
- Mọi trang đều trắng → lỗi "Every page of this PDF looks blank: nothing was removed."

**Add watermark** (`pdf.watermark`):
- Tùy chọn: chữ, bắt buộc, tối đa 100 ký tự, tiếng Việt được. Ô nhập có gợi ý "Chỉ dùng để nộp hồ sơ …".
- Chữ xám, độ mờ 30%, xoay 45°, lặp thành lưới phủ cả trang (một dấu duy nhất trên bản scan giấy tờ thì dễ cắt bỏ). Cỡ chữ theo khổ trang.
- Font: **Arial của Windows**, nhúng phần cần dùng lúc chạy (`FPDFText_LoadFont` với byte của `%WINDIR%\Fonts\arial.ttf`). Không đóng gói font. Thiếu Arial → lỗi rõ.
- Mô tả trong cửa sổ phải nói rõ: watermark nằm trên trang nhưng **không** chống được việc sửa (người có công cụ vẫn xóa được).

**Split into parts** (`pdf.split-10mb` / `pdf.split-25mb`):
- Chia tham lam theo trang, như `SplitPdfBySizeController` của Stirling-PDF: thêm trang tới khi phần vượt giới hạn thì lùi lại một trang.
- Mỗi phần ghi bằng qpdf `--pages … --stream-data=preserve`, rồi đo cỡ thật của file.
- Một trang đơn lẻ đã vượt giới hạn → lỗi nêu số trang.
- File đã dưới giới hạn → skipped.
- 1 MB = 1 000 000 byte.
- Ghi qua `WriteDirectoryAtomicallyAsync` (1 → N).

**Extract images** (`pdf.extract-images`):
- Như spec v0.2 (dòng `pdf.extract-images`): JPEG trong PDF ra `.jpg` nguyên byte; ảnh khác ra `.png`, giải mã trong worker.
- Ảnh dùng lại ở nhiều chỗ chỉ ra một lần.
- Ảnh nhỏ hơn 32 × 32 px bỏ qua (biểu tượng, đường kẻ).
- Không có ảnh → skipped.
- Giới hạn tổng pixel và kiểm chỗ trống như P6.6.

## Testing Strategy (v0.2.2)

- Test viết trước và thấy đỏ.
- Fixture sinh bằng script, ghi nguồn gốc:
  - scan 6 trang, trong đó 2 trang trắng và 1 trang chỉ có một dòng chữ nhỏ (trang này không được bỏ);
  - PDF có trang xoay `/Rotate 90` (số trang và watermark phải đúng chỗ khi xem);
  - trang Letter, A3 và khổ lạ (cho fit A4);
  - form AcroForm đã điền, kèm chú thích;
  - PDF 30 MB có ảnh (cho split 10 MB);
  - PDF có JPEG, ảnh Flate, và ảnh dùng lại.
- Kiểm nội dung, không chỉ kiểm file có tồn tại:
  - render và so điểm mẫu: số trang ở góc dưới giữa, watermark có mặt ở giữa trang;
  - số trang và khổ trang, đọc bằng qpdf;
  - cỡ từng phần < giới hạn;
  - JPEG xuất ra trùng byte với luồng trong PDF;
  - chữ tiếng Việt của watermark lấy lại được bằng `pdf.extract-text`.
- Mỗi công cụ mới chạy qua bảng PDF độc `MaliciousPdf`: ≤ 10 s hoặc lỗi rõ, không còn file tạm.
- Cửa sổ chọn công cụ:
  - test không cần UI cho phần logic (danh sách, tùy chọn, một câu trả lời cho cả lô);
  - UI Automation kiểm tên trợ năng và phím. Báo người dùng trước khi chạy test bật cửa sổ trên màn hình;
  - MenuProbe: menu `.pdf` đúng 16 mục.

## Boundaries (v0.2.2)

- **Always:** như v0.2.1; mọi việc pdfium với PDF lạ chạy trong worker; nêu nguồn Stirling-PDF khi lấy tham số hay thuật toán.
- **Ask first:** thêm công cụ ngoài bảng trên; đóng gói font hay binary mới; đổi bố cục menu.
- **Never:**
  - đọc hay lấy gì từ các thư mục giấy phép riêng của Stirling-PDF, hay chép code Java;
  - render trang thành ảnh để "làm phẳng" hay "đổi màu" (mất chữ);
  - đẩy thẳng vào `main`;
  - sửa version, `tasks/HANDOFF.md`, hay mục đã phát hành của CHANGELOG.

## Success Criteria (v0.2.2)

1. Menu `.pdf` và menu ảnh thật đúng bố cục ở trên (≤ 16 mục, đúng thứ tự, đúng tên, có icon, rõ trên nền sáng và tối). `More PDF tools…` mở cửa sổ, chạy được từng công cụ cho 1 file và cho 5 file (một câu hỏi cho cả lô). Người dùng đã duyệt ảnh chụp menu và cửa sổ; các kịch bản tìm việc đạt (≤ 2 lần di chuột).
2. Bỏ trang trắng: fixture 6 trang → 4 trang; trang chỉ có một dòng chữ được giữ; thông báo nêu số trang đã bỏ.
3. Số trang và watermark nằm đúng chỗ trên trang thường và trang xoay; watermark tiếng Việt lấy lại được nguyên văn.
4. 2/4 trang mỗi tờ và A4: khổ trang output đúng A4 (±1 pt); số trang đúng (⌈N/2⌉, ⌈N/4⌉, N).
5. Làm phẳng: form không còn trường điền được (qpdf `--json` không còn `/AcroForm` có trường); nội dung đã điền vẫn hiện và chọn được.
6. Chia theo dung lượng: mọi phần < giới hạn, tổng số trang bằng input, đúng thứ tự.
7. Lấy ảnh: JPEG trùng byte; ảnh dùng lại chỉ ra một lần.
8. Mọi công cụ đạt bảng PDF độc. CI xanh 3 lần. `test-installer.ps1` đạt (gồm nâng cấp từ bản đã ghim). Ba review bối cảnh trống không còn Critical/High/Required.

## Đã quyết (v0.2.2, người dùng 2026-10-20)

1. **Menu**: thêm `More PDF tools…` mở cửa sổ chọn công cụ (có icon, có nhóm); `Convert > Text` vào cửa sổ; làm lại menu ảnh và PDF theo người dùng thật, có icon Fluent (MIT).
2. **Phạm vi**: cả 8 công cụ trong bảng Yêu cầu.
3. **OCR**: v0.4.0, sau media (v0.3.0); spec riêng viết khi bắt đầu v0.4.0 (Tesseract Apache-2.0 + dữ liệu `vie`, bộ cài nặng thêm khoảng 30–60 MB).

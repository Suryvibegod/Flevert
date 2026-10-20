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
- **Ảnh → PDF**: mỗi ảnh một trang, kích thước trang theo ảnh ở 150 DPI nếu ảnh không ghi DPI (ảnh điện thoại 4000 px → trang ~68 cm là quá to: trang lớn hơn A4 thì thu về vừa A4 hoặc Letter theo chiều dài nhất, giữ tỉ lệ). Xoay đúng chiều theo EXIF như module `image`. JPEG nhúng **nguyên byte** (DCTDecode, không nén lại, không mất chất lượng; EXIF/GPS bị bỏ khỏi luồng nhúng). Ảnh khác: HEIC/HEIF/AVIF/WebP chụp → JPEG q92; PNG/GIF/BMP/TIFF → nén Flate không mất dữ liệu; nền trong suốt thành trắng. Metadata của PDF tạo ra: chỉ `Producer: Flevert`.
- **1 → N**: thư mục output có tên chưa tồn tại (quy tắc `(1)` của core áp cho thư mục); file đánh số đủ chữ số (`001` khi ≤ 999 trang).
- **PDF có mật khẩu mở**: mọi thao tác trừ `pdf.unlock` báo lỗi rõ "This PDF needs a password: use Remove password first" (không hỏi mật khẩu cho từng thao tác ở v0.2).
- **Render** (`to-jpg`, `to-png`): 150 DPI mặc định (đủ đọc trên điện thoại, nhỏ), giới hạn ở dưới. JPEG q90, 4:2:0 (file nhỏ hơn; ImageMagick tự để 4:4:4 từ q90); PNG không mất dữ liệu, không kênh alpha (trang vẽ trên nền trắng). Ảnh ghi DPI thật của trang (in đúng khổ). Một trang pdfium không đọc được → cả file lỗi, thông báo nêu số trang (thư mục ghi nguyên vẹn hoặc không có gì; thiếu một trang mà trông như đủ thì tệ hơn). PDF không có trang → lỗi "This PDF has no pages.". Mỗi trang xin `MemoryBudget` (chung với module ảnh) gấp 3 lần ảnh BGRA trước khi render.

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
| Ảnh → PDF | **Tự ghi PDF** (writer tối giản: trang + XObject ảnh), giải mã ảnh bằng Magick.NET có sẵn | — | Nhúng JPEG nguyên byte; Magick.NET **đọc** PDF cần Ghostscript (AGPL) nên không dùng Magick cho PDF ở chiều đọc; chiều ghi của Magick nén lại JPEG nên cũng không dùng |
| Compress for sharing | pdfium (đọc ảnh, DPI hiệu dụng) + Magick (giảm ảnh) + qpdf (thay luồng ảnh)? | — | **Câu hỏi mở**: cách thay ảnh trong PDF không cần thư viện AGPL. Nếu không làm được gọn ở bước Plan thì bỏ khỏi v0.2 (Compress > Lossless vẫn có) |

Loại trừ: Ghostscript, iText, MuPDF (AGPL); PDFsharp **6.5 trở lên** (đổi sang AGPL-3.0; 6.2.4 trở xuống là MIT nhưng không còn được vá). Mọi binary (`pdfium.dll`, `qpdf.exe` + DLL) **không commit**: `scripts/fetch-deps.ps1` tải bản ghim theo URL + SHA256 vào `deps/`, CI và release dùng đúng script đó.

## Thay đổi ở core (làm trước, có ADR)

1. **Thao tác nhiều file → một output** (`IBatchOperation`): leader gom hết file của lô (như hiện nay), sắp theo thứ tự tự nhiên, gọi thao tác **một lần** với cả danh sách. Thông báo cuối: `Done: IMG_0001 (+11).pdf`. Lỗi một file đầu vào (ảnh hỏng) → cả output thất bại, thông báo nêu file nào (không tạo PDF thiếu trang âm thầm). Explorer giới hạn 100 file được chọn cho menu (đã ghi ở README).
2. **Một file → nhiều output** (1 → N): thao tác trả về thư mục; ghi vào thư mục tạm `.flevert-<guid>.partial` rồi đổi tên một lần khi xong, như file đơn (hủy giữa chừng không để lại thư mục dở).
3. **Tiến độ trong một file**: `IProgress<FileProgress>` cho thao tác dài (render 200 trang); cửa sổ tiến độ hiện "page 37 of 200".
4. **Gọi tiến trình ngoài** (`ExternalTool`): đường dẫn tuyệt đối trong thư mục cài; `ArgumentList` (không ghép chuỗi); stdin cho dữ liệu nhạy cảm; stdout/stderr đọc giới hạn (1 MB); timeout cứng; Cancel giết cả cây tiến trình; job object để tiến trình con chết theo Flevert; kiểm hash của exe khi khởi động lần đầu sau cài? (**câu hỏi mở**, xem Boundaries).
5. **Giới hạn tài nguyên**: tối đa 2 000 trang/file; trang render tối đa 10 000 px mỗi cạnh **sau khi nhân DPI** (trang lớn thì giảm DPI cho trang đó); bộ nhớ theo `MemoryBudget` hiện có; file PDF tối đa 2 GB.
6. **pdfium không thread-safe**: mọi lời gọi pdfium qua một khóa toàn tiến trình; thao tác pdfium đặt `MaxParallel = 1`; qpdf chạy song song được (mỗi file một tiến trình): `MaxParallel = 4` (runner còn giới hạn theo số nhân − 1). Mỗi qpdf một nhân, việc chủ yếu là đọc và ghi lại file nên quá vài tiến trình thì đĩa là giới hạn; bộ nhớ của qpdf tăng theo PDF và nằm ngoài `MemoryBudget` (quyết định ở T3).
7. **Menu**: `.pdf` thêm vào danh sách đuôi; ảnh có thêm mục `Combine into PDF`. Thứ tự nhóm trong menu ảnh: Convert, Compress, Combine into PDF, Remove metadata.
8. **Gọi qpdf** (đã kiểm với qpdf 12.4.2, T3): tùy chọn (hằng của Flevert) trên dòng lệnh, **hai đường dẫn qua stdin bằng `@-`** (mỗi dòng một đối số). Lý do: C runtime của qpdf mở rộng ký tự đại diện trong đối số dòng lệnh (`r*.pdf` thành nhiều file), nên dấu `?` của đường dẫn `\\?\…` (runner dùng cho đường dẫn dài hơn 260 ký tự) biến thành tên khác; dòng đọc từ `@-` giữ nguyên, và qpdf mở được `\\?\` dài, có Unicode. qpdf không có dấu kết thúc tùy chọn (`--` không phải), nên đường dẫn phải tuyệt đối (không bắt đầu bằng `-` hay `@`) và không chứa xuống dòng. Mã thoát: 0 xong; 3 (cảnh báo: qpdf đã sửa gì đó, ví dụ trang thiếu `/Resources` mà mọi trình xem vẫn mở) **tính là xong**, cảnh báo không hiện cho người dùng; 2 lỗi: hỏi lại `qpdf --requires-password` (mã thoát có tài liệu, chữ "invalid password" thì có thể đổi) → thông báo mật khẩu; stderr nhắc file output → lỗi ghi file; còn lại "This file is not a PDF, or it is damaged." (chi tiết của qpdf nằm trong log). Timeout: 2 phút + 1 giây mỗi MB. File > 2 GB bị từ chối trước khi chạy qpdf; giới hạn số trang cho thao tác qpdf để T18.

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
src/Flevert.Pdf/             → thao tác PDF (IOperation, IBatchOperation), PdfWriter tối giản, Pdfium (P/Invoke), Qpdf (ExternalTool)
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

- **Unit** (`Flevert.Pdf.Tests`): thứ tự tự nhiên (`ảnh 2` < `ảnh 10`, chữ có dấu, số rất dài); kích thước trang từ DPI; PdfWriter tạo file mà qpdf `--check` báo hợp lệ và pdfium mở được; JPEG nhúng đúng byte (so hash luồng); tên output; giới hạn trang/px.
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
6. Fixture PDF độc bị từ chối trong ≤ 10 giây, bộ nhớ không vượt `MemoryBudget`.
7. Bộ cài: có `pdfium.dll`, `qpdf.exe`, notices đủ; nâng cấp từ 0.1.0 giữ lựa chọn menu và kiểm cập nhật; gỡ sạch.
8. Khởi động lạnh `flevert.exe run` cho thao tác ảnh không chậm hơn v0.1.0 quá 10% (pdfium chỉ nạp khi cần).
9. Ba review bối cảnh trống không còn Critical/High/Required.

## Open Questions

1. Compress for sharing: thay ảnh trong PDF gọn bằng pdfium + qpdf (qpdf có API thay luồng qua JSON `--json-input`/`--update-from-json` từ v11) hay bỏ khỏi v0.2? Quyết ở bước Plan sau khi thử.
2. qpdf bản msvc64 kèm thư viện crypto nào (OpenSSL hay native)? Ảnh hưởng notices và kích thước.
3. Kiểm hash `qpdf.exe`/`pdfium.dll` lúc chạy có đáng không, khi thư mục cài đã được setup kiểm quyền (chỉ chủ và admin ghi được)? Mặc định: không, vì cùng mức tin cậy với `flevert.exe`.

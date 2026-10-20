# Todo: pdf (v0.2.0)

Plan: [plan.md](plan.md). Spec: [docs/specs/pdf.md](../../docs/specs/pdf.md).

Mỗi task chỉ xong khi đạt tiêu chí nghiệm thu **và** Definition of Done: test viết trước và thấy đỏ; build, `dotnet format --verify-no-changes`, hai project test (cộng `Flevert.Pdf.Tests` từ T3) xanh; chạy thật; tài liệu cập nhật; commit nguyên tử, message nói vì sao.

Lệnh chung: `powershell -File scripts/fetch-deps.ps1`, `dotnet build Flevert.sln`, `dotnet test tests/Flevert.Core.Tests`, `dotnet test tests/Flevert.Image.Tests`, `dotnet test tests/Flevert.Pdf.Tests`.

## Giai đoạn 1: nền

### T1: Tải deps ghim theo hash
Tải pdfium và qpdf (cùng giấy phép của chúng) vào `deps/` theo một manifest có SHA256; CI dùng chung script.
- [x] `scripts/deps.json`: pdfium `chromium/8100` `pdfium-win-x64.tgz`, qpdf `v12.4.2` `qpdf-12.4.2-msvc64.zip`, `LICENSE.txt` + `NOTICE.md` của qpdf ở tag `v12.4.2`; mỗi mục URL + SHA256 + file cần giữ
- [x] `fetch-deps.ps1`: tải vào thư mục tạm mới, kiểm hash **trước** khi giải nén, chỉ giữ file liệt kê (`pdfium.dll`, `licenses/*`, `LICENSE`; `qpdf.exe`, `qpdf30.dll`, DLL runtime MSVC); chạy lại khi đã đủ thì không tải; hash sai → lỗi, `deps/` không đổi
- [x] CI (`ci.yml`, `release.yml`) và setup-dev gọi script (không cache: tải vài giây, kiểm hash mỗi lần); ADR-0008
- Kiểm: chạy hai lần (lần 2 không tải); sửa một ký tự hash → thất bại rõ ràng (đối chứng âm); CI xanh
- Phụ thuộc: không. File: `scripts/deps.json`, `scripts/fetch-deps.ps1`, `.github/workflows/*.yml`, `docs/adr/0008-…`. Cỡ: M

### T2: `ExternalTool` trong core
Chạy công cụ ngoài an toàn: đường dẫn tuyệt đối, `ArgumentList`, stdin, đọc output có giới hạn, timeout, hủy giết cả cây, job object để con chết theo Flevert.
- [x] Trả mã thoát, stdout/stderr (tối đa 1 MB mỗi luồng, phần thừa bỏ); stdin là byte do caller đưa (cho mật khẩu), đóng ngay sau khi ghi
- [x] Hủy hoặc quá timeout → tiến trình và con của nó chết trong ≤ 2 s; tiến trình Flevert chết → con chết (job object `KILL_ON_JOB_CLOSE`)
- [x] Không bao giờ chạy qua shell; đường dẫn phải tuyệt đối và tồn tại
- Kiểm: test dùng `cmd.exe`/`ping` của hệ thống: echo stdin, output 5 MB bị cắt, timeout, hủy, cây con bị giết; giết Flevert test-host con → không còn tiến trình mồ côi
- Đã làm: 18 test trong `ExternalToolTests` (≈1 s), đỏ với stub rồi xanh; thử đột biến (bỏ giết tiến trình sót, bỏ `KILL_ON_JOB_CLOSE`, đọc stdout trước stderr) thì test tương ứng đỏ. "Flevert chết" được kiểm bằng cách đóng handle job (Windows làm đúng việc đó khi tiến trình chết); kiểm bằng `flevert.exe` thật để lại cho T3, khi đã có thao tác gọi qpdf. Job theo từng lần gọi; khi tool thoát, thứ nó để lại cũng bị kết thúc. Gán vào job ngay sau `Process.Start` (không khởi động treo được qua `Process`), khe hở nhỏ được bù bằng `Kill(entireProcessTree)` khi hủy/timeout. Chỉ chạy `.exe` (`.bat`/`.cmd` đi qua cmd).
- Phụ thuộc: không. File: `src/Flevert.Core/ExternalTool.cs`, `tests/Flevert.Core.Tests/ExternalToolTests.cs`. Cỡ: M

### T3: Lát đầu: xoay PDF (`pdf.rotate-right`/`-left`/`-180`)
Project `Flevert.Pdf`, wrapper `Qpdf`, ba thao tác xoay, `.pdf` vào menu.
- [ ] `src/Flevert.Pdf` + `tests/Flevert.Pdf.Tests` (thêm vào sln, CI, release, setup-dev, tài liệu lệnh test); `Qpdf` tìm `qpdf.exe` cạnh `flevert.exe` (bản build: `deps/qpdf`)
- [ ] Output `<tên> (rotated).pdf`, mọi trang xoay đúng góc (đọc lại bằng `qpdf --json`); file gốc không đổi; PDF có mật khẩu → lỗi "This PDF needs a password: use Remove password first"
- [ ] Fixture: `make_pdf_fixtures.py` (thư viện chuẩn) + README nguồn gốc
- Kiểm: test đỏ trước; `flevert.exe run pdf.rotate-right` thật; `install-menu --dev --allow-insecure-location` → MenuProbe thấy `Flevert > Rotate > 90° right` trên `.pdf`; `uninstall-menu`
- Phụ thuộc: T1, T2. Cỡ: M

### T4: Đóng gói qpdf
- [ ] `build-installer.ps1` chép `deps/qpdf/bin/*` cần thiết; `THIRD-PARTY-NOTICES.txt` có qpdf (LICENSE + NOTICE), OpenSSL, zlib, libjpeg-turbo, MSVC runtime; thiếu → build lỗi
- [ ] Check-location của setup không giải nén qpdf vào thư mục tạm
- [ ] `test-installer.ps1`: có `qpdf.exe`, notices đủ; xoay một PDF bằng bản đã cài; giới hạn kích thước 80 MB; gỡ sạch
- Kiểm: build bộ cài, `test-installer.ps1` PASS, ghi kích thước bộ cài vào todo
- Phụ thuộc: T3. Cỡ: S

### Checkpoint 1
- [ ] Mọi test xanh, CI xanh, bộ cài chạy xoay PDF thật
- [ ] Review ba góc (code, bảo mật, doubt) cho T1–T4; reconcile

## Giai đoạn 2: thao tác nhiều file

### T5: Thao tác N→1 trong core (`IBatchOperation`)
- [ ] Hợp đồng mới cạnh `IOperation` (không đổi đường đi hiện có): nhận danh sách file đã ghim, thứ tự `StrCmpLogicalW`, trả một output; menu và catalog nhận cả hai loại
- [ ] Runner ghim mọi file của lô trước khi gọi; một file bị từ chối (đuôi, đường dẫn) → cả lô lỗi, thông báo nêu file
- [ ] Thông báo `Done: <tên output>`; ADR-0007
- Kiểm: thao tác dev `dev.join` (nối các `.txt`) qua 12 tiến trình `flevert.exe` thật → một file đúng thứ tự tự nhiên (`ảnh 2` trước `ảnh 10`); toàn bộ test tích hợp cũ vẫn xanh
- Phụ thuộc: T2. Cỡ: M

### T6: `pdf.merge`
- [ ] ≥ 2 PDF → `<tên đầu> (merged).pdf`, số trang bằng tổng, thứ tự tự nhiên; 1 PDF → lỗi rõ "Select at least two PDFs"
- Kiểm: test + chạy thật 3 PDF qua menu (MenuProbe invoke)
- Phụ thuộc: T3, T5. Cỡ: S

### T7: Output thư mục (1→N) và tiến độ trong file
- [x] `OutputPath.WriteDirectoryAtomicallyAsync`: thư mục tạm, đổi tên một lần, tên trùng → ` (1)`; hủy → không còn thư mục dở
- [x] `IProgress<FileProgress>` cho thao tác; cửa sổ tiến độ hiện "page 37 of 200" (UI Automation đọc được)
- Kiểm: thao tác dev tạo 50 file chậm, hủy giữa chừng; ảnh chụp cửa sổ tiến độ (chỉ cửa sổ app, báo người dùng trước)
- Đã làm: `dev.split` (50 trang, `FLEVERT_DEV_PAGE_MS`), ADR-0007. Hủy giữa chừng kiểm bằng test runner (hủy ở trang 10 → không còn `.partial`), không bấm Cancel thật (không gửi phím vào desktop). Chạy thật có UI: PrintWindow chỉ cửa sổ Flevert thấy `0 of 1 done: Báo cáo quý.txt` / `page 24 of 50`; UI Automation đọc `Progress in the current file: page 25 of 50`; xong 50 file, không còn `.partial`. `Directory.Delete(recursive)` không dùng được (junction bên trong → cần admin, để lại thư mục), nên tự duyệt và gỡ link không đi theo
- Phụ thuộc: T2. Cỡ: M

### T8: `pdf.split`
- [ ] Thư mục `<tên> (pages)\<tên> 001.pdf…`, số chữ số đủ; mỗi file một trang
- Phụ thuộc: T3, T7. Cỡ: S

### Checkpoint 2
- [ ] Review ba góc cho T5–T8 (trọng tâm: runner, hủy, đặt tên); reconcile

## Giai đoạn 3: pdfium

### T9: `Pdfium` (P/Invoke) và render an toàn
- [x] `LibraryImport` tới `pdfium.dll` cạnh exe; `SafeHandle` cho document/page/bitmap; khóa toàn tiến trình; `FPDF_InitLibrary` một lần
- [x] Giới hạn: ≤ 2 000 trang, trang render ≤ 10 000 px mỗi cạnh **sau khi nhân DPI** (vượt thì giảm DPI trang đó), file ≤ 2 GB; PDF có mật khẩu → lỗi như T3
- [x] ADR-0008 cập nhật (P/Invoke tự viết, lý do không dùng wrapper NuGet)
- Kiểm: fixture 3 trang render đúng màu tại điểm mẫu; fixture trang khổng lồ bị giảm DPI; test chạy song song 8 luồng không crash
- Đã làm (2026-10-20): project `Flevert.Pdf` + `Flevert.Pdf.Tests` tạo sớm ở T9 (sln, CI, release, setup-dev, tài liệu lệnh test); T3 chỉ còn `Qpdf` và thao tác xoay. Đọc PDF bằng `FPDF_LoadCustomDocument` qua handle .NET (lý do trong ADR-0008). 20 test đỏ trước (stub), rồi xanh; bỏ khóa trong `Render` → test 8 luồng làm sập test host (đối chứng âm). Chưa nối `MemoryBudget`: `GetRenderSize(...).Bytes` cho thao tác T10 xin trước khi render. Bộ nhớ riêng của pdfium (ảnh lớn trong trang) chưa giới hạn: để T18.
- Phụ thuộc: T1. Cỡ: M

### T10: `pdf.to-jpg`, `pdf.to-png`
- [ ] 150 DPI, JPEG q90; thư mục `<tên> (pages)`; tiến độ theo trang; đóng gói `pdfium.dll` + notices (freetype, libjpeg-turbo, openjpeg, lcms, zlib, …)
- Kiểm: PDF 200 trang (fixture sinh) → 200 ảnh, hủy giữa chừng sạch; test-installer kiểm `pdfium.dll` và notices
- Phụ thuộc: T7, T9. Cỡ: M

### T11: `pdf.from-images` (ảnh → một PDF)
- [ ] JPEG nhúng nguyên byte trừ APP1/APP13/COM (so hash luồng DCT); EXIF hướng → ma trận xoay trang; HEIC/HEIF/AVIF/WebP → JPEG q92; PNG/GIF/BMP/TIFF → Flate; trong suốt → trắng; trang ≤ A4/Letter theo cạnh dài
- [ ] Tên `<tên đầu> (+N−1).pdf`; menu ảnh có `Combine into PDF`; `Producer: Flevert`, không metadata khác
- [ ] Cập nhật spec (pdfium thay bộ ghi tự viết)
- Kiểm: 12 ảnh gồm ảnh dọc EXIF 6 và `portrait-gps.heic` → 12 trang đúng thứ tự, đúng chiều (render trang kiểm điểm mẫu), không còn chuỗi GPS/Make trong byte; `qpdf --check` sạch
- Phụ thuộc: T5, T9. Cỡ: M

### Checkpoint 3
- [ ] Review ba góc cho T9–T11 (trọng tâm: P/Invoke, input không tin cậy, rò metadata); reconcile

## Giai đoạn 4: qpdf còn lại

### T12: `pdf.strip-metadata`
- [ ] Bỏ Info (giữ `Producer: Flevert`), XMP, tệp đính kèm, JavaScript/OpenAction; kiểm bằng `qpdf --json`
- Phụ thuộc: T3. Cỡ: S

### T13: `pdf.compress` (lossless)
- [ ] object streams, nén lại Flate mức 9; không nhỏ hơn → bỏ qua (skipped)
- Phụ thuộc: T3. Cỡ: S

### T14: `pdf.protect`, `pdf.unlock` + hộp thoại mật khẩu
- [ ] Hộp thoại WinForms: che ký tự, nhập lại (protect), "Use this password for all N files"; Cancel = bỏ qua
- [ ] Mật khẩu chỉ qua stdin (`@-`, `--password-file=-`), `char[]` ghi đè sau khi dùng; AES-256; sai mật khẩu → lỗi file đó, file khác vẫn chạy
- [ ] Test rò rỉ: mật khẩu đánh dấu không có trong log, dòng lệnh tiến trình con (WMI), file tạm
- Kiểm: chạy thật qua menu; ảnh chụp hộp thoại (báo trước); UI Automation tên trợ năng
- Phụ thuộc: T2, T3. Cỡ: M

### Checkpoint 4
- [ ] Review ba góc cho T12–T14 (trọng tâm: mật khẩu); reconcile

## Giai đoạn 5: tùy chọn (bỏ được nếu không kịp)

### T15: Spike compress-sharing
- [ ] Thử trên 3 PDF scan (sinh): thay ảnh bằng pdfium, lưu, qpdf nén → nhỏ hơn ≥ 40%, render vẫn đọc được; ghi kết quả; quyết làm hay bỏ
- Phụ thuộc: T9. Cỡ: S

### T16: `pdf.compress-sharing` (nếu T15 đạt)
- Phụ thuộc: T15. Cỡ: M

### T17: `pdf.reverse`, `pdf.repair`, `pdf.extract-text`, `pdf.extract-images` (theo thứ tự, mỗi cái một commit)
- Phụ thuộc: T3/T7/T9. Cỡ: S mỗi cái

## Giai đoạn 6: hoàn thiện và phát hành

### T18: Hardening input độc
- [ ] Fixture: bom nén, vòng tham chiếu object, 100 000 trang rỗng, trang khổng lồ, xref hỏng, file không phải PDF đổi đuôi → bị từ chối hoặc xử lý trong ≤ 10 s, bộ nhớ trong `MemoryBudget`
- [ ] PDF có chữ ký số: thông báo nói chữ ký không còn hợp lệ ở output

### T19: Nâng cấp từ 0.1.0
- [ ] `test-installer.ps1`: cài 0.1.0 (tải từ Releases, kiểm SHA256 `aa85392b…92e3`) rồi cài 0.2.0 đè; giữ lựa chọn menu cổ điển và kiểm cập nhật; menu có mục PDF; gỡ sạch

### T20: Docs và phát hành v0.2.0
- [ ] README (menu PDF, ảnh động mới cho Combine into PDF), CHANGELOG `[0.2.0]`, capability-map, HANDOFF; `<Version>0.2.0`
- [ ] `/ship`: ba chuyên gia song song → go/no-go; phát hành theo RELEASING (CI ×3, tag, kiểm bản nháp, VirusTotal); người dùng v0.1.0 thấy hộp thoại "Flevert 0.2.0 is available" (kiểm bằng bản 0.1.0 thật sau khi publish)

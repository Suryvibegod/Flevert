# Todo: ocr (v1.4.0)

Spec: [docs/specs/ocr.md](../../docs/specs/ocr.md). ADR: [0011](../../docs/adr/0011-tesseract-static-build.md). Nhánh `feat/v1.4-ocr`.

## Bối cảnh phiên 2026-10-21

Phiên trước giao việc cho worker của `ceo-worker-agent` trong 5 worktree `ceo/ocr-*` (thư mục `D:\github\Flevert-wt\`). Chỉ engine được commit (21ca96b); bốn worktree kia để lại thay đổi chưa commit, và tất cả dùng tạm `tesseract.exe` của bộ cài UB Mannheim chép tay vào `deps/tesseract`. Repo sau đó được dời từ `D:\Flevert` sang `D:\github\Flevert`, nên git báo các worktree là "prunable"; đã chạy `git worktree repair` ngày 2026-10-22. Phiên 2026-10-22 gom việc của chúng về nhánh này, review như mã của mình.

## Việc

- [x] T0 Engine `Flevert.Ocr` (`Tesseract.cs`): tiến trình riêng, job object, trần 1 GB, OSD ≥ 14. (21ca96b)
- [x] T1 Bản dựng tĩnh (người dùng chốt 2026-10-22): `build-tesseract.yml` + `scripts/build-tesseract.ps1` trên main (2e49eec, fdaf69d). [Run 39769558741](https://github.com/Suryvibegod/Flevert/actions/runs/39769558741) đạt; release `deps-tesseract-5.5.3` bất biến, pre-release; zip `f9e9f86b…` (3,3 MB, exe 6,8 MB), attestation kiểm bằng `gh attestation verify --signer-workflow … --source-ref refs/heads/main` đạt; dumpbin chỉ 8 DLL hệ thống. Ghim trong `scripts/deps.json` (f6ee52b).
  - Lượt 1 (39769079343) lỗi: ghim nhầm SHA của tag object `2026.10.16`; commit thật `9eec002d…`.
  - Port vcpkg kéo theo libcurl (Schannel), libarchive (bzip2, lz4, lzma, zstd, xar, CNG) và OpenSSL 3.6.5: không copyleft, nhưng có bản vá bảo mật của chúng thì phải dựng lại (đổi tag release trong workflow, ghim mới).
- [x] T2 `image.to-text` thay `Convert to > BMP` (d1ba8f3). Sửa so với worktree: thư mục tạm qua `PrivateFolder` (dời sang Core), bắt lỗi cụ thể.
- [x] T3 `pdf.make-searchable` (61f456e). Sửa: `IsLiteralLine` chỉ nhận `--to=`/`--from=` kèm phạm vi trang hợp lệ; song song tối đa `Tesseract.MaxParallel` (lõi, và RAM/4 chia 1 GB); thêm test (worktree không có).
- [x] T4 `pdf.to-text` OCR trang scan: **không** dùng patch `ocr-pdftext` (nó bỏ `SingleProcess` của pdf-worker để worker tự chạy tesseract). Làm lại ở leader bằng `RenderOcrPagesAsync`; chữ OCR chỉ thay chữ sẵn có khi nhiều hơn; tiến độ lượt đọc là "scanned page N of M" (cả Make searchable).
- [x] T5 Đóng gói (f6ee52b): publish kèm `tesseract\` (trừ `licenses\`), `installer/Flevert.iss` thư mục riêng ngoài lượt giải nén check-location, notices, Installer/NoticesScriptTests.
- [x] T5b README/CHANGELOG (mục Unreleased). `build-installer.ps1` thật đạt: setup 100,2 MB (1.3.0: 97,7 MB). Thử riêng với ISCC: `Excludes: "tesseract\*"` loại cả thư mục con, nên `tessdata\configs\pdf` không vào lượt giải nén check-location.
- [x] T6 File độc: bảng PDF độc (`PdfHardeningTests`) đã lặp mọi `PdfTools`, nên tự chạy qua `pdf.make-searchable` và `pdf.extract-text` có OCR, đạt; ảnh đi qua `ImageFile.Read` chung. Hủy giữa chừng: test Make searchable (hủy lúc tesseract đang đọc, không file dở, không tesseract nào còn chạy) và test ảnh có sẵn. Trang scan nằm ngang được `/Rotate` 90/180/270 dựng thẳng: lớp chữ đúng chỗ.
- [x] T6b Make searchable dùng OSD như Convert to > Text: trang hiển thị nằm ngang/lộn ngược (scan sai chiều, không `/Rotate` sửa) được dựng thẳng trước tesseract, lớp chữ xoay ngược lại bằng `qpdf --rotate`, và qpdf theo `/Rotate` của trang lớp khi overlay. Test RED trên mã cũ ở 90°/180° (270° tesseract tự đọc được), GREEN sau sửa. Ghi nhận:
  - OSD chỉ tin trang đủ chữ: 2 dòng cho độ tin cậy ≈ 2,6 (< 14, đọc nguyên trạng); 10 dòng ≈ 15; 30 dòng 16–19. Test dùng trang 30 dòng.
  - ~~Trang lộn ngược 180° giữ nguyên hướng nhìn (spec), nên pdfium đọc từ trong mỗi dòng theo thứ tự ngược (tìm từ đơn được, tìm cụm thì không).~~ **Thay bằng R5** (2026-10-25): trang OSD chắc là nằm ngang/lộn ngược được dựng thẳng bằng `/Rotate`, lớp chữ không xoay nữa.
  - Mỗi trang scan thêm một lượt tesseract OSD (như Convert to > Text).
- [x] T6c OSD chưa chắc (nhánh `fix/osd-fallback`): chữ không chân (Arial, Segoe UI, Tahoma) cho OSD 8–9 với 5 dòng, 13,5–19 với 30 dòng, nên trang lộn ngược bị đọc ra rác. Từ 1,5 đến dưới 14, góc ≠ 0: đọc cả nguyên lẫn xoay (cùng lúc), giữ bản có độ tin cậy từ trung bình cao hơn (chênh ≤ 2 thì theo hướng dòng trong TSV); Make searchable dùng góc thật sự đã xoay cho `/Rotate`. Bảng đo trong ocr.md "Đo OSD". Test RED trên mã cũ (ảnh 180/270 và 2 dòng; PDF 90/180/270 không `/Rotate`, Convert to > Text 180), GREEN sau sửa. Mã đo không commit.
- [x] T7 Icon riêng cho Make searchable: `scan_text` (Fluent Filled, ghim SHA256 trong `make-menu-icons.py`), lý do chọn ở ocr.md "Icon".
- [x] T7b Make searchable bỏ qua (skipped "No text was found in the scanned pages of this PDF.") khi tesseract không thấy chữ trên trang scan nào, như Convert to > Text của ảnh; không ghi file. `WriteTextLayerAsync` trả về chữ của lớp (`-c tessedit_create_txt=1`, cùng lượt đọc). Bảng PDF độc: `pdf.make-searchable` giờ là Skipped với các file độc trang trắng (trước là Ok).
- [x] Trước release: app thật, MenuProbe, installer (256 kiểm tra trên máy sạch), ba review, reconcile, hai reviewer gộp; phát hành 1.4.0 ngày 2026-10-25 (HANDOFF).
- [x] Đã xóa worktree `D:githubFlevert-wt*` và nhánh `ceo/ocr-*` (người dùng đồng ý 2026-10-25).

## Review trước release (2026-10-25): reconcile

Ba review chỉ đọc, bối cảnh trống, phạm vi v1.3.0..HEAD + working tree + origin/main (Linux PR #5): code-reviewer, security-auditor, doubt-driven. Không có Critical. Gộp trùng:

| # | Mức | Vấn đề | Xử lý |
|---|---|---|---|
| R1 | High | tesseract đọc argv/`TESSDATA_PREFIX` qua code page ANSI: đường dẫn có chữ Việt (tài khoản `Dương`) hỏng mọi OCR. Đã thấy thật khi dò T6b (`cannot read input file …?nh…`). | Sửa: manifest `activeCodePage=UTF-8` trong `build-tesseract.ps1`, smoke test trong thư mục tên Việt, dựng lại release tesseract; test `ScratchRoot` + tessdata dưới thư mục tên Việt. |
| R2 | High | Ngân sách render OCR (`PdfToolsWorker.RenderOcrPages`) cộng mọi trang, không chỉ trang sẽ đọc: PDF > ~575 trang A4 có một trang trống hỏng Convert to > Text (1.3.0 làm được); đòi ổ trống cho mọi trang. | Sửa: lọc trang trước rồi mới tính ngân sách; thông điệp riêng cho OCR; test PDF chữ dài có một trang trống. **Xong** 2f27cc8: chỉ tính trang scan, thông điệp OCR riêng, dòng `checked <n>` giữ watchdog. |
| R3 | High | Gộp main: Linux build hỏng (`RequireTesseract`, test WinForms), menu Nautilus có mục OCR không chạy được, nguy cơ mất nhánh Linux của `PrivateFolder`, hai ADR-0010. | Sửa trong commit gộp main: lọc `NeedTesseract` trên Linux, `[WindowsFact]`, ADR tesseract → 0011, kiểm `PrivateFolder` giữ nhánh Linux. CI ubuntu xác nhận. **Xong** ca2611e/e486de9; chờ CI ubuntu. |
| R4 | High | Cổng release chạy bốn suite, không chạy `Flevert.Ocr.Tests` (`release.yml`, `setup-dev.ps1`, CLAUDE.md, HANDOFF). | Sửa. **Xong** trong ca2611e (release.yml, setup-dev, CONTRIBUTING, CLAUDE.md). |
| R5 | High→quyết | Trang lộn ngược 180° giữ nguyên: Ctrl+F cụm từ trong Edge/Chrome hỏng. | **Người dùng chốt 2026-10-25: dựng thẳng trang** bằng `/Rotate` khi OSD chắc (như OCRmyPDF, không mã hóa lại); sửa spec. **Đã làm:** `--rotate=+N:<trang>` trong cùng lượt `qpdf --overlay` (qpdf xoay trang trước rồi mới đặt lớp chữ theo hướng hiện của trang), lớp chữ không xoay; test so với trang gốc xoay bằng Rotate, chữ đúng thứ tự ở 90/180/270, và hộp ký tự pdfium nằm trên điểm ảnh tối (phần "vị trí lớp chữ" của R9). **Xong** c5c0f56. |
| R6 | Medium | `TurnUprightAsync` đọc PNG từ pdf-worker bằng `new MagickImage(path)` trong flevert.exe: không policy/giới hạn, đoán định dạng. | Sửa: `MagickSetup.EnsureInitialized` + `Format = Png` (hoặc `ImageFile.Read`). **Xong** c6deb85: `EnsureInitialized`, đọc stream định dạng PNG. |
| R7 | Medium | Một trang OCR lỗi (timeout, trần bộ nhớ, OSD lỗi) làm hỏng cả PDF → Text, mất chữ pdfium có sẵn. | Sửa: lỗi OCR theo trang giữ chữ pdfium + `OutputNote`; OSD lỗi → 0 độ. **Xong** ecec2fb: giữ chữ pdfium từng trang + ghi chú trang lỗi. |
| R8 | Medium | Bộ nhớ tesseract ngoài ngân sách chung khi batch ảnh (8 file × 1 GB); phóng 2× không trần; ảnh 50–200 MP đưa thẳng vào tesseract. | Sửa: cổng chung `Tesseract.MaxParallel` cho mọi lần chạy; phóng/thu để cạnh dài ≤ ~6000 px và trong giới hạn ImageMagick. **Xong** a5a5f4c/c6deb85: cổng chung `MaxParallel`; trần 25 MP, 30000 px/cạnh (chọn diện tích thay cạnh dài 6000 px để ảnh chụp màn hình dài vẫn đọc được). |
| R9 | Medium | Test chưa chứng minh spec: hủy ảnh trước khi bắt đầu, không kiểm `%TEMP%\flevert-ocr-*` sạch; xoay ảnh chỉ 90° (tesseract tự đọc được); không test 180°/270°, góc OSD chính xác; vị trí lớp chữ không kiểm. | Sửa: `ScratchRoot` vào thư mục test và kiểm rỗng; hủy khi tesseract đang chạy; test ảnh 180°/270° RED khi tắt OSD; kiểm hộp ký tự so với điểm ảnh tối. **Xong** 12672c4 (ảnh 90/180/270, góc chính xác, hủy khi tesseract chạy, scratch sạch, HEIC) và vị trí lớp chữ trong c5c0f56. Phát hiện: Arial OSD 11–18, hay < 14 → agent dự phòng OSD đang làm. |
| R10 | Medium | Trang scan có header/số trang > 10 ký tự không bao giờ được OCR; worker đếm cả khoảng trắng, leader không. | Thống nhất một định nghĩa (không tính khoảng trắng); ghi giới hạn trong spec/README. **Xong** 2f27cc8 (một định nghĩa, không tính khoảng trắng); giới hạn ghi trong spec/README (cdbc8ee). |
| R11 | Medium | Không có kiểm OCR sau khi cài (`test-installer.ps1`). | Thêm bước Convert to > Text trên PNG sau cài. **Xong** 823e3e9; chạy thật ở workflow kiểm bộ cài khi release (nâng timeout). |
| R12 | Low | PDF → Text xóa file tạm rồi tạo lại bằng `FileMode.Create` (khe chen trong thư mục chung). | Sửa: ghi đè tại chỗ hoặc `CreateNew` + đổi tên. **Xong** 883fcd3 (thay ecec2fb, review gộp thấy `Truncate` theo tên ghi xuyên link): ghi file mới `CreateNew` rồi `File.Move` thay tên; test hard link RED/GREEN. |
| R13 | Low | tesseract.exe kèm libcurl/libarchive; "không mạng" chỉ nhờ kiểm tham số. | Sửa cùng R1: `DISABLE_CURL`/`DISABLE_ARCHIVE`, thêm DLL mạng vào danh sách cấm; sửa chữ spec/ADR. |
| R14 | Low | Job `attest` chạy mọi ref; RELEASING không `gh attestation verify` zip tesseract; không kiểm danh sách port vcpkg. | Sửa: `if: main`, bước verify, allow-list port. |
| R15 | Low | `.claude/settings.json` bật plugin không ghim. | **Người dùng chốt: ghim commit/tag.** **Xong** 1ddb2a2/9da5ad4. |
| R16 | Low | Lặt vặt: `ParseOsdRotation` nhận số bất kỳ (chỉ 0/90/180/270); thông điệp "this image" cho PDF; tiến độ lượt render thứ hai `null`; Make searchable không thấy chữ vẫn ghi file; doc `PageTimeout` lạc; Test SDK của Ocr.Tests cũ; README/CHANGELOG nói quá; ADR ghi 5.5.0; notices OpenSSL/zstd/lzma. | Sửa từng mục. **Xong** phần lớn: ecec2fb, c6deb85, 65d89bf (Make searchable không thấy chữ → bỏ qua), 87ea557…ca40f7e, 0f0ca70, 1891a7d. |
| — | — | Đã ổn theo review: kill khi hủy, `Parallel.ForEachAsync`, qpdf `@-`/`--rotate`, hardening tesseract, deps ghim, Linux PR. | — |

## Kiểm runtime trước release (2026-10-25)

Bản kiểm: `feat/v1.4-ocr` 32091d2 (có T6c, sửa lỗi dự phòng OSD và tesseract `deps-tesseract-5.5.3-2` của R1/R13). PDF, hủy, đường dẫn chữ Việt và khởi động đo trên f40c386: từ đó tới 32091d2 chỉ `ImageToText.cs` đổi, nên phần ảnh và bộ cài đã chạy lại trên 32091d2. `flevert.exe` publish Release, self-contained, ReadyToRun, chạy headless: `FLEVERT_HEADLESS=1 flevert.exe run <op-id> <file>`. Máy 16 luồng, 31,8 GB RAM; agent khác build/test cùng lúc (CPU 3–45 %). Bộ nhớ là đỉnh `PeakWorkingSet64`, lấy mẫu 40 ms một lần, chỉ tính tiến trình của bản publish này. Độ đúng = 1 − Levenshtein/độ dài, trên chữ NFC đã gộp khoảng trắng.

File thử đều tự tạo:

- Trang A4 300 DPI, 30 dòng chữ Việt cỡ 44 px, Arial và Times New Roman (vẽ bằng Pillow).
- Trang xoay 90° ngược chiều kim đồng hồ và 180°; với Arial thêm 270°.
- JPG q90 và HEIC (pillow-heif).
- PDF scan tạo bằng `pdf.from-images` của Flevert. PDF trộn: 2 trang chữ thật (PyMuPDF, Arial nhúng) và 1 trang scan, ghép bằng `pdf.merge`. Thêm một PDF scan 20 trang, Arial và Times xen kẽ.

1. **Publish:** 276 file, 391.469.832 byte.
   - `tesseract\`: `tesseract.exe` 5.442.048 byte (5.5.3, leptonica 1.87.0, dựng 24/10); tessdata `eng` 4.113.088, `vie` 531.275, `osd` 10.562.727, `pdf.ttf` 572, `configs\pdf` 22. Không có `licenses\`.
   - `icons\scan_text.ico` 26.054 byte.
   - Trong `tesseract.exe` chỉ có tên DLL `kernel32.dll`, và có manifest `activeCodePage` UTF-8. THIRD-PARTY-NOTICES ghi `deps-tesseract-5.5.3-2` và không còn libcurl.
2. **Installer** (`scripts/build-installer.ps1`, 2 phút 34 s, không chạy bộ cài): `Flevert-Setup-1.4.0.exe` 104.610.720 byte (99,8 MiB), SHA256 `ab4b3bb3cc933ea7…f95410f8`.
   - So với 1.3.0 (97.674.127 byte): +6.936.593 byte (+7,1 %).
   - Nhỏ hơn 100,2 MB đo ở T5b: cùng ngày, bản dựng ở ac9ad88 với tesseract cũ (có libcurl/libarchive) nặng 105.139.062 byte.
3. **Chạy thật:**

| Thao tác | File | Thời gian | flevert.exe đỉnh | tesseract.exe | Độ đúng |
|---|---|---|---|---|---|
| Ảnh → Text | PNG Arial 0/90/180/270° | 3,88 / 4,03 / 3,07 / 3,01 s | 78–81 MB | 2 lượt; 3 lượt khi OSD chưa chắc (90°, 270°), 2 cùng lúc; 73 MB | 99,69 % cả bốn |
| | PNG Times 0/90/180° | 3,82 / 3,01 / 4,29 s | 78–81 MB | 2 lượt, 73 MB | 98,90 % cả ba |
| | JPG (Arial) / HEIC (Times) | 3,59 / 2,59 s | 85 / 97 MB | 73 / 72 MB | 99,39 / 99,51 % |
| PDF → Text | scan Arial 3 trang 0/90/180°, không `/Rotate` | 5,65 s | 133 MB (leader + worker 160) | 7 lượt, tối đa 4 cùng lúc, 73 MB mỗi tiến trình, tổng 288 MB | 99,69 % mỗi trang |
| | scan Times 3 trang | 4,35 s | 133 MB | 6 lượt, 3 cùng lúc, tổng 219 MB | 98,90 % mỗi trang |
| | trộn (2 trang chữ + 1 scan) | 3,04 s | 133 MB | 2 lượt, chỉ trang scan | trang chữ 100 %, trang scan 98,90 % |
| | scan 20 trang | 17,32 s | 147 MB (tổng 184) | 40 lượt, tối đa 7 cùng lúc, 74 MB, tổng 507 MB | 99,21 %, trang thấp nhất 98,76 % |
| Make searchable | scan Arial 3 trang | 4,72 s | 133 MB (tổng 160) | 7 lượt, 4 cùng lúc, tổng 287 MB | 99,69 % mỗi trang |
| | scan Times 3 trang | 4,36 s | 133 MB | 6 lượt, tổng 217 MB | 98,90 % mỗi trang |
| | trộn | 3,04 s | 133 MB | 2 lượt | 100 / 100 / 98,90 % |
| | scan 20 trang | 21,19 s (1,06 s/trang) | 147 MB (tổng 188) | 40 lượt, 7 cùng lúc, 74 MB, tổng 504 MB | 99,21 %, thấp nhất 98,76 % |

   - **File searchable:**
     - `/Rotate`: các trang scan 0/90/180° thành 0, 90, 180 (cả Arial lẫn Times). PDF trộn và PDF 20 trang giữ 0.
     - Số trang và MediaBox không đổi. Render 40 DPI, so với trang gốc xoay đúng góc đó: 0 điểm ảnh khác, trên cả 29 trang.
     - Đọc chữ của file searchable bằng pdfium (Convert to > Text của Flevert: 1,67–1,70 s, flevert.exe 41 MB, không gọi tesseract vì trang đã có chữ) và bằng MuPDF theo thứ tự nội dung: độ đúng như bảng, nên từ vẫn đúng thứ tự trên trang dựng thẳng 90°/180°.
     - Tìm cụm từ ("phường thông báo lịch tiêm chủng", "hoạt động suốt hai mươi bốn giờ") thấy trên mọi trang.
     - Trang chữ thật của PDF trộn giữ 178 ký tự, khớp 100 %: không bị chồng lớp chữ thứ hai.
   - **Hủy giữa chừng:** CLI không có lệnh hủy. Nút Cancel chỉ có trên cửa sổ tiến độ, và lần kiểm này không mở cửa sổ trên máy người dùng. Thay vào đó, kill cứng `flevert.exe` (`Stop-Process -Force`) ở giây 9,4 của Make searchable 20 trang, lúc 7 tesseract đang chạy:
     - 1,5 s sau không còn tesseract.exe hay flevert.exe nào của bản publish (pdf-worker cũng đã tắt).
     - Cạnh PDF không có file kết quả hay file dở.
     - Còn lại `%TEMP%\flevert-ocr-<guid>`: 20 PNG trang 300 DPI, 4.660.719 byte (K2).
   - **Đường dẫn chữ Việt (R1):** app chạy qua junction `…\Dương\app`, `TEMP`/`TMP` = `…\Dương\tmp`, file nằm trong `…\Dương\files`.
     - Ảnh → Text 98,90 %; Make searchable và PDF → Text đạt, 98,90 % mỗi trang; thư mục tạm sạch.
     - Cùng bài trên ac9ad88 (tesseract `deps-tesseract-5.5.3`) hỏng: `cannot read input file …\Duong\tmp\flevert-ocr-…\input.png`, và người dùng nhận thông báo "the image may be damaged".
4. **MenuProbe** (bản dc31f30; mã menu không đổi từ đó tới f40c386).
   - Máy này không cài Flevert: không có mục gỡ cài, không có khóa menu. Chạy `install-menu --allow-insecure-location` từ bản publish (thiếu cờ thì thư mục dev bị từ chối, mã 3), rồi `uninstall-menu` của chính bản đó. Sau đó còn 0 khóa `Flevert.*`, và `SystemFileAssociations` về lại đúng 14 khóa có từ trước.
   - **.png, .jpg, .heic, .bmp:** mục Text đứng cuối Convert to, không còn BMP. Đích của png: JPG, WebP, AVIF, GIF, TIFF, Text; heic/bmp: JPG, PNG, WebP, AVIF, TIFF, Text. Cây Flevert có 16 mục (không tính đường kẻ) và hiện đủ, kể cả Combine into PDF. Giữ Shift không đổi menu ảnh. Lệnh là `run image.to-text "%1"`.
   - **.pdf:** menu thường có 16 mục; Convert to gồm JPG images và Text (`run pdf.extract-text`). Giữ Shift thì có thêm "Flevert: more PDF tools" (Extended, icon toolbox) với 13 mục; mục cuối, sau đường kẻ, là "Make searchable (OCR)" với icon `icons\scan_text.ico` và lệnh `run pdf.make-searchable "%1"`.
5. **measure-startup** (`-Runs 20`, hai lượt): median 84 / 83 ms (min 82 / 79, max 111 / 105). Trên dc31f30, lúc máy tải cao hơn: 107 / 111 ms. Đạt ≤ 300 ms.

Phát hiện:

- **K1 (High, đã sửa trước khi ghi):** trên dc31f30 (trước T6c), trang Arial xoay 90° có OSD 11,04 (< 14) nên được đọc nguyên trạng, và tesseract đọc nó lộn ngược.
  - Kết quả 17,38 % ở cả Ảnh → Text lẫn PDF → Text. Make searchable để trang nằm ngang, kèm lớp chữ rác: tìm cụm từ không thấy gì.
  - OSD cùng trang ở các góc khác: Arial 0° 12,54, 180° 14,03, 270° 12,39; Times 90° 29,81.
  - Trên f40c386 (T6c đọc cả hai hướng), cả bốn góc đạt 99,69 % và trang được `/Rotate` 90. Không cần làm thêm.
- **K2 (Low, mở):** kill cứng hoặc tắt máy giữa chừng để lại `%TEMP%\flevert-ocr-<guid>` chứa ảnh 300 DPI các trang tài liệu của người dùng (4,7 MB cho 20 trang, tăng theo số trang).
  - Lần chạy sau không dọn: chỉ `VideoCompress` có `DeleteStaleRuns`, còn thư mục `flevert-*` của các công cụ PDF khác cũng để lại như vậy.
  - Hủy bằng nút Cancel thì đã có test tự động chứng minh dọn sạch (T6, R9).
  - Đề xuất: dọn `flevert-ocr-*`/`flevert-*` cũ hơn 1 ngày, như video đang làm.
- **K3 (Nit, đã sửa):** bảng thao tác trong docs/specs/ocr.md ghi op-id `pdf.to-text`, nhưng op-id thật là `pdf.extract-text` (theo `flevert list-ops` và lệnh menu).
- **Quy trình:** `feat/v1.4-ocr` có commit mới hai lần trong lúc kiểm (ac9ad88 → dc31f30 → f40c386). Bước 1–3 và 5 đã chạy lại toàn bộ trên f40c386; sau đó feat có thêm 32091d2 (chỉ `ImageToText.cs`), nên chạy lại phần ảnh và bộ cài trên commit đó (số ghi ở trên). Lần chạy trước của phần ảnh trên f40c386 cho cùng độ đúng. Một lượt PDF → Text 20 trang trên dc31f30 mất 93,73 s vì VBCSCompiler của agent khác chiếm CPU; chạy lại còn 26,41 / 18,13 s.

## Ghi chú

- 7-Zip/bộ cài UB Mannheim không còn cần (ADR-0011). Không đưa DLL GPL/LGPL nào vào bộ cài.
- Bộ test sau T5 (bản tĩnh): Core 532/2 skip, Image 240, Pdf 1410/1 skip, Media 225, Ocr 13.

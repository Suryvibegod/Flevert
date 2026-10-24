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
- [ ] Trước release: app thật, MenuProbe, installer, ba review, reconcile; theo docs/RELEASING.md.
- [ ] Sau khi gom xong: xóa worktree `D:\github\Flevert-wt\*` và nhánh `ceo/ocr-*` (≈ 15 GB).

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

## Ghi chú

- 7-Zip/bộ cài UB Mannheim không còn cần (ADR-0011). Không đưa DLL GPL/LGPL nào vào bộ cài.
- Bộ test sau T5 (bản tĩnh): Core 532/2 skip, Image 240, Pdf 1410/1 skip, Media 225, Ocr 13.

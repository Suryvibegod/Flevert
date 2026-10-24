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
  - Trang lộn ngược 180° giữ nguyên hướng nhìn (spec), nên pdfium đọc từ trong mỗi dòng theo thứ tự ngược (tìm từ đơn được, tìm cụm thì không). Muốn sửa phải đổi `/Rotate` của trang, trái spec; giữ nguyên.
  - Mỗi trang scan thêm một lượt tesseract OSD (như Convert to > Text).
- [x] T7 Icon riêng cho Make searchable: `scan_text` (Fluent Filled, ghim SHA256 trong `make-menu-icons.py`), lý do chọn ở ocr.md "Icon".
- [ ] Trước release: app thật, MenuProbe, installer, ba review, reconcile; theo docs/RELEASING.md.
- [ ] Sau khi gom xong: xóa worktree `D:\github\Flevert-wt\*` và nhánh `ceo/ocr-*` (≈ 15 GB).

## Review trước release (2026-10-25): reconcile

Ba review chỉ đọc, bối cảnh trống, phạm vi v1.3.0..HEAD + working tree + origin/main (Linux PR #5): code-reviewer, security-auditor, doubt-driven. Không có Critical. Gộp trùng:

| # | Mức | Vấn đề | Xử lý |
|---|---|---|---|
| R1 | High | tesseract đọc argv/`TESSDATA_PREFIX` qua code page ANSI: đường dẫn có chữ Việt (tài khoản `Dương`) hỏng mọi OCR. Đã thấy thật khi dò T6b (`cannot read input file …?nh…`). | Sửa: manifest `activeCodePage=UTF-8` trong `build-tesseract.ps1`, smoke test trong thư mục tên Việt, dựng lại release tesseract; test `ScratchRoot` + tessdata dưới thư mục tên Việt. |
| R2 | High | Ngân sách render OCR (`PdfToolsWorker.RenderOcrPages`) cộng mọi trang, không chỉ trang sẽ đọc: PDF > ~575 trang A4 có một trang trống hỏng Convert to > Text (1.3.0 làm được); đòi ổ trống cho mọi trang. | Sửa: lọc trang trước rồi mới tính ngân sách; thông điệp riêng cho OCR; test PDF chữ dài có một trang trống. |
| R3 | High | Gộp main: Linux build hỏng (`RequireTesseract`, test WinForms), menu Nautilus có mục OCR không chạy được, nguy cơ mất nhánh Linux của `PrivateFolder`, hai ADR-0010. | Sửa trong commit gộp main: lọc `NeedTesseract` trên Linux, `[WindowsFact]`, ADR tesseract → 0011, kiểm `PrivateFolder` giữ nhánh Linux. CI ubuntu xác nhận. |
| R4 | High | Cổng release chạy bốn suite, không chạy `Flevert.Ocr.Tests` (`release.yml`, `setup-dev.ps1`, CLAUDE.md, HANDOFF). | Sửa. |
| R5 | High→quyết | Trang lộn ngược 180° giữ nguyên: Ctrl+F cụm từ trong Edge/Chrome hỏng. | **Người dùng chốt 2026-10-25: dựng thẳng trang** bằng `/Rotate` khi OSD chắc (như OCRmyPDF, không mã hóa lại); sửa spec. |
| R6 | Medium | `TurnUprightAsync` đọc PNG từ pdf-worker bằng `new MagickImage(path)` trong flevert.exe: không policy/giới hạn, đoán định dạng. | Sửa: `MagickSetup.EnsureInitialized` + `Format = Png` (hoặc `ImageFile.Read`). |
| R7 | Medium | Một trang OCR lỗi (timeout, trần bộ nhớ, OSD lỗi) làm hỏng cả PDF → Text, mất chữ pdfium có sẵn. | Sửa: lỗi OCR theo trang giữ chữ pdfium + `OutputNote`; OSD lỗi → 0 độ. |
| R8 | Medium | Bộ nhớ tesseract ngoài ngân sách chung khi batch ảnh (8 file × 1 GB); phóng 2× không trần; ảnh 50–200 MP đưa thẳng vào tesseract. | Sửa: cổng chung `Tesseract.MaxParallel` cho mọi lần chạy; phóng/thu để cạnh dài ≤ ~6000 px và trong giới hạn ImageMagick. |
| R9 | Medium | Test chưa chứng minh spec: hủy ảnh trước khi bắt đầu, không kiểm `%TEMP%\flevert-ocr-*` sạch; xoay ảnh chỉ 90° (tesseract tự đọc được); không test 180°/270°, góc OSD chính xác; vị trí lớp chữ không kiểm. | Sửa: `ScratchRoot` vào thư mục test và kiểm rỗng; hủy khi tesseract đang chạy; test ảnh 180°/270° RED khi tắt OSD; kiểm hộp ký tự so với điểm ảnh tối. |
| R10 | Medium | Trang scan có header/số trang > 10 ký tự không bao giờ được OCR; worker đếm cả khoảng trắng, leader không. | Thống nhất một định nghĩa (không tính khoảng trắng); ghi giới hạn trong spec/README. |
| R11 | Medium | Không có kiểm OCR sau khi cài (`test-installer.ps1`). | Thêm bước Convert to > Text trên PNG sau cài. |
| R12 | Low | PDF → Text xóa file tạm rồi tạo lại bằng `FileMode.Create` (khe chen trong thư mục chung). | Sửa: ghi đè tại chỗ hoặc `CreateNew` + đổi tên. |
| R13 | Low | tesseract.exe kèm libcurl/libarchive; "không mạng" chỉ nhờ kiểm tham số. | Sửa cùng R1: `DISABLE_CURL`/`DISABLE_ARCHIVE`, thêm DLL mạng vào danh sách cấm; sửa chữ spec/ADR. |
| R14 | Low | Job `attest` chạy mọi ref; RELEASING không `gh attestation verify` zip tesseract; không kiểm danh sách port vcpkg. | Sửa: `if: main`, bước verify, allow-list port. |
| R15 | Low | `.claude/settings.json` bật plugin không ghim. | **Người dùng chốt: ghim commit/tag.** |
| R16 | Low | Lặt vặt: `ParseOsdRotation` nhận số bất kỳ (chỉ 0/90/180/270); thông điệp "this image" cho PDF; tiến độ lượt render thứ hai `null`; Make searchable không thấy chữ vẫn ghi file; doc `PageTimeout` lạc; Test SDK của Ocr.Tests cũ; README/CHANGELOG nói quá; ADR ghi 5.5.0; notices OpenSSL/zstd/lzma. | Sửa từng mục. |
| — | — | Đã ổn theo review: kill khi hủy, `Parallel.ForEachAsync`, qpdf `@-`/`--rotate`, hardening tesseract, deps ghim, Linux PR. | — |

## Ghi chú

- 7-Zip/bộ cài UB Mannheim không còn cần (ADR-0011). Không đưa DLL GPL/LGPL nào vào bộ cài.
- Bộ test sau T5 (bản tĩnh): Core 532/2 skip, Image 240, Pdf 1410/1 skip, Media 225, Ocr 13.

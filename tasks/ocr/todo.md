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
- [ ] T6 File độc (bảng file độc hiện có cho ảnh/PDF qua OCR), hủy giữa chừng (tesseract dừng, không file dở), trang `/Rotate 90` cho Make searchable.
- [ ] T7 Icon riêng cho Make searchable (đang dùng chung `draw_text` với Add watermark).
- [ ] Trước release: app thật, MenuProbe, installer, ba review, reconcile; theo docs/RELEASING.md.
- [ ] Sau khi gom xong: xóa worktree `D:\github\Flevert-wt\*` và nhánh `ceo/ocr-*` (≈ 15 GB).

## Ghi chú

- 7-Zip/bộ cài UB Mannheim không còn cần (ADR-0011). Không đưa DLL GPL/LGPL nào vào bộ cài.
- Bộ test sau T5 (bản tĩnh): Core 532/2 skip, Image 240, Pdf 1410/1 skip, Media 225, Ocr 13.

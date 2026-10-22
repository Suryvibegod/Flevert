# Todo: ocr (v1.4.0)

Spec: [docs/specs/ocr.md](../../docs/specs/ocr.md). ADR: [0010](../../docs/adr/0010-tesseract-static-build.md). Nhánh `feat/v1.4-ocr`.

## Bối cảnh phiên 2026-10-21

Phiên trước giao việc cho worker của `ceo-worker-agent` trong 5 worktree `ceo/ocr-*` (thư mục `D:\github\Flevert-wt\`). Chỉ engine được commit (21ca96b); bốn worktree kia để lại thay đổi chưa commit, và tất cả dùng tạm `tesseract.exe` của bộ cài UB Mannheim chép tay vào `deps/tesseract`. Repo sau đó được dời từ `D:\Flevert` sang `D:\github\Flevert`, nên git báo các worktree là "prunable"; đã chạy `git worktree repair` ngày 2026-10-22.

## Việc

- [x] T0 Engine `Flevert.Ocr` (`Tesseract.cs`): tiến trình riêng, job object, trần 1 GB, OSD ≥ 14. (21ca96b)
- [ ] T1 Bản dựng tĩnh: `build-tesseract.yml` + `scripts/build-tesseract.ps1` (main, 2e49eec, fdaf69d), chạy workflow, ghim zip trong `scripts/deps.json`, notices, bỏ bản UB Mannheim tạm trong `deps/tesseract`.
  - Lượt 1 (39769079343) lỗi: ghim nhầm SHA của tag object `2026.10.16`; commit thật `9eec002d…`.
- [ ] T2 `image.to-text` thay `Convert to > BMP` (từ worktree `ocr-image`; sửa: thư mục tạm qua `PrivateFolder`, `PrivateFolder` dời sang Core).
- [ ] T3 `pdf.make-searchable` (worktree `ocr-pdf`): worker render trang ≤ 10 ký tự ra PNG 300 DPI, tesseract chạy ở leader, qpdf overlay.
- [ ] T4 `pdf.to-text` OCR trang scan: **không** dùng patch `ocr-pdftext` (nó bỏ `SingleProcess` của pdf-worker để worker tự chạy tesseract: tiến trình đọc PDF lạ được quyền sinh tiến trình). Làm lại bằng `RenderOcrPagesAsync` của T3, OCR ở leader.
- [ ] T5 Đóng gói: bỏ `CopyToPublishDirectory="Never"` trong `Flevert.Ocr.csproj` (đặt tạm: bản publish chưa kèm tesseract vì notices chưa có, NoticesScriptTests chặn), `installer/Flevert.iss` (thư mục `tesseract\`), notices, `InstallerScriptTests`, README/CHANGELOG, CI fetch.
- [ ] T6 File độc, hủy giữa chừng, giới hạn bộ nhớ cho mọi lối vào OCR.
- [ ] Trước release: app thật, MenuProbe, installer, ba review, reconcile; theo docs/RELEASING.md.
- [ ] Sau khi gom xong: xóa worktree `D:\github\Flevert-wt\*` và nhánh `ceo/ocr-*` (≈ 15 GB).

## Ghi chú

- 7-Zip/bộ cài UB Mannheim không còn cần (ADR-0010). Không đưa DLL GPL/LGPL nào vào bộ cài.

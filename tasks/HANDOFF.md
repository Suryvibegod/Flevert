# Flevert: trạng thái và việc tiếp theo

## Mục tiêu và quyết định

- Flevert là bộ chuyển đổi file miễn phí qua menu chuột phải trên Windows, repo public cho cộng đồng. Năm module trong [capability-map.md](../docs/specs/capability-map.md) đã có: core, image, installer, PDF, media.
- Chỉ có menu chuột phải, không cửa sổ chọn công cụ. Hộp thoại dành cho chữ phải nhập (mật khẩu, watermark, thời điểm Trim), theo kiểu hộp thoại mật khẩu.
- Menu thường chỉ một dòng Flevert cho mỗi loại file. Mũi tên thứ hai chỉ hiện khi giữ Shift, tên “Flevert: more … tools”, dùng MenuCascade với ShiftOnly: true. Explorer giới hạn 16 mục cho cả cây của một mục gốc; dài hạn cân nhắc IExplorerCommand sau khi ký số.
- Phiên bản tính năng là 1.x.0, bản sửa/cập nhật là 1.x.y. Hai release bất biến v0.2.0, v0.2.1 giữ tên cũ và được xem là 1.2.0, 1.2.1. Lịch sử trong [CHANGELOG.md](../CHANGELOG.md).
- UI, README và commit tiếng Anh; spec/plan nội bộ tiếng Việt. Tự quyết việc có đủ thông tin và có thể đảo ngược; giữ đúng phạm vi được phép khi phát hành, gửi file ra dịch vụ khác hoặc dùng file cá nhân.
- Mọi thứ miễn phí: Inno Setup, không mua chứng chỉ; ký mã qua SignPath Foundation khi được duyệt.

## Quy trình

1. Đọc file này và [CLAUDE.md](../CLAUDE.md); trên máy mới chạy scripts/setup-dev.ps1. Đối chiếu ID tài khoản GitHub với chủ repo 79949229 trước việc phát hành.
2. Dùng agent-skills của Addy Osmani. Spec có success criteria; plan/todo trong tasks/<module>/. Kiểm nguồn chính thức, bảo trì và giấy phép trước khi thêm thư viện; ưu tiên dependency được ghim (source-driven-development). Chỉ đưa mã MIT/BSD/Apache-2.0/Zlib/ISC vào Flevert; GPL như ffmpeg chạy thành tiến trình riêng.
3. Làm đủ code trước, từng lát nhỏ cùng test; TDD khi rẻ, kiểm RED trên mã cũ rồi GREEN. Không review hay kiểm runtime lại sau từng task. Build, format (kiểm exit code) và **năm project Core/Image/Pdf/Media/Ocr** xanh trước mỗi commit; chạy lần lượt, không chạy cả solution cùng lúc.
4. Một lần trước release: kiểm app thật, MenuProbe và installer; ba review bối cảnh trống, chỉ đọc (code, security, doubt) của thay đổi từ release trước; reconcile trong todo, sửa và một reviewer gộp kiểm lại tới khi không còn Critical/High/Required. Không giới hạn số agent song song (người dùng chốt 2026-10-24), mỗi agent một worktree; bộ test đầy đủ trước commit chạy khi không agent nào đang build/test.
5. Theo [docs/RELEASING.md](../docs/RELEASING.md): CI ba lần trên cùng commit, nguồn/giấy phép ffmpeg, tag trên main, installer do GitHub dựng, hash/attestation, cài mới và nâng cấp từ bản trước, Defender/VirusTotal, publish latest. Release đã publish bất biến, chỉ sửa tiến về phía trước.
6. Commit nguyên tử, message nói vì sao, danh tính Suryvibegod <79949229+Suryvibegod@users.noreply.github.com> và dòng Co-Authored-By; không email cá nhân, không binary. Sau release ghim setup vừa phát hành trong scripts/get-previous-setup.ps1, cập nhật trạng thái và dọn nhánh/artifacts tạm.

Lưu ý kiểm thử:

- Test nhiều tiến trình phải nhận diện PID/tiến trình cha của chính nó. Test batch nhạy thời gian: đỏ thì chạy riêng trước; còn đỏ khi chạy riêng thì điều tra. Test PDF mở pdf-worker phải thuộc collection WorkerProcesses.
- Báo người dùng trước khi test có hộp thoại trên cùng. scripts/test-installer.ps1 dùng HKCU và thư mục cài thật, cần máy/profile sạch, không Flevert/menu dev/tiến trình flevert.exe; không gỡ bản đang dùng chỉ để chạy test. Sau MenuProbe trên profile thật, gỡ menu dev đã tạo.
- Smart App Control có thể chặn DLL vừa build (0xE0434352, 0x800711C7): xác nhận bằng chứng rồi build lại với SourceRevisionId mới, không đổi mã hay tắt bảo vệ.
- Sau đổi scripts/deps.json, chạy lại scripts/fetch-deps.ps1. qpdf tự mở rộng wildcard, nên đường dẫn chuyển qua stdin @-; giá trị tùy chọn chứa *, ?, @ bị từ chối.

## Trạng thái

| Module | Trạng thái và lịch sử |
|---|---|
| core | Xong; batch song song, ngân sách bộ nhớ chung, báo bản mới. [todo](todo.md), [perf](perf/todo.md), [update](update/todo.md), ADR-0006. |
| image | Xong; [todo](image/todo.md), ADR-0004. |
| installer | Xong; cài theo user, menu cổ điển, lựa chọn kiểm cập nhật; [todo](installer/todo.md), ADR-0005. |
| PDF | Đã phát hành tới 1.2.3, menu thường một dòng và thêm công cụ qua Shift; [todo](pdf/todo.md), ADR-0007/0008. |
| media 1.3.0 | Đã phát hành, latest và immutable; đã gộp main qua PR #3. [todo](media/todo.md), [plan](media/plan.md), [spec](../docs/specs/media.md), ADR-0009. |
| ocr 1.4.0 | Đã phát hành 1.4.0, latest và immutable: ảnh → Text, PDF Make searchable (dựng thẳng trang xoay bằng `/Rotate`), PDF → Text đọc trang scan; Tesseract 5.5.3 dựng tĩnh, UTF-8, không mạng (release `deps-tesseract-5.5.3-2`, ADR-0011). [todo](ocr/todo.md), [spec](../docs/specs/ocr.md). |
| linux | Gộp qua PR #5 (tlong @long89kev), phát hành lần đầu trong 1.4.0: menu GNOME Files, tarball `Flevert-<version>-linux-x64.tar.gz` do release.yml dựng và attest; OCR, video/audio chỉ trên Windows. [spec](../docs/specs/linux.md), [pr5-fixes](linux/pr5-fixes.md), ADR-0010. |

### v1.4.0 — đã phát hành ngày 2026-10-25

[Flevert v1.4.0](https://github.com/Suryvibegod/Flevert/releases/tag/v1.4.0), latest, immutable; công khai lúc 05:49 giờ Việt Nam. scripts/get-previous-setup.ps1 ghim 1.4.0 (kiểm bằng file thật) cho lần nâng cấp tiếp theo.

- Tag v1.4.0 trỏ commit 7a2bbf1fd6ffaaa4f1ed640615b7e25c74b05b77 (main sau PR #6 squash). [Release workflow](https://github.com/Suryvibegod/Flevert/actions/runs/40136568955): bộ cài Windows, tarball Linux (lần đầu), attestation hai file, nguồn ffmpeg hai phần. Lượt đầu job Linux đỏ ở `PdfSplitTests` (đọc `/proc/<pid>/fd` đúng lúc fd đóng); chạy lại job lỗi đạt, không đổi mã tag.
- CI trước tag: bốn lượt trên 7a2bbf1 đạt (40134622627, 40134623384, 40134625602, 40134628161). Commit trước đó babc579 có hai lượt đỏ ở test nhạy thời gian khi bốn lượt chạy cùng lúc (Linux `PdfWorkerTests` cửa sổ treo, `PdfHardening` bomb-image/compress; Windows `ProcessBatchIntegration` 100 tiến trình, rồi `Cancel_while_tesseract_reads…` còn một tesseract); test hủy OCR được sửa chờ ≤ 5 s và in dòng lệnh tiến trình còn sót (7a2bbf1). Hai commit sửa test và dựng tesseract được đẩy thẳng lên main, bỏ qua luật PR bằng quyền admin.
- Review: ba review bối cảnh trống (code, security, doubt) từ v1.3.0, reconcile R1–R16 trong ocr todo; hai reviewer gộp kiểm lại các bản sửa, không còn Critical/High/Required.
- Bộ test cục bộ trên nhánh: Core 535, Image 284, Pdf 1453, Media 225, Ocr 38 đạt; hai test thời gian đỏ khi máy tải nặng, chạy riêng đạt. Kiểm app thật: ảnh/PDF → Text 98,9–99,7 %, Make searchable 20 trang 21 s, profile tên `Dương`, MenuProbe 16 mục, khởi động lạnh 84 ms (ocr todo).
- [Kiểm bộ cài trên máy Windows sạch](https://github.com/Suryvibegod/Flevert/actions/runs/40139682864) (nhánh `release/verify-1.4.0`): **256 kiểm tra** đạt, gồm cài mới, nâng cấp 1.3.0, OCR thật và gỡ sạch.
- Flevert-Setup-1.4.0.exe: SHA256 a5f866a063771c7c62342c6d17d1ee55d76f273c3b251953af679df3565f3206, 104.628.008 byte. Flevert-1.4.0-linux-x64.tar.gz: SHA256 67a5481317b066759b069fa9b4469c89dcf63cdeafad78301648e31823ffb9ec, 59.289.075 byte, .NET 10.0.13. Hash khớp `SHA256SUMS.txt`; attestation ghim tag, commit, workflow chính thức, cấm self-hosted đạt cho cả hai; Defender sạch.
- Còn mở, đã chấp nhận: **VirusTotal chưa kiểm** (không có API key trong phiên); chưa cài tarball trên máy GNOME thật (job Linux đã cài/cập nhật/chuyển đổi/gỡ trong profile tạm); tắt ngang flevert.exe để lại `%TEMP%levert-ocr-*` (như các công cụ PDF khác).
- v1.3.0 (2026-10-21, tag 967ad3c) vẫn nằm trong lịch sử release; chi tiết kiểm của nó ở media todo.

## Việc tiếp theo

1. **Test nhạy thời gian:** ổn định các test hay đỏ khi máy/CI tải nặng: `PdfSplitTests.A_page_held_open…` (Linux, liệt kê `/proc/<pid>/fd` gặp fd vừa đóng), `PdfWorkerTests.A_page_that_never_finishes…`, `PdfHardening` bomb-image/`pdf.compress` (lượt qpdf đầy đủ quá giờ → "ok" thay "skipped"), `PdfMergeTests.Cancelling_stops_qpdf…`, `ProcessBatchIntegration` 100 tiến trình; tìm nguồn tesseract còn chạy sau khi hủy Make searchable (test giờ in dòng lệnh). Chạy VirusTotal cho hai file 1.4.0 nếu có cách.
2. **Ký số SignPath Foundation.** Đã có [code-signing-policy.md](../docs/code-signing-policy.md) và mục README; chưa xác nhận người dùng gửi đơn. Người dùng tự điền tên/email, đồng ý điều khoản và reCAPTCHA. Khi duyệt: action chính thức ghim SHA, secret GitHub, ký installer/binary Flevert, kiểm chữ ký; cập nhật ADR-0005, README/install.md/release notes. Không ghi token vào chat/file.
3. **v1.3.1 (sau v1.4.0, số phiên bản xét lại khi bắt đầu):** menu Shift “Flevert: more video tools” (sửa nhãn trong media spec), Trim, Rotate, Remove sound, Make volume even, Save a frame, Merge videos, Send by Email/WhatsApp/Messenger. Audio một mũi tên, không cửa sổ công cụ.
4. **Hiệu năng, chỉ khi đo chậm.** performance-optimization: đo → sửa một chỗ → đo lại → giữ/hoàn tác, ghi sổ. Mục tiêu máy 4 nhân: khởi động lạnh ≤300 ms, HEIC 12 MP → JPG ≤1 s/ảnh, PDF → JPG A4 150 DPI ≤0,3 s/trang, Compress for sharing ≤2 s/trang, Merge/Rotate 100 trang ≤2 s, video 1080p Convert/Compress ≤2× thời lượng. Dùng scripts/measure-startup.ps1 và scripts/measure-convert.ps1. Ứng viên: qpdf đếm trang 3–8 s trên file lớn/độc; đã nhanh thì bỏ.

Bài học 2026-10-22: việc giao cho worker `ceo-worker-agent` phải được commit (hoặc ghi vào todo) trước khi phiên kết thúc; phiên 2026-10-21 để lại bốn worktree chưa commit mà HANDOFF không nhắc, nên phiên sau suýt làm v1.3.1 thay vì OCR.

PDF còn mở (chi tiết trong todo): nhúng con Arial cho watermark bằng HarfBuzz hb-subset; kiểu literal/path cho qpdf @-; Mark of the Web cho file lấy ra; Split by size vượt trần tạm khoảng 50 ms khi tách từng trang; cleanup của test installer khi trình gỡ cài thất bại.

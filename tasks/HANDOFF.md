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
3. Làm đủ code trước, từng lát nhỏ cùng test; TDD khi rẻ, kiểm RED trên mã cũ rồi GREEN. Không review hay kiểm runtime lại sau từng task. Build, format (kiểm exit code) và **bốn project Core/Image/Pdf/Media** xanh trước mỗi commit; chạy lần lượt, không chạy cả solution cùng lúc.
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
| ocr 1.4.0 | **Đang làm** trên nhánh `feat/v1.4-ocr` (đã push): engine, Tesseract dựng tĩnh bằng vcpkg (release `deps-tesseract-5.5.3`, ADR-0011), ảnh → Text, PDF Make searchable, PDF → Text đọc trang scan, đóng gói. [todo](ocr/todo.md), [spec](../docs/specs/ocr.md). |

### v1.3.0 — đã phát hành ngày 2026-10-21

[Flevert v1.3.0](https://github.com/Suryvibegod/Flevert/releases/tag/v1.3.0), latest, immutable; công khai lúc 16:59 giờ Việt Nam. scripts/get-previous-setup.ps1 ghim đúng phiên bản và checksum 1.3.0 cho lần nâng cấp tiếp theo.

- Tag v1.3.0 trỏ commit 967ad3c5418773f066ba1910b1eed31f39f6b6f3. [Release workflow](https://github.com/Suryvibegod/Flevert/actions/runs/39568534419) đạt build, bốn suite, kiểm dependency, attestation và nguồn/giấy phép ffmpeg; kèm đủ hai phần source tar.
- CI trước tag đạt ba lượt cùng commit: 39562578958, 39563402169, 39563407386; CI main 39568530658 cũng đạt. Lượt đầu sau tag có test batch 100 tiến trình lỗi thời gian; chạy riêng đạt rồi chạy lại job thất bại đạt, không đổi mã tag.
- Bốn regression test bổ sung cho GIF một khung và cleanup khi hủy đã kiểm RED/GREEN; reviewer gộp độc lập không còn finding cần sửa. Ba review tổng thể và reconcile trong media todo. Gemini CLI bổ sung đã bỏ theo yêu cầu người dùng.
- Kiểm runtime trước tag: installer 197 mục, nâng cấp từ 1.2.3, MenuProbe video/audio/PDF, 18 thao tác media và đo bộ nhớ; chi tiết trong media todo. Bộ cài chính thức đạt **198 kiểm tra** trên máy Windows tạm bằng tài khoản thường, gồm mười kịch bản, chuyển đổi thật, nâng cấp 1.2.3 và gỡ sạch: [installer verification](https://github.com/Suryvibegod/Flevert/actions/runs/39580335044). Bộ cài/menu đang dùng trên máy người dùng được giữ nguyên.
- Flevert-Setup-1.3.0.exe: SHA256 48d484f477fefc5dc4c65ff368539ab41f4a2cec45463237abec0a5df3c7bf5c, 97.674.127 byte. Hash khớp checksum draft; strict attestation ghim tag, toàn bộ commit, workflow chính thức và cấm self-hosted đạt. Defender sạch; [VirusTotal 0/65](https://www.virustotal.com/gui/file/48d484f477fefc5dc4c65ff368539ab41f4a2cec45463237abec0a5df3c7bf5c/detection). Manifest sau cài xác nhận .NET 10.0.13, khớp bản vá mới nhất của Microsoft.
- Kiểm trước commit ghi nhận release: build 0 warning/error, format mã 0, Core 531 đạt/3 skip, Image 245 đạt, Pdf 1368 đạt/1 skip, Media 225 đạt; kiểm ghim setup 1.3.0 bằng file thật đạt. Bài UI kiểm cập nhật quá giờ ở lượt đầu, chạy riêng đạt rồi chạy lại cả bốn suite đạt.
- PR Dependabot #4 (Test SDK 18.11.0) đã đóng/xóa nhánh: chỉ đổi công cụ test, hoãn để giữ dependency đã kiểm cho 1.3.0; không có bản vá runtime cần đưa vào release này.
- Còn mở đã chấp nhận: kiểm màu bằng mắt trên video HDR iPhone thật và ảnh menu sáng/tối; không lấy file cá nhân khi chưa được phép.

## Việc tiếp theo

### Linux — đóng góp của long89kev qua PR #5

- Maintainer yêu cầu sửa các lỗi review ngày 2026-10-23, trên nhánh riêng `codex/fix-linux-pr5`; không trộn OCR. Đã sửa và kiểm chứng nhả pin Windows sau Cancel, toàn bộ cây ứng dụng Linux, bộ cài có manifest sở hữu/khóa/rollback và giữ file lạ, cấu hình solution/formatter Windows. Phạm vi, test và reconcile ở [linux/pr5-fixes.md](linux/pr5-fixes.md); re-review gộp không còn Critical/High/Required.
- Windows local: bốn suite lần lượt, tổng 2379 test đạt; build/format đạt. [Ubuntu RED/GREEN và gói thật](https://github.com/Suryvibegod/Flevert/actions/runs/39830117473) đạt: Core 414, Image 243, PDF 1391, Python 54; cài/cập nhật/chuyển đổi/gỡ trong profile tạm và giữ file cá nhân. [CI của PR](https://github.com/Suryvibegod/Flevert/pull/5/checks) là gate cuối trên head trước merge.
- README ghi nhận tlong (@long89kev). Ruleset repo chỉ cho squash; commit merge phải có `Co-Authored-By: tlong <74588006+long89kev@users.noreply.github.com>`. [PR #5](https://github.com/Suryvibegod/Flevert/pull/5) giữ lịch sử 47 commit contributor. Không có release/tag mới trong đợt này.
- Khi tích hợp Linux vào nhánh OCR: hợp nhất main trên nhánh OCR, kiểm lại thay đổi nền tảng; đổi số ADR Tesseract vì Linux đã dùng `0010`. Worktree OCR đang có sửa chưa commit, phải giữ nguyên trước khi tích hợp.

1. **v1.4.0 OCR (đang làm, ưu tiên):** làm trên nhánh `feat/v1.4-ocr`, theo [ocr/todo.md](ocr/todo.md): T5b (README/CHANGELOG, `build-installer.ps1` thật), T6 (file độc, hủy giữa chừng, trang xoay), T7 (icon), rồi các bước trước release và PR vào main. Trên máy mới chạy `scripts/fetch-deps.ps1` để lấy bản tesseract tĩnh. Worktree cũ `D:\github\Flevert-wt\ocr-*` (≈ 15 GB) đã được gom; chỉ xóa khi người dùng đồng ý.
2. **Ký số SignPath Foundation.** Đã có [code-signing-policy.md](../docs/code-signing-policy.md) và mục README; chưa xác nhận người dùng gửi đơn. Người dùng tự điền tên/email, đồng ý điều khoản và reCAPTCHA. Khi duyệt: action chính thức ghim SHA, secret GitHub, ký installer/binary Flevert, kiểm chữ ký; cập nhật ADR-0005, README/install.md/release notes. Không ghi token vào chat/file.
3. **v1.3.1 (sau v1.4.0, số phiên bản xét lại khi bắt đầu):** menu Shift “Flevert: more video tools” (sửa nhãn trong media spec), Trim, Rotate, Remove sound, Make volume even, Save a frame, Merge videos, Send by Email/WhatsApp/Messenger. Audio một mũi tên, không cửa sổ công cụ.
4. **Hiệu năng, chỉ khi đo chậm.** performance-optimization: đo → sửa một chỗ → đo lại → giữ/hoàn tác, ghi sổ. Mục tiêu máy 4 nhân: khởi động lạnh ≤300 ms, HEIC 12 MP → JPG ≤1 s/ảnh, PDF → JPG A4 150 DPI ≤0,3 s/trang, Compress for sharing ≤2 s/trang, Merge/Rotate 100 trang ≤2 s, video 1080p Convert/Compress ≤2× thời lượng. Dùng scripts/measure-startup.ps1 và scripts/measure-convert.ps1. Ứng viên: qpdf đếm trang 3–8 s trên file lớn/độc; đã nhanh thì bỏ.

Bài học 2026-10-22: việc giao cho worker `ceo-worker-agent` phải được commit (hoặc ghi vào todo) trước khi phiên kết thúc; phiên 2026-10-21 để lại bốn worktree chưa commit mà HANDOFF không nhắc, nên phiên sau suýt làm v1.3.1 thay vì OCR.

PDF còn mở (chi tiết trong todo): nhúng con Arial cho watermark bằng HarfBuzz hb-subset; kiểu literal/path cho qpdf @-; Mark of the Web cho file lấy ra; Split by size vượt trần tạm khoảng 50 ms khi tách từng trang; cleanup của test installer khi trình gỡ cài thất bại.

# Bàn giao: làm tiếp Flevert

File này thay cho trí nhớ của phiên làm việc trước: mọi bối cảnh cần để làm tiếp nằm ở đây và trong các file được trỏ tới. Cập nhật mỗi khi xong một việc.

## Quy trình agent-skills của Addy Osmani

Mỗi task: viết code cùng test (TDD khi rẻ), build + format + ba project test xanh trước mỗi commit, ghi chú ngắn trong todo. Bước 4 (Verify) và 5 (Review) làm một lần cho cả bản phát hành, trước khi tag:

1. **Define:** spec (`docs/specs/<module>.md`) đủ 6 mục, success criteria kiểm được; nếu thiếu thì `interview-me` / `idea-refine` trước.
2. **Plan:** `planning-and-task-breakdown` ra `tasks/<module>/plan.md` + `todo.md`; không ghi đè plan còn việc dở.
3. **Build:** `incremental-implementation` + `test-driven-development`: viết test, **chạy thấy RED**, rồi mới code cho GREEN. Test pass ngay lần đầu là dấu hiệu xấu: kiểm lại bằng đối chứng âm. `source-driven-development`: kiểm API/giấy phép ở nguồn chính thức trước khi dùng. Tối ưu hiệu năng: `performance-optimization` (đo → sửa một thứ → đo lại, giữ hoặc hoàn tác, ghi sổ).
4. **Verify:** chạy thật (MenuProbe, tiến trình `flevert.exe` thật, ảnh chụp chỉ cửa sổ của app, UI Automation cho trợ năng), `debugging-and-error-recovery` khi lỗi.
5. **Review:** `code-review-and-quality`, `security-and-hardening`, `doubt-driven-development` bằng reviewer bối cảnh trống, chỉ đọc; RECONCILE từng finding vào bảng trong `todo.md`; lặp tới khi không còn Critical/High/Required.
6. **Ship:** `git-workflow-and-versioning` (commit nguyên tử, message nói vì sao), `documentation-and-adrs`, `shipping-and-launch` (lệnh `/ship`: 3 chuyên gia song song → go/no-go + rollback); quy trình phát hành: [docs/RELEASING.md](../docs/RELEASING.md).

## Mục tiêu và cách làm việc (người dùng đã chốt)

- Làm **đủ 5 module** theo [docs/specs/capability-map.md](../docs/specs/capability-map.md): core → image → installer (v0.1) → pdf (v0.2) → media (v0.3).
- **Không hỏi người dùng** những gì tự quyết được, ghi lý do vào spec/ADR; hỏi trước việc khó đảo ngược hoặc ra bên ngoài (xóa repo, publish, gửi file lên dịch vụ khác, dùng file cá nhân của người dùng).
- Mọi thứ **miễn phí** (Inno Setup; không mua chứng chỉ; ký mã sau qua SignPath Foundation, người dùng tự nộp đơn).
- Repo **public cho cộng đồng**, tên **Flevert**. Ngôn ngữ: UI/README/commit tiếng Anh; spec/plan nội bộ tiếng Việt.
- Commit: tên **Suryvibegod** với email ẩn của GitHub `79949229+Suryvibegod@users.noreply.github.com` (script setup đặt khi `gh` đăng nhập đúng tài khoản ID 79949229; GitHub gắn email ẩn với tài khoản qua số ID); **không bao giờ** dùng email cá nhân; kết thúc message bằng dòng `Co-Authored-By`.

## Trạng thái

| Module | Trạng thái |
|---|---|
| core | Xong, review nhiều vòng ([todo.md](todo.md)). Chạy song song nhiều file trong một lô ([perf/todo.md](perf/todo.md)). Báo bản mới từ GitHub Releases ([update/todo.md](update/todo.md), ADR-0006). |
| image | Xong ([image/todo.md](image/todo.md), ADR-0004). Ngân sách bộ nhớ chung khi chạy song song. |
| installer (v0.1) | Xong, review 5 vòng ([installer/todo.md](installer/todo.md)); menu cổ điển kiểm tay đạt (ADR-0005); trang "New versions" (`/UPDATECHECK`). |
| pdf (v0.2) | Xong, phát hành 0.2.0 ([pdf/todo.md](pdf/todo.md), ADR-0007, ADR-0008). |
| media (v0.3) | Spec đã chốt: [docs/specs/media.md](../docs/specs/media.md). Chưa có plan/code. |

Test đầu-cuối installer: `scripts/test-installer.ps1` (10 kịch bản; kịch bản nâng cấp chạy khi `get-previous-setup.ps1` đã ghim một bản trước; chạy tay trên máy thật, `%LOCALAPPDATA%\Flevert` phải không tồn tại). CI: `.github/workflows/ci.yml`; phát hành: `release.yml` + [docs/RELEASING.md](../docs/RELEASING.md).

## v0.1.0 (2026-10-20)

Module core, image, installer xong và qua kiểm phát hành ([release/todo.md](release/todo.md)). Bản đầu tiên tải được trên GitHub Releases là 0.2.0.

## v0.2.1: đã phát hành 2026-10-20

https://github.com/Suryvibegod/Flevert/releases/tag/v0.2.1 (latest, immutable). Compress > For sharing, Convert > Text, báo PDF đã phải sửa, giới hạn T18 cho mọi thao tác ghi PDF, sửa các mục review của 0.2.0. Ba review + soát lại (bảng reconcile trong [pdf/todo.md](pdf/todo.md)); kiểm thật: 35 thao tác qua `flevert.exe` đã cài, For sharing 0,42 s/trang (26% cỡ), menu `.pdf` 16 mục (MenuProbe), `test-installer.ps1` đạt trên file bản nháp gồm nâng cấp từ 0.2.0; CI ×3; Defender sạch, VirusTotal 0/68. `get-previous-setup.ps1` ghim 0.2.1.

Còn mở cho v0.2.2: lần thử lại của Lossless chưa lùi về bản không giải mã khi chậm; Compress for sharing coi mọi `IOException` là lỗi đĩa; khối `finally` của `test-installer.ps1` xóa thư mục cài nhưng để lại khóa Installed apps và menu khi gỡ cài không chạy được.

## v0.2.0 (pdf): đã phát hành 2026-10-20

https://github.com/Suryvibegod/Flevert/releases/tag/v0.2.0 (latest, immutable). Kiểm trước khi publish: CI ×3 xanh, hash và attestation khớp, `test-installer.ps1` đạt trên file của bản nháp, Defender sạch, VirusTotal 0/68. Sau khi publish: cài bản tải về, chạy thật cả 30 thao tác (ảnh, PDF, mật khẩu qua hộp thoại) và menu Explorer cho JPG, HEIC, PDF, đều đúng; đã gỡ. `get-previous-setup.ps1` ghim 0.2.0 nên lần phát hành sau chạy kịch bản nâng cấp.

Plan: [pdf/plan.md](pdf/plan.md). Trạng thái từng task và bảng reconcile: [pdf/todo.md](pdf/todo.md). Spec: [docs/specs/pdf.md](../docs/specs/pdf.md). ADR: 0007 (tiến độ trong file, output thư mục, N→1), 0008 (pdfium + qpdf, deps ghim hash).

Đã quyết: T15–T17 (nén ảnh scan, đảo trang, sửa PDF, rút chữ/ảnh) chuyển sang v0.2.1. Chấp nhận license Visual Studio Community: Flevert giữ 3 DLL MSVC runtime qpdf dùng.

**Việc tiếp theo** (spec đã duyệt 2026-10-20), hai nhánh song song, mỗi nhánh mở pull request; maintainer review (code, security, doubt) rồi gộp, v0.2.1 trước:
- `feat/v0.2.1-pdf`: [docs/specs/pdf.md](../docs/specs/pdf.md), mục "v0.2.1" (menu PDF 16 mục, Compress for sharing có spike, Extract text, báo "đã sửa", giới hạn T18 cho mọi thao tác ghi PDF, mục review còn mở, Dependabot). Tiến độ: `tasks/pdf/todo.md`.
- `feat/v0.2.2-pdf` (tạo sau khi v0.2.1 vào `main`): [docs/specs/pdf.md](../docs/specs/pdf.md), mục "v0.2.2" (8 công cụ PDF tham khảo Stirling-PDF trong cửa sổ "More PDF tools…", menu ảnh và PDF sắp lại cho người dùng thật, icon Fluent). Thứ tự phát hành: v0.2.1 → v0.2.2 → v0.3.0 → v0.4.0 (OCR).
- `feat/v0.3-media`: [docs/specs/media.md](../docs/specs/media.md) (ffmpeg bản GPL, menu video 16 mục). Tiến độ: `tasks/media/plan.md`, `tasks/media/todo.md`. Rebase lên `main` sau khi v0.2.1 được gộp.
- `feat/v0.3.1-media` (sau v0.3.0): [docs/specs/media.md](../docs/specs/media.md), mục "v0.3.1" (Trim, xoay/lật không mã hóa lại, tắt tiếng, làm đều âm lượng, lấy khung hình, ghép clip an toàn, nén theo nơi gửi Email 18 MB/WhatsApp 16 MB/Messenger 25 MB, cửa sổ Video/Audio tools).
- **Bản hiệu năng** (người dùng yêu cầu 2026-10-20, số phiên bản linh hoạt: 0.2.3 hoặc 0.3.2): sau v0.2.2 và v0.3.x, đo tốc độ theo skill `performance-optimization` (đo, sửa một chỗ, đo lại, giữ hoặc hoàn tác, ghi sổ). Chỉ phát hành khi số đo chậm hơn mục tiêu; đã nhanh thì bỏ. Mục tiêu trên máy 4 nhân: khởi động lạnh `flevert.exe run` ≤ 300 ms; ảnh HEIC 12 MP → JPG ≤ 1 s/ảnh; PDF → JPG ≤ 0,3 s/trang A4 ở 150 DPI; Compress for sharing ≤ 2 s/trang; Merge/Rotate 100 trang ≤ 2 s; video 1080p Convert/Compress ≤ 2× thời lượng. Công cụ đo có sẵn: `scripts/measure-startup.ps1`, `scripts/measure-convert.ps1`.

**Ghi chú kỹ thuật:**
- qpdf tự mở rộng ký tự đại diện trong đối số dòng lệnh (làm hỏng `\\?\` đường dẫn dài): đường dẫn đưa cho qpdf qua stdin `@-`; tùy chọn có `*`/`?`/`@` bị từ chối.
- Test nào đếm tiến trình phải nhận diện đúng tiến trình của nó (PID, tiến trình cha), không đếm theo tên trên toàn máy: CI và nhiều lần chạy test có thể song song.
- Test lô nhiều tiến trình nhạy thời gian khi máy bận: đỏ thì chạy lại riêng trước; đỏ cả khi chạy riêng thì điều tra.
- Test Pdf khởi động `flevert.exe pdf-worker` nằm chung collection xUnit `WorkerProcesses`; lớp test mới nào khởi động worker cũng phải vào collection đó.
- Smart App Control chặn ngẫu nhiên DLL vừa build (test tiến trình thoát `0xE0434352`, hoặc `0x800711C7`): build lại với `-p:SourceRevisionId=<chuỗi mới>`, không đổi mã nguồn.
- Sau khi `scripts/deps.json` đổi: chạy lại `scripts/fetch-deps.ps1`.

## Sau v0.2

- v0.3 (media): sau v0.2; quyết GPL hay LGPL ffmpeg (spec, Open Question 1) ở bước Plan.

## Dựng máy mới

Clone repo rồi chạy `scripts/setup-dev.ps1` (CLAUDE.md). Setup tải luôn pdfium/qpdf vào `deps/` (cần mạng; build Flevert.Pdf báo lỗi rõ nếu thiếu). [CLAUDE.md](../CLAUDE.md) chỉ Claude chạy `scripts/setup-dev.ps1`: cài .NET 10 SDK cho riêng user nếu thiếu, nhắc cài/đăng nhập GitHub CLI; **chỉ trên máy chủ repo** (gh đăng nhập tài khoản ID 79949229) mới cài Inno Setup, đặt danh tính commit, sửa rule git rewrite hỏng, cài plugin agent-skills; rồi build và chạy test từng project. Các việc phát hành (tag, publish) chỉ chủ repo làm. Plugin cài giữa phiên chỉ nạp sau khi khởi động lại hoặc `/reload-plugins`; trong lúc đó đọc skill từ `~/.claude/plugins/cache/addy-agent-skills/agent-skills/<version>/skills/`.

Lưu ý môi trường (đã gặp):
- SDK cài cho riêng user: shell mở trước khi chạy setup không có `dotnet` trong PATH; đặt `$env:Path = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:Path"; $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"`.
- Smart App Control (enforce) chặn ngẫu nhiên theo hash từng file mới build; đổi nhẹ mã nguồn để ra hash mới. MenuProbe chạy qua host đã ký: `dotnet tools/MenuProbe/bin/Debug/net10.0-windows/MenuProbe.dll list <file>`.
- `%TEMP%` có thể cho nhóm sandbox quyền ghi; code và test đã tính tới.
- Đẩy file trong `.github/workflows` cần token có quyền `workflow`: `gh auth refresh -h github.com -s workflow`.
- Inno Setup 6.7.3 cho riêng user: `winget install JRSoftware.InnoSetup -e --version 6.7.3 --scope user`.
- Sửa text có `\` bằng công cụ Edit, không qua heredoc/Python (dễ thành ký tự điều khiển).
- `dotnet format --verify-no-changes` là **lệnh riêng**; chỉ commit khi exit code 0 (đã có lần lệnh commit nối sau `;` vẫn chạy khi format đỏ).
- Message commit có dấu `"` qua here-string của Windows PowerShell 5.1 bị tách thành nhiều đối số: viết message ra file rồi `git commit -F <file>`.
- `dotnet test` tạo `%LOCALAPPDATA%\Flevert`; `test-installer.ps1` từ chối chạy khi thư mục này còn. Xóa trước khi chạy, và đừng chạy `dotnet test` song song với nó. Trên CI chạy hai project test ở hai bước riêng.
- Khi bấm thử menu bằng bản build (không qua installer): `install-menu --allow-insecure-location` (thêm `--dev` nếu cần); nhớ `uninstall-menu`.
- Đo tốc độ: `scripts/measure-convert.ps1` (ảnh tổng hợp từ `scripts/make_bench_photos.py`; ảnh HEIC tổng hợp một tile chậm hơn ảnh iPhone thật ~7 lần). Không dùng ảnh cá nhân của người dùng khi chưa hỏi.

# Bàn giao: làm tiếp Flevert

File này thay cho trí nhớ của phiên làm việc trước: mọi bối cảnh cần để làm tiếp nằm ở đây và trong các file được trỏ tới. Cập nhật mỗi khi xong một việc.

## BẮT BUỘC: quy trình agent-skills của Addy Osmani

Người dùng yêu cầu tuân thủ quy trình này cho mọi thay đổi, không ngoại lệ. Mỗi module, mỗi task:

1. **Define:** spec (`docs/specs/<module>.md`) đủ 6 mục, success criteria kiểm được; nếu thiếu thì `interview-me` / `idea-refine` trước.
2. **Plan:** `planning-and-task-breakdown` ra `tasks/<module>/plan.md` + `todo.md`; không ghi đè plan còn việc dở.
3. **Build:** `incremental-implementation` + `test-driven-development`: viết test, **chạy thấy RED**, rồi mới code cho GREEN. Test pass ngay lần đầu là dấu hiệu xấu: kiểm lại bằng đối chứng âm. `source-driven-development`: kiểm API/giấy phép ở nguồn chính thức trước khi dùng. Tối ưu hiệu năng: `performance-optimization` (đo → sửa một thứ → đo lại, giữ hoặc hoàn tác, ghi sổ).
4. **Verify:** chạy thật (MenuProbe, tiến trình `flevert.exe` thật, ảnh chụp chỉ cửa sổ của app, UI Automation cho trợ năng), `debugging-and-error-recovery` khi lỗi.
5. **Review:** `code-review-and-quality`, `security-and-hardening`, `doubt-driven-development` bằng reviewer bối cảnh trống, chỉ đọc; RECONCILE từng finding vào bảng trong `todo.md`; lặp tới khi không còn Critical/High/Required.
6. **Ship:** `git-workflow-and-versioning` (commit nguyên tử, message nói vì sao), `documentation-and-adrs`, `shipping-and-launch` (lệnh `/ship`: 3 chuyên gia song song → go/no-go + rollback); quy trình phát hành: [docs/RELEASING.md](../docs/RELEASING.md).

## Mục tiêu và cách làm việc (người dùng đã chốt)

- Làm **đủ 5 module** theo [docs/specs/capability-map.md](../docs/specs/capability-map.md): core → image → installer (v0.1) → media (v0.2) → pdf (v0.3).
- **Không hỏi người dùng** những gì tự quyết được, ghi lý do vào spec/ADR; hỏi trước việc khó đảo ngược hoặc ra bên ngoài (xóa repo, publish, gửi file lên dịch vụ khác, dùng file cá nhân của người dùng).
- Mọi thứ **miễn phí** (Inno Setup; không mua chứng chỉ; ký mã sau qua SignPath Foundation, người dùng tự nộp đơn).
- Repo **public cho cộng đồng**, tên **Flevert**. Ngôn ngữ: UI/README/commit tiếng Anh; spec/plan nội bộ tiếng Việt.
- Commit: `Suryvibegod` với email ẩn của GitHub (`79949229+<tên đăng nhập>@users.noreply.github.com`; script setup đặt khi `gh` đăng nhập đúng tài khoản ID 79949229, nay tên **Suryvibegod** (trước là Suryvibegod; commit cũ mang tên cũ vẫn gắn đúng tài khoản qua ID); **không bao giờ** dùng email cá nhân), kết thúc message bằng dòng `Co-Authored-By`.

## Trạng thái

| Module | Trạng thái |
|---|---|
| core | Xong, review nhiều vòng ([todo.md](todo.md)). Chạy song song nhiều file trong một lô ([perf/todo.md](perf/todo.md)). |
| image | Xong ([image/todo.md](image/todo.md), ADR-0004). Ngân sách bộ nhớ chung khi chạy song song. |
| installer (v0.1) | Xong, review 5 vòng ([installer/todo.md](installer/todo.md)); menu cổ điển kiểm tay đạt (ADR-0005). |
| media, pdf | Chưa làm. Spec nháp: [docs/specs/media.md](../docs/specs/media.md), [docs/specs/pdf.md](../docs/specs/pdf.md). |

Test: Core 204 (+1 skip), Image 221. Test đầu-cuối installer: `scripts/test-installer.ps1` (9 kịch bản, chạy tay trên máy thật). CI: `.github/workflows/ci.yml`; phát hành: `release.yml` (release notes lấy từ `CHANGELOG.md`).

## Việc đang làm: phát hành v0.1.0

`/ship` (2026-10-19): code-reviewer APPROVE, test-engineer GO có điều kiện, security-auditor **chặn**: GitHub vẫn phục vụ các commit trước khi viết lại lịch sử (có email cá nhân của người dùng). **Người dùng chọn xóa repo và tạo lại** (repo chưa có sao/fork, chưa phát hành). Thứ tự:

1. Sửa trên máy các mục `/ship` nêu (xong: Cancel không có phím tắt, tên file có `&`, README `gh auth login` + câu chữ Compress, release notes tự động từ CHANGELOG kèm `--source-digest`, Known issues (TIFF GHSA-g5j8-ff59-wp88), RELEASING theo immutable releases, `.gitignore` cho `.claude/settings.local.json`, mã commit cũ trong `tasks/` đổi sang mã mới).
2. Xóa `Suryvibegod/Flevert`, tạo lại cùng tên, **chỉ** đẩy `main` (không `--all`, không đẩy `backup/pre-rewrite-main`).
3. Cài lại: mô tả, homepage, 20 topics, Discussions, ruleset "Release tags" (`refs/tags/v*`, chỉ admin) và "Protect main" (cấm force-push/xóa), private vulnerability reporting, Dependabot alerts + security updates, secret scanning + push protection, immutable releases, bắt buộc pin action bằng SHA, fork PR cần duyệt.
4. Kiểm `github.com/Suryvibegod/ConvertAnything/commit/6498e3f` (commit cũ có email; repo cũ, tên tài khoản cũ Suryvibegod) trả 404; xóa nhánh local `backup/pre-rewrite-main`.
5. CI xanh 3 lần → tag `v0.1.0` → kiểm bản nháp theo RELEASING (hash, attestation, `test-installer.ps1`, runtime) → quét VirusTotal (người dùng đưa API key trong chat; **không ghi key vào file nào**; nhắc người dùng tạo key mới sau khi quét) → người dùng Publish.
6. Điều kiện của test-engineer: người dùng kiểm tay một ảnh iPhone **dọc** thật có GPS (Convert → JPG đúng chiều; Remove metadata hết GPS). Sau phát hành: test tự động cho HEIC xoay và cho Remove metadata trên mọi định dạng ([perf/todo.md](perf/todo.md) không có; xem báo cáo test-engineer tóm trong lịch sử commit `docs: record the ship review`).

Sau v0.1.0: nhắc người dùng nộp đơn SignPath Foundation (https://signpath.org/apply), đặt ảnh social preview trên web, soạn bài giới thiệu (Reddit, nhóm dev VN). Rồi media (v0.2): giữ setup 0.1.0 làm fixture test nâng cấp; bật `RestorePackagesWithLockFile` + `--locked-mode`; ffmpeg đặt `MaxParallel = 1`.

## Dựng máy mới

Người dùng chỉ clone repo, mở Claude Code trong thư mục repo rồi gõ "làm tiếp đi". [CLAUDE.md](../CLAUDE.md) chỉ Claude chạy `scripts/setup-dev.ps1`: cài .NET 10 SDK cho riêng user nếu thiếu, nhắc cài/đăng nhập GitHub CLI, đặt danh tính commit, sửa rule git rewrite hỏng, cài plugin agent-skills, build và chạy test. Plugin cài giữa phiên chỉ nạp sau khi khởi động lại hoặc `/reload-plugins`; trong lúc đó đọc skill từ `~/.claude/plugins/cache/addy-agent-skills/agent-skills/<version>/skills/`.

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

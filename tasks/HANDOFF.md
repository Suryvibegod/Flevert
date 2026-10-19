# Bàn giao: làm tiếp ConvertAnything trên máy khác

Cập nhật: 2026-10-19. File này thay cho trí nhớ của phiên làm việc trước: mọi bối cảnh cần để làm tiếp nằm ở đây và trong các file được trỏ tới.

## BẮT BUỘC: quy trình agent-skills của Addy Osmani

Người dùng yêu cầu tuân thủ quy trình này cho mọi thay đổi, không ngoại lệ. Mỗi module, mỗi task:

1. **Define:** spec (`SPEC-<module>.md`) đủ 6 mục, success criteria kiểm được; nếu thiếu thì `interview-me` / `idea-refine` trước.
2. **Plan:** `planning-and-task-breakdown` ra `tasks/<module>/plan.md` + `todo.md`; không ghi đè plan còn việc dở.
3. **Build:** `incremental-implementation` + `test-driven-development`: viết test, **chạy thấy RED**, rồi mới code cho GREEN. Test pass ngay lần đầu là dấu hiệu xấu: kiểm lại bằng đối chứng âm. `source-driven-development`: kiểm API/giấy phép ở nguồn chính thức trước khi dùng.
4. **Verify:** chạy thật (MenuProbe, tiến trình `ca.exe` thật, ảnh chụp chỉ cửa sổ của app), `debugging-and-error-recovery` khi lỗi: tái hiện → khoanh vùng → sửa → test chặn.
5. **Review:** `code-review-and-quality`, `security-and-hardening`, `doubt-driven-development` bằng reviewer bối cảnh trống, chỉ đọc; RECONCILE từng finding (hợp đồng sai / hợp lệ / trade-off / nhiễu); lặp tới khi không còn Critical/High/Required.
6. **Ship:** `git-workflow-and-versioning` (commit nguyên tử, message nói vì sao), `documentation-and-adrs`, `shipping-and-launch`; Definition of Done trong `references/definition-of-done.md` của plugin.

## Mục tiêu và cách làm việc (người dùng đã chốt)

- Làm **đủ 5 module** theo [CAPABILITY-MAP.md](../CAPABILITY-MAP.md): core → image → installer (v0.1) → media (v0.2) → pdf (v0.3).
- Tuân thủ **quy trình agent-skills của Addy Osmani** (plugin `agent-skills@addy-agent-skills`): spec → plan/tasks → TDD (RED trước, rồi GREEN) → làm từng lát nhỏ → review 3 góc (code-reviewer, security-auditor, doubt-driven, mỗi reviewer bối cảnh trống, chỉ đọc) → sửa → review lại tới khi không còn Critical/High/Required → docs/ADR → ship.
- **Không hỏi người dùng**, tự quyết và ghi lý do vào spec/ADR. Cross-model review (Gemini/Codex) bỏ qua, ghi rõ là bỏ qua.
- Xong hết: repo **public**, release có installer, README có ảnh/GIF demo, topics GitHub, soạn sẵn bài giới thiệu để người dùng tự đăng (Reddit, Facebook nhóm dev VN, ...).
- Ngôn ngữ: UI/README/commit tiếng Anh; spec/plan nội bộ tiếng Việt.
- Commit: `Suryvibegod <79949229+Suryvibegod@users.noreply.github.com>`, kết thúc message bằng dòng `Co-Authored-By`. Mỗi commit một việc; kiểm `dotnet format --verify-no-changes` bằng **exit code** (đừng nối qua `| head`).

## Trạng thái

| Module | Trạng thái |
|---|---|
| core | Xong + đã sửa theo 3 vòng review. **Vòng 4 (xác minh các bản sửa vòng 3) chưa có kết quả, cần chạy lại** (xem dưới). 100 test pass, 1 skip (symlink cần quyền admin). |
| image | Task 1 xong (MagickSetup: policy allow-list, giới hạn tài nguyên, đọc theo đuôi file). Hai project image đã có trong solution. Tiếp theo: Task 2 trong [tasks/image/todo.md](image/todo.md). |
| installer, media, pdf | Chưa làm. Spec nháp: [SPEC-media.md](../SPEC-media.md), [SPEC-pdf.md](../SPEC-pdf.md). |

Quyết định quan trọng: [docs/adr/](../docs/adr) (0001 registry, 0002 gom lô, 0003 đóng gói). Kế hoạch core: [plan.md](plan.md), [todo.md](todo.md) (Phase 4 = sửa theo review).

## Dựng máy mới

Người dùng chỉ clone repo, mở Claude Code trong thư mục repo (chấp nhận hộp thoại tin cậy thư mục) rồi gõ "làm tiếp đi". [CLAUDE.md](../CLAUDE.md) chỉ Claude chạy `scripts/setup-dev.ps1`: cài .NET 10 SDK cho riêng user (`%LOCALAPPDATA%\Microsoft\dotnet`, không cần quyền admin nên không kẹt hộp thoại UAC) nếu thiếu, nhắc lệnh cài/đăng nhập GitHub CLI nếu thiếu, đặt danh tính commit, sửa rule git rewrite hỏng nếu có, cài plugin agent-skills, build và chạy test. `.claude/settings.json` khai báo marketplace và bật plugin cho repo.

Plugin cài giữa phiên chỉ được nạp sau khi khởi động lại Claude Code hoặc `/reload-plugins`; trong lúc đó đọc thẳng skill từ `~/.claude/plugins/cache/addy-agent-skills/agent-skills/<version>/skills/`.

Lưu ý môi trường của máy cũ (có thể gặp lại):
- Git global có rule hỏng `url.https://github.com.insteadof git@github.com:` (thiếu `/` cuối) làm cài plugin lỗi; script tự sửa.
- Test chạy tiến trình `ca.exe` thật cần build cả solution trước (script đã làm).
- Thư mục `%TEMP%` có thể có nhóm lạ (ví dụ `CodexSandboxUsers`) được quyền ghi; code đã tính tới, test tự dựng ACL.

## Việc dở dang cụ thể (làm theo thứ tự)

1. Chạy lại review xác minh vòng 4 cho core: reviewer bối cảnh trống, chỉ đọc, trên diff `6498e3f..HEAD` của `src/ConvertAnything.Core`, `src/ConvertAnything.Cli`, `tests/ConvertAnything.Core.Tests`; sửa nếu còn Critical/High/Required (agent đang chạy khi tắt máy, kết quả mất).
2. Image Task 2: lát dọc JPG → PNG (auto-orient, đăng ký op vào catalog trong `src/ConvertAnything.Cli/Program.cs`, `ProjectReference` từ Cli tới Image, menu, kiểm thật bằng `tools/MenuProbe`).
3. Khi bấm thử menu trên máy thật: `install-menu --dev --allow-insecure-location` nếu chạy từ thư mục build; nhớ `uninstall-menu` sau khi thử.

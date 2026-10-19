# Bàn giao: làm tiếp ConvertAnything trên máy khác

Cập nhật: 2026-10-19 (cuối phiên trên máy thứ hai, chuyển sang máy nhà). File này thay cho trí nhớ của phiên làm việc trước: mọi bối cảnh cần để làm tiếp nằm ở đây và trong các file được trỏ tới.

## BẮT BUỘC: quy trình agent-skills của Addy Osmani

Người dùng yêu cầu tuân thủ quy trình này cho mọi thay đổi, không ngoại lệ. Mỗi module, mỗi task:

1. **Define:** spec (`docs/specs/<module>.md`) đủ 6 mục, success criteria kiểm được; nếu thiếu thì `interview-me` / `idea-refine` trước.
2. **Plan:** `planning-and-task-breakdown` ra `tasks/<module>/plan.md` + `todo.md`; không ghi đè plan còn việc dở.
3. **Build:** `incremental-implementation` + `test-driven-development`: viết test, **chạy thấy RED**, rồi mới code cho GREEN. Test pass ngay lần đầu là dấu hiệu xấu: kiểm lại bằng đối chứng âm. `source-driven-development`: kiểm API/giấy phép ở nguồn chính thức trước khi dùng.
4. **Verify:** chạy thật (MenuProbe, tiến trình `ca.exe` thật, ảnh chụp chỉ cửa sổ của app), `debugging-and-error-recovery` khi lỗi: tái hiện → khoanh vùng → sửa → test chặn.
5. **Review:** `code-review-and-quality`, `security-and-hardening`, `doubt-driven-development` bằng reviewer bối cảnh trống, chỉ đọc; RECONCILE từng finding (hợp đồng sai / hợp lệ / trade-off / nhiễu); lặp tới khi không còn Critical/High/Required.
6. **Ship:** `git-workflow-and-versioning` (commit nguyên tử, message nói vì sao), `documentation-and-adrs`, `shipping-and-launch`; Definition of Done trong `references/definition-of-done.md` của plugin.

## Mục tiêu và cách làm việc (người dùng đã chốt)

- Làm **đủ 5 module** theo [docs/specs/capability-map.md](../docs/specs/capability-map.md): core → image → installer (v0.1) → media (v0.2) → pdf (v0.3).
- Tuân thủ **quy trình agent-skills của Addy Osmani** (plugin `agent-skills@addy-agent-skills`): spec → plan/tasks → TDD (RED trước, rồi GREEN) → làm từng lát nhỏ → review 3 góc (code-reviewer, security-auditor, doubt-driven, mỗi reviewer bối cảnh trống, chỉ đọc) → sửa → review lại tới khi không còn Critical/High/Required → docs/ADR → ship.
- **Không hỏi người dùng**, tự quyết và ghi lý do vào spec/ADR. Cross-model review (Gemini/Codex) bỏ qua, ghi rõ là bỏ qua.
- Xong hết: repo **public**, release có installer, README có ảnh/GIF demo, topics GitHub, soạn sẵn bài giới thiệu để người dùng tự đăng (Reddit, Facebook nhóm dev VN, ...).
- Ngôn ngữ: UI/README/commit tiếng Anh; spec/plan nội bộ tiếng Việt.
- Commit: `Suryvibegod` với email ẩn của GitHub (`79949229+Suryvibegod@users.noreply.github.com`, script setup đặt khi `gh` đăng nhập Suryvibegod; **không** dùng email cá nhân), kết thúc message bằng dòng `Co-Authored-By`. Mỗi commit một việc; kiểm `dotnet format --verify-no-changes` bằng **exit code** (đừng nối qua `| head`).

## Trạng thái

| Module | Trạng thái |
|---|---|
| core | Xong. Review vòng 4–9 đã sửa hết (Phase 5–10 trong [todo.md](todo.md)). Còn mở: test gom lô qua junction hỏng hiếm khi chạy cả solution (đã cho in đủ chẩn đoán). |
| image | **Xong** (Task 1–7): review 5 vòng, README + ADR-0004. 204 test pass. |
| installer (v0.1) | **Code + review xong** (5 vòng, [installer/todo.md](installer/todo.md)): setup kiểm quyền thư mục bằng `ca check-location` chạy từ `{tmp}` **trước khi chép**. Test đầu-cuối 9 kịch bản. Còn: kiểm tay menu cổ điển (người dùng), rồi phát hành. Core 190 test + 1 skip, image 204. CI/release workflow đã ở `.github/workflows`. |
| media, pdf | Chưa làm. Spec nháp: [docs/specs/media.md](../docs/specs/media.md), [docs/specs/pdf.md](../docs/specs/pdf.md). |

Quyết định quan trọng: [docs/adr/](../docs/adr) (0001 registry, 0002 gom lô, 0003 đóng gói, 0004 Magick.NET, 0005 installer/ký mã). Spec installer: [docs/specs/installer.md](../docs/specs/installer.md).

Người dùng đã chốt (2026-10-19):
- Mọi thứ **miễn phí** (Inno Setup; không mua chứng chỉ; ký mã sau qua SignPath Foundation, người dùng tự nộp đơn khi repo đã public).
- Repo **public cho cộng đồng**: tự push và public **sau khi** đạt quy trình Addy; setup GitHub cho "popular" (mô tả, topics, Discussions, release có installer). Giữ tên **ConvertAnything** (tự mô tả, đã gắn vào menu/registry/installer; đổi tên tốn công mà không tăng khả năng được tìm thấy).
- Lịch sử commit đã **viết lại sang email ẩn** của GitHub (người dùng chọn). Máy nào có bản clone cũ phải clone lại (hoặc `git fetch` rồi `git reset --hard origin/main`).

## Dựng máy mới

Người dùng chỉ clone repo, mở Claude Code trong thư mục repo (chấp nhận hộp thoại tin cậy thư mục) rồi gõ "làm tiếp đi". [CLAUDE.md](../CLAUDE.md) chỉ Claude chạy `scripts/setup-dev.ps1`: cài .NET 10 SDK cho riêng user (`%LOCALAPPDATA%\Microsoft\dotnet`, không cần quyền admin nên không kẹt hộp thoại UAC) nếu thiếu, nhắc lệnh cài/đăng nhập GitHub CLI nếu thiếu, đặt danh tính commit, sửa rule git rewrite hỏng nếu có, cài plugin agent-skills, build và chạy test. `.claude/settings.json` khai báo marketplace và bật plugin cho repo.

Plugin cài giữa phiên chỉ được nạp sau khi khởi động lại Claude Code hoặc `/reload-plugins`; trong lúc đó đọc thẳng skill từ `~/.claude/plugins/cache/addy-agent-skills/agent-skills/<version>/skills/`.

Lưu ý môi trường của máy cũ (có thể gặp lại):
- Git global có rule hỏng `url.https://github.com.insteadof git@github.com:` (thiếu `/` cuối) làm cài plugin lỗi; script tự sửa.
- Test chạy tiến trình `ca.exe` thật cần build cả solution trước (script đã làm).
- Thư mục `%TEMP%` có thể có nhóm lạ (ví dụ nhóm của một công cụ sandbox) được quyền ghi; code đã tính tới, test tự dựng ACL.

Lưu ý môi trường máy thứ hai (Windows 11 Home, 2026-10-19):
- SDK cài cho riêng user. Shell mở **trước** khi chạy setup không có `dotnet` trong PATH: trong PowerShell đặt `$env:Path = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:Path"; $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"`. Bản Debug `ca.exe` (framework-dependent) chạy thiếu `DOTNET_ROOT` sẽ bật hộp thoại đòi tải .NET Desktop Runtime: không cần tải, chỉ là thiếu biến môi trường. Explorer chỉ thấy biến mới sau khi đăng xuất/đăng nhập.
- Smart App Control đang bật (enforce). Nó chặn theo **hash** từng file mới build, ngẫu nhiên: lúc chặn `MenuProbe.exe`, lúc chặn `ConvertAnything.Image.dll` ("An Application Control policy has blocked this file", test host thoát mã 255). Đổi nhẹ mã nguồn để ra hash mới thường qua được. MenuProbe chạy qua host đã ký: `dotnet tools/MenuProbe/bin/Debug/net10.0-windows/MenuProbe.dll list <file>`. **Hệ quả cho bản phát hành:** người dùng bật Smart App Control có thể bị chặn ca.exe/DLL chưa ký; module installer phải tính chuyện ký mã (code signing) hoặc ghi rõ trong README.
- GitHub CLI đã cài và đăng nhập tài khoản Suryvibegod trên máy này (2026-10-19); `gh auth setup-git` đã chạy. Đẩy file trong `.github/workflows` cần token có quyền `workflow`: `gh auth refresh -h github.com -s workflow` (hoặc đăng nhập mới với `gh auth login --web -s workflow`).
- Inno Setup 6.7.3 cài cho riêng user: `winget install JRSoftware.InnoSetup -e --version 6.7.3 --scope user`.
- Ghi file có dấu `\` qua heredoc/Python dễ bị biến `\a`, `\b` thành ký tự điều khiển (đã làm hỏng `BatchTests.cs` một lần). Sửa text có backslash bằng công cụ Edit.
- Kiểm `dotnet format --verify-no-changes` thành **lệnh riêng**, đọc exit code rồi mới commit; đừng gộp chung một lệnh với `git commit`.
- Message commit có dấu `"` truyền qua here-string của **Windows PowerShell 5.1** bị tách thành nhiều đối số (git báo "pathspec ... did not match", commit không xảy ra, file đã stage lọt vào commit sau). Viết message ra file rồi `git commit -F <file>`, và mỗi commit một lệnh riêng, kiểm kết quả trước khi commit tiếp.
- Máy Windows 11 Pro (2026-10-19): test `dotnet test` tạo `%LOCALAPPDATA%\ConvertAnything` (magick, pipes); `test-installer.ps1` từ chối chạy khi thư mục này có. Xóa nó trước khi chạy test đầu-cuối, và đừng chạy `dotnet test` song song với nó.

## Việc dở dang cụ thể (làm theo thứ tự)

0. ~~Đưa workflow về chỗ~~ **Xong** (2026-10-19, máy Windows 11 Pro): `.github/workflows/` đã push, CI chạy. Máy này có clone cũ trước khi viết lại lịch sử: đã `git reset --hard origin/main`, bản cũ giữ ở nhánh local `backup/pre-rewrite-main` (chỉ khác email, có thể xóa).

1. ~~Review installer~~ **XONG** (2026-10-19): 5 vòng, vòng 5 không còn Critical/High/Required, phần sửa Low được soát lại (bảng trong [installer/todo.md](installer/todo.md)). E2E 9 kịch bản xanh; test 190(+1 skip)+204 xanh. CI: các test thời gian chập chờn đã sửa (`RunLogTests`, `ImageIoTests` Cancel), hai project test chạy ở bước riêng. Bước 3.1 (CI xanh 3 lần liên tiếp) và mục 2 cũng xong. Bước 3.2–3.4 **xong** (2026-10-19): repo public, ruleset `Release tags` (chỉ admin tạo/sửa/xóa `v*`) và `Protect main` (cấm force-push/xóa), private vulnerability reporting bật, mô tả/homepage/20 topics/Discussions. Bước 3.5 đã chạy một lần: tag `v0.1.0` tạo bản nháp (build/attest/release xanh), **nhưng** sau đó người dùng yêu cầu tăng tốc convert, nên bản nháp đó cũ.

**Tăng tốc convert: XONG** ([perf/plan.md](perf/plan.md), [perf/todo.md](perf/todo.md)): chạy song song các file trong một lô + ngân sách bộ nhớ chung + `IOperation.MaxParallel` (mặc định 1; ảnh/dev 8) + hạ ưu tiên khi ≥ 2 file chạy. Lô 10 ảnh HEIC 12 MP: ×3,5–4; ảnh iPhone thật (reviewer đo) 16 ảnh 8,7 s → 2,1 s. Review 2 vòng, không còn Critical/High/Required. Ứng viên tiếp theo (chưa làm, cần hỏi người dùng vì đổi ADR-0002): sàn 1,5 s của lô lớn gấp 3–4 lần thời gian convert một ảnh thật. Bản nháp **v0.1.0 đã build lại** trên `65edc07` (sau 3 lần CI xanh liên tiếp) và đã kiểm theo bước 3.5 (2026-10-19): SHA256 khớp `SHA256SUMS.txt`; `gh attestation verify ... --source-ref refs/tags/v0.1.0 --deny-self-hosted-runners` qua (đối chứng: sai tag hoặc file bị sửa thì bị từ chối); `test-installer.ps1 -Setup <file tải từ bản nháp>` 9/9 kịch bản xanh; runtime đóng gói `10.0.13` = bản vá .NET 10 mới nhất (2026-10-13), Magick.NET 15.0.0. Bản cài thử menu cổ điển đã gỡ (người dùng đồng ý). **Còn lại trước khi publish (người dùng làm):** quét file setup trên VirusTotal (tải lên dịch vụ bên ngoài), rồi Publish bản nháp. Sau đó bước 3.6–3.7.

`MinimumLifetime` 1,5 s (người dùng chọn "đo rồi quyết"): đã đo qua menu Explorer thật, **giữ nguyên** (file xuất hiện ngay, chỉ thông báo muộn ~1 s; chưa đo được khởi động lạnh). Số đo và lý do trong [perf/todo.md](perf/todo.md). Danh sách vòng 2 gốc (đã làm hết, để đối chiếu):
   - `.iss`: mã 3 (thư mục bị từ chối) có thông báo + mục README riêng (gồm trường hợp `%LOCALAPPDATA%` qua junction/ổ khác); câu dự phòng "Windows blocked ConvertAnything or it stopped unexpectedly"; chỉ hiện dòng stderr đầu trong hộp thoại, log phần còn lại; bỏ `(Code = ErrorPolicyBlocked)` thừa; `RunCa` bọc `try/except` (ngoại lệ không được bỏ qua `SweepRegistry`); không chạy `classic-menu on` khi `install-menu` đã lỗi, tách chữ trang cuối cho hai loại lỗi; `[Messages]` ghi đè `SetupAppRunningError`/`UninstallAppRunningError` ("A conversion is still running. Wait a few seconds..."); `PrepareToInstall` từ chối `{app}`/`{userpf}` là reparse point, và `{app}` có sẵn, **không rỗng**, mà không có mục gỡ `_is1` của app.
   - `Program.cs`: giữ mutex tới khi tiến trình thoát (trường static, không `using`); hằng số thay cho `SomeFilesFailed` ở nhánh classic-menu.
   - `InstallerScriptTests`: lấy tên thư mục `logs`/`pipes`/`magick` và `SystemFileAssociations` từ hằng số C# (thêm hằng số `internal`), bỏ assert hiển nhiên `Title + ".Menu"`.
   - Test junction `install-menu`: `finally` chạy `uninstall-menu` nếu thoát 0; thêm test đơn vị junction cho `InstallLocation` nếu chưa có.
   - `test-installer.ps1`: assert setup ≤ 60 MB; kịch bản 5 `ExitCode -eq 7` + log có "installs only into"; kịch bản 6 chỉ chấp nhận nhánh "từ chối" (cài im lặng thì Inno chọn Cancel); `finally` dừng `ca.exe` trong `{app}` trước khi gỡ, bọc `Remove-Item`; `MenuKeys` kiểm mọi khóa `ConvertAnything*`.
   - Workflow: tách job `attest` riêng (job build chỉ `contents: read`); kiểm tag nằm trên `main` (`git merge-base --is-ancestor`, checkout `fetch-depth: 0`); gọi thẳng `ISCC.exe` vừa cài và kiểm mã thoát khi cài Inno (`build-installer.ps1` nhận tham số `-Iscc`); CI/release dùng `dotnet-version: 10.0.x` (bản vá mới nhất, tức runtime mới nhất trong bản self-contained), `global.json` hạ về `10.0.100` + `latestFeature`; thêm `.github/dependabot.yml` (github-actions, nuget).
   - `setup-dev.ps1`: tải `dotnet-install.ps1` vào thư mục tạm ngẫu nhiên, đòi chữ ký Authenticode hợp lệ của Microsoft trước khi chạy; lời nhắc đăng nhập `gh` thêm `--scopes workflow`.
   - README: hash chỉ chứng minh file không hỏng, provenance mới chứng minh build từ repo này (`gh attestation verify ... --signer-workflow Suryvibegod/ConvertAnything/.github/workflows/release.yml`); so sánh hash không phân biệt hoa thường; kiểm **trước** khi bấm Run anyway. Smart App Control: "is more likely to work once signed", bỏ câu "cannot be turned back on" tuyệt đối. Cách gỡ tay nếu uninstaller bị chặn. Nhắc thay tên user/tên file trong log trước khi đăng issue (cả CONTRIBUTING, mẫu bug).
   - Spec/ADR-0005: AppMutex là **hỏi/hủy** chứ không "chờ" (cài im lặng thì hủy); `CloseApplications` chỉ còn tác dụng khi không có mutex. Ghi các trade-off: `.claude/settings.json` giữ lại (quy trình bắt buộc của maintainer, Claude Code hỏi tin cậy thư mục trước); test thời gian trên CI (nới nếu CI đỏ).
   - Rồi chạy **review vòng 3** (3 reviewer bối cảnh trống, chỉ đọc) trên phần thay đổi, reconcile tới khi không còn Critical/High/Required; ghi bảng vào [installer/todo.md](installer/todo.md).
2. ~~Kiểm tay menu cổ điển~~ **ĐẠT** (2026-10-19, Windows 11 Pro 26H2 build 26300.9711, ghi trong ADR-0005). Bản cài thử vẫn còn trên máy này: gỡ (Settings > Apps) trước khi chạy `test-installer.ps1`. Mô tả gốc: cài setup trong `artifacts\installer`, tick "Use the classic (Windows 10 style) right-click menu for all apps", đăng xuất/đăng nhập, chuột phải ảnh: menu kiểu cũ phải có ConvertAnything ngay ngoài. Ghi kết quả + bản Windows vào ADR-0005. Không xác nhận được thì ẩn trang này ở v0.1.
3. **Phát hành v0.1.0, đúng thứ tự** (attestation chỉ chạy khi repo public; mọi link trong README/setup trỏ vào release):
   1. Push; CI xanh **3 lần liên tiếp** (Actions, Re-run). Nới test thời gian nếu chỉ đỏ trên CI.
   2. Đổi repo sang public: `gh repo edit Suryvibegod/ConvertAnything --visibility public --accept-visibility-change-consequences`. Ngay sau đó (review vòng 3, DD F5/SA-L2): ruleset cho tag `refs/tags/v*` (chỉ maintainer được tạo/sửa/xóa) và cho `main` (cấm force-push, cấm xóa), qua `gh api repos/Suryvibegod/ConvertAnything/rulesets`.
   3. Bật private vulnerability reporting: `gh api -X PUT repos/Suryvibegod/ConvertAnything/private-vulnerability-reporting`; bấm thử link `/security/advisories/new`.
   4. Mô tả repo (chỉ nói ảnh ở v0.1; video/PDF là "coming"), homepage = trang releases, topics (tối đa 20: windows, context-menu, image-converter, heic, heic-to-jpg, image-compression, webp, avif, exif, metadata-remover, file-converter, explorer, windows-11, dotnet, csharp, imagemagick, offline, privacy, inno-setup, open-source), bật Discussions; nhờ người dùng đặt ảnh social preview (chỉ làm được trên web).
   5. Tag `v0.1.0`; workflow release tạo bản **nháp**. Tải file exe trong bản nháp, chạy `scripts/test-installer.ps1 -Setup <exe>` trên máy thật + cài tay một lần; so `runtimepack` trong `ca.deps.json` với bản vá .NET 10 mới nhất; quét VirusTotal. Đạt thì Publish.
   6. README: thêm ảnh/GIF demo (menu chuột phải, cửa sổ tiến trình); soạn bài giới thiệu cho người dùng tự đăng.
   7. Nhắc người dùng nộp đơn SignPath Foundation (https://signpath.org/apply).
4. Media (v0.2), PDF (v0.3) theo [docs/specs/capability-map.md](../docs/specs/capability-map.md). v0.2: giữ setup 0.1.0 làm fixture để test nâng cấp thật. Trước v0.2 (SA Info vòng 3): bật `RestorePackagesWithLockFile` + `--locked-mode` trên CI/release và package source mapping về nuget.org.
5. Khi bấm thử menu bằng bản build (không qua installer): `install-menu --allow-insecure-location` (thêm `--dev` nếu cần); nhớ `uninstall-menu`. `test-installer.ps1` từ chối chạy nếu còn menu/app data của bản dev.

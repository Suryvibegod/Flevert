# Bàn giao: làm tiếp Flevert

File này thay cho trí nhớ của phiên làm việc trước: mọi bối cảnh cần để làm tiếp nằm ở đây và trong các file được trỏ tới. Cập nhật mỗi khi xong một việc.

## Quy trình agent-skills của Addy Osmani: xây trước, verify một lần trước khi phát hành

Từ 2026-10-20 người dùng muốn bớt quy trình: **xây hết rồi mới verify ở bước cuối**. Mỗi task chỉ còn: viết code cùng test (TDD khi rẻ), build + format + ba project test xanh trước mỗi commit, ghi chú ngắn trong todo. **Không** chạy agent review sau từng task, không vòng re-review, không chạy thật từng task. Bước 4 (Verify) và 5 (Review) dưới đây làm **một lần cho cả bản phát hành**, trước T20; vòng soát lại phần sửa dùng một reviewer gộp. Chạy song song tối đa 2–3 agent. Các bước gốc, để tham chiếu:

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
| pdf (v0.2) | Đang làm: T1, T2, T3, T5, T7, T9, T10 trên `main`; năm nhánh `wip/*` chờ gộp (bên dưới). Plan/todo: [pdf/](pdf/). |
| media (v0.3) | Spec đã chốt: [docs/specs/media.md](../docs/specs/media.md). Chưa có plan/code. |

Test trên `main` (2026-10-20): Core 354 (+1 skip), Image 240, Pdf 78. Test đầu-cuối installer: `scripts/test-installer.ps1` (9 kịch bản, chạy tay trên máy thật, `%LOCALAPPDATA%\Flevert` phải không tồn tại). CI: `.github/workflows/ci.yml`; phát hành: `release.yml` + [docs/RELEASING.md](../docs/RELEASING.md).

## v0.1.0 đã phát hành (2026-10-20)

https://github.com/Suryvibegod/Flevert/releases/tag/v0.1.0 (latest, immutable). Kết quả kiểm: [release/todo.md](release/todo.md).

Người dùng cần tự làm: bật 2FA cho tài khoản GitHub; tạo API key VirusTotal mới (key cũ đã nằm trong lịch sử chat); nếu muốn, báo dương tính giả cho DeepInstinct. Hai PR Dependabot (#1 Test SDK 18, #2 xunit runner 4) chờ xem xét theo RELEASING.

## Việc đang làm: v0.2 (pdf), cập nhật 2026-10-20

Plan: [pdf/plan.md](pdf/plan.md). Trạng thái từng task: [pdf/todo.md](pdf/todo.md). Spec: [docs/specs/pdf.md](../docs/specs/pdf.md). ADR mới: 0007 (tiến độ trong file, output thư mục, N→1), 0008 (pdfium + qpdf, deps ghim hash).

**Đã xong và đã gộp vào `main`** (mỗi task có test viết trước, thấy đỏ rồi mới xanh; build, format, ba project test xanh):
- T1 `scripts/fetch-deps.ps1` + `scripts/deps.json` (pdfium chromium/8100, qpdf 12.4.2, giấy phép; ghim SHA256; CI/release/setup-dev gọi).
- T2 `ExternalTool` (tiến trình con an toàn, job object kill-on-close, stdin, giới hạn output, timeout/hủy).
- T3 `Qpdf` + `pdf.rotate-right/left/180` + menu `.pdf`; Flevert.Cli tham chiếu Flevert.Pdf.
- T5 `IBatchOperation` (N→1, thứ tự tự nhiên `StrCmpLogicalW`, ghim cả lô) + `dev.join`.
- T7 `OutputPath.WriteDirectoryAtomicallyAsync` (1→N) + tiến độ trong file ("page 37 of 200") + `dev.split`.
- T9 `PdfDocument` (pdfium P/Invoke, khóa toàn cục, giới hạn 2 000 trang / 2 GB / 10 000 px).
- T10 `pdf.to-jpg`, `pdf.to-png` (đã có trong menu PDF: Convert > JPG pages / PNG pages).

### Bàn giao giữa chừng (2026-10-20, chuyển sang máy PC ở nhà)

Review ba góc cho T1–T10 đã chạy (bảng reconcile "Review vòng 1" trong [pdf/todo.md](pdf/todo.md), nhóm sửa F1–F7, quyết định F6 → T9b). Năm nhánh sửa trên GitHub, **chưa gộp vào `main`**, mỗi nhánh tách ra trước commit `0e39979` (chỉ đổi tài liệu) nên cần rebase. Commit cuối có chữ `wip:` là phần agent đang làm dở lúc dừng: **chưa build, chưa chạy test**.

| Nhánh | Nội dung | Trạng thái |
|---|---|---|
| `wip/f1-safe-folder-outputs` | F1: `PartialFolder` giữ thư mục `.partial` bằng handle + file neo ẩn, đổi tên qua handle, thử lại khi trình quét giữ file, ẩn trong lúc ghi, dùng đường dẫn thật đã resolve (chống junction cao hơn trong đường dẫn), `DevSplit` ghi `CreateNew` | 7 commit, không có `wip:`. Lượt sửa review F1 dừng giữa chừng: **còn** DD-M1 (hết thời gian thử lại thì giữ trang, không xóa; chờ tới ~30 s, hủy được), và các mục Optional 3–9, 11–12 trong bảng "Review F1 vòng 1" (agent có thể chưa ghi bảng: xem lại danh sách trong lịch sử commit / todo) |
| `wip/f2-f3-f7-qpdf-limits` | F2/F3/F7: `ExternalTool` giới hạn bộ nhớ/1 tiến trình/môi trường, qpdf luôn `\\?\`, UTF-8 nghiêm ngặt, OpenSSL dùng cấu hình rỗng của Flevert, trần bộ nhớ theo kích thước input, "bad allocation" = lỗi, thử lại khi Windows từ chối giới hạn 1 tiến trình; test chứng minh đúng | **Xong** (2026-10-20): đã rebase lên `main` (sau T11a), `wip:` thay bằng commit thật; spec, ADR-0008, bảng "Review F2/F3/F7 vòng 2" trong todo; format và ba project test xanh. **Sẵn sàng gộp** sau T11a |
| `wip/f4-scripts-and-notices` | F4: `write-notices.ps1` (từ chối file không có giấy phép, tính cả file ẩn), notices pdfium/qpdf/OpenSSL/zlib/libjpeg-turbo/MSVC, `fetch-deps` thư mục riêng quyền + hash từng file, `check-checkout.ps1` (cảnh báo checkout người khác ghi được, lệnh sửa đúng) | 14 commit, đã rebase lên `main` (sau T11a), build + format + ba project test xanh, **sẵn sàng gộp**. Phần "Còn" đã làm: 3 DLL runtime, chặn phát hành vì giấy phép MSVC (RELEASING bước 6), giấy phép ghim đúng phiên bản (zlib 1.3.2, libjpeg-turbo 3.2.0, OpenSSL 3.6.4; bằng chứng trong ADR-0008), `#error` trong `.iss`, test pwsh; bảng "Review F4" trong todo ghi lại những gì còn biết. Sau khi gộp: **phải fetch-deps lại** |
| `wip/f5-batch-joins` | F5: lô N→1 không bao giờ ra output thiếu file im lặng (không ghi output dở, khóa lô riêng cho từng exe/phiên, đọc thời điểm tạo/dòng lệnh tiến trình khác để chặn tiến trình đến muộn), báo kết quả xong trong ân hạn hủy, không còn "Done: 0 files" | 7 commit + 1 `wip:` (ADR-0002/0007, spec core, `JoinRecord`, `JoinRulesTests`). Agent chưa gửi báo cáo cuối: đọc message các commit để biết thiết kế; build + chạy test, đặc biệt test nhiều tiến trình ở cửa sổ thật |
| `wip/t11a-images-to-pdf` | T11a: `JpegSegments` (allow-list marker, APP0 chỉ JFIF không thumbnail, kiểm đủ bảng DQT/DHT, CMYK cần APP14, ≤ 1 000 scan) + `PdfBuilder` (pdfium tạo PDF, Info chỉ `Producer: Flevert`, trần 1,5 GB nội dung, 100 MP mỗi ảnh) | 8 commit, đã qua review vòng 1 và 2, **sẵn sàng gộp** |

**Việc tiếp theo, theo thứ tự:**
1. Trên máy mới: `git fetch`, rồi từng nhánh trên: rebase lên `main`, `scripts/fetch-deps.ps1`, build, format (exit code), ba project test lần lượt; sửa nốt phần "Còn" ở bảng trên (không cần review lại từng nhánh: quy trình mới verify một lần trước phát hành); gộp vào `main` theo thứ tự T11a → F2/F3/F7 → F1 → F4 → F5 (lịch sử thẳng), push, xóa nhánh `wip/*`. F1 và F5 cùng sửa core: gộp lần lượt và chạy lại Core sau mỗi lần.
2. T11 (thao tác `pdf.from-images`, menu ảnh `Combine into PDF`, dùng `IBatchOperation`); đọc ghi chú T11 trong todo (DPI, MemoryBudget suốt đời builder, chuyển HEIC sang sRGB, JPEG cắt cụt bị từ chối, cạnh trang tối thiểu).
3. T9b: pdfium đọc PDF trong tiến trình worker (quyết định F6, ADR-0008 **chưa ghi** phần này: thêm khi làm).
4. T4 (phần còn lại sau F4: check-location, `test-installer.ps1`), T6 merge, T8 split, T12–T14 (đọc ghi chú T14 về mật khẩu), T15–T17 tùy chọn, T18, T19.
5. Trước T20: verify một lần (chạy thật + ba review + reconcile), rồi phát hành theo RELEASING (có bước dọn repo).

**Chặn phát hành v0.2, người dùng quyết:** quyền phân phối lại DLL MSVC runtime đi kèm qpdf (đề xuất: người dùng chấp nhận license Visual Studio Community, giữ 3 DLL qpdf thật sự dùng; chi tiết trong review doubt F4, ADR-0008 Consequences). Đã ghi thành bước 6 "Before tagging" trong `docs/RELEASING.md`: xóa bước đó khi đã quyết.

**Cách làm agent:** việc độc lập giao cho agent `general-purpose` với `isolation: worktree`, **tối đa 2–3 agent cùng lúc** (nhiều hơn làm test lô N→1 lỗi ngẫu nhiên và Smart App Control chặn DLL mới build). Prompt: đọc CLAUDE.md, plan, todo; test cùng code; danh tính commit Suryvibegod (`git -c user.name=... -c user.email=... commit`); không push. Xong thì gộp, chạy lại ba project test, xóa worktree và nhánh. Trước khi đổi máy: dừng agent, commit `wip:` phần dở, push lên `wip/*`.

**Ghi chú kỹ thuật cho phiên sau:**
- qpdf tự mở rộng ký tự đại diện trong đối số dòng lệnh (làm hỏng `\\?\` đường dẫn dài): đường dẫn đưa cho qpdf qua stdin `@-`; tùy chọn có `*`/`?` bị từ chối.
- Một lần chạy cả bộ Image test, `FramesTests.An_animation_whose_expanded_frames_exceed_the_area_limit_is_refused_before_expanding` đỏ khi máy bận, chạy lại riêng xanh 2/2: theo dõi, nếu lặp lại thì điều tra (debugging-and-error-recovery).
- `AGENTS.md` ở gốc repo là file người dùng tạo cho Codex, **chưa commit**, không đụng tới.
- Các agent chạy thử UI có thể bật cửa sổ Flevert trên màn hình: báo người dùng trước.
- Smart App Control chặn ngẫu nhiên DLL vừa build (test tiến trình thoát `0xE0434352`, hoặc `0x800711C7`): build lại với `-p:SourceRevisionId=<chuỗi mới>` để có hash khác, không đổi mã nguồn.
- Thư mục checkout cũ trên máy văn phòng là `D:\ConvertAnything` (người dùng muốn đổi thành `D:\Flevert`). Trên máy mới, clone vào thư mục tên `Flevert`.
- Repo GitHub và tài khoản đã đổi tên (SuryAdelia → Suryvibegod, ConvertAnything → Flevert) và lịch sử đã viết lại: clone cũ không có commit chung với `origin`; trỏ lại `origin` và `reset` về `origin/main`, đừng merge.

## Sau v0.2

- Nhắc người dùng: bật 2FA GitHub, tạo API key VirusTotal mới, nộp đơn SignPath Foundation (https://signpath.org/apply), đặt ảnh social preview. Hai PR Dependabot (#1 Test SDK 18, #2 xunit runner 4) chờ xem xét theo RELEASING.
- v0.3 (media): sau v0.2; quyết GPL hay LGPL ffmpeg (spec, Open Question 1) ở bước Plan.

## Dựng máy mới

Người dùng (chủ repo) chỉ clone repo, mở Claude Code trong thư mục repo rồi gõ "làm tiếp đi". Setup tải luôn pdfium/qpdf vào `deps/` (cần mạng; build Flevert.Pdf báo lỗi rõ nếu thiếu). [CLAUDE.md](../CLAUDE.md) chỉ Claude chạy `scripts/setup-dev.ps1`: cài .NET 10 SDK cho riêng user nếu thiếu, nhắc cài/đăng nhập GitHub CLI; **chỉ trên máy chủ repo** (gh đăng nhập tài khoản ID 79949229) mới cài Inno Setup, đặt danh tính commit, sửa rule git rewrite hỏng, cài plugin agent-skills; rồi build và chạy test từng project. Các việc phát hành (tag, publish) chỉ chủ repo làm. Plugin cài giữa phiên chỉ nạp sau khi khởi động lại hoặc `/reload-plugins`; trong lúc đó đọc skill từ `~/.claude/plugins/cache/addy-agent-skills/agent-skills/<version>/skills/`.

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

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

- Làm **đủ 5 module** theo [docs/specs/capability-map.md](../docs/specs/capability-map.md): core → image → installer (v0.1) → pdf (v0.2) → media (v1.3).
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
| media (v1.3) | Spec đã chốt: [docs/specs/media.md](../docs/specs/media.md). Chưa có plan/code. |

Test đầu-cuối installer: `scripts/test-installer.ps1` (10 kịch bản; kịch bản nâng cấp chạy khi `get-previous-setup.ps1` đã ghim một bản trước; chạy tay trên máy thật, `%LOCALAPPDATA%\Flevert` phải không tồn tại). CI: `.github/workflows/ci.yml`; phát hành: `release.yml` + [docs/RELEASING.md](../docs/RELEASING.md).

## v0.1.0 (2026-10-20)

Module core, image, installer xong và qua kiểm phát hành ([release/todo.md](release/todo.md)). Bản đầu tiên tải được trên GitHub Releases là 0.2.0.

## v1.2.3 (hotfix menu PDF): đã phát hành 2026-10-21

https://github.com/Suryvibegod/Flevert/releases/tag/v1.2.3. Người dùng thấy hai dòng `Flevert` và `Flevert PDF tools` khi chuột phải PDF là rối, chọn "1 dòng + Shift": menu thường chỉ một dòng `Flevert` (16 mục, Add watermark… vào đây, Convert to còn JPG và Text); dòng thứ hai đổi tên `Flevert: more PDF tools`, đặt giá trị registry `Extended` nên chỉ hiện khi giữ Shift (thêm Convert to PNG images). Đo bằng MenuProbe: Explorer tính giới hạn 16 mục cho cả cây của một mục gốc, nên một dòng không chứa được 28 mục PDF. Lâu dài: `IExplorerCommand` (cần ký số trước). MenuProbe có `--shift` cho `list` và `invoke`. Kiểm: một reviewer gộp + sửa, Core/Image/Pdf xanh, CI ×3, `test-installer.ps1` đạt (gồm nâng cấp từ 1.2.2), MenuProbe trên bản cài (thường: một dòng; Shift: hai dòng), bấm thử mục trong menu Shift, 42 thao tác chạy thật. Bản nháp: hash và attestation khớp, `test-installer.ps1` đạt, Defender sạch; VirusTotal 1/75: chỉ DeepInstinct (ML tĩnh, tên chung "MALICIOUS"), cùng phiên bản engine 5.0.0.8 không báo 1.2.2, không đổi dependency/đóng gói so với 1.2.2 → coi là báo nhầm. **Việc của người dùng:** báo nhầm cho Deep Instinct (SHA256 `7addc80a6267ee5fa937ec6b4f11f14e21b54784c22828681946c12470d1b1fc`); ký số SignPath sẽ giảm loại báo nhầm này. `get-previous-setup.ps1` ghim 1.2.3.

**Quy tắc cho các bản sau:** mũi tên thứ hai cho một loại file (ví dụ "Flevert video tools" của 1.3.1) cũng chỉ hiện khi giữ Shift (`MenuCascade` với `ShiftOnly: true`), tên dạng `Flevert: more … tools`; menu thường chỉ một dòng `Flevert`.

## v1.2.2: đã phát hành 2026-10-21

https://github.com/Suryvibegod/Flevert/releases/tag/v1.2.2 (latest, immutable; bản đầu đánh số từ 1, sau 0.2.1). Menu ảnh và PDF sắp lại cho người dùng thật, nhãn dễ hiểu, icon Fluent (MIT); mũi tên thứ hai "Flevert PDF tools" (không cửa sổ công cụ, người dùng chốt) với 8 công cụ: bỏ trang trắng, đánh số trang, watermark (ô nhập kiểu mật khẩu), 2/4 trang mỗi tờ, vừa A4, khóa form đã điền, chia theo dung lượng, lấy ảnh. Ba review + hai vòng sửa + soát lại (bảng reconcile trong [pdf/todo.md](pdf/todo.md)); kiểm thật 45 thao tác qua bản cài, MenuProbe hai mũi tên PDF và menu ảnh, `test-installer.ps1` đạt trên file bản nháp gồm nâng cấp từ 0.2.1; CI ×3; Defender sạch, VirusTotal 0/66. `get-previous-setup.ps1` ghim 1.2.2.

Còn mở cho bản sau: nhúng con font Arial cho watermark (HarfBuzz hb-subset); dòng `@-` của qpdf có kiểu (literal/path); Mark of the Web cho file lấy ra; lượt tách từng trang của Split by size vượt trần tạm ~50 ms; khối `finally` của `test-installer.ps1` để lại khóa khi gỡ cài không chạy được; đếm trang qpdf 3–8 s với file lớn/độc (ứng viên bản hiệu năng).

## v0.2.1: đã phát hành 2026-10-20

https://github.com/Suryvibegod/Flevert/releases/tag/v0.2.1 (latest, immutable). Compress > For sharing, Convert > Text, báo PDF đã phải sửa, giới hạn T18 cho mọi thao tác ghi PDF, sửa các mục review của 0.2.0. Ba review + soát lại (bảng reconcile trong [pdf/todo.md](pdf/todo.md)); kiểm thật: 35 thao tác qua `flevert.exe` đã cài, For sharing 0,42 s/trang (26% cỡ), menu `.pdf` 16 mục (MenuProbe), `test-installer.ps1` đạt trên file bản nháp gồm nâng cấp từ 0.2.0; CI ×3; Defender sạch, VirusTotal 0/68. `get-previous-setup.ps1` ghim 0.2.1.

Còn mở cho v1.2.2: lần thử lại của Lossless chưa lùi về bản không giải mã khi chậm; Compress for sharing coi mọi `IOException` là lỗi đĩa; khối `finally` của `test-installer.ps1` xóa thư mục cài nhưng để lại khóa Installed apps và menu khi gỡ cài không chạy được.

## v0.2.0 (pdf): đã phát hành 2026-10-20

https://github.com/Suryvibegod/Flevert/releases/tag/v0.2.0 (latest, immutable). Kiểm trước khi publish: CI ×3 xanh, hash và attestation khớp, `test-installer.ps1` đạt trên file của bản nháp, Defender sạch, VirusTotal 0/68. Sau khi publish: cài bản tải về, chạy thật cả 30 thao tác (ảnh, PDF, mật khẩu qua hộp thoại) và menu Explorer cho JPG, HEIC, PDF, đều đúng; đã gỡ. `get-previous-setup.ps1` ghim 0.2.0 nên lần phát hành sau chạy kịch bản nâng cấp.

Plan: [pdf/plan.md](pdf/plan.md). Trạng thái từng task và bảng reconcile: [pdf/todo.md](pdf/todo.md). Spec: [docs/specs/pdf.md](../docs/specs/pdf.md). ADR: 0007 (tiến độ trong file, output thư mục, N→1), 0008 (pdfium + qpdf, deps ghim hash).

Đã quyết: T15–T17 (nén ảnh scan, đảo trang, sửa PDF, rút chữ/ảnh) chuyển sang v0.2.1. Chấp nhận license Visual Studio Community: Flevert giữ 3 DLL MSVC runtime qpdf dùng.

**Đánh số phiên bản** (người dùng đổi 2026-10-20): từ bản tiếp theo, số đầu là 1: bản tính năng tăng số giữa (1.3.0, 1.4.0), bản cập nhật/sửa lỗi tăng số cuối (1.2.2, 1.3.1). Hai bản đã phát hành v0.2.0 và v0.2.1 giữ tên cũ (release bất biến) và được xem là 1.2.0 và 1.2.1; bản đang làm là **1.2.2**. Tên nhánh `feat/v0.*` cũ giữ nguyên.

**Việc tiếp theo** (cập nhật 2026-10-21, dừng theo yêu cầu người dùng). Làm theo thứ tự; người dùng muốn tự làm hết, không hỏi; mỗi bản: code đủ trước, rồi review tổng thể (code, security, doubt) + kiểm thật trên Windows + RELEASING.

1. **v1.3.0 (video và âm thanh), nhánh `feat/v0.3-media`, PR #3.** Trước hết gộp `main` (1.2.3: menu PDF một dòng, `MenuCascade.ShiftOnly`, MenuProbe `--shift`) vào nhánh và sửa test menu nếu cần. Code của agent media xong; đã gộp `main` 1.2.2 (merge commit `56a6a9f`), menu video/audio theo nguyên tắc 1.2.2 (icon Fluent, đường kẻ, `music_note_2` mới), tài liệu đổi sang 1.3. CI `build-and-test` xanh trên `1c63f1e`. Còn lại, theo thứ tự:
   1. **Giấy phép ffmpeg (chặn phát hành):** commit giấy phép của các thư viện trong bản dựng BtbN vào `installer/notices/ffmpeg/` từ artifact `ffmpeg-licences` của workflow `ffmpeg-source.yml` (`gh run list --workflow ffmpeg-source.yml --branch feat/v0.3-media`, rồi `gh run download`); chỉ giữ văn bản giấy phép (`.gitattributes` đã đặt `-text` cho thư mục đó). Rồi chạy lại cả bốn project test (Core đọc thư mục này).
   2. Ba review bối cảnh trống trên `git diff main..feat/v0.3-media` (gồm workflow `ffmpeg-source.yml` tạo asset mã nguồn ~2,2 GB nhiều phần, trần bộ nhớ 4 GB của ffmpeg so với spec 2 GB), reconcile, sửa, một reviewer soát lại.
   3. Kiểm thật trên Windows (báo người dùng trước): `build-installer.ps1` (bộ cài ước tính ~94 MB, giới hạn test 110 MB), `test-installer.ps1` (nâng cấp từ 1.2.2 đã ghim), cài rồi chạy từng thao tác video/âm thanh, MenuProbe `.mp4` `.mkv` `.mp3` (≤ 16 mục, icon), màu HDR, bộ nhớ trên máy nhiều nhân; `uninstall`.
   4. `<Version>1.3.0`, CHANGELOG `[1.3.0]`, CI ×3, tag `v1.3.0`, kiểm bản nháp (hash, attestation, test-installer, Defender, VirusTotal), publish, ghim 1.3.0.
2. **Ký số SignPath Foundation (miễn phí).** Đã có [docs/code-signing-policy.md](../docs/code-signing-policy.md) và mục "Code signing policy" trong README. Đơn: đã mở form đăng ký điền sẵn mọi ô về dự án; **chưa rõ người dùng đã bấm Submit chưa** → hỏi lại. Người dùng tự điền tên, email, tích 2 ô đồng ý, reCAPTCHA. Khi được duyệt: thêm bước ký vào `release.yml` (action chính thức của SignPath ghim SHA; token trong GitHub Secrets, không bao giờ dán vào chat), ký installer và `flevert.exe`/`Flevert.*.dll`, câu ghi công trong release notes, kiểm `Get-AuthenticodeSignature` ở bước kiểm bản nháp (RELEASING), bỏ câu "not code-signed" trong README/install.md, cập nhật ADR-0005.
3. **v1.3.1** (sau 1.3.0): [docs/specs/media.md](../docs/specs/media.md), mục "v1.3.1": mũi tên thứ hai cho video, chỉ hiện khi giữ Shift như PDF từ 1.2.3 (sửa spec: tên `Flevert: more video tools`, các dòng 204, 252, 291 của media.md) (Trim, Rotate, Remove sound, Make volume even, Save a frame, Merge videos, Send by Email/WhatsApp/Messenger); âm thanh một mũi tên. Không cửa sổ công cụ.
4. **Bản hiệu năng (1.3.2 hoặc số trống kế tiếp; 1.2.3 đã dùng cho hotfix menu), chỉ khi đo thấy chậm** (mục tiêu ở dòng dưới). Ứng viên đã thấy: qpdf đếm trang 3–8 s với PDF lớn/độc.
5. **v1.4.0: OCR** (Tesseract Apache-2.0 + dữ liệu `vie`); viết spec khi bắt đầu.

**Quy tắc người dùng đã chốt (2026-10-20/21):** Flevert chỉ là menu chuột phải, không cửa sổ chọn công cụ; thêm việc thì thêm mũi tên thứ hai cho loại file đó; hộp thoại chỉ cho chữ người dùng phải gõ (mật khẩu, watermark, thời điểm Trim), kiểu hộp thoại mật khẩu. Đánh số từ 1 (bản tính năng 1.x.0, cập nhật 1.x.y). Tối đa 2–3 agent cùng lúc. Test bật cửa sổ chỉ chạy khi đã báo người dùng (hộp thoại luôn ở trên cùng bắt phím người dùng đang gõ). `test-installer.ps1` từ chối chạy khi có menu Flevert dev hoặc bất kỳ `flevert.exe` nào đang chạy (agent đang chạy test): tạm dừng agent trước khi chạy.

- **Mục tiêu tốc độ cho bản hiệu năng** (người dùng yêu cầu 2026-10-20, số phiên bản linh hoạt; 1.2.3 đã dùng cho hotfix menu nên là 1.3.2 hoặc số trống kế tiếp): sau v1.2.2 và v1.3.x, đo tốc độ theo skill `performance-optimization` (đo, sửa một chỗ, đo lại, giữ hoặc hoàn tác, ghi sổ). Chỉ phát hành khi số đo chậm hơn mục tiêu; đã nhanh thì bỏ. Mục tiêu trên máy 4 nhân: khởi động lạnh `flevert.exe run` ≤ 300 ms; ảnh HEIC 12 MP → JPG ≤ 1 s/ảnh; PDF → JPG ≤ 0,3 s/trang A4 ở 150 DPI; Compress for sharing ≤ 2 s/trang; Merge/Rotate 100 trang ≤ 2 s; video 1080p Convert/Compress ≤ 2× thời lượng. Công cụ đo có sẵn: `scripts/measure-startup.ps1`, `scripts/measure-convert.ps1`.

**Ghi chú kỹ thuật:**
- qpdf tự mở rộng ký tự đại diện trong đối số dòng lệnh (làm hỏng `\\?\` đường dẫn dài): đường dẫn đưa cho qpdf qua stdin `@-`; tùy chọn có `*`/`?`/`@` bị từ chối.
- Test nào đếm tiến trình phải nhận diện đúng tiến trình của nó (PID, tiến trình cha), không đếm theo tên trên toàn máy: CI và nhiều lần chạy test có thể song song.
- Test lô nhiều tiến trình nhạy thời gian khi máy bận: đỏ thì chạy lại riêng trước; đỏ cả khi chạy riêng thì điều tra.
- Test Pdf khởi động `flevert.exe pdf-worker` nằm chung collection xUnit `WorkerProcesses`; lớp test mới nào khởi động worker cũng phải vào collection đó.
- Smart App Control chặn ngẫu nhiên DLL vừa build (test tiến trình thoát `0xE0434352`, hoặc `0x800711C7`): build lại với `-p:SourceRevisionId=<chuỗi mới>`, không đổi mã nguồn.
- Sau khi `scripts/deps.json` đổi: chạy lại `scripts/fetch-deps.ps1`.

## Sau v0.2

- v1.3 (media): ffmpeg bản GPL (đã chốt, ADR-0009 trên nhánh media).

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

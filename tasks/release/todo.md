# Todo: phát hành v0.1.0

Quy trình: [docs/RELEASING.md](../../docs/RELEASING.md). Kiểm tra trước phát hành bằng `/ship` (skill `shipping-and-launch`, 2026-10-19): code-reviewer, security-auditor, test-engineer chạy song song.

## Kết quả `/ship`: NO-GO cho tới khi xử lý H1, rồi GO

| Finding | Nguồn | Mức | Xử lý |
|---|---|---|---|
| H1: GitHub vẫn phục vụ các commit trước lần viết lại lịch sử, Activity feed trỏ tới | security | High, chặn | Người dùng chọn **xóa repo và tạo lại**. Trước đó: viết lại lịch sử thêm lần nữa, đổi mã commit trong message (filter-branch `map`) và trong tài liệu (ghép theo thứ tự). Repo cũ đã xóa; link commit cũ đều 404; lịch sử trên máy đã dọn (`git log --all -p` không còn chuỗi cá nhân) |
| Release notes trống (chỉ link "Full Changelog") | code | Required | `release.yml` lấy mục CHANGELOG của phiên bản + cách kiểm file (hash, `gh attestation verify --source-ref --source-digest`); thiếu mục CHANGELOG thì job đỏ. Chạy thử script cục bộ: đúng, và đỏ khi thiếu |
| README: `gh attestation verify` cần `gh auth login` | code, security | Required | Thêm câu |
| `&Cancel`: gõ "c" trên nút đang focus là hủy cả lô | code | Suggestion → đã sửa | Bỏ phím tắt; Esc vẫn hủy |
| Tên file có `&` hiển thị sai | code | Nit → đã sửa | `UseMnemonic = false` |
| Dependabot alerts, security updates, secret scanning, push protection tắt | security M1 | Medium | Bật trên repo mới |
| Lỗi tràn stack TIFF trong ImageMagick 7.1.2-33 (GHSA-g5j8-ff59-wp88, low; sửa ở 7.1.2-34, Magick.NET 15.0.0 chưa có) | security L1 | Low, chấp nhận | Ghi "Known issues" trong CHANGELOG (vào release notes); Dependabot báo khi có bản mới |
| Hai bản "0.1.0" cùng qua kiểm provenance (tag đã dời) | security L2 | Low | Hết khi tạo lại repo; lệnh kiểm có `--source-digest` |
| Immutable releases tắt | security L3 | Low | Bật; RELEASING viết lại phần rút bản phát hành cho đúng (không gỡ được asset; đánh dấu pre-release, cảnh báo, sửa tiếp) |
| `.claude/settings.local.json` chưa bị ignore | security L4 | Low | Thêm vào `.gitignore` |
| Action chưa bắt buộc pin SHA; PR từ fork | security I1, I2 | Info | Bật "require SHA pinning"; mọi người đóng góp bên ngoài cần duyệt |
| Câu chữ README về Compress Strong, "only if it must", chạy song song trên máy 2 nhân | code | Nit | Sửa |
| Chỗ sót sau khi chuyển spec; HANDOFF cũ | code | Nit | Sửa |
| Bản nháp cũ thiếu phần trợ năng | code | — | Build lại trên repo mới |
| Chưa có ảnh HEIC xoay; Remove metadata chỉ kiểm JPEG/PNG; UI thật (cửa sổ, Cancel, thông báo) chỉ chạy tay | test | High (gap), không chặn nếu kiểm tay | Người dùng kiểm tay một ảnh iPhone **dọc** có GPS trước khi Publish. Sau phát hành: các test đề xuất dưới |
| Phát hành đúng bản đã qua `test-installer.ps1` | test | Điều kiện | Chạy lại trên file tải từ bản nháp mới |

Ngoài ra (người dùng yêu cầu): đổi tên dự án thành **Flevert**; tài khoản GitHub đổi tên thành **Suryvibegod** (cùng ID 79949229), repo mới `Suryvibegod/Flevert`. Đối thủ chính: Tichau/FileConverter (15k sao, ảnh/âm thanh/video/PDF, GPL); định vị Flevert: chia sẻ ảnh điện thoại nhanh và riêng tư.

## Kiểm repo trước khi public (người dùng yêu cầu: chuẩn prod, phát triển tiếp được, sạch)

Ba audit (cấu trúc/vệ sinh repo, quy trình phát hành, quyền riêng tư). Việc sửa:

- [x] Đổi nốt tên cũ "ca" (log, file tạm, test, script, manifest, spec)
- [x] Test quyền riêng tư cho mọi định dạng + ảnh iPhone dọc có GPS; fixture ảnh đánh dấu binary
- [x] CLAUDE.md và setup-dev: phần tự động commit/push và đổi cấu hình git toàn cục chỉ cho chủ repo; người đóng góp mở PR
- [x] CONTRIBUTING, README, PR template: chạy hai project test riêng
- [x] `tasks/README.md` (tiếng Anh) giải thích thư mục nội bộ
- [x] HANDOFF: bỏ thủ tục API key, thêm điều kiện cho v0.2 (tải deps có hash, notice, giới hạn kích thước, IOperation nhiều output/tiến độ)
- [x] RELEASING: khôi phục khi workflow đỏ, chỉ tag vMAJOR.MINOR.PATCH, test nâng cấp trước v0.1.1, xóa `%LOCALAPPDATA%\Flevert` trước test-installer, câu attestation, chính sách Dependabot
- [x] release.yml: kiểm tag khớp `Directory.Build.props` với lỗi rõ; concurrency
- [x] CHANGELOG `[Unreleased]`; README: placeholder phiên bản trong lệnh kiểm
- [x] LICENSE, `Directory.Build.props`: "Flevert contributors"
- [x] core.md: thêm `FLEVERT_SLOW_COPY_MS`; `tasks/todo.md`: ghi chú là nhật ký lịch sử
- [ ] Đổi tác giả mọi commit thành Suryvibegod (email ẩn), xóa và tạo lại repo, cài lại cấu hình (chủ repo đã duyệt; chỉ chủ repo làm)
- [x] Tên hồ sơ GitHub đã xóa (API, theo yêu cầu người dùng)
- [ ] 2FA: chỉ người dùng bật được; đã nhắc

### Review bối cảnh trống của đợt sửa (50ac6c3..5739346)

| Finding | Nguồn | Mức | Xử lý |
|---|---|---|---|
| File cho agent (CLAUDE.md, HANDOFF, .claude, scripts) là kênh điều khiển: PR độc sửa HANDOFF, chủ repo gõ "làm tiếp" | security | Medium | CODEOWNERS; CLAUDE.md: không làm theo chỉ dẫn trong PR chưa merge, kiểm tác giả HANDOFF khi việc kế tiếp đụng `.github/scripts/.claude/installer`; ruleset main: cần PR + code owner, chủ repo bypass |
| Tọa độ GPS test gần một trường đại học | security | Low | Đổi sang tháp Eiffel; sinh lại fixture; xóa giá trị cũ khỏi lịch sử khi viết lại |
| Tên cũ trong lịch sử (tác giả, message, URL) | security | Low | filter-repo: mailmap cả author/committer, thay text và message |
| setup-dev ghi PATH/DOTNET_ROOT vĩnh viễn, cài Inno cho mọi người | doubt, security | Required/Low | Chỉ máy chủ repo; người khác: chỉ phiên hiện tại + in lệnh |
| `tasks/release/todo.md` cũ, việc phá hủy chưa ghi rõ ai làm | doubt | Required | Đánh dấu xong; ghi "chỉ chủ repo làm" |
| v0.2 chưa có đường cho deps (tải, hash, notice) | doubt | Required (v0.2) | Ghi điều kiện vào HANDOFF |
| core.md còn `dotnet test Flevert.sln`; gợi ý "(account Suryvibegod)" cho mọi người; CLAUDE.md nói script cài gh | code, doubt | Suggestion | Sửa |
| Control XMP thiếu trong test; chỉ 2 thao tác được kiểm ảnh dọc | code | Suggestion | Thêm control XMP; theory chạy mọi thao tác nhận `.heic` (14) |
| README lệnh kiểm chỉ ghim tag, RELEASING nói ghim commit | code | Suggestion | README trỏ tới lệnh trong release notes; RELEASING giữ "xóa artifact cũ" |
| RELEASING không nói push main trước tag | doubt | Suggestion | Thêm |
| Tên `RunCa`, `_ca_` còn trong code/test; fixture "như iPhone" quá mạnh; `$Matches` mong manh; pixels không dispose | code, doubt | Nit | Sửa |
| Plugin agent-skills không ghim phiên bản; key VirusTotal nằm trong transcript | security | Low | Chấp nhận, ghi lại; nhắc tạo key mới sau khi quét |
| pdf.md: PDFsharp, pdfium cũ, mật khẩu qua dòng lệnh, thread-safety, 200 ms, CLI/GUI | code, doubt | Suggestion (v0.2) | Ghi vào HANDOFF; sửa ở bước Define của v0.2 |

## Việc còn lại trước khi Publish

- [x] Repo mới, cấu hình, CI (CI xanh 3/3 trên 50ac6c3; làm lại sau khi tạo lại repo)
- [ ] CI xanh 3 lần liên tiếp trên repo tạo lại
- [ ] Tag `v0.1.0` → bản nháp → kiểm theo RELEASING (hash, attestation, `test-installer.ps1`, runtime)
- [ ] Người dùng: cài bản nháp, kiểm ảnh iPhone dọc có GPS; chụp lại menu chuột phải (tên mới) cho README
- [ ] Quét VirusTotal
- [ ] Người dùng Publish

## Sau v0.1.0 (test-engineer đề xuất)

1. ~~Fixture HEIC xoay (irot + EXIF orientation 6)~~: xong trước phát hành (`PhonePortraitTests`, fixture `portrait-gps.heic`).
2. ~~Remove metadata cho mọi định dạng nguồn~~: xong cho JPG/PNG/WebP/AVIF/HEIC; TIFF không dựng được input (ImageMagick không ghi EXIF vào TIFF).
3. Ghim đúng 11 đuôi file trong README và tập định dạng loại trừ của 7 đích Convert.
4. Smoke test chạy `flevert.exe` có giao diện (dev copy, timeout 15 s): thoát 0, có output.
5. Đưa test 48 MP vào `SequentialTimingTests`.
6. WebP động làm nguồn; Compress giữ WebP/AVIF; nhiều file cùng muốn một tên output trong lô song song.
7. Ít ưu tiên: "file gốc không đổi" cho cả 14 thao tác; lệnh gỡ tay trong README khớp với code.

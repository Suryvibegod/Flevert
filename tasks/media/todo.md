# Todo: media (v1.3.0)

Plan: [plan.md](plan.md). Spec: [docs/specs/media.md](../../docs/specs/media.md).

Mỗi task: test viết trước và thấy đỏ; build, `dotnet format --verify-no-changes`, các project test xanh (chạy lần lượt); ghi chú ngắn ở đây; commit nguyên tử. Review ba góc làm một lần trước khi phát hành (CLAUDE.md). Mục "cần kiểm trên Windows": viết trong phiên Linux, chưa chạy được ở đây.

Lệnh: `powershell -File scripts/fetch-deps.ps1`, `dotnet build Flevert.sln`, `dotnet test tests/Flevert.Media.Tests` (cùng ba project test cũ).

## T1: deps ffmpeg, project, Ffprobe
- [x] `scripts/deps.json`: ffmpeg `autobuild-2026-09-30-13-08` `win64-gpl-shared-8.1` + SHA256, giữ exe/DLL/LICENSE (plan, "Bản dựng ffmpeg"); notices tạm cho `write-notices.ps1`
- [x] `src/Flevert.Media` (chép `deps/ffmpeg/bin/*` vào `ffmpeg\`), `tests/Flevert.Media.Tests` (sln; CI/release/setup-dev ở T8)
- [x] `Ffprobe`: JSON, timeout 10 s, whitelist giao thức + demuxer; `MediaInfo` (thời lượng, luồng video/audio, xoay, HDR); từ chối: không có luồng, thời lượng 0 hoặc > 12 h, > 8K, không đọc được
- Kiểm: unit đọc JSON (xoay ở `side_data_list` và `tags.rotate`, HDR, ảnh bìa); tích hợp với fixture sinh bằng ffmpeg
- Đã làm (2026-10-20, Linux, ffmpeg 6.1 của hệ thống): `Ffmpeg.ProbeAsync` + `MediaInfo.Parse`; 36 test xanh. Fixture không lưu trong repo: `Fixtures.cs` sinh bằng lavfi lần đầu được dùng (1080p, MOV điện thoại xoay 90° có GPS, HEVC HDR10, MKV hai audio, WAV, M4A). Đối chứng âm: bỏ `-format_whitelist` thì test danh sách concat (file nó trỏ là video thật) đỏ; HLS và concat trỏ file không có vẫn bị từ chối nhờ whitelist giao thức. Core (2 commit riêng): `ExternalToolLimits.BelowNormalPriority` (đặt trên tiến trình, không qua giới hạn của job vì cần đặc quyền; **cần kiểm trên Windows**: test PowerShell in lớp ưu tiên của nó và của tiến trình con) và `FileProgress.Percent` ("37%", thanh tiến độ theo %). Tên `VideoTrack`/`AudioTrack` (CA1711 cấm hậu tố Stream).

## T2: lát dọc `audio.to-mp3`
- [x] `Ffmpeg.EncodeAsync`: `-nostdin -hide_banner -nostats -n -progress pipe:1`, input `file:`, output `file:` + `-f`; tiến độ `out_time_us`; timeout thời lượng × 6 + 60 s; lỗi → `MediaException` tiếng Anh ngắn, chi tiết stderr vào log
- [x] Core: `FileProgress.Percent`, `ExternalToolLimits.BelowNormalPriority` (làm ở T1)
- [x] `audio.to-mp3` (MP3 V2), metadata theo danh sách cho phép; đăng ký `MediaOperations.All` trong `Program.cs`
- Kiểm: thời lượng ±0,1 s, codec mp3; Cancel giữa chừng → không còn `.partial`, ffmpeg dừng ≤ 2 s; tiến độ tăng dần tới 100
- Đã làm: `AudioConvert` (chung cho Convert audio và Extract audio, theo `AudioFormat`), `MediaJob` (tên output, metadata cho phép, `-fs`, đĩa trống, %). 10 test (`AudioToMp3Tests`); đối chứng âm: bỏ `MetadataOptions` thì test GPS đỏ (muxer MP3 ghi `location` thành ID3 TXXX). Giới hạn output: spec ghi ≤ 4 × input + 100 MB, nhưng MP3 ngắn → WAV hợp lệ lớn gấp ~10 lần, nên lấy max với 2 × cỡ ước lượng theo thời lượng; output chạm `-fs` (ffmpeg dừng và vẫn thoát 0) là lỗi, không phải file cụt. Bỏ chapter (`-map_chapters -1`). `FlevertExeTests` (chạy `flevert.exe run audio.to-mp3` thật; ffmpeg `BelowNormal`; giết `flevert.exe` → ffmpeg chết ≤ 3 s): **cần kiểm trên Windows**, bỏ qua trên Linux.

## T3: audio còn lại + extract
- [x] `audio.to-m4a/-wav/-flac/-ogg/-opus`, không đề xuất đích trùng nguồn; M4A từ AAC chép luồng
- [x] `video.extract-mp3/-m4a` (M4A chép luồng AAC, hash luồng trùng)
- Đã làm: 23 test (`AudioConvertTests`): mỗi đích từ WAV (codec, thời lượng ±0,1 s, title), mọi đuôi nguồn đọc được qua whitelist demuxer (mp3, aac ADTS, m4a, flac, ogg, opus, wma/asf, aiff, aif), AAC → M4A và Extract M4A không mã hóa lại (MD5 của âm thanh giải mã trùng; MD5 gói khác nhau giữa ADTS và MP4 nên không dùng được), 5.1 → MP3 stereo, MKV hai audio lấy luồng default (T10: nay lấy luồng **đầu**). Phát hiện: Ogg/Opus giữ tag trên luồng chứ không trên file, nên `AudioTrack.Tags` được đọc và dùng khi file không có tag đó (vẫn qua danh sách cho phép: GPS trên luồng cũng bị bỏ, có test). WAV > 4 GB: `-rf64 auto`.

## T4: video.to-mp4/mov/mkv/webm
- [x] H.264 CRF 20 `medium` `yuv420p` + AAC 192k, `+faststart`; xoay; HDR → SDR; bỏ GPS; MOV/MKV như MP4; WEBM VP9 + Opus
- [x] Ước lượng output so với đĩa trống; `-fs` ≤ 4 × input + 100 MB
- Đã làm: `VideoConvert` + `VideoEncoding` (kích thước theo hình hiển thị, cạnh chẵn làm tròn xuống để không phóng to; lọc `scale` chỉ khi đổi cỡ; tone map chỉ khi HDR; `format=yuv420p` cuối). 19 test; đối chứng âm: bỏ tone map thì test HDR đỏ (x264 chép nguyên nhãn PQ/BT.2020). Video điện thoại: ra 360 × 640 thật trong điểm ảnh, không còn display matrix, không còn tag `location*` (cả file và luồng), giữ `creation_time`. Convert giữ mọi luồng audio cùng ngôn ngữ (T10: nay chỉ luồng đầu; MKV không còn là đích). Đo 2026-10-20 (4 nhân, 1080p 10 s): x264 `medium` 8,6 s; VP9 `-cpu-used 2` 25 s, `4` 18 s (file lớn hơn 3 %) → chọn 4, và timeout của WEBM gấp đôi (thời lượng × 12 + 60 s) vì VP9 chậm gấp ~2 lần x264. Ước lượng cỡ: 0,15 bit/điểm ảnh/khung + audio.

## T5: compress
- [x] Light/Balanced/Strong (CRF 23/28/32, Strong ≤ 720p, AAC 128/96/64k); không nhỏ hơn gốc → skipped
- [x] Under 10/25/100 MB: bitrate, chọn độ phân giải, 2 lượt (passlog trong `%LOCALAPPDATA%\Flevert\media`), chạy lại −10 % tối đa 2 lần, "Too long to fit under 10 MB"; gốc đã dưới N MB → skipped
- Đã làm: `VideoCompress`, `VideoCompressUnder` (17 test, gồm tiêu chí 2: 60 s 1080p → < 25 000 000 byte, 60 s; 20 phút → "Too long to fit under 10 MB…", không tạo file). `Plan`: audio 128k (96k khi tổng < 1 000 kbit/s, 64k khi < 400), bitrate cần theo cạnh ngắn 2160: 8 000, 1440: 5 000, 1080: 2 500, 720: 1 200, 480: 500 kbit/s; không đủ cho cỡ nào thì 480p nếu video ≥ 100 kbit/s, không thì lỗi. Lượt 1 chỉ hình (`-f null`), lượt 2 có `-fs` bằng giới hạn; chạm `-fs` hoặc vẫn ≥ giới hạn → xóa file tạm (ffmpeg không ghi đè), bitrate × 0,9, tối đa 2 lần. Thư mục `media` (commit core riêng: `AppData.Subfolders`, gỡ cài, `docs/install.md`, `test-installer.ps1`); thư mục của lần chạy bị giết được xóa sau 1 ngày; kiểm đĩa trống cho file thống kê của x264 (mbtree 2 byte/khối 16 × 16/khung: 20 phút 1080p ≈ 590 MB). **Cần kiểm trên Windows**: x264 mở `-passlogfile` có ký tự Unicode trong `%LOCALAPPDATA%` (tên user tiếng Việt).

## T6: video.to-gif
- [x] ≤ 480 px cạnh dài, 12 fps, palette; > 30 s → 30 s đầu + note
- Đã làm: `VideoToGif`, một lần chạy `split` → `palettegen` (stats_mode=diff) → `paletteuse` (bayer 5, diff_mode=rectangle), lặp vô hạn, không metadata; HDR tone map trước. 5 test (480 × 270 từ 640 × 360, đứng 270 × 480, nhỏ giữ cỡ, 45 s → 30 s); test note qua `Runner` (`"Only the first 30 seconds were converted to GIF."`) **cần kiểm trên Windows** (runner chỉ nhận đường dẫn Windows).

## T7: menu
- [x] Video 16 mục (15 khi đích trùng nguồn), audio ≤ 7; test ≤ 16 cho mọi đuôi; `.gif` vẫn là ảnh
- [ ] MenuProbe trên Explorer thật (cần kiểm trên Windows): `.mp4`, `.mov`, `.mkv`, `.mp3`, `.flac` (tiêu chí 9); rồi `uninstall-menu`
- Đã làm: `MediaMenuTests` (17 test) cài `MediaOperations.All` vào registry giả: bố cục cũ (T10 đã thay: `Compress ▸` · `Convert to ▸ MP4 · MOV · WEBM · GIF` · `Extract audio ▸` · `Remove hidden info` = 16, 15 cho `.mp4 .mov .webm`); audio `Convert ▸` 5 hoặc 6 đích; lệnh registry `run audio.to-<đích>`. Thứ tự menu đến từ thứ tự trong `MediaOperations.Create`.

## T8: đóng gói
- [x] `build-installer.ps1`/Inno: `ffmpeg\` trong thư mục cài; `THIRD-PARTY-NOTICES.txt`; `test-installer.ps1` kiểm `ffmpeg\`; cỡ bộ cài ≤ 150 MB (đo)
- [x] `scripts/fetch-ffmpeg-source.sh` → asset `ffmpeg-source-<tag>.tar`; `release.yml` đính kèm; ADR-0009
- [x] CI, release, setup-dev: bước `dotnet test tests/Flevert.Media.Tests`; `ffmpeg -version` khớp bản ghim; RELEASING: lịch kiểm bản mới hằng tháng
- [ ] **Chặn phát hành**: commit giấy phép của 101 thư viện vào `installer/notices/ffmpeg/` từ artifact `ffmpeg-licences` của workflow `ffmpeg-source.yml` (chạy trên pull request đổi pin); viết tay điều khoản cho thư viện trong `licences-missing.txt` (chắc chắn có nv-codec-headers: giấy phép chỉ nằm trong header). Đến lúc đó `write-notices.ps1` (nên `build-installer.ps1`) báo lỗi "Nothing matches installer/notices/ffmpeg/*", đúng ý: không có bộ cài nào thiếu giấy phép.
- [ ] **Cần kiểm trên Windows**: `build-installer.ps1` + `test-installer.ps1` (cỡ thật, `ffmpeg\` không bị giải nén vào `%TEMP%` lúc kiểm thư mục, MP3 bằng bản cài, gỡ sạch cả `ffmpeg\` và `%LOCALAPPDATA%\Flevert\media`), `InstallerScriptTests` (chạy `check-location` từ bản sao không có `ffmpeg\`), khởi động lạnh (`measure-startup.ps1`, tiêu chí 11: ≤ +10 % so với v0.2).
- Đã làm: Inno cài `ffmpeg\*` vào `{code:InstallFolder}\ffmpeg` (loại khỏi mục `{app}`); test script cho `DestDir` có thư mục con. Ước lượng bộ cài: thư mục ffmpeg nén `xz -9e` = 46,3 MB → ≈ 94 MB (giới hạn test-installer 110 MB). Nguồn: không viết được bằng PowerShell trên runner Windows (BtbN `download.sh` cần Docker Linux), nên là script bash chạy trên `ubuntu-latest` (workflow tái dùng `ffmpeg-source.yml`, gọi từ `release.yml`); dựng image công cụ của BtbN từ đúng commit (không kéo `base:latest`), chạy `download.sh`, giữ 101 stage mà `win64 gpl-shared 8.1` bật (đếm lại bằng chính logic `ffbuild_enabled` của BtbN), cộng FFmpeg `29e619e767cde9045a75c29bc9a8278ae7b3a98b` và repo BtbN `6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b`; asset là `.tar` (bên trong đã là `.tar.xz`), chặn ≥ 2 GiB. Trong phiên Linux này Docker kéo image bị chặn và phần lớn host mã nguồn ngoài GitHub (videolan, googlesource, freedesktop, sourceforge, savannah) bị chặn: đã chạy thử script với Docker/`download.sh` thay bằng tải 2–3 stage từ GitHub (bố cục archive, README, giấy phép phẳng `<stage>-<file>`, `licences-missing.txt`), shellcheck sạch; **chạy thật lần đầu trên CI của pull request**. Pin nguồn nằm trong `deps.json` (`ffmpegSource`). CHANGELOG `[Unreleased]` có mục media.

## T9: file độc
- [x] HLS, concat, subfile, 0 byte, hỏng, thời lượng giả 99 giờ: ≤ 10 s hoặc lỗi rõ, không kết nối mạng, không còn `.partial`
- Đã làm: `MaliciousMediaTests` (11 test, mỗi file < 10 s, thư mục chỉ còn input, `TcpListener` loopback không nhận kết nối nào): playlist HLS đặt tên `.mp4` trỏ cổng test (qua 4 loại thao tác), danh sách concat `.mkv` trỏ video thật bên cạnh, file rỗng/văn bản/script, MOV cắt mất `moov`, MPEG-TS có mốc thời gian nhảy 99 giờ (→ "longer than 12 hours", trước khi mã hóa), MKV hỏng giữa file (ra file hoặc lỗi rõ). `subfile:` không áp dụng: input luôn là `file:<đường dẫn>`, nội dung file không chọn được giao thức. Đối chứng âm: bỏ hai whitelist thì test concat đỏ; bản đầu của test concat có thêm dòng `http://…` nên vẫn xanh khi bỏ whitelist (demuxer concat tự từ chối URL, `safe=1`), đã sửa để chỉ còn tên file. HLS trong `.mp4` còn bị chính ffmpeg (≥ 6) từ chối vì đuôi không phải `.m3u8`; whitelist là lớp thứ hai.

## T10: spec bổ sung lần 2 (gộp từ `main` 2026-10-20)
- [x] Gộp `origin/main` vào nhánh (merge, không rebase: v0.2.1/v1.2.2 chưa vào `main`; rebase theo spec khi chúng vào)
- [x] Bỏ `video.to-mkv`; nhãn `Convert to ▸`; Compress đứng đầu, nhãn Light (best quality)/Strong (smallest); `Remove hidden info` riêng; video 16 mục (15 khi đích trùng nguồn), audio `Convert to ▸`
- [x] Luồng video đầu + luồng audio đầu; note khi bỏ track audio khác (test note **cần kiểm trên Windows**)
- [x] `video.strip-metadata`: `-c copy`, cùng container (mpegts 192 byte cho `.mts/.m2ts`), chỉ giữ `creation_time`, giữ ma trận xoay; bỏ luồng khác; không có gì → skipped. 12 test: gói video/audio trùng MD5 với nguồn, không còn khóa `location*`, mp4/webm/avi viết lại đúng container; đối chứng âm: bỏ `-map_metadata -1` thì 4 test đỏ. "Hidden info" = tag ngoài danh sách tag do container/ffmpeg tự ghi, hoặc luồng bị bỏ (data, phụ đề, đính kèm, ảnh bìa, track thêm). MPEG-TS không có chỗ cho title, nên TS chỉ có title là "không có gì để bỏ".
- [x] Core: `OutputNote.AddSizes` + `FileResult.Sizes` + `BatchSummary`: "Done: clip (under 25 MB).mp4 (24.3 MB, was 412 MB)", nhiều file "Done: 5 files, 412 MB → 61 MB" (chỉ khi mọi output có cỡ); `SizeText` (KB < 1 MB, 1 chữ số thập phân < 100 MB, GB từ 1 GB). Compress và Under N gọi nó (test qua `Runner`: **cần kiểm trên Windows**).
- [x] ffmpeg/ffprobe chạy `SingleProcess = true`, trần 2 GB; bị từ chối bộ nhớ → "needs more memory than Flevert allows its video tool (2 GB)".
- [x] File độc thêm: HLS/`ffconcat` trỏ một bản ghi âm cục bộ khác (bị từ chối bởi 3 loại thao tác, không ghi gì; đối chứng âm: bỏ whitelist demuxer thì danh sách concat tên trơn đỏ; bản đầu dùng file `.txt` thì vẫn xanh khi bỏ whitelist vì ffmpeg 6+ không còn dò `.txt` bằng demuxer `tty`, đã đổi), AVI khai 100 000 px (→ "larger than 8K" trước khi giải mã), MKV có phụ đề + tệp đính kèm → MP4 chỉ h264 + aac.
- [x] Đo tốc độ (máy dev 4 nhân, ffmpeg 6.1, video 60 s 1080p): Convert to MP4 (CRF 20) 36,3 s = 0,60× thời gian thực; Compress Balanced (CRF 28) 32,2 s = 0,53×; mục tiêu ≤ 2×: đạt.
- [x] Icon Fluent cho nhóm và việc đơn lẻ (`arrow_minimize`, `arrow_swap`, `music_note_2`, `shield`) sau khi v1.2.2 vào `main`: làm ở T11.
- [ ] Ảnh chụp menu video và audio (sáng và tối) trong PR (maintainer, trên Windows thật).

## T11: gộp `main` 1.2.2 (2026-10-21)
- [x] Merge `origin/main` (1.2.2) vào nhánh, không viết lại lịch sử PR. Xung đột: CHANGELOG (mục media ở `[Unreleased]`, trên `[1.2.2]`), `installer/Flevert.iss` (mục `{app}` loại `ffmpeg\*`, chú thích nói cả thư mục `icons`), `Program.cs` (catalog có `MediaOperations.All`), `NoticesScriptTests` (publish giả có cả `ffmpeg\` lẫn `icons\`; notices phải có FFmpeg, GPL và Fluent). Xung đột ngầm: `IRegistry.SetDword` (1.2.2) thêm vào registry giả của `MediaMenuTests`.
- [x] Menu theo nguyên tắc 1.2.2: `MediaOperations.Looks` (Compress `arrow_minimize`, Convert to `arrow_swap`, Extract audio `music_note_2`, nhóm 1; Remove hidden info `shield`, nhóm 2 → đường kẻ trước nó). `install-menu` gộp looks của ảnh/PDF và media bằng `MenuEntryLook.Combine`, từ chối một nhãn có hai kiểu. Icon `music_note_2` vẽ bằng `make-menu-icons.py` từ cùng commit Fluent đã ghim (3 SVG có SHA256); chạy lại cho 18 icon cũ trùng từng byte. Nhãn, thứ tự, op-id không đổi: video 16 mục (15 cho `.mp4 .mov .webm`), audio 6 hoặc 7; đường kẻ không tính vào 16. Test: `MediaMenuTests` (icon ở mục trên cùng, không icon trong submenu, đường kẻ, icon có trong `icons\`), `MenuTests` (Combine); `test-installer.ps1` kiểm icon của menu `.mp4` và `.mp3`.
- [ ] MenuProbe trên Explorer thật cho `.mp4`, `.mkv`, `.mp3` (icon, đường kẻ, 16 mục); rồi `uninstall-menu` (maintainer).


## Review trước khi gộp (2026-10-20): ba review bối cảnh trống, chỉ đọc

Code-reviewer (CR), security-auditor (SEC), doubt-driven (DD) trên `git diff origin/main...HEAD`. Không có Critical/High bảo mật; các mục Required của code và doubt đã sửa, trừ những mục chỉ Windows/CI xác nhận được. Mỗi sửa có đối chứng âm (bỏ sửa thì test đỏ) khi chạy được trên Linux.

| id | mức | việc | quyết định |
|---|---|---|---|
| DD-1, CR-2, SEC-7 | Critical | ADR-0009 bị commit 3e2524f làm rỗng (script mở file để ghi trước khi đọc) | **Sửa**: khôi phục từ af2f710 + ba bổ sung định làm |
| CR-1 | Critical | Notice FFmpeg làm đỏ hai test Core trên Windows (thiếu `ffmpeg\` trong publish giả; `installer/notices/ffmpeg/*` chưa có) | **Sửa**: test chép `deps\ffmpeg\bin\*` vào `publish\ffmpeg\`; notice chỉ liệt kê `LICENSE.txt` của BtbN đến khi giấy phép được commit; `ffmpeg-source.yml` đỏ đến khi có cả file lẫn mẫu `installer/notices/ffmpeg/*` (chặn phát hành, không chặn CI) |
| CR-3 | High | Hai lượt Under N khác chế độ khung (`null` passthrough, MP4 CFR) | **Sửa**: `-fps_mode passthrough` cho mọi lần mã hóa video. Tái hiện trên 6.1: với video VFR, lượt 2 **treo** (0 % CPU) chứ không chỉ vượt cỡ. Test VFR (108 khung) cho Under và Convert; đối chứng âm: bỏ thì cả hai đỏ (Under treo đến hết chờ) |
| SEC-1 | Medium, Required | Đầy đĩa: file chứa nhiều hơn header khai; `-fs` lớn hơn chỗ trống | **Sửa** (bản cuối, sau RC-1): `-fs` không quá chỗ trống − 50 MB; mã hóa video có thêm `-t` = max(thời lượng × 1,5 + 10 s, byte × 8 / 8 kbit/s), chạm `-t` là lỗi. Test FLAC khai 2 s chứa 60 s → WAV đủ 60 s trong giới hạn |
| SEC-2 | Medium, Required | Remove hidden info giữ SEI của H.264 (chữ ký x264, MDPM của máy quay AVCHD có GPS) | **Sửa**: H.264 bỏ SEI (`filter_units=remove_types=6`): hình giải mã y hệt, gói không còn trùng byte (lệch tiêu chí 13, ghi ADR); HEVC giữ SEI (dữ liệu HDR). Test `.m2ts` có SEI x264 → hết; đối chứng âm đỏ |
| DD-9 | Medium | "No hidden info" sai với XMP/hộp riêng của hãng/SEI mà ffprobe không thấy | **Sửa**: MP4/MOV/3GP/AVCHD và mọi H.264 luôn được ghi bản sao, không skip |
| CR-4 | Medium | Remove hidden info bỏ track audio không báo; mất ngôn ngữ | **Sửa**: `FirstAudio` (note) + giữ `language` |
| DD-8 | Medium, Required | `SizeText` làm tròn lên: 24 960 000 byte hiện "25 MB" | **Sửa**: luôn làm tròn xuống; test 24 960 000 → "24.9 MB" |
| DD-10, CR-6 | Medium | Đường chạy lại của Under N chưa từng chạy | **Sửa**: hai test với runner giả làm output chạm giới hạn: 3 lượt 2, bitrate × 0,9 mỗi lần; luôn vượt → "could not make…", không ghi gì; đối chứng âm đỏ |
| CR-11 | Low | Thanh tiến độ lùi từ 100 % về 50 % khi chạy lại | **Sửa**: một `PercentProgress` cho mọi lần |
| CR-8 | Low | Thư mục lượt tạo ngoài `try` | **Sửa** |
| CR-9 | Low | `-fs` của Remove hidden info + 10 MB cố định (TS 188 → 192 byte) | **Sửa**: input × 1,05 + 10 MB |
| CR-5, DD-3 | High | Trần 2 GB và timeout chưa đo trên 1080p | **Đo** (Linux, 4 nhân, 3 s 4K60): SDR 2,2 GB, HLG 2,5 GB, 10,8× thời gian thực. **Sửa**: trần 4 GB (lệch spec 2 GB, ghi ADR); timeout × tốc độ điểm ảnh so với 1080p30, × 2 cho HDR. Còn mở: đo trên máy 8–16 nhân Windows, 8K |
| DD-5 | High | Timeout 6× quá ngắn cho 4K60 HDR | **Sửa** (như trên: 4K60 HDR = 96× thời lượng + 60 s) |
| DD-4 | High | Chỉ thử HDR10 (PQ), không HLG; không kiểm màu | **Sửa một phần**: fixture HLG + test. Còn mở: kiểm màu bằng mắt trên video iPhone thật (Windows Media Player) |
| CR-7, DD-12 | Medium | CI 30 phút có thể hết giờ | **Sửa**: 45 phút; release build 60 phút |
| CR-10, SEC-9 | Low | Job release 10 phút; tar không có hash | **Sửa**: 30 phút; hash tar thêm vào `SHA256SUMS.txt` |
| DD-6 | High (release) | Bộ gom giấy phép chỉ lấy hai tầng đầu (bỏ sót crate vendored, `third_party/`) | **Sửa**: lấy ở mọi tầng trừ `.git` và thư mục test; ADR ghi runtime toolchain. Còn mở: chạy workflow, soát bộ giấy phép |
| DD-7 | High (release) | Archive mã nguồn chưa từng được tạo thật | **Mở**: chạy `ffmpeg-source.yml` trên pull request |
| DD-15 | Low | `test-installer.ps1` viết cứng phiên bản | **Sửa**: đọc `ffmpegSource.ffmpegVersion` từ `deps.json` |
| DD-14, CR-12 | Low | Tài liệu cũ (MKV, luồng default, menu cũ, ưu tiên qua job, `.ps1`/`.zip`, "qua e-mail") | **Sửa** |
| SEC-8 | Info | `^[a-z]{3}$` khớp "eng\n" | **Sửa**: `\z` |
| CR-13 | Nit | so chuỗi message, kiểm tra thừa, "-t 30", 2 GB/GiB, `DriveInfo` với `\\?\` | **Sửa** "-t 30"; còn lại giữ (không đổi hành vi) |
| SEC-3 | Medium, Recommended | Không có whitelist decoder | **Để bản sau**: danh sách phải đủ mọi codec nguồn + phụ đề/ảnh bìa/data, chỉ kiểm được trên bản 8.1 Windows; ghi ADR |
| SEC-4, RC-5 | Low | Video 12 h 8K giả rỗng → timeout nhiều ngày (và sau sửa CR-5: hàng năm) | **Sửa**: hệ số ≤ 200, timeout ≤ 2 ngày; còn lại nhờ Cancel, ưu tiên thấp |
| SEC-5 | Low | 8 audio song song × trần riêng | **Chấp nhận**: audio dùng ít bộ nhớ |
| SEC-6 | Low | `check-location` không kiểm thư mục con `ffmpeg\` có sẵn | **Để bản sau** (installer) |
| DD-17 | Low | Chi tiết lỗi lấy từ 1 MB stderr đầu | **Chấp nhận** |
| DD-2, DD-11, DD-13, DD-16, DD-18 | — | Mọi thứ chỉ Windows: CI, ffmpeg 8.1 qua `ExternalTool`, job/ưu tiên, `flevert.exe`, Cancel video, khởi động lạnh, bộ cài, MenuProbe, icon | **Mở**: chạy CI Windows (pull request) và kiểm tay trên máy Windows |

### Review lại các sửa (một reviewer gộp, bối cảnh trống)

| id | mức | việc | quyết định |
|---|---|---|---|
| RC-1 | High, Required | `-t` (sửa SEC-1) cắt im lặng file thật có thời lượng chỉ ước lượng: AAC thô 300 s → 43 s, MP3 không header VBR, TS ghép bằng `copy /b` | **Sửa**: âm thanh và chép luồng không giới hạn thời gian (`-fs` đủ); mã hóa video `-t` = max(thời lượng × 1,5 + 10 s, byte / 8 kbit/s); chạm `-t` → lỗi "holds far more than its header says", không bao giờ commit file cụt. Test AAC/MP3 ước lượng (300 s đủ) và TS ghép 3 lần (60 s đủ); đối chứng âm: công thức cũ thì cả ba đỏ; test runner giả báo chạm giới hạn → lỗi |
| RC-2 | High, Required | Trần 4 GB vỡ với số luồng tự chọn trên PC 16 luồng (4,4 GB) | **Sửa**: x264 `-threads` ≤ 12 (1,5 × nhân), decoder ≤ 8 (số nhân). Đo (Linux, giả lập 16 luồng, 3 s 4K60 HLG): tự chọn 24/13 → 4,47 GB; 12/8 → 3,16 GB, nhanh hơn (26 s so với 37 s); 8/4 → 2,65 GB. Còn mở: đo trên Windows 8–16 nhân |
| RC-3 | Medium | Bỏ mọi SEI làm mất điểm phục hồi (intra refresh không còn tua được) | **Sửa**: chỉ bỏ SEI của H.264 trong AVCHD (`.mts/.m2ts`, nơi có MDPM); ADR ghi rõ hệ quả |
| RC-4 | Medium (release) | Regex giấy phép sót `LICENSES/…`, `docs/FTL.TXT` của FreeType; bắt nhầm `notice.c` | **Sửa**: thêm thư mục `LICENSES/`, file FreeType, loại đuôi mã nguồn. Soát bộ giấy phép thật vẫn mở (DD-6) |
| RC-6 | Low | Chạm `-fs` vì đĩa đầy bị báo "file may be damaged" | **Sửa**: thông báo nói cả chỗ trống |
| RC-7 | Low | Không có test cho `Slowness`, `MaxTime` | **Sửa**: test thuần |
| RC-8 | Low | `passthrough` với khung sát nhau hơn tốc độ danh nghĩa → cảnh báo DTS | **Chấp nhận**, ghi ADR |
| RC-9 | Nit | < 1 000 byte hiện "0 KB" | **Sửa**: "<1 KB" |
| RC-10 | Nit | Ghi chú notice nói giấy phép thư viện "follow" khi chưa có | **Sửa**: bỏ câu đó đến khi commit giấy phép (RELEASING bước 6 nói thêm lại) |

Kết luận: không còn Critical/High/Required nào mở trừ các mục chỉ Windows/CI xác nhận được (DD-2, DD-11, DD-13, DD-16, DD-18, đo bộ nhớ RC-2 trên máy nhiều nhân, chạy `ffmpeg-source.yml` cho DD-6/DD-7 và RC-4).


### Review lại lần 2 (một reviewer gộp, bối cảnh trống)

| id | mức | việc | quyết định |
|---|---|---|---|
| RC2-1 | Medium, Required | Bỏ SEI theo đuôi file (`.mts/.m2ts`) sót clip AVCHD đã remux sang MP4/MKV, vẫn giữ MDPM (GPS); ADR nói sai "SEI khác chỉ chứa thông số encoder" | **Sửa**: bỏ SEI của H.264 khi là AVCHD hoặc 8 MB đầu file có byte `MDPM` (camcorder ghi MDPM mỗi GOP). Cách theo keyframe (reviewer đề xuất) đã thử và hỏng: intra refresh của x264 vẫn có cờ K trong MP4. Test: clip MP4 có SEI MDPM → mất MDPM, hình giống hệt; luồng intra refresh → giữ SEI, gói video giống hệt. Đối chứng âm: không bao giờ bỏ → test MDPM và `.m2ts` đỏ; luôn bỏ với H.264 → test intra refresh đỏ |
| RC2-2 | Low | VP9 không giới hạn `-threads`, có thể vượt 4 GB trên máy nhiều nhân | **Sửa**: VP9 dùng `EncoderThreads` như x264. Đo trên Windows nhiều nhân vẫn mở (cùng RC-2) |
| RC2-3 | Low | `-fs` của Remove hidden info (input × 1,05 + 10 MB) không kẹp theo chỗ trống, trái ADR | **Sửa**: `MediaJob.WithinFreeSpace` dùng chung với `MaxOutputBytes`; ADR nói rõ áp dụng cả chép luồng |
| RC2-4 | Low | Âm thanh: ffprobe ước lượng thiếu > 2 lần thì `-fs` theo ước lượng có thể chặn file thật | **Sửa**: thời lượng ước lượng = max(ffprobe, byte × 8 / 32 kbit/s) |
| RC2-5 | Low | Doc và ADR nói `-t` giới hạn thống kê x264; với sàn 8 kbit/s, file độc vẫn khai được byte ÷ 1000 s, giới hạn thật là timeout | **Sửa doc**: XML doc `MaxTime` và ADR nêu timeout là giới hạn thật. Kiểm chỗ trống cho thư mục passlog: **chấp nhận** (không phải hồi quy; timeout và Cancel chặn) |
| RC2-6 | Low | Lề 0,5 s khi so `LastTime` với `-t` giả định khung dày; video thưa (≤ 1 fps) dừng sớm hơn lề, file cụt được commit | **Chấp nhận**: chỉ file độc tự cắt chính nó, không ảnh hưởng tài nguyên; fps trong header cũng do file khai nên lề theo fps không chắc hơn |
| RC2-7 | Low | Không test nào kiểm tra thao tác thật sự truyền `boundTime`, `DecoderThreads`, `EncoderThreads`; test TS ghép không khẳng định ffprobe đọc thiếu | **Sửa**: theory ghi lại đối số của từng thao tác (MP4, WEBM, compress, GIF, Remove hidden info, Extract audio); test TS ghép khẳng định ffprobe đọc < 30 s |
| RC2-8 | Nit | (a) assert `TimeoutFor` lặp ở mỗi dòng theory; (b) `FileInfo.Length` đọc cả khi không `boundTime`; (c) regex `LICENSES/` lấy mọi file không phải mã nguồn trong thư mục đó | **Sửa** (a) tách thành test riêng, (b) chỉ đọc khi `boundTime`. (c) **Chấp nhận**: thừa file giấy phép vô hại |

Kết luận lần 2: không còn Critical/High/Required nào mở trừ các mục chỉ Windows/CI xác nhận được (như trên, thêm hành vi của ffmpeg 8.1 với `-threads` trước `-i` và hai `-t` của GIF).

### Review lại lần 3 (một reviewer gộp, bối cảnh trống)

| id | mức | việc | quyết định |
|---|---|---|---|
| RC3-1 | High, Required | Sửa RC2-4 đưa thời lượng sàn (byte ÷ 32 kbit/s) vào cả kiểm chỗ trống: WAV/FLAC 1 GB → MP3 đòi 5,9 GB, Extract audio từ phim 4 GB đòi 23,75 GB → "Not enough free disk space" sai | **Sửa**: `AudioConvert.Sizes` trả về (cần, giới hạn): chỗ trống theo thời lượng ffprobe đọc, sàn chỉ cho `-fs` (đã kẹp theo chỗ trống). Test thuần: WAV 1 GB → MP3 cần 134,6 MB, giới hạn ước tính 5,94 GB (`-fs` = 2×, kẹp theo chỗ trống) |
| RC3-2 | Low | Cửa sổ 8 MB sót MDPM trong MP4 faststart dài vài giờ (moov > 8 MB) hoặc MKV có attachment lớn ở đầu | **Chấp nhận**, ghi ADR: hiếm, đọc xa hơn làm chậm mọi file |
| RC3-3 | Low | Khớp 4 byte `MDPM` trần → ~0,2 % file H.264 > 8 MB khớp nhầm, dashcam intra refresh mất điểm phục hồi | **Sửa**: khớp 20 byte UUID + `MDPM` |
| RC3-4 | Low | `FileShare.Read` lỗi khi tiến trình khác đang mở ghi (đồng bộ đám mây) | **Sửa**: `FileShare.ReadWrite \| FileShare.Delete` |
| RC3-5 | Nit | Test MDPM không chạy qua biên đọc và cuối cửa sổ | **Sửa**: theory `DropsSei` trên file tổng hợp (vắt qua 64 KB, sát và quá 8 MB, 4 chữ trần, HEVC). Đối chứng âm: bỏ phần mang sang → dòng vắt qua 64 KB đỏ |
| RC3-6 | Nit | ADR thiếu sàn âm thanh và `-fs` của Remove hidden info | **Sửa** |

Kết luận lần 3: RC3-1 là mục Required duy nhất, đã sửa; còn lại Low/Nit đã sửa hoặc chấp nhận. Không còn Critical/High/Required nào mở trừ các mục chỉ Windows/CI xác nhận được.

### Review lại lần 4 (một reviewer gộp, bối cảnh trống)

Không còn Critical/High/Required. RC4-1 (không test nào kiểm `RunAsync` truyền đúng `needed`/`bound`), RC4-2 (MP3 nói dưới 16 kbit/s bị ước lượng thiếu chạm `-fs`: báo lỗi, không cắt), RC4-3 (ffprobe ước lượng thừa có thể đòi thừa chỗ trống), RC4-5 ("8 MB" là 8 MiB): Low/Nit, **chấp nhận**. RC4-4 (ghi chú giới hạn RC3-1): **sửa**.

## Review trước phát hành 1.3.0 (2026-10-21): ba review bối cảnh trống, chỉ đọc (code, security, doubt)

Trên `git diff main...feat/v0.3-media` (trừ 1084 file giấy phép và icon). Mã: CR = code-reviewer, SA = security-auditor, DV = doubt-driven.

| id | mức | việc | quyết định |
|---|---|---|---|
| DV-1, CR (blocker) | Required | Giấy phép thư viện ffmpeg chưa commit | **Sửa**: 1084 file từ artifact `ffmpeg-licences` (run 39491031755) vào `installer/notices/ffmpeg/`, `deps.json` liệt kê thư mục; Core/Image xanh với notices thật |
| CR-1, DV-2 | Required | README còn mô tả 1.2: không video/audio, "Coming next 1.3", không nói ffmpeg GPL | **Sửa**: bảng menu Video, dòng Audio, đoạn ffmpeg GPL + link mã nguồn; Coming next = 1.3.1, 1.4 |
| CR-2 | Required | Opus từ âm thanh 5.1(side) (AC-3, E-AC-3, DTS) lỗi "Invalid channel layout": WEBM từ phim MKV, FLAC 5.1 → Opus | **Sửa**: `aformat` về bố cục libopus nhận (`AudioFormat.OpusChannels`, cả WEBM). Tái hiện bằng ffmpeg 8.1 trước khi sửa. Test: FLAC 5.1(side) → Opus 6 kênh, MKV AC-3 5.1(side) → WEBM 6 kênh |
| DV-3, CR-13 | Required | "iPhone HDR videos keep their colours" chưa kiểm trên video thật | **Sửa lời** (CHANGELOG/README: đổi sang màu chuẩn) **và đo**: ảnh SDR đổi đúng sang HLG/PQ (zscale, 203 nit) rồi qua Flevert: Hable cho độ sáng trung bình 103/126 (tối đi ~18%), npl=203 còn tối hơn (87), Mobius 122/126, Reinhard 117, clip 132. **Sửa**: tone map Mobius. Test: HLG và PQ giữ độ sáng trong ±8%. Kiểm bằng mắt trên video iPhone thật vẫn mở (không có mẫu; không dùng file cá nhân của người dùng khi chưa hỏi) |
| DV-5, CR-3 | Required/Should | Video xen kẽ (AVCHD 1080i, DV) không khử xen kẽ | **Sửa**: `bwdif=mode=send_frame:deint=interlaced` đầu chuỗi lọc khi ffprobe báo `field_order` tt/bb/tb/bt (`VideoTrack.IsInterlaced`), cả GIF. Test: MTS 1080i tổng hợp → MP4 progressive |
| CR-4 | Should | Under N tính bitrate lượt 2 theo thời lượng ffprobe (TS ghép đọc 20 s/60 s) → ~2,5× giới hạn, 3 lần mã hóa rồi báo không vừa | **Sửa**: `EncodeAsync` trả thời gian thật lượt 1 đi qua; dài hơn thì tính lại ngân sách, giữ cỡ hình của lượt 1. Test: TS ghép 3 × 20 s → dưới giới hạn, > 55 s |
| CR-5, SA-L4, DV-12 | Should/Low | Spec ghi trần 2 GB, timeout ×6, output ≤ 4 × input; code khác | **Sửa spec** theo code và ADR-0009. Trần riêng thấp hơn cho ffprobe/âm thanh: **chấp nhận** (ffprobe giải mã vài khung 8K HEVC 10-bit nhiều luồng có thể cần > 1 GB; trần là commit tối đa, không phải dùng thật) |
| CR-6 | Should | `creation_time` của Remove hidden info không giới hạn độ dài (40 KB → dòng lệnh quá dài) | **Sửa**: chỉ giữ khi là ngày hợp lệ ≤ 64 ký tự (`VideoStripMetadata.IsDate`), test |
| CR-7, DV-6 | Should | GIF tone map trước khi bỏ khung và thu nhỏ | **Sửa**: fps → scale → tone map |
| DV-7 | Should | ffprobe timeout 10 s làm hỏng file OneDrive chỉ-trên-mạng/ổ mạng chậm, báo "damaged" | **Sửa**: 1 phút (như qpdf) và câu riêng `ReadTooSlowMessage` gợi ý chép về máy |
| DV-8 | Should | `DriveInfo` không đọc được UNC/`\?\` → không kiểm chỗ trống, đĩa đầy báo "damaged" | **Sửa**: `GetDiskFreeSpaceEx`; ffmpeg báo "No space left on device" → `DiskFullMessage`. Test bằng RunTool giả |
| DV-9 | Should | `ffmpeg-source.yml` không bắt thư viện trong `licences-missing.txt` thiếu bản điều khoản viết tay | **Sửa**: mỗi dòng phải có `installer/notices/ffmpeg/<stage>-terms*.txt` |
| DV-10 | Should | Notice ffmpeg không nói mã nguồn ở đâu (GPLv3 §6(d)) | **Sửa**: note trong `deps.json` có link Releases |
| SA-M1 | Medium | `release.yml` tải artifact `ffmpeg-source` (do script bên thứ ba tạo) vào cùng thư mục setup, rồi đăng `installer/*` | **Sửa**: artifact nguồn vào `source/`, chỉ nhận tên `ffmpeg-source-…-N-of-M.tar` là file thường; job build xuất SHA256 của setup, job release so lại; đăng danh sách file tường minh |
| SA-L1 | Low | `fetch-ffmpeg-source.sh`: tên member tar thành tùy chọn; `cp -L` theo symlink ra ngoài cache | **Sửa**: `--verbatim-files-from -T`; link chỉ được trỏ vào file trong cache; file giấy phép giải nén phải là file thường |
| SA-M2 | Medium | Mọi decoder của ffmpeg mở được qua container hợp lệ, không sandbox quyền | **Mở cho bản sau** (ghi ADR-0009): `-codec_whitelist` cần đo trên file thật (tên decoder như `libdav1d`, `mp3float`, luồng phụ đề/dữ liệu), token quyền thấp |
| SA-L2 | Low | Thống kê x264 lượt 1 không giới hạn cỡ với file độc nhiều khung tí hon | **Mở cho bản sau**: `-frames:v` theo thời lượng × fps; timeout và Cancel chặn |
| SA-L3 | Low | File độc giữ ffmpeg chạy tới 2 ngày | **Chấp nhận**: ưu tiên thấp, Cancel dừng ngay |
| DV-4 | High | Trần 4 GB và tốc độ đo trên Linux ffmpeg 6.1; 8K chưa đo | **Đo và sửa** (Windows, ffmpeg 8.1, 16 nhân, qua `flevert.exe`): 8K HLG → MP4 và Compress **vượt trần 4 GB sau 1,7 s** (xác nhận); 4K60 HLG đỉnh 3,4 GB, đạt. Ra 4K mà giữ luồng vẫn 4,1–4,6 GB; 2 luồng giải mã + 6 x264 → 3,3 GB. Sửa: `OutputSize` trần cạnh ngắn 2160, `DecoderThreadsFor`/`EncoderThreadsFor` cho ảnh > 4K. Đo lại: 8K → MP4 7,4 s đỉnh 3,3 GB, Compress 6,6 s 3,3 GB, GIF 1 GB. Test: kích thước 8K → 4K, số luồng |
| DV-11 | Should | Luồng âm thanh đầu có thể là codec ffmpeg không giải mã được (APAC) | **Chấp nhận**: chưa có mẫu; lỗi rõ, không hỏng gì |
| DV-13 | Nit | Spec nói Under 25 MB gửi Discord (miễn phí 10 MB) | **Sửa spec** |
| DV-14 | Nit | libdvdcss trong bản dựng | **Chấp nhận**, ghi ADR-0009 (không demuxer DVD nào được phép) |
| CR-8 | Nit | GIF bỏ qua tỉ lệ điểm ảnh (HDV 1440×1080) | **Mở cho bản sau** |
| CR-9, CR-10 | Nit | Thanh tiến độ khi thử lại; so khớp chuỗi thông báo để thử lại | **Chấp nhận** |
| CR-11 | Nit | `DeleteStaleRuns` dừng ở thư mục đầu không xóa được | **Sửa**: bắt lỗi từng thư mục |
| CR-12 | Nit | Tham số `extra` của `Filters` không dùng | **Sửa**: bỏ |

### Soát lại các bản sửa (một reviewer gộp, bối cảnh trống)

| id | mức | việc | quyết định |
|---|---|---|---|
| RR-1 | High | Kiểm "file giấy phép phải là file thường" mới sẽ dừng job `ffmpeg-source` (và cả release) nếu thư viện để giấy phép là symlink trong archive (lilv `COPYING` → `LICENSES/ISC.txt`, re2) | **Sửa**: theo link nhưng chỉ tới file thường nằm trong chính thư mục giải nén của thư viện đó (`realpath`); kết quả như `cp` trước đây |
| RR-2 | Should | Under N: lượt 2 vẫn dùng thời lượng ffprobe cho timeout và thanh tiến độ | **Sửa**: lượt 2 dùng thời lượng lượt 1 đo được |
| RR-3 | Should | `GetDiskFreeSpaceEx` cần `\server\share\` có dấu phân cách cuối | **Sửa**: thêm dấu phân cách. Chưa có test UNC (`\localhost\C$` cần quyền admin) |
| RR-4 | Should | Mobius (điểm uốn mặc định) làm phẳng vùng sáng của cảnh ngoài trời HLG 600–1000 nit | **Chấp nhận**, cùng mục mở "kiểm bằng mắt trên video iPhone thật" |
| RR-5 | Should | Không test nào kiểm `-threads 6` đứng sau `-threads` của codec trên dòng lệnh thật | **Sửa**: theory MP4/WEBM/Balanced với ffprobe giả báo 8K: `-threads 2` trước `-i`, `-threads` cuối là 6, `scale=3840:2160` |
| RR-6 | Nit | README "8K ra 4K": mọi video lớn hơn 4K (5K, 6K) đều thu về | **Sửa lời** |
| RR-7 | Nit | Video xoay mà xen kẽ: transpose chạy trước bwdif | **Sửa**: không khử xen kẽ khi `Rotation != 0` |
| RR-8 | Nit | `IsDate` bỏ kiểm `\0` | **Sửa** |
| RR-9 | Nit | Không test ffprobe quá giờ → `ReadTooSlowMessage` | **Sửa**: test với RunTool giả ném `TimeoutException` |

Kiểm thật trên Windows: `test-installer.ps1` kịch bản 6 đỏ hai lần liền ("flevert.exe was still running when the uninstaller finished"): `dev.slow-copy` chạy 8 file một lúc nên 20 file chỉ 3,4 s (đo tay), còn trình gỡ cài chậm hơn khi Defender đang quét ffmpeg vừa cài. **Sửa test**: `FLEVERT_SLOW_COPY_MS=7000` (ba lượt, ~21 s), đúng như chú thích muốn.

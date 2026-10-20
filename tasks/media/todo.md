# Todo: media (v0.3.0)

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
- Đã làm: 23 test (`AudioConvertTests`): mỗi đích từ WAV (codec, thời lượng ±0,1 s, title), mọi đuôi nguồn đọc được qua whitelist demuxer (mp3, aac ADTS, m4a, flac, ogg, opus, wma/asf, aiff, aif), AAC → M4A và Extract M4A không mã hóa lại (MD5 của âm thanh giải mã trùng; MD5 gói khác nhau giữa ADTS và MP4 nên không dùng được), 5.1 → MP3 stereo, MKV hai audio lấy luồng default. Phát hiện: Ogg/Opus giữ tag trên luồng chứ không trên file, nên `AudioTrack.Tags` được đọc và dùng khi file không có tag đó (vẫn qua danh sách cho phép: GPS trên luồng cũng bị bỏ, có test). WAV > 4 GB: `-rf64 auto`.

## T4: video.to-mp4/mov/mkv/webm
- [x] H.264 CRF 20 `medium` `yuv420p` + AAC 192k, `+faststart`; xoay; HDR → SDR; bỏ GPS; MOV/MKV như MP4; WEBM VP9 + Opus
- [x] Ước lượng output so với đĩa trống; `-fs` ≤ 4 × input + 100 MB
- Đã làm: `VideoConvert` + `VideoEncoding` (kích thước theo hình hiển thị, cạnh chẵn làm tròn xuống để không phóng to; lọc `scale` chỉ khi đổi cỡ; tone map chỉ khi HDR; `format=yuv420p` cuối). 19 test; đối chứng âm: bỏ tone map thì test HDR đỏ (x264 chép nguyên nhãn PQ/BT.2020). Video điện thoại: ra 360 × 640 thật trong điểm ảnh, không còn display matrix, không còn tag `location*` (cả file và luồng), giữ `creation_time`. Convert giữ mọi luồng audio cùng ngôn ngữ. Đo 2026-10-20 (4 nhân, 1080p 10 s): x264 `medium` 8,6 s; VP9 `-cpu-used 2` 25 s, `4` 18 s (file lớn hơn 3 %) → chọn 4, và timeout của WEBM gấp đôi (thời lượng × 12 + 60 s) vì VP9 chậm gấp ~2 lần x264. Ước lượng cỡ: 0,15 bit/điểm ảnh/khung + audio.

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
- Đã làm: `MediaMenuTests` (17 test) cài `MediaOperations.All` vào registry giả: video `Convert ▸ MP4 · MOV · MKV · WEBM · GIF`, `Compress ▸ Light · Balanced · Strong · Under 10/25/100 MB`, `Extract audio ▸ MP3 · M4A` = 16 (15 cho `.mp4 .mov .mkv .webm`); audio `Convert ▸` 5 hoặc 6 đích; lệnh registry `run audio.to-<đích>`. Thứ tự menu đến từ thứ tự trong `MediaOperations.Create`.

## T8: đóng gói
- [ ] `build-installer.ps1`/Inno: `ffmpeg\` trong thư mục cài; `THIRD-PARTY-NOTICES.txt`; `test-installer.ps1` kiểm `ffmpeg\`; cỡ bộ cài ≤ 150 MB (đo)
- [ ] `scripts/fetch-ffmpeg-source.ps1` → asset `ffmpeg-source-<tag>.zip`; `release.yml` đính kèm; ADR-0009
- [ ] CI, release, setup-dev: bước `dotnet test tests/Flevert.Media.Tests`; `ffmpeg -version` khớp bản ghim; RELEASING: lịch kiểm bản mới hằng tháng

## T9: file độc
- [ ] HLS, concat, subfile, 0 byte, hỏng, thời lượng giả 99 giờ: ≤ 10 s hoặc lỗi rõ, không kết nối mạng, không còn `.partial`

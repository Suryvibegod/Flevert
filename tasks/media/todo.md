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
- [ ] H.264 CRF 20 `medium` `yuv420p` + AAC 192k, `+faststart`; xoay; HDR → SDR; bỏ GPS; MOV/MKV như MP4; WEBM VP9 + Opus
- [ ] Ước lượng output so với đĩa trống; `-fs` ≤ 4 × input + 100 MB

## T5: compress
- [ ] Light/Balanced/Strong (CRF 23/28/32, Strong ≤ 720p, AAC 128/96/64k); không nhỏ hơn gốc → skipped
- [ ] Under 10/25/100 MB: bitrate, chọn độ phân giải, 2 lượt (passlog trong `%LOCALAPPDATA%\Flevert\media`), chạy lại −10 % tối đa 2 lần, "Too long to fit under 10 MB"; gốc đã dưới N MB → skipped

## T6: video.to-gif
- [ ] ≤ 480 px cạnh dài, 12 fps, palette; > 30 s → 30 s đầu + note

## T7: menu
- [ ] Video 16 mục (15 khi đích trùng nguồn), audio ≤ 7; test ≤ 16 cho mọi đuôi; `.gif` vẫn là ảnh
- [ ] MenuProbe trên Explorer thật (cần kiểm trên Windows)

## T8: đóng gói
- [ ] `build-installer.ps1`/Inno: `ffmpeg\` trong thư mục cài; `THIRD-PARTY-NOTICES.txt`; `test-installer.ps1` kiểm `ffmpeg\`; cỡ bộ cài ≤ 150 MB (đo)
- [ ] `scripts/fetch-ffmpeg-source.ps1` → asset `ffmpeg-source-<tag>.zip`; `release.yml` đính kèm; ADR-0009
- [ ] CI, release, setup-dev: bước `dotnet test tests/Flevert.Media.Tests`; `ffmpeg -version` khớp bản ghim; RELEASING: lịch kiểm bản mới hằng tháng

## T9: file độc
- [ ] HLS, concat, subfile, 0 byte, hỏng, thời lượng giả 99 giờ: ≤ 10 s hoặc lỗi rõ, không kết nối mạng, không còn `.partial`

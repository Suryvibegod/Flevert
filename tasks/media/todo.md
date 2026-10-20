# Todo: media (v0.3.0)

Plan: [plan.md](plan.md). Spec: [docs/specs/media.md](../../docs/specs/media.md).

Mỗi task: test viết trước và thấy đỏ; build, `dotnet format --verify-no-changes`, các project test xanh (chạy lần lượt); ghi chú ngắn ở đây; commit nguyên tử. Review ba góc làm một lần trước khi phát hành (CLAUDE.md). Mục "cần kiểm trên Windows": viết trong phiên Linux, chưa chạy được ở đây.

Lệnh: `powershell -File scripts/fetch-deps.ps1`, `dotnet build Flevert.sln`, `dotnet test tests/Flevert.Media.Tests` (cùng ba project test cũ).

## T1: deps ffmpeg, project, Ffprobe
- [ ] `scripts/deps.json`: ffmpeg `autobuild-2026-09-30-13-08` `win64-gpl-shared-8.1` + SHA256, giữ exe/DLL/LICENSE (plan, "Bản dựng ffmpeg"); notices tạm cho `write-notices.ps1`
- [ ] `src/Flevert.Media` (chép `deps/ffmpeg/bin/*` vào `ffmpeg\`), `tests/Flevert.Media.Tests` (sln; CI/release/setup-dev ở T8)
- [ ] `Ffprobe`: JSON, timeout 10 s, whitelist giao thức + demuxer; `MediaInfo` (thời lượng, luồng video/audio, xoay, HDR); từ chối: không có luồng, thời lượng 0 hoặc > 12 h, > 8K, không đọc được
- Kiểm: unit đọc JSON (xoay ở `side_data_list` và `tags.rotate`, HDR, ảnh bìa); tích hợp với fixture sinh bằng ffmpeg

## T2: lát dọc `audio.to-mp3`
- [ ] `Ffmpeg.RunAsync`: `-nostdin -hide_banner -nostats -n -progress pipe:1`, input `file:`, output `file:` + `-f`; tiến độ `out_time_us`; timeout thời lượng × 6 + 60 s; lỗi → `MediaException` tiếng Anh ngắn, chi tiết stderr vào log
- [ ] Core: `FileProgress.Percent`, `ExternalToolLimits.BelowNormalPriority` (cần kiểm trên Windows)
- [ ] `audio.to-mp3` (MP3 V2), metadata theo danh sách cho phép; đăng ký `MediaOperations.All` trong `Program.cs`
- Kiểm: thời lượng ±0,1 s, codec mp3; Cancel giữa chừng → không còn `.partial`, ffmpeg dừng ≤ 2 s; tiến độ tăng dần tới 100

## T3: audio còn lại + extract
- [ ] `audio.to-m4a/-wav/-flac/-ogg/-opus`, không đề xuất đích trùng nguồn; M4A từ AAC chép luồng
- [ ] `video.extract-mp3/-m4a` (M4A chép luồng AAC, hash luồng trùng)

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

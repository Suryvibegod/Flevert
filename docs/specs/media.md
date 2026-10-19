# Spec: media

Module `media` trong [docs/specs/capability-map.md](capability-map.md) (v0.3). Phụ thuộc `core`. Bản nháp: chốt sau khi `pdf` (v0.2) xong.

## Objective

Chuột phải video / audio → `Flevert` → đổi định dạng, nén cho vừa giới hạn gửi file, tách nhạc.

- Video quay điện thoại quá nặng để gửi Messenger / Gmail (25 MB), Discord (10 MB) → `Compress > Under 25 MB`.
- MOV từ iPhone không chạy trên máy cũ → `Convert > MP4`.
- Lấy nhạc từ video → `Extract audio (MP3)`.
- Đổi định dạng audio (FLAC → MP3, M4A → MP3, WAV → FLAC ...).

## Tech Stack

| Thành phần | Lựa chọn | Lý do |
|---|---|---|
| Engine | `ffmpeg.exe` + `ffprobe.exe` 8.1, bản GPL của BtbN/FFmpeg-Builds, **tag autobuild cố định + SHA256** | Có libx264 (chất lượng/dung lượng tốt nhất cho H.264), libvpx, libopus, libmp3lame. Chạy như **tiến trình riêng**: code của ta vẫn MIT (aggregation); kèm giấy phép GPL + link mã nguồn trong `THIRD_PARTY_NOTICES.md` |
| Tải về | `scripts/fetch-deps.ps1` tải vào `deps/` (gitignored), kiểm SHA256 | Không commit nhị phân vào git |

Bản LGPL bị loại: không có libx264; encoder `h264_mf` của Media Foundation không có CRF, chất lượng kém hơn rõ ở cùng dung lượng.

## Thao tác

Nguồn video: `.mp4 .mov .mkv .webm .avi .wmv .m4v`. Nguồn audio: `.mp3 .wav .m4a .flac .ogg .opus .aac .wma .aiff`.

| op-id | Menu | Output |
|---|---|---|
| `video.to-mp4` / `-mov` / `-mkv` | Convert/MP4, MOV, MKV | H.264 CRF 20 + AAC 192k (`+faststart` cho MP4/MOV) |
| `video.to-webm` | Convert/WEBM | VP9 CRF 32 + Opus 128k |
| `video.to-avi` / `-wmv` | Convert/AVI, WMV | MPEG-4 / WMV2 + MP3 / WMA (tương thích máy cũ) |
| `video.to-gif` | Convert/GIF | 480 px, 12 fps, palette tối ưu |
| `video.compress-light` / `-balanced` / `-strong` | Compress/Light, Balanced, Strong | H.264 CRF 23 / 28 / 32; Strong thu về tối đa 720p; AAC 128k / 96k / 64k |
| `video.compress-under-10mb` / `-25mb` / `-100mb` | Compress/Under 10 MB, 25 MB, 100 MB | 2-pass theo bitrate = (N × 8 / thời lượng) − audio, thu độ phân giải nếu bitrate quá thấp |
| `video.extract-mp3` | Extract audio (MP3) | MP3 192k |
| `audio.to-mp3` / `-m4a` / `-wav` / `-flac` / `-ogg` / `-opus` | Convert/... | MP3 V2, AAC 192k, PCM, FLAC, Vorbis q5, Opus 128k |

## Bảo mật (theo security audit của core)

- Gọi `ffmpeg.exe` bằng **đường dẫn tuyệt đối** cạnh `flevert.exe`; `ArgumentList`, không ghép chuỗi.
- Input truyền dạng `file:<đường dẫn>` và `-protocol_whitelist file` → file "video" thực chất là playlist HLS / concat không thể kéo URL hay đọc file khác.
- Output chỉ ghi vào file `.partial` do `OutputPath` cấp; luôn `-n` (không ghi đè).
- `-nostdin -hide_banner`; timeout cứng theo thời lượng × 4 + 60 s; Cancel kill cả cây tiến trình.

## Tiến độ

Video dài vài phút → cửa sổ tiến độ cần % trong từng file: đọc `-progress pipe:1` (`out_time_us`) so với thời lượng từ ffprobe. Cần mở rộng `IOperation.RunAsync` nhận `IProgress<double>` (thay đổi API core, làm trong task đầu của module).

## Success Criteria (nháp)

1. MOV iPhone → MP4 chạy được trong Movies & TV.
2. Video 60 s 1080p → Under 25 MB cho file < 25 000 000 byte.
3. Extract MP3 ra MP3 hợp lệ đúng thời lượng (±0,1 s).
4. Mọi chuyển đổi audio trong bảng đọc lại đúng codec bằng ffprobe.
5. File `.mp4` thực chất là playlist HLS trỏ URL bị từ chối, không có kết nối mạng.
6. Cancel giữa chừng kill ffmpeg, không để lại `.partial`.
7. Cửa sổ tiến độ hiện % trong file.

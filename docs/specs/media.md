# Spec: media (v0.3)

Module `media` trong [capability-map.md](capability-map.md). Phụ thuộc `core` (dùng lại `ExternalTool`, tiến độ trong file và output nhiều file làm ở v0.2). Phiên bản: **v0.3.0**. Trạng thái: **đã chốt phạm vi** (2026-10-20); bản dựng ffmpeg và preset được đo lại ở bước Plan.

## Objective

Video quay điện thoại quá nặng để gửi, định dạng không mở được trên máy khác, cần lấy nhạc từ video: hôm nay người dùng tải "video converter" đầy quảng cáo hoặc upload lên web. Flevert v0.3 làm các việc phổ biến nhất **bằng chuột phải, trên máy, không upload**, với preset dễ hiểu thay cho thông số kỹ thuật.

### User stories (ưu tiên từ trên xuống)

1. Video 2 phút quay iPhone (MOV, HEVC, 400 MB) → **Compress > Under 25 MB** → MP4 dưới 25 MB, gửi được qua Gmail/Messenger, xem được trên mọi máy.
2. MOV/MKV/WEBM không mở được → **Convert > MP4** (H.264 + AAC, chạy mọi nơi).
3. Lấy nhạc/tiếng từ video → **Extract audio > MP3**.
4. Đổi định dạng audio: FLAC/WAV/M4A/OGG/OPUS/WMA → MP3, M4A, FLAC, WAV…
5. Video ngắn → **Convert > GIF** (meme, hướng dẫn).
6. Chọn 20 video → một lô, từng file một (mã hóa video đã dùng hết CPU), cửa sổ tiến độ hiện % của file đang chạy và số file.

Ngoài phạm vi v0.3: cắt/ghép đoạn, ghép nhiều video, phụ đề, xoay video, tải video từ mạng, DRM, mã hóa phần cứng tùy chọn (xem Open Questions), H.265/AV1 làm output.

### Thao tác

Nguồn video: `.mp4 .mov .m4v .mkv .webm .avi .wmv .3gp .mts .m2ts`. Nguồn audio: `.mp3 .wav .m4a .aac .flac .ogg .opus .wma .aiff .aif`. Output cạnh file gốc, không ghi đè, file gốc không đổi.

| op-id | Menu | Output (mặc định) |
|---|---|---|
| `video.to-mp4` | Convert > MP4 | H.264 (CRF 20, preset `medium`, `yuv420p`) + AAC 192k, `+faststart`; giữ độ phân giải và fps; xoay theo metadata |
| `video.to-mov` / `video.to-mkv` | Convert > MOV / MKV | như MP4 |
| `video.to-webm` | Convert > WEBM | VP9 (CRF 32, `-b:v 0`, `-row-mt 1`) + Opus 128k |
| `video.to-gif` | Convert > GIF | tối đa 480 px cạnh dài, 12 fps, palette 2 lượt (`palettegen`/`paletteuse`); video > 30 s: chỉ 30 s đầu, thông báo nói rõ |
| `video.compress-light` / `-balanced` / `-strong` | Compress > Light / Balanced / Strong | MP4 H.264 CRF 23 / 28 / 32; Strong thu về tối đa 720p; AAC 128k / 96k / 64k; không nhỏ hơn bản gốc thì bỏ qua (skipped) |
| `video.compress-under-10mb` / `-25mb` / `-100mb` | Compress > Under 10 MB / 25 MB / 100 MB | MP4 2 lượt theo bitrate đích = (giới hạn × 8 × 0,97 / thời lượng) − audio; bitrate video < ngưỡng theo độ phân giải thì thu nhỏ (1080p → 720p → 480p); không thể (video quá dài) thì lỗi rõ "Too long to fit under 10 MB" |
| `video.extract-mp3` / `video.extract-m4a` | Extract audio > MP3 / M4A | MP3 VBR V2 / AAC 192k (M4A giữ nguyên luồng AAC nếu nguồn đã là AAC: không mã hóa lại) |
| `audio.to-mp3` / `-m4a` / `-wav` / `-flac` / `-ogg` / `-opus` | Convert > … | MP3 V2; AAC 192k; PCM 16-bit; FLAC mức 5; Vorbis q5; Opus 128k. Không đề xuất đích trùng nguồn |

Quy tắc chung:

- 1 MB = 1 000 000 byte (như module ảnh). Kết quả "Under N MB" **phải** < N × 1 000 000 byte; lượt 2 vượt thì chạy lại với bitrate thấp hơn 10% (tối đa 2 lần), rồi mới báo lỗi.
- Giữ metadata cơ bản (ngày quay), **bỏ GPS** (`-map_metadata` có lọc; iPhone ghi vị trí vào `com.apple.quicktime.location.ISO6709`).
- HDR (HLG/PQ từ iPhone) → SDR bằng tone-map khi ra H.264 8-bit, để màu không bị nhạt.
- Tên output: `clip.mp4`, `clip (compressed).mp4`, `clip (under 25 MB).mp4`, `clip.mp3`.

## Engine: ffmpeg

| | Lựa chọn | Ghi chú |
|---|---|---|
| Nhị phân | `ffmpeg.exe` + `ffprobe.exe` bản dựng Windows của **BtbN/FFmpeg-Builds**, nhánh phát hành 8.x, **tag autobuild có ngày** (không dùng tag `latest`: file đổi mỗi ngày) + SHA256, tải bằng `scripts/fetch-deps.ps1` | Đã kiểm 2026-10-20: có bản `win64-gpl` và `win64-lgpl` cho n8.1 và n9.0 |
| Bản GPL hay LGPL | **Mặc định GPL** (có libx264: chất lượng/dung lượng H.264 tốt nhất, CRF). LGPL không có x264; `h264_mf` (Media Foundation) không có CRF và kém rõ ở cùng dung lượng | Chạy như **tiến trình riêng**: Flevert vẫn MIT (aggregation). Nghĩa vụ GPL khi phân phối: kèm văn bản GPL và **mã nguồn tương ứng** của đúng bản dựng (ffmpeg + x264 + các thư viện GPL khác + script build của BtbN) như asset của release, hoặc lời đề nghị cung cấp bằng văn bản trong 3 năm. Quyết ở bước Plan (Open Question 1) |
| Gọi | Đường dẫn tuyệt đối trong thư mục cài; `ArgumentList`; `-nostdin -hide_banner -nostats -n`; input `file:<path>` với `-protocol_whitelist file`; `-progress pipe:1` | Video "độc" (playlist HLS, `concat`, `subfile`, URL) không được kéo mạng hay đọc file khác |
| Song song | `MaxParallel = 1` cho video (x264 tự dùng mọi nhân); audio song song theo số nhân | |

## Thay đổi ở core

Phần lớn đã làm ở v0.2 (`ExternalTool`, `IProgress<double>` trong file, output thư mục). Thêm:

1. **Thời lượng và tiến độ**: `ffprobe` (JSON, timeout 10 s) lấy thời lượng, luồng, xoay, HDR; tiến độ = `out_time_us` / thời lượng; 2 lượt tính 0–50% và 50–100%.
2. **Timeout theo nội dung**: thời lượng × 6 + 60 s cho mỗi lượt (máy yếu, 1080p, preset `medium` khoảng 1–2× thời gian thực); vượt thì giết cả cây tiến trình, báo lỗi.
3. **Không gian đĩa**: trước khi chạy, ước lượng output (bitrate × thời lượng) so với dung lượng trống; thiếu thì lỗi rõ.
4. **Ưu tiên tiến trình**: ffmpeg chạy `BelowNormal` (video chạy lâu, không làm máy giật).
5. Menu: các đuôi video/audio thêm vào; ảnh không đổi.

## Bảo mật

- Input video/audio là không tin cậy: ffmpeg chạy với whitelist giao thức `file`, không `concat`/`subfile`/`hls` (`-f` không cho người dùng chọn), giới hạn thời gian, giới hạn kích thước output (≤ 4 × input + 100 MB), ưu tiên thấp.
- `ffprobe` trước để từ chối sớm: không có luồng video/audio, thời lượng 0 hoặc > 12 giờ, độ phân giải > 8K.
- Bản dựng ghim hash; CI kiểm `ffmpeg -version` khớp bản mong đợi; Dependabot không theo dõi được nên lịch kiểm bản mới hằng tháng ghi trong RELEASING.

## Commands

```
powershell -File scripts/fetch-deps.ps1     # thêm ffmpeg, ffprobe (ghim tag + SHA256)
dotnet test tests/Flevert.Media.Tests
```

(và các lệnh build/test/installer như v0.2)

## Project Structure

```
src/Flevert.Media/            → thao tác video/audio, Ffmpeg/Ffprobe (ExternalTool), preset
tests/Flevert.Media.Tests/    → test; fixtures/ sinh bằng ffmpeg (lavfi testsrc/sine), có README nguồn gốc
docs/adr/0009-…                → bản dựng ffmpeg (GPL hay LGPL) và nghĩa vụ giấy phép
```

## Testing Strategy

- **Unit**: tính bitrate cho "Under N MB" (thời lượng rất ngắn/dài, audio chiếm hết), chọn độ phân giải, dựng dòng lệnh (không có đường dẫn ghép chuỗi), đọc `-progress`.
- **Tích hợp với ffmpeg thật** trên fixture sinh sẵn: video 10 s 1080p (testsrc + sine), MOV xoay 90°, HEVC HDR mẫu (tự sinh bằng x265 nếu bản dựng có), MKV nhiều luồng audio, file `.mp4` thực chất là playlist HLS trỏ `http://127.0.0.1:<cổng test>` (phải không có kết nối nào tới cổng), file hỏng, file 0 byte.
- **Kiểm kết quả bằng ffprobe**: codec, độ phân giải, thời lượng ±0,1 s, kích thước < giới hạn, không còn GPS, xoay đúng.
- **Tiến trình thật + Cancel**: Cancel ở 50% → ffmpeg chết trong 2 s, không còn `.partial`.
- **Runtime thủ công**: bấm thật trên video iPhone thật **của người dùng chỉ khi họ cho phép**; mặc định dùng fixture tổng hợp.

## Boundaries

- **Always:** tiến trình riêng, whitelist giao thức, timeout, giết cả cây khi Cancel; kết quả "Under N MB" thật sự dưới N; ghi qua `.partial`.
- **Ask first:** chọn GPL hay LGPL (Open Question 1) nếu nghĩa vụ mã nguồn không làm tự động được; tăng bộ cài quá 150 MB; thêm mã hóa phần cứng.
- **Never:** liên kết thư viện GPL vào tiến trình Flevert; tải ffmpeg lúc chạy; gửi file ra mạng; cho người dùng truyền tham số ffmpeg tự do.

## Success Criteria

1. MOV iPhone (HEVC, có thể HDR) → MP4 H.264 mở được trong ứng dụng Media Player của Windows, đúng chiều, màu không nhạt.
2. Video 60 s 1080p → Under 25 MB < 25 000 000 byte; Under 10 MB với video 20 phút → lỗi rõ, không tạo file.
3. Extract MP3: thời lượng ±0,1 s so với nguồn; M4A từ nguồn AAC không mã hóa lại (hash luồng audio trùng).
4. Mọi chuyển đổi audio trong bảng: ffprobe đọc đúng codec, thời lượng ±0,1 s.
5. File `.mp4` là playlist HLS trỏ URL: bị từ chối, không có kết nối mạng nào.
6. Cancel giữa chừng: ffmpeg dừng ≤ 2 s, không còn `.partial`.
7. Cửa sổ tiến độ hiện % trong file và "2 of 20".
8. Bộ cài có `ffmpeg.exe`, `ffprobe.exe`, giấy phép và (nếu GPL) mã nguồn tương ứng hoặc lời đề nghị; nâng cấp từ 0.2 giữ cài đặt; gỡ sạch.
9. Ba review bối cảnh trống không còn Critical/High/Required.

## Open Questions

1. **GPL hay LGPL**: GPL cho chất lượng tốt nhất nhưng mỗi release phải kèm mã nguồn tương ứng của ffmpeg và mọi thư viện GPL trong bản dựng. Bước Plan: thử tự động hóa (tải source theo commit trong bản dựng BtbN, đóng gói thành asset release); nếu không đáng tin cậy thì dùng LGPL và đo `h264_mf`/`libopenh264` so với x264 trên cùng fixture (mục tiêu: Under 25 MB không xấu rõ rệt).
2. Kích thước: bản GPL static khoảng 80–130 MB giải nén; có cần tải ffmpeg lúc cài (bộ cài nhỏ) thay vì đóng gói? Mặc định: đóng gói (offline, không tải gì lúc chạy hay lúc cài).
3. Mã hóa phần cứng (NVENC/QSV/AMF) cho máy yếu: để sau v0.3 trừ khi đo thấy preset `medium` quá chậm (> 3× thời gian thực ở 1080p trên máy 4 nhân).

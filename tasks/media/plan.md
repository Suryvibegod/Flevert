# Plan: media (v0.3.0)

Spec: [docs/specs/media.md](../../docs/specs/media.md). Todo: [todo.md](todo.md). Lập ngày 2026-10-20 theo `planning-and-task-breakdown`, nhánh `feat/v0.3-media`.

## Tổng quan

v0.3 thêm video/audio vào menu chuột phải, chạy bằng ffmpeg/ffprobe (bản GPL của BtbN) như tiến trình riêng qua `ExternalTool`. Làm theo lát dọc như v0.2: lát đầu `audio.to-mp3` đi trọn đường (probe, chạy, tiến độ, Cancel, `.partial`, đăng ký trong `flevert.exe`), các thao tác sau chỉ thêm tham số và test.

## Bản dựng ffmpeg (đã kiểm 2026-10-20)

- **Release `autobuild-2026-09-30-13-08`** của BtbN/FFmpeg-Builds: bản cuối tháng, được giữ **2 năm** (README của BtbN, "Release Retention Policy"; bản hằng ngày chỉ giữ 14 ngày nên không ghim được). Asset `ffmpeg-n8.1.3-9-g29e619e767-win64-gpl-shared-8.1.zip`, SHA256 `dfe81c3aa0a546ee81980b1b824486dd0fe0a1d452f00cb55135379a74be7bcf` (khớp `checksums.sha256` của release, tải lại và tính lại ra cùng giá trị), 90 MB nén.
- Giữ: `bin/ffmpeg.exe`, `bin/ffprobe.exe`, 7 DLL (`avcodec-62`, `avdevice-62`, `avfilter-11`, `avformat-62`, `avutil-60`, `swresample-6`, `swscale-9`), `LICENSE.txt` (GPLv3: bản GPL của BtbN bật `--enable-version3`). Không giữ `ffplay.exe`, `include/`, `lib/`, `doc/`. Cỡ giải nén ≈ 199 MB (avcodec 119 MB); cỡ bộ cài đo ở T8.
- Bản dựng có rất nhiều thư viện mạng và script (libcurl, openssl, libssh, srt, rist, zmq, avisynth, vapoursynth). Vì vậy input chỉ mở qua **whitelist giao thức `file`** và **whitelist demuxer** (`-format_whitelist`, xem dưới), không chỉ cấm vài demuxer.

## Quyết định kiến trúc

- **Project `Flevert.Media`** chỉ tham chiếu `Flevert.Core`. Csproj chép `deps/ffmpeg/bin/*` vào thư mục con `ffmpeg\` của output (spec: DLL của ffmpeg không lẫn với DLL của Flevert); thiếu thì build báo "run fetch-deps" như Flevert.Pdf.
- **Chạy công cụ**: `MediaTools` giữ đường dẫn `ffmpeg\ffmpeg.exe`, `ffmpeg\ffprobe.exe` cạnh `flevert.exe` và một delegate chạy công cụ (mặc định `ExternalTool.RunAsync`). Test trên Windows dùng đúng đường của sản phẩm; delegate chỉ để test thay được trên máy dev không phải Windows (xem "Môi trường dev").
- **Whitelist demuxer** cho mọi input: `-format_whitelist mov,matroska,avi,asf,mpegts,mp3,wav,flac,ogg,aac,aiff` (ffmpeg so bằng `av_match_list`, nên `mov` khớp demuxer `mov,mp4,m4a,3gp,3g2,mj2`, `matroska` khớp `matroska,webm`). File `.mp4` chứa playlist HLS, `concat`, `subfile`, avisynth… bị ffprobe từ chối trước khi ffmpeg chạy. Cộng `-protocol_whitelist file` và input `file:<path>`.
- **Môi trường của ffmpeg**: bỏ `FFREPORT` (ffmpeg ghi báo cáo vào file nó chỉ), `AV_LOG_FORCE_*`; không đặt gì khác.
- **Ưu tiên thấp**: `ExternalToolLimits.BelowNormalPriority` đặt lớp ưu tiên của job (`JOB_OBJECT_LIMIT_PRIORITY_CLASS`), nên ffmpeg và mọi tiến trình nó khởi động chạy `BelowNormal`.
- **Tiến độ**: `-progress pipe:1 -nostats`; `out_time_us` / thời lượng → `FileProgress.Percent(p)` (thêm vào core: dòng thứ hai của cửa sổ là "37%" thay cho "page 37 of 200", thanh tiến độ đi theo %). Hai lượt: 0–50 % và 50–100 %.
- **Timeout mỗi lượt** = thời lượng × 6 + 60 s (spec); probe 10 s.
- **Metadata**: `-map_metadata -1` rồi chép lại theo **danh sách cho phép** các tag toàn cục ffprobe đọc được (title, artist, album, album_artist, composer, genre, date, track, disc, comment, copyright, creation_time), mỗi giá trị cắt 1 000 ký tự. Vì vậy GPS (`location`, `com.apple.quicktime.location.*`, ©xyz) không bao giờ được chép, kể cả ra MKV (muxer MKV ghi mọi tag).
- **Xoay**: ffmpeg tự xoay khi mã hóa lại (autorotate, mặc định bật) và xóa ma trận xoay; kích thước sau xoay tính từ probe để chọn độ phân giải.
- **HDR → SDR**: nguồn có `color_transfer` `smpte2084` (PQ) hoặc `arib-std-b67` (HLG) → `zscale` về tuyến tính, `tonemap=hable`, `zscale` về BT.709, `format=yuv420p`. Mọi video ra H.264/VP9 8-bit đều kết thúc bằng `format=yuv420p` và kích thước chẵn.
- **Luồng** (spec bổ sung lần 2): video là luồng video đầu tiên không phải ảnh bìa (`attached_pic`); audio là luồng audio **đầu tiên** (không theo `default`), mọi thao tác chỉ ghi một; còn track khác thì `OutputNote` "Only the first of its N sound tracks was kept." (spec ghi "ghi vào log": note nằm trong dòng log của file, và hiện cả trong thông báo vì người dùng mất track ngôn ngữ khác). Bỏ phụ đề, luồng data, tệp đính kèm.
- **Spec bổ sung lần 2 (main, 2026-10-20)**: bỏ MKV khỏi đích Convert; menu `Compress ▸` (Light (best quality) · Balanced · Strong (smallest) · Under 10/25/100 MB) · `Convert to ▸` (MP4 · MOV · WEBM · GIF) · `Extract audio ▸` · `Remove hidden info` = 16; audio `Convert to ▸`; icon chờ v0.2.2. `video.strip-metadata` (`-c copy`, cùng container, chỉ giữ `creation_time`, giữ ma trận xoay). Thông báo nêu dung lượng (`OutputNote.AddSizes`, core). ffmpeg `SingleProcess` + trần 2 GB.
- **2 lượt (Under N MB)**: file thống kê x264 (`-passlogfile`) nằm trong `%LOCALAPPDATA%\Flevert\media\<guid>\` (thư mục riêng của user, thêm vào `AppData.Subfolders` để gỡ cài xóa được), xóa sau mỗi file; không ghi cạnh file của người dùng, không ghi vào `%TEMP%` (có thể cho tài khoản khác quyền ghi).
- **GIF**: một lệnh `split` → `palettegen` → `paletteuse` (palette tính trên toàn bộ đoạn rồi mới dùng: "2 lượt" trong một tiến trình). Bộ nhớ tối đa ≈ 30 s × 12 fps × 480 × 480 × 4 B ≈ 330 MB, chấp nhận được; không cần file palette tạm. Video > 30 s: `OutputNote` "Only the first 30 seconds were converted."
- **Thứ tự kiểm tra trước khi chạy**: probe (từ chối sớm), dung lượng đĩa trống so với ước lượng output, giới hạn kích thước output (≤ 4 × input + 100 MB, qua `-fs`).

## Môi trường dev (phiên 2026-10-20 trên Linux)

Phiên này chạy trong container Linux (không có Windows): build bằng `-p:EnableWindowsTargeting=true`, `Flevert.Media.Tests` chạy với ffmpeg 6.1 của hệ thống (delegate chạy công cụ trong test dùng `Process` khi không phải Windows). Phần chỉ chạy được trên Windows (job object, ưu tiên, `flevert.exe` thật, installer, MenuProbe) viết theo mẫu có sẵn và **phải kiểm trên Windows/CI** trước khi gộp; mỗi mục như vậy đánh dấu "cần kiểm trên Windows" trong todo.

## Thứ tự và phụ thuộc

```
T1 deps + project + Ffprobe ── T2 audio.to-mp3 (lát dọc, core: Percent, ưu tiên) ── T3 audio còn lại + extract
                                         │
                                         ├── T4 video.to-mp4/mov/mkv/webm (xoay, HDR, metadata) ── T5 compress (CRF, Under N MB)
                                         │                                                    └── T6 GIF
                                         └── T7 menu ≤ 16 ── T8 đóng gói (installer, notices, mã nguồn, ADR-0009, CI) ── T9 file độc
```

## Rủi ro

| Rủi ro | Mức | Cách giảm |
|---|---|---|
| ffmpeg kéo mạng/đọc file khác qua demuxer (HLS, concat, avisynth) | Cao | Whitelist demuxer + giao thức; ffprobe từ chối trước; test fixture HLS trỏ cổng loopback, kiểm không có kết nối |
| Under N MB vượt giới hạn | Cao | Kích thước < N × 10⁶ là điều kiện của test; chạy lại lượt 2 với bitrate −10 % tối đa 2 lần, rồi lỗi |
| Bộ cài > 150 MB | Trung | Bản shared, chỉ giữ exe/DLL cần; đo ở T8, quá thì hỏi |
| Không kiểm được trên Windows trong phiên Linux | Trung | Phần Windows theo mẫu đã review của qpdf; ghi rõ mục cần kiểm; CI Windows trước khi gộp |
| Nghĩa vụ mã nguồn GPL | Trung | Asset mã nguồn sinh bằng script ghim hash (T8), ADR-0009 |
| VP9 chậm (`-cpu-used 0` mặc định) | Thấp | Đo, chọn `-cpu-used` (T4) |

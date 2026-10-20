# Spec: media (v0.3)

Module `media` trong [capability-map.md](capability-map.md). Phụ thuộc `core` (dùng lại `ExternalTool`, tiến độ trong file và output nhiều file làm ở v0.2). Phiên bản: **v0.3.0**. Trạng thái: **đã chốt phạm vi** (2026-10-20), **bản bổ sung đã duyệt** (2026-10-20: menu, giấy phép GPL, thứ tự làm, phạm vi nhánh); **bổ sung lần 2** (2026-10-20, người dùng giao maintainer tự quyết: Remove hidden info cho video, bỏ MKV khỏi đích Convert, menu theo người dùng thật có icon, thông báo nêu dung lượng, danh sách demuxer được phép, chọn luồng, thư viện đã cân nhắc); bản dựng ffmpeg và preset được đo lại ở bước Plan.

Làm trên nhánh `feat/v0.3-media` từ `main` sau v0.2.0, song song với v0.2.1 (nhánh `feat/v0.2.1-pdf`). Thứ tự phát hành: v0.2.1 → v0.2.2 → v0.3.0. Khi v0.2.1 rồi v0.2.2 vào `main`, nhánh này rebase lên `main` rồi mới mở (hoặc cập nhật) pull request. Menu video/audio theo nguyên tắc menu của v0.2.2 (pdf.md, mục "Menu: sắp lại theo người dùng thật"): nhãn "Convert to ▸", việc hay làm đứng trên, icon Fluent (MIT) cho nhóm và việc đơn lẻ. Để ít đụng nhau: chỉ thêm file trong `src/Flevert.Media/`, `tests/Flevert.Media.Tests/`, `docs/adr/0009-…`, và sửa tối thiểu ở chỗ chung (đăng ký thao tác trong `Program.cs`, danh sách đuôi của menu, `scripts/deps.json`, `Flevert.sln`, CI, installer, notices).

## Objective

Video quay điện thoại quá nặng để gửi, định dạng không mở được trên máy khác, cần lấy nhạc từ video: hôm nay người dùng tải "video converter" đầy quảng cáo hoặc upload lên web. Flevert v0.3 làm các việc phổ biến nhất **bằng chuột phải, trên máy, không upload**, với preset dễ hiểu thay cho thông số kỹ thuật.

### User stories (ưu tiên từ trên xuống)

1. Video 2 phút quay iPhone (MOV, HEVC, 400 MB) → **Compress > Under 25 MB** → MP4 dưới 25 MB, gửi được qua Gmail/Messenger, xem được trên mọi máy.
2. MOV/MKV/WEBM không mở được → **Convert to > MP4** (H.264 + AAC, chạy mọi nơi).
3. Lấy nhạc/tiếng từ video → **Extract audio > MP3**.
4. Đổi định dạng audio: FLAC/WAV/M4A/OGG/OPUS/WMA → MP3, M4A, FLAC, WAV…
5. Video ngắn → **Convert to > GIF** (meme, hướng dẫn).
6. Chọn 20 video → một lô, từng file một (mã hóa video đã dùng hết CPU), cửa sổ tiến độ hiện % của file đang chạy và số file.
7. Video quay iPhone có vị trí GPS → **Remove hidden info** → bản sao không còn vị trí, không mã hóa lại (nhanh, không giảm chất lượng), trước khi đăng lên mạng.

Ngoài phạm vi v0.3: cắt/ghép đoạn, ghép nhiều video, phụ đề, xoay video, tải video từ mạng, DRM, mã hóa phần cứng tùy chọn (xem Open Questions), H.265/AV1 làm output, MKV làm output (ít người cần; MKV vẫn là nguồn đọc được).

### Thao tác

Nguồn video: `.mp4 .mov .m4v .mkv .webm .avi .wmv .3gp .mts .m2ts`. Nguồn audio: `.mp3 .wav .m4a .aac .flac .ogg .opus .wma .aiff .aif`. Output cạnh file gốc, không ghi đè, file gốc không đổi.

| op-id | Menu | Output (mặc định) |
|---|---|---|
| `video.to-mp4` | Convert to > MP4 | H.264 (CRF 20, preset `medium`, `yuv420p`) + AAC 192k, `+faststart`; giữ độ phân giải và fps; xoay theo metadata |
| `video.to-mov` | Convert to > MOV | như MP4 |
| `video.to-webm` | Convert to > WEBM | VP9 (CRF 32, `-b:v 0`, `-row-mt 1`) + Opus 128k |
| `video.to-gif` | Convert to > GIF | tối đa 480 px cạnh dài, 12 fps, palette 2 lượt (`palettegen`/`paletteuse`); video > 30 s: chỉ 30 s đầu, thông báo nói rõ |
| `video.compress-light` / `-balanced` / `-strong` | Compress > Light (best quality) / Balanced / Strong (smallest) | MP4 H.264 CRF 23 / 28 / 32; Strong thu về tối đa 720p; AAC 128k / 96k / 64k; không nhỏ hơn bản gốc thì bỏ qua (skipped) |
| `video.compress-under-10mb` / `-25mb` / `-100mb` | Compress > Under 10 MB / 25 MB / 100 MB | MP4 2 lượt theo bitrate đích = (giới hạn × 8 × 0,97 / thời lượng) − audio; bitrate video < ngưỡng theo độ phân giải thì thu nhỏ (1080p → 720p → 480p); không thể (video quá dài) thì lỗi rõ "Too long to fit under 10 MB" |
| `video.extract-mp3` / `video.extract-m4a` | Extract audio > MP3 / M4A | MP3 VBR V2 / AAC 192k (M4A giữ nguyên luồng AAC nếu nguồn đã là AAC: không mã hóa lại) |
| `video.strip-metadata` | Remove hidden info | `<tên> (no metadata).<đuôi gốc>`: **không mã hóa lại** (`-c copy`), cùng container; bỏ metadata toàn cục **và** của từng luồng (GPS `location`, `com.apple.quicktime.location.ISO6709`, model máy, phần mềm), chỉ giữ `creation_time`; luồng video/audio trùng byte với nguồn. Không có gì để bỏ → skipped "This video has no hidden info to remove." |
| `audio.to-mp3` / `-m4a` / `-wav` / `-flac` / `-ogg` / `-opus` | Convert to > … | MP3 V2; AAC 192k; PCM 16-bit; FLAC mức 5; Vorbis q5; Opus 128k. Không đề xuất đích trùng nguồn |

Quy tắc chung:

- 1 MB = 1 000 000 byte (như module ảnh). Kết quả "Under N MB" **phải** < N × 1 000 000 byte; lượt 2 vượt thì chạy lại với bitrate thấp hơn 10% (tối đa 2 lần), rồi mới báo lỗi.
- Giữ metadata cơ bản (ngày quay), **bỏ GPS** (`-map_metadata` có lọc; iPhone ghi vị trí vào `com.apple.quicktime.location.ISO6709`).
- HDR (HLG/PQ từ iPhone) → SDR bằng tone-map khi ra H.264 8-bit, để màu không bị nhạt.
- Tên output: `clip.mp4`, `clip (compressed).mp4`, `clip (under 25 MB).mp4`, `clip.mp3`, `clip (no metadata).mov`.
- **Thông báo nêu dung lượng** cho Compress và Under N (người dùng nén là để biết đã đủ nhỏ chưa): "Done: clip (under 25 MB).mp4 (24.3 MB, was 412 MB)"; nhiều file: "Done: 5 files, 412 MB → 61 MB". Dùng `OutputNote` hiện có.
- Chỉ lấy **luồng video đầu và luồng audio đầu** (`-map 0:v:0? -map 0:a:0?`); bỏ phụ đề, luồng dữ liệu, tệp đính kèm (`-sn -dn`, không map attachment), ảnh bìa/thumbnail của MOV. MKV nhiều track audio: lấy track đầu, ghi vào log.
- Video dọc của điện thoại: xoay theo metadata khi mã hóa lại và **không** để lại ma trận xoay ở output (trình xem không xoay hai lần); `Remove hidden info` giữ nguyên ma trận xoay (không mã hóa lại).

### Menu (bắt buộc ≤ 16 mục mỗi đuôi): theo người dùng thật, có icon

Theo nguyên tắc menu của v0.2.2 (pdf.md, mục "Menu: sắp lại theo người dùng thật"): việc hay làm đứng trên, tên là lời thường, icon Fluent (MIT) cho nhóm và việc đơn lẻ, đường kẻ chỉ dùng nếu v0.2.2 đã đo là không tính vào giới hạn 16. Explorer chỉ hiện 16 mục đầu của cây menu dưới `Flevert` (tính cả mục mở submenu; core.md, mục Menu).

```
Video:
[icon] Compress ▸        Light (best quality) · Balanced · Strong (smallest)
                         · Under 10 MB · Under 25 MB · Under 100 MB                   (7)
[icon] Convert to ▸      MP4 · MOV · WEBM · GIF (bỏ đích trùng nguồn)               (≤ 5)
[icon] Extract audio ▸   MP3 · M4A                                                    (3)
──────
[icon] Remove hidden info                                                             (1)   ≤ 16
Audio:
[icon] Convert to ▸      MP3 · M4A · WAV · FLAC · OGG · Opus (bỏ đích trùng nguồn)  (≤ 7)
```

- **Compress đứng đầu**: user story số 1 (video quá nặng để gửi). Convert thứ hai, Extract audio thứ ba, Remove hidden info riêng một cụm (riêng tư).
- Icon: Compress `arrow_minimize`, Convert to `arrow_swap`, Extract audio `music_note_2`, Remove hidden info như ảnh và PDF (`shield`/`eye_off`); dùng chung `assets/menu/` và cách ghi `Icon` của v0.2.2. Nếu v0.2.2 chưa vào `main` khi làm task menu: làm nhãn và thứ tự trước, thêm icon sau khi rebase.
- Đích trùng nguồn không hiện (`.mp4` không có `Convert to > MP4`). Không thêm mục nào vào menu video mà không bỏ mục khác.
- Test menu khẳng định mọi đuôi video/audio ≤ 16 mục; MenuProbe kiểm trên Explorer thật; ảnh chụp menu video và audio (sáng và tối) trong PR để người dùng duyệt.
- `.gif` vẫn là **ảnh** (module image), không thêm vào nguồn video.

## Tham khảo trước khi viết (CLAUDE.md, "Reuse before writing")

| Ứng viên | Giấy phép | Quyết định |
|---|---|---|
| ffmpeg/ffprobe bản dựng BtbN (`win64-gpl-shared`) | GPL (tiến trình riêng) | **Dùng** (bảng Engine) |
| FFMpegCore (NuGet, wrapper C#) | MIT | Không dùng làm dependency: tự tạo tiến trình, không qua `ExternalTool` (job object, trần bộ nhớ, giết cả cây, stdin), mà các giới hạn đó là yêu cầu bảo mật. Được tham khảo cách dựng tham số và đọc `-progress` (MIT, ghi nguồn nếu lấy) |
| Xabe.FFmpeg | giấy phép phi thương mại | Loại (không hợp MIT) |
| HandBrakeCLI | GPL, ~40 MB thêm | Loại (trùng ffmpeg) |
| FileConverter (Tichau) | GPL | Chỉ tham khảo ý tưởng preset, **không** đọc để chép code |
| Công thức 2 lượt theo bitrate, palette GIF | tài liệu ffmpeg (wiki "Encode/H.264", `palettegen`) | Dùng làm nguồn tham số, ghi trong ADR-0009 |

## Engine: ffmpeg

| | Lựa chọn | Ghi chú |
|---|---|---|
| Nhị phân | `ffmpeg.exe` + `ffprobe.exe` bản dựng Windows của **BtbN/FFmpeg-Builds**, nhánh phát hành 8.x, **tag autobuild có ngày** (không dùng tag `latest`: file đổi mỗi ngày) + SHA256, tải bằng `scripts/fetch-deps.ps1` | Đã kiểm 2026-10-20: có bản `win64-gpl` và `win64-lgpl` cho n8.1 và n9.0 |
| Bản GPL hay LGPL | **GPL** (có libx264: chất lượng/dung lượng H.264 tốt nhất, CRF). LGPL không có x264; `h264_mf` (Media Foundation) không có CRF và kém rõ ở cùng dung lượng | Chạy như **tiến trình riêng**: Flevert vẫn MIT (aggregation). **Cả hai** bản đều buộc kèm mã nguồn khi phân phối binary: LGPL 2.1 (mục 4, 6) cho ffmpeg và thư viện LGPL; GPL thêm x264 và mọi thư viện GPL trong bản dựng. Cách đáp ứng: asset `ffmpeg-source-<tag>.zip` của mỗi release Flevert gồm mã nguồn đúng commit của ffmpeg và từng thư viện trong bản dựng, cùng script build của BtbN ở đúng tag, sinh tự động bởi `scripts/fetch-ffmpeg-source.ps1` (ghim hash); kèm văn bản giấy phép trong `THIRD-PARTY-NOTICES.txt`. **Đã chốt GPL** (người dùng duyệt 2026-10-20, Open Question 1); ghi quyết định và nghĩa vụ mã nguồn vào ADR-0009 |
| Biến thể | Bản **shared** (`win64-gpl-shared`): `ffmpeg.exe` và `ffprobe.exe` dùng chung DLL, bộ cài nhỏ hơn hai exe static. Đo cỡ ở task đóng gói; bộ cài quá 150 MB → hỏi trước | DLL của ffmpeg nằm trong thư mục con `ffmpeg\` của thư mục cài, không lẫn với DLL của Flevert |
| Gọi | Đường dẫn tuyệt đối trong thư mục cài; `ArgumentList`; `-nostdin -hide_banner -nostats -n`; input `file:<path>` với `-protocol_whitelist file` **và** `-format_whitelist <danh sách demuxer được phép>`; `-progress pipe:1`; chạy qua `ExternalTool` với `SingleProcess = true` (ffmpeg không tạo tiến trình con), trần bộ nhớ 2 GB mỗi tiến trình, ưu tiên `BelowNormal` | Video "độc" (playlist HLS, `concat`, `subfile`, URL) không được kéo mạng hay đọc file khác. **Chỉ whitelist giao thức là chưa đủ**: playlist HLS hay `concat` trỏ tới file cục bộ khác vẫn đi qua giao thức `file`, và nội dung file đó sẽ nằm trong output mà người dùng gửi đi (kiểu lỗi đọc file cục bộ đã biết của ffmpeg). Vì vậy demuxer bị giới hạn: `mov,mp4,m4a,3gp,3g2,mj2`, `matroska,webm`, `avi`, `asf`, `mpegts`, `mp3`, `wav`, `aac`, `flac`, `ogg`, `aiff`; `ffprobe` (cùng whitelist) đọc `format_name` trước, ngoài danh sách → từ chối trước khi chạy ffmpeg |
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
- `ffprobe` trước để từ chối sớm: không có luồng video/audio, thời lượng 0 hoặc > 12 giờ, độ phân giải > 8K, demuxer ngoài danh sách được phép.
- Không bao giờ dùng `-f concat`, `-f hls`, `subfile:`, `-i` với URL, hay filter đọc file (`movie=`, `amovie=`, `subtitles=`); tham số là hằng của Flevert.
- Bản dựng ghim hash; CI kiểm `ffmpeg -version` khớp bản mong đợi; Dependabot không theo dõi được nên lịch kiểm bản mới hằng tháng ghi trong RELEASING.

## Thứ tự làm (gợi ý cho Plan)

1. `fetch-deps` thêm ffmpeg/ffprobe (bản đã chọn, ghim tag + SHA256), project `Flevert.Media` + test project, `Ffprobe` (JSON, timeout, từ chối sớm).
2. Lát dọc đầu: `audio.to-mp3` chạy thật qua `flevert.exe run` (ExternalTool, tiến độ, Cancel, `.partial`).
3. Các chuyển đổi audio còn lại; `video.extract-mp3/m4a`; `video.strip-metadata` (không mã hóa lại, dễ, giá trị riêng tư cao).
4. `video.to-mp4` (xoay, HDR → SDR, bỏ GPS, chọn luồng), rồi MOV/WEBM.
5. Compress Light/Balanced/Strong; Under 10/25/100 MB (2 lượt, chạy lại khi vượt); thông báo nêu dung lượng.
6. `video.to-gif`.
7. Menu video/audio theo người dùng thật (≤ 16, icon) + MenuProbe + ảnh chụp sáng/tối.
8. Đóng gói: installer, notices, asset mã nguồn ffmpeg (bản GPL), ADR-0009, test-installer kiểm `ffmpeg\`.
9. Bảng file độc (HLS trỏ URL và trỏ file cục bộ, concat, subfile, MKV có tệp đính kèm, 0 byte, hỏng, thời lượng giả 99 giờ, độ phân giải giả 100 000 px) ≤ 10 s hoặc lỗi rõ.

## Commands

```
powershell -ExecutionPolicy Bypass -File scripts/fetch-deps.ps1     # thêm ffmpeg, ffprobe (ghim tag + SHA256)
dotnet build Flevert.sln
dotnet test tests/Flevert.Core.Tests
dotnet test tests/Flevert.Image.Tests
dotnet test tests/Flevert.Pdf.Tests
dotnet test tests/Flevert.Media.Tests
dotnet format Flevert.sln --verify-no-changes
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
powershell -ExecutionPolicy Bypass -File scripts/test-installer.ps1
```

Chạy từng project test **lần lượt**, không chạy cả `Flevert.sln` một lần (mã hóa ảnh/video làm các test gom lô nhạy thời gian thiếu CPU). Thêm `tests/Flevert.Media.Tests` vào CI thành một bước riêng.

## Code Style

Như `src/Flevert.Pdf`: thao tác là class nhỏ, không trạng thái, nhận đường dẫn đã ghim, ghi qua `OutputPath`, gọi công cụ ngoài qua `ExternalTool` với `ArgumentList`; lỗi người dùng hiểu được là exception có message tiếng Anh ngắn.

```csharp
// Dáng mong đợi (không phải code có sẵn): tham số là hằng của Flevert, đường dẫn đi riêng, không bao giờ ghép chuỗi.
public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
{
    var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + ".mp3");
    return OutputPath.WriteAtomicallyAsync(desired, (temp, token) =>
        _ffmpeg.RunAsync(inputPath, temp, ["-vn", "-c:a", "libmp3lame", "-q:a", "2"], _probe.Duration, progress, token), ct);
}
```

## Project Structure

```
src/Flevert.Media/            → thao tác video/audio, Ffmpeg/Ffprobe (ExternalTool), preset
tests/Flevert.Media.Tests/    → test; fixtures/ sinh bằng ffmpeg (lavfi testsrc/sine), có README nguồn gốc
docs/adr/0009-…                → bản dựng ffmpeg (GPL hay LGPL) và nghĩa vụ giấy phép
```

## Testing Strategy

- **Unit**: tính bitrate cho "Under N MB" (thời lượng rất ngắn/dài, audio chiếm hết), chọn độ phân giải, dựng dòng lệnh (không có đường dẫn ghép chuỗi), đọc `-progress`.
- **Tích hợp với ffmpeg thật** trên fixture sinh sẵn: video 10 s 1080p (testsrc + sine), MOV xoay 90°, MOV có metadata vị trí (`-metadata location=+21.0285+105.8542/` và khóa QuickTime), HEVC HDR mẫu (tự sinh bằng x265 của bản GPL), MKV nhiều luồng audio + phụ đề + tệp đính kèm, file `.mp4` thực chất là playlist HLS trỏ `http://127.0.0.1:<cổng test>` (phải không có kết nối nào tới cổng), playlist HLS/`ffconcat` trỏ một file cục bộ chứa chuỗi đánh dấu (output không được chứa chuỗi đó, thao tác bị từ chối), file hỏng, file 0 byte.
- **Kiểm kết quả bằng ffprobe**: codec, độ phân giải, thời lượng ±0,1 s, kích thước < giới hạn, không còn GPS (toàn cục và từng luồng), xoay đúng và không còn ma trận xoay sau khi mã hóa lại, chỉ một luồng video + một luồng audio; `Remove hidden info`: gói (packet) video/audio trùng hash với nguồn.
- **Đo tốc độ** trên máy dev (ghi vào todo): video 60 s 1080p, Convert to MP4 và Compress Balanced; mục tiêu ≤ 2× thời gian thực trên máy 4 nhân, nếu chậm hơn thì ghi số đo và xét Open Question 3.
- **Tiến trình thật + Cancel**: Cancel ở 50% → ffmpeg chết trong 2 s, không còn `.partial`.
- **Runtime thủ công**: bấm thật trên video iPhone thật **của người dùng chỉ khi họ cho phép**; mặc định dùng fixture tổng hợp.

## Boundaries

- **Always:** tiến trình riêng, whitelist giao thức, timeout, giết cả cây khi Cancel; kết quả "Under N MB" thật sự dưới N; ghi qua `.partial`; test viết trước và thấy đỏ; mỗi commit một thay đổi logic, build + format + mọi project test xanh trước commit; ghi tiến độ và quyết định vào `tasks/media/plan.md` + `tasks/media/todo.md`.
- **Ask first:** tăng bộ cài quá 150 MB; thêm mã hóa phần cứng; thêm NuGet; thêm mục menu ngoài bố cục ở trên.
- **Never:** liên kết thư viện GPL vào tiến trình Flevert; tải ffmpeg lúc chạy; gửi file ra mạng; cho người dùng truyền tham số ffmpeg tự do; đẩy thẳng vào `main`; sửa `Directory.Build.props` (version), `tasks/HANDOFF.md`, mục đã phát hành của CHANGELOG (chỉ thêm dòng vào `[Unreleased]`); dùng video/ảnh cá nhân của người dùng khi chưa hỏi.

## Success Criteria

1. MOV iPhone (HEVC, có thể HDR) → MP4 H.264 mở được trong ứng dụng Media Player của Windows, đúng chiều, màu không nhạt, không còn GPS.
2. Video 60 s 1080p → Under 25 MB < 25 000 000 byte; Under 10 MB với video 20 phút → lỗi rõ, không tạo file.
3. Extract MP3: thời lượng ±0,1 s so với nguồn; M4A từ nguồn AAC không mã hóa lại (hash luồng audio trùng).
4. Mọi chuyển đổi audio trong bảng: ffprobe đọc đúng codec, thời lượng ±0,1 s.
5. File `.mp4` là playlist HLS trỏ URL: bị từ chối, không có kết nối mạng nào.
6. Cancel giữa chừng: ffmpeg dừng ≤ 2 s, không còn `.partial`.
7. Cửa sổ tiến độ hiện % trong file và "2 of 20".
8. Bộ cài có `ffmpeg.exe`, `ffprobe.exe` (thư mục `ffmpeg\`), giấy phép trong `THIRD-PARTY-NOTICES.txt`; release có asset mã nguồn ffmpeg tương ứng; nâng cấp từ bản 0.2.x đã ghim giữ cài đặt; gỡ sạch; bộ cài ≤ 150 MB (hoặc người dùng đã đồng ý con số khác).
9. Menu thật (MenuProbe) của `.mp4`, `.mov`, `.mkv`, `.mp3`, `.flac` đúng bố cục và thứ tự, ≤ 16 mục, có icon (khi v0.2.2 đã vào `main`); người dùng đã duyệt ảnh chụp menu sáng/tối.
13. Remove hidden info: video iPhone có vị trí → output không còn khóa vị trí nào (ffprobe, toàn cục và từng luồng), gói video/audio trùng hash với nguồn, chạy ≤ 5 s cho video 400 MB.
14. Playlist HLS hoặc `ffconcat` trỏ file cục bộ khác: bị từ chối trước khi chạy ffmpeg, output không tồn tại, nội dung file kia không xuất hiện ở đâu.
15. Thông báo Compress/Under N nêu dung lượng mới và cũ.
10. Bảng file độc: mỗi file ≤ 10 s hoặc lỗi rõ, không kết nối mạng, không còn `.partial`.
11. Khởi động lạnh `flevert.exe run` cho thao tác ảnh không chậm hơn v0.2 quá 10% (ffmpeg chỉ chạy khi cần).
12. CI xanh 3 lần; ba review bối cảnh trống (maintainer chạy khi nhận PR) không còn Critical/High/Required.

## Open Questions

1. **GPL hay LGPL: đã chốt GPL** (người dùng duyệt 2026-10-20). Lý do: "Under N MB" cần mã hóa 2 lượt và CRF của x264 để vừa dung lượng mà vẫn đẹp; bản LGPL phải dùng `h264_mf`, không CRF, không 2 lượt, chất lượng tùy bộ mã hóa của từng máy (khó test, khó hứa). Cả hai bản đều phải kèm mã nguồn, nên GPL chỉ làm asset mã nguồn lớn hơn, và asset đó sinh tự động. ffmpeg chạy tiến trình riêng nên Flevert vẫn MIT.
2. Kích thước: bản GPL static khoảng 80–130 MB giải nén; có cần tải ffmpeg lúc cài (bộ cài nhỏ) thay vì đóng gói? Mặc định: đóng gói (offline, không tải gì lúc chạy hay lúc cài).
3. Mã hóa phần cứng (NVENC/QSV/AMF) cho máy yếu: để sau v0.3 trừ khi đo thấy preset `medium` quá chậm (> 3× thời gian thực ở 1080p trên máy 4 nhân).

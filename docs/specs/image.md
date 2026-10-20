# Spec: image

Module `image` trong [docs/specs/capability-map.md](capability-map.md). Phụ thuộc `core` (hợp đồng `IOperation`, `OutputPath`, `Runner`, menu).

## Objective

Người dùng chuột phải vào ảnh → `Flevert` → chọn thao tác → có file ảnh mới cạnh file gốc. Nhu cầu chính:

- Ảnh HEIC từ iPhone không mở được trên Windows / không gửi được → đổi sang JPG.
- Ảnh quá nặng để gửi Zalo / email → nén, hoặc nén xuống dưới một dung lượng cụ thể.
- Đổi định dạng qua lại (PNG ↔ JPG ↔ WebP ↔ AVIF ...).
- Xóa thông tin vị trí GPS / máy chụp trước khi chia sẻ.

### User stories

- Chuột phải `IMG_0001.HEIC` → `Convert > JPG` → có `IMG_0001.jpg`, xoay đúng chiều.
- Chọn 30 ảnh → `Compress > Balanced` → 30 file `tên (compressed).jpg` nhỏ hơn rõ rệt, nhìn không khác.
- Chuột phải ảnh 8 MB → `Compress > Under 1 MB` → file < 1 MB.
- Chuột phải ảnh có GPS → `Remove metadata` → file mới không còn EXIF/GPS.

## Tech Stack

| Thành phần | Lựa chọn | Lý do |
|---|---|---|
| Thư viện ảnh | **Magick.NET-Q8-x64 15.0.0** (Apache-2.0; ImageMagick 7.1.2) | Đã kiểm chứng thực nghiệm: đọc HEIC/HEIF, đọc+ghi AVIF, WebP, JPEG, PNG, GIF, BMP, TIFF. Một package, có sẵn native cho win-x64 |
| Project | `src/Flevert.Image` tham chiếu `Flevert.Core` | Giữ Core không phụ thuộc thư viện ảnh |

Đã cân nhắc NetVips (libvips, LGPL-3.0): nhanh và ít RAM hơn, nhưng bản native Windows không kèm decoder HEIC (HEVC). Magick.NET thắng vì HEIC là nhu cầu số 1.

## Thao tác

Định dạng nguồn: `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff`.

| op-id | Menu | Áp dụng cho | Output |
|---|---|---|---|
| `image.to-jpg` | Convert/JPG | mọi nguồn trừ `.jpg .jpeg` | `tên.jpg` (quality 92) |
| `image.to-png` | Convert/PNG | mọi nguồn trừ `.png` | `tên.png` |
| `image.to-webp` | Convert/WEBP | mọi nguồn trừ `.webp` | `tên.webp` (quality 90) |
| `image.to-avif` | Convert/AVIF | mọi nguồn trừ `.avif` | `tên.avif` (quality 80) |
| `image.to-gif` | Convert/GIF | mọi nguồn trừ `.gif .heic .heif` (từ v0.2: 256 màu làm hỏng ảnh chụp, và mục này là mục thứ 17 của menu HEIC/HEIF khi có Combine into PDF, quá 16 mục Explorer hiện; xem core.md, mục Menu) | `tên.gif` |
| `image.to-bmp` | Convert/BMP | mọi nguồn trừ `.bmp` | `tên.bmp` |
| `image.to-tiff` | Convert/TIFF | mọi nguồn trừ `.tif .tiff` | `tên.tiff` |
| `image.compress-light` | Compress/Light | mọi nguồn | `tên (compressed).<ext>` |
| `image.compress-balanced` | Compress/Balanced | mọi nguồn | như trên |
| `image.compress-strong` | Compress/Strong | mọi nguồn | như trên |
| `image.compress-under-500kb` | Compress/Under 500 KB | mọi nguồn | `tên (under 500 KB).jpg` |
| `image.compress-under-1mb` | Compress/Under 1 MB | mọi nguồn | `tên (under 1 MB).jpg` |
| `image.compress-under-5mb` | Compress/Under 5 MB | mọi nguồn | `tên (under 5 MB).jpg` |
| `image.strip-metadata` | Remove metadata | mọi nguồn | `tên (no metadata).<ext>` |

Quy tắc chung:

- **Xoay theo EXIF** (auto-orient) trước khi ghi, để ảnh không bị nằm ngang sau khi mất EXIF.
- **Nguồn không ghi lại được** (HEIC/HEIF): Compress và Remove metadata ghi ra JPG.
- **Ảnh động** (GIF/WebP nhiều frame): giữ animation khi đích là GIF hoặc WebP (frame được mở rộng trước khi ghi, kể cả khi Remove metadata, vì WebP kiểu gif2webp dùng frame trộn alpha); đích khác lấy frame đầu. **TIFF nhiều trang** không phải animation: Convert lấy trang đầu; Compress và Under N **báo lỗi** (chỉ đọc tới trang 2 để biết) vì kết quả một ảnh sẽ mất trang; Remove metadata giữ đủ trang. Chỉ giải mã frame đầu khi kết quả là một ảnh; animation có frame × canvas vượt 400 MP bị từ chối trước khi mở rộng.
- **File sai đuôi** (WebP/PNG lưu thành `.jpg`): module tự đọc chữ ký đầu file và chọn bộ giải mã tương ứng, chỉ trong danh sách cho phép; ImageMagick không bao giờ tự đoán. Nội dung không khớp chữ ký nào (script) được giải mã theo đuôi và lỗi.
- **HEIC/HEIF/AVIF → JPG** dùng chroma 4:2:0 như ảnh điện thoại (ImageMagick mặc định 4:4:4 ở quality ≥ 90, làm file to thêm ~44%).
- **CMYK** (ảnh từ in ấn): Convert, Compress, Under N chuyển sang sRGB qua profile CMYK của ảnh (hoặc profile in phổ biến nếu không có). Remove metadata giữ nguyên.
- **Độ sâu màu:** dùng Magick.NET Q8, nên ảnh 16-bit/kênh (PNG, TIFF) ra 8-bit. **Trade-off chấp nhận:** Q8 nhẹ và nhanh hơn Q16; khác biệt 16 → 8 bit không thấy bằng mắt, nhưng "lossless" của PNG Light/Balanced chỉ đúng với ảnh 8-bit.
- **ICC** được giữ nguyên từng byte; profile có thể chứa tag riêng của người tạo (hiếm). Ghi nhận, không xử lý.
- **Hủy:** mọi thao tác dừng giữa chừng khi người dùng bấm Cancel (qua callback tiến độ của ImageMagick, gắn lại sau Coalesce/Optimize). Mã hóa/giải mã AVIF và HEIC không báo tiến độ nên không ngắt được (giới hạn thư viện); vì ảnh được mã hóa vào bộ nhớ rồi mới tạo file, tiến trình thoát giữa chừng không để lại file tạm.
- **Bộ nhớ:** kết quả được mã hóa trong RAM rồi mới ghi; ảnh nhiều trang cần khoảng 2 lần dung lượng output. Ghi TIFF nhiều trang bị chặn khi kích thước thô tệ nhất (rộng × cao × kênh × bit) nhân hệ số phình của kiểu nén (Group 4/fax ×3, LZW ×1,4) vượt 1 GB: 60 trang A4 trắng đen qua, khoảng 40 trang A4 màu trở lên bị từ chối. Nén không phải bảo đảm (TIFF tự chế 1,5–2,2 MB ra 435–450 MB). GIF/WebP động không chịu giới hạn này mà chịu giới hạn frame × canvas 400 MP. Ảnh 400 MP có thể dùng ~3 GB RAM đỉnh. Chấp nhận.
- **TIFF nhiều trang khi Remove metadata:** mọi trang ghi cùng một kiểu nén (giới hạn của bộ ghi): giữ kiểu nén gốc nếu mọi trang giống nhau, nếu khác nhau (scanner trộn trang Group 4 trắng đen với trang màu) thì dùng Zip lossless cho tất cả, để trang màu không thành trắng đen (bản scan trộn như vậy to khoảng 1,8 lần). TIFF nén JPEG được ghi lại ở quality 95, nên bản scan chất lượng thấp có thể to ra (q50: 351 KB → 805 KB). Ảnh thu nhỏ (thumbnail, NewSubfileType = 1) không phải trang: Remove metadata bỏ chúng (có thể còn hiện ảnh trước khi sửa), Compress/Under N lấy trang thật duy nhất dù thumbnail nằm trước, giữa hay sau.
- **WebP động khi Remove metadata** được mở rộng frame trước khi ghi, nên cũng chịu giới hạn frame × canvas 400 MP như Convert/Compress (ví dụ 1080p × 200 frame bị từ chối).
- **PNG có ICC** ghi profile hai lần (chunk iCCP và một bản text "Raw profile type icc"): thêm vài KB, không lộ gì thêm. Chấp nhận.
- **Compress HEIC** thật thường bị bỏ qua (JPG không nhỏ hơn HEIC đủ 5%); đó là kết quả "skipped", không phải lỗi.
- **Remove metadata** với WebP/AVIF lossy có thể làm file to hơn (ghi lại ở quality 95 để không mất chất lượng). Chấp nhận.
- **Policy:** ImageMagick còn nạp `policy.xml`/`delegates.xml` từ thư mục làm việc, `MAGICK_HOME`, `MAGICK_CONFIGURE_PATH`, `%LOCALAPPDATA%\ImageMagick`, thư mục cấu hình XDG/HOME, và áp dụng sau policy của ta (có thể đổi temp path, bật delegate). Vì vậy: có bất kỳ file cấu hình ImageMagick nào (policy, delegates, colors, type, …) trong các thư mục người dùng kia (`%LOCALAPPDATA%`, `%APPDATA%`, `%USERPROFILE%`, XDG/HOME, `MAGICK_HOME`, `MAGICK_CONFIGURE_PATH`) thì không xử lý ảnh nào; lúc khởi tạo, thư mục làm việc là thư mục cấu hình riêng; sau khi nạp còn đọc thử MVG/MSL/TEXT và phải bị chính luật coder từ chối. Giới hạn: file cấu hình ImageMagick nạp **sau** khởi tạo (colors, thresholds, type) vẫn tìm ở thư mục làm việc hiện tại; flevert.exe luôn chạy với thư mục làm việc là thư mục cài (SafeStartup) nên không bị ảnh hưởng, host khác phải tự làm vậy.
- **Trong suốt**: đích không có alpha (JPG, BMP) thì nền trắng.
- **Compress**:
  - JPG/WebP/AVIF: Light = quality 85, Balanced = 75, Strong = 60 + thu cạnh dài về tối đa 2560 px.
  - PNG: Light/Balanced = nén lossless mức cao nhất; Strong = giảm còn 256 màu.
  - GIF/BMP/TIFF: ghi ra JPG theo mức tương ứng (BMP/TIFF thường không nén; GIF giữ nguyên nếu là ảnh động).
  - Xóa metadata trừ profile màu ICC.
  - Nếu kết quả **nhỏ hơn chưa tới 5%** (JPG đã nén sẵn ở quality ~70–80, PNG đã tối ưu, HEIC thật) thì file **bị bỏ qua** ("skipped", không phải lỗi), không ghi file: một bản "compressed" gần bằng hoặc to hơn sẽ đánh lừa người dùng. Ngưỡng 5% áp dụng cả cho PNG nén lại lossless (chấp nhận: dưới 5% không đáng một bản sao). Riêng GIF động (chỉ tối ưu lại, không nén mạnh hơn được) thì **báo lỗi** kèm gợi ý dùng Convert > WEBP, vì "đã nén tốt" là sai với GIF quá nặng.
- **Under N**: luôn ra JPG. Tìm nhị phân quality 40–92; nếu quality 40 vẫn quá lớn thì thu kích thước 15% mỗi bước rồi tìm lại. Kết quả phải < N (1 MB = 1 000 000 byte cho dễ hiểu với người dùng).
- **Remove metadata**: xóa EXIF, GPS, XMP, IPTC, comment, text chunk PNG; giữ ICC (cả với PNG); giữ định dạng (trừ HEIC → JPG). Điểm ảnh được ghi lại: JPEG giữ quality ước lượng của nguồn, WebP/AVIF ghi ở quality 95, WebP lossless giữ lossless.
- **Under N** với ảnh đã nhỏ hơn N: vẫn ghi lại JPG ở quality tốt nhất vừa N, nên có thể to hơn file gốc nhưng luôn < N (hợp đồng là "dưới N"). Ảnh rất lớn được thu ngay từ đầu về khoảng N × 20 pixel; mỗi vòng thu nhỏ tính từ ảnh gốc theo kích thước đo được ở quality 40; profile lớn hơn 1/10 N (profile in CMYK) được chuyển sang sRGB và bỏ.
- **Convert** giữ metadata (trừ khi định dạng đích không chứa được). Giới hạn đã biết: bộ ghi TIFF của ImageMagick bỏ khối EXIF, nên `Convert > TIFF` mất EXIF (không ảnh hưởng ảnh; chấp nhận, vì vá phải tự chép từng thẻ EXIF sang thẻ TIFF).

## Giới hạn tài nguyên (bảo mật)

Ảnh độc hại có thể "nổ" bộ nhớ (decompression bomb). Đặt `ResourceLimits` của Magick.NET trước mọi thao tác:

- Chiều rộng/cao tối đa 40 000 px; tổng pixel tối đa 400 MP.
- Bộ nhớ tối đa 2 GB, phần còn lại dùng đĩa tạm; thời gian tối đa 120 giây/ảnh.
- Chỉ bật coder cho các định dạng nguồn và đích ở trên (chặn MSL, MVG, URL, TEXT ... của ImageMagick).

Vượt giới hạn → file đó lỗi, ghi log, các file khác chạy tiếp.

### Ngân sách bộ nhớ khi chạy song song

Giới hạn 2 GB của ImageMagick tính cho **cả tiến trình**. Khi chủ lô chạy nhiều file cùng lúc (docs/specs/core.md, "Xử lý song song"), vài ảnh cực lớn cùng lúc sẽ tràn sang đĩa (chậm) hoặc vượt giới hạn đĩa 4 GB và lỗi, trong khi chạy lần lượt thì qua. Không được để song song làm hỏng file mà tuần tự làm được:

- Mỗi thao tác ảnh lấy "suất" từ một ngân sách chung **trước khi giải mã**. Ngân sách = `min(ResourceLimits.Memory (2 GB), 1/4 bộ nhớ máy)`, dựng từ hằng số chứ không khởi tạo ImageMagick (một lỗi khởi tạo chỉ làm hỏng một file). Suất = max(byte pixel × 3, kích thước file × 2):
  - byte pixel: mỗi khung tính theo **canvas** (khung tối ưu của GIF/WebP động bị `Coalesce` bung ra cả canvas: GIF 200 khung 1×1 trên canvas 1000×1000 cần ~800 MB, ping từng khung chỉ thấy 2,4 KB) × max(4, số kênh ping báo + 1) byte (ping không báo alpha: TIFF CMYK + alpha ping ra 4 kênh, giải mã ra 5); × 3 = ảnh nguồn, bản đang xử lý, bộ đệm mã hóa.
  - kích thước file × 2: bộ giải mã WebP/HEIC đọc cả file vào bộ nhớ, profile và comment được chép, ngoài phần ImageMagick đếm.
  - Kích thước lấy bằng ping (chỉ đọc header, cùng đường đọc an toàn: stream, decoder theo chữ ký trong allow-list). Ping lỗi → suất = cả ngân sách (chạy một mình; lỗi thật sẽ do thao tác báo).
- Suất lớn hơn nửa ngân sách → lấy cả ngân sách (chạy một mình): giới hạn thời gian 120 s của ImageMagick tính theo đồng hồ cho từng ảnh, ảnh lớn chạy cạnh ảnh khác có thể vượt.
- Cấp theo thứ tự đến (FIFO): một ảnh lớn đang chờ không bị các ảnh nhỏ đến sau chen mãi. Hủy trong lúc chờ: không giữ suất nào, và người chờ phía sau được xét lại.
- Ảnh điện thoại 12 MP ≈ 144 MB/suất → tối đa 8 cùng lúc; 48 MP ≈ 576 MB → 1 (hơn nửa ngân sách 1 GB của máy 4 GB) đến 3; ảnh 400 MP → một mình.
- Ping lấy kích thước cũng có thể đọc cả file (bộ giải mã WebP làm vậy): trong lúc ping giữ một suất bằng kích thước file × 2, trả lại trước khi xin suất đầy đủ (giữ trong lúc chờ có thể kẹt hàng đợi FIFO).
- Đã biết, chấp nhận:
  - TIFF nhiều trang tính mọi trang dù Convert/Compress chỉ đọc một trang (chỉ chậm hơn, không sai).
  - Ping không thấy hết kênh: hầu hết định dạng RGB báo 0 kênh (dùng sàn 4), CMYK + alpha báo 4 (cộng 1 → 5); TIFF có nhiều extra sample hơn nữa vẫn bị tính thiếu.
  - File từ 1000 khung/trang trở lên: ping vượt `ListLength` → suất cả ngân sách → chạy một mình (đúng như trước khi có song song).
- Ưu tiên tiến trình: BelowNormal chỉ khi từ 2 file chạy cùng lúc; về Normal khi còn 1 (ảnh lớn chạy một mình không bị bỏ đói tới giới hạn 120 s; Explorer mở log không thừa hưởng BelowNormal).

## Commands

Như `core`. Thêm: test fixture HEIC nhỏ trong `tests/Flevert.Image.Tests/fixtures/` (lấy từ nguồn có giấy phép cho phép phân phối, ghi rõ nguồn trong `fixtures/README.md`).

## Project Structure

```
src/Flevert.Image/
  ImageOperations.cs     → danh sách IOperation của module
  ImageConvert.cs        → Convert/*
  ImageCompress.cs       → Compress/* (mức + dưới N)
  ImageStripMetadata.cs  → Remove metadata
  MagickSetup.cs         → ResourceLimits + coder policy, gọi một lần
tests/Flevert.Image.Tests/
```

## Testing Strategy

- Ảnh test sinh trong test bằng Magick.NET (gradient, có alpha, có EXIF orientation + GPS, GIF động); HEIC dùng fixture.
- Mỗi thao tác: kiểm định dạng output (đọc magic bytes), kích thước, xoay đúng, metadata còn/mất đúng, file gốc không đổi (hash).
- Under N: kiểm `< N` với ảnh 12 MP nhiễu (khó nén).
- Giới hạn: ảnh khai báo 100 000 × 100 000 px → lỗi nhanh, không cấp phát lớn.
- Runtime: chuột phải thật qua `MenuProbe` trên HEIC, JPG, PNG.

## Boundaries

- **Always:** auto-orient; không ghi đè file gốc; đặt ResourceLimits trước khi đọc ảnh.
- **Ask first:** (người dùng đã ủy quyền tự quyết — ghi lý do vào spec/ADR).
- **Never:** bật coder ImageMagick có thể đọc file/URL khác (MSL, MVG, TEXT, URL, EPHEMERAL); gọi Ghostscript.

## Success Criteria

1. `IMG.heic` → `Convert > JPG` ra JPG đúng chiều, mở được trong Photos.
2. Mọi cặp nguồn → đích trong bảng chuyển được (test ma trận).
3. Compress Balanced trên ảnh chụp gốc 12 MP (JPG quality ≥ 92, 4:2:0 như điện thoại) cho file nhỏ hơn ≥ 40%. Với ảnh đã nén sẵn (quality ≤ 85) mức giảm ít hơn nhiều, hoặc bị từ chối vì không nhỏ hơn.
4. Under 1 MB cho kết quả < 1 000 000 byte với ảnh 12 MP nhiễu.
5. Remove metadata: output không còn GPS/EXIF (kiểm bằng Magick.NET đọc lại), vẫn còn ICC.
6. GIF động → WebP vẫn động (số frame bằng nhau).
7. Ảnh "bomb" bị từ chối < 2 giây, RAM tiến trình < 500 MB.
8. Menu chỉ hiện Convert sang định dạng khác định dạng nguồn.
9. `dotnet test` + `dotnet format --verify-no-changes` qua.

## Open Questions

Không còn (người dùng ủy quyền tự quyết).

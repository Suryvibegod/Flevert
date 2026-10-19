# Spec: image

Module `image` trong [CAPABILITY-MAP.md](CAPABILITY-MAP.md). Phụ thuộc `core` (hợp đồng `IOperation`, `OutputPath`, `Runner`, menu).

## Objective

Người dùng chuột phải vào ảnh → `ConvertAnything` → chọn thao tác → có file ảnh mới cạnh file gốc. Nhu cầu chính:

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
| Project | `src/ConvertAnything.Image` tham chiếu `ConvertAnything.Core` | Giữ Core không phụ thuộc thư viện ảnh |

Đã cân nhắc NetVips (libvips, LGPL-3.0): nhanh và ít RAM hơn, nhưng bản native Windows không kèm decoder HEIC (HEVC). Magick.NET thắng vì HEIC là nhu cầu số 1.

## Thao tác

Định dạng nguồn: `.jpg .jpeg .png .webp .heic .heif .avif .gif .bmp .tif .tiff`.

| op-id | Menu | Áp dụng cho | Output |
|---|---|---|---|
| `image.to-jpg` | Convert/JPG | mọi nguồn trừ `.jpg .jpeg` | `tên.jpg` (quality 92) |
| `image.to-png` | Convert/PNG | mọi nguồn trừ `.png` | `tên.png` |
| `image.to-webp` | Convert/WEBP | mọi nguồn trừ `.webp` | `tên.webp` (quality 90) |
| `image.to-avif` | Convert/AVIF | mọi nguồn trừ `.avif` | `tên.avif` (quality 80) |
| `image.to-gif` | Convert/GIF | mọi nguồn trừ `.gif` | `tên.gif` |
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
- **Ảnh động** (GIF/WebP nhiều frame): giữ animation khi đích là GIF hoặc WebP; đích khác lấy frame đầu. **TIFF nhiều trang** không phải animation: Convert/Compress lấy trang đầu, Remove metadata giữ đủ trang. Chỉ giải mã frame đầu khi kết quả là một ảnh; animation có frame × canvas vượt 400 MP bị từ chối trước khi mở rộng.
- **File sai đuôi** (WebP/PNG lưu thành `.jpg`): module tự đọc chữ ký đầu file và chọn bộ giải mã tương ứng, chỉ trong danh sách cho phép; ImageMagick không bao giờ tự đoán. Nội dung không khớp chữ ký nào (script) được giải mã theo đuôi và lỗi.
- **CMYK** (ảnh từ in ấn): Convert, Compress, Under N chuyển sang sRGB qua profile CMYK của ảnh (hoặc profile in phổ biến nếu không có). Remove metadata giữ nguyên.
- **Độ sâu màu:** dùng Magick.NET Q8, nên ảnh 16-bit/kênh (PNG, TIFF) ra 8-bit. **Trade-off chấp nhận:** Q8 nhẹ và nhanh hơn Q16; khác biệt 16 → 8 bit không thấy bằng mắt, nhưng "lossless" của PNG Light/Balanced chỉ đúng với ảnh 8-bit.
- **ICC** được giữ nguyên từng byte; profile có thể chứa tag riêng của người tạo (hiếm). Ghi nhận, không xử lý.
- **Hủy:** mọi thao tác dừng giữa chừng khi người dùng bấm Cancel (qua callback tiến độ của ImageMagick) và không để lại file tạm.
- **Policy:** ImageMagick còn nạp `policy.xml`/`delegates.xml` từ thư mục làm việc, `MAGICK_HOME`, `MAGICK_CONFIGURE_PATH`, `%LOCALAPPDATA%\ImageMagick`, thư mục cấu hình XDG/HOME, và áp dụng sau policy của ta (có thể đổi temp path, bật delegate). Vì vậy: nạp với thư mục làm việc là thư mục cấu hình riêng; có file cấu hình lạ ở các thư mục kia thì không xử lý ảnh nào; sau khi nạp còn đọc thử MVG/MSL/TEXT và phải bị chính luật coder từ chối.
- **Trong suốt**: đích không có alpha (JPG, BMP) thì nền trắng.
- **Compress**:
  - JPG/WebP/AVIF: Light = quality 85, Balanced = 75, Strong = 60 + thu cạnh dài về tối đa 2560 px.
  - PNG: Light/Balanced = nén lossless mức cao nhất; Strong = giảm còn 256 màu.
  - GIF/BMP/TIFF: ghi ra JPG theo mức tương ứng (BMP/TIFF thường không nén; GIF giữ nguyên nếu là ảnh động).
  - Xóa metadata trừ profile màu ICC.
  - Nếu file nén **không nhỏ hơn** file gốc (JPG đã nén sẵn ở quality ~70, PNG đã tối ưu, GIF động không có metadata) thì **báo lỗi, không ghi file**: một bản "compressed" to hơn sẽ đánh lừa người dùng. (Thay quy tắc cũ "vẫn ghi + log not smaller", theo review module.)
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

## Commands

Như `core`. Thêm: test fixture HEIC nhỏ trong `tests/ConvertAnything.Image.Tests/fixtures/` (lấy từ nguồn có giấy phép cho phép phân phối, ghi rõ nguồn trong `fixtures/README.md`).

## Project Structure

```
src/ConvertAnything.Image/
  ImageOperations.cs     → danh sách IOperation của module
  ImageConvert.cs        → Convert/*
  ImageCompress.cs       → Compress/* (mức + dưới N)
  ImageStripMetadata.cs  → Remove metadata
  MagickSetup.cs         → ResourceLimits + coder policy, gọi một lần
tests/ConvertAnything.Image.Tests/
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

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
- **Ảnh động** (GIF/WebP nhiều frame): giữ animation khi đích là GIF hoặc WebP; đích khác lấy frame đầu.
- **Trong suốt**: đích không có alpha (JPG, BMP) thì nền trắng.
- **Compress**:
  - JPG/WebP/AVIF: Light = quality 85, Balanced = 75, Strong = 60 + thu cạnh dài về tối đa 2560 px.
  - PNG: Light/Balanced = nén lossless mức cao nhất; Strong = giảm còn 256 màu.
  - GIF/BMP/TIFF: ghi ra JPG theo mức tương ứng (BMP/TIFF thường không nén; GIF giữ nguyên nếu là ảnh động).
  - Xóa metadata trừ profile màu ICC.
  - Nếu file nén **không nhỏ hơn** file gốc thì vẫn ghi, nhưng báo trong log `not smaller`.
- **Under N**: luôn ra JPG. Tìm nhị phân quality 40–92; nếu quality 40 vẫn quá lớn thì thu kích thước 15% mỗi bước rồi tìm lại. Kết quả phải < N (1 MB = 1 000 000 byte cho dễ hiểu với người dùng).
- **Remove metadata**: xóa EXIF, GPS, XMP, IPTC, comment; giữ ICC; giữ định dạng (trừ HEIC → JPG).
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
3. Compress Balanced trên ảnh chụp 12 MP JPG cho file nhỏ hơn ≥ 40%.
4. Under 1 MB cho kết quả < 1 000 000 byte với ảnh 12 MP nhiễu.
5. Remove metadata: output không còn GPS/EXIF (kiểm bằng Magick.NET đọc lại), vẫn còn ICC.
6. GIF động → WebP vẫn động (số frame bằng nhau).
7. Ảnh "bomb" bị từ chối < 2 giây, RAM tiến trình < 500 MB.
8. Menu chỉ hiện Convert sang định dạng khác định dạng nguồn.
9. `dotnet test` + `dotnet format --verify-no-changes` qua.

## Open Questions

Không còn (người dùng ủy quyền tự quyết).

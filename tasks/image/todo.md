# Todo: image

Spec: [SPEC-image.md](../../SPEC-image.md) · Plan: [plan.md](plan.md)

Mỗi task xong khi đạt acceptance criteria **và** Definition of Done.

## Task 1: Project + Magick.NET + MagickSetup
- Acceptance:
  - [x] `src/ConvertAnything.Image` tham chiếu Core + Magick.NET-Q8-x64 15.0.0; `tests/ConvertAnything.Image.Tests`
  - [x] Ảnh khai báo 100 000 × 100 000 px bị từ chối nhanh (< 2 s), không cấp phát lớn
  - [x] File `.jpg` có nội dung MVG/MSL/TEXT bị từ chối (không đọc file khác)
- Verify: `dotnet test --filter MagickSetup`
- Files: `src/ConvertAnything.Image/*.csproj`, `MagickSetup.cs`, tests
- Scope: M

## Task 2: Lát dọc JPG → PNG
- Acceptance:
  - [x] `image.to-png` trên JPG có EXIF orientation 6 ra PNG đã xoay
  - [x] Catalog `ca.exe` có thao tác; menu `.jpg` hiện `Convert > PNG`, menu `.png` không có `Convert > PNG`
  - [x] Bấm thật qua MenuProbe ra PNG
- Verify: `dotnet test`; MenuProbe
- Files: `ImageOperation.cs`, `ImageOperations.cs`, `Program.cs`, tests
- Scope: M

## Task 3: Ma trận convert
- Acceptance:
  - [x] Mọi nguồn × đích trong spec chuyển được, đọc lại đúng định dạng (magic bytes)
  - [x] PNG có alpha → JPG/BMP nền trắng
  - [x] GIF động → WebP / GIF giữ số frame; → PNG lấy frame đầu
  - [x] HEIC fixture → JPG đúng kích thước
- Verify: `dotnet test --filter Convert`
- Scope: M

## Task 4: Compress 3 mức
- Acceptance:
  - [x] Balanced trên JPG 12 MP nhỏ hơn ≥ 40%
  - [x] PNG Strong giảm còn ≤ 256 màu; PNG Light/Balanced lossless (so pixel)
  - [x] HEIC → JPG; metadata trừ ICC bị xóa
- Verify: `dotnet test --filter Compress`
- Scope: M

## Task 5: Compress dưới N
- Acceptance:
  - [x] Ảnh nhiễu 12 MP → < 1 000 000 byte; < 500 000 byte
  - [x] Ảnh đã nhỏ hơn N vẫn ra JPG hợp lệ (không phóng to)
- Verify: `dotnet test --filter Under`
- Scope: S

## Task 6: Remove metadata
- Acceptance:
  - [ ] Output không còn EXIF/GPS/XMP/IPTC; còn ICC; xoay đúng
- Verify: `dotnet test --filter Metadata`
- Scope: S

## Task 7: Review và tài liệu
- Acceptance:
  - [ ] 3 reviewer bối cảnh trống; không còn Critical/High/Required
  - [ ] README liệt kê thao tác ảnh; ADR chọn Magick.NET
- Scope: M

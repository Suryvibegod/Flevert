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
  - [x] Output không còn EXIF/GPS/XMP/IPTC; còn ICC; xoay đúng
- Verify: `dotnet test --filter Metadata`
- Scope: S

## Task 7: Review và tài liệu
- Acceptance:
  - [ ] 3 reviewer bối cảnh trống; không còn Critical/High/Required
  - [x] README liệt kê thao tác ảnh; ADR chọn Magick.NET (ADR-0004)
- Scope: M

### Review vòng 1 (2026-10-19): đối chiếu

CR = code-reviewer, SA = security-auditor, DD = doubt-driven. Không có Critical.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| ImageMagick đọc thêm `policy.xml` ở thư mục làm việc, có thể mở lại coder bị cấm; hiện chỉ an toàn nhờ `SafeStartup` đổi thư mục (SA1) | Required | Hợp lệ | K1 |
| PNG mất ICC: `Strip()` đặt `png:exclude-chunk` gồm iCCP (CR-R1, DD-R1, SA2) | Required | Hợp lệ | K2 |
| Đường dẫn output đưa cho Magick: thư mục có `%d`/`%s` làm ghi sai chỗ, để lại `.partial` (CR-H1) | High | Hợp lệ | K3 |
| Compress có thể làm file to hơn mà không báo (DD-H1) | High | Hợp lệ | K4: không nhỏ hơn thì báo lỗi, không ghi file (thay quy tắc "vẫn ghi + log") |
| Remove metadata ghi lại WebP/AVIF ở quality mặc định, mất chất lượng (DD-H2) | High | Hợp lệ | K5 |
| Under N quá 120 s với ảnh rất lớn; thu nhỏ dồn từ ảnh đã thu (DD-H3, SA4, CR-L2) | High | Hợp lệ | K6 |
| Không nghe token hủy trong lúc xử lý ảnh (CR-H2) | High | Hợp lệ phần module; phần tiến trình thoát để lại `.partial` là trade-off đã chấp nhận ở ADR-0002 | K7 |
| PNG 16-bit thành 8-bit (Q8) nên Light/Balanced không lossless tuyệt đối (DD-R2, CR-L4) | Required | Trade-off chấp nhận: dùng Q8 (nhẹ, nhanh); ghi SPEC | K12 |
| Balanced ≥ 40% chỉ đúng với nguồn q ≥ 90; test không thực tế; nên dùng 4:2:0 (DD-M1, DD-L2) | Medium | Hợp lệ | K8 |
| CMYK chuyển sang RGB bỏ qua ICC; profile CMYK lớn chiếm ngân sách Under N (DD-M2, DD-M4) | Medium | Hợp lệ | K9 |
| TIFF nhiều trang: Remove metadata mất trang; Convert sang GIF/WebP thành "animation" (DD-M3, CR-R2, CR-M1) | Required | Hợp lệ | K10 |
| Giải mã mọi frame dù chỉ cần frame đầu; Coalesce khuếch đại (SA3, CR-M2) | Medium | Hợp lệ | K10 |
| File sai đuôi (WebP/PNG đặt tên .jpg) luôn lỗi (CR-M3) | Medium | Quyết định: tự dò magic bytes, chỉ trong danh sách cho phép, Magick vẫn không tự đoán | K11 |
| Chưa có test HEIC xoay (CR-M4) | Medium | Hợp lệ (DD đã kiểm tay: đúng) | K11 nếu có fixture |
| HEIC có alpha → Remove metadata ra JPG không nền trắng (CR-L1) | Low | Hợp lệ | K9 |
| Op-id suy từ chữ hiển thị (CR-L3) | Low | Hợp lệ | K12 |
| Thư mục magick thiếu nhãn no-read-up (SA5) | Low | Hợp lệ | K1 |
| TIFF: bỏ qua layer Photoshop (SA7) | Low | Hợp lệ (phòng thủ) | K1 |
| Under N trên file đã nhỏ có thể to lên nhưng vẫn < N (DD-L1) | Low | Chấp nhận (hợp đồng vẫn đúng) | ghi SPEC |
| ICC có thể chứa tag riêng (SA6) | Low | Chấp nhận, ghi SPEC | K12 |
| Thiếu test giới hạn area 400 MP, RAM < 500 MB (SA8, CR) | Low | Ghi lại, làm khi có fixture nhỏ | ghi chú |
| Nits: ngoặc điều kiện, helper nền trắng lặp 3 lần, hình dạng ImageCompress | Nit | Hợp lệ | K9 |

### Việc
- [x] K1: Kiểm tra policy tự khóa (fail closed) sau Initialize; nhãn no-read-up; `tiff:ignore-layers`
- [x] K2: Giữ iCCP khi strip PNG; test ICC cho PNG
- [x] K3: Đọc/ghi qua FileStream, không đưa đường dẫn cho Magick; test thư mục `x %d`, `p%s q`
- [x] K4: Compress không nhỏ hơn → lỗi rõ ràng, không file
- [x] K5: Remove metadata giữ chất lượng (JPEG giữ quality ước lượng; WebP/AVIF quality cao)
- [x] K6: Under N: ước lượng tỉ lệ thu nhỏ, luôn thu từ ảnh gốc, chặn trên số pixel; test ảnh lớn trong thời gian giới hạn
- [x] K7: Nghe token giữa các bước và qua sự kiện Progress
- [x] K8: Test Balanced bằng ảnh kiểu chụp q90 4:2:0; 4:2:0 cho Balanced/Strong/Under N; thu hẹp tuyên bố trong SPEC
- [x] K9: CMYK → sRGB theo ICC; helper nền trắng dùng chung (cả Remove metadata HEIC)
- [x] K10: Chỉ đọc frame đầu khi đích một frame; chỉ giữ animation khi nguồn là GIF/WebP động; TIFF nhiều trang giữ đủ trang khi Remove metadata
- [x] K11: Đọc theo magic bytes trong danh sách cho phép; (tùy) fixture HEIC xoay
- [x] K12: Op-id cố định; SPEC: Q8, ICC, Under N với file nhỏ, TIFF nhiều trang
- [ ] Review vòng 2 module image

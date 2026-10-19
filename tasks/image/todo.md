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
  - [x] 3 reviewer bối cảnh trống; không còn Critical/High/Required (vòng 5)
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

### Review vòng 2 (2026-10-19): đối chiếu

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| Callback tiến độ trỏ vào delegate đã bị GC sau Coalesce/Optimize, tiến trình chết (CR-C1, DD1) | Critical | Hợp lệ, do K7 gây ra | Đã sửa `eb5bd6c` |
| Remove metadata đổi chroma subsampling JPEG (xóa thuộc tính `jpeg:sampling-factor`): ảnh điện thoại to thêm 8–16% (CR-R1, DD3) | Required | Hợp lệ, do K2 gây ra | L1 |
| Policy lạ (thư mục làm việc, MAGICK_HOME, XDG/LOCALAPPDATA ImageMagick) vẫn qua kiểm tra 3 coder, đổi được temp path, delegate (SA-M1) | Medium | Hợp lệ; lời "fail closed" trong SPEC/ADR nói quá | L2 |
| Remove metadata ghi TIFF không nén (2 KB → 2 MB; trang lớn → 750 MB) (SA-M2) | Medium | Hợp lệ | L3 |
| WebP động lossless không được nhận ra (VP8L nằm trong ANMF) (CR-M1, DD4) | Medium | Hợp lệ | L4 |
| File sai đuôi: đọc đúng nhưng định dạng output vẫn theo đuôi; làm phẳng nền theo đuôi (DD5, CR-L1) | Medium | Hợp lệ | L5 |
| Hủy không kịp với AVIF (encode/decode không báo tiến độ); để lại `.partial` 0 byte (DD2) | Medium | Hợp lệ phần file tạm; AVIF/HEIC không ngắt được là giới hạn thư viện | L6 |
| Từ chối "không nhỏ hơn" hiện thành "failed" kèm stack trace; ngưỡng 1 byte (DD6) | Medium | Hợp lệ | L7: kết quả "skipped" ở core + ngưỡng 5% |
| Compress TIFF nhiều trang so JPEG trang 1 với cả TIFF, âm thầm bỏ trang (DD7) | Low/Medium | Hợp lệ | L8: bỏ qua có thông báo |
| Mọi file `ftyp` (MP4, MOV) đều được đưa cho libheif (CR-L2) | Low | Hợp lệ | L9 |
| ICC ghi hai lần trong PNG (iCCP + zTXt raw) (DD8, SA) | Low | Chấp nhận: không lộ thêm gì, vài KB | ghi SPEC |
| Under N giữ độ phân giải, quality thấp (q43–51) (DD9) | Low | Chấp nhận: hợp đồng vẫn đúng | — |
| Remove metadata làm WebP/AVIF lossy to hơn (q95) (DD10) | Low | Chấp nhận (đánh đổi để giữ chất lượng) | ghi SPEC |
| Ghi nhiều frame giữ dữ liệu hai lần trong RAM; 400 MP đỉnh ~3 GB (CR-L3, SA) | Low | Chấp nhận, ghi SPEC | ghi SPEC |
| HEIC thật nén Light thường bị từ chối (CR-L4) | Low | Chấp nhận (thành "skipped" sau L7), ghi SPEC | ghi SPEC |
| CMYK dùng profile do file cung cấp → lcms (SA) | Low | Chấp nhận (lcms giới hạn cấp phát) | — |
| Không có test âm cho VerifyPolicy (CR-L5) | Low | Hợp lệ | L2 (test quét thư mục cấu hình) |
| Nits: doc "never sniffed", `Read` có thể đọc thiếu byte | Nit | Hợp lệ | L9 |

### Việc vòng 2
- [x] Sửa crash GC (`eb5bd6c`)
- [x] L1: Giữ `jpeg:sampling-factor` của nguồn khi Remove metadata
- [x] L2: Khởi tạo Magick với thư mục làm việc là thư mục cấu hình riêng; từ chối nếu có `policy.xml`/`delegates.xml` ở thư mục tìm kiếm của ImageMagick; probe kiểm đúng lỗi policy; sửa lời trong SPEC/ADR
- [x] L3: Giữ kiểu nén TIFF; chặn tổng diện tích khi ghi nhiều trang
- [x] L4: Nhận WebP động lossless (ANMF)
- [x] L5: Định dạng nguồn theo chữ ký thật cho mọi thao tác
- [x] L6: Mã hóa vào bộ nhớ rồi mới tạo file tạm
- [x] L7: Kết quả "skipped" (core) cho "không nhỏ hơn đáng kể" (< 5%)
- [x] L8: Compress TIFF nhiều trang → skipped có thông báo
- [x] L9: Chỉ nhận brand HEIF/AVIF; nits
- [ ] Review vòng 3 module image

### Review vòng 3 (2026-10-19): đối chiếu

Code-reviewer và security-auditor: không Critical/High/Required; đều kết luận "ship / approve with fixes". Doubt-driven: chờ kết quả.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| Remove metadata ghi lại TIFF nén JPEG ở quality ~75 (reader báo 0) (CR-M1) | Medium | Hợp lệ, do L3 gây ra | `05b2440` |
| Convert > TIFF không nén (8 KB → 8 MB) (SA-M2) | Medium | Hợp lệ | `05b2440` (Zip) |
| Giới hạn 400 MP tổng trang chặn bản scan nhiều trang bình thường (CR-M2) | Medium | Hợp lệ, do L3 gây ra | `8c04d07`: chỉ chặn khi ghi không nén |
| File cấu hình khác (colors.xml, type.xml) và APPDATA/USERPROFILE chưa được kiểm; thư mục cấu hình có thể thành tương đối (SA-M1, SA-L1/L2, CR-L1) | Medium | Hợp lệ | `236921b`; SPEC/ADR nói rõ giới hạn thư mục làm việc |
| Compress giải mã mọi trang TIFF chỉ để đếm; TIFF nhiều trang là "skipped" (CR-L2, SA-L5, core vòng 8) | Low | Hợp lệ | `8c04d07`: đọc tối đa 2 trang, thành lỗi |
| 4:2:2 không xoay theo ảnh (CR-L3) | Low | Hợp lệ | `b67b828` |
| `RealFormat` chạy trên luồng gọi (CR-L5) | Low | Hợp lệ | `b67b828` |
| Đổi thư mục làm việc ảnh hưởng cả tiến trình (CR-L4, SA-L3) | Low | Chấp nhận: ca.exe chỉ dùng đường dẫn tuyệt đối; ghi chú trong code | — |
| Test "không có file tạm khi đang mã hóa" dựa vào thời gian (CR-L6) | Low | Chấp nhận, có kiểm đối chứng "vẫn đang mã hóa" | — |
| Ghi nhiều trang dùng cài đặt nén của trang đầu (CR-L7) | Low | Chấp nhận, đã ghi chú trong code | — |
| Nits: comment MOV/.heic, brand mif2, thông báo "pages or frames" | Nit | Hợp lệ một phần | thông báo đã sửa |

Doubt-driven vòng 3 (đến sau): 1 High, còn lại Medium/Low; không crash nào.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| Remove metadata làm hỏng WebP động kiểu gif2webp (frame trộn alpha bị ghi không trộn → gần trong suốt) | High | Hợp lệ (Pillow xác nhận) | `98224dd`: mở rộng frame trước khi ghi; fixture tự sinh + test đọc cờ ANMF |
| Under N âm thầm bỏ trang TIFF | Medium | Hợp lệ | báo lỗi như Compress |
| Compress GIF động "already well compressed" là sai | Medium | Hợp lệ | báo lỗi kèm gợi ý Convert > WEBP |
| AVIF đặt tên .heic bị coi là HEIC | Low | Hợp lệ | brand cụ thể thắng đuôi; chỉ brand chung (mif1) theo đuôi |
| HEIC/AVIF → JPG ghi 4:4:4 (to 144%) | Low | Hợp lệ | 4:2:0 |
| Ngưỡng 5% với PNG lossless | Low | Chấp nhận, ghi SPEC | — |
| Hủy giữa AutoOrient ra ObjectDisposedException | Low | Hợp lệ | chuyển thành hủy |
| SPEC lỗi thời (skip, TIFF) | Low | Hợp lệ | đã cập nhật |
| WebP động trộn lossy/lossless thành một kiểu | Low | Chấp nhận (hiếm) | — |

### Review vòng 4 (2026-10-19): đối chiếu

1 reviewer, 3 góc, trên `ef7ef4f..HEAD`. 1 High, 2 Medium, không Critical; mọi bản sửa vòng 3 kiểm lại đều đứng (test hỏng khi gỡ fix).

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| TIFF nhiều trang trộn kiểu nén: Remove metadata dùng kiểu nén trang đầu cho mọi trang → trang màu thành trắng đen (bằng chứng mới cho CR-L7 vòng 3) | High | Hợp lệ | một kiểu nén chung; khác nhau thì Zip; fixture `mixed-g4-colour.tif` |
| Ảnh thu nhỏ trong TIFF tính là trang 2 → Compress/Under N từ chối | Medium | Hợp lệ | `PageCount` bỏ qua REDUCEDIMAGE; fixture `with-thumbnail.tif` |
| `FitsInMemory` bị vượt: nén không bảo đảm output nhỏ (2,2 MB → 435 MB) | Medium | Hợp lệ | chặn theo kích thước thô tệ nhất 1 GB |
| JPEG-in-TIFF q95 làm to bản scan chất lượng thấp | Low | Chấp nhận, ghi SPEC | — |
| WebP lossy → JPG ra 4:4:4 | Low | Hợp lệ | 4:2:0 cho nguồn ảnh chụp lossy |
| Ép 4:2:0 với AVIF 4:4:4 | Low | Chấp nhận (hiếm, ảnh chụp là chủ yếu) | — |
| WebP động lớn giờ bị giới hạn frame × canvas khi Remove metadata | Low | Chấp nhận, ghi SPEC | — |
| Thiếu test: ánh xạ ObjectDisposedException, `Write` gọi `FitsInMemory`, WebP động trong suốt | Low | Ghi nhận | — |
| Thư mục cấu hình tương đối được tạo trước khi bị từ chối | Low | Hợp lệ | kiểm trước khi tạo |
| Biến môi trường LOCALAPPDATA/APPDATA khác known folder | Low | Chấp nhận (cần môi trường đã bị sửa) | — |
| README fixtures hỏng mã "×", BOM | Nit | Hợp lệ | đã sửa |

- [x] Review vòng 5 module image: **không Critical/High/Required** → đạt checkpoint Task 7

### Review vòng 5 (2026-10-19): Medium đã sửa

| Finding | Mức | Xử lý |
|---|---|---|
| Thumbnail nằm trước ảnh → Compress/Under N xử lý thumbnail; thumbnail xen giữa trang → mất trang 2 (hồi quy) | Medium | `ReadFirstPage`: đọc từng ảnh, bỏ thumbnail, gặp trang thật thứ hai thì lỗi |
| Remove metadata giữ thumbnail (thành trang, lộ ảnh trước khi sửa, ép kiểu nén) | Medium | bỏ thumbnail trước khi chọn kiểu nén |
| Giới hạn thô thấp 3 lần với Group 4 (TIFF 1,5 MB → 450 MB) | Medium | nhân hệ số phình theo kiểu nén (G4/fax ×3, LZW ×1,4) |
| Giới hạn thô áp cả GIF/WebP động (chặn trước giới hạn 400 MP) | Medium | chỉ áp cho TIFF |
| Test giới hạn chỉ kiểm ngưỡng | Low | thêm test `RawBytes` trên trang Group 4 thật |
| Subfile type 3/4, palette tính 4 byte/pixel | Low | chấp nhận (ước lượng dư, an toàn) |
| README fixtures, cảnh báo của script sinh | Nit | README sửa; cảnh báo là của Pillow, file đúng |

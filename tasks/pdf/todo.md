# Todo: pdf (v0.2.0)

Plan: [plan.md](plan.md). Spec: [docs/specs/pdf.md](../../docs/specs/pdf.md).

Mỗi task chỉ xong khi đạt tiêu chí nghiệm thu **và** Definition of Done: test viết trước và thấy đỏ; build, `dotnet format --verify-no-changes`, hai project test (cộng `Flevert.Pdf.Tests` từ T3) xanh; chạy thật; tài liệu cập nhật; commit nguyên tử, message nói vì sao.

Lệnh chung: `powershell -File scripts/fetch-deps.ps1`, `dotnet build Flevert.sln`, `dotnet test tests/Flevert.Core.Tests`, `dotnet test tests/Flevert.Image.Tests`, `dotnet test tests/Flevert.Pdf.Tests`.

## Giai đoạn 1: nền

### T1: Tải deps ghim theo hash
Tải pdfium và qpdf (cùng giấy phép của chúng) vào `deps/` theo một manifest có SHA256; CI dùng chung script.
- [x] `scripts/deps.json`: pdfium `chromium/8100` `pdfium-win-x64.tgz`, qpdf `v12.4.2` `qpdf-12.4.2-msvc64.zip`, `LICENSE.txt` + `NOTICE.md` của qpdf ở tag `v12.4.2`; mỗi mục URL + SHA256 + file cần giữ
- [x] `fetch-deps.ps1`: tải vào thư mục tạm mới, kiểm hash **trước** khi giải nén, chỉ giữ file liệt kê (`pdfium.dll`, `licenses/*`, `LICENSE`; `qpdf.exe`, `qpdf30.dll`, DLL runtime MSVC); chạy lại khi đã đủ thì không tải; hash sai → lỗi, `deps/` không đổi
- [x] CI (`ci.yml`, `release.yml`) và setup-dev gọi script (không cache: tải vài giây, kiểm hash mỗi lần); ADR-0008
- Kiểm: chạy hai lần (lần 2 không tải); sửa một ký tự hash → thất bại rõ ràng (đối chứng âm); CI xanh
- Phụ thuộc: không. File: `scripts/deps.json`, `scripts/fetch-deps.ps1`, `.github/workflows/*.yml`, `docs/adr/0008-…`. Cỡ: M

### T2: `ExternalTool` trong core
Chạy công cụ ngoài an toàn: đường dẫn tuyệt đối, `ArgumentList`, stdin, đọc output có giới hạn, timeout, hủy giết cả cây, job object để con chết theo Flevert.
- [x] Trả mã thoát, stdout/stderr (tối đa 1 MB mỗi luồng, phần thừa bỏ); stdin là byte do caller đưa (cho mật khẩu), đóng ngay sau khi ghi
- [x] Hủy hoặc quá timeout → tiến trình và con của nó chết trong ≤ 2 s; tiến trình Flevert chết → con chết (job object `KILL_ON_JOB_CLOSE`)
- [x] Không bao giờ chạy qua shell; đường dẫn phải tuyệt đối và tồn tại
- Kiểm: test dùng `cmd.exe`/`ping` của hệ thống: echo stdin, output 5 MB bị cắt, timeout, hủy, cây con bị giết; giết Flevert test-host con → không còn tiến trình mồ côi
- Đã làm: 18 test trong `ExternalToolTests` (≈1 s), đỏ với stub rồi xanh; thử đột biến (bỏ giết tiến trình sót, bỏ `KILL_ON_JOB_CLOSE`, đọc stdout trước stderr) thì test tương ứng đỏ. "Flevert chết" được kiểm bằng cách đóng handle job (Windows làm đúng việc đó khi tiến trình chết); kiểm bằng `flevert.exe` thật để lại cho T3, khi đã có thao tác gọi qpdf. Job theo từng lần gọi; khi tool thoát, thứ nó để lại cũng bị kết thúc. Gán vào job ngay sau `Process.Start` (không khởi động treo được qua `Process`), khe hở nhỏ được bù bằng `Kill(entireProcessTree)` khi hủy/timeout. Chỉ chạy `.exe` (`.bat`/`.cmd` đi qua cmd).
- Phụ thuộc: không. File: `src/Flevert.Core/ExternalTool.cs`, `tests/Flevert.Core.Tests/ExternalToolTests.cs`. Cỡ: M

### T3: Lát đầu: xoay PDF (`pdf.rotate-right`/`-left`/`-180`)
Project `Flevert.Pdf`, wrapper `Qpdf`, ba thao tác xoay, `.pdf` vào menu.
- [x] `src/Flevert.Pdf` + `tests/Flevert.Pdf.Tests` (thêm vào sln, CI, release, setup-dev, tài liệu lệnh test); `Qpdf` tìm `qpdf.exe` cạnh `flevert.exe` (bản build: `deps/qpdf`)
- [x] Output `<tên> (rotated).pdf`, mọi trang xoay đúng góc (đọc lại bằng `qpdf --json`); file gốc không đổi; PDF có mật khẩu → lỗi "This PDF needs a password: use Remove password first"
- [x] Fixture: `make_pdf_fixtures.py` (thư viện chuẩn) + README nguồn gốc
- Kiểm: test đỏ trước; `flevert.exe run pdf.rotate-right` thật; `install-menu --dev --allow-insecure-location` → MenuProbe thấy `Flevert > Rotate > 90° right` trên `.pdf`; `uninstall-menu`
- Đã làm (2026-10-20): `Qpdf` (`TransformAsync`, `NextToApp`, `TimeoutFor`), `PdfRotate`, `PdfOperations.All` (đăng ký trong `Program.cs`; `Flevert.Cli` tham chiếu `Flevert.Pdf`). csproj của Flevert.Pdf chép `deps/qpdf/bin/*` vào output của mọi project tham chiếu nó (như `pdfium.dll`), thiếu thì build báo "run fetch-deps"; `dotnet publish` cũng có đủ. 28 test đỏ với stub, rồi xanh; thêm 2 test cho tùy chọn có ký tự đại diện (đỏ trước), tổng 61/61 xanh. Phát hiện khi chạy test đường dẫn dài: C runtime của qpdf **mở rộng ký tự đại diện** trong đối số dòng lệnh, nên `\\?\C:\…\x.pdf` thành `\\x.pdf`, và qpdf không mở được đường dẫn > 260 ký tự nếu thiếu `\\?\` (cả `\\.\`): hai đường dẫn đi qua stdin bằng `@-` (dòng đọc từ đó không bị mở rộng), tùy chọn vẫn trên dòng lệnh; `--` không phải dấu kết thúc tùy chọn của qpdf, nên đường dẫn bắt buộc tuyệt đối, không xuống dòng. Mã thoát 3 (cảnh báo, ví dụ trang thiếu `/Resources`) = xong; mã 2 → hỏi `--requires-password` (mã thoát có tài liệu) → thông báo mật khẩu, stderr nhắc output → `IOException`, còn lại "not a PDF, or damaged" với chi tiết qpdf trong log. `MaxParallel = 4` (lý do trong spec mục 8 và `PdfRotate`). Test tiến trình thật: `flevert.exe run pdf.rotate-right` headless; giết `flevert.exe` (không giết cây) khi qpdf đang chạy PDF 500 000 trang → qpdf chết trong ≤ 3 s (đối chứng âm: bỏ `KILL_ON_JOB_CLOSE` thì test đỏ; bản đầu với 200 000 trang và chờ 5 s vẫn xanh khi bỏ cờ, vì qpdf kịp xong: đã sửa). MenuProbe: `.pdf` → `Flevert > Rotate > 90° right / 90° left / 180°`, `.jpg` không có mục PDF; `invoke "Flevert/Rotate/90° right"` (với `FLEVERT_HEADLESS=1` để không hiện cửa sổ) → `Hóa đơn (rotated).pdf`, `/Rotate` 0,90,180,270 → 90,180,270,0; đã `uninstall-menu`. Khởi động tiến trình phụ (R2R, `measure-startup.ps1`, 3 lượt × 15): trung vị 71–75 ms không có catalog PDF, 74–76 ms có (≈ +3%, dưới ngưỡng 10%). Chưa giới hạn số trang cho thao tác qpdf (T18).
- Phụ thuộc: T1, T2. Cỡ: M

### T4: Đóng gói qpdf và pdfium
- [ ] `build-installer.ps1` chép `deps/qpdf/bin/*` cần thiết; `THIRD-PARTY-NOTICES.txt` có qpdf (LICENSE + NOTICE), OpenSSL, zlib, libjpeg-turbo, MSVC runtime; thiếu → build lỗi
- [ ] Check-location của setup không giải nén qpdf vào thư mục tạm
- [ ] `test-installer.ps1`: có `qpdf.exe`, notices đủ; xoay một PDF bằng bản đã cài; giới hạn kích thước 80 MB; gỡ sạch
- Kiểm: build bộ cài, `test-installer.ps1` PASS, ghi kích thước bộ cài vào todo
- Lưu ý từ T3: `Flevert.iss` gói cả thư mục publish (`{#SourceDir}\*`), nên từ T3 bộ cài **đã** chứa `qpdf.exe`, `qpdf30.dll`, DLL runtime MSVC và `pdfium.dll`, nhưng `THIRD-PARTY-NOTICES.txt` chưa có giấy phép của chúng: không phát hành trước khi T4 xong.
- Phụ thuộc: T3. Cỡ: S

### Checkpoint 1
- [ ] Mọi test xanh, CI xanh, bộ cài chạy xoay PDF thật
- [x] Review ba góc (code, bảo mật, doubt) cho T1–T3 (gộp vào review vòng 1 dưới đây, phạm vi T1–T10); T4 review khi xong

## Review vòng 1 (T1, T2, T3, T5, T7, T9, T10; `v0.1.0..0888ccb`, 2026-10-20): reconcile

Ba reviewer bối cảnh trống, chỉ đọc. Ký hiệu: CR = code-reviewer, SA = security-auditor, DD = doubt-driven. Kết luận: cả ba "request changes"; không làm thêm thao tác đọc PDF trước khi xong F1–F5 và quyết định F6.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| CR-1, SA-H1, DD-L2: `DeleteTree` không kiểm gốc `.partial`; người khác đổi tên thư mục tạm rồi đặt junction cùng tên giữa hai trang → dọn dẹp xóa nội dung đích (Documents của nạn nhân); `finally` còn xóa tên GUID vừa trống sau khi move | High, Required | Hợp lệ | **F1** |
| CR-3, DD-H1: đổi tên thư mục thất bại (access denied) khi Defender/indexer/Explorer đang mở một file bên trong; chỉ thử lại khi trùng tên → mất hết trang đã render | High/Medium, Required | Hợp lệ (DD đã probe) | **F1**: thử lại có giới hạn khi access denied/sharing violation; thư mục tạm Hidden + NotContentIndexed |
| DD-M1: "không để lại gì" chỉ đúng khi Cancel, không đúng khi crash; `.partial` không ẩn; hủy thật phụ thuộc tiến trình còn sống ~6 s | Medium, Required (ghi + sửa rẻ) | Hợp lệ | **F1** (ẩn thư mục tạm); quét `.partial` cũ: Optional, T18 |
| CR nit: thư mục `<tên> (pages)` bị cắt mất hậu tố khi stem 248–251 ký tự | Nit | Hợp lệ | **F1** |
| CR-2: input < 260 ký tự nhưng output tạm ≥ 260 → qpdf không ghi được (runner bỏ `\\?\`) | Medium, Required | Hợp lệ | **F2**: luôn đưa qpdf đường dẫn `\\?\`; test qua `Shorten` |
| SA-M1, DD-L3: tên có surrogate lẻ → `Encoding.UTF8` thay bằng U+FFFD → qpdf mở file khác (vượt ghim) | Medium, Required | Hợp lệ | **F2**: UTF-8 nghiêm ngặt, lỗi rõ |
| CR nit: tùy chọn qpdf bắt đầu bằng `@`; `Detail()` cắt đôi cặp surrogate | Nit | Hợp lệ | **F2** |
| CR-5, SA-L2: qpdf kế thừa `OPENSSL_CONF/MODULES/ENGINES` | Low, Optional | Hợp lệ | **F2**: bỏ các biến này khỏi môi trường của qpdf (rẻ) |
| SA-M2, DD-M3: qpdf không giới hạn bộ nhớ (4 song song, timeout 2 phút) | Medium, Required trước phát hành | Hợp lệ | **F3**: `JOB_OBJECT_LIMIT_PROCESS_MEMORY` + `ACTIVE_PROCESS = 1` tùy chọn theo lần gọi; lỗi cấp phát → "too complex" |
| DD-M2, CR-8: tool thoát trước `AssignProcessToJobObject` → "Access is denied" | Medium/Low, Optional | Hợp lệ (DD đã probe) | **F3**: đã thoát thì bỏ qua lỗi gán, trả kết quả |
| CR-9: reader stdout/stderr không dispose | Low, Optional | Hợp lệ | **F3** |
| DD-H4: không gì chặn kỹ thuật một bản phát hành chứa pdfium/qpdf mà thiếu notices (hotfix 0.1.x từ `main`) | High, Required | Hợp lệ | **F4**: `build-installer.ps1` lỗi khi thư mục publish có binary native không có trong danh sách notices |
| SA-M4: `fetch-deps.ps1` kiểm hash rồi giải nén trong `%TEMP%` mà nhóm sandbox ghi được; `Remove-Item -Recurse` có thể đi theo junction | Medium, Required (máy dev) | Hợp lệ (SA đã `icacls`) | **F4**: thư mục làm việc DACL chỉ chủ/SYSTEM/Admins; xóa không đi theo link |
| CR nit, DD-L4, SA-L3: "up to date" chỉ kiểm thư mục, không kiểm từng file | Low, Optional | Hợp lệ | **F4**: kiểm từng file giữ lại có mặt (hash từng file: Optional) |
| SA-M5: checkout cho `Authenticated Users` quyền sửa (kế thừa từ `D:\`) → tài khoản khác thay `qpdf.exe`/hook git | Medium, Required (môi trường) | Hợp lệ | **F4**: `setup-dev.ps1` cảnh báo và in lệnh `icacls` sửa; người dùng tự chạy (đổi quyền là việc của máy) |
| DD-H3: N→1 chỉ kiểm với `FLEVERT_BATCH_IDLE_MS=3000`; ở cửa sổ 1 s thật, tiến trình đến muộn thành chủ lô mới → PDF thiếu trang, im lặng | High, Required | Hợp lệ | **F5**: cửa sổ rảnh riêng cho N→1 dài hơn; tiến trình đến muộn sau khi lô N→1 cùng thao tác vừa đóng → từ chối có thông báo nêu file, không bao giờ ra output thứ hai im lặng; test ở cửa sổ thật khi máy bận |
| CR-7: lô hoàn tất trong 2 s ân hạn khi hủy → output có mà báo "Cancelled", không log | Low, Optional | Hợp lệ | **F5** |
| CR nit: lô N→1 không nhận file nào báo "Done: 0 files"; hai constructor `BatchInputException` dễ nhầm | Nit | Hợp lệ | **F5** |
| DD-H2, SA-M3, CR-4: pdfium cùng tiến trình: không timeout/hủy trong `FPDF_LoadPage`/`RenderPageBitmap`, bộ nhớ riêng không giới hạn, crash giết chủ lô (file còn lại mất im lặng) → không đạt tiêu chí 6 | High, Required (quyết định trước T11) | Hợp lệ | **F6**: quyết định ADR-0008 (2026-10-20): **đọc PDF không tin cậy chạy trong tiến trình worker** (`flevert.exe` dưới job của `ExternalTool`: giới hạn bộ nhớ, timeout theo trang, hủy = giết); tạo PDF từ ảnh (T11) ở lại trong tiến trình vì input là ảnh đã qua Magick và JPEG chỉ được pdfium đọc header. Task mới **T9b**, trước T15/T17 và trước phát hành |
| CR-10: test vẫn xanh khi code sai: tên dài 240 ký tự không chạm nhánh rút gọn; chất lượng JPEG 90/4:2:0 không kiểm; không chứng minh qpdf thoát mã 3; đường bỏ rơi 2 s khi hủy chưa test | Low, Required | Hợp lệ | **F7** (PdfToImages, PdfRotate); đường bỏ rơi 2 s: **F5** |
| CR nit: `PdfError.NoPages`; mã rút gọn tên trang lặp ở `PdfToImages`/`DevSplit` | Nit | Hợp lệ | **F7** (`NoPages`); gộp mã rút gọn: Optional, làm khi đụng lại |
| DD-M4: Checkpoint 1 bị bỏ qua; T10 chưa chạy thật qua `flevert.exe`/menu; tiến độ "page N of M" chỉ kiểm bằng `dev.split` | Medium, Required (quy trình) | Hợp lệ | Review này thay Checkpoint 1 cho T1–T3; T10 đã chạy thật (ghi ở T10); tiến độ với PDF thật: kiểm ở T9b (worker báo tiến độ) |
| DD-M5: pdfium không có API ghi Info → `Producer: Flevert` cần qpdf hoặc đổi spec | Medium, Required trước T11 | Hợp lệ | Xong ở T11a: giữ spec, không cần qpdf: `PdfBuilder` nạp một document rỗng của Flevert có Info chỉ `Producer` rồi thêm trang (pdfium giữ Info của document đã nạp); test đọc bằng qpdf; ADR-0008 |
| DD-M6: qpdf đọc `@-` tới hết stdin, `--password-file=-` sau đó không dùng được; lỗi của qpdf có thể lặp lại mật khẩu | Medium, Optional (ghi ngay) | Hợp lệ (chưa kiểm) | Ghi chú T14: mật khẩu là dòng `--password=` trong `@-`; test stderr/log không chứa mật khẩu; stdin bằng byte[] xóa sau khi dùng (CR nit) |
| DD-M7: mã thoát 3 của qpdf luôn là thành công; sửa file hỏng có thể mất nội dung | Medium, Optional | Hợp lệ | T18: so số trang (`--show-npages`) hoặc thông báo "repaired" |
| CR-6, DD-L5: DLL runtime MSVC của qpdf nằm cạnh `flevert.exe` | Low, Optional | Hợp lệ | T4: cân nhắc thư mục con `qpdf\` |
| SA-L1: PDF → ảnh có thể làm đầy đĩa (2 000 trang × 100 MP) | Low, Optional | Hợp lệ | T18: giới hạn tổng byte/pixel mỗi file, kiểm chỗ trống |
| SA-L4: output N→1 nằm cạnh file đầu; chọn từ nhiều thư mục có thể ghi PDF riêng tư vào thư mục chung | Low, Optional | Hợp lệ | T6/T11: quyết định trong spec (đề xuất: từ chối khi input ở nhiều thư mục) |
| SA-L5: ghim hash không nói ai build | Low, Optional | Hợp lệ | RELEASING (kiểm hằng tháng): xem provenance upstream khi đổi ghim |
| CR-11, CR-12: trang 10 000 px giữ nhiều hơn mức dùng; buffer mới mỗi trang | Low, Optional | Hợp lệ | T9b (worker tự quản buffer) |
| CR nit: thanh tiến độ một file đứng ở 0 | Nit, Optional | Hợp lệ | T9b (tiến độ trong file nối vào thanh) |
| DD-L1: test `NaturalOrder` chỉ so `StrCmpLogicalW` với chính nó | Low, Optional | Hợp lệ (DD đã probe khớp Explorer trên máy này) | Không đổi; ghi nhận probe |
| DD-L6: tài liệu lệch (bảng HANDOFF, `plan.md` nói CI cache deps, số task, `IProgress<double>` trong spec) | Low, Optional | Hợp lệ | Sửa cùng commit reconcile |
| SA Info: PNG/JPEG trang có thể mang `date:*`/`tIME`; `{tmp}` của Inno trong `%TEMP%` (T4) | Info | Ghi nhận | T18 (exiftool); T4 |

### F1: Ghi thư mục an toàn trước junction và trình quét (Core)
- [ ] Giữ handle thư mục tạm (không `FILE_SHARE_DELETE`) suốt lúc ghi, kiểm qua handle không phải reparse point, đổi tên qua handle; dọn dẹp chỉ khi chưa move, gốc là link thì chỉ gỡ link, không đệ quy
- [ ] Thử lại đổi tên có giới hạn khi access denied/sharing violation (tên đích trống); thư mục tạm Hidden + NotContentIndexed, bỏ Hidden sau khi đổi tên
- [ ] Không cắt hậu tố ` (pages)` khi stem dài
- Kiểm: test thay gốc `.partial` bằng junction rồi lỗi trang → đích không mất gì (đỏ với mã cũ); file bên trong đang mở lúc move → vẫn xong; thuộc tính Hidden lúc ghi
- File: `src/Flevert.Core/OutputPath.cs`, test Core. Cỡ: M

### F2: Đường dẫn và môi trường cho qpdf
- [x] Luôn `\\?\` (`\\?\UNC\`) cho đường dẫn đưa qpdf; UTF-8 nghiêm ngặt (surrogate lẻ → lỗi rõ); tùy chọn bắt đầu bằng `@` bị từ chối; `Detail()` không cắt đôi surrogate; bỏ `OPENSSL_*` khỏi môi trường qpdf
- Kiểm: input ~250 ký tự qua `Shorten` → xoay được (đỏ trước); tên có `\uD800` → lỗi "file name" chứ không mở file khác
- File: `src/Flevert.Pdf/Qpdf.cs`, `ExternalTool` (tham số môi trường). Cỡ: S
- Đã làm (2026-10-20): `Qpdf.ToExtendedPath` (`GetFullPath` trước vì `\\?\` tắt chuẩn hóa; `\\.\` → `\\?\`; UNC → `\\?\UNC\`); test đi qua `Runner` thật với input 250 ký tự (thư mục dựng từ dạng dài của `%TEMP%`, CI có `RUNNER~1`): đỏ "qpdf could not write the output" rồi xanh. Surrogate lẻ: đỏ trước kiểu nguy hiểm thật (có file anh em `scan\uFFFD.pdf` → thao tác xoay **file kia** thành output, không lỗi), sau đó `PdfError.UnsupportedFileName`, qpdf không chạy. `@file` trong tùy chọn: đỏ (qpdf đọc tùy chọn từ file và xoay) rồi `ArgumentException`. `Detail` bỏ nửa sau cặp surrogate ở chỗ cắt. `ExternalTool.RunAsync(environment:)` (giá trị đặt biến, `null` xóa, không phân biệt hoa thường; tên có `=`/`\0` bị từ chối); qpdf chạy không có `OPENSSL_CONF/MODULES/ENGINES`. Probe: qpdf 12.4.2 mở file `OPENSSL_CONF` chỉ để xoay (nhà cung cấp crypto mặc định là openssl, băm cho `/ID`), và **treo tới timeout** nếu đó là pipe không trả lời; test dùng named pipe: qpdf chạy thẳng mở pipe (đối chứng), qua wrapper thì không. Không có biến, OpenSSL dùng thư mục dựng sẵn trong `qpdf30.dll` (`C:\Program Files\Common Files\SSL`, `C:\Program Files\OpenSSL\…`), chỉ admin ghi được. ~~Còn mở: `openssl.cnf` do admin/phần mềm khác đặt ở đó vẫn áp cho qpdf~~ → đã sửa ở review vòng 2 (bảng dưới F7): ổ C: không nhất thiết là ổ hệ thống, ai cũng tạo được thư mục đó, nên qpdf dùng `openssl.cnf` rỗng của Flevert.

### F3: Giới hạn cho tiến trình con
- [x] `ExternalTool`: giới hạn bộ nhớ mỗi tiến trình và số tiến trình đang chạy (tùy chọn theo lần gọi); qpdf dùng cả hai; tool thoát trước khi gán job → trả kết quả; dispose reader
- Kiểm: tiến trình con xin quá giới hạn bị dừng (đỏ trước); tool thoát ngay không còn "Access is denied"
- File: `src/Flevert.Core/ExternalTool.cs`, `Qpdf.cs`. Cỡ: S
- Đã làm (2026-10-20): `ExternalToolLimits { MaxMemoryBytes, SingleProcess }` và `ExternalToolResult.MemoryLimitExceeded`. Probe: vượt giới hạn thì **cấp phát thất bại**, tiến trình không bị giết; số đỉnh của job không thấy (PowerShell xin 500 MB dưới 200 MB: đỉnh 55 MB), `JobObjectLimitViolationInformation` rỗng; dấu hiệu duy nhất là `JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT` (9) gửi tới completion port gắn với job (tài liệu: "best effort"). qpdf dưới giới hạn: 100 MB → chết `0xC00000FD` (stack overflow); 30 MB → mã 2 "bad allocation"; mảng 2 triệu số không ai dùng dưới 32/64/128 MB → **mã 3 "succeeded with warnings"**, object thành null. Nên `Qpdf` báo `PdfError.TooComplex` ("This PDF is too complex: working on it needs more than 1 GB of memory, the most Flevert allows.") bất kể mã thoát; đột biến bỏ kiểm tra → test đỏ (review vòng 2 đổi thành `PdfError.NeedsTooMuchMemory`, trần theo cỡ input, xem bảng dưới F7). Giới hạn 1 GB ban đầu: 200 000 trang 330 MB, 500 000 trang 830 MB, luồng 400 MB → 403 MB; 4 song song ≤ 4 GB. `SingleProcess`: cmd không chạy được ping; conhost Windows tự đưa vào job vẫn được (qpdf 200/200 lần). Tool thoát trước khi gán job: probe `AssignProcessToJobObject` → lỗi 5; test qua seam `afterStart` (chờ thoát trước khi gán; không tái hiện tất định từ ngoài được), đỏ "Access is denied" rồi xanh. Reader: `Process.Close` không đóng stream đã đọc đồng bộ (mã nguồn dotnet/runtime); đỏ "stdout/stdin is still open" rồi xanh; stdin cũng đóng khi ghi lỗi.

### F4: Script: notices, `%TEMP%`, checkout
- [ ] `build-installer.ps1` lỗi khi publish có binary native (pdfium, qpdf, runtime MSVC) chưa có notices; `fetch-deps.ps1` thư mục làm việc DACL riêng, xóa không theo link, "up to date" kiểm từng file; `setup-dev.ps1` cảnh báo checkout người khác ghi được và in lệnh sửa
- Kiểm: build bộ cài hiện tại lỗi rõ (đỏ đúng mong muốn cho tới T4); thiếu `pdfium.dll` → fetch-deps tải lại
- File: `scripts/*.ps1`. Cỡ: M

### F5: Lô N→1 không bao giờ thiếu file im lặng
- [ ] Cửa sổ rảnh N→1 riêng (dài hơn); tiến trình đến muộn sau lô N→1 vừa đóng → thông báo nêu file, không output thứ hai; kết quả xong trong ân hạn hủy được báo và log; "Done: 0 files" không xảy ra
- Kiểm: 12 và 100 tiến trình ở cửa sổ thật khi máy bận (đỏ trước nếu tái hiện được); test đường bỏ rơi 2 s
- File: `src/Flevert.Core/Runner.cs`, batch. Cỡ: M

### F6 → T9b: pdfium đọc PDF trong tiến trình worker
- [ ] Lệnh ẩn `flevert.exe pdf-worker`: nhận handle/đường dẫn đã ghim và thư mục tạm của cha, render + mã hóa trang, báo tiến độ từng dòng; cha chạy qua `ExternalTool` (giới hạn bộ nhớ, không có tiến độ quá N s → giết, hủy = giết); `pdf.to-jpg/png` chuyển sang worker
- Kiểm: fixture trang treo/bom → bị từ chối ≤ 10 s, chủ lô sống, file khác trong lô vẫn xong; tiến độ "page N of M" với PDF thật
- Phụ thuộc: F1, F3. Cỡ: L

### F7: Test chứng minh đúng điều chúng nói (Pdf)
- [x] Tên dài chạm nhánh rút gọn (stem 251); kiểm quality 90 và 4:2:0; chứng minh qpdf thoát mã 3 trong test tương ứng; `PdfError.NoPages`
- File: `tests/Flevert.Pdf.Tests/*`, `PdfToImages.cs`. Cỡ: S
- Đã làm (2026-10-20): đối chứng âm từng cái: bỏ rút gọn tên → test cũ (240) vẫn xanh, test mới (251, mọi tên trang đúng 255) đỏ; quality 85 → đỏ "Expected 90, Actual 85"; define 4:4:4 → đỏ "2x2,1x1,1x1" / "1x1,1x1,1x1"; coi mã 3 là lỗi → test cảnh báo đỏ; test chạy qpdf trực tiếp trên fixture và khẳng định mã 3. `NoPages`: đỏ (`NotPdf`) rồi xanh.

#### Review F2/F3/F7 vòng 2: reconcile

| Phát hiện | Mức | Đánh giá | Xử lý |
|---|---|---|---|
| DD-1: qpdf bị từ chối bộ nhớ (luồng ảnh 520 MB dưới 1 GB) thoát mã 3, output 653 byte thiếu ảnh; chỉ thông báo "best effort" của job bắt được | High, Required | Hợp lệ (đã probe) | Xong: `: bad allocation`/`: std::bad_alloc` trong stderr cũng làm lần chạy lỗi, bất kể mã thoát; thành công mà stderr quá 1 MB → không tin. Test `Untrustworthy` trên kết quả dựng sẵn |
| DD-2: trần phẳng 1 GB từ chối PDF trước đó xoay được (luồng không giải mã tốn ~2 lần cỡ) | High, Required | Hợp lệ (đã probe: 100/300/520 MB → 202/602/1 043 MB) | Xong: trần 256 MB + 2,5 × input, 1–3 GB (`MaxMemoryFor`); `PdfError.TooComplex` → `NeedsTooMuchMemory`, thông báo nêu trần của file đó. PDF một luồng 530 MB lỗi với trần cũ, xoay được với trần mới |
| SA-L1: không có `OPENSSL_CONF`, OpenSSL đọc `C:\Program Files\Common Files\SSL\openssl.cnf`; C: có thể không phải ổ hệ thống, ai cũng tạo được thư mục đó | Low → Required (rẻ) | Hợp lệ | Xong: `openssl.cnf` rỗng cạnh `qpdf.exe` (có trong output publish, bộ cài chép cả thư mục), `OPENSSL_MODULES/ENGINES` trỏ thư mục đó. AES-256 và RC4 vẫn chạy |
| SA-I1, DD-5: `QPDF_CRYPTO_PROVIDER`, `QPDF_ZOPFLI`, `OPENSSL_CONF_INCLUDE` vẫn tới qpdf; provider lạ làm mọi lần xoay lỗi | Info/Medium | Hợp lệ (đã probe) | Xong: bỏ `OPENSSL_CONF_INCLUDE` và mọi `QPDF_*` |
| DD-4: conhost có thể bị tính vào `ACTIVE_PROCESS = 1` trên bản Windows khác (chỉ kiểm Windows 11) → qpdf không bao giờ chạy | Medium, Required | Hợp lệ (không tái hiện được trên máy này) | Xong: `ERROR_NOT_ENOUGH_QUOTA` khi gán job → chạy lại không có giới hạn một tiến trình (giữ trần bộ nhớ), `SingleProcessLimitDropped` + trace warning; test qua seam từ chối lần gán đầu. "Thoát trước khi vào job" giờ đòi `ERROR_ACCESS_DENIED` |
| `\0` trong **giá trị** môi trường (tên đã bị từ chối) | Required | Hợp lệ | Xong: `ArgumentException`. Đột biến bỏ kiểm tra → test `A_value_with_a_null_character_is_refused` đỏ ("No exception was thrown": .NET không tự chặn) |
| Dạng tiền tố đường dẫn: `\??\` thành `\\?\\??\…`, `//?/` thành `\\?\UNC\?\…` | Required | Hợp lệ | Xong: `\??\` → `\\?\` (không chuẩn hóa, như .NET mở); `//?/` qua `GetFullPath` ra `\\?\` thì giữ; thêm ca `//./`, `\\.\UNC\`, `\\?\UNC\`, tên có dấu chấm/cách ở cuối. Đỏ 2 ca rồi xanh. `Detail` bỏ `\\?\`/`\\?\UNC\` khỏi thông báo của qpdf (đỏ rồi xanh) |
| SA-I6: chưa có test tên ngoài BMP (emoji) | Info | Hợp lệ | Xong: thư mục và tên có emoji, mồi nhử thay emoji bằng một/hai U+FFFD (cả thư mục mồi nhử); đột biến thay surrogate bằng U+FFFD trước khi mã hóa → test đỏ (qpdf đọc mồi nhử, ghi output vào thư mục mồi nhử). Chạy thật `flevert.exe run pdf.rotate-right` trên đường dẫn 249 ký tự có emoji → `/Rotate` 90,180,270,0 |
| Đối chứng của test môi trường chạy qpdf **không** có giới hạn job, wrapper thì có: hai lần chạy khác nhau hơn một điều | Low | Hợp lệ | Xong: đối chứng chạy cùng dạng đường dẫn, trần bộ nhớ, một tiến trình và timeout như wrapper, chỉ khác môi trường; đối chứng provider khẳng định mã 2 và stderr nêu tên provider |
| Test bộ nhớ chỉ chạy dưới giới hạn bị vượt | Low | Hợp lệ | Xong: thêm lần chạy dưới giới hạn tool không chạm tới, phải không báo gì |
| Chú thích đóng pipe nói dừng được lần đọc bị chặn | Nit | Hợp lệ | Sửa chú thích: pipe đồng bộ, chờ sau khi giết có giới hạn `DrainAfterKill` |
| Spec `docs/specs/pdf.md` mục 4, 6, 8 và tiêu chí 6 chưa nói giới hạn job, trần bộ nhớ, `\\?\`, môi trường, "bad allocation" | Required | Hợp lệ | Xong; ADR-0008 cập nhật theo vòng 2 (trần theo input, cấu hình OpenSSL rỗng, chạy lại khi Windows từ chối giới hạn một tiến trình) |
| SA-L2: thành công mà đỉnh bộ nhớ gần trần nên nghi ngờ | Low, Optional | Không làm | Lần chạy 520 MB lỗi có đỉnh 522/1 024 MB (cấp phát bị từ chối không tính vào đỉnh): kiểm tra này bỏ sót đúng ca cần bắt. Ghi ADR-0008 |

## Giai đoạn 2: thao tác nhiều file

### T5: Thao tác N→1 trong core (`IBatchOperation`)
- [x] Hợp đồng mới cạnh `IOperation` (không đổi đường đi hiện có): nhận danh sách file đã ghim, thứ tự `StrCmpLogicalW`, trả một output; menu và catalog nhận cả hai loại
- [x] Runner ghim mọi file của lô trước khi gọi; một file bị từ chối (đuôi, đường dẫn) → cả lô lỗi, thông báo nêu file
- [x] Thông báo `Done: <tên output>`; ADR-0007
- Kiểm: thao tác dev `dev.join` (nối các `.txt`) qua 12 tiến trình `flevert.exe` thật → một file đúng thứ tự tự nhiên (`ảnh 2` trước `ảnh 10`); toàn bộ test tích hợp cũ vẫn xanh
- Đã làm (2026-10-20): `IMenuOperation` (Id, MenuPath, Extensions) là gốc chung của `IOperation` (không đổi gì khác) và `IBatchOperation`; catalog từ chối thao tác vừa là cả hai loại. Chủ lô không đổi: runner N → 1 đọc hết `ReadAllAsync` (hết luồng = pipe đã đóng) rồi mới ghim; cửa sổ rảnh tối thiểu 1 s cho N → 1. `NaturalOrder` (`StrCmpLogicalW` qua `LibraryImport`): đo trên máy này, dãy số > 18 chữ số so như chữ và dấu thanh xếp sau số (`Ảnh 2` < `anh 3`), giữ nguyên để khớp Explorer. `BatchInputException` để thao tác nêu file hỏng; hủy cho thao tác 2 s để dọn `.partial`. 31 test mới đỏ với stub rồi xanh; đột biến (thả ghim trước khi chạy, bỏ sắp xếp) làm test tương ứng đỏ, kể cả test 12 tiến trình. Chạy thật headless: 6 tiến trình khởi động lộn thứ tự → một `ghi chú 1 (+5).txt` đúng thứ tự; có file `.md` → chủ lô thoát 1, hai tiến trình kia 0, không có output, log nêu file. Chưa chạy qua menu thật và cửa sổ tiến độ (không có UI mới; để T6 khi có `pdf.merge`)
- Phụ thuộc: T2. Cỡ: M

### T6: `pdf.merge`
- [ ] ≥ 2 PDF → `<tên đầu> (merged).pdf`, số trang bằng tổng, thứ tự tự nhiên; 1 PDF → lỗi rõ "Select at least two PDFs"
- Kiểm: test + chạy thật 3 PDF qua menu (MenuProbe invoke)
- Phụ thuộc: T3, T5. Cỡ: S

### T7: Output thư mục (1→N) và tiến độ trong file
- [x] `OutputPath.WriteDirectoryAtomicallyAsync`: thư mục tạm, đổi tên một lần, tên trùng → ` (1)`; hủy → không còn thư mục dở
- [x] `IProgress<FileProgress>` cho thao tác; cửa sổ tiến độ hiện "page 37 of 200" (UI Automation đọc được)
- Kiểm: thao tác dev tạo 50 file chậm, hủy giữa chừng; ảnh chụp cửa sổ tiến độ (chỉ cửa sổ app, báo người dùng trước)
- Đã làm: `dev.split` (50 trang, `FLEVERT_DEV_PAGE_MS`), ADR-0007. Hủy giữa chừng kiểm bằng test runner (hủy ở trang 10 → không còn `.partial`), không bấm Cancel thật (không gửi phím vào desktop). Chạy thật có UI: PrintWindow chỉ cửa sổ Flevert thấy `0 of 1 done: Báo cáo quý.txt` / `page 24 of 50`; UI Automation đọc `Progress in the current file: page 25 of 50`; xong 50 file, không còn `.partial`. `Directory.Delete(recursive)` không dùng được (junction bên trong → cần admin, để lại thư mục), nên tự duyệt và gỡ link không đi theo
- Phụ thuộc: T2. Cỡ: M

### T8: `pdf.split`
- [ ] Thư mục `<tên> (pages)\<tên> 001.pdf…`, số chữ số đủ; mỗi file một trang
- Phụ thuộc: T3, T7. Cỡ: S

### Checkpoint 2
- [ ] Review ba góc cho T5–T8 (trọng tâm: runner, hủy, đặt tên); reconcile

## Giai đoạn 3: pdfium

### T9: `Pdfium` (P/Invoke) và render an toàn
- [x] `LibraryImport` tới `pdfium.dll` cạnh exe; `SafeHandle` cho document/page/bitmap; khóa toàn tiến trình; `FPDF_InitLibrary` một lần
- [x] Giới hạn: ≤ 2 000 trang, trang render ≤ 10 000 px mỗi cạnh **sau khi nhân DPI** (vượt thì giảm DPI trang đó), file ≤ 2 GB; PDF có mật khẩu → lỗi như T3
- [x] ADR-0008 cập nhật (P/Invoke tự viết, lý do không dùng wrapper NuGet)
- Kiểm: fixture 3 trang render đúng màu tại điểm mẫu; fixture trang khổng lồ bị giảm DPI; test chạy song song 8 luồng không crash
- Đã làm (2026-10-20): project `Flevert.Pdf` + `Flevert.Pdf.Tests` tạo sớm ở T9 (sln, CI, release, setup-dev, tài liệu lệnh test); T3 chỉ còn `Qpdf` và thao tác xoay. Đọc PDF bằng `FPDF_LoadCustomDocument` qua handle .NET (lý do trong ADR-0008). 20 test đỏ trước (stub), rồi xanh; bỏ khóa trong `Render` → test 8 luồng làm sập test host (đối chứng âm). Chưa nối `MemoryBudget`: `GetRenderSize(...).Bytes` cho thao tác T10 xin trước khi render. Bộ nhớ riêng của pdfium (ảnh lớn trong trang) chưa giới hạn: để T18.
- Phụ thuộc: T1. Cỡ: M

### T10: `pdf.to-jpg`, `pdf.to-png`
- [x] 150 DPI, JPEG q90; thư mục `<tên> (pages)`; tiến độ theo trang (đóng gói `pdfium.dll` + notices chuyển sang T4, làm chung với qpdf)
- Kiểm: PDF 200 trang (fixture sinh) → 200 ảnh, hủy giữa chừng sạch; test-installer kiểm `pdfium.dll` và notices
- Đã làm (2026-10-20): `PdfToImages` (`pdf.to-jpg`, `pdf.to-png`, `MaxParallel = 1`) trong `src/Flevert.Pdf/PdfToImages.cs`, công khai qua `PdfRenderOperations.All` (danh sách riêng cạnh `PdfOperations.All` của T3). **Chưa vào catalog**: dòng đăng ký trong `Program.cs` thêm lúc merge với T3 (T10 không sửa `Program.cs`, bộ cài, `build-installer.ps1`); chạy thật qua `flevert.exe`/menu làm lúc đó. Pixel BGRA của pdfium → `MagickImage.ReadPixels` (không coder nào parse gì) → JPEG q90 4:2:0 / PNG RGB, ghi DPI thật; mỗi trang xin `MemoryBudget.Shared` 3 × `GetRenderSize(...).Bytes` trước khi render. Trang hỏng → cả file lỗi, thông báo nêu số trang; PDF 0 trang → "This PDF has no pages.". 17 test (`PdfToImagesTests`), đỏ cả 16 với stub (`NotImplementedException`) rồi xanh; đột biến bỏ xin bộ nhớ / bỏ số chữ số tối thiểu → test tương ứng đỏ (bỏ `Alpha(Off)` thì không: bộ mã PNG tự bỏ alpha khi mọi điểm ảnh đục). Thời gian (máy dev, cả bộ test chạy cùng): 200 trang Letter trống 150 DPI → JPEG 5,6 s, PNG 11,5 s; 1 000 trang ở 2 DPI 0,6 s. Chạy thật (2026-10-20, theo DD-M4 vòng 1): `flevert.exe run pdf.to-jpg/pdf.to-png` headless trên `Hóa đơn.pdf` (3 trang) → `Hóa đơn (pages)` và `Hóa đơn (pages) (1)` đúng 3 ảnh; `huge-page.pdf` → ảnh 10000×100 px; PDF có mật khẩu → thoát 1, log "This PDF needs a password: use Remove password first". MenuProbe sau `install-menu --allow-insecure-location`: `.pdf` → `Flevert > Convert > JPG pages / PNG pages`, `Rotate > …`; `invoke "Flevert/Convert/JPG pages"` → `Hóa đơn (pages) (2)`; đã `uninstall-menu`, mục Flevert biến mất.
- Phụ thuộc: T7, T9. Cỡ: M

### T11: `pdf.from-images` (ảnh → một PDF)
- [ ] JPEG nhúng nguyên byte trừ APP1/APP13/COM (so hash luồng DCT); EXIF hướng → ma trận xoay trang; HEIC/HEIF/AVIF/WebP → JPEG q92; PNG/GIF/BMP/TIFF → Flate; trong suốt → trắng; trang ≤ A4/Letter theo cạnh dài
- [ ] Tên `<tên đầu> (+N−1).pdf`; menu ảnh có `Combine into PDF`; `Producer: Flevert`, không metadata khác
- [ ] Cập nhật spec (pdfium thay bộ ghi tự viết)
- Kiểm: 12 ảnh gồm ảnh dọc EXIF 6 và `portrait-gps.heic` → 12 trang đúng thứ tự, đúng chiều (render trang kiểm điểm mẫu), không còn chuỗi GPS/Make trong byte; `qpdf --check` sạch
- Phần A (T11a, 2026-10-20), phần nền, **chưa có thao tác**: `JpegSegments` (bỏ metadata JPEG ở mức marker, kiểm bảng, đọc hướng EXIF và kích thước) và `PdfBuilder` (internal: `AddJpegPage(JpegImage, dpi?)`, `AddBitmapPage(bgra, w, h, dpi?)`, `Save(Stream)`), quy tắc trong ADR-0008. Test `PdfBuilderTests` và `JpegSegmentsTests`, đỏ trước rồi xanh; sau review vòng 1 (bảng dưới). Điều T11 cần biết (đã kiểm bằng probe với pdfium chromium/8100, Magick.NET 15.0.0 / ImageMagick 7.1.2-33 Q8):
  - `FPDFImageObj_LoadJpegFileInline` luôn trả TRUE; JPEG pdfium không đọc được header trước đây thành **trang trắng** im lặng. `PdfBuilder` giờ so kích thước pdfium đọc được (`FPDFImageObj_GetImagePixelSize`) với SOF và ném `InvalidDataException`. pdfium **có** từ chối JPEG có chỉ số DQT sai (bản wip tưởng là không, vì chỉ nhìn giá trị trả về), cùng với "không phải JPEG", cắt trước SOF, cắt trước SOS.
  - ImageMagick ping (`MagickImage.Ping`) **có** từ chối JPEG chỉ số DQT sai: `` MagickCorruptImageErrorException: Bogus DQT index 15 `' @ error/jpeg.c/JPEGErrorHandler/615 ``. Nhưng ping chỉ đọc header như pdfium, nên không bắt thêm được gì: cùng từ chối "không phải JPEG" (`insufficient image data in file`), cắt trước SOF (`JPEG datastream contains no image`), cắt trước SOS (`Invalid JPEG file structure: missing SOS marker`).
  - Lọt qua cả pdfium lẫn ping: JPEG **không có bảng lượng tử** (bỏ hết DQT): pdfium nhúng, trang render **đen** (test `A_jpeg_without_quantization_tables_gets_past_pdfium_so_the_tables_are_checked_before`); chỉ giải mã đầy đủ (`new MagickImage(bytes)`) từ chối: `` MagickCorruptImageErrorException: Quantization table 0x00 was not defined `' @ error/jpeg.c/JPEGErrorHandler/615 ``. JPEG bị bỏ bảng Huffman tối ưu (DD-H1): cả giải mã đầy đủ cũng "thành công" (libjpeg dùng bảng chuẩn, chỉ cảnh báo) và ra rác. **Đã xử lý ở review T11a vòng 1**: `JpegSegments` kiểm mức marker rằng mọi bảng mỗi scan dùng đã được định nghĩa trước scan đó; thiếu → `InvalidDataException` (từ chối, không mã hóa lại: mã hóa lại rác chỉ ra rác). T11 **không** cần ping hay giải mã thêm cho các trường hợp này.
  - JPEG bị cắt giữa dữ liệu entropy: `JpegSegments` đã từ chối ("ends too early", thiếu EOI); ImageMagick giải mã được, chỉ cảnh báo `Premature end of JPEG file`. Quyết (DD-L4): T11 **từ chối**, báo file hỏng, không mã hóa lại phần ImageMagick đọc được: một trang thiếu nửa dưới trông như đủ, tệ hơn lỗi rõ (spec: lỗi một file đầu vào → cả output thất bại, nêu file).
  - CMYK: JPEG 4 thành phần không có APP14 Adobe → `NotSupportedException` (mã hóa lại). pdfium hiện CMYK như mực in (đỏ CMYK là (238, 29, 35)), không như RGB ImageMagick đổi ra: đúng, không phải lỗi.
  - Màu (DD-M2, CR): pdfium ghi `DeviceRGB`/`DeviceGray`/`DeviceCMYK`, profile ICC giữ trong APP2 không có tác dụng. Quyết: đường HEIC/HEIF/AVIF/WebP chuyển sang sRGB trước khi ghi JPEG q92; JPEG có profile khác sRGB (Display P3 của iPhone, Adobe RGB) nhúng nguyên, màu nhạt đi chút như trình xem bỏ qua profile (ADR-0008).
  - DPI (DD-M4): đơn vị JFIF 0 (chỉ tỉ lệ) hoặc không ghi = không biết (150); đơn vị cm → nhân 2,54; X ≠ Y = không biết (trang dùng điểm ảnh vuông, như mọi trình xem ảnh); DPI thấp bất thường không cần luật riêng: trang lớn hơn A4 tự thu về A4, ngoài 1–10 000 đã tính là không biết.
  - Bộ nhớ: builder giữ mọi ảnh tới khi `Save` (giới hạn 1,5 GB, `MaxContentBytes`). T11 xin phần `MemoryBudget` cho **cả đời builder** (tổng byte JPEG + 3 × rộng × cao mỗi bitmap, cộng bộ đệm BGRA của ảnh đang giải mã), trả lại sau `Save`. Bộ đệm tạm phải tính thêm vào lúc cao nhất: trong `AddBitmapPage`, bản BGR của builder (3 × rộng × cao, tới ~300 MB ở 100 MP) cùng lúc với BGRA đầu vào (tới ~400 MB) và bản pdfium chép vào ảnh; trong `Save`, một bộ đệm nén Flate cỡ luồng bitmap lớn nhất. `Save` giữ khóa pdfium suốt lúc ghi: ghi vào file tạm cục bộ của `OutputPath`.
  - Cạnh trang tối thiểu (SA/CR, Low): ảnh rất dẹt (10 000 × 100) hoặc rất nhỏ cho trang có cạnh < 3 pt, dưới mức nhỏ nhất Acrobat mở (giới hạn trong phụ lục C của PDF 1.7, cần kiểm ở nguồn). T11 quyết; đề xuất: trang có cạnh ngắn ít nhất 3 pt, phần thừa để trắng quanh ảnh (ảnh không bị méo, không phóng).
  - Metadata: `PdfBuilder` đã chỉ ghi `Producer: Flevert` (bắt đầu từ một document rỗng của Flevert thay vì `FPDF_CreateNewDocument`, vốn ghi `CreationDate` và `Creator (PDFium)`); không cần qpdf sau khi lưu. T11 chỉ cần test ở mức thao tác.
  - T11 còn phải: đọc DPI của ảnh (ImageMagick `Density`, đổi sang DPI nếu đơn vị là cm) rồi truyền vào; HEIC/HEIF/AVIF/WebP → xoay thẳng, JPEG q92 → `JpegSegments.Strip` → `AddJpegPage` (như test `A_phone_photo_leaves_no_location_or_camera_in_the_pdf_and_stays_upright`); PNG/GIF/BMP/TIFF → BGRA đã xoay thẳng → `AddBitmapPage` (giữ trong bộ nhớ dạng BGR tới khi `Save`: xin `MemoryBudget`); `JpegSegments` ném `NotSupportedException` (arithmetic, lossless, 12-bit…) → mã hóa lại bằng ImageMagick; ghi qua `OutputPath`. Spec mục "Lựa chọn thư viện" và "Project Structure" vẫn ghi "tự ghi PDF / PdfWriter tối giản": cập nhật cùng T11 (ô ở trên).
- Phụ thuộc: T5, T9. Cỡ: M

#### Review T11a vòng 1: reconcile

Ba reviewer bối cảnh trống, chỉ đọc, trên nhánh `t11a` (JpegSegments, PdfBuilder, ADR-0008). CR = code-reviewer, SA = security-auditor, DD = doubt-driven. Không có Critical. Mỗi sửa có test viết trước và thấy đỏ.

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| CR, SA, DD-M1: `JpegSegments` giữ mọi APP0: thumbnail JFIF (có thể lộ phần đã cắt khỏi ảnh), JFXX, Canon CIFF (tên chủ, số máy), AVI1 vào tới PDF | Required | Hợp lệ (đã probe) | Xong: chỉ giữ APP0 JFIF đầu tiên (≥ 14 byte); đã không thumbnail thì chép nguyên, không thì ghi lại 16 byte cùng version/units/density, thumbnail 0 × 0; mọi APP0 khác bỏ. Test `Keeps_the_jfif_header_but_not_its_thumbnail_or_anything_after_it`, `Drops_every_other_app0_segment` |
| DD-H1: JPEG bị bỏ DHT tối ưu qua được Strip, pdfium và cả giải mã đầy đủ (chỉ cảnh báo), ra rác; không DQT thì ra đen | High, Required | Hợp lệ (đã probe) | Xong: kiểm mức marker mọi bảng mỗi scan dùng (Tq của thành phần; DC/AC theo kiểu scan, progressive tính đúng scan DC tinh chỉnh không cần bảng) đã định nghĩa trước scan đó; thiếu → `InvalidDataException` (từ chối, không mã hóa lại). Test `Refuses_a_jpeg_whose_tables_are_missing_or_malformed`, `Accepts_tables_defined_after_the_frame_header` |
| CR, SA, DD-H2, DD-M3: không giới hạn byte giữ tới `Save`; pdfium hủy tiến trình khi cấp phát lỗi; bảng xref ghi offset bằng `%010d` nên file ≥ 2 GB hỏng | High, Required | Hợp lệ (đã đọc `CPDF_Creator` chromium/8100) | Xong: `MaxContentBytes` = 1,5 GB (dư 512 MB cho Flate và từ điển trang), đếm byte JPEG + 3 × rộng × cao mỗi bitmap, kiểm trước khi pdfium chép, vượt → `PdfException(TooLarge)`, document không đổi; tiêm được qua constructor. Test `Refuses_images_past_the_content_cap_and_keeps_the_pages_it_has` |
| SA: `FPDFPage_InsertObject` trả `void`? (CR, DD đọc nguồn: `FPDF_BOOL`) | Required (kiểm) | Không hợp lệ | Header `fpdf_edit.h` của chromium/8100 (trong `pdfium-win-x64.tgz` đã ghim): `FPDF_EXPORT FPDF_BOOL FPDF_CALLCONV FPDFPage_InsertObject(FPDF_PAGE page, FPDF_PAGEOBJECT page_object);`, "freed on failure"; nguồn `fpdf_editpage.cpp` khớp. Giữ nguyên, ghi vào ADR-0008 |
| SA, CR, DD-L3: JPEG 4 thành phần không APP14: pdfium luôn ghi `Decode [1 0 1 0 …]` | Low, Optional | Hợp lệ | Xong: `NotSupportedException` (mã hóa lại). Test `A_cmyk_jpeg_without_its_adobe_segment_is_left_to_re_encoding`; CMYK có APP14 render đúng (`A_cmyk_jpeg_with_its_adobe_segment_renders_in_its_colours`) |
| SA: marker không phải APP đi qua nguyên; APP14 giữ cả phần đuôi | Low, Optional | Hợp lệ | Xong: chỉ DQT, DHT, SOF0–2, SOS, DRI (đúng 2 byte), RSTn, EOI; DAC/DHP/EXP → `NotSupportedException`; còn lại (TEM, JPG, JPGn, DNL, dành riêng) → `InvalidDataException`; APP14 chỉ giữ 12 byte Adobe đầu tiên. Test `Refuses_markers_a_photo_for_a_pdf_has_no_use_for`, `Keeps_only_the_twelve_bytes_of_the_adobe_segment`, thêm 3 dòng `Unsupported` |
| SA: `AddJpegPage` không giới hạn điểm ảnh | Low, Optional | Hợp lệ | Xong: 100 MP như bitmap (`MaxImagePixels`), kiểm trước khi pdfium thấy byte. Test `Refuses_a_jpeg_past_the_pixel_limit_before_pdfium_reads_it` |
| DD-L1: trang bị gỡ có thể để lại từ điển trang | Low | Hợp lệ (đã probe) | Probe: còn `N 0 obj null`, không còn ảnh và nội dung của trang đó; `GenerateContent` vẫn là bước cuối. Ghi ADR-0008; test kiểm |
| DD-L2: "mất tối đa 6%" sai với trang gần vuông | Low | Hợp lệ | Sửa ADR-0008 và chú thích `PageSize`: 94% cho trang dáng A4, 73% (Letter) / 71% (A4) cho trang vuông |
| `Save` giữ `Pdfium.Gate` suốt lúc ghi stream | Info | Hợp lệ | Ghi ADR-0008 và tài liệu `Save`; T11 ghi vào file tạm cục bộ |
| DD-M2, CR: pdfium ghi `DeviceRGB`, profile ICC giữ lại không có tác dụng | Medium, Optional | Hợp lệ | Quyết cho T11 (ADR-0008, ghi chú T11 ở trên): HEIC… chuyển sRGB; JPEG khác sRGB nhúng nguyên |
| DD-M4: luật DPI (đơn vị 0, X ≠ Y, DPI thấp bất thường) | Medium, Required trước T11 | Hợp lệ | Quyết, ghi ở ghi chú T11 ở trên |
| DD-L4: JPEG bị cắt | Low | Hợp lệ | Quyết: T11 từ chối, ghi ở trên |
| T11 phải giữ phần `MemoryBudget` suốt đời builder | Medium, Required ở T11 | Hợp lệ | Ghi ở trên |
| CR: thiếu test: giới hạn trang và builder đã dispose ở `AddJpegPage`, đường gỡ trang, xref của document mẫu | Low, Required | Hợp lệ | Xong: `Refuses_more_jpeg_pages_than_the_limit_and_any_after_disposal`, `A_page_whose_content_cannot_be_written_is_removed_again` (qua `GenerateContent` tiêm được), `The_document_every_builder_starts_from_is_valid_as_written` (qpdf không dựng lại xref; lỗi duy nhất là "không có trang"). Hai test đầu xanh ngay vì mã đã đúng: đột biến (bỏ `FPDFPage_Delete`, bỏ `ThrowIfFull` trong `AddJpegPage`) làm chúng đỏ |
| SA, CR: không có cạnh trang tối thiểu | Low, Optional | Hợp lệ | Để T11 quyết, đề xuất ở trên |
| Vòng 2: luật bảng của scan progressive (`needsDc`/`needsAc`) không được test ghim: `Synthetic(sof: 0xC2)` chỉ tạo scan Ss = 0, Td = 0, đổi `needsAc` thành `!frame.Progressive` hay bỏ `&& !refining` vẫn xanh | Required | Hợp lệ | Xong: `TestJpeg.Synthetic(scanHeader:)`; test `Checks_only_the_huffman_table_a_progressive_scan_decodes_with` (scan AC thiếu bảng AC → từ chối; scan DC đầu thiếu bảng DC → từ chối; scan DC tinh chỉnh nêu bảng DC chưa có → nhận; scan AC nêu bảng DC chưa có nhưng bảng AC có → nhận). Đột biến `needsAc = !frame.Progressive`, bỏ `&& !refining`, bỏ `spectralStart == 0` → mỗi cái làm đúng dòng tương ứng đỏ |
| Vòng 2: không giới hạn số scan mỗi JPEG | Low, Optional | Hợp lệ | Xong: `MaxScans` = 1 000 → `InvalidDataException` (libjpeg-turbo có `TJPARAM_SCANLIMIT` cho đúng việc này, mặc định tắt; libjpeg ghi khoảng mười scan). Test `Refuses_a_jpeg_with_more_scans_than_the_limit` (1 000 nhận, 1 001 đỏ trước rồi xanh) |
| Vòng 2: ghi chú "Bộ nhớ" thiếu bộ đệm tạm (BGR cùng BGRA trong `AddBitmapPage`, bộ đệm nén khi `Save`) | Low, Optional | Hợp lệ | Xong: thêm vào ghi chú T11 ở trên |
| Vòng 2: chú thích `JpegSegments` và ADR nói libjpeg giải mã JPEG thiếu DQT "bằng mặc định, ra đen" | Low, Optional | Hợp lệ | Sửa: libjpeg từ chối giải mã, trang đen vì pdfium không giải mã được |
| Vòng 2: `Array.FindIndex` với lambda cho mỗi thành phần | Nit, Optional | Hợp lệ | Thay bằng vòng lặp |

### Checkpoint 3
- [ ] Review ba góc cho T9–T11 (trọng tâm: P/Invoke, input không tin cậy, rò metadata); reconcile

## Giai đoạn 4: qpdf còn lại

### T12: `pdf.strip-metadata`
- [ ] Bỏ Info (giữ `Producer: Flevert`), XMP, tệp đính kèm, JavaScript/OpenAction; kiểm bằng `qpdf --json`
- Phụ thuộc: T3. Cỡ: S

### T13: `pdf.compress` (lossless)
- [ ] object streams, nén lại Flate mức 9; không nhỏ hơn → bỏ qua (skipped)
- Phụ thuộc: T3. Cỡ: S

### T14: `pdf.protect`, `pdf.unlock` + hộp thoại mật khẩu
- [ ] Hộp thoại WinForms: che ký tự, nhập lại (protect), "Use this password for all N files"; Cancel = bỏ qua
- [ ] Mật khẩu chỉ qua stdin (`@-`, `--password-file=-`), `char[]` ghi đè sau khi dùng; AES-256; sai mật khẩu → lỗi file đó, file khác vẫn chạy
- Lưu ý từ T3: `Qpdf.TransformAsync` đã dùng stdin cho hai đường dẫn (`@-`); mật khẩu phải đi cùng luồng `@-` đó, hoặc kiểm trước rằng `--password-file=-` vẫn đọc được phần sau các dòng `@-`
- Lưu ý từ review vòng 1 (DD-M6, CR nit): qpdf đọc `@-` tới hết stdin khi phân tích đối số, nên nhiều khả năng mật khẩu phải là dòng `--password=` trong `@-` (kiểm bằng probe trước); stdin dựng bằng `byte[]` xóa sau khi dùng, không qua `string`; test stderr/log/`Detail` không bao giờ chứa mật khẩu (kể cả khi qpdf báo "unrecognized argument")
- [ ] Test rò rỉ: mật khẩu đánh dấu không có trong log, dòng lệnh tiến trình con (WMI), file tạm
- Kiểm: chạy thật qua menu; ảnh chụp hộp thoại (báo trước); UI Automation tên trợ năng
- Phụ thuộc: T2, T3. Cỡ: M

### Checkpoint 4
- [ ] Review ba góc cho T12–T14 (trọng tâm: mật khẩu); reconcile

## Giai đoạn 5: tùy chọn (bỏ được nếu không kịp)

### T15: Spike compress-sharing
- [ ] Thử trên 3 PDF scan (sinh): thay ảnh bằng pdfium, lưu, qpdf nén → nhỏ hơn ≥ 40%, render vẫn đọc được; ghi kết quả; quyết làm hay bỏ
- Phụ thuộc: T9. Cỡ: S

### T16: `pdf.compress-sharing` (nếu T15 đạt)
- Phụ thuộc: T15. Cỡ: M

### T17: `pdf.reverse`, `pdf.repair`, `pdf.extract-text`, `pdf.extract-images` (theo thứ tự, mỗi cái một commit)
- Phụ thuộc: T3/T7/T9. Cỡ: S mỗi cái

## Giai đoạn 6: hoàn thiện và phát hành

### T18: Hardening input độc
- [ ] Fixture: bom nén, vòng tham chiếu object, 100 000 trang rỗng, trang khổng lồ, xref hỏng, file không phải PDF đổi đuôi → bị từ chối hoặc xử lý trong ≤ 10 s, bộ nhớ trong `MemoryBudget`
- [ ] PDF có chữ ký số: thông báo nói chữ ký không còn hợp lệ ở output

### T19: Nâng cấp từ 0.1.0
- [ ] `test-installer.ps1`: cài 0.1.0 (tải từ Releases, kiểm SHA256 `aa85392b…92e3`) rồi cài 0.2.0 đè; giữ lựa chọn menu cổ điển và kiểm cập nhật; menu có mục PDF; gỡ sạch

### T20: Docs và phát hành v0.2.0
- [ ] README (menu PDF, ảnh động mới cho Combine into PDF), CHANGELOG `[0.2.0]`, capability-map, HANDOFF; `<Version>0.2.0`
- [ ] `/ship`: ba chuyên gia song song → go/no-go; phát hành theo RELEASING (CI ×3, tag, kiểm bản nháp, VirusTotal); người dùng v0.1.0 thấy hộp thoại "Flevert 0.2.0 is available" (kiểm bằng bản 0.1.0 thật sau khi publish)

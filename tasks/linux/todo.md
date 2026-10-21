# Todo: linux

Plan: [plan.md](plan.md). Spec: [docs/specs/linux.md](../../docs/specs/linux.md).

Mỗi task chỉ xong khi đạt tiêu chí nghiệm thu **và** Definition of Done: test viết trước và thấy đỏ; `dotnet build Flevert.sln` 0 cảnh báo, `dotnet format Flevert.sln --verify-no-changes` exit 0, ba project test xanh trên Linux (chạy lần lượt); commit nguyên tử, message nói vì sao. Review và chạy thật làm một lần cuối, trước PR.

Lệnh chung: `export PATH=$HOME/.dotnet:$PATH`, `python3 scripts/fetch-deps-linux.py`, `dotnet build Flevert.sln`, `dotnet test tests/Flevert.Core.Tests`, `dotnet test tests/Flevert.Image.Tests`, `dotnet test tests/Flevert.Pdf.Tests`.

## Giai đoạn 1: nền

### T1: Solution build được trên Linux
- [x] `Directory.Build.props` chọn TFM theo hệ điều hành (đã làm); `Flevert.Cli`, `MenuProbe` cố định `net10.0-windows` + `EnableWindowsTargeting`
- [x] Kiểu chỉ dành cho Windows gắn `[SupportedOSPlatform("windows")]`; chỗ gọi chung rẽ nhánh `OperatingSystem.IsWindows()` (tạm thời ném `PlatformNotSupportedException` ở nhánh Linux chưa làm); không thêm `NoWarn`
- [x] Project rỗng `src/Flevert.Linux` (exe `flevert`, lệnh `list-ops`) trong sln
- Kiểm: bốn project `src` và `Flevert.Cli` build 0 cảnh báo, 0 lỗi trên Linux (cả solution, gồm test, là việc của T3); `git diff` trên code Windows chỉ có attribute và điểm rẽ nhánh
- Đã làm (2026-10-21): CA1416 báo 168 chỗ ở 5 file Core, `ConfigurationFile` (Image), `PrivateFolder` (Pdf). Gắn `[SupportedOSPlatform("windows")]` cho `PipePeer`, `ProcessFacts`, `ProcessCensus`, `InstallLocation`, `WindowsRegistry`, `Batch`, `ConfigurationFile`, ba hàm ACL của `SecureDirectory`, `ProcessStamp.Self/Of/IsRunning`, phần pipe của `BatchLeader` (`Start`, `Listen*`, `ServeAsync`, `TryReplaceAsync`). Nhánh Linux chưa làm ném `PlatformNotSupportedException`: `SecureDirectory.TrySecure`, `MagickSetup` (T6), `PrivateFolder.Create` (T6). Bước kiểm deps của `Flevert.Pdf.csproj` tạm chỉ chạy trên Windows (T2 thêm bản Linux). Các lớp chỉ gọi P/Invoke Windows (`InputPin`, `NaturalOrder`, `ExternalTool`, `PartialFolder`, `FileIdentity`, `PdfRenderBudget`) không bị analyzer bắt: đổi ở T4–T8. Bản Windows biên dịch trên Linux: `dotnet build src/Flevert.Cli -p:TargetFramework=net10.0-windows -p:EnableWindowsTargeting=true` 0 cảnh báo. `flevert list-ops` in 42 thao tác (cần `DOTNET_ROOT=$HOME/.dotnet` với SDK cài cho user).
- Phụ thuộc: không. Cỡ: M

### T2: Deps cho Linux
- [x] `scripts/deps-linux.json`: `pdfium-linux-x64.tgz` (chromium/8100), `qpdf-12.4.2-bin-linux-x86_64.zip`, SHA256 từ digest của GitHub; giấy phép như bản Windows
- [x] `scripts/fetch-deps-linux.py` (thư viện chuẩn): tải vào thư mục tạm mới, kiểm hash **trước** khi giải nén, chỉ giữ file liệt kê, chạy lại khi đã đủ thì không tải; hash sai → lỗi, `deps/` không đổi
- [x] `Flevert.Pdf.csproj` chép `libpdfium.so` và qpdf (`bin/qpdf`, `lib/libqpdf.so*`) trên Linux; `Qpdf.FileName`, `PdfWorker.FileName` theo nền tảng; `ldd` không thiếu thư viện
- Kiểm: chạy hai lần (lần 2 không tải); sửa một ký tự hash → thất bại rõ (đối chứng âm); test Python cho script
- Đã làm (2026-10-21): zip qpdf cho Linux kèm gnutls, nettle, libunistring, libidn2, p11-kit, libtasn1, libffi (phần lớn LGPL/GPL). Chỉ giữ `bin/qpdf`, `lib/libqpdf.so.30` (Apache-2.0) và `lib/libjpeg.so.8` (libjpeg-turbo 2.1.2, BSD/IJG; giấy phép ghim ở tag `2.1.2`); `libgnutls.so.30` và `libz.so.1` lấy từ hệ thống (có trên Ubuntu/Debian/Fedora): không phân phối lại thư viện LGPL nào. `ldd` không thiếu gì, `qpdf --version` = 12.4.2. pdfium chỉ cần libc/libm/libgcc. qpdf nằm ở `qpdf/bin/qpdf` + `qpdf/lib/` cạnh `flevert` (RUNPATH `$ORIGIN/../lib`), `Qpdf.RelativePath`. Script chỉ lấy file được liệt kê ra khỏi archive theo tên (không giải nén cả archive: tên có `..` không bao giờ được ghi), symlink trong archive thành bản sao của file nó trỏ tới (chỉ trong archive), giữ bit thực thi. 14 test `unittest` (`python3 -m unittest discover -s scripts -p 'test_*.py'`), 12 đỏ trước khi có script. Đối chứng âm: hash sai một ký tự → exit 1, `deps/` không đổi; bỏ bước kiểm hash trong script → test hash đỏ. Thiếu deps → build báo lệnh cần chạy. `FileName` của `Qpdf`/`PdfWorker` đổi từ `const` sang `static readonly` (theo nền tảng); bản Windows vẫn biên dịch. Test còn ghi `"qpdf.exe"` cứng: sửa ở T3.
- Phụ thuộc: T1. Cỡ: M

### T3: Test chạy được trên Linux, lập bảng
- [x] Test project tham chiếu `Flevert.Linux` trên Linux; file chỉ dành cho Windows bỏ khỏi build trên Linux (có comment lý do)
- [x] Chạy ba project, ghi bảng: xanh / đỏ vì phần Linux chưa làm (task nào sửa) / bỏ qua vì chỉ có nghĩa trên Windows
- Kiểm: ba project chạy hết (đỏ được phép, nhưng mỗi test đỏ có task nhận); không test nào bị xóa
- Đã làm (2026-10-21): bảng ở cuối file. `tests/Shared/PlatformFacts.cs` (`[WindowsFact]`, `[WindowsTheory]`, link vào ba project) cho test lẻ, kèm `[SupportedOSPlatform("windows")]` để CA1416 vẫn kiểm thân test. Test chỉ sai vì dữ liệu đường dẫn Windows (`@"C:\x\p7.heic"` khi chỉ cần tên file) đổi sang `"C:/x/p7.heic"` (Windows cũng nhận `/`), không đổi ý nghĩa; `Repository.Root()` tách khỏi `InstallerScriptTests` để `UpdateCheckTests` dùng được trên Linux.
- Phụ thuộc: T1, T2. Cỡ: M

### Mốc: nền
- [ ] Build 0 cảnh báo; bảng test có trong todo

## Giai đoạn 2: phần nền tảng

### T4: Ghim file và quy tắc đường dẫn (Runner)
- [x] `UnixInputPin.TryPin`: `O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`, `fstat` chỉ file thường, `st_nlink == 1`, đường dẫn thật từ `/proc/self/fd/N`
- [x] Quy tắc đường dẫn Linux trong `Runner`/`InputPath`: tuyệt đối, bắt đầu bằng `/`, không có `\0`; tên có dấu chấm cuối, khoảng trắng cuối hay `:` là hợp lệ
- Kiểm: test âm cho symlink, hard link, FIFO (không treo), `/dev/zero`, thư mục, đường dẫn tương đối; thư mục cha là symlink → đường dẫn thật
- Đã làm (2026-10-21): mở bằng `O_PATH|O_NOFOLLOW|O_CLOEXEC` thay cho `O_RDONLY|O_NONBLOCK` (không đọc gì, mở FIFO hay thiết bị không treo và không có tác dụng phụ; symlink cho fd của chính nó); `statx(AT_EMPTY_PATH)` thay cho `fstat` (layout cố định mọi kiến trúc); hằng số kiểm từ header glibc 2.35, `O_NOFOLLOW`/`O_DIRECTORY` chỉ khai cho x86-64 (kiến trúc khác ném `PlatformNotSupportedException`). `src/Flevert.Core/Unix/Libc.cs` gom P/Invoke libc. 11 test `UnixInputPinTests` (`[LinuxFact]`), đỏ với stub; đột biến bỏ `O_NOFOLLOW` hoặc kiểm `nlink` → test tương ứng đỏ. 15 test của `RunnerTests`/`RunnerBatchOperationTests` chỉ có nghĩa trên Windows (8.3, ADS, dấu chấm cuối, `\\?\`, thiết bị, named pipe, junction, hệ thống file không phân biệt hoa/thường, khóa không cho đổi tên) → `[WindowsFact]`; bản Linux tương ứng nằm trong `UnixInputPinTests`, trừ hai test khóa (Linux không có khóa bắt buộc: ADR-0010). **Lỗi tìm thấy:** `RunLog` nhờ `FileShare.Read` để các tiến trình ghi lần lượt; .NET trên Linux đổi thành `flock` chia sẻ nên hai luồng ghi cùng lúc làm mất dòng (`RunnerParallelTests` 6 → 5 dòng). Sửa: `FileShare.None` trên Linux, lỗi `EAGAIN` (HResult 11) là "đang bị giữ". Cần soát các chỗ khác dựa vào `FileShare` (T6/T7, review).
- Phụ thuộc: T3. Cỡ: S

### T8: ExternalTool trên Linux (làm sớm: rủi ro cao)
- [x] Kiểm đường dẫn công cụ theo nền tảng (tuyệt đối, tồn tại, có quyền chạy); giới hạn bộ nhớ qua `prlimit --as`; hủy/hết giờ giết cả cây
- [x] `MemoryLimitExceeded` suy ra được trên Linux (qpdf `std::bad_alloc`, tiến trình bị `SIGKILL`/`SIGABRT` khi có giới hạn); `SingleProcess` không áp dụng (ghi vào ADR)
- Kiểm: test dùng `/bin/sh -c`, `cat`, `sleep`, `head -c`: echo stdin, output 5 MB bị cắt, timeout, hủy giết cây con, vượt giới hạn bộ nhớ được báo; đo `prlimit --as` với worker .NET
- Đã làm (2026-10-21): đo trước: `flevert` (.NET 10) chạy được dưới `prlimit --as=2.5G` (không cần `DOTNET_GCHeapHardLimit`), qpdf dưới 512 MB. Công cụ chạy qua `/usr/bin/setsid` (exec tại chỗ khi .NET khởi động nó: PID = PGID, đã kiểm) và `prlimit --as=N --` khi có giới hạn, cả hai tìm ở `/usr/bin`, `/bin`, không qua PATH (`Unix/UnixGroup.cs`). Thay cho job object: xong, hủy hay hết giờ thì `kill(-pgid, SIGKILL)` cả nhóm. `MemoryLimitExceeded` trên Linux: có giới hạn và (chết vì tín hiệu, hoặc stderr có `bad_alloc`/`out of memory`/`cannot allocate memory`/`memory exhausted`/`OutOfMemoryException`). Không làm được trên Linux, ghi vào ADR-0010: công cụ không tự chết khi flevert chết (`setpriv --pdeathsig` gắn với luồng đã fork, .NET fork từ thread pool), không giới hạn "một tiến trình". `ExternalToolTests`/`ExternalToolLineTests` (cmd, PowerShell, ping) không build trên Linux; `UnixExternalToolTests` (25 test, cùng các hành vi bằng `sh`, `cat`, `sleep`, `dd`, `env`, `pwd`) đỏ 18/25 trước khi làm; đột biến bỏ `kill` nhóm → test "để lại tiến trình" đỏ, bỏ `prlimit` → test bộ nhớ đỏ. `Qpdf.ToExtendedPath` không thêm `\\?\` trên Linux (không có giới hạn 260 ký tự). Test Pdf: đường dẫn qpdf theo nền tảng; `TestPdf.Native` (dữ liệu `C:\…` → `/…`) cho test giao thức worker, `TestPdf.Long` (`\\?\` chỉ trên Windows) cho test đường dẫn dài, nhờ đó `TempDir` của Pdf.Tests xóa được thư mục tạm trên Linux (trước đó để lại); `PackagingTests` có bản Linux (`libpdfium.so`, `qpdf/bin/qpdf` còn quyền chạy, `qpdf/lib/*`); test OpenSSL, khóa file, `cmd.exe`, tên có surrogate lẻ (NTFS) → `[WindowsFact]`. **Tìm thấy:** watermark và đánh số trang dùng Arial của Windows: thêm vào T6. Tên file dài: Linux giới hạn 255 **byte** (Windows 255 ký tự UTF-16), tiếng Việt nhiều byte → `PathTooLongException`: T7.
- Phụ thuộc: T3. Cỡ: M

### T5: Thứ tự tự nhiên như Nautilus
- [x] `NaturalOrder.Compare` trên Linux: dãy số so theo giá trị, không phân biệt hoa/thường, giống `g_utf8_collate_key_for_filename`
- Kiểm: test đối chiếu với thứ tự do GLib tính trên máy (`python3 -c` với `GLib.utf8_collate_key_for_filename` khi có, bỏ qua khi không)
- Đã làm (2026-10-21): đo GLib trước: thứ tự theo luật collation của locale, không phải so sánh tự nhiên đơn giản (spec cập nhật). `Unix/UnixNaturalOrder.cs` gọi `g_utf8_collate_key_for_filename` (`libglib-2.0.so.0`, so khóa như `strcmp`) sau `setlocale(LC_COLLATE, "")` (.NET không tự gọi `setlocale`, khi đó libc sắp theo locale "C"); không có GLib → so sánh tự nhiên trong Flevert. Test: 30 tên (tiếng Việt, hoa/thường, số 20 chữ số, `.`/`-`/`(1)`) so với GLib qua `/usr/bin/python3 -I` cùng locale; đỏ trước khi làm; đột biến tắt GLib → đỏ. 3 test của Explorer (bỏ qua hoa/thường hoàn toàn, chữ có dấu cạnh chữ gốc, số quá 64 bit so như chữ) → `[WindowsFact]`; test đường dẫn đổi dữ liệu sang `C:/…`.
- Phụ thuộc: T3. Cỡ: S

### T6: Thư mục riêng và native nhỏ
- [ ] `SecureDirectory` trên Linux: tạo/đặt quyền 0700; `AppData.Root` = `~/.local/share/Flevert`
- [ ] Pdf: `FileIdentity` (`st_dev`, `st_ino`), `PrivateFolder` (`mkdir` 0700, không theo symlink), `PdfRenderBudget.FreeBytes` (`statvfs`)
- [ ] Image: `ConfigurationFile` mở không theo symlink
- Kiểm: test quyền 0700, test identity (cùng file qua hai tên → khác vì hard link bị từ chối ở T4; cùng file hai lần mở → bằng nhau)
- Phụ thuộc: T3. Cỡ: M

### T7: Thư mục kết quả 1→N (PartialFolder)
- [ ] `UnixPartialFolder`: `mkdir` 0700 tên ẩn, giữ fd `O_DIRECTORY|O_NOFOLLOW`, đổi tên `renameat2(RENAME_NOREPLACE)` (EINVAL/ENOSYS → `link`-free fallback: kiểm tên đích rồi `rename`, ghi rủi ro), dọn bằng `unlinkat` theo fd, không đi vào thư mục con
- Kiểm: test có sẵn của `OutputPath` cho thư mục chạy trên Linux; test âm: tên đích bị chiếm giữa chừng, thư mục bị thay bằng symlink
- Phụ thuộc: T3, T6. Cỡ: M

### Mốc: phần nền tảng
- [ ] Core, Image, Pdf xanh trên Linux, trừ test cần `flevert` thật

## Giai đoạn 3: ứng dụng

### T9: `flevert` headless
- [ ] `Flevert.Linux/Program.cs`: `run` (dùng `BatchLeader.Alone`, `Runner`, song song như Windows), `pdf-worker`, `list-ops`; `FLEVERT_HEADLESS=1` chạy không giao diện
- [ ] Test tích hợp đang chạy `flevert.exe` dùng helper tìm file thực thi theo nền tảng
- Kiểm: `flevert run image.to-jpg x.heic` thật; test tích hợp Image/Pdf xanh
- Phụ thuộc: T4–T8. Cỡ: M

### T10: Cửa sổ tiến độ và thông báo
- [ ] `ZenityProgress`: hiện sau 2 s (như Windows), `NN` và `# text` từ `BatchProgress`; Cancel → hủy lô
- [ ] `Notifier`: `gdbus call … org.freedesktop.Notifications.Notify` với `BatchSummary.Text`; không có D-Bus thì chỉ ghi log
- Kiểm: test với zenity/gdbus giả (script ghi lại đối số và stdin); chạy thật một lô 20 ảnh
- Phụ thuộc: T9. Cỡ: M

### T11: Hộp thoại mật khẩu và watermark
- [ ] `IPasswordPrompt`/watermark prompt qua `zenity --password` và `--entry --hide-text`; hỏi một lần cho lô; Cancel → bỏ lô
- Kiểm: test với zenity giả; test rò rỉ: mật khẩu không có trong `/proc/*/cmdline` của mọi tiến trình con, không có trong log
- Phụ thuộc: T9. Cỡ: S

### T12: Menu Nautilus
- [ ] `flevert install-menu`/`uninstall-menu` trên Linux: ghi `menu.json` (đường dẫn `flevert`, cây menu từ catalog: nhãn, nhóm, menu con `More PDF tools`, đuôi file) và chép `flevert_menu.py` vào `~/.local/share/nautilus-python/extensions/`; từ chối cài khi thư mục chứa `flevert` ghi được bởi người khác (như `InstallLocation` trên Windows)
- [ ] `flevert_menu.py` (MIT): API 3.0 và 4.0, chỉ file cục bộ, lọc theo đuôi, gọi `flevert run <op> <files…>` bằng danh sách đối số, không shell; tối đa 1000 file
- Kiểm: test C# cho `menu.json`; `python3 -m unittest` với module Nautilus giả (3.0, 4.0); cài từ bản build, `nautilus -q`, bấm thử
- Phụ thuộc: T9. Cỡ: M

### Mốc: ứng dụng
- [ ] Ba project xanh; bấm thử trong Nautilus mỗi nhóm thao tác một lần

## Giai đoạn 4: đóng gói và tài liệu

### T13: Tarball và install.sh
- [ ] `scripts/build-linux.sh`: `dotnet publish src/Flevert.Linux -c Release -r linux-x64 --self-contained -p:PublishReadyToRun=true`, kèm deps, notices, extension; tạo `artifacts/Flevert-<version>-linux-x64.tar.gz`
- [ ] `install.sh`: kiểm `python3-nautilus`, `zenity`, `gdbus`; chép vào `~/.local/share/flevert/app` (0755, chỉ user ghi được); `install-menu`; `--uninstall` gỡ sạch
- Kiểm: cài, bấm, gỡ trên máy này; không còn file sót
- Phụ thuộc: T12. Cỡ: M

### T14: Tài liệu
- [ ] ADR-0010 (các quyết định và điểm yếu hơn Windows), README mục Linux, CHANGELOG `[Unreleased]`, CONTRIBUTING (lệnh Linux)
- Phụ thuộc: T13. Cỡ: S

### T15: CI Ubuntu (hỏi người dùng trước khi sửa `.github/`)
- [ ] Job `build-and-test-linux` trong `ci.yml`: fetch deps, build, format, ba project test lần lượt
- Phụ thuộc: T13. Cỡ: S

## Kiểm một lần trước PR
- [ ] Chạy thật: cài từ tarball, mọi thao tác ảnh và PDF trong Nautilus, Cancel, mật khẩu, gỡ
- [ ] Ba review bối cảnh trống, chỉ đọc (code-reviewer, security-auditor, doubt) trên `git diff main..feat/linux`; bảng reconcile ở đây; sửa; một reviewer soát lại tới khi hết Critical/High/Required
- [ ] Đủ 10 tiêu chí của spec

## Bảng test trên Linux (T3)

Lần chạy đầu (2026-10-21, sau T2), mỗi project chạy riêng:

| Project | Xanh | Đỏ | Bỏ qua |
|---|---|---|---|
| Core | 250 | 123 | 13 |
| Image | 0 | 168 | 0 |
| Pdf | 221 | 1094 | 3 |

**Không build trên Linux** (chỉ có nghĩa trên Windows; `<Compile Remove>` có lý do trong csproj): Core: `BatchTests`, `ProcessBatchIntegrationTests`, `ProcessFactsTests` (gom lô qua pipe giữa nhiều tiến trình), `MenuRegistryIntegrationTests` (registry thật), `CheckoutScriptTests`, `FetchDepsScriptTests`, `PreviousSetupScriptTests`, `BuildPathsScriptTests`, `NoticesScriptTests`, `InstallerScriptTests`, `PowerShellScript`, `QpdfDepsTests` (script PowerShell, Inno Setup, DLL MSVC), `LongPathTests` (tên 8.3, `\\?\`), `UpdateCheckIntegrationTests` (báo bản mới tắt trên Linux). Pdf: `WatermarkWindowTests` (WinForms + UI Automation).

**`[WindowsFact]`/`[WindowsTheory]`** (ACL Windows): 12 test `InstallLocation` trong `MenuTests`, 1 trong `OutputPathTests` (anchor bị từ chối bằng ACL), 8 trong `MagickSetupTests` (ACL của thư mục cấu hình, `icacls`), `PdfSplitTests.The_pages_get_the_permissions_of_the_folder_like_any_new_file`, `PdfStripMetadataTests.The_private_folder_is_only_for_this_user_system_and_administrators`. T6 viết bản Linux tương ứng (quyền 0700).

**Đỏ, theo nguyên nhân → task:**

| Nguyên nhân | Test (lớp: đỏ/tổng) | Task |
|---|---|---|
| `InputPin` (kernel32), quy tắc đường dẫn Windows trong `Runner` | Runner 22/54, RunnerBatchOperation 21/28, RunnerParallel 6/7, RunnerFileProgress 8/9, OutputNote 4/7 | T4 |
| `NaturalOrder` (shlwapi) | NaturalOrder 8/8 (và các join sắp xếp đầu vào) | T5 |
| `MagickSetup` chưa có bản Linux (chặn khởi tạo module test) | Image 168/168; Pdf: JpegSegments, PdfBuilder, ImagesToPdf, PdfCompressSharing… | T6 |
| `PrivateFolder`, `FileIdentity`, `PdfRenderBudget` (kernel32) | PdfSplit, PdfStripMetadata, PdfFlatten, PdfRenderWorker, PdfToImages… | T6 |
| `PartialFolder` (kernel32), junction | OutputPath 29/45, RunnerFileProgress | T7 |
| `ExternalTool` chỉ chạy `.exe`, test dùng `cmd.exe`/`powershell.exe`/`findstr.exe` | ExternalTool 19/30, ExternalToolLine 4/4; Pdf: mọi lớp gọi qpdf (PdfHardening 644/646, Qpdf 18/48, PdfProtect, PdfRotate, PdfMerge…) | T8 |
| Test tìm `qpdf.exe` cạnh test (`QpdfTests.Finds_qpdf…`, `QpdfSelectPagesTests`, `PdfFlattenTests`) | | T8 |
| `flevert` thật (`FlevertExeTests`, `PackagingTests`, `PdfWorker*Tests`, `PasswordLeakTests`, `TestProcessesTests`) | | T9 |
| Menu registry với đường dẫn exe kiểu Windows (`MenuTests` 2, `PdfMenuTests` 3) | | T12 |

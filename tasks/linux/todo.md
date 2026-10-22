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
- [x] `SecureDirectory` trên Linux: tạo/đặt quyền 0700; `AppData.Root` = `~/.local/share/Flevert`
- [x] Pdf: `FileIdentity` (`st_dev`, `st_ino`), `PrivateFolder` (`mkdir` 0700, không theo symlink), `PdfRenderBudget.FreeBytes` (`statvfs`)
- [x] Image: `ConfigurationFile` mở không theo symlink
- [x] Font của watermark trên Linux (thêm lúc làm, xem spec)
- Kiểm: test quyền 0700, test identity (cùng file qua hai tên → khác vì hard link bị từ chối ở T4; cùng file hai lần mở → bằng nhau)
- Đã làm (2026-10-21), 4 commit: (1) `SecureDirectory.RestrictToOwner`: mở `O_PATH|O_NOFOLLOW|O_DIRECTORY`, kiểm là thư mục của chính user (`statx`, `geteuid`), `chmod 0700` qua `/proc/self/fd/N` của handle đã kiểm; `TrySecure` tạo 0700; 4 test, đột biến bỏ `O_NOFOLLOW` → đỏ. (2) ImageMagick: thư mục cấu hình 0700, file mới 0600, bản có sẵn chỉ giữ khi là file thường 1 tên, của user, không ai khác ghi được, đúng byte (mở `O_NOFOLLOW|O_NONBLOCK`); 5 test Linux + `A_stale_or_tampered…` chạy cả hai nền tảng; đột biến bỏ kiểm `nlink`/quyền ghi → 2 test đỏ. Toàn bộ Image chạy được trên Linux (229 xanh, trước là 0). (3) `PrivateFolder` = `mkdir(0700)` (lỗi nếu tên đã có, kể cả symlink), `FileIdentity` = (dev, ino) từ `statx`, `FreeBytes` = `DriveInfo` (đã đo: nhận thư mục bất kỳ trên Linux, khớp `df`). (4) `SystemFont`: Arial trên Windows, trên Linux `fc-match` (máy thử: Arimo, rồi Liberation Sans); test: file `.ttf` có đủ chữ Việt. `InternalsVisibleTo` Flevert.Image/Flevert.Pdf trong Core cho `Libc`.
- Phụ thuộc: T3. Cỡ: M

### T7: Thư mục kết quả 1→N (PartialFolder)
- [x] `UnixPartialFolder`: `mkdir` 0700 tên ẩn, giữ fd `O_DIRECTORY|O_NOFOLLOW`, đổi tên `renameat2(RENAME_NOREPLACE)` (EINVAL/ENOSYS → `link`-free fallback: kiểm tên đích rồi `rename`, ghi rủi ro), dọn bằng `unlinkat` theo fd, không đi vào thư mục con
- Kiểm: test có sẵn của `OutputPath` cho thư mục chạy trên Linux; test âm: tên đích bị chiếm giữa chừng, thư mục bị thay bằng symlink
- Đã làm (2026-10-21): `IPartialFolder` cho `OutputPath` (Windows: `PartialFolder` giữ nguyên, Linux: `Unix/UnixPartialFolder.cs`). Linux: thư mục tạm `mkdir 0700`, liệt kê và xóa qua `/proc/self/fd/N` + `unlinkat` theo fd; chỉ đổi tên khi tên còn trỏ đúng (dev, ino) của thư mục đang giữ; xong thì quyền `0777 & ~umask` (umask đọc từ `/proc/self/status`, không gọi `umask(2)` vì đổi cho mọi luồng). `OutputPath`: kiểm tên qua `C:\`, mã "đang bận" 5/32 và bỏ dấu chấm/khoảng trắng cuối chỉ còn trên Windows (trên Linux 5/32 là `EIO`/`EPIPE`, và kiểm `C:\` luôn từ chối). **Độ dài tên:** `OutputPath.FitName` (công khai) đo 255 byte UTF-8 trên Linux, cắt giữa các ký tự; 4 bản chép cùng đoạn cắt tên (`DevSplit`, `PdfSplit`, `PdfSplitBySize`, `PdfToImages`) dùng nó. 12 test `UnixOutputPathTests` (đỏ 9/10 trước khi làm); đột biến bỏ `RENAME_NOREPLACE` lúc đầu **sống**: thêm test gọi thẳng `TryRenameTo` khi tên bị chiếm giữa chừng → bắt được; bỏ kiểm tên-còn-trỏ-đúng → đỏ. Windows-only (16 trong `OutputPathTests`: junction, thuộc tính ẩn/index/OneDrive, file neo, chờ chương trình khác đóng file; các test đếm 255 *ký tự* của Split/PdfToImages; khóa file). Bản Linux: `A_long_vietnamese_name_is_shortened…_255_bytes`. Hai test tiến độ/hủy của Split dựa vào qpdf chạy chậm: trên Linux qpdf tách 1 500 trang trong 0,10 s (đo), trước lần nhìn đầu (200 ms), mà trần là 2 000 trang → Windows-only, ghi lý do trong test.
- Phụ thuộc: T3, T6. Cỡ: M

### Mốc: phần nền tảng
- [x] Core, Image, Pdf xanh trên Linux, trừ test cần `flevert` thật (2026-10-21: Core 344 xanh, 2 đỏ là menu registry của T12; Image 229 xanh, 2 đỏ cần `flevert`; Pdf: các đỏ còn lại cần `flevert`/worker (T9), `PasswordLeakTests` (T11), menu (T12))

## Giai đoạn 3: ứng dụng

### T9: `flevert` headless
- [x] `Flevert.Linux/Program.cs`: `run` (dùng `BatchLeader.Alone`, `Runner`, song song như Windows), `pdf-worker`, `list-ops`; `FLEVERT_HEADLESS=1` chạy không giao diện
- [x] Test tích hợp đang chạy `flevert.exe` dùng helper tìm file thực thi theo nền tảng
- Kiểm: `flevert run image.to-jpg x.heic` thật; test tích hợp Image/Pdf xanh
- Đã làm (2026-10-21): `Program` Linux chạy cả lô một lần (`BatchLeader.Alone`, giờ `public`), song song như Windows nhưng **không** hạ độ ưu tiên (Linux cho user hạ `nice` mà không cho nâng lại), từ chối quá 1 000 file (Open Question 5). `tests/Shared/BuiltFlevert.cs` thay 3 bản chép tìm `flevert.exe`; `ProcFs.cs` (đọc `/proc`) thay API NT cho `TestProcesses`/`ProcessPeek` trên Linux. `UnixFlevertTests` (6, đỏ 4 trước khi làm). **Ba phát hiện ở worker PDF**, mỗi cái có test: (1) `prlimit --as` làm worker .NET hết bộ nhớ (đo: 35 GB địa chỉ ảo, 27 MB RSS) → `--data` (`RLIMIT_DATA`, gần "committed" của job). (2) Worker mồ côi quay 100% CPU hơn 10 phút sau khi test host chết (thấy thật) → `setpriv --pdeathsig KILL`, mọi công cụ khởi động từ luồng riêng `UnixStarter` (tín hiệu gắn với luồng đã tạo tiến trình); test "công cụ sống lâu hơn luồng gọi" lúc đầu **có race** (luồng chết trước khi `setpriv` đặt tín hiệu → kernel chuyển con sang luồng khác) nên đột biến sống: sửa cho luồng chờ công cụ chạy rồi mới chết → đột biến bị bắt 2/2 lần; `FlevertExeTests.Killing_flevert…ends_qpdf_too` đạt trên Linux. (3) pdfium bị từ chối bộ nhớ thì `abort` không in gì (bom 4 GiB: thoát 134, RSS 2,5 GB) → lấy mẫu `VmData` 50 ms, chết vì tín hiệu (trừ SIGKILL) khi đã dùng ≥ 3/4 hạn mức là "vượt"; bỏ luật cũ "mọi cái chết vì tín hiệu" (worker bị giết bị báo nhầm). Test với Python (330 MB + SIGABRT → vượt; SIGKILL → không; 10 MB + SIGABRT → không). Toàn bộ 656 test hardening + worker xanh. Windows-only: 6/3 tiến trình `flevert.exe` gom lô (Explorer); bản Linux: một `flevert` nhận 6 ảnh, ghép theo thứ tự Nautilus. Số trang: Helvetica không nhúng, pdfium trên Linux vẽ bằng Nimbus Sans cao hơn ≤ 5 pt trên cùng baseline (nội dung PDF: `Tm … 28`, đã kiểm) → test nới trên Linux, có lý do.
- Phụ thuộc: T4–T8. Cỡ: M

### T10: Cửa sổ tiến độ và thông báo
- [x] `ZenityProgress`: hiện sau 2 s (như Windows), `NN` và `# text` từ `BatchProgress`; Cancel → hủy lô
- [x] `Notifier`: `gdbus call … org.freedesktop.Notifications.Notify` với `BatchSummary.Text`; không có D-Bus thì chỉ ghi log
- Kiểm: test với zenity/gdbus giả (script ghi lại đối số và stdin); chạy thật một lô 20 ảnh (để ở bước kiểm cuối, báo người dùng trước vì bật cửa sổ)
- Đã làm (2026-10-21): `Core/Unix/UnixDesktop.cs`: `ZenityProgress` (zenity qua `setsid`/`setpriv`, chết theo flevert; chữ escape markup Pango; một dòng; 99% tối đa để `--auto-close` không đóng sớm), `DesktopNotification` (đối số GVariant có kiểu: `uint32 0`, `@as []`, `@a{sv} {}`, `int32 6000`; chuỗi trong `'…'` escape `\`, `'`, ký tự điều khiển; body escape markup), `UnixBatchUi` (cửa sổ sau 2 s, Cancel hủy lô, một thông báo). Đường dẫn zenity/gdbus truyền vào (test dùng script giả, không qua biến môi trường). 10 test; đột biến bỏ escape → 2 test đỏ; bỏ cờ "flevert tự đóng" lúc đầu **sống** (Dispose hủy `Process` trước khi luồng theo dõi thấy zenity thoát) → `Dispose` chờ luồng theo dõi, đột biến bị bắt. `Program`: headless như cũ, còn lại qua `UnixBatchUi`; quá 1 000 file có thông báo. `Flevert.Linux` khai `[assembly: SupportedOSPlatform("linux")]`, TFM `net10.0` cố định; `Flevert.sln` không tự build nó (trên Windows nó không tham chiếu được Core `net10.0-windows`), test trên Linux build nó qua tham chiếu.
- Phụ thuộc: T9. Cỡ: M

### T11: Hộp thoại mật khẩu và watermark
- [x] `IPasswordPrompt`/watermark prompt qua `zenity --password` và `--entry --hide-text`; hỏi một lần cho lô; Cancel → bỏ lô
- Kiểm: test với zenity giả; test rò rỉ: mật khẩu không có trong `/proc/*/cmdline` của mọi tiến trình con, không có trong log
- Đã làm (2026-10-21): `zenity --forms` thay cho `--password`/`--entry` (cần hai ô khi đặt mật khẩu và ô chọn "All N files / This file only" như hộp kiểm của Windows; mặc định là tất cả như hộp kiểm đã tích); watermark là ô thường (`--add-entry`), như bản Windows. `Core/Unix/UnixDesktop.cs`: `ZenityDialog` đọc stdout vào mảng byte cố định 64 KB, `ZenityReply.Dispose` xóa trắng; `IUnixQuestion` (đang hỏi, tiến độ). `Pdf/Unix/ZenityPrompts.cs`: hỏi lại kèm lý do theo `PasswordRules`/`WatermarkPrompts` của Windows; mật khẩu thành `char[]` cho `PdfPassword`, ô nhập lại xóa trắng sau khi so. `UnixBatchUi`: cửa sổ tiến độ chờ khi hộp thoại mở rồi thêm 2 s. 8 test prompt + 1 test luồng; **lỗi tìm thấy bằng test**: xóa trắng ô nhập lại *trước* khi so → mọi lần đặt mật khẩu "không khớp"; đột biến bỏ kiểm luật/escape/cờ đang hỏi → đỏ. `PasswordLeakTests` đạt trên Linux (đọc qpdf qua `/proc`) sau khi sửa test: nó quét `/tmp` và treo khi mở một FIFO (chờ người ghi mãi) → bỏ qua mục rỗng (FIFO, socket có độ dài 0). `dotnet-dump` (công cụ của Microsoft, cài cho user) dùng để đọc stack của test treo.
- Phụ thuộc: T9. Cỡ: S

### T12: Menu Nautilus
- [x] `flevert install-menu`/`uninstall-menu` trên Linux: ghi `menu.json` (đường dẫn `flevert`, cây menu từ catalog: nhãn, nhóm, menu con `More PDF tools`, đuôi file) và chép `flevert_menu.py` vào `~/.local/share/nautilus-python/extensions/`; từ chối cài khi thư mục chứa `flevert` ghi được bởi người khác (như `InstallLocation` trên Windows)
- [x] `flevert_menu.py` (MIT): API 3.0 và 4.0, chỉ file cục bộ, lọc theo đuôi, gọi `flevert run <op> <files…>` bằng danh sách đối số, không shell; tối đa 1000 file
- Kiểm: test C# cho `menu.json`; `python3 -m unittest` với module Nautilus giả (3.0, 4.0); cài từ bản build, `nautilus -q`, bấm thử (cần `sudo apt install python3-nautilus`: hỏi người dùng ở bước kiểm cuối)
- Đã làm (2026-10-21): `Core/Menu/NautilusMenu.cs` dựng cây **cho từng đuôi** bằng chính `MenuInstaller.MenuNode` (đổi `private` → `internal`, không đổi hành vi Windows), nên thứ tự giống hệt Windows; menu thứ hai (Shift trên Windows) thành menu con "More PDF tools" cuối menu Flevert; không icon, không đường kẻ (Nautilus không vẽ). Chọn nhiều loại file: cây của file đầu, chỉ giữ thao tác nhận *mọi* đuôi đã chọn. Extension đọc lại `menu.json` mỗi lần chuột phải (cài lại không cần khởi động lại Nautilus), chạy `flevert` qua `subprocess` không shell, luồng nền thu dọn tiến trình con (không zombie trong Nautilus). `Core/Unix/UnixMenu.cs`: ghi file tạm rồi `rename` (symlink đặt sẵn bị thay, không bị ghi xuyên); `UnixInstallLocation`: thư mục và mọi thư mục cha là thư mục thật (không symlink), của user hoặc root, không ai khác ghi được; thư mục cha người khác ghi được chỉ chấp nhận khi có sticky bit. Repo trên máy thử có quyền 775 (umask 002) → bản build dev cần `--allow-insecure-location`, như Windows. Thiếu nautilus-python → in lệnh cài theo distro. Test: 12 Python (API 3.0/4.0, lọc, chạy không shell, file menu hỏng, `flevert` không hợp lệ), 3 C# `menu.json` (chạy cả hai nền tảng), 8 cài/gỡ/vị trí (gồm `flevert install-menu` thật bị từ chối khi thư mục ghi được bởi người khác), 2 liên thông (catalog thật → `menu.json` → extension thật dựng menu PDF và ảnh). 5 test menu registry → `[WindowsFact]`.
- Phụ thuộc: T9. Cỡ: M

### Mốc: ứng dụng
- [x] Ba project xanh; bấm thử trong Nautilus mỗi nhóm thao tác một lần (ba project: xem bảng cuối; bấm thử: bước kiểm cuối, cần `python3-nautilus`)

## Giai đoạn 4: đóng gói và tài liệu

### T13: Tarball và install.sh
- [x] `scripts/build-linux.sh`: `dotnet publish src/Flevert.Linux -c Release -r linux-x64 --self-contained -p:PublishReadyToRun=true`, kèm deps, notices, extension; tạo `artifacts/Flevert-<version>-linux-x64.tar.gz`
- [x] `install.sh`: kiểm `python3-nautilus`, `zenity`, `gdbus`; chép vào `~/.local/share/flevert/app` (0755, chỉ user ghi được); `install-menu`; `--uninstall` gỡ sạch
- Kiểm: cài, bấm, gỡ trên máy này; không còn file sót (cài thật: bước kiểm cuối)
- Đã làm (2026-10-21): app ở `~/.local/share/Flevert/app` (cùng thư mục với dữ liệu của Flevert, 0700; thay cho `~/.local/share/flevert/app` của plan để không có hai thư mục `flevert`/`Flevert`). `install.sh` kiểm `setsid setpriv prlimit gdbus zenity fc-match`, chép ra thư mục tạm rồi đổi tên (cài đè không để lại bản cũ), `go-w`, gọi `install-menu` (in lệnh cài nautilus-python khi thiếu); `--uninstall` gỡ menu và xóa cả thư mục. `scripts/write-notices-linux.py`: bản Python của `write-notices.ps1`, cùng luật (gói NuGet không có giấy phép, file không ai che, mục notice không có file → lỗi); lần chạy thật đầu tiên bắt được `deps-linux.json` ghi tên `qpdf` thay vì `qpdf/bin/qpdf` (đã sửa). `build-linux.sh`: gói 59 MB (thư mục 134 MB), 92 mục notice, không file `.pdb`, tar có thứ tự và owner 0. Chạy thử bản publish không cần .NET: HEIC → JPG, xoay PDF đạt; khởi động ~60 ms (tiêu chí ≤ 300 ms). Test: 4 `install.sh` (flevert giả, HOME tạm), 6 notices.
- Phụ thuộc: T12. Cỡ: M

### T14: Tài liệu
- [x] ADR-0010 (các quyết định và điểm yếu hơn Windows), README mục Linux, CHANGELOG `[Unreleased]`, CONTRIBUTING (lệnh Linux)
- Phụ thuộc: T13. Cỡ: S

### T15: CI Ubuntu (hỏi người dùng trước khi sửa `.github/`)
- [x] Job `build-and-test-linux` trong `ci.yml`: fetch deps, build, format, ba project test lần lượt (người dùng đồng ý 2026-10-21; thêm cài `fontconfig fonts-liberation2 python3-gi`, test Python, đóng gói; job Windows không đổi; chưa chạy được ở local, kiểm khi mở PR)
- Phụ thuộc: T13. Cỡ: S

## Kiểm một lần trước PR
- [ ] Chạy thật: cài từ tarball, mọi thao tác ảnh và PDF trong Nautilus, Cancel, mật khẩu, gỡ
  - 2026-10-21, lần bấm thử đầu của người dùng: ảnh, PDF qua qpdf, mật khẩu đạt; **mọi tác vụ của PDF worker (pdfium) báo "not a PDF" trong gói**, Debug/Release/publish không ReadyToRun đều chạy. Nguyên nhân: `unsigned long` của pdfium là 64 bit trên Linux, Flevert khai `uint` (đúng trên Windows), nên `m_FileLen` của `FPDF_FILEACCESS` có 4 byte cao là rác; JIT tình cờ để 0, ReadyToRun thì không. Sửa: `CULong` cho mọi `unsigned long`/`FPDF_DWORD` của pdfium (FILEACCESS, FILEWRITE, `GetLastError`, `FillRect`, `FormObj_GetObject`, `GetImageDataRaw`, `GetImageFilter`, `GetMetaText`, `SaveAsCopy`), đã đối chiếu header của bản chromium/8100 đã ghim; ABI Windows không đổi. `build-linux.sh` giờ chạy thử bản publish (pdfium to-jpg và page-numbers, qpdf, ImageMagick) trước khi đóng gói: gói cũ trả exit 1 ở bước này. Người dùng bấm lại: Add page numbers và Add watermark đạt.
  - Gỡ (`install.sh --uninstall`) để lại `nautilus-python/extensions/__pycache__/flevert_menu.cpython-310.pyc` (Python tạo khi Nautilus import extension): `uninstall-menu` giờ xóa `flevert_menu.*.pyc` và thư mục cache khi rỗng, không theo link (2 test).
  - Chưa kiểm bằng tay: Cancel (lô 20 PNG xong trước khi cửa sổ tiến độ hiện ra sau 2 s); có test tự động cho Cancel của `UnixBatchUi`.
- [ ] Ba review bối cảnh trống, chỉ đọc (code-reviewer, security-auditor, doubt) trên `git diff main..feat/linux`; bảng reconcile ở đây; sửa; một reviewer soát lại tới khi hết Critical/High/Required
- [ ] Đủ 10 tiêu chí của spec

### Reconcile ba review (2026-10-21)

CR = code-reviewer, SA = security-auditor, DD = doubt-driven. "Kiểm" = tôi tự kiểm lại trước khi xếp mức.

| # | Phát hiện | Nguồn | Mức | Kiểm | Quyết định |
|---|---|---|---|---|---|
| R1 | Input được pin (O_PATH) nhưng thao tác mở lại **theo tên**: đổi file thành symlink sau khi kiểm thì đọc được file riêng của người dùng, bản chuyển đổi nằm trong thư mục chung; ADR nói sai ("đường dẫn kernel giữ") | CR, SA, DD | High | Đúng; đã thử: tiến trình con (qua setsid/setpriv, Yama 1) đọc `/proc/<pid>/fd/N` của handle O_PATH ra file gốc sau khi tên bị thay | Sửa: `PinnedInput` (đọc qua `/proc/<flevert>/fd/N`, kể cả qpdf và worker; request worker bản 3 mang cả hai đường dẫn; lời qpdf hiện tên). Test: mọi thao tác (33) chạy khi tên bị đổi thành link tới rác ngay sau khi ghim, so với bản đối chứng không ghim; kiểm đột biến: đo độ dài qua link bị bắt. Lỗi phụ tìm ra khi sửa: `FileInfo` của `/proc/.../fd/N` cho độ dài của link (64 byte), nén và tách theo cỡ bỏ qua mọi file: đo qua handle (`PinnedInput.Length`). ADR, spec đã sửa |
| R2 | Thư mục output ghi qua tên, không qua handle đang giữ | SA | Low | Đúng | Sửa: người ghi nhận `WritePath` = handle (`/proc/<flevert>/fd/N`); test đổi thư mục thành link giữa chừng. Hệ quả: qpdf không còn thấy `%d` trong tên thư mục thật, nên trên Linux thư mục `50%done` được tách bình thường (test riêng); Windows vẫn từ chối |
| R3 | zenity chạy `g_strcompress` trước markup (`\074` thành `<`); dòng `#` của progress là `set_text` nên `&` hiện thành `&amp;` | SA | Low | Đúng, đọc `forms.c:255`, `progress.c:176,382` của zenity 3.42.1 | Sửa: `Markup.ForZenity` nhân đôi `\\`; dòng `#` của progress không thoát markup nữa. Test cho cả hai |
| R4 | Trường form tách bằng `\n`, không kiểm số trường | SA, CR | Low | Đúng | Sửa: `--separator=\u001e`, đúng số trường, không thì hỏi lại ("could not read the answer"). Test |
| R5 | Thiếu zenity/gdbus trên runner CI → test `install.sh` đỏ | DD | High (CI) | Đúng: `install.sh` đòi `/usr/bin/zenity` | Sửa (người dùng đồng ý 2026-10-22): thêm `zenity libglib2.0-bin` vào bước cài gói của job Linux |
| R6 | Restore trên Windows có thể lỗi NU1201 vì `Flevert.Linux` trong sln | DD | (High nếu đúng) | **Sai**: `OS=Windows_NT dotnet restore/build Flevert.sln` (worktree tạm, deps giả): Flevert.Linux không được restore; cả solution, gồm ba project test, build `net10.0-windows` 0 cảnh báo | Không sửa (sai) |
| R7 | PDF worker (một tiến trình) chạy `fc-match` | CR | Medium | Đúng | Sửa: cha tìm font, gửi trong `PdfEdit` (trường thứ ba, đường dẫn tuyệt đối); worker chỉ đọc file. Test giao thức |
| R8 | Không có test Linux cho Cancel giữa lúc qpdf ghi trang (SC5) | CR, DD | Medium | Đúng | Sửa: test Linux hủy khi qpdf (bọc bởi script vẫn chạy sau khi ghi xong) còn chạy: không còn thư mục tạm, < 10 s |
| R9 | Phát hiện quá bộ nhớ yếu hơn: cần exit ≠ 0; một cấp phát lớn bị từ chối không làm VmData tăng; kernel < 4.7 | DD | Medium | Đúng một phần | Sửa: exit ≠ 0 (trừ SIGKILL) và đỉnh ≥ 3/4 là quá giới hạn, gồm exit 3; thành công được tin. Test 5 trường hợp. Phần còn lại ghi ADR |
| R10 | Thiếu bằng chứng SC2, 3, 5, 10 | DD | Medium | Đúng | Đang làm: chạy mọi thao tác trên gói, đo khởi động; nhờ người dùng thử 20 HEIC + Cancel |
| R11 | Thứ tự Nautilus: tên bắt đầu `.` hoặc `#` xếp cuối | DD | Medium | Kiểm nguồn Nautilus | Đúng (nautilus-file.c 42.6 và 46.2, `SORT_LAST_CHAR1/2`). Sửa + test |
| R12 | `install-menu` khi thiếu nautilus-python vẫn báo đã vào menu (spec: in lệnh rồi dừng) | DD | Low | Kiểm | Sửa: in lệnh cài, trả `Failed`, không báo đã vào menu (menu.json vẫn ghi sẵn) |
| R13 | Log ghi trước `TrySecure` ở hai đường thoát sớm | CR, SA | Low | Đúng | Sửa: `TrySecure` đầu `RunBatch` |
| R14 | Lời lỗi nêu `setsid` thay vì tên công cụ | CR | Low | Đúng | Sửa lời hết giờ (tên công cụ); lỗi khởi động thật sự là của setsid nên giữ. Test |
| R15 | `out of memory`/`bad_alloc` trong tên file làm lời báo sai | CR, DD | Low | Đúng, an toàn (vẫn là lỗi) | Ghi nhận: lời báo sai nhưng vẫn là lỗi; không sửa |
| R16 | `maxFiles` không được extension đọc; `Popen` lỗi (E2BIG) im lặng | CR, DD | Low | Đúng | Sửa: extension đưa tối đa `maxFiles + 1` file (flevert từ chối và báo), bắt `OSError` của `Popen`; lời từ chối không còn nêu số file. Test |
| R17 | `AskAsync` ngừng đọc sau 64 KiB rồi chờ zenity không hạn | CR | Low | Đúng | Sửa: đọc bỏ phần dư (xóa), trả lời rỗng, hỏi lại. Test 300 KB |
| R18 | `WorkerEnvironment` so tên biến không phân biệt hoa thường trên Linux | CR | Low | Kiểm | Sửa: `Ordinal` trên Linux. Test |
| R19 | `TryStat` không kiểm `stx_mask` | CR | Low | Đúng | Sửa: `TryStat`/`TryStatNoFollow` đòi đủ type, mode, nlink, uid, ino; thiếu → EOPNOTSUPP |
| R20 | `setlocale` trong static init khi đã có luồng khác | CR | Low | Có thể | Sửa: `NaturalOrder.Prepare()` trước khi chạy join |
| R21 | `afterStart` ném thì không giết tool (chỉ test dùng) | CR | Low | Đúng | Sửa: giết nhóm rồi ném lại |
| R22 | `kill(-pgid)` sau khi đã reap; PID tái dùng | CR, SA, DD | Low | Đúng về lý thuyết; số pgid còn bị giữ khi nhóm còn tiến trình | Ghi chú trong `UnixGroup.Kill` |
| R23 | Kiểm vị trí cài chỉ xét thư mục, chỉ lúc cài | SA | Info | Đúng; `install.sh` `go-w` + umask 077 che | Ghi nhận |
| R24 | Nhánh dự phòng khi không có `RENAME_NOREPLACE` có thể thay thư mục rỗng | SA | Info | Đúng | Ghi ADR |
| R25 | libjpeg-turbo 2.1.2 cũ (đi kèm bản qpdf chính thức) | SA | Info | Đúng | Ghi ADR |
| R26 | pdeathsig: khe giữa fork và prctl; util-linux ≥ 2.33; mức tối thiểu glibc/libstdc++ chưa ghi | DD | Low | Đúng | Ghi ADR và README: glibc 2.34, GLIBCXX_3.4.29 (qpdf), util-linux 2.33 |
| R27 | `PasswordLeakTests` đọc file /tmp của user khác thì ném | DD | Low | Đúng | Không sửa: `Holds` đã bỏ qua file không đọc được (`UnauthorizedAccessException`) |
| R28 | Tài liệu cũ: bảng spec (`O_RDONLY`/`fstat`), mô tả `UnixExternalToolTests`, comment `UnixMemoryWatch`, summary `Prune` lệch | CR, DD | Nit | Đúng | Sửa |
| R29 | Trùng: `ENoSys`, 0700, `0x12`, dev/ino, `Markup`, `MaxFiles`; `FitName` dùng `!IsWindows()` | CR | Nit | Đúng | Sửa: `ENoSys` trùng, `Markup` trùng; phần còn lại giữ |
| R30 | Extension chỉ kiểm cấp ngoài của `menu.json` | CR | Nit | Đúng | Sửa: kiểm từng mục (nhãn chữ, op chữ hoặc children không rỗng), `operations`, `maxFiles`. Test |
| R31 | (tìm ra khi sửa R1) `dotnet build Flevert.sln` không build `Flevert.Linux` (không có trong cấu hình solution, và solution bỏ qua tham chiếu tới project nó không build): trên runner sạch mọi test chạy `flevert` thật sẽ đỏ | tự kiểm | High (CI) | Đúng: xóa `bin/obj` của Flevert.Linux rồi build solution → không có `flevert` | Sửa: `OnlyReferenceAndBuildProjectsEnabledInSolutionConfiguration=false` trong ba project test (chỉ Linux) |
| R32 | (tìm ra khi tái hiện) Khi `~/.local/share` (hoặc `XDG_DATA_HOME`) chưa có, `GetFolderPath` trả "", log và cấu hình thành đường dẫn tương đối theo thư mục flevert được chạy | tự kiểm | Medium | Đúng: log nằm trong thư mục đang đứng | Sửa: `AppData.UserFolder` dùng `DoNotVerify` trên Linux (AppData, menu, MagickSetup). Test |

### Soát lại các bản sửa (2026-10-22, một reviewer bối cảnh trống, code + bảo mật)

Kết luận của reviewer: **không còn Critical/High**. Bấm thử của người dùng tìm thêm một lỗi (R33).

| # | Phát hiện | Mức | Quyết định |
|---|---|---|---|
| R33 | (người dùng bấm thử SC5) Cancel trong cửa sổ tiến độ khi PDF → PNG để lại `.flevert-….partial` với 50 trang: `Runner` thôi chờ thao tác ngay lúc hủy (`WaitAsync(ct)`), flevert kết thúc trước khi thao tác xóa thư mục tạm | High | Sửa: thao tác bị hủy được chờ tối đa `StopGrace` (2 s, như join đã có) để dừng và dọn; test Core (thao tác dọn chậm) và test PDF qua `Runner` + worker thật, cả hai đỏ với `Runner` cũ |
| L1 | Đường dẫn `/proc/<pid>/fd/N` có thể lọt vào lời lỗi (.NET, worker) | Low | Sửa: `Runner` dùng `PinnedInput.ShowNames` cho mọi lời lỗi; lỗi trong thư mục output (handle thư mục) còn có thể hiện `/proc`: ghi nhận |
| L2 | Pin bị đóng khi `Runner` bỏ thao tác, thao tác còn đọc qua `/proc/.../fd/N` thì gặp fd khác | Low | Sửa cùng R33: pin được giữ tới khi thao tác thật sự kết thúc (`Release` trong continuation). Test Linux: thao tác bỏ qua token đọc lại sau 3 s vẫn ra file gốc |
| L3 | qpdf exit 3 gần giới hạn giờ bị từ chối | Low | Ghi ADR (an toàn, lời có thể sai) |
| L4 | Nhánh dự phòng rename coi "không mô tả được" là "trống" | Low | Sửa: chỉ ENOENT là trống |
| N1 | `DevCopy`/`DevJoin` mở theo tên | Nit | Sửa |
| N2 | `ShowNames` thay chuỗi con (`/fd/7` trong `/fd/70`) | Nit | Sửa: không thay khi theo sau là chữ số; test |
| N3 | Comment `MaxBytes` "two paths" | Nit | Sửa |
| N4, N5 | Font chứa U+001F; hook `AfterPin` | Nit | Giữ |
| S1, S2 | Đỉnh VmData bình thường của worker so với 3/4; zenity 4 (Ubuntu 24.04) | — | Mở: chưa có máy zenity 4; ghi ADR mục Nautilus 43+ |

## Bằng chứng tiêu chí (2026-10-22, gói từ `build-linux.sh`, cài bằng `install.sh`)

**SC2**: mọi thao tác của menu chạy qua `flevert` đã cài, headless, trên fixture (HEIC thật, PDF của test; JPG/PNG/PDF ảnh do chính flevert làm từ HEIC). "skipped" là đúng với fixture đó (không có trang trắng, đã nhỏ…). Mật khẩu và watermark cần cửa sổ: người dùng đã bấm thử trong Nautilus (Protect, Remove password, Add watermark, Add page numbers đạt). Chưa so nội dung byte với bản Windows (không có máy Windows); tên file kết quả giống bảng tên của bản Windows.

| Thao tác | Đầu vào | Kết quả | Trạng thái |
|---|---|---|---|
| `image.to-jpg` | ảnh 1.heic | ảnh 1.jpg | ok |
| `image.to-png` | ảnh 1.heic | ảnh 1.png | ok |
| `image.to-webp` | ảnh 1.heic | ảnh 1.webp | ok |
| `image.to-avif` | ảnh 1.heic | ảnh 1.avif | ok |
| `image.to-gif` | ảnh 1.jpg | ảnh 1.gif | ok |
| `image.to-bmp` | ảnh 1.heic | ảnh 1.bmp | ok |
| `image.to-tiff` | ảnh 1.heic | ảnh 1.tiff | ok |
| `image.compress-light` | ảnh 1.heic | ảnh 1 (compressed).jpg | ok |
| `image.compress-balanced` | ảnh 1.heic | ảnh 1 (compressed).jpg | ok |
| `image.compress-strong` | ảnh 1.heic | ảnh 1 (compressed).jpg | ok |
| `image.compress-under-500kb` | ảnh 1.heic | ảnh 1 (under 500 KB).jpg | ok |
| `image.compress-under-1mb` | ảnh 1.heic | ảnh 1 (under 1 MB).jpg | ok |
| `image.compress-under-5mb` | ảnh 1.heic | ảnh 1 (under 5 MB).jpg | ok |
| `image.strip-metadata` | ảnh 1.heic | ảnh 1 (no metadata).jpg | ok |
| `pdf.from-images` | ảnh 1.jpg | ảnh 1 (+1).pdf | ok |
| `pdf.compress` | Hồ sơ 1.pdf | Hồ sơ 1 (compressed).pdf | ok |
| `pdf.compress-sharing` | Hồ sơ 1.pdf | — | skipped: This PDF is already small: compressing it for sharing would not make it much small |
| `pdf.merge` | Hồ sơ 1.pdf | Hồ sơ 1 (merged).pdf | ok |
| `pdf.split` | Hồ sơ 1.pdf | Hồ sơ 1 (split) | ok |
| `pdf.to-jpg` | Hồ sơ 1.pdf | Hồ sơ 1 (pages) | ok |
| `pdf.extract-text` | Hồ sơ 1.pdf | — | skipped: This PDF has no text to extract: it may be a scanned document (Flevert does not re |
| `pdf.rotate-right` | Hồ sơ 1.pdf | Hồ sơ 1 (rotated).pdf | ok |
| `pdf.rotate-left` | Hồ sơ 1.pdf | Hồ sơ 1 (rotated).pdf | ok |
| `pdf.rotate-180` | Hồ sơ 1.pdf | Hồ sơ 1 (rotated).pdf | ok |
| `pdf.watermark` | Hồ sơ 1.pdf | — | failed: Flevert asks for the watermark text only in a window, and it runs without windows h |
| `pdf.protect` | Hồ sơ 1.pdf | — | failed: Flevert asks for a PDF password only in a window, and it runs without windows here |
| `pdf.unlock` | Hồ sơ 1.pdf | — | skipped: This PDF has no password. |
| `pdf.strip-metadata` | Hồ sơ 1.pdf | Hồ sơ 1 (no metadata).pdf | ok |
| `pdf.remove-blank-pages` | Hồ sơ 1.pdf | — | skipped: This PDF has no blank pages. |
| `pdf.page-numbers` | Hồ sơ 1.pdf | Hồ sơ 1 (numbered).pdf | ok |
| `pdf.n-up-2` | Hồ sơ 1.pdf | Hồ sơ 1 (2 per sheet).pdf | ok |
| `pdf.n-up-4` | Hồ sơ 1.pdf | Hồ sơ 1 (4 per sheet).pdf | ok |
| `pdf.fit-a4` | Hồ sơ 1.pdf | Hồ sơ 1 (A4).pdf | ok |
| `pdf.flatten` | Hồ sơ 1.pdf | — | skipped: This PDF has no filled forms or comments to lock. |
| `pdf.split-10mb` | Hồ sơ 1.pdf | — | skipped: This PDF is already under 10 MB: there is nothing to split. |
| `pdf.split-25mb` | Hồ sơ 1.pdf | — | skipped: This PDF is already under 25 MB: there is nothing to split. |
| `pdf.to-png` | Hồ sơ 1.pdf | Hồ sơ 1 (pages) | ok |
| `pdf.extract-images` | ảnh 1 (+1).pdf | ảnh 1 (+1) (images) | ok |

**SC10**: `flevert run dev.copy` trên file nhỏ, 12 lần, cache ấm: 87–110 ms, trung vị ~96 ms (`list-ops` 48 ms). Khởi động lạnh thật cần root để xóa page cache: chưa đo.

**SC3, SC5**: chờ người dùng bấm thử (`~/Flevert-test/kiem-cuoi`): 20 HEIC (xong dưới 1 s nên cửa sổ tiến độ không hiện, đúng thiết kế: chỉ hiện sau 2 s), Cancel trên PDF 450 trang (`pdf.to-jpg` mất ~12 s).

## Bảng test trên Linux (T3)

Lần chạy đầu (2026-10-21, sau T2), mỗi project chạy riêng:

| Project | Xanh | Đỏ | Bỏ qua |
|---|---|---|---|
| Core | 250 | 123 | 13 |
| Image | 0 | 168 | 0 |
| Pdf | 221 | 1094 | 3 |

Lần chạy cuối (2026-10-21, sau sửa `CULong`):

| Project | Xanh | Đỏ | Bỏ qua |
|---|---|---|---|
| Core | 376 | 0 | 51 |
| Image | 243 | 0 | 7 |
| Pdf | 1347 | 0 | 28 |

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

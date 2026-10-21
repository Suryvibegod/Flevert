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
- [ ] `scripts/deps-linux.json`: `pdfium-linux-x64.tgz` (chromium/8100), `qpdf-12.4.2-bin-linux-x86_64.zip`, SHA256 từ digest của GitHub; giấy phép như bản Windows
- [ ] `scripts/fetch-deps-linux.py` (thư viện chuẩn): tải vào thư mục tạm mới, kiểm hash **trước** khi giải nén, chỉ giữ file liệt kê, chạy lại khi đã đủ thì không tải; hash sai → lỗi, `deps/` không đổi
- [ ] `Flevert.Pdf.csproj` chép `libpdfium.so` và qpdf (`bin/qpdf`, `lib/libqpdf.so*`) trên Linux; `Qpdf.FileName`, `PdfWorker.FileName` theo nền tảng; `ldd` không thiếu thư viện
- Kiểm: chạy hai lần (lần 2 không tải); sửa một ký tự hash → thất bại rõ (đối chứng âm); test Python cho script
- Phụ thuộc: T1. Cỡ: M

### T3: Test chạy được trên Linux, lập bảng
- [ ] Test project tham chiếu `Flevert.Linux` trên Linux; file chỉ dành cho Windows bỏ khỏi build trên Linux (có comment lý do)
- [ ] Chạy ba project, ghi bảng: xanh / đỏ vì phần Linux chưa làm (task nào sửa) / bỏ qua vì chỉ có nghĩa trên Windows
- Kiểm: ba project chạy hết (đỏ được phép, nhưng mỗi test đỏ có task nhận); không test nào bị xóa
- Phụ thuộc: T1, T2. Cỡ: M

### Mốc: nền
- [ ] Build 0 cảnh báo; bảng test có trong todo

## Giai đoạn 2: phần nền tảng

### T4: Ghim file và quy tắc đường dẫn (Runner)
- [ ] `UnixInputPin.TryPin`: `O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`, `fstat` chỉ file thường, `st_nlink == 1`, đường dẫn thật từ `/proc/self/fd/N`
- [ ] Quy tắc đường dẫn Linux trong `Runner`/`InputPath`: tuyệt đối, bắt đầu bằng `/`, không có `\0`; tên có dấu chấm cuối, khoảng trắng cuối hay `:` là hợp lệ
- Kiểm: test âm cho symlink, hard link, FIFO (không treo), `/dev/zero`, thư mục, đường dẫn tương đối; thư mục cha là symlink → đường dẫn thật
- Phụ thuộc: T3. Cỡ: S

### T8: ExternalTool trên Linux (làm sớm: rủi ro cao)
- [ ] Kiểm đường dẫn công cụ theo nền tảng (tuyệt đối, tồn tại, có quyền chạy); giới hạn bộ nhớ qua `prlimit --as`; hủy/hết giờ giết cả cây
- [ ] `MemoryLimitExceeded` suy ra được trên Linux (qpdf `std::bad_alloc`, tiến trình bị `SIGKILL`/`SIGABRT` khi có giới hạn); `SingleProcess` không áp dụng (ghi vào ADR)
- Kiểm: test dùng `/bin/sh -c`, `cat`, `sleep`, `head -c`: echo stdin, output 5 MB bị cắt, timeout, hủy giết cây con, vượt giới hạn bộ nhớ được báo; đo `prlimit --as` với worker .NET
- Phụ thuộc: T3. Cỡ: M

### T5: Thứ tự tự nhiên như Nautilus
- [ ] `NaturalOrder.Compare` trên Linux: dãy số so theo giá trị, không phân biệt hoa/thường, giống `g_utf8_collate_key_for_filename`
- Kiểm: test đối chiếu với thứ tự do GLib tính trên máy (`python3 -c` với `GLib.utf8_collate_key_for_filename` khi có, bỏ qua khi không)
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

(điền ở T3)

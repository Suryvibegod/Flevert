# Spec: linux (bản Linux, menu chuột phải của Nautilus)

Module `linux` trong [capability-map.md](capability-map.md). Phụ thuộc `core`, `image`, `pdf`. Trạng thái: **đã duyệt** (2026-10-21; người dùng chốt các đề xuất ở Open Questions). Người đóng góp: tài khoản `long89kev`, làm trên nhánh `feat/linux`; pull request mở sau, khi bản Linux chạy được trọn vẹn. Capability map trước đây ghi Linux là "ngoài phạm vi": spec này là đề xuất của người đóng góp, maintainer quyết định có nhận hay không khi xem PR.

## Objective

Người dùng Linux (GNOME, trình quản lý file Nautilus) có cùng vấn đề như người dùng Windows: ảnh HEIC từ iPhone không mở được, ảnh quá nặng để gửi, PDF cần gộp, tách, nén hoặc đặt mật khẩu. Hôm nay họ phải gõ lệnh (`convert`, `qpdf`) hoặc upload lên web. Flevert cho Linux làm **đúng những việc bản Windows 1.2.3 làm, bằng chuột phải trong Nautilus, trên máy, không upload**: cùng thao tác, cùng nhãn menu, cùng tên file kết quả, cùng thông báo.

### User stories (ưu tiên từ trên xuống)

1. Chọn 20 ảnh HEIC trong Nautilus → chuột phải → **Flevert ▸ Convert to ▸ JPG** → 20 file JPG cạnh file gốc, đúng chiều, một thông báo "Done: 20 files".
2. Chọn 5 PDF → **Flevert ▸ Merge PDFs** → một `report (merged).pdf`, theo thứ tự tên như Nautilus hiển thị.
3. PDF → **Protect with password** → hộp thoại hỏi mật khẩu một lần cho cả lô → `report (protected).pdf`.
4. Ảnh có GPS → **Remove hidden info** → bản sao không còn vị trí.
5. Lô dài → cửa sổ tiến độ có nút **Cancel**; bấm Cancel thì dừng, không để lại file dở.
6. Cài không cần `sudo` (trừ gói `python3-nautilus` của hệ điều hành, nếu máy chưa có), gỡ sạch bằng một lệnh.

### Phạm vi

Trong phạm vi: mọi thao tác ảnh và PDF của 1.2.3 (menu thường và các công cụ của "Flevert: more PDF tools"); cửa sổ tiến độ, thông báo, hộp thoại mật khẩu và watermark; cài/gỡ theo từng user; chạy cả ba project test trên Linux.

Ngoài phạm vi (lần này): Dolphin, Nemo, Thunar, Caja; video/âm thanh (module `media` chưa vào `main`); báo bản mới (xem Open Questions); gói `.deb`, `.rpm`, Flatpak, Snap (Flatpak/Snap chạy trong sandbox, không gắn extension vào Nautilus được); ARM64; macOS.

## Khác biệt nền tảng và cách làm

Explorer chạy **một `flevert.exe` cho mỗi file** đã chọn, nên Core có cả cơ chế pipe để gom các tiến trình thành một lô (ADR-0002, ADR-0007). Nautilus thì gọi extension **một lần với mọi file đã chọn**, nên trên Linux một tiến trình `flevert` nhận cả lô: dùng `BatchLeader.Alone(files)` có sẵn, không cần pipe, không cần census tiến trình. Đây là phần đơn giản hóa lớn nhất.

| Việc | Windows (giữ nguyên) | Linux |
|---|---|---|
| Menu chuột phải | Registry, `ExtendedSubCommandsKey`, giới hạn 16 mục, mục Shift | Extension Python của Nautilus (`nautilus-python`, API 3.0 cho Nautilus 42 và 4.0 cho Nautilus 43+), đọc `menu.json` do `flevert install-menu` ghi. Không có giới hạn 16 mục: "More PDF tools" là một menu con trong `Flevert`, không cần Shift. Không icon (Nautilus 43+ không vẽ icon trong menu) |
| Gom lô | Pipe có tên + khóa bí mật + kiểm tra tiến trình | Một tiến trình nhận cả lô (`BatchLeader.Alone`) |
| Ghim file đầu vào | `CreateFileW` không theo reparse point, không chia sẻ quyền xóa, đường dẫn thật từ handle | `open(O_RDONLY \| O_NOFOLLOW \| O_NONBLOCK \| O_CLOEXEC)`, `fstat`: chỉ file thường, `st_nlink == 1`; đường dẫn thật đọc từ `/proc/self/fd/N`. Linux không có khóa bắt buộc nên không chặn được việc đổi tên file trong lúc chạy: ghi rõ trong ADR |
| Quy tắc đường dẫn | Ổ đĩa/UNC, tên 8.3, tên kết thúc bằng dấu chấm/khoảng trắng, ADS | Chỉ đường dẫn tuyệt đối bắt đầu bằng `/`, không có `\0`. Tên kết thúc bằng dấu chấm và dấu `:` là tên hợp lệ trên Linux |
| Thư mục kết quả 1→N (PartialFolder) | Handle giữ thư mục, file neo, đổi tên qua handle | `mkdir` quyền 0700 tên ẩn, mở bằng `O_DIRECTORY \| O_NOFOLLOW`, đổi tên bằng `renameat2(RENAME_NOREPLACE)`, dọn dẹp bằng `unlinkat` tương đối với fd thư mục (không theo symlink) |
| Thư mục dữ liệu app | `%LOCALAPPDATA%\Flevert`, ACL chỉ chủ + Admin + nhãn integrity | `~/.local/share/Flevert` (cùng `SpecialFolder.LocalApplicationData`), quyền 0700 |
| Sắp xếp tên | `StrCmpLogicalW` (như Explorer) | Gọi chính `g_utf8_collate_key_for_filename` của GLib (hàm Nautilus dùng để sắp tên) với `LC_COLLATE` của người dùng; GLib có sẵn trên mọi desktop chạy Nautilus, liên kết từ hệ thống, không đóng gói. Không có GLib: so sánh tự nhiên viết trong Flevert (dãy số theo giá trị, không phân biệt hoa/thường) |
| Công cụ ngoài (qpdf, worker PDF) | Job object: giới hạn bộ nhớ, một tiến trình, chết theo flevert | `prlimit --as=<bytes>` (util-linux) cho giới hạn bộ nhớ; hủy hoặc hết giờ thì `Kill(entireProcessTree)`. Không có giới hạn "một tiến trình" và không tự chết khi flevert chết: ghi rõ trong ADR. `MemoryLimitExceeded` suy ra từ mã thoát và stderr của qpdf ("std::bad_alloc") |
| Cửa sổ tiến độ, hộp thoại | WinForms | `zenity` (`--progress`, `--password`, `--entry --hide-text`), chạy thành tiến trình riêng; mật khẩu đi qua stdout của zenity vào pipe, không qua dòng lệnh |
| Thông báo | Shell notification | `org.freedesktop.Notifications` qua `gdbus call` (có sẵn trong GLib) |
| pdfium, qpdf | `pdfium.dll`, `qpdf.exe` (ghim SHA256 trong `scripts/deps.json`) | `libpdfium.so` từ `bblanchon/pdfium-binaries` (`pdfium-linux-x64.tgz`, chromium/8100, sha256 `1c1894a1…320a`), `qpdf` từ `qpdf-12.4.2-bin-linux-x86_64.zip` (sha256 `db367d89…f8b1`), ghim trong `scripts/deps-linux.json` |
| Cài đặt | Inno Setup, per-user | Tarball `Flevert-<version>-linux-x64.tar.gz` + `install.sh`: chép vào `~/.local/share/flevert/app`, gọi `flevert install-menu`, báo nếu thiếu `python3-nautilus` và cách cài; `flevert uninstall` gỡ sạch |

### Tham khảo trước khi viết (CLAUDE.md, "Reuse before writing")

- **Menu Nautilus:** `nautilus-python` (GPL-2.0, chạy trong tiến trình Nautilus; extension của Flevert là file Python riêng, MIT, chỉ gọi `flevert` bằng `subprocess` với danh sách đối số, không shell). Phương án khác: Nautilus Scripts (`~/.local/share/nautilus/scripts`): không cần gói thêm nhưng hiện cho mọi loại file và nằm dưới mục "Scripts", nên không chọn. Phương án `.desktop` "Open With": sai chỗ trong menu.
- **Giao diện:** zenity (LGPL-2.1, tiến trình riêng, có sẵn trên Ubuntu/Pop/Fedora Workstation) thay vì Avalonia (MIT, nhưng thêm khoảng 30 MB và khởi động chậm hơn). GTK# không còn được bảo trì.
- **Thông báo:** `gdbus` (GLib, LGPL, tiến trình riêng) thay vì `notify-send` (gói `libnotify-bin` không phải lúc nào cũng có, máy thử không có).
- **Sắp xếp tự nhiên:** NuGet `NaturalSort.Extension` (MIT) đã cân nhắc. Đo thật (2026-10-21) cho thấy thứ tự của GLib đi theo luật collation của locale (ở `en_US`, `img_0011` đứng trước `IMG_0009`; `ảnh 10` đứng trước `Ảnh 1`), không bản so sánh tự nhiên nào khớp được, nên gọi thẳng GLib (sửa lại quyết định ban đầu "viết trong Flevert"); test đối chiếu với GLib qua Python trong cùng locale.
- **Giới hạn tiến trình:** `prlimit`/`setpriv` (util-linux, tiến trình riêng). `setpriv --pdeathsig` bị loại: tín hiệu gắn với *luồng* đã fork, mà .NET fork từ luồng của thread pool.
- **Magick.NET-Q8-x64** đã có sẵn bản native linux-x64 (kể cả HEIC), không cần đổi gói.

## Commands

```
export PATH=$HOME/.dotnet:$PATH                      # .NET 10 SDK cài cho user (dotnet-install.sh)
python3 scripts/fetch-deps-linux.py                  # libpdfium.so, qpdf vào deps/ (ghim SHA256)
dotnet build Flevert.sln
dotnet test tests/Flevert.Core.Tests
dotnet test tests/Flevert.Image.Tests
dotnet test tests/Flevert.Pdf.Tests
dotnet format Flevert.sln --verify-no-changes        # kiểm bằng exit code
dotnet build src/Flevert.Cli -p:EnableWindowsTargeting=true -p:TargetFramework=net10.0-windows   # bản Windows vẫn biên dịch được
scripts/build-linux.sh                               # publish + tarball vào artifacts/
python3 -m unittest discover -s src/Flevert.Linux/nautilus -p 'test_*.py'   # extension, với module Nautilus giả
```

## Project Structure

```
Directory.Build.props                  → TargetFramework: net10.0-windows trên Windows, net10.0 nơi khác
src/Flevert.Core/Unix/                 → phần Linux của Core: UnixInputPin, UnixPartialFolder, UnixFile (P/Invoke libc), UnixNaturalOrder
src/Flevert.Core/*.cs                  → chỉ thêm điểm rẽ nhánh OperatingSystem.IsWindows(); code Windows giữ nguyên
src/Flevert.Pdf/Unix/                  → FileIdentity (st_dev, st_ino), PrivateFolder (mkdir 0700)
src/Flevert.Linux/                     → tiến trình `flevert` cho Linux: Program, ZenityProgress, Dialogs, Notifier, NautilusMenu (install-menu/uninstall)
src/Flevert.Linux/nautilus/flevert_menu.py   → extension Nautilus (MIT) + test_flevert_menu.py
scripts/deps-linux.json, scripts/fetch-deps-linux.py, scripts/build-linux.sh, packaging/linux/install.sh
tests/Flevert.*.Tests/                 → chạy được trên Linux; test chỉ dành cho Windows bỏ qua có lý do
docs/adr/0010-linux-nautilus-port.md   → các quyết định ở bảng trên (0009 đã dùng ở nhánh media)
```

## Code Style

Như phần còn lại của repo: comment giải thích *vì sao*, tên tiếng Anh, `LibraryImport` cho native, `TreatWarningsAsErrors`, analyzer CA1416 bật. Điểm rẽ nhánh nền tảng nhỏ, đặt ở chỗ gọi, để code Windows không đổi:

```csharp
private static string? Pin(string file, out SafeFileHandle? pin, out string realPath)
{
    // Linux has no mandatory locks: the pin keeps the file open and checks it, but cannot stop a rename (ADR-0010).
    pin = OperatingSystem.IsWindows()
        ? InputPin.TryPin(file, out var reason, out realPath)
        : UnixInputPin.TryPin(file, out reason, out realPath);
    return reason;
}
```

Kiểu chỉ dành cho Windows được đánh dấu `[SupportedOSPlatform("windows")]` để analyzer bắt lỗi khi bị gọi từ code chạy trên Linux; không tắt CA1416.

## Testing Strategy

- xUnit như hiện tại; test mới cho phần Linux nằm trong ba project có sẵn (thư mục `Unix/`), extension Python test bằng `unittest` với module `gi.repository.Nautilus` giả (cả API 3.0 và 4.0).
- TDD: mỗi phần Linux (ghim file, quy tắc đường dẫn, thư mục 1→N, sắp xếp, công cụ ngoài, menu.json) có test đỏ trước. Test bảo mật kiểm cả trường hợp âm: symlink, hard link, FIFO, `/dev/zero`, thư mục bị thay bằng symlink giữa chừng, tên đích đã có.
- Test chỉ có nghĩa trên Windows (registry, Inno Setup, PowerShell, junction, 8.3, ACL) **không xóa, không sửa nội dung**: file chỉ dành cho Windows bỏ khỏi build trên Linux trong `.csproj` (có comment), test lẻ dùng `Skip.IfNot(OperatingSystem.IsWindows(), "…")`. Số test bị bỏ qua được ghi trong `tasks/linux/todo.md`.
- Test tích hợp chạy `flevert` thật (đang chạy `flevert.exe`) dùng chung một helper tìm file thực thi theo nền tảng.
- Kiểm thật một lần trước khi mở PR: cài từ tarball trên Pop!_OS 22.04 (Nautilus 42), bấm từng thao tác trong Nautilus, rồi ba review bối cảnh trống (code, security, doubt) theo quy trình của repo.

## Boundaries

- **Always:** giữ nguyên hành vi Windows (chỉ thêm điểm rẽ nhánh); build, format và ba project test xanh trên Linux trước mỗi commit; bản Windows vẫn biên dịch được (`EnableWindowsTargeting`); UI, README, commit tiếng Anh; spec/plan tiếng Việt; binary chỉ tải qua script, ghim SHA256.
- **Ask first:** commit và push (người đóng góp không phải maintainer: chỉ commit khi người dùng bảo); mở issue hoặc PR lên repo gốc; sửa `.github/workflows` (thêm job Ubuntu); thêm gói NuGet; chạy `sudo` (cài `python3-nautilus`).
- **Never:** đổi menu, installer hay hành vi của bản Windows; chép code GPL/LGPL vào Flevert; commit binary; xóa hoặc nới lỏng test có sẵn; đưa mật khẩu lên dòng lệnh hay vào log.

## Success Criteria

1. Trên Pop!_OS 22.04 (Nautilus 42): `install.sh` cài không cần `sudo` (khi `python3-nautilus` đã có; nếu chưa, script in đúng lệnh cài rồi dừng), sau `nautilus -q` chuột phải một ảnh có `Flevert ▸` với đúng các mục của bản Windows; chuột phải PDF có các mục của menu PDF thường và menu con `More PDF tools`; file loại khác không có `Flevert`.
2. Mọi thao tác ảnh và PDF của 1.2.3 chạy qua `flevert` đã cài trên file thật, cho file kết quả cùng tên và cùng nội dung như bản Windows (danh sách thao tác và kết quả ghi trong `tasks/linux/todo.md`).
3. Chọn 20 ảnh HEIC → một tiến trình, một cửa sổ tiến độ, một thông báo "Done: 20 files".
4. Merge PDFs và Combine into PDF: một file kết quả, theo thứ tự tên của Nautilus.
5. Cancel trong cửa sổ tiến độ dừng lô, không để lại file hay thư mục dở.
6. Mật khẩu chỉ được hỏi một lần cho cả lô và không xuất hiện trong `/proc/*/cmdline` của bất kỳ tiến trình nào, cũng không có trong log.
7. Từ chối symlink, file có nhiều hard link, FIFO và thiết bị; không bao giờ ghi đè file hay thư mục có sẵn (kể cả khi tên bị chiếm giữa chừng); `~/.local/share/Flevert` quyền 0700. Mỗi điều có test.
8. `dotnet test` xanh cho cả ba project trên Linux; test chỉ dành cho Windows được bỏ qua có lý do, không test nào bị xóa; `dotnet format --verify-no-changes` exit 0; bản Windows vẫn biên dịch được.
9. `flevert uninstall` (hoặc `install.sh --uninstall`) gỡ extension, `menu.json` và thư mục cài; menu biến mất sau `nautilus -q`; chỉ còn lại file kết quả của người dùng.
10. Khởi động lạnh `flevert run` ≤ 300 ms trên máy thử (cùng mục tiêu với bản Windows).

## Open Questions (đã chốt 2026-10-21: theo đề xuất)

1. **Maintainer có nhận bản Linux không?** Capability map ghi Linux là ngoài phạm vi. Đề xuất: làm xong trên nhánh, rồi mở PR kèm spec và ADR này; nếu không được nhận thì giữ ở fork.
2. **Giấy phép extension:** extension Python (MIT) chạy trong Nautilus (GPL) qua `nautilus-python` (GPL-2.0). Nhiều extension MIT làm như vậy, và extension chỉ gọi `flevert` thành tiến trình riêng. Đề xuất: MIT, ghi lý do trong ADR-0010.
3. **Báo bản mới trên Linux:** để sau (cần asset Linux trên GitHub Releases). Đề xuất: tắt hẳn ở bản Linux đầu tiên.
4. **Nautilus 43+ (GNOME 43 trở lên):** máy thử chỉ có Nautilus 42; API 4.0 chỉ được kiểm bằng test với module giả. Cần một người có Ubuntu 24.04 hoặc Fedora thử tay trước khi phát hành.
5. **Giới hạn số file:** Windows giới hạn bởi Explorer (100). Đề xuất: Linux nhận tới `Batch.MaxFilesPerBatch` (1000) mỗi lần chọn; quá số đó thì thông báo, không chạy.

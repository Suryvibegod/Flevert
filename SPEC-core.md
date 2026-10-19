# Spec: core

Module `core` trong [CAPABILITY-MAP.md](CAPABILITY-MAP.md). Không phụ thuộc module nào. Module `image`, `media`, `pdf`, `installer` đều xây trên nó.

## Objective

Cho phép người dùng Windows chuột phải vào file, chọn một thao tác trong menu `ConvertAnything`, và nhận file kết quả nằm cạnh file gốc trong vài giây, không bật cửa sổ nào.

`core` không tự convert gì. Nó lo phần khung dùng chung:

- Ghi / gỡ mục menu chuột phải (kiểu cũ) trong registry, chỉ hiện thao tác phù hợp với loại file.
- Nhận lệnh từ Explorer, gom nhiều file được chọn cùng lúc thành **một lô** xử lý bởi **một tiến trình**.
- Đặt tên file output không bao giờ đè file có sẵn, không bao giờ sửa file gốc.
- Báo kết quả bằng một thông báo góc màn hình cho cả lô.
- Lô chạy lâu hơn 2 giây thì hiện cửa sổ tiến độ nhỏ (file thứ mấy / tổng, nút Cancel). Nén video mất vài phút; chạy im lặng thì người dùng tưởng treo. Cancel dừng **ngay** file đang chạy (bỏ file dở) và không xử lý các file còn lại.
- Ghi log lỗi để người dùng gửi lại khi báo bug.
- Định nghĩa hợp đồng `IOperation` để các module khác cắm thao tác vào.

### User stories

- Là người dùng, tôi chuột phải 1 file → `ConvertAnything` → thao tác, và thấy file mới cạnh file gốc.
- Là người dùng, tôi chọn 30 file, chuột phải → thao tác, và nhận **một** thông báo "Xong 30 file" chứ không phải 30 cửa sổ / 30 thông báo.
- Là người dùng, khi 2 trong 30 file lỗi, 28 file kia vẫn xong và thông báo nói rõ có 2 file lỗi, kèm chỗ xem log.
- Là người dùng, tôi gỡ tool và menu chuột phải biến mất hoàn toàn.

## Tech Stack

| Thành phần | Lựa chọn | Lý do |
|---|---|---|
| Ngôn ngữ / runtime | C# trên .NET 10 (LTS, hỗ trợ tới 11/2028) | Gọi Win32 / registry dễ; một ngôn ngữ cho mọi module |
| Biên dịch | Self-contained, `win-x64`, ReadyToRun | Không cần cài runtime. **Không dùng NativeAOT**: cần Visual Studio C++ Build Tools (vài GB) và WinForms không hỗ trợ AOT |
| Kiểu exe | `WinExe` | Explorer gọi exe không nháy cửa sổ console |
| Test | xUnit | Chuẩn phổ biến của .NET |
| Thông báo + tiến độ | WinForms (`NotifyIcon.ShowBalloonTip`, một `Form` nhỏ) | Có sẵn trong .NET; balloon hiện như toast trên Win10/11, không cần AUMID / MSIX. Chỉ nạp WinForms khi cần hiện UI |
| Gom lô | Named pipe một instance (không mutex) | Có sẵn trong .NET; xem [ADR-0002](docs/adr/0002-batching-over-a-single-instance-pipe.md) |

Không thêm package NuGet nào ngoài xUnit cho module này. `ConvertAnything.Core` không tham chiếu WinForms; UI nằm trong `ConvertAnything.Cli`.

## Commands

```
Build:   dotnet build ConvertAnything.sln -c Debug
Test:    dotnet test ConvertAnything.sln
Lint:    dotnet format ConvertAnything.sln --verify-no-changes
Publish: dotnet publish src/ConvertAnything.Cli -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o artifacts/publish
Startup: powershell -File scripts/measure-startup.ps1
```

Lệnh của `ca.exe`:

```
ca.exe run <op-id> <file>...        Chạy thao tác (Explorer gọi lệnh này)
ca.exe install-menu [--dev] [--allow-insecure-location]
                                    Ghi menu chuột phải vào HKCU (--dev: thêm thao tác thử dev.copy;
                                    --allow-insecure-location: cho phép thư mục người khác sửa được, đi qua junction,
                                    chứa link, nằm trên ổ subst/ổ mạng/UNC; mặc định thư mục cài phải ở ổ cố định
                                    cục bộ và đường dẫn thật trùng đường dẫn đưa vào)
ca.exe uninstall-menu               Gỡ mọi key đã ghi
ca.exe list-ops                     In danh sách thao tác và loại file áp dụng

Biến môi trường: CONVERTANYTHING_HEADLESS=1 (chạy lô không UI), CONVERTANYTHING_LOG_DIR (đổi thư mục log)
```

## Project Structure

```
ConvertAnything.sln
src/
  ConvertAnything.Core/        → Hợp đồng IOperation, gom lô, đặt tên output, mô hình menu, log
  ConvertAnything.Cli/         → ca.exe: đọc lệnh, ghi registry, hiện thông báo
tests/
  ConvertAnything.Core.Tests/  → Unit test + integration test (registry trong key test riêng)
docs/                          → Tài liệu người dùng, ADR
CAPABILITY-MAP.md
SPEC-<module-id>.md
tasks/                         → plan.md, todo.md
```

Module sau thêm project riêng: `src/ConvertAnything.Image/`, `src/ConvertAnything.Media/`, `src/ConvertAnything.Pdf/`.

## Thiết kế

### Hợp đồng thao tác

```csharp
public interface IOperation
{
    string Id { get; }                         // "image.to-png" — ổn định, dùng trong registry
    string MenuPath { get; }                   // "Convert/PNG" — dấu / tạo menu con
    IReadOnlyCollection<string> Extensions { get; }  // ".jpg", ".heic", ...
    Task<string> RunAsync(string inputPath, CancellationToken ct);  // trả về đường dẫn output
}
```

### Luồng một lần bấm

```
Explorer ──"ca.exe run image.to-png <file1>"──▶ A tạo được pipe → A là chủ lô, xử lý file1 ngay
Explorer ──"ca.exe run image.to-png <file2>"──▶ B: pipe đã có → gửi path → nhận ack → thoát
                                                   A: nhận file2 vào hàng đợi trong lúc đang xử lý
A: hết hàng đợi + đang rảnh + 400 ms không có path mới → đóng pipe → 1 thông báo → thoát
```

Tên pipe: `ConvertAnything-<SID>-<session>-<khóa>-<op-id>`. Khóa ngẫu nhiên riêng cho mỗi thao tác trong `%LOCALAPPDATA%\ConvertAnything\pipes`, thư mục chỉ user / SYSTEM / Administrators mở được, nhãn medium no-read-up; khóa đổi mỗi khi một lô đóng. Hai đầu kiểm tra tiến trình bên kia chạy cùng file exe, cùng session, integrity không thấp hơn. Lô sống tối thiểu 1,5 s; lô đầy (1000 file) trả `Full`: người gửi đổi khóa và tự làm chủ một lô mới, các tiến trình đến sau vào lô đó. Chi tiết, các bảo đảm và trade-off: [ADR-0002](docs/adr/0002-batching-over-a-single-instance-pipe.md).

### Menu trong registry

Đã kiểm chứng với [Creating Shortcut Menu Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/context-menu-handlers) (Microsoft Learn):

- `SubCommands` với verb tự định nghĩa cần `CommandStore` trong **HKLM** (phải admin), nên **không dùng**.
- Dùng `ExtendedSubCommandsKey`: chạy được trong HKCU, hỗ trợ menu lồng nhiều cấp, và nhiều loại file trỏ chung về một bộ menu.
- Verb dạng dòng lệnh: `MultiSelectModel=Document` (mặc định) chỉ hiện khi chọn ≤ 15 file; `Player` thì ≤ 100 file. Explorer vẫn gọi **mỗi file một tiến trình**. Đặt `Player` trên mọi verb.

Cấu trúc:

```
HKCU\Software\Classes\
  ConvertAnything.Menu.<.ext>\shell\              ← cây menu riêng cho mỗi đuôi file
    000\  MUIVerb=Convert, ExtendedSubCommandsKey=ConvertAnything.Menu.<.ext>\Convert
    ...
  ConvertAnything.Menu.<.ext>\Convert\shell\000\  MUIVerb=PNG, MultiSelectModel=Player
      command\ (Default) = "<installdir>\ca.exe" run image.to-png "%1"
  SystemFileAssociations\.jpg\shell\ConvertAnything\
    MUIVerb=ConvertAnything, Icon=<installdir>\ca.exe, ExtendedSubCommandsKey=ConvertAnything.Menu.<.ext>
```

Chỉ ghi HKCU nên không cần admin. Key tool tạo đều có tên bắt đầu bằng `ConvertAnything`, gỡ là tìm theo tên đó.

**Giới hạn đã biết:** chọn > 100 file thì menu không hiện. Bỏ giới hạn cần verb COM (`DropTarget`), để phiên bản sau.

### Ngôn ngữ

Menu, thông báo, log, README: tiếng Anh. Tài liệu nội bộ (spec, plan) giữ tiếng Việt.

### Đầu vào bị từ chối

Mỗi file được mở giữ (pin) không cho xóa/đổi tên trong lúc xử lý; thao tác nhận đường dẫn thật lấy từ handle. Bị từ chối, ghi lỗi vào log, các file khác chạy tiếp:

- Đường dẫn thiết bị (`\\.\`, `\\?\GLOBALROOT`, `\??\` ...), share `pipe` / `IPC$`, tên server bắt đầu bằng `;` (`\\;LanmanRedirector\…`). Sau khi pin còn kiểm handle là file trên đĩa (`FILE_TYPE_DISK`), nên không cách viết nào biến pipe/thiết bị thành đầu vào. Pin là lần mở duy nhất (không thăm dò `Exists` trước), có SQOS.
- Bất kỳ tên file **hoặc thư mục** nào trong đường dẫn kết thúc bằng dấu chấm hoặc khoảng trắng (trừ `.`/`..`), kiểm cả trên đường dẫn thật sau khi pin: Win32 bỏ các ký tự đó, nên `sub.\x.txt` sẽ bị đọc thành `sub\x.txt`.
- Alternate data stream (`a.txt:s.png`).
- Symlink / junction (reparse point kiểu name surrogate). File OneDrive (placeholder) vẫn chạy.
- File có hơn một hard link. **Trade-off chấp nhận:** chặn cả vài file hợp lệ (ảnh nền trong `C:\Windows\Web` liên kết sang WinSxS, kho pnpm, công cụ backup dùng hard link); đổi lại hard link không thể làm lộ bản sao của file người khác vào thư mục chung.
- Đuôi file không thuộc thao tác, kiểm cả trên tên thật sau khi pin.

### Đặt tên output

- Cùng thư mục với file gốc. Tên do thao tác đề xuất (`anh.png`, `anh (nén).jpg`).
- Trùng tên thì thêm ` (1)`, ` (2)`...
- Ghi ra file tạm `.ca-<guid>.partial` (độ dài cố định) rồi đổi tên khi xong: hỏng giữa chừng không để lại file dở. Tên output tự cắt bớt để vừa 255 ký tự; thư mục trùng tên cũng tính là đã có.

### Log

`%LOCALAPPDATA%\ConvertAnything\logs\ca-YYYYMMDD.log`, mỗi dòng: thời gian, op-id, file, kết quả, thời gian xử lý, lỗi (kèm stack). Giữ 7 ngày. Không ghi nội dung file.

## Code Style

```csharp
public static class OutputPath
{
    public static string Reserve(string desiredPath)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        var candidate = desiredPath;
        for (var n = 1; File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");

        return candidate;
    }
}
```

- `PascalCase` cho type/method, `camelCase` cho biến, file-scoped namespace, `nullable` bật, warning = error.
- Gọi tiến trình ngoài (ffmpeg...) luôn dùng `ProcessStartInfo.ArgumentList`, không bao giờ ghép chuỗi lệnh.
- Không comment giải thích *code làm gì*; chỉ comment *vì sao* khi không hiển nhiên.

## Testing Strategy

- **Unit** (`ConvertAnything.Core.Tests`): đặt tên output, mô hình menu từ danh sách thao tác, gom lô trong cùng tiến trình (50 lời gọi song song, file đến muộn khi đang xử lý, surrogate lẻ), parse lệnh.
- **Tiến trình thật** (`ProcessBatchIntegrationTests`): chạy `ca.exe` với `CONVERTANYTHING_HEADLESS=1` và `CONVERTANYTHING_LOG_DIR` trỏ thư mục tạm.
- **Integration**: ghi/gỡ registry vào key test riêng `HKCU\Software\Classes\ConvertAnything.Test`, kiểm tra gỡ xong không còn key nào; gom lô thật bằng nhiều tiến trình `ca.exe`.
- **Runtime thủ công** (bắt buộc trước khi đóng module): bấm thật trong Explorer bằng thao tác `dev.copy`.
- Viết test trước, chạy đỏ, rồi mới code cho xanh (theo `test-driven-development`).
- Đường dẫn test luôn gồm tiếng Việt có dấu và dấu cách, ví dụ `Ảnh Đà Lạt\ảnh 1.jpg`.

## Boundaries

- **Always:** chạy test trước khi commit; chỉ ghi registry trong HKCU; không bao giờ sửa hoặc xóa file gốc; dùng `ArgumentList` khi gọi tiến trình ngoài.
- **Ask first:** thêm package NuGet; ghi HKLM; đổi module id hoặc op-id đã phát hành; cài phần mềm lên máy; push lên GitHub.
- **Never:** commit file nhị phân (ffmpeg, pdfium) vào git; gửi file hay dữ liệu ra mạng; thêm telemetry.

## Success Criteria

1. `ca.exe install-menu --dev --allow-insecure-location` (chạy từ thư mục build) xong, chuột phải file `.txt` trong menu cũ thấy `ConvertAnything > Dev > Copy`.
2. Bấm vào 1 file: xuất hiện `tên (copy).txt` cạnh file gốc, có thông báo, **không** nháy cửa sổ console.
3. Chọn 20 file rồi bấm: đúng **một** tiến trình xử lý, **một** thông báo, đủ 20 file output. Chọn 100 file menu vẫn hiện.
4. File output đã tồn tại thì tên mới có ` (1)`; file gốc không đổi (so sánh hash trước/sau).
5. 1 file lỗi không chặn các file còn lại; thông báo ghi số file lỗi; log có stack trace.
6. `ca.exe uninstall-menu` xong không còn key nào tên `ConvertAnything` trong HKCU (test liệt kê kiểm tra).
7. Khởi động lạnh `ca.exe run` (bản publish, chưa nạp WinForms) < 300 ms, đo bằng `Measure-Command`, trung vị 10 lần.
8. Đường dẫn tiếng Việt có dấu, dấu cách, dài > 260 ký tự đều chạy.
9. `dotnet test` và `dotnet format --verify-no-changes` đều qua.
10. Lô chạy > 2 giây hiện cửa sổ tiến độ; bấm Cancel thì dừng ngay, không để lại file `.partial`.

## Quyết định đã chốt

1. Cài .NET 10 SDK cho riêng user bằng `dotnet-install.ps1` (không cần admin, không kẹt UAC); `scripts/setup-dev.ps1` làm việc này.
2. Menu, thông báo, README: tiếng Anh.
3. Cài `gh` CLI, tạo repo GitHub private `ConvertAnything`.
4. License MIT. Thư viện phụ thuộc chỉ MIT/BSD/Apache/LGPL (LGPL thì liên kết động) để public được.
5. Registry: `ExtendedSubCommandsKey` + `MultiSelectModel=Player`, đã kiểm chứng với tài liệu Microsoft (xem mục Menu trong registry).

## Open Questions

Không còn.

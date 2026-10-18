# Spec: core

Module `core` trong [CAPABILITY-MAP.md](CAPABILITY-MAP.md). Không phụ thuộc module nào. Module `image`, `media`, `pdf`, `installer` đều xây trên nó.

## Objective

Cho phép người dùng Windows chuột phải vào file, chọn một thao tác trong menu `ConvertAnything`, và nhận file kết quả nằm cạnh file gốc trong vài giây, không bật cửa sổ nào.

`core` không tự convert gì. Nó lo phần khung dùng chung:

- Ghi / gỡ mục menu chuột phải (kiểu cũ) trong registry, chỉ hiện thao tác phù hợp với loại file.
- Nhận lệnh từ Explorer, gom nhiều file được chọn cùng lúc thành **một lô** xử lý bởi **một tiến trình**.
- Đặt tên file output không bao giờ đè file có sẵn, không bao giờ sửa file gốc.
- Báo kết quả bằng một thông báo góc màn hình cho cả lô.
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
| Biên dịch | NativeAOT, `win-x64` | Khởi động nhanh (mục tiêu < 150 ms), file exe đơn, không cần cài runtime |
| Kiểu exe | `WinExe` | Explorer gọi exe không nháy cửa sổ console |
| Test | xUnit | Chuẩn phổ biến của .NET |
| Thông báo | Balloon của `Shell_NotifyIcon` (P/Invoke) | Hiện như toast trên Win10/11, không cần đăng ký AUMID / MSIX, chạy được với NativeAOT |
| Gom lô | Named mutex + named pipe | Có sẵn trong .NET, không thêm thư viện |

Không thêm package NuGet nào ngoài xUnit cho module này.

## Commands

```
Build:   dotnet build ConvertAnything.sln -c Debug
Test:    dotnet test ConvertAnything.sln
Lint:    dotnet format ConvertAnything.sln --verify-no-changes
Publish: dotnet publish src/ConvertAnything.Cli -c Release -r win-x64 -p:PublishAot=true -o artifacts/publish
```

Lệnh của `ca.exe`:

```
ca.exe run <op-id> <file>...        Chạy thao tác (Explorer gọi lệnh này)
ca.exe install-menu [--dev]         Ghi menu chuột phải vào HKCU (--dev: thêm thao tác thử dev.copy)
ca.exe uninstall-menu               Gỡ mọi key đã ghi
ca.exe list-ops                     In danh sách thao tác và loại file áp dụng
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
Explorer ──"ca.exe run image.to-png <file>"──▶ tiến trình A (giữ mutex, mở pipe)
Explorer ──"ca.exe run image.to-png <file>"──▶ tiến trình B ──gửi path qua pipe──▶ A, rồi thoát
                                                        ...
A: chờ 400 ms không có path mới → xử lý cả lô → 1 thông báo → thoát
```

Mutex và pipe đặt tên theo `<op-id>` + session người dùng, để hai thao tác khác nhau chạy song song không lẫn lô.

### Menu trong registry

Đã kiểm chứng với [Creating Shortcut Menu Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/context-menu-handlers) (Microsoft Learn):

- `SubCommands` với verb tự định nghĩa cần `CommandStore` trong **HKLM** (phải admin), nên **không dùng**.
- Dùng `ExtendedSubCommandsKey`: chạy được trong HKCU, hỗ trợ menu lồng nhiều cấp, và nhiều loại file trỏ chung về một bộ menu.
- Verb dạng dòng lệnh: `MultiSelectModel=Document` (mặc định) chỉ hiện khi chọn ≤ 15 file; `Player` thì ≤ 100 file. Explorer vẫn gọi **mỗi file một tiến trình**. Đặt `Player` trên mọi verb.

Cấu trúc:

```
HKCU\Software\Classes\
  ConvertAnything.Menu.<nhóm>\shell\              ← bộ menu dùng chung cho 1 nhóm loại file
    convert\  MUIVerb=Convert, ExtendedSubCommandsKey=ConvertAnything.Menu.<nhóm>\convert
    ...
  ConvertAnything.Menu.<nhóm>\convert\shell\png\  MUIVerb=PNG, MultiSelectModel=Player
      command\ (Default) = "<installdir>\ca.exe" run image.to-png "%1"
  SystemFileAssociations\.jpg\shell\ConvertAnything\
    MUIVerb=ConvertAnything, Icon=<installdir>\ca.exe, ExtendedSubCommandsKey=ConvertAnything.Menu.<nhóm>
```

Chỉ ghi HKCU nên không cần admin. Key tool tạo đều có tên bắt đầu bằng `ConvertAnything`, gỡ là tìm theo tên đó.

**Giới hạn đã biết:** chọn > 100 file thì menu không hiện. Bỏ giới hạn cần verb COM (`DropTarget`), để phiên bản sau.

### Ngôn ngữ

Menu, thông báo, log, README: tiếng Anh. Tài liệu nội bộ (spec, plan) giữ tiếng Việt.

### Đặt tên output

- Cùng thư mục với file gốc. Tên do thao tác đề xuất (`anh.png`, `anh (nén).jpg`).
- Trùng tên thì thêm ` (1)`, ` (2)`...
- Ghi ra file tạm `.<tên>.partial` rồi đổi tên khi xong: hỏng giữa chừng không để lại file dở.

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

- **Unit** (`ConvertAnything.Core.Tests`): đặt tên output, mô hình menu từ danh sách thao tác, gom lô (logic chờ 400 ms với đồng hồ giả), parse lệnh.
- **Integration**: ghi/gỡ registry vào key test riêng `HKCU\Software\Classes\ConvertAnything.Test`, kiểm tra gỡ xong không còn key nào; gom lô thật bằng nhiều tiến trình `ca.exe`.
- **Runtime thủ công** (bắt buộc trước khi đóng module): bấm thật trong Explorer bằng thao tác `dev.copy`.
- Viết test trước, chạy đỏ, rồi mới code cho xanh (theo `test-driven-development`).
- Đường dẫn test luôn gồm tiếng Việt có dấu và dấu cách, ví dụ `Ảnh Đà Lạt\ảnh 1.jpg`.

## Boundaries

- **Always:** chạy test trước khi commit; chỉ ghi registry trong HKCU; không bao giờ sửa hoặc xóa file gốc; dùng `ArgumentList` khi gọi tiến trình ngoài.
- **Ask first:** thêm package NuGet; ghi HKLM; đổi module id hoặc op-id đã phát hành; cài phần mềm lên máy; push lên GitHub.
- **Never:** commit file nhị phân (ffmpeg, pdfium) vào git; gửi file hay dữ liệu ra mạng; thêm telemetry.

## Success Criteria

1. `ca.exe install-menu --dev` xong, chuột phải file `.txt` trong menu cũ thấy `ConvertAnything > Dev > Copy`.
2. Bấm vào 1 file: xuất hiện `tên (copy).txt` cạnh file gốc, có thông báo, **không** nháy cửa sổ console.
3. Chọn 20 file rồi bấm: đúng **một** tiến trình xử lý, **một** thông báo, đủ 20 file output. Chọn 100 file menu vẫn hiện.
4. File output đã tồn tại thì tên mới có ` (1)`; file gốc không đổi (so sánh hash trước/sau).
5. 1 file lỗi không chặn các file còn lại; thông báo ghi số file lỗi; log có stack trace.
6. `ca.exe uninstall-menu` xong không còn key nào tên `ConvertAnything` trong HKCU (test liệt kê kiểm tra).
7. Khởi động lạnh `ca.exe` (bản AOT) < 150 ms, đo bằng `Measure-Command`.
8. Đường dẫn tiếng Việt có dấu, dấu cách, dài > 260 ký tự đều chạy.
9. `dotnet test` và `dotnet format --verify-no-changes` đều qua.

## Quyết định đã chốt

1. Cài .NET 10 SDK qua winget.
2. Menu, thông báo, README: tiếng Anh.
3. Cài `gh` CLI, tạo repo GitHub private `ConvertAnything`.
4. License MIT. Thư viện phụ thuộc chỉ MIT/BSD/Apache/LGPL (LGPL thì liên kết động) để public được.
5. Registry: `ExtendedSubCommandsKey` + `MultiSelectModel=Player`, đã kiểm chứng với tài liệu Microsoft (xem mục Menu trong registry).

## Open Questions

Không còn.

# Spec: core

Module `core` trong [docs/specs/capability-map.md](capability-map.md). Không phụ thuộc module nào. Module `image`, `media`, `pdf`, `installer` đều xây trên nó.

## Objective

Cho phép người dùng Windows chuột phải vào file, chọn một thao tác trong menu `Flevert`, và nhận file kết quả nằm cạnh file gốc trong vài giây, không bật cửa sổ nào.

`core` không tự convert gì. Nó lo phần khung dùng chung:

- Ghi / gỡ mục menu chuột phải (kiểu cũ) trong registry, chỉ hiện thao tác phù hợp với loại file.
- Nhận lệnh từ Explorer, gom nhiều file được chọn cùng lúc thành **một lô** xử lý bởi **một tiến trình**.
- Đặt tên file output không bao giờ đè file có sẵn, không bao giờ sửa file gốc.
- Báo kết quả bằng một thông báo góc màn hình cho cả lô.
- Lô chạy lâu hơn 2 giây thì hiện cửa sổ tiến độ nhỏ (số file xong / tổng, nút Cancel; thao tác báo được tiến độ trong một file thì thêm dòng thứ hai, ví dụ `page 37 of 200`). Nén video mất vài phút; chạy im lặng thì người dùng tưởng treo. Cancel dừng **ngay** file đang chạy (bỏ file dở) và không xử lý các file còn lại.
- Ghi log lỗi để người dùng gửi lại khi báo bug.
- Định nghĩa hợp đồng `IOperation` để các module khác cắm thao tác vào.
- Báo khi GitHub có bản phát hành mới hơn và mời người dùng cập nhật (xem "Báo bản mới").

### User stories

- Là người dùng, tôi chuột phải 1 file → `Flevert` → thao tác, và thấy file mới cạnh file gốc.
- Là người dùng, tôi chọn 30 file, chuột phải → thao tác, và nhận **một** thông báo "Xong 30 file" chứ không phải 30 cửa sổ / 30 thông báo.
- Là người dùng, khi 2 trong 30 file lỗi, 28 file kia vẫn xong và thông báo nói rõ có 2 file lỗi, kèm chỗ xem log.
- Là người dùng, tôi gỡ tool và menu chuột phải biến mất hoàn toàn.
- Là người dùng bản cũ, khi có bản mới tôi được hỏi có muốn tải không, nhiều nhất một lần mỗi ngày, và có thể bỏ qua bản đó hoặc tắt hẳn việc kiểm.

## Tech Stack

| Thành phần | Lựa chọn | Lý do |
|---|---|---|
| Ngôn ngữ / runtime | C# trên .NET 10 (LTS, hỗ trợ tới 11/2028) | Gọi Win32 / registry dễ; một ngôn ngữ cho mọi module |
| Biên dịch | Self-contained, `win-x64`, ReadyToRun | Không cần cài runtime. **Không dùng NativeAOT**: cần Visual Studio C++ Build Tools (vài GB) và WinForms không hỗ trợ AOT |
| Kiểu exe | `WinExe` | Explorer gọi exe không nháy cửa sổ console |
| Test | xUnit | Chuẩn phổ biến của .NET |
| Thông báo + tiến độ | WinForms (`NotifyIcon.ShowBalloonTip`, một `Form` nhỏ) | Có sẵn trong .NET; balloon hiện như toast trên Win10/11, không cần AUMID / MSIX. Chỉ nạp WinForms khi cần hiện UI |
| Gom lô | Named pipe một instance (không mutex) | Có sẵn trong .NET; xem [ADR-0002](../adr/0002-batching-over-a-single-instance-pipe.md) |

Không thêm package NuGet nào ngoài xUnit cho module này. `Flevert.Core` không tham chiếu WinForms; UI nằm trong `Flevert.Cli`.

## Commands

```
Build:   dotnet build Flevert.sln -c Debug
Test:    dotnet test tests/Flevert.Core.Tests; dotnet test tests/Flevert.Image.Tests; dotnet test tests/Flevert.Pdf.Tests   (lần lượt, không song song)
Lint:    dotnet format Flevert.sln --verify-no-changes
Publish: dotnet publish src/Flevert.Cli -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o artifacts/publish
Startup: powershell -File scripts/measure-startup.ps1
```

Lệnh của `flevert.exe`:

```
flevert.exe run <op-id> <file>...        Chạy thao tác (Explorer gọi lệnh này)
flevert.exe install-menu [--dev] [--allow-insecure-location]
                                    Ghi menu chuột phải vào HKCU (--dev: thêm các thao tác thử dev.*:
                                    Copy, Slow copy, Split into pages, Join;
                                    --allow-insecure-location: cho phép thư mục người khác sửa được, đi qua junction,
                                    chứa link, nằm trên ổ subst/ổ mạng/UNC; mặc định thư mục cài phải ở ổ cố định
                                    cục bộ có ACL (NTFS, không FAT/exFAT) và đường dẫn thật trùng đường dẫn đưa vào.
                                    Trade-off chấp nhận: ổ cứng gắn ngoài và VHD cũng báo là ổ cố định; installer
                                    cài vào Program Files/LocalAppData nên không gặp)
flevert.exe uninstall-menu               Gỡ mọi key đã ghi
flevert.exe list-ops                     In danh sách thao tác và loại file áp dụng

Biến môi trường: FLEVERT_HEADLESS=1 (chạy lô không UI), FLEVERT_LOG_DIR (đổi thư mục log), FLEVERT_BATCH_IDLE_MS (cửa sổ rảnh của lô, 100–10 000 ms, thao tác N → 1 tối thiểu 3 000 ms; chỉ cho test tích hợp trên máy bận), FLEVERT_SLOW_COPY_MS (thời gian của thao tác dev Slow copy, 0–60 000 ms, mặc định 1 000; chỉ cho test tích hợp), FLEVERT_DEV_PAGE_MS (thời gian mỗi trang của thao tác dev Split into pages, 0–10 000 ms, mặc định 100; cho test tích hợp và để xem cửa sổ tiến độ), FLEVERT_UPDATE_URL (GitHub giả cho test báo bản mới; chỉ nhận `http://` tới địa chỉ IP loopback)
```

## Project Structure

```
Flevert.sln
src/
  Flevert.Core/        → Hợp đồng IOperation, gom lô, đặt tên output, mô hình menu, log
  Flevert.Cli/         → flevert.exe: đọc lệnh, ghi registry, hiện thông báo
tests/
  Flevert.Core.Tests/  → Unit test + integration test (registry trong key test riêng)
docs/adr/                      → Quyết định kiến trúc (ADR)
docs/specs/                    → Spec từng module (<module-id>.md), capability-map.md
tasks/                         → plan.md, todo.md
```

Module sau thêm project riêng: `src/Flevert.Image/`, `src/Flevert.Media/`, `src/Flevert.Pdf/`.

## Thiết kế

### Hợp đồng thao tác

```csharp
public interface IMenuOperation                // phần chung mà menu, catalog, list-ops cần
{
    string Id { get; }                         // "image.to-png" — ổn định, dùng trong registry
    string MenuPath { get; }                   // "Convert/PNG" — dấu / tạo menu con
    IReadOnlyCollection<string> Extensions { get; }  // ".jpg", ".heic", ...
}

public interface IOperation : IMenuOperation   // mỗi file một output
{
    int MaxParallel => 1;                      // xem "Xử lý song song trong một lô"
    Task<string> RunAsync(string inputPath, CancellationToken ct);  // trả về đường dẫn output (file hoặc thư mục)
    Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
        => RunAsync(inputPath, ct);            // runner gọi overload này; mặc định bỏ qua progress
}

public interface IBatchOperation : IMenuOperation  // cả lô → một output (ghép PDF, ảnh → PDF)
{
    Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct);
}
```

`OperationCatalog` nhận cả hai loại (`IMenuOperation`) và từ chối thao tác là cả hai loại hoặc không loại nào, vì `run` chọn đường chạy theo loại. Menu ghi cùng một lệnh cho cả hai loại (`run <op-id> "%1"`, Explorer vẫn chạy mỗi file một tiến trình); `list-ops` liệt kê cả hai.

**Tiến độ trong một file** ([ADR-0007](../adr/0007-progress-within-a-file-and-folder-outputs.md)): thao tác chạy lâu (render 200 trang) cài overload có `progress` và báo `new FileProgress("page", 37, 200)` (đơn vị là danh từ tiếng Anh số ít; vị trí từ 1 tới tổng, tổng ≥ 1, sai thì ném `ArgumentException`); overload cũ gọi overload mới với `null`. Thao tác không báo tiến độ chỉ cài overload cũ, không đổi gì. Runner chuyển mỗi lần báo thành `BatchProgress` có `Within` và `Current` là file đó, dưới cùng khóa với lúc file bắt đầu/xong, nên dòng trang luôn nằm giữa hai mốc đó; báo sau khi file đã xong thì bỏ. Cửa sổ tiến độ hiện dòng thứ hai `page 37 of 200` (`BatchProgressText.DescribeWithin`), xóa khi một file bắt đầu hoặc xong; thanh tiến độ vẫn đếm file. Tên trợ năng của dòng đó là `Progress in the current file: page 37 of 200` (UI Automation đọc được); không đặt live region để trình đọc màn hình không đọc từng trang một.

Thao tác dev `dev.split` (`Dev > Split into pages`, chỉ có với `install-menu --dev`): ghi 50 file trang vào thư mục `<tên> (pages)`, báo tiến độ mỗi trang, để thử output thư mục, tiến độ trong file và Cancel từ đầu tới cuối.

**Thao tác N → 1** ([ADR-0007](../adr/0007-progress-within-a-file-and-folder-outputs.md), phần 2), `Runner.RunAsync(IBatchOperation, files, progress, ct)`:

- **Chờ đủ lô:** runner đọc hết luồng file của chủ lô rồi mới làm gì khác. Chủ lô chỉ kết thúc luồng khi rảnh, hàng đợi trống, không ai gửi path trong cửa sổ rảnh và lô ≥ 1,5 s; lúc đó pipe đã đóng, nên hết luồng nghĩa là không còn file nào tới. Với thao tác N → 1 cửa sổ rảnh tối thiểu **3 s**, và chủ lô còn chờ cả lựa chọn của mình (mục dưới).
- **Không bao giờ thiếu file im lặng** (F5, [ADR-0007](../adr/0007-progress-within-a-file-and-folder-outputs.md) "No partial output"): đo ở cửa sổ thật khi máy bận, 100 tiến trình ra tới 65 output (9 trong 10 lần vỡ lô) (64 tiến trình hết hạn 10 s rồi tự ghép một mình). Vì vậy với thao tác N → 1 (`Batch.LeadOrJoinOneOutputAsync`): tiến trình giao file tối đa **60 s** và không bao giờ tự làm một mình; chủ lô xem danh sách tiến trình (`ProcessCensus`: cùng exe, session, integrity, dòng lệnh `run <op>`) và giữ lô mở khi còn tiến trình cùng lựa chọn chưa giao file (tối đa 30 s sau lần giao cuối); "cùng lựa chọn" = chuỗi thời điểm tạo tiến trình cách nhau ≤ **2 s** tính từ chủ lô (shell đo được ≤ 0,5 s khi máy bận), tiến trình ngoài chuỗi nhận trả lời `Late`; file `pipes\<op>.<scope>.join` ghi lô đang mở / đã đóng lúc nào / thành viên, ghi trước khi nhả tên pipe; tiến trình tạo ra trước lần đóng cuối là **đến muộn**; bản ghi đang mở mà chủ lô còn chạy (tên pipe trống trong chốc lát khi chủ lô thay instance) → trả tên, chờ pipe mới với vai người gửi để được ghép; bản ghi đang mở mà chủ lô đã chết lúc gom → tiến trình tạo trong vòng 30 s quanh chủ lô đó là đến muộn (file đã giao cho chủ lô chết bị mất, ghép phần còn lại sẽ trông như đủ). File đến muộn (cả khi bị `Late`, `Full`, bị từ chối hai lần, hết 60 s, không có khóa) vào lô riêng `<op>#late`: không ghi gì, log từng file, **một** thông báo `<file> and N more files were left out: they arrived while Flevert was joining other files. Select the files again.`, mã thoát 1. Tiến trình của lựa chọn chết trước khi giao file (bị giết, hết bộ nhớ) → cả output thất bại, nêu file đó (`… never reached Flevert, so nothing was joined.`). Thành viên mất ack thoát 0 im lặng. Rủi ro còn lại (chấp nhận, ghi ở ADR): shell dừng > 3 s giữa chừng sau khi mọi file trước đã giao; hai lựa chọn tạo cách nhau < 2 s coi là một; chủ lô crash lúc gom (file đã giao bị mất; phần còn lại của lựa chọn, và lựa chọn mới trong 30 s, bị "left out").
- **Ghim hết, giữ suốt lúc chạy:** mọi file qua đúng các kiểm tra của mục "Đầu vào bị từ chối"; mọi handle giữ tới khi thao tác xong (hoặc bị bỏ sau Cancel). Hai cách viết của cùng một file (tên 8.3 và tên dài) ra cùng đường dẫn thật thì chỉ ghép một lần.
- **Thứ tự:** `NaturalOrder.PathComparer`: `StrCmpLogicalW` (thứ tự Explorer dùng) trên tên file thật, rồi trên cả đường dẫn, rồi so ordinal, nên thứ tự luôn cố định dù file tới theo thứ tự nào. Giữ đúng giới hạn của Windows để khớp Explorer: dấu thanh và chữ đ xếp sau chữ và số (`Ảnh 2` trước `anh 3` trước `ảnh 10`; `đ` giữa `d` và `e`), dãy số tới 18 chữ số so theo giá trị, dài hơn thì so như chữ; theo locale của người dùng.
- **Tất cả hoặc không:** một file bị từ chối → thao tác không chạy, không có output; log ghi từng file bị từ chối kèm lý do, kết quả nêu file đầu tiên (theo thứ tự tự nhiên) và số file bị từ chối khác. Thao tác ném `BatchInputException.ForInput(inputPath, message)` khi không dùng được một file (ảnh hỏng) để thông báo nêu đúng file đó (constructor có đường dẫn để private: đường dẫn và message đều là chuỗi, dễ đảo); tạo bằng constructor chuẩn thì không nêu file nào, như lỗi khác: output thất bại với message của nó. Lô không nhận được file nào → `Failed: No files were received.` (không bao giờ `Done: 0 files`).
- **Thao tác** nhận danh sách đường dẫn thật (≥ 1 file, đã sắp), ghi output cạnh file đầu tiên qua `OutputPath`, tự quyết một file có được không (merge: không; ảnh → PDF: được).
- **Kết quả:** `BatchOutcome { OneOutput = true }`, `Total` = 1, một kết quả (không có khi bị hủy). Thông báo: `Done: <tên output>`; `Failed: <file>: <lý do> Details: <thư mục log>` (chỉ dòng đầu, tối đa 120 ký tự, stack ở log); `Skipped: <lý do>`; `Cancelled.`. Ghép xong thì log mỗi file đầu vào một dòng `ok` kèm output.
- **Tiến độ:** trong lúc gom file (tiến trình giao file có thể chậm vài giây), cửa sổ ghi `Waiting for the selected files…` thay vì để trống; rồi dòng lô đếm một "file": `0 of 1 done: <file đầu>`, dòng hai là tiến độ của thao tác (`file 3 of 12`), rồi `1 of 1`.
- **Hủy:** khi file còn đang tới thì không chạy gì; khi đang chạy thì thao tác nhận token và tối đa 2 s để dừng và xóa output tạm, sau đó bị bỏ và các file được thả ghim. Thao tác đã ghi xong output trong 2 s đó thì output có thật: báo `Done: …` và log như mọi lần ghép xong.

Thao tác dev `dev.join` (`Dev > Join`, chỉ có với `install-menu --dev`): nối các `.txt` theo thứ tự vào `<tên đầu> (+N).txt` cạnh file đầu (N = số file − 1; một file: `<tên> (joined).txt`), báo tiến độ mỗi file.

### Luồng một lần bấm

```
Explorer ──"flevert.exe run image.to-png <file1>"──▶ A tạo được pipe → A là chủ lô, xử lý file1 ngay
Explorer ──"flevert.exe run image.to-png <file2>"──▶ B: pipe đã có → gửi path → nhận ack → thoát
                                                   A: nhận file2 vào hàng đợi trong lúc đang xử lý
A: hết hàng đợi + đang rảnh + 400 ms không có path mới → đóng pipe → 1 thông báo → thoát
```

Tên pipe: `Flevert-<SID>-<session>-<khóa>-<op-id>`. Khóa ngẫu nhiên riêng cho mỗi thao tác, mỗi file exe và session (`<op>.<hash đường dẫn exe>-<session>.key`: hai bản flevert.exe từ chối nhau nên trước đây xoay khóa chung của nhau và làm vỡ lô của nhau) trong `%LOCALAPPDATA%\Flevert\pipes`, thư mục chỉ user / SYSTEM / Administrators mở được, nhãn medium no-read-up; khóa đổi mỗi khi một lô đóng. Hai đầu kiểm tra tiến trình bên kia chạy cùng file exe, cùng session, integrity không thấp hơn. Lô sống tối thiểu 1,5 s; lô đầy (1000 file) trả `Full`: người gửi đổi khóa và tự làm chủ một lô mới, các tiến trình đến sau vào lô đó. Chi tiết, các bảo đảm và trade-off: [ADR-0002](../adr/0002-batching-over-a-single-instance-pipe.md).

### Xử lý song song trong một lô

Đo trên máy 16 nhân, ảnh 12 MP: Magick.NET bản phát hành không có OpenMP (`ResourceLimits.Thread = 1`), phần ImageMagick của mỗi ảnh chỉ dùng một nhân (libheif tự giải mã các tile HEIC song song, ~2,8 nhân với ảnh iPhone thật); lô 10 ảnh HEIC→JPG chạy **lần lượt**. Vì vậy:

- `Runner.RunAsync` nhận `maxParallel` (mặc định 1 = hành vi cũ). `IOperation.MaxParallel` mặc định 1: thao tác tự chọn chạy song song (ảnh, dev: 8); ffmpeg ở v0.2 vốn đã dùng mọi nhân nên giữ 1. `flevert.exe` dùng `Clamp(min(MaxParallel, số nhân logic − 1), 1, 8)` và hạ ưu tiên tiến trình xuống BelowNormal khi chạy song song (để máy vẫn mượt; đo: thời gian lô không đổi trong nhiễu).
- Giữ nguyên mọi bảo đảm: mỗi file một kết quả, trùng thì bỏ, 1 file lỗi không chặn file khác, log mỗi file. `Results` theo thứ tự **xong**, không theo thứ tự nhận. Cancel: không lấy file mới, các file đang chạy nhận token; mỗi file bị hủy giữa chừng tính là chưa xử lý (một file đã qua bước kiểm token cuối trước khi đổi tên vẫn có thể có output; với file chạy song song, tối đa 8 file như vậy). Runner luôn chờ các file đang chạy xong rồi mới báo kết quả, kể cả khi nguồn file lỗi.
- Chủ lô chỉ đóng pipe khi **không còn file nào đang chạy** (đếm số file đang chạy, không chỉ cờ "đang cầm một file"): đóng sớm lúc vẫn còn file đang chạy sẽ khiến file đến muộn mở lô thứ hai.
- Hai file cùng muốn một tên output (`photo.png` và `photo.webp` → `photo.jpg`): `OutputPath` vốn đã an toàn khi ghi đồng thời (`File.Move` không ghi đè, trùng thì thử tên kế), nhưng ai được `photo.jpg` và ai được `photo (1).jpg` giờ tùy file nào xong trước.
- Lô 1 file: không đổi. Thời gian một file không giảm (nằm trong bộ giải mã); tối thiểu 1,5 s của lô (ADR-0002) giữ nguyên.

### Menu trong registry

Đã kiểm chứng với [Creating Shortcut Menu Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/context-menu-handlers) (Microsoft Learn):

- `SubCommands` với verb tự định nghĩa cần `CommandStore` trong **HKLM** (phải admin), nên **không dùng**.
- Dùng `ExtendedSubCommandsKey`: chạy được trong HKCU, hỗ trợ menu lồng nhiều cấp, và nhiều loại file trỏ chung về một bộ menu.
- Verb dạng dòng lệnh: `MultiSelectModel=Document` (mặc định) chỉ hiện khi chọn ≤ 15 file; `Player` thì ≤ 100 file. Explorer vẫn gọi **mỗi file một tiến trình**. Đặt `Player` trên mọi verb.

Cấu trúc:

```
HKCU\Software\Classes\
  Flevert.Menu.<.ext>\shell\              ← cây menu riêng cho mỗi đuôi file
    000\  MUIVerb=Convert, ExtendedSubCommandsKey=Flevert.Menu.<.ext>\Convert
    ...
  Flevert.Menu.<.ext>\Convert\shell\000\  MUIVerb=PNG, MultiSelectModel=Player
      command\ (Default) = "<installdir>\flevert.exe" run image.to-png "%1"
  SystemFileAssociations\.jpg\shell\Flevert\
    MUIVerb=Flevert, Icon=<installdir>\flevert.exe, ExtendedSubCommandsKey=Flevert.Menu.<.ext>
```

Chỉ ghi HKCU nên không cần admin. Key tool tạo đều có tên bắt đầu bằng `Flevert`, gỡ là tìm theo tên đó.

**Giới hạn đã biết:** chọn > 100 file thì menu không hiện. Bỏ giới hạn cần verb COM (`DropTarget`), để phiên bản sau.

**Tối đa 16 mục mỗi menu** (thấy bằng MenuProbe trên Windows 11, 2026-10-20; Microsoft không ghi): Explorer chỉ hiện 16 mục đầu của cây menu tĩnh dưới `Flevert` (tính cả submenu lẫn lệnh trong đó, theo thứ tự cây) và **lặng lẽ bỏ** phần còn lại. Khi T11 thêm `Combine into PDF`, menu `.heic`/`.heif` thành 17 mục và `Remove metadata` biến mất; bỏ trên registry một lệnh Convert thì nó hiện lại. Test `PdfMenuTests.No_file_type_gets_more_menu_entries_than_explorer_shows` giữ mọi đuôi ≤ 16 mục; thêm thao tác mới phải bớt mục khác.

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
- Ghi ra file tạm `.flevert-<guid>.partial` (độ dài cố định) rồi đổi tên khi xong: hỏng giữa chừng không để lại file dở. Tên output tự cắt bớt để vừa 255 ký tự; thư mục trùng tên cũng tính là đã có.
- **Output là thư mục** (1 → N, ví dụ mỗi trang PDF một file): `OutputPath.WriteDirectoryAtomicallyAsync(thư mục mong muốn, [hậu tố giữ nguyên,] write, ct)`. Tạo thư mục tạm `.flevert-<guid>.partial` cạnh file gốc (chỉ chứa file neo ẩn khi giao cho thao tác; đường dẫn là đường dẫn thật đã resolve, junction trong cách viết của người gọi bị bỏ qua), thao tác ghi mọi file vào đó, rồi đổi tên **một lần** sang tên trống đầu tiên: `tên`, `tên (1)`, … (cùng quy tắc và cắt 255 ký tự như file; file hoặc thư mục trùng tên đều tính là đã có; ` (n)` luôn ở cuối tên thư mục, kể cả khi tên có dấu chấm). Không bao giờ gộp vào hay ghi đè thư mục có sẵn; tên bị chiếm giữa lúc chọn và lúc đổi tên thì thử tên kế. Thư mục tạm được giữ bằng handle (không chia sẻ xóa) và một file neo ẩn `.flevert.lock` suốt lúc ghi, ẩn và không đánh chỉ mục, đổi tên qua handle; tên ` (pages)` không bị cắt khi rút gọn; file bên trong đang bị chương trình khác mở thì thử đổi tên lại tới khoảng 30 giây (Hủy dừng chờ ngay); hết thời gian mà vẫn bị giữ thì **giữ lại** các trang đã xong trong thư mục tạm (bỏ ẩn), báo lỗi nêu tên thư mục đó, không xóa. Lỗi hoặc hủy: xóa thư mục tạm và các file thao tác đã ghi, qua handle, không bao giờ đi theo link (link chỉ bị gỡ, thư mục con chỉ xóa khi rỗng), nên không xóa được gì bên ngoài; thứ gì không xóa được thì thư mục tạm ở lại và được bỏ ẩn để người dùng thấy. Đổi thuộc tính (ẩn/hiện, chỉ mục) giữ nguyên các bit khác, kể cả ghim OneDrive (ADR-0007, "Holding the folder").

### Log

`%LOCALAPPDATA%\Flevert\logs\flevert-YYYYMMDD.log`, mỗi dòng: thời gian, pid, op-id, file, kết quả (`ok` / `skipped` / `failed`), thời gian xử lý, output hoặc lý do. Lỗi ghi message rồi stack; file bị bỏ qua chỉ ghi lý do; file bị ngắt khi Cancel ghi `failed` với lý do `cancelled`. Giữ 7 ngày. Không ghi nội dung file.

### Báo bản mới

Người dùng cài từ file setup, không có kho ứng dụng tự cập nhật; không báo thì bản cũ (có thể có lỗi bảo mật đã sửa) ở lại mãi. Quyết định: [ADR-0006](../adr/0006-update-check-against-github-releases.md).

- **Khi nào:** chỉ tiến trình chủ lô có giao diện (không headless, không lệnh cài đặt, không tiến trình giao file), bắt đầu song song khi lô bắt đầu, nhiều nhất **một lần mỗi 24 giờ** (giờ kiểm ghi lại *trước* khi gửi, nên lỗi mạng cũng tính; giờ ghi ở tương lai coi như đến hạn).
- **Hỏi gì:** `GET https://api.github.com/repos/Suryvibegod/Flevert/releases/latest`, `User-Agent: Flevert/<phiên bản>`, không cookie, không thông tin máy hay file; timeout 5 giây; đọc tối đa 1 MB; chỉ đọc `tag_name`. Endpoint này bỏ qua bản nháp và pre-release, nên một bản bị rút (đánh dấu pre-release theo RELEASING) không được mời cài.
- **So sánh:** tag phải đúng `vMAJOR.MINOR.PATCH` (chữ số ASCII, mỗi phần ≤ 5 chữ số); khác dạng thì bỏ qua. Lớn hơn phiên bản đang chạy và không phải bản người dùng đã bỏ qua thì hỏi.
- **Hỏi người dùng:** sau khi lô xong (thông báo kết quả vẫn hiện như cũ), một hộp thoại Windows: "Flevert X.Y.Z is available" / "You have A.B.C. Download the new setup from GitHub and run it; it installs over this version." Nút **Download** (mở `https://github.com/Suryvibegod/Flevert/releases/latest` bằng trình duyệt mặc định; **không** dùng URL nào lấy từ phản hồi), **Later**, **Skip this version**. Nút mặc định là **Later** (phím gõ nhầm từ cửa sổ khác không mở trình duyệt); hộp thoại nằm trên cùng nhưng không giành focus; không trả lời sau 10 phút thì coi như Later (tiến trình còn chạy thì setup phải chờ). Thông báo đóng sớm mà việc kiểm chưa xong thì tiến trình chờ kết quả, tối đa thêm 10 giây sau thời gian thông báo; quá hạn thì bỏ, lần sau kiểm lại.
- **Không bao giờ** tự tải hay chạy gì: người dùng tự tải setup và kiểm như README hướng dẫn.
- **Tắt/bật:** `flevert.exe update-check off|on`; setup có ô chọn "Tell me when a new version is out" (mặc định bật; `/UPDATECHECK=0|1` khi cài im lặng, giá trị khác bị bỏ qua; không có tham số thì giữ lựa chọn hiện tại, đọc từ `state.json` vì `update-check off` có thể đã đổi nó sau lần cài trước, rồi tới lựa chọn của lần cài trước). Setup báo lỗi nếu không tắt được (`update-check off` thất bại).
- **Trạng thái:** `%LOCALAPPDATA%\Flevert\update\state.json` (`enabled`, `lastCheck`, `skipped`); thiếu hoặc nội dung hỏng thì dùng mặc định (bật, chưa kiểm), vì file được flush xuống đĩa trước khi thay nên hỏng không do Flevert, và tắt luôn sẽ bỏ người dùng ở bản cũ; file có mà không đọc được (chương trình khác đang giữ, không có quyền, hoặc là thư mục) thì không kiểm và không ghi gì, kể cả `update-check on|off` (báo lỗi), để không bao giờ vô tình bật lại. Ghi: file tạm, flush xuống đĩa, rồi thay. Gỡ cài đặt xóa thư mục này.
- Mọi lỗi (mạng, JSON, ghi file) đều im lặng: không bao giờ làm lô lỗi hay chặn tiến trình thoát.

### Bỏ qua (skipped)

Thao tác ném `OperationSkippedException` khi đầu vào đã đáp ứng yêu cầu, không có gì đáng ghi (ví dụ ảnh đã nén tốt); yêu cầu không làm được là lỗi, không phải bỏ qua. File bị bỏ qua không tính là lỗi. Thông báo: `Done: 2 files, 30 skipped`; cả lô bị bỏ qua: `Nothing to do: 3 files skipped (<lý do>)`, hoặc chỉ tới log nếu lý do khác nhau; một file: `Skipped: <lý do>`.

Thông báo khi có file lỗi: còn file xong thì `Done: 28 files, 2 failed. Details: <thư mục log>`; không file nào xong thì không nói `Done:`: một file → `Failed: <file>: <lý do> Details: <thư mục log>` (dòng đầu của lỗi, tức message của exception, tối đa 120 ký tự; stack ở log), nhiều file → `Failed: 3 files. Details: <thư mục log>` (kèm `, 1 skipped` nếu có file bị bỏ qua).

Bấm vào thông báo mở log khi thông báo có chỉ tới log: có file lỗi (kể cả lô bị hủy giữa chừng), hoặc cả lô bị bỏ qua vì nhiều lý do khác nhau.

Mã thoát của `flevert.exe run` (dùng cho script, chế độ headless): chỉ **tiến trình chủ lô** báo kết quả lô: 0 nếu mọi file đã xử lý và không file nào lỗi (file bị bỏ qua vẫn là 0, dù không có output; thao tác N → 1: output đã ghi hoặc bị bỏ qua); 1 nếu có file lỗi hoặc lô bị hủy (N → 1: output thất bại, kể cả vì một file bị từ chối); 2 nếu sai cú pháp/thao tác không tồn tại. Tiến trình giao file cho một lô đang chạy thoát 0 ngay khi giao xong, bất kể kết quả các file đó; script cần kết quả từng file thì đọc log (hoặc chạy các lệnh cách nhau quá cửa sổ gom lô).

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

- **Unit** (`Flevert.Core.Tests`): đặt tên output, mô hình menu từ danh sách thao tác, gom lô trong cùng tiến trình (50 lời gọi song song, file đến muộn khi đang xử lý, surrogate lẻ), parse lệnh, thứ tự tự nhiên (`NaturalOrderTests`), thao tác N → 1 (`RunnerBatchOperationTests`: chờ đủ lô, tất cả hoặc không, ghim suốt lúc chạy, tiến độ, hủy).
- **Tiến trình thật** (`ProcessBatchIntegrationTests`): chạy `flevert.exe` với `FLEVERT_HEADLESS=1` và `FLEVERT_LOG_DIR` trỏ thư mục tạm; các test N → 1 chạy ở cửa sổ thật (không `FLEVERT_BATCH_IDLE_MS`): 12 và 100 tiến trình `dev.join` → đúng một file theo thứ tự tự nhiên; tiến trình khởi động chậm (tạo ở trạng thái suspend) được chờ; tiến trình đến muộn sau khi lô đóng và lựa chọn thứ hai trong lúc lô đang gom → "left out", không output thứ hai; tiến trình bị giết trước khi giao → lô thất bại nêu file; hai lần ghép liền nhau đều ra output; hai bản exe chạy cùng lúc mỗi bên một output. Quy tắc thuần (`JoinRulesTests`): chuỗi thời điểm tạo, đến muộn, bản ghi lô.
- **Integration**: ghi/gỡ registry vào key test riêng `HKCU\Software\Classes\Flevert.Test`, kiểm tra gỡ xong không còn key nào; gom lô thật bằng nhiều tiến trình `flevert.exe`.
- **Runtime thủ công** (bắt buộc trước khi đóng module): bấm thật trong Explorer bằng thao tác `dev.copy`.
- Viết test trước, chạy đỏ, rồi mới code cho xanh (theo `test-driven-development`).
- Đường dẫn test luôn gồm tiếng Việt có dấu và dấu cách, ví dụ `Ảnh Đà Lạt\ảnh 1.jpg`.

## Boundaries

- **Always:** chạy test trước khi commit; chỉ ghi registry trong HKCU; không bao giờ sửa hoặc xóa file gốc; dùng `ArgumentList` khi gọi tiến trình ngoài.
- **Ask first:** thêm package NuGet; ghi HKLM; đổi module id hoặc op-id đã phát hành; cài phần mềm lên máy; push lên GitHub.
- **Never:** commit file nhị phân (ffmpeg, pdfium) vào git; gửi file hay dữ liệu ra mạng (ngoại lệ duy nhất: request kiểm phiên bản ở mục "Báo bản mới", tắt được); thêm telemetry; tự tải hay chạy bản cập nhật.

## Success Criteria

1. `flevert.exe install-menu --dev --allow-insecure-location` (chạy từ thư mục build) xong, chuột phải file `.txt` trong menu cũ thấy `Flevert > Dev > Copy`.
2. Bấm vào 1 file: xuất hiện `tên (copy).txt` cạnh file gốc, có thông báo, **không** nháy cửa sổ console.
3. Chọn 20 file rồi bấm: đúng **một** tiến trình xử lý, **một** thông báo, đủ 20 file output. Chọn 100 file menu vẫn hiện.
4. File output đã tồn tại thì tên mới có ` (1)`; file gốc không đổi (so sánh hash trước/sau).
5. 1 file lỗi không chặn các file còn lại; thông báo ghi số file lỗi; log có stack trace.
6. `flevert.exe uninstall-menu` xong không còn key nào tên `Flevert` trong HKCU (test liệt kê kiểm tra).
7. Khởi động lạnh `flevert.exe run` (bản publish, chưa nạp WinForms) < 300 ms, đo bằng `Measure-Command`, trung vị 10 lần.
8. Đường dẫn tiếng Việt có dấu, dấu cách, dài > 260 ký tự đều chạy.
9. `dotnet test` và `dotnet format --verify-no-changes` đều qua.
10. Lô chạy > 2 giây hiện cửa sổ tiến độ; bấm Cancel thì dừng ngay, không để lại file `.partial`.
11. Bản 0.1.0 thấy tag `v0.2.0` trên GitHub: sau một lô có giao diện thì hiện hộp thoại mời cập nhật; Download mở trang Releases; trong 24 giờ không hỏi lại; Skip this version thì không hỏi lại bản đó nhưng vẫn hỏi `v0.2.1`.
12. `update-check off` thì không có request nào (test bằng handler giả); mất mạng hay GitHub trả lỗi thì lô vẫn xong và tiến trình thoát như thường.
13. Tag sai dạng (`v0.2`, `v0.2.0-rc1`, chữ số không phải ASCII, phần quá dài) không bao giờ gây hỏi.

## Quyết định đã chốt

1. Cài .NET 10 SDK cho riêng user bằng `dotnet-install.ps1` (không cần admin, không kẹt UAC); `scripts/setup-dev.ps1` làm việc này.
2. Menu, thông báo, README: tiếng Anh.
3. Cài `gh` CLI, tạo repo GitHub private `Flevert`.
4. License MIT. Thư viện phụ thuộc chỉ MIT/BSD/Apache/LGPL (LGPL thì liên kết động) để public được.
5. Registry: `ExtendedSubCommandsKey` + `MultiSelectModel=Player`, đã kiểm chứng với tài liệu Microsoft (xem mục Menu trong registry).

## Open Questions

Không còn.

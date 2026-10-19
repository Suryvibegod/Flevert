# Spec: installer

Module `installer` trong [CAPABILITY-MAP.md](CAPABILITY-MAP.md). Phụ thuộc `core` (lệnh `install-menu`/`uninstall-menu`, kiểm tra `InstallLocation`) và đóng gói theo [ADR-0003](docs/adr/0003-self-contained-readytorun-winforms.md). Cùng với core + image tạo thành bản **v0.1**.

## Objective

Người dùng phổ thông tải một file `ConvertAnything-Setup-0.1.0.exe`, bấm đúp, Next vài lần là chuột phải vào ảnh thấy menu `ConvertAnything`. Không cần quyền admin, không cần cài .NET, không tốn đồng nào. Gỡ ra từ Settings > Apps thì sạch: không còn file, không còn khóa registry, không còn menu chết.

Ràng buộc của người dùng (2026-10-19): **mọi thứ miễn phí**: công cụ build, công cụ đóng gói, ký mã. Không mua chứng chỉ.

### User stories

- Tải setup → chạy → không có hộp thoại UAC → xong trong < 30 s → chuột phải `IMG_0001.HEIC` → `ConvertAnything > Convert > JPG` chạy được.
- Trên Windows 11, tick ô "Use the classic right-click menu" khi cài → sau khi khởi động lại Explorer, menu của app hiện ngay, không phải bấm "Show more options".
- Cài bản mới đè bản cũ → menu vẫn đúng, không phải gỡ trước.
- Gỡ cài đặt → mọi thứ app tạo ra biến mất; menu cổ điển chỉ bị tắt nếu chính installer đã bật nó.

## Tech Stack

| Thành phần | Lựa chọn | Lý do |
|---|---|---|
| Công cụ đóng gói | **Inno Setup 6.7.3** | Giấy phép cho dùng mọi mục đích kể cả thương mại, không phí (kiểm tại jrsoftware.org/files/is/license.txt, 2026-10-19). Cài per-user không admin (`PrivilegesRequired=lowest`), nén LZMA2, có sẵn trên runner `windows-latest` của GitHub Actions. Bản 7 mới ra, chưa cần tính năng nào của nó |
| Payload | `dotnet publish` self-contained, ReadyToRun, win-x64 (ADR-0003) | Không bắt người dùng cài .NET |
| Logic registry | Viết bằng C# trong `ca.exe` (test được), installer chỉ gọi | Một nguồn sự thật cho tên khóa; script Pascal chỉ là lưới an toàn khi gỡ |
| Ký mã | **Chưa ký ở v0.1.** Sau khi repo public: xin **SignPath Foundation** (miễn phí cho OSS, ký trên CI) | Chứng chỉ OV/EV và Azure Trusted Signing đều tốn tiền. Chứng chỉ tự ký không giúp gì với SmartScreen/Smart App Control. Xem ADR-0005 |

Đã cân nhắc:
- WiX 6: miễn phí với dự án OSS nhưng có "Open Source Maintenance Fee" cho tổ chức có doanh thu, mơ hồ hơn; MSI cũng nặng nề hơn cho cài per-user.
- MSIX: cần ký số mới cài được, trái ràng buộc miễn phí.
- Chỉ phát hành zip kèm `ca.exe install-menu`: không có mục gỡ trong Settings, người dùng phổ thông dễ giải nén vào Downloads rồi xóa mất.

## Thiết kế

### Cài đặt

- Thư mục: `{autopf}\ConvertAnything`, với cài per-user tức `%LOCALAPPDATA%\Programs\ConvertAnything`. Không cho chọn thư mục (`DisableDirPage=yes`): thư mục khác dễ trượt kiểm tra `InstallLocation` (ổ USB, ổ mạng, thư mục người khác ghi được) và chỉ đổi lấy rắc rối. Đã kiểm trên máy thật: mọi thư mục cha từ `C:\` tới `%LOCALAPPDATA%\Programs` chỉ cho tài khoản khác quyền đọc.
- `PrivilegesRequired=lowest`, không cho chuyển sang cài toàn máy: menu nằm ở HKCU của từng người, cài toàn máy không đem lại gì.
- `ArchitecturesAllowed=x64compatible` (chạy cả Windows ARM64 qua giả lập), `MinVersion=10.0.17763`.
- Sau khi chép file: chạy `ca.exe install-menu` (ẩn cửa sổ, đợi xong). Nếu mã thoát khác 0, báo lỗi rõ ràng; mã 4551 (`ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION`, Smart App Control chặn file) có thông báo riêng chỉ cách xử lý. File vẫn giữ, người dùng có thể gỡ bình thường.
- Trang "Right-click menu" (chỉ hiện trên Windows 11 build ≥ 22000) có một checkbox, mặc định không tick: tick thì chạy `ca.exe classic-menu on`, không tick thì `classic-menu off` (chỉ gỡ khóa do app tạo, nên bỏ tick khi cài đè sẽ hoàn tác). Lựa chọn được nhớ cho lần cài đè sau. Cài im lặng: `/CLASSICMENU=1|0`, không truyền thì giữ lựa chọn cũ. Trang cuối nhắc cách mở menu; với menu cổ điển: có hiệu lực sau khi khởi động lại Explorer hoặc đăng xuất.
  - Không dùng `[Tasks]`: danh sách task (`TNewCheckListBox`) của Inno 6.7.3 vẽ checkbox bị cắt viền trái ở 200% DPI (người dùng phát hiện 2026-10-19); checkbox thường (`TNewCheckBox`) do Windows vẽ thì đúng.
  - Không dùng `WizardStyle=modern windows11` (VCL style): Smart App Control chặn mọi bản setup build với style đó trên máy dev (2 lần liên tiếp), bỏ style thì chạy được.
- Cài đè: `AppId` cố định, `CloseApplications=yes` (Restart Manager đóng `ca.exe` đang chạy). `install-menu` tự gỡ menu cũ trước khi ghi.
- Kèm theo: `LICENSE.txt` của app, `THIRD-PARTY-NOTICES.txt` gom từ Notice.txt của Magick.NET và LICENSE/ThirdPartyNotices của .NET runtime (bắt buộc khi phân phối lại bản self-contained).
- Phiên bản: lấy từ `<Version>` trong `Directory.Build.props`, installer đọc lại từ file version của `ca.exe` nên chỉ có một nơi khai báo.

### Menu cổ điển (`ca.exe classic-menu on|off`)

Windows 11 hiện menu cổ điển khi có khóa `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32` với giá trị mặc định rỗng. Khóa này ảnh hưởng menu của mọi app, nên:

- `on`: nếu khóa `CLSID\{86ca1aa0-…}` **chưa có**, tạo khóa và đặt giá trị đánh dấu `ConvertAnything` trên khóa `{86ca1aa0-…}`. Nếu đã có (người dùng tự bật trước đó), không đụng tới và không đánh dấu.
- `off`: chỉ xóa khi có giá trị đánh dấu. Khóa người dùng tự tạo được giữ nguyên.
- Không tự khởi động lại Explorer: giết Explorer làm mất cửa sổ thư mục đang mở của người dùng.

### Gỡ cài đặt

Lúc `usUninstall` (trước khi xóa file), theo thứ tự:
1. `ca.exe uninstall-menu`, `ca.exe classic-menu off`.
2. Lưới an toàn bằng Pascal, chạy **luôn** (vô hại nếu bước 1 đã dọn): xóa `SystemFileAssociations\*\shell\ConvertAnything`, mọi khóa `ConvertAnything.Menu*` dưới `HKCU\Software\Classes`, và khóa menu cổ điển nếu có đánh dấu. Cần vì bước 1 có thể không chạy được (Smart App Control chặn, file hỏng), và menu trỏ tới exe đã xóa là thứ tệ nhất để lại.
3. Inno xóa file đã cài; `[UninstallDelete]` xóa `%LOCALAPPDATA%\ConvertAnything` (log, khóa gom lô, policy ImageMagick). Log chỉ là chẩn đoán của chính app nên không giữ lại.

### Ký mã và cảnh báo của Windows

File chưa ký tải từ mạng: SmartScreen hiện "Windows protected your PC" → `More info` → `Run anyway`. Smart App Control (enforce) có thể chặn hẳn và không có nút bỏ qua. README ghi rõ cả hai và giải thích vì sao (dự án miễn phí, đang xin ký qua SignPath Foundation). Khi được duyệt, workflow release trên GitHub Actions gửi file đi ký trước khi đính kèm vào release.

## Commands

```
powershell -File scripts/build-installer.ps1            # publish + ISCC → artifacts/installer/ConvertAnything-Setup-<ver>.exe
powershell -File scripts/test-installer.ps1             # cài thật (im lặng) → kiểm → gỡ → kiểm sạch; từ chối chạy nếu máy đã cài sẵn
ca classic-menu on|off
```

## Project Structure

```
installer/ConvertAnything.iss          script Inno Setup
scripts/build-installer.ps1            publish, gom notices, gọi ISCC
scripts/test-installer.ps1             kiểm thử đầu-cuối trên máy thật
src/ConvertAnything.Core/Menu/ClassicMenu.cs
tests/ConvertAnything.Core.Tests/ClassicMenuTests.cs
docs/adr/0005-inno-setup-unsigned-until-signpath.md
```

## Code Style

Theo [SPEC-core.md](SPEC-core.md#code-style). Script Inno: hằng số ở `#define` đầu file, Pascal ngắn, mỗi hàm một việc, chú thích nói *vì sao*. PowerShell: `Set-StrictMode -Version Latest`, `$ErrorActionPreference = 'Stop'`, kiểm `$LASTEXITCODE` sau mỗi lệnh ngoài.

## Testing Strategy

| Tầng | Kiểm cái gì |
|---|---|
| Unit (xUnit, `FakeRegistry`) | `ClassicMenu`: bật khi chưa có → có khóa + đánh dấu; bật khi đã có → không đổi gì; tắt chỉ xóa khi có đánh dấu; bật/tắt lặp lại không lỗi. `CommandLine` nhận `classic-menu on|off`, từ chối thứ khác |
| Đầu-cuối (`test-installer.ps1`, máy thật) | Build installer → cài im lặng (`/VERYSILENT /SUPPRESSMSGBOXES /CLASSICMENU=1`) → có file, có mục gỡ trong HKCU Uninstall, có menu cho `.heic`/`.jpg`, có khóa menu cổ điển + đánh dấu → `ca.exe` đã cài chạy thật `image.to-jpg` trên fixture HEIC (chứng minh bản self-contained đủ native) → cài đè không tham số (giữ menu cổ điển) → cài đè `/CLASSICMENU=0` (gỡ khóa) → gỡ im lặng → không còn thư mục app, `%LOCALAPPDATA%\ConvertAnything`, khóa menu, khóa menu cổ điển, mục gỡ. Kịch bản 2: cài, đổi tên `ca.exe`, gỡ → lưới an toàn Pascal vẫn dọn sạch registry. Kịch bản 3: khóa menu cổ điển có sẵn (không đánh dấu) → cài với `/CLASSICMENU=1` rồi gỡ → khóa vẫn còn |

Thứ tự TDD: viết `ClassicMenuTests` và `test-installer.ps1` trước, chạy thấy đỏ, rồi mới viết code và `.iss`.

## Boundaries

- **Always:** cài per-user, không admin; chỉ ghi registry dưới HKCU; gỡ sạch những gì app tạo ra; ghi lý do mọi quyết định vào spec/ADR.
- **Ask first:** không có (người dùng đã giao quyền tự quyết). Riêng việc đổi repo sang public: người dùng đã đồng ý (2026-10-19) với điều kiện xong quy trình review.
- **Never:** dùng công cụ/dịch vụ trả phí; ghi HKLM; tự khởi động lại Explorer; xóa khóa menu cổ điển do người dùng tự tạo; commit binary (bản publish, setup.exe).

## Success Criteria

1. `build-installer.ps1` chạy từ máy sạch (có SDK + Inno Setup) ra một file setup ≤ 60 MB.
2. Cài không hiện UAC, xong < 30 s; `ca.exe` đã cài chạy `image.to-jpg` thành công trên HEIC.
3. Sau khi gỡ: 0 file trong thư mục cài và `%LOCALAPPDATA%\ConvertAnything`, 0 khóa `ConvertAnything*` dưới `HKCU\Software\Classes`, 0 mục gỡ cài đặt.
4. Lưới an toàn: gỡ khi `ca.exe` không chạy được vẫn đạt (3).
5. Menu cổ điển: bật/tắt chỉ đụng tới khóa do app tạo (unit test + kịch bản 3).
6. Cài đè bản cũ: menu vẫn đầy đủ, không trùng lặp.
7. `dotnet test` xanh, `dotnet format --verify-no-changes` exit 0, ba vòng review không còn Critical/High/Required.

## Open Questions

- SignPath Foundation cần repo public + build trên CI; sẽ nộp đơn sau khi public (người dùng phải tự điền đơn bằng tài khoản của họ).
- Icon riêng cho app (hiện dùng icon exe mặc định): làm ở bước chuẩn bị phát hành.

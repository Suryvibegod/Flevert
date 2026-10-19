# Spec: installer

Module `installer` trong [CAPABILITY-MAP.md](CAPABILITY-MAP.md). Phụ thuộc `core` (lệnh `install-menu`/`uninstall-menu`, kiểm tra `InstallLocation`) và đóng gói theo [ADR-0003](docs/adr/0003-self-contained-readytorun-winforms.md). Cùng với core + image tạo thành bản **v0.1**.

## Objective

Người dùng phổ thông tải một file `ConvertAnything-Setup-0.1.0.exe`, bấm đúp, Next vài lần là chuột phải vào ảnh thấy menu `ConvertAnything`. Không cần quyền admin, không cần cài .NET, không tốn đồng nào. Gỡ ra từ Settings > Apps thì sạch: không còn file, khóa registry hay menu chết nào do ConvertAnything ghi ra. (Dấu vết do chính Windows ghi, như MuiCache, UserAssist, biểu tượng khay, nằm ngoài phạm vi.)

Ràng buộc của người dùng (2026-10-19): **mọi thứ miễn phí**: công cụ build, công cụ đóng gói, ký mã. Không mua chứng chỉ.

### User stories

- Tải setup → chạy → không có hộp thoại UAC → xong trong < 30 s → chuột phải `IMG_0001.HEIC` → `ConvertAnything > Convert > JPG` chạy được.
- Trên Windows 11, tick ô "Use the classic (Windows 10 style) right-click menu for all apps" khi cài → sau khi đăng xuất/đăng nhập lại hoặc khởi động lại máy, menu của app hiện ngay, không phải bấm "Show more options".
- Cài bản mới đè bản cũ → menu vẫn đúng, không phải gỡ trước.
- Gỡ cài đặt → mọi thứ app tạo ra biến mất; menu cổ điển chỉ bị tắt nếu chính installer đã bật nó.

## Tech Stack

| Thành phần | Lựa chọn | Lý do |
|---|---|---|
| Công cụ đóng gói | **Inno Setup 6.7.3** | Giấy phép cho dùng mọi mục đích kể cả thương mại, không phí (kiểm tại jrsoftware.org/files/is/license.txt, 2026-10-19). Cài per-user không admin (`PrivilegesRequired=lowest`), nén LZMA2. Script đòi ≥ 6.7.3 (`#if VER`) vì RedirectionGuard và thư mục tạm ngẫu nhiên; CI cài đúng 6.7.3 từ release chính thức và kiểm SHA256. Bản 7 mới ra, chưa cần tính năng nào của nó |
| Payload | `dotnet publish` self-contained, ReadyToRun, win-x64 (ADR-0003) | Không bắt người dùng cài .NET |
| Logic registry | Viết bằng C# trong `ca.exe` (test được), installer chỉ gọi | Một nguồn sự thật cho tên khóa; script Pascal chỉ là lưới an toàn khi gỡ |
| Ký mã | **Chưa ký ở v0.1.** Sau khi repo public: xin **SignPath Foundation** (miễn phí cho OSS, ký trên CI) | Chứng chỉ OV/EV và Azure Trusted Signing đều tốn tiền. Chứng chỉ tự ký không giúp gì với SmartScreen/Smart App Control. Xem ADR-0005 |

Đã cân nhắc:
- WiX 6: miễn phí với dự án OSS nhưng có "Open Source Maintenance Fee" cho tổ chức có doanh thu, mơ hồ hơn; MSI cũng nặng nề hơn cho cài per-user.
- MSIX: cần ký số mới cài được, trái ràng buộc miễn phí.
- Chỉ phát hành zip kèm `ca.exe install-menu`: không có mục gỡ trong Settings, người dùng phổ thông dễ giải nén vào Downloads rồi xóa mất.

## Thiết kế

### Cài đặt

- Thư mục: `{autopf}\ConvertAnything`, với cài per-user tức `%LOCALAPPDATA%\Programs\ConvertAnything`. Không cho chọn thư mục: thư mục khác dễ trượt kiểm tra `InstallLocation` (ổ USB, ổ mạng, thư mục người khác ghi được), và setup chạy `ca.exe`/`unins000.exe` từ đó nên một DLL người khác đặt vào sẽ chạy dưới quyền người dùng. Ẩn trang chọn thư mục chưa đủ (`/DIR`, `UsePreviousAppDir` vẫn đổi được), nên `UsePreviousAppDir=no` và `PrepareToInstall` từ chối mọi thư mục khác `{userpf}\ConvertAnything` (setup thoát mã 7). Đã kiểm trên máy thật: mọi thư mục cha từ `C:\` tới `%LOCALAPPDATA%\Programs` chỉ cho tài khoản khác quyền đọc.
- `PrepareToInstall` còn từ chối, **trước khi chép gì**: (a) `{app}` hoặc bất kỳ thư mục cha nào tới gốc ổ là reparse point (junction/symlink): file chép qua link rơi vào nơi link trỏ tới; kiểm cả cha thay vì chỉ `{app}`/`{userpf}` vì `ca.exe` cũng từ chối link ở mọi cấp, từ chối sớm thì không để lại gì; (b) `{app}` có sẵn, không rỗng, mà không có mục gỡ `{AppId}_is1` của app: `ca.exe` sẽ nạp DLL ai đó đặt sẵn. Cả hai thoát mã 7, lý do nằm trong log và link README `#setup-refused-the-install-folder`.
- Rồi kiểm quyền **trước khi chép hay chạy gì trong `{app}`** (review vòng 3, SA H-1): tạo `{app}` nếu chưa có (để thấy đúng ACL được kế thừa), `ExtractTemporaryFiles('{app}\*')` bung bản cài vào `{tmp}\{app}`, chạy `{tmp}\{app}\ca.exe check-location "<app>"` (cùng kiểm tra `InstallLocation` như `install-menu`: chủ sở hữu, DACL của thư mục, mọi thứ bên trong và mọi thư mục cha, link, ổ cố định có ACL). Không an toàn (mã 3) hoặc không chạy được (Smart App Control, chính sách chặn chương trình trong thư mục tạm, lỗi giải nén…) → thoát 7, xóa `{app}` (và `Programs`) nếu chính setup vừa tạo. Hệ quả: máy bật Smart App Control chặn `ca.exe` giờ bị từ chối cả bản cài (mã 7) thay vì cài thiếu menu (mã 10). Với thư mục cài và mọi thứ bên trong, ACE inherit-only (chỉ áp cho file/thư mục tạo sau) cũng tính, vì lúc kiểm thư mục còn rỗng (review vòng 4). Bản bung thêm ~140 MB vào `{tmp}`; ổ đầy thì lỗi giải nén có thông báo riêng. Không dùng `ExtraDiskSpaceRequired`: nó chỉ đổi con số hiển thị (trang chọn thư mục đã ẩn) và làm Installed apps báo gấp đôi dung lượng.
- **Giả định `%TEMP%`:** chỉ người dùng ghi được, như Inno vốn giả định cho `setup.tmp`. Ví dụ ngược: `TEMP` chuyển sang `C:\Temp` (kế thừa Modify của Authenticated Users từ `C:\`), hay máy dev này (nhóm `CodexSandboxUsers` có Modify); ở đó tài khoản khác có thể tráo file vừa bung trong vài giây trước khi chạy. Lý do: chạy `ca.exe` trong `{app}` trước khi kiểm thì DLL người khác thay vào đã chạy rồi. `{tmp}` nằm trong `%TEMP%` của người dùng, nơi Inno vốn đã chạy `setup.tmp`, nên không thêm giả định tin cậy mới. Trade-off: quyền bị nới **sau** khi cài cần chính người dùng hoặc admin; gỡ cài đặt không kiểm lại.
- Vòng kiểm link đi tới **gốc ổ** (`RemoveBackslashUnlessRoot`): `C:` không có `\` là thư mục hiện tại trên ổ C, không phải gốc.
- `PrivilegesRequired=lowest`, không cho chuyển sang cài toàn máy: menu nằm ở HKCU của từng người, cài toàn máy không đem lại gì.
- `ArchitecturesAllowed=x64compatible` (Windows ARM64 chạy được qua giả lập về lý thuyết, **chưa kiểm**), `MinVersion=10.0.17763`.
- Sau khi chép file: chạy `ca.exe install-menu` (ẩn cửa sổ, đợi xong, `ExecAndCaptureOutput` lấy stderr). Nếu thất bại:
  - Hộp thoại nêu mã và **dòng stderr đầu tiên** của `ca.exe` (mọi dòng vào log). Mã 3 (thư mục bị từ chối): câu "Running setup again will not change this" + link `README#setup-refused-the-install-folder`. Mã 1–2: link `README#if-the-menu-is-missing`. Không chạy được, hoặc mã ngoài 1–3 (chết lúc nạp DLL, như khi Smart App Control chặn một DLL, gồm cả 4551): "Windows blocked ConvertAnything or it stopped unexpectedly" + link `README#smart-app-control`.
  - Gọi `ca.exe` bọc `try/except`: ngoại lệ không được bỏ qua phần báo lỗi khi cài hay `SweepRegistry` khi gỡ.
  - `install-menu` lỗi thì **không** chạy `classic-menu on` (đổi menu mọi app mà không có menu của mình để hiện); `classic-menu off` vẫn chạy nếu bỏ tick.
  - Setup thoát với mã **10** (cài im lặng/winget nhận ra được) khi menu hoặc menu cổ điển lỗi; trang cuối nói rõ từng trường hợp thay vì hướng dẫn dùng.
  - File vẫn giữ, người dùng có thể gỡ bình thường.
- Trang "Right-click menu" (chỉ hiện trên Windows 11 build ≥ 22000) có một checkbox, mặc định không tick: tick thì chạy `ca.exe classic-menu on`, không tick thì `classic-menu off` (chỉ gỡ khóa do app tạo, nên bỏ tick khi cài đè sẽ hoàn tác). Lựa chọn được nhớ cho lần cài đè sau. Cài im lặng: `/CLASSICMENU=1|0`, không truyền thì giữ lựa chọn cũ. Nhãn checkbox nói rõ ảnh hưởng mọi app. Trang cuối nhắc cách mở menu; với menu cổ điển: có hiệu lực sau khi đăng xuất/đăng nhập lại hoặc khởi động lại máy ("khởi động lại Explorer" quá khó hiểu với người dùng phổ thông).
  - Không dùng `[Tasks]`: danh sách task (`TNewCheckListBox`) của Inno 6.7.3 vẽ checkbox bị cắt viền trái ở 200% DPI (người dùng phát hiện 2026-10-19); checkbox thường (`TNewCheckBox`) do Windows vẽ thì đúng.
  - Style: `modern`. Từng nghi `modern windows11` bị Smart App Control chặn (2 bản liền bị chặn); thử lại 6 bản có style đó và 3 bản không đều chạy được, nên việc chặn là ngẫu nhiên theo hash, không do style. Giữ `modern` vì đã kiểm kỹ nhất, khác biệt hình thức nhỏ.
- Cài đè: `AppId` cố định, `CloseApplications=yes` (Restart Manager đóng `ca.exe` đang chạy). `install-menu` tự gỡ menu cũ trước khi ghi.
- `ca.exe` nào đang chạy cũng giữ mutex `ConvertAnything.App` tới khi tiến trình kết thúc (trường static, không `using`). `AppMutex` **không chờ**: setup/uninstall hỏi người dùng đợi chuyển đổi xong rồi bấm OK, hoặc Cancel; với `/SUPPRESSMSGBOXES` thì Cancel (chỉ `/SILENT` thì hộp thoại vẫn hiện). `[Messages]` thay câu mặc định ("hãy đóng ứng dụng") bằng "A ConvertAnything conversion is still running. Wait until it has finished (or click Cancel in its progress window)…". `CloseApplications` chỉ còn tác dụng với `ca.exe` không mở được mutex. Lý do (đã tái hiện): gỡ giữa lúc đang convert để lại 29 file trong thư mục cài trong khi mục gỡ cài đặt đã mất, người dùng hết cách gỡ.
- `ChangesAssociations=yes`: báo Explorer sau khi cài/gỡ, kể cả khi chỉ lưới an toàn Pascal dọn được.
- Kèm theo: `LICENSE.txt` của app, `THIRD-PARTY-NOTICES.txt` gom từ Notice.txt của Magick.NET và LICENSE/ThirdPartyNotices của .NET runtime (bắt buộc khi phân phối lại bản self-contained). Không kèm `*.pdb` (lộ đường dẫn build, tốn chỗ). Không bật `SetupLogging` (để lại log trong `%TEMP%` mỗi lần cài); cần log thì chạy với `/LOG`.
- Phiên bản: lấy từ `<Version>` trong `Directory.Build.props`, installer đọc lại từ file version của `ca.exe` nên chỉ có một nơi khai báo.

### Menu cổ điển (`ca.exe classic-menu on|off`)

Windows 11 hiện menu cổ điển khi có khóa `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32` với giá trị mặc định rỗng. Khóa này ảnh hưởng menu của mọi app, nên:

- `on`: nếu khóa `CLSID\{86ca1aa0-…}` **chưa có**, tạo khóa và đặt giá trị đánh dấu `ConvertAnything` trên khóa `{86ca1aa0-…}`. Nếu đã có (người dùng tự bật trước đó), không đụng tới và không đánh dấu.
- Nếu khóa đã có nhưng ở dạng không bật được menu (thiếu `InprocServer32` hoặc giá trị mặc định không rỗng): vẫn không đụng tới, nhưng `on` thoát mã 1 kèm lý do, để setup báo thay vì hứa menu sẽ đổi.
- `off`: chỉ xóa khi có giá trị đánh dấu. Khóa người dùng tự tạo được giữ nguyên.
- Không tự khởi động lại Explorer: giết Explorer làm mất cửa sổ thư mục đang mở của người dùng.

### Gỡ cài đặt

Lúc `usUninstall` (trước khi xóa file), theo thứ tự:
1. `ca.exe uninstall-menu`, `ca.exe classic-menu off`.
2. Lưới an toàn bằng Pascal, chạy **luôn** (vô hại nếu bước 1 đã dọn): xóa `SystemFileAssociations\*\shell\ConvertAnything`, mọi khóa `ConvertAnything.Menu*` dưới `HKCU\Software\Classes`, và khóa menu cổ điển nếu có đánh dấu. Cần vì bước 1 có thể không chạy được (Smart App Control chặn, file hỏng), và menu trỏ tới exe đã xóa là thứ tệ nhất để lại.
3. Inno xóa file đã cài; `[UninstallDelete]` xóa đúng các thư mục con app ghi trong `%LOCALAPPDATA%\ConvertAnything` (`logs`, `pipes`, `magick`), rồi thư mục gốc nếu rỗng, và `{app}` nếu rỗng. Không xóa đệ quy cả thư mục gốc: tên chung chung, có thể chứa file của thứ khác. Log chỉ là chẩn đoán của chính app nên không giữ lại.

### Ký mã và cảnh báo của Windows

File chưa ký tải từ mạng: SmartScreen hiện "Windows protected your PC" → `More info` → `Run anyway`. Smart App Control (enforce) có thể chặn hẳn và không có nút bỏ qua. README ghi rõ cả hai, cách kiểm SHA256 và provenance, và khuyên **không** tắt Smart App Control. Bản phát hành chỉ build bằng workflow release (quyền đọc khi build/test, attestation provenance, job riêng có quyền ghi chỉ đính kèm vào release nháp). Chi tiết, rủi ro SignPath từ chối và các DLL bên thứ ba chưa ký: [ADR-0005](docs/adr/0005-inno-setup-unsigned-until-signpath.md).

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
tests/ConvertAnything.Core.Tests/InstallerScriptTests.cs   tên khóa/mutex trong .iss khớp với C#
.github/workflows/ci.yml, release.yml
docs/adr/0005-inno-setup-unsigned-until-signpath.md
```

## Code Style

Theo [SPEC-core.md](SPEC-core.md#code-style). Script Inno: hằng số ở `#define` đầu file, Pascal ngắn, mỗi hàm một việc, chú thích nói *vì sao*. PowerShell: `Set-StrictMode -Version Latest`, `$ErrorActionPreference = 'Stop'`, kiểm `$LASTEXITCODE` sau mỗi lệnh ngoài.

## Testing Strategy

| Tầng | Kiểm cái gì |
|---|---|
| Unit (xUnit, `FakeRegistry`) | `ClassicMenu`: bật khi chưa có → có khóa + đánh dấu; bật khi đã có → không đổi gì; khóa có sẵn dạng khác → báo về, không đổi; tắt chỉ xóa khi có đánh dấu; bật/tắt lặp lại không lỗi. `CommandLine` nhận `classic-menu on|off`, từ chối thứ khác. `InstallerScriptTests`: tên khóa, đánh dấu, mutex, khóa gỡ cài đặt (khớp `AppId`) và thư mục con trong `.iss` khớp hằng số C# (`AppData.Subfolders`, `MenuInstaller.Associations`) |
| Tích hợp (tiến trình `ca.exe` thật) | `ca.exe` đang chạy giữ mutex; mutex bị chiếm tên không làm hỏng `ca.exe`; `install-menu` qua junction thoát mã 3 kèm lý do trên stderr; `check-location` thư mục `Users` ghi được hoặc không tồn tại → mã 3; lỗi registry trong `uninstall-menu` → mã 1 kèm lý do (skip khi máy đang có menu ConvertAnything) |
| Đầu-cuối (`test-installer.ps1`, máy thật) | Từ chối chạy nếu máy đang có bất cứ thứ gì của app, `ca.exe` đang chạy, hoặc đang chạy với quyền admin. Kịch bản 1: cài im lặng (`/VERYSILENT /SUPPRESSMSGBOXES /CLASSICMENU=1`) → log ghi `Administrative install mode: No` → có file, notices, không có `.pdb`, có mục gỡ trong HKCU Uninstall, có menu cho `.heic`/`.jpg`, có khóa menu cổ điển + đánh dấu → `ca.exe` đã cài chạy thật `image.to-jpg` trên fixture HEIC, ra JPEG 320×240 hợp lệ (chứng minh bản self-contained đủ native) → cài đè không tham số (giữ menu cổ điển) → cài đè `/CLASSICMENU=0` (gỡ khóa) → gỡ im lặng, đợi bản sao uninstaller trong `%TEMP%` thoát → không còn thư mục app, `%LOCALAPPDATA%\ConvertAnything`, khóa menu, khóa `SystemFileAssociations\<ext>` rỗng do app tạo, khóa menu cổ điển, mục gỡ. Kịch bản 2: cài, ghi đè `ca.exe` bằng rác (có nhưng không chạy được), gỡ → lưới an toàn Pascal vẫn dọn sạch. Kịch bản 3: khóa menu cổ điển có sẵn (không đánh dấu) → cài `/CLASSICMENU=1` rồi gỡ → khóa vẫn còn. Kịch bản 4: thư mục cài có quyền ghi cho `Users`, `/CLASSICMENU=1` → setup thoát mã 7 trước khi chép, log có lý do của `check-location` và link `#setup-refused-the-install-folder`, thư mục vẫn rỗng, không có mục gỡ, menu, menu cổ điển. (Đường mã 10 do `install-menu` từ chối giờ chỉ là phòng thủ nhiều lớp, không còn kịch bản đầu-cuối.) Kịch bản 5: `/DIR` khác → thoát 7, log có "installs only into", không cài gì. Kịch bản 6: gỡ im lặng trong lúc `ca.exe` đang chạy (chờ tới khi nó giữ mutex; 20 file ~20 s; assert nó vẫn chạy khi uninstaller xong) → bị hủy, mọi thứ còn nguyên; gỡ lại sau khi `ca.exe` xong → sạch. Kịch bản 9 (smoke test, không tái hiện được lỗi `C:`; bản sửa được `InstallerScriptTests` kiểm trên chính script): chạy setup với thư mục làm việc là junction → vẫn cài được. Kịch bản 7: `{app}` là junction → thoát 7, không chép gì qua junction. Kịch bản 8: `{app}` có file lạ, chưa có mục gỡ → thoát 7, file lạ còn nguyên, không cài gì. Trước cả: file setup ≤ 60 MB. Khóa menu kiểm theo mọi khóa `ConvertAnything*` dưới `HKCU\Software\Classes`; `finally` dừng `ca.exe` trong `{app}` trước khi gỡ, xóa junction chứ không xóa đích |

Thứ tự TDD: viết `ClassicMenuTests` và `test-installer.ps1` trước, chạy thấy đỏ, rồi mới viết code và `.iss`.

## Boundaries

- **Always:** cài per-user, không admin; chỉ ghi registry dưới HKCU; gỡ sạch những gì app tạo ra; ghi lý do mọi quyết định vào spec/ADR.
- **Ask first:** không có (người dùng đã giao quyền tự quyết). Riêng việc đổi repo sang public: người dùng đã đồng ý (2026-10-19) với điều kiện xong quy trình review.
- **Never:** dùng công cụ/dịch vụ trả phí; ghi HKLM; tự khởi động lại Explorer; xóa khóa menu cổ điển do người dùng tự tạo; commit binary (bản publish, setup.exe).

## Success Criteria

1. `build-installer.ps1` chạy từ máy sạch (có SDK + Inno Setup) ra một file setup ≤ 60 MB.
2. Cài không hiện UAC, xong < 30 s; `ca.exe` đã cài chạy `image.to-jpg` thành công trên HEIC.
3. Sau khi gỡ: 0 file trong thư mục cài và `%LOCALAPPDATA%\ConvertAnything`, 0 khóa `ConvertAnything*` dưới `HKCU\Software\Classes` và 0 khóa `SystemFileAssociations\<ext>` do app tạo, 0 mục gỡ cài đặt.
4. Lưới an toàn: gỡ khi `ca.exe` không chạy được vẫn đạt (3).
5. Menu cổ điển: bật/tắt chỉ đụng tới khóa do app tạo (unit test + kịch bản 3).
6. Cài đè: menu vẫn đầy đủ, không trùng lặp. (v0.1 chỉ kiểm cài đè cùng bản; từ v0.2 giữ setup 0.1.0 làm fixture để kiểm nâng cấp thật.)
7. `dotnet test` xanh, `dotnet format --verify-no-changes` exit 0, ba vòng review không còn Critical/High/Required.
8. Đăng ký menu thất bại → setup thoát mã 10 và nói rõ lý do; `/DIR` khác, thư mục qua link, thư mục có file lạ → từ chối (mã 7) trước khi chép.
9. Gỡ khi `ca.exe` đang chạy không bao giờ để lại cài đặt dở dang.

## Open Questions

- SignPath Foundation cần repo public + build trên CI; sẽ nộp đơn sau khi public (người dùng phải tự điền đơn bằng tài khoản của họ).
- ~~Menu cổ điển mới chỉ kiểm ở mức registry~~ Đã kiểm tay (2026-10-19, Windows 11 Pro 26H2 build 26300.9711): sau khi cài có tick và đăng xuất/đăng nhập, menu kiểu cũ hiện ConvertAnything ngay ngoài, đủ Convert/Compress/Remove metadata (ghi trong ADR-0005). Khóa vẫn không chính thức: bản Windows sau có thể bỏ nó.
- Bộ giải mã HEVC (libheif/libde265 trong Magick.Native) có thể vướng bằng sáng chế ở vài nước; "miễn phí" chỉ nói về giấy phép phần mềm. Ghi trong ADR-0004.

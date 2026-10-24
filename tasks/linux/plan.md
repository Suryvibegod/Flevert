# Plan: linux

Spec: [docs/specs/linux.md](../../docs/specs/linux.md). Task và trạng thái: [todo.md](todo.md). Nhánh `feat/linux`.

## Tổng quan

Đưa core, image, pdf lên `net10.0` khi build ngoài Windows. Mỗi chỗ gọi API Windows được thay bằng bản Linux tương ứng, qua một điểm rẽ nhánh nhỏ tại chỗ gọi. Thêm tiến trình `flevert` cho Linux (`src/Flevert.Linux`) dùng lại `Runner`, `BatchLeader.Alone`, `BatchSummary` và catalog thao tác; giao diện qua zenity và gdbus; menu qua extension Nautilus. Bản Windows không đổi hành vi.

## Quyết định kiến trúc

- **TFM theo hệ điều hành** (`Directory.Build.props`, đã làm): `net10.0-windows` trên Windows, `net10.0` nơi khác. CI Windows build đúng như trước.
- **`Flevert.Cli` vẫn build trên Linux** như một app Windows (`EnableWindowsTargeting=true`, TFM `net10.0-windows` cố định trong csproj). Một project `net10.0-windows` tham chiếu được project `net10.0`, nên `dotnet build Flevert.sln` chạy được ở cả hai nơi, và lỗi biên dịch phía Windows lộ ra ngay trên Linux.
- **Analyzer CA1416 giữ nguyên:** kiểu chỉ dành cho Windows được gắn `[SupportedOSPlatform("windows")]`; chỗ gọi chung rẽ nhánh bằng `OperatingSystem.IsWindows()`. Không thêm `NoWarn`.
- **Không gom lô liên tiến trình trên Linux:** `Flevert.Linux` gọi `BatchLeader.Alone(files)` (đổi từ `internal` sang `public`). `Batch` (pipe) chỉ dành cho Windows.
- **P/Invoke libc ở một chỗ:** `src/Flevert.Core/Unix/UnixFile.cs` (`open`, `fstat`, `readlink`, `mkdirat`, `renameat2`, `unlinkat`, `statvfs`, `fchmod`), dùng lại ở Pdf và Image qua `InternalsVisibleTo` sẵn có hoặc `public`.
- **Đường dẫn qpdf và worker theo nền tảng:** `Qpdf.FileName` và `PdfWorker.FileName` thành `qpdf`/`flevert` trên Linux. qpdf bản Linux giữ cấu trúc `bin/` + `lib/` của zip (RPATH `$ORIGIN/../lib`) dưới `deps/qpdf/` và thư mục cài.
- **Test:** test project tham chiếu `Flevert.Linux` (Linux) hoặc `Flevert.Cli` (Windows) để định thứ tự build. File test chỉ dành cho Windows bị bỏ khỏi build trên Linux; test lẻ dùng `Skip.IfNot`.

## Đồ thị phụ thuộc

```
T1 build được trên Linux ──┬── T2 deps Linux ──┬── T3 test chạy trên Linux (baseline đỏ/xanh)
                           │                   │
                           │                   ├── T4 ghim file + quy tắc đường dẫn ─┐
                           │                   ├── T5 thứ tự tự nhiên                 │
                           │                   ├── T6 thư mục riêng, native nhỏ (Pdf, Image)
                           │                   ├── T7 thư mục kết quả 1→N             │
                           │                   └── T8 ExternalTool ──────────────────┤
                           │                                                          │
                           └────────────────────────────── T9 flevert headless (run, pdf-worker, list-ops)
                                                                  ├── T10 tiến trình + thông báo
                                                                  ├── T11 hộp thoại mật khẩu, watermark
                                                                  └── T12 menu Nautilus (install-menu, extension)
                                                                          └── T13 đóng gói ── T14 tài liệu ── T15 CI (hỏi trước)
```

T8 (ExternalTool) là rủi ro lớn nhất cho PDF: mọi thao tác qpdf và worker pdfium đều đi qua nó, nên làm sớm trong giai đoạn 2, ngay sau T4.

## Giai đoạn

1. **Nền (T1–T3):** solution build trên Linux, deps tải được, test chạy được. Bảng test đỏ ở T3 là danh sách việc cho giai đoạn 2.
2. **Phần nền tảng của core/pdf/image (T4–T8):** TDD, test âm cho từng điều kiện bảo mật.
3. **Ứng dụng (T9–T12):** lát dọc từ dòng lệnh tới menu Nautilus.
4. **Đóng gói và tài liệu (T13–T15).**
5. **Kiểm một lần trước PR:** chạy thật trong Nautilus, ba review bối cảnh trống (code-reviewer, security-auditor, doubt), reconcile, sửa, soát lại.

## Rủi ro

| Rủi ro | Mức | Giảm thiểu |
|---|---|---|
| qpdf bản Linux cần thư viện hệ thống không có sẵn (libjpeg, gnutls…) | Cao | T2 chạy `ldd` trên binary đã tải; nếu thiếu thì dùng AppImage giải nén hoặc qpdf của distro, ghi vào ADR |
| Magick.NET native trên Linux cần `libgomp`/`fontconfig` | TB | T3 chạy test ảnh sớm; ghi gói cần có vào install.sh |
| `prlimit --as` làm hỏng tiến trình .NET (worker pdfium là chính `flevert`: CLR đặt trước nhiều không gian địa chỉ ảo) | Cao | T8 đo trước; nếu hỏng thì worker dùng `DOTNET_GCHeapHardLimit` thay cho giới hạn AS, qpdf vẫn dùng `prlimit` |
| Nhiều test giả định đường dẫn Windows (`C:\`, `\\?\`) dù không gọi API Windows | TB | T3 phân loại từng test: sửa dữ liệu test thành theo nền tảng khi ý nghĩa test vẫn giữ, bỏ qua khi test chỉ có nghĩa trên Windows |
| Test nhạy thời gian chạy chậm trên máy này | Thấp | Chạy từng project một, như CLAUDE.md |
| zenity 3.x (Nautilus 42) và 4.x khác nhau ở vài tùy chọn | Thấp | Chỉ dùng tùy chọn có ở cả hai: `--progress --auto-close`, `--password`, `--entry --hide-text`, `--title`, `--text` |
| API 4.0 của nautilus-python chưa thử tay | TB | Test Python với module giả cho cả 3.0 và 4.0; ghi vào Open Questions của PR |

## Kiểm ở các mốc

- Sau T3: `dotnet build Flevert.sln` 0 cảnh báo; bảng test đỏ/xanh/bỏ qua ghi trong todo.
- Sau T8: Core, Image, Pdf xanh trên Linux, trừ test cần `flevert` thật.
- Sau T12: ba project xanh; cài vào Nautilus từ bản build, bấm thử mỗi nhóm thao tác một lần.
- Cuối: đủ 10 tiêu chí của spec.

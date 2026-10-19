# Todo: tăng tốc convert

Plan: [plan.md](plan.md).

- [x] Task 1: Runner song song (`880ceca`; đối chứng: ép tuần tự thì 4/6 test đỏ)
- [x] Task 2: chủ lô đếm file đang chạy (`321291b`; test đỏ với `IsIdle` cũ)
- [x] Task 3: ngân sách bộ nhớ ảnh (`ff2d400`; đối chứng FIFO và cấp sau khi hủy đều đỏ)
- [x] Task 4: nối vào flevert.exe, đo lại (test tích hợp tiến trình thật đỏ → xanh; số đo dưới)
- [x] Task 5: review 3 góc (vòng 1, bảng dưới), docs, soát phần sửa (vòng 2: không còn Critical/High/Required; Medium/Low đã sửa, bảng dưới)
- [ ] Build lại bản nháp v0.1.0 (HANDOFF mục 3.5)

## Review vòng 2 (một reviewer bối cảnh trống, 3 góc, trên `7c354dd..3994e7d`): không còn Critical/High/Required

| Finding | Mức | Xử lý |
|---|---|---|
| M1: ping chạy ngoài ngân sách mà có thể đọc cả file (WebP đệm 300 MB → +300 MB khi ping), tới 8 cái cùng lúc | Medium | Giữ suất kích thước file × 2 trong lúc ping, trả trước khi xin suất đầy đủ (tránh kẹt FIFO). Chưa có test riêng: hành vi chỉ thấy qua bộ nhớ đỉnh |
| M2, L4: BelowNormal cho mọi lô; ảnh lớn chạy một mình có thể bị bỏ đói tới giới hạn 120 s; Explorer mở log thừa hưởng | Medium/Low | Ưu tiên theo số file đang chạy: BelowNormal khi ≥ 2, Normal khi ≤ 1. Test tiến trình 2 ca (đối chứng: hạ từ 1 file → ca 1 file đỏ) |
| L1: câu chữ về số kênh sai (ping báo 0 kênh cho hầu hết RGB) | Low | Sửa chú thích, spec "Đã biết" |
| L2: test ưu tiên lấy mẫu một lần sau 1 s | Low | Thăm dò 50 ms tới 2,5 s |
| L3: test đến muộn biên hẹp | Low | `dev.slow-copy` 6 s |
| L5: ≥ 1000 khung/trang → chạy một mình | Low | Ghi spec "Đã biết" |
| Nit: tóm tắt lớp `MemoryBudget`; AVIF không đọc cả file khi ping; lỗi kép trong `finally`; chưa test thao tác `MaxParallel = 1` qua flevert.exe | Nit | Sửa tóm tắt; còn lại ghi nhận (test sẽ có khi v0.2 thêm ffmpeg) |

## Review vòng 1 (code-reviewer, security-auditor, doubt-driven): reconcile

| Finding | Mức | Phân loại | Xử lý |
|---|---|---|---|
| DD H1, CR M2, SA M1: GIF/WebP động tối ưu bị ước lượng quá thấp (ping thấy từng khung nhỏ, `Coalesce` bung ra cả canvas); số kênh > 4 | High, Required | Hợp lệ, đã tái hiện (GIF 200 khung: 2,4 KB thay vì ~800 MB) | Ping vào `MagickImageCollection`, mỗi khung tính theo canvas × max(4, kênh + 1) (ping không báo alpha: đã đo TIFF CMYK + alpha 4 → 5). Test GIF, WebP động, CMYKA (đỏ → xanh) |
| SA M2: bộ nhớ theo kích thước file (WebP/HEIC đọc cả file, profile) không được tính | Medium | Hợp lệ | Suất ≥ kích thước file × 2; test PNG 16×16 có comment 4 MB |
| CR M1: `Lazy` lưu ngoại lệ khởi tạo → mọi file sau đó hỏng | Medium | Hợp lệ | Ngân sách dựng từ hằng `MagickSetup.MemoryLimit`, không khởi tạo ImageMagick |
| CR M3, DD M5, SA L: giới hạn 120 s theo đồng hồ từng ảnh; ảnh lớn chạy cạnh ảnh khác có thể vượt | Medium | Hợp lệ | Suất > nửa ngân sách → chạy một mình; test |
| DD M4: ngân sách cố định 2 GB, không theo RAM máy | Medium | Hợp lệ | `min(2 GB, 1/4 bộ nhớ máy)` |
| DD M3: một mức song song chung sẽ áp cả cho ffmpeg (v0.2) | Medium, Required trước v0.2 | Hợp lệ | `IOperation.MaxParallel` mặc định 1; ảnh và dev chọn 8; test |
| DD M2: không hạ ưu tiên, máy có thể giật | Medium | Hợp lệ | BelowNormal khi chạy song song; test tiến trình thật (đỏ khi gỡ); đo: thời gian lô không đổi trong nhiễu |
| CR M4: không test nào bắt được việc `flevert.exe` thôi gọi `ReportRunning` | Medium | Hợp lệ | Test tiến trình: file chạy 4 s, cửa sổ idle 200 ms, tiến trình đến muộn ở 2,2 s phải vào cùng lô (đỏ khi gỡ lời gọi). `FLEVERT_SLOW_COPY_MS` cho `dev.slow-copy` |
| DD M1: ảnh đo tổng hợp không đại diện (HEIC một tile giải mã 1,6 s; ảnh iPhone thật ~0,2 s nhờ libheif đa luồng) | Medium | Hợp lệ | Ghi số của reviewer với ảnh thật (16 ảnh: 8,7 s → 2,1 s, ×4,1); sửa câu chữ spec. Không dùng ảnh cá nhân của người dùng để đo khi chưa hỏi |
| CR L1, DD L1: lỗi của một tác vụ bị nuốt; nguồn lỗi thì thoát khi file còn đang ghi | Low | Hợp lệ | `await done`; `WhenAll` trong `finally` |
| CR L2: file bị hủy vẫn có thể ra output | Low | Hợp lệ (đã có từ trước) | Ghi trong spec và chú thích `BatchOutcome` |
| CR L3: dễ quên `running` khi dùng leader | Low | Hợp lệ | Ghi rõ trong chú thích tham số |
| CR L4: thiếu test (trả suất khi lỗi, ngân sách 0) | Low | Hợp lệ | Thêm test; chặn ngân sách 0 |
| CR L5: ADR-0002, chú thích `BatchOutcome` còn mô tả tuần tự | Low, Required (DoD) | Hợp lệ | Cập nhật ADR-0002, spec |
| DD L2: test "mặc định tuần tự" không thể đỏ; test leader bận dựa vào cửa sổ 3 s | Low | Hợp lệ | Thao tác giữ 150 ms (đối chứng: mặc định 2 → đỏ); test leader bận viết lại (xem CR M4) |
| DD L4: TIFF nhiều trang tính mọi trang | Low | Trade-off | Chỉ chậm hơn; ghi spec |
| DD L5: dòng tiến độ nêu file bắt đầu sau cùng; tổng giảm khi Cancel | Low | Trade-off | Mỹ quan |
| DD L6: với một ảnh thật, sàn 1,5 s của lô lớn gấp 3–4 lần công việc | Low | Ghi nhận | Ứng viên tiếp theo (cần đổi ADR-0002, có rủi ro tách lô); hỏi người dùng |
| Nit (CR): tổng có thể giảm 1 thoáng; lock trên `results`; tên tuple; tràn số; vị trí field; "thứ tự đến" | Nit | Hợp lệ | Sửa chú thích, `Lock` riêng, `.Granted`, `need <= capacity - used`, chú thích FIFO. `_lastStarted` giữ cạnh `Report` (dùng duy nhất ở đó) |
| SA Info: ping cùng đường đọc an toàn; leader không đóng sớm; đặt tên output an toàn | Info | Ghi nhận | — |

## Đo lại (cùng `scripts/measure-convert.ps1`, cùng ảnh, cùng máy, trung vị 5 lần)

| Kịch bản | Trước (ms, min–max) | Sau (ms, min–max) |
|---|---|---|
| HEIC→JPG ×10 | 18857 (18460–18892) | **4710 (4680–5097)**, ×4,0 |
| HEIC→JPG ×1 | 2140 (2121–2246) | 2132 (2126–2393) |
| PNG→JPG ×1 | 1607 | 1605 |
| JPG→WebP ×1 | 2127 | 2144 |
| JPG Compress Balanced | 1628 | 1613 |
| JPG Under 1 MB | 1734 | 1732 |
| JPG Remove metadata | 1628 | 1614 |

Lô 10 ảnh: 8 file cùng lúc (`Clamp(16 − 1, 1, 8)`), tức 2 đợt. Mỗi file chậm hơn khi chạy cùng nhau (tổng thời gian thao tác 18,6 → 23,1 s: băng thông bộ nhớ, xung nhịp) nhưng tổng thời gian giảm 4 lần. Lô 1 file không đổi (trong nhiễu): ping header không tốn đáng kể.

## Sổ thử nghiệm (giữ cả ý tưởng bị hoàn tác)

| Ý tưởng | Nền → Kết quả | Quyết định | Vì sao |
|---|---|---|---|
| Chạy song song các file trong một lô | lô 10 HEIC 18,9 s → 4,7 s | **giữ** | Vượt xa dao động (±0,4 s); test xanh |
| (đo lại khi máy bận ~52% CPU) tuần tự `04f5890` vs bản sau review | 23,9 s → 6,85 s (×3,5); commit `0bc2530` lúc đó 6,4 s | giữ | Cùng điều kiện; bản sau review không chậm hơn bản đầu |
| Ảnh iPhone thật (reviewer đo, 16 ảnh) | 8,7 s → 2,1 s (×4,1) | — | libheif tự dùng ~2,8 nhân/ảnh; máy 4 nhân dự kiến ~×2 |
| Hạ ưu tiên BelowNormal khi song song | 6381 → 6414 ms (trong nhiễu) | **giữ** | Không nhằm tăng tốc mà để máy mượt; không tốn thời gian lô |
| Giảm `MinimumLifetime` 1,5 s cho lô 1 file nhanh | đo qua menu Explorer thật (MenuProbe, bản publish ấm, 5 lần mỗi cỡ): 1/2/10/50/100 file đều 1 lô; Explorer khởi chạy tiến trình cách nhau ~27 ms (2 file: 7–10 ms; 100 file: 2,7 s); file xong → tiến trình thoát ~1,5 s với 1–2 file | **giữ 1,5 s** (người dùng chọn "đo rồi quyết", 2026-10-19) | File kết quả xuất hiện ngay khi convert xong; mức sàn chỉ làm thông báo và tiến trình thoát muộn ~1 s. Lúc ấm cửa sổ idle 400 ms đã đủ, nhưng chưa đo khởi động lạnh (Defender/Smart App Control quét exe lần đầu), là trường hợp mức sàn này bảo vệ. Không đáng đổi ADR-0002 để thông báo sớm ~1 s. Đo lại nếu có cách tái hiện khởi động lạnh |
| Tăng tốc một file (đa luồng trong ImageMagick) | — | không làm được | Magick.NET bản này không có OpenMP; thời gian nằm trong bộ giải mã libheif/libjpeg |

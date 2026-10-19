# Todo: tăng tốc convert

Plan: [plan.md](plan.md).

- [x] Task 1: Runner song song (`880ceca`; đối chứng: ép tuần tự thì 4/6 test đỏ)
- [x] Task 2: chủ lô đếm file đang chạy (`321291b`; test đỏ với `IsIdle` cũ)
- [x] Task 3: ngân sách bộ nhớ ảnh (`ff2d400`; đối chứng FIFO và cấp sau khi hủy đều đỏ)
- [x] Task 4: nối vào ca.exe, đo lại (test tích hợp tiến trình thật đỏ → xanh; số đo dưới)
- [ ] Task 5: review 3 góc, docs, build lại bản nháp

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
| Giảm `MinimumLifetime` 1,5 s cho lô 1 file nhanh | (không thử) | không làm | Có chủ đích trong ADR-0002: tránh tách lô khi tiến trình khởi động chậm |
| Tăng tốc một file (đa luồng trong ImageMagick) | — | không làm được | Magick.NET bản này không có OpenMP; thời gian nằm trong bộ giải mã libheif/libjpeg |

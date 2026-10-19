# Plan: tăng tốc convert (xử lý song song trong một lô)

Spec: [docs/specs/core.md](../../docs/specs/core.md#xử-lý-song-song-trong-một-lô), [docs/specs/image.md](../../docs/specs/image.md#ngân-sách-bộ-nhớ-khi-chạy-song-song). Quy trình: skill `performance-optimization` (đo → tìm nút thắt → sửa một thứ → đo lại, giữ hoặc hoàn tác → chặn hồi quy).

## Đo nền (2026-10-19, bản publish Release, máy 16 nhân, ảnh 12 MP, trung vị 5 lần)

| Kịch bản | Tổng (ms) | Thao tác (ms) |
|---|---|---|
| HEIC→JPG ×1 | 2140 | 1772 |
| PNG→JPG ×1 | 1607 | 962 |
| JPG→WebP ×1 | 2127 | 1668 |
| JPG Compress Balanced | 1628 | 423 |
| JPG Under 1 MB | 1734 | 1529 |
| JPG Remove metadata | 1628 | 535 |
| HEIC→JPG ×10 | 18857 | 18588 |

Nút thắt: lô chạy lần lượt trên một nhân (Magick.NET không OpenMP: decode HEIC 12 MP 1,6 s, encode JPG 0,4–0,7 s, WebP 1,7–1,9 s, đều đơn luồng). Sàn ~1,6 s của lô một file là `MinimumLifetime` 1,5 s (ADR-0002, có chủ đích), không đụng.

Script đo: `scripts/measure-convert.ps1` (thêm ở Task 4).

## Tasks

1. **Runner song song** (Core). `maxParallel`; test trước: N file chậm xong trong ~1/N thời gian; trùng vẫn bỏ; lỗi một file không chặn; Cancel không lấy file mới và đếm file bị hủy; `maxParallel = 1` giữ nguyên mọi test cũ. Nghiệm thu: `RunnerTests` mới đỏ → xanh, test cũ xanh.
2. **Chủ lô biết số file đang chạy** (Core, `BatchLeader`). Test trước: lô không đóng khi vẫn còn file đang chạy dù hàng đợi rỗng quá cửa sổ idle. Nghiệm thu: test đỏ với `_busy` cũ, xanh sau.
3. **Ngân sách bộ nhớ** (Image). `MemoryBudget` (FIFO, có trọng số, hủy được) + bọc mọi thao tác ảnh; ping kích thước. Test trước: cấp FIFO, ảnh quá lớn chạy một mình, hủy khi chờ không giữ suất, tổng suất không vượt ngân sách.
4. **Nối vào `ca.exe` + đo lại.** `Program` truyền `maxParallel`; `scripts/measure-convert.ps1`. Nghiệm thu: lô 10 HEIC nhanh hơn rõ rệt so với nền (vượt dao động); lô 1 file không chậm hơn; test tích hợp tiến trình thật xanh.
5. **Review** 3 góc → reconcile → docs (ADR-0002, README nếu cần) → build lại bản nháp v0.1.0.

Mỗi task một commit; số đo trước/sau ghi trong message và bảng ở `todo.md`.

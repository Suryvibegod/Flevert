# Implementation Plan: image

Spec: [SPEC-image.md](../../SPEC-image.md). Danh sách việc: [todo.md](todo.md).

## Overview

Thêm project `ConvertAnything.Image` (Magick.NET) với 14 thao tác: convert sang 7 định dạng, nén 3 mức, nén dưới 500 KB / 1 MB / 5 MB, xóa metadata. Cắm vào catalog của `ca.exe`; menu chỉ hiện thao tác hợp với đuôi file.

## Architecture Decisions

- **Lát dọc đầu tiên là JPG → PNG từ đầu đến cuối** (Magick → `OutputPath` → catalog → menu → bấm thật bằng MenuProbe), rồi mới mở rộng ma trận định dạng.
- **Giới hạn tài nguyên + chặn coder nguy hiểm làm đầu tiên** (rủi ro bảo mật lớn nhất: ảnh độc hại, coder MSL/MVG/TEXT đọc file khác).
- **Một lớp `ImageOperation` tham số hóa** (đích, mức nén, hậu tố tên) thay vì 14 lớp gần giống nhau.
- **Fixture HEIC tự sinh** bằng `pillow-heif` từ ảnh tổng hợp; repo sở hữu file, không vướng bản quyền.

## Task List

### Phase 1: Foundation
- [x] Task 1: Project + Magick.NET + `MagickSetup` (giới hạn tài nguyên, chính sách coder)
- [x] Task 2: Lát dọc JPG → PNG (auto-orient, catalog, menu, MenuProbe)

### Checkpoint: Foundation
- [x] Bấm thật `Convert > PNG` trên JPG ra PNG đúng chiều

### Phase 2: Tính năng
- [ ] Task 3: Ma trận convert (7 đích; alpha → nền trắng; giữ animation; HEIC nguồn)
- [ ] Task 4: Compress 3 mức
- [ ] Task 5: Compress dưới N
- [ ] Task 6: Remove metadata

### Checkpoint: Tính năng
- [ ] Đủ 9 success criteria trong SPEC-image

### Phase 3: Hoàn thiện
- [ ] Task 7: Review 3 góc (code, bảo mật, doubt), sửa, cập nhật README/ADR

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Ảnh độc hại làm tràn RAM / CPU | High | `ResourceLimits` trước mọi thao tác; test ảnh "bomb" |
| Coder ImageMagick đọc file khác (MSL, MVG, TEXT, URL) | High | Chỉ cho phép danh sách coder định dạng; test file `.jpg` thực chất là MVG |
| Magick.NET native nặng (~25 MB) | Low | Chấp nhận; installer nén |
| Nén "dưới N" không đạt với ảnh cực lớn | Med | Thu nhỏ dần 15%; nếu vẫn không đạt thì báo lỗi rõ ràng |

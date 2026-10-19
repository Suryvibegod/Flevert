# Todo: installer

Plan: [plan.md](plan.md).

## Task 1: Version + ClassicMenu
- [ ] `<Version>0.1.0</Version>` trong `Directory.Build.props`
- [ ] `IRegistry.GetValue`; `ClassicMenuTests` (đỏ): on khi chưa có / on khi đã có / off có đánh dấu / off không đánh dấu / lặp lại
- [ ] `ClassicMenu` (xanh); `CommandLine` nhận `classic-menu on|off` (test trước); nối vào `Program`
- Acceptance: unit test xanh; `ca classic-menu on` rồi `off` trên máy thật để lại registry như cũ

## Task 2: Installer tối thiểu
- [ ] `scripts/test-installer.ps1` (đỏ: chưa có installer)
- [ ] `scripts/build-installer.ps1`, `installer/ConvertAnything.iss`
- [ ] Kịch bản 1 (cài, chạy HEIC→JPG, cài đè, gỡ sạch), 2 (lưới an toàn), 3 (giữ khóa menu cổ điển của người dùng)
- Acceptance: test-installer xanh; success criteria 1–6

## Task 3: Notices + icon
- [ ] `THIRD-PARTY-NOTICES.txt` gom lúc build; LICENSE trong bộ cài
- [ ] Icon app (`ApplicationIcon`, dùng cho menu, installer, mục gỡ)

## Checkpoint
- [ ] Review 3 góc → reconcile → lặp
- [ ] ADR-0005, README, HANDOFF; commit + push

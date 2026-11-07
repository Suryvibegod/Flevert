# Test nhạy thời gian: plan và todo

Mục tiêu (HANDOFF việc 1): các test dưới đây không còn đỏ khi máy/CI tải nặng, mà vẫn bắt được đúng lỗi chúng canh. Không nới lỏng test bằng cách bỏ kiểm tra, tăng timeout vô hạn hay `Skip`; sửa nguyên nhân (đua trong test, hoặc lỗi thật trong mã).

Log CI gốc của từng lỗi được tải về lúc lập plan (2026-11-01); số run ghi bên dưới để tải lại bằng `gh run view <run> --attempt <n> --log-failed`.

Máy dev không có WSL: test chỉ chạy Linux được kiểm bằng CI trên PR. Agent chỉ chạy test liên quan trong worktree của mình; bộ đầy đủ năm project chạy lần lượt một lần trước khi gộp, khi không agent nào build/test.

## Việc

| # | Test | Nền | Lỗi CI | Agent |
|---|---|---|---|---|
| T1 | `PdfSplitTests.A_page_held_open_in_qpdfs_folder_for_a_moment_is_waited_for` | Linux | release 40136568955 a1: `DirectoryNotFoundException '/proc/<pid>/fd/<n>'` trong `ReleaseAfterTheLastCopyAsync`: thư mục tạm chỉ là fd, đóng xong là mất | split |
| T2 | `PdfMergeTests.Cancelling_stops_qpdf_and_leaves_no_partial_file` (và mẫu giống ở Rotate/Compress/StripMetadata) | Linux | CI 40142926381 a1, 40121733246: "No exception was thrown", qpdf xong trước khi hủy | cancel-qpdf |
| T3 | `PdfWorkerTests.A_page_that_never_finishes_is_stopped_and_reported_within_the_stall_window` | Linux | CI 40130224702 a1 | worker |
| T4 | `PdfHardeningTests` bomb-image / `pdf.compress` (lượt qpdf đầy đủ quá giờ → "ok" thay "skipped") | Linux | CI 40130224702 a1 | hardening |
| T5 | `ProcessBatchIntegrationTests.A_hundred_processes_of_a_joining_operation_give_one_file` | Windows | CI 40130230119 a1: tiến trình [94] không đạt | batch |
| T6 | `PdfMakeSearchableTests.Cancel_while_tesseract_reads…` còn một tesseract; `TesseractTests.Cancelling_long_run…`; `PinnedInputTests` make-searchable/image.to-text (Linux) | Windows/Linux | CI 40130230119 a2, 40122855731 | ocr-cancel |

Tiêu chí xong mỗi việc: nguyên nhân ghi rõ (đua trong test hay lỗi mã); test vẫn đỏ trên lỗi nó canh (kiểm RED khi rẻ); chạy riêng test đó nhiều lần (≥ 20 lần khi nhanh) và dưới tải CPU đạt; build và `dotnet format --verify-no-changes` đạt.

Tiêu chí xong cả đợt: năm project xanh chạy lần lượt; CI trên PR đạt ba lượt chạy cùng lúc trên cùng commit (như kiểm release).

## Ghi chú kết quả (2026-11-01)

Sáu agent song song, mỗi agent một worktree; các commit gộp vào nhánh `fix/flaky-tests`.

- **T1 — lỗi test.** Trên Linux, Linux cho xóa file đang mở, nên split xong ngay sau lần chép cuối và đóng fd; helper vẫn đọc `/proc/<pid>/fd/<n>` (có thể là fd của file khác). Helper giờ theo dõi thư mục `.flevert-*.partial` bằng tên thật và nhả trang khi thư mục đã mất. Windows không có đua này.
- **T2 — lỗi test.** Bốn test hủy qpdf (Merge, Rotate, Compress, StripMetadata) hủy bằng timer 500 ms: Linux merge xong trước; trên Windows timer luôn rơi vào lượt đếm trang, nên bước ghi chưa bao giờ bị hủy; Compress/StripMetadata dùng file quá trần trang. Thêm hook nội bộ `Qpdf.AfterStart`, helper `CancelWhenQpdfRuns` hủy đúng khi qpdf của bước ghi khởi động, input chạy 15–22 s nếu không hủy. RED: qpdf bỏ qua token → 4 test đỏ. 10/10 xanh.
- **T3 — lỗi test.** Assert cuối `Assert.Empty(Workers())` đếm worker của `PinnedInputTests` (chỉ chạy trên Linux, thiếu collection `WorkerProcesses`). Thêm collection; cũng che `PdfHardeningTests.cs:180`. RED bằng cách bỏ collection của PdfToImagesTests.
- **T4 — sản phẩm có ngân sách thời gian chủ ý.** Hai ca bomb-image/bomb-content × pdf.compress: lượt qpdf đầy đủ có ngân sách 10 s + 1 s/MB (chống bom giải nén); runner Linux nhanh làm xong trong ngân sách → "ok" (nhỏ hơn 1,6 %), máy chậm hết ngân sách → lượt dự phòng → "skipped". Test chấp nhận `OkOrSkipped` cho riêng hai ca này, và chỉ nhận "skipped" khi đã hết ngân sách. Giữ nguyên giới hạn bắt treo. Ghi nhận nhỏ chưa sửa: thông báo "already compact" khi chỉ lượt dự phòng không nhỏ hơn.
- **T5 — lỗi sản phẩm.** Máy tải nặng làm tiến trình tạo các flevert cách nhau 2,7 s; leader coi phần sau là selection khác (`ChainGap` 2 s), ghép 51/100 file và báo 49 file "left out". Sửa `JoinGate`: quãng ≤ 10 s (`StartingChainGap`) vẫn cùng selection nếu có tiến trình trước quãng chưa giao file; leader lấy danh sách tiến trình ngay khi bắt đầu (`Survey()`), không để tiến trình chết sớm bị bỏ qua. ADR-0007 cập nhật. 30/30 trên 4 nhân. **Còn mở:** trên một bản build trung gian, 3/60 lượt leader nhận 19 file rồi chờ 30 s, 80 file "did not reach in time" (nghi follower đổi batch key sau một "refused" giả trong `LeadOrJoinOneOutputAsync`); 40 lượt có trace không tái hiện. Nếu CI gặp lại thì điều tra.
- **T6 — lỗi sản phẩm (một phần).** `TerminateProcess` chỉ bắt đầu kết thúc tiến trình; `HasExited` theo exit code nên `ExternalTool` có thể trả về khi tesseract vẫn giữ file/bộ nhớ. `ExternalTool` giờ chờ cả handle (`EndedAsync`), test RED cố định bằng ping bị giữ bởi debugger. Agent bị dừng trước khi báo cáo; phần dọn test chưa commit (bỏ chờ 5 s trong `ChildrenStillRunningAsync`) không được lấy. `TesseractTests.Cancelling_long_run…` và `PinnedInputTests` Linux chưa được xác nhận riêng.

### Bộ test đầy đủ trên `fix/flaky-tests` (chạy lần lượt, máy rảnh)

Core 541, Pdf 1454, Media 225, Ocr 38 đạt. Image lượt đầu đỏ 1: `FramesTests.An_animation_whose_expanded_frames_exceed_the_area_limit_is_refused_before_expanding` "took 6.26 s" (giới hạn 5 s); chạy riêng 5/5 đạt trong 40 ms, chạy lại cả project 291 đạt. Ứng viên test nhạy thời gian mới (nhiều khả năng do các bài encode cùng project chiếm CPU); chưa sửa.

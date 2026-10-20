# Media fixtures

No video or audio file is kept here. `Fixtures.cs` makes each one with ffmpeg's own test sources (lavfi `testsrc2` for
the picture, `sine` for the sound) the first time a test asks for it, into a temporary folder that lives for one test
run: a 1080p clip, an upright phone video stored sideways with GPS tags, an HDR10 HEVC clip, an MKV with two audio
streams, a WAV and an M4A. Each method there says exactly what it makes and how.

On Windows the fixtures are made by the pinned ffmpeg the tests also convert with; elsewhere by the ffmpeg on the PATH
(see `TestFfmpeg.cs`).

Files that must not be valid media (an HLS playlist named `.mp4`, a concat list, an empty file) are written by the tests
that use them, as text.

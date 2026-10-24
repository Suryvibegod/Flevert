namespace Flevert.Ocr.Tests;

/// <summary>
/// What Flevert takes from tesseract to choose between a page read as it is and read turned (docs/specs/ocr.md): OSD's
/// answer with its confidence, and how sure the reading was of its words.
/// </summary>
[Collection(TesseractProcesses.Name)]
public sealed class ReadingQualityTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("Rotate: 180\nOrientation confidence: 8.99\n", 180, 8.99)]
    [InlineData("Page number: 0\nOrientation in degrees: 270\nRotate: 90\nOrientation confidence: 1.69\nScript: Latin\n", 90, 1.69)]
    [InlineData("Rotate: 0\nOrientation confidence: 17.40\n", 0, 17.40)]
    [InlineData("Rotate: 45\nOrientation confidence: 20.00\n", 0, 20.00)] // not a quarter turn: nothing to turn
    [InlineData("Rotate: 90\n", 90, 0)] // no confidence: none
    [InlineData("", 0, 0)]
    public void OSD_answers_are_read_with_their_confidence(string output, int rotate, double confidence)
    {
        var orientation = Tesseract.ParseOsd(output);

        Assert.Equal(rotate, orientation.Rotate);
        Assert.Equal(confidence, orientation.Confidence, precision: 2);
    }

    [Fact]
    public void A_reading_counts_its_words_their_mean_confidence_and_those_on_lines_across()
    {
        // A line across the picture with two words, and a line standing up (a sideways page read as it is) with one.
        const string tsv = """
            level	page_num	block_num	par_num	line_num	word_num	left	top	width	height	conf	text
            1	1	0	0	0	0	0	0	3508	2480	-1
            2	1	1	0	0	0	200	200	800	60	-1
            3	1	1	1	0	0	200	200	800	60	-1
            4	1	1	1	1	0	200	200	800	60	-1
            5	1	1	1	1	1	200	200	300	60	96.5	Cộng
            5	1	1	1	1	2	520	200	300	60	93.5	hòa
            4	1	2	1	1	0	3253	211	49	814	-1
            5	1	2	1	1	1	3253	211	49	300	60.0	Độc
            5	1	2	1	1	2	3253	520	49	300	-1
            """;

        var quality = ReadingQuality.Parse(tsv);

        Assert.Equal(3, quality.Words);
        Assert.Equal(83.333, quality.MeanConfidence, precision: 3);
        Assert.Equal(2, quality.WordsAcross);
    }

    [Theory]
    [InlineData("")]
    [InlineData("level\tpage_num\n")]
    [InlineData("5\t1\t1\t1\t1\t1\tx\ty\tz\tw\tnot a number\tword\n")]
    [InlineData("5\t1\t1\n")]
    public void A_reading_without_words_has_none(string tsv)
    {
        var quality = ReadingQuality.Parse(tsv);

        Assert.Equal(0, quality.Words);
        Assert.Equal(0, quality.MeanConfidence);
    }

    [WindowsFact]
    public async Task One_run_gives_the_text_and_how_sure_tesseract_is_of_it()
    {
        var image = TestHelpers.CreateTextImage(_dir.Combine("page.png"), "Hóa đơn số 123", "Arial", fontSize: 22);
        // Read in an ASCII scratch folder, as Flevert's own: tesseract's output goes beside the picture.
        var scratch = Path.Combine(Path.GetTempPath(), "flevert-tests", $"ocr-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var picture = Path.Combine(scratch, "page.png");
            File.Copy(image, picture);

            var reading = await Tesseract.NextToApp.ReadAsync(picture, Path.Combine(scratch, "page-text"), CancellationToken.None);

            Assert.Equal("Hóa đơn số 123", reading.Text.Trim());
            Assert.Equal(4, reading.Quality.Words);
            Assert.InRange(reading.Quality.MeanConfidence, 85, 100);
            Assert.Equal(4, reading.Quality.WordsAcross);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [WindowsFact]
    public async Task The_text_layer_run_gives_how_sure_tesseract_is_of_its_text()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "flevert-tests", $"ocr-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var picture = TestHelpers.CreateTextImage(Path.Combine(scratch, "page.png"), "Hóa đơn số 123", "Arial", fontSize: 22);

            var reading = await Tesseract.NextToApp.ReadIntoTextLayerAsync(picture, Path.Combine(scratch, "layer"), CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(scratch, "layer.pdf")));
            Assert.Contains("123", reading.Text, StringComparison.Ordinal);
            Assert.Equal(4, reading.Quality.Words);
            Assert.InRange(reading.Quality.MeanConfidence, 85, 100);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}

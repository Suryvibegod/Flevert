using System.Globalization;

namespace Flevert.Ocr;

/// <summary>
/// OSD's answer as tesseract gives it: how many degrees clockwise to turn the page to stand it upright (0, 90, 180 or
/// 270), and how sure it is (its "Orientation confidence"; 0 when it gave none).
/// </summary>
internal readonly record struct OsdAnswer(int Rotate, double Confidence);

/// <summary>The text of one tesseract reading and how sure tesseract was of it.</summary>
internal readonly record struct OcrReading(string Text, ReadingQuality Quality);

/// <summary>
/// How sure tesseract was of a reading, from its TSV output: the words it read, their mean confidence (0-100), and how
/// many of them lie on lines running across the picture (wider than tall), as lines of a page shown upright do.
/// </summary>
/// <remarks>
/// A page read upside down gives as many words, but at a mean confidence of 53-74 where the page read upright gives 95-97
/// (docs/specs/ocr.md). A page turned a quarter clockwise reads as well as it does upright, on lines standing up.
/// </remarks>
internal readonly record struct ReadingQuality(int Words, double MeanConfidence, int WordsAcross)
{
    // level page_num block_num par_num line_num word_num left top width height conf text: a line's box is all it needs.
    private const int LineColumns = 10;
    private const int WordColumns = 12;
    private const int LineLevel = 4;
    private const int WordLevel = 5;

    /// <summary>Reads tesseract's TSV; rows it cannot read are left out.</summary>
    public static ReadingQuality Parse(string tsv)
    {
        var words = 0;
        var across = 0;
        var sum = 0.0;
        var lineAcross = false;
        foreach (var row in tsv.Split('\n'))
        {
            var cells = row.TrimEnd('\r').Split('\t');
            if (cells.Length < LineColumns || !int.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                continue;

            if (level == LineLevel)
            {
                lineAcross = Number(cells[8]) is { } width && Number(cells[9]) is { } height && width >= height;
            }
            else if (level == WordLevel && cells.Length >= WordColumns && cells[11].Trim().Length > 0 &&
                     Number(cells[10]) is { } confidence and >= 0 and <= 100)
            {
                words++;
                sum += confidence;
                if (lineAcross)
                    across++;
            }
        }

        return new ReadingQuality(words, words == 0 ? 0 : sum / words, across);
    }

    private static double? Number(string cell) =>
        double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
}

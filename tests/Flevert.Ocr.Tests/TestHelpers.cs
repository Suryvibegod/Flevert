using System.Drawing;
using System.Drawing.Imaging;

namespace Flevert.Ocr.Tests;

internal static class TestHelpers
{
    public static string CreateTextImage(
        string path,
        string text,
        string fontName = "Arial",
        float fontSize = 22,
        int width = 2400,
        int height = 400,
        int dpi = 300)
    {
        using var bmp = new Bitmap(width, height);
        bmp.SetResolution(dpi, dpi);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString(text, font, brush, 50, 100);
        }

        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    public static string CreateMultiLineImage(
        string path,
        string text,
        string fontName = "Arial",
        float fontSize = 18,
        int width = 1500,
        int height = 1500,
        int dpi = 300)
    {
        using var bmp = new Bitmap(width, height);
        bmp.SetResolution(dpi, dpi);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString(text, font, brush, new RectangleF(40, 40, width - 80, height - 80));
        }

        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    public static string CreateRotatedImage(string sourcePath, string destPath, RotateFlipType flipType)
    {
        using var bmp = new Bitmap(sourcePath);
        bmp.RotateFlip(flipType);
        bmp.Save(destPath, ImageFormat.Png);
        return destPath;
    }

    public static string CreateLargeNoiseImage(string path, int width = 5000, int height = 5000, int dpi = 300)
    {
        using var bmp = new Bitmap(width, height);
        bmp.SetResolution(dpi, dpi);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font("Arial", 8, FontStyle.Regular, GraphicsUnit.Point);
            using var brush = new SolidBrush(Color.Black);
            var rng = new Random(42);
            for (var y = 10; y < height - 10; y += 20)
            {
                var chars = new char[200];
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = (char)('A' + rng.Next(26));
                g.DrawString(new string(chars), font, brush, 10, y);
            }
        }

        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    public static double Similarity(string expected, string actual)
    {
        var e = Normalize(expected);
        var a = Normalize(actual);
        if (e == a)
            return 1.0;
        if (e.Length == 0 || a.Length == 0)
            return 0.0;
        var dist = Levenshtein(e, a);
        return 1.0 - (double)dist / Math.Max(e.Length, a.Length);
    }

    private static string Normalize(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    public static int Levenshtein(string s, string t)
    {
        if (string.IsNullOrEmpty(s))
            return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t))
            return s.Length;

        var v0 = new int[t.Length + 1];
        var v1 = new int[t.Length + 1];

        for (var i = 0; i <= t.Length; i++)
            v0[i] = i;

        for (var i = 0; i < s.Length; i++)
        {
            v1[0] = i + 1;
            for (var j = 0; j < t.Length; j++)
            {
                var cost = s[i] == t[j] ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }

            Array.Copy(v1, v0, v0.Length);
        }

        return v0[t.Length];
    }
}

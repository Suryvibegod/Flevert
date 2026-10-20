using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flevert.Pdf;

/// <summary>
/// Turns qpdf's description of a PDF, with every stream's stored data in a file of its own
/// (<see cref="Qpdf.DescribeWithStreamDataAsync"/>), and the smaller JPEGs the PDF worker made
/// (<see cref="PdfWorker.ShrinkImagesAsync"/>) into the update <c>qpdf --update-from-json</c> applies for
/// <c>Compress for sharing</c>: each image stream whose stored data is one the worker shrank gets the JPEG as its data,
/// and a dictionary that says so.
/// </summary>
/// <remarks>
/// <para>
/// An image is found by the SHA-256 of its stored data, which pdfium (in the worker) and qpdf (here) both read without
/// decoding anything: no image data of the PDF is decoded in this process. Images with the same data are the same image
/// to pdfium; if their dictionaries differ (one inverted by <c>/Decode</c>, say), the JPEG is right for only one, so none
/// is replaced.
/// </para>
/// <para>
/// Left as they are: stencil masks (<c>/ImageMask</c>), images with a <c>/Mask</c> (a colour key would no longer match
/// lossy pixels), JPEG 2000 with its own alpha (<c>/SMaskInData</c>), data kept in another file (<c>/F</c>), and any image
/// whose JPEG is not smaller than what it replaces. A soft mask (<c>/SMask</c>) stays as it is, at its own size, which
/// PDF allows.
/// </para>
/// </remarks>
internal static class ImageReplacement
{
    // What the JPEG replaces in the dictionary, and what no longer applies to its data.
    private static readonly string[] ReplacedKeys =
        ["/Filter", "/DecodeParms", "/Decode", "/Width", "/Height", "/BitsPerComponent", "/ColorSpace", "/Length", "/DL"];

    /// <summary>
    /// Writes to <paramref name="updatePath"/> the update for the PDF <paramref name="describedPath"/> describes, whose
    /// stream data files start with <paramref name="streamPrefix"/>, and returns how many images it replaces.
    /// </summary>
    /// <exception cref="InvalidDataException">The description is not complete or not in the form qpdf writes.</exception>
    public static int WriteUpdate(string describedPath, string streamPrefix, IReadOnlyList<PdfShrunkImage> shrunk, string updatePath, CancellationToken ct)
    {
        var byHash = shrunk.ToDictionary(image => image.Hash, StringComparer.Ordinal);
        var prefix = Qpdf.ToExtendedPath(streamPrefix);
        JsonObject? header = null;
        var matches = new List<(string Key, JsonObject Dictionary, PdfShrunkImage Image, long Stored)>();
        var dictionaries = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        QpdfJsonFile.Read(describedPath, (element, key, value) =>
        {
            if (element == QpdfJsonFile.Header)
            {
                (header ??= [])[key] = value?.DeepClone();
                return;
            }

            if (value?["stream"] is not JsonObject stream || stream["dict"] is not JsonObject dictionary || !Is(dictionary, "/Subtype", "/Image")
                || stream["datafile"] is not JsonValue file || !file.TryGetValue<string>(out var dataPath))
                return;
            // Only files qpdf wrote for this description: nothing else is read.
            if (!dataPath.StartsWith(prefix + "-", StringComparison.Ordinal) || dataPath.AsSpan(prefix.Length + 1).ContainsAny('\\', '/'))
                throw new InvalidDataException("qpdf's description of the PDF names a stream file outside its folder.");
            var (hash, stored) = Hash(dataPath);
            if (!byHash.TryGetValue(hash, out var image))
                return;
            matches.Add((key, dictionary, image, stored));
            if (!dictionaries.TryGetValue(hash, out var seen))
                dictionaries[hash] = seen = new HashSet<string>(StringComparer.Ordinal);
            seen.Add(dictionary.ToJsonString());
        }, ct);
        if (header is null)
            throw new InvalidDataException("qpdf's description of the PDF has no header.");

        var replaced = 0;
        using var output = new FileStream(updatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = QpdfJsonFile.MaxDepth });
        json.WriteStartObject();
        json.WriteStartArray("qpdf");
        header.WriteTo(json);
        json.WriteStartObject();
        foreach (var (key, dictionary, image, stored) in matches)
        {
            if (dictionaries[image.Hash].Count != 1 || !Replaceable(dictionary) || new FileInfo(image.Path).Length >= stored)
                continue;
            var replacement = (JsonObject)dictionary.DeepClone();
            foreach (var name in ReplacedKeys)
                replacement.Remove(name);
            replacement["/Filter"] = "/DCTDecode";
            replacement["/Width"] = image.Width;
            replacement["/Height"] = image.Height;
            replacement["/BitsPerComponent"] = 8;
            replacement["/ColorSpace"] = image.Gray ? "/DeviceGray" : "/DeviceRGB";
            json.WritePropertyName(key);
            json.WriteStartObject();
            json.WritePropertyName("stream");
            new JsonObject { ["dict"] = replacement, ["datafile"] = Qpdf.ToExtendedPath(image.Path) }.WriteTo(json);
            json.WriteEndObject();
            replaced++;
        }

        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
        return replaced;
    }

    private static bool Replaceable(JsonObject dictionary) =>
        !(dictionary["/ImageMask"] is JsonValue mask && mask.TryGetValue<bool>(out var isMask) && isMask)
        && !dictionary.ContainsKey("/Mask")
        && !(dictionary["/SMaskInData"] is JsonValue inData && inData.TryGetValue<int>(out var alpha) && alpha != 0)
        && !dictionary.ContainsKey("/F");

    private static (string Hash, long Length) Hash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return (Convert.ToHexStringLower(SHA256.HashData(file)), file.Length);
    }

    private static bool Is(JsonObject dictionary, string key, string name) =>
        dictionary[key] is JsonValue value && value.TryGetValue<string>(out var text) && text == name;
}

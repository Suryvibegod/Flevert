using System.Security.Cryptography;
using System.Text;
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
/// is replaced. Only images a page draws from its resources are replaced, and only when nothing else in the PDF may draw
/// them (<see cref="PageImages"/>).
/// </para>
/// <para>
/// Left as they are: stencil masks (<c>/ImageMask</c>), images with a <c>/Mask</c> (a colour key would no longer match
/// lossy pixels), JPEG 2000 with its own alpha (<c>/SMaskInData</c>), data kept in another file (<c>/F</c>), and any image
/// whose JPEG is not smaller than what it replaces. A soft mask (<c>/SMask</c>) stays as it is, at its own size, which
/// PDF allows.
/// </para>
/// <para>
/// flevert.exe has no memory cap of its own, so little of the PDF is kept while the description is read: for each image
/// to replace its key, the SHA-256 of its dictionary and its JPEG, at most <see cref="MaxMatches"/> of them, and for each
/// data hash the worker shrank the SHA-256 of the dictionary seen with it. An image whose dictionary is longer than
/// <see cref="MaxDictionaryChars"/> is left as it is, and so is any image with the same data. The dictionaries of the
/// images replaced are read again as the update is written.
/// </para>
/// </remarks>
internal static class ImageReplacement
{
    /// <summary>The most images matched to a JPEG; any more are left as they are.</summary>
    internal const int MaxMatches = 10_000;

    /// <summary>An image whose dictionary, as qpdf's JSON, is longer than this is left as it is: nothing that large is kept.</summary>
    internal const int MaxDictionaryChars = 64 * 1024;

    // What the JPEG replaces in the dictionary, and what no longer applies to its data.
    private static readonly string[] ReplacedKeys =
        ["/Filter", "/DecodeParms", "/Decode", "/Width", "/Height", "/BitsPerComponent", "/ColorSpace", "/Length", "/DL"];

    /// <summary>
    /// Writes to <paramref name="updatePath"/> the update for the PDF <paramref name="describedPath"/> describes, whose
    /// stream data files start with <paramref name="streamPrefix"/>, and returns how many images it replaces.
    /// </summary>
    /// <exception cref="InvalidDataException">The description is not complete or not in the form qpdf writes.</exception>
    public static int WriteUpdate(
        string describedPath, string streamPrefix, IReadOnlyList<PdfShrunkImage> shrunk, string updatePath, CancellationToken ct, int maxMatches = MaxMatches)
    {
        var byHash = shrunk.ToDictionary(image => image.Hash, StringComparer.Ordinal);
        var prefix = Qpdf.ToExtendedPath(streamPrefix);
        JsonObject? header = null;
        // Small facts only: the dictionaries are read again, for the images written, when the update is written.
        var matches = new List<Match>();
        // For each data hash the worker shrank: the SHA-256 of the one dictionary seen with it, or null once two differ.
        var dictionaries = new Dictionary<string, string?>(StringComparer.Ordinal);
        var pages = new PageImages();
        QpdfJsonFile.Read(describedPath, (element, key, value) =>
        {
            if (element == QpdfJsonFile.Header)
            {
                (header ??= [])[key] = value?.DeepClone();
                return;
            }

            if (element == QpdfJsonFile.Pages)
            {
                pages.OnPage(value);
                return;
            }

            pages.OnObject(key, value);
            // Only the images pages draw from their resources are the worker's: it sees no other.
            if (!pages.Drawn.Contains(key) || ImageData(value, prefix) is not var (dictionary, dataPath))
                return;
            var (hash, stored) = Hash(dataPath);
            if (!byHash.TryGetValue(hash, out var image))
                return;
            // A dictionary too large to keep cannot be told to match another: no image with its data is replaced.
            var json = dictionary.ToJsonString();
            var dictionaryHash = json.Length <= MaxDictionaryChars ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) : null;
            dictionaries[hash] = dictionaries.TryGetValue(hash, out var seen) && seen != dictionaryHash ? null : dictionaryHash;
            if (dictionaryHash is not null && matches.Count < maxMatches && Replaceable(dictionary) && new FileInfo(image.Path).Length < stored)
                matches.Add(new Match(key, dictionaryHash, image));
        }, ct);
        if (header is null)
            throw new InvalidDataException("qpdf's description of the PDF has no header.");

        var written = matches.Where(match => dictionaries[match.Image.Hash] == match.DictionaryHash).ToDictionary(match => match.Key, StringComparer.Ordinal);
        if (written.Count > 0)
        {
            foreach (var key in pages.UsedElsewhere(describedPath, written.Keys, ct))
                written.Remove(key);
        }

        var replaced = 0;
        using var output = new FileStream(updatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = QpdfJsonFile.MaxDepth });
        json.WriteStartObject();
        json.WriteStartArray("qpdf");
        header.WriteTo(json);
        json.WriteStartObject();
        if (written.Count > 0)
        {
            QpdfJsonFile.Read(describedPath, (element, key, value) =>
            {
                if (element != QpdfJsonFile.Objects || !written.TryGetValue(key, out var match) || ImageData(value, prefix) is not var (dictionary, _))
                    return;
                // The file is in the operation's private folder; read twice, it must still say what it said.
                if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dictionary.ToJsonString()))) != match.DictionaryHash)
                    throw new InvalidDataException("qpdf's description of the PDF changed while it was read.");
                var image = match.Image;
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
            }, ct);
        }

        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
        return replaced;
    }

    /// <summary>
    /// Which images the pages draw from their resources, as qpdf lists them (<c>--json-key=pages</c>: a page's own
    /// <c>/Resources</c> or the one it inherits, <c>/XObject</c>, not inside forms), and which of them something else in
    /// the PDF may draw too: an annotation's appearance, a pattern, a Type 3 glyph or a soft mask group that refers to
    /// them, or to the resources or XObject dictionary that holds them. The worker sees only what a page's content
    /// draws, so an image drawn anywhere else may be drawn larger there: it is never replaced.
    /// </summary>
    private sealed class PageImages
    {
        // Page objects and page tree nodes.
        private readonly HashSet<string> _pageTree = new(StringComparer.Ordinal);

        // A page tree object's /Resources: the object that is the dictionary, or the page tree object itself when inline.
        private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal);

        // An object's /XObject dictionary (a resources dictionary's, or the inline resources of a page tree object): the
        // object that is the dictionary, or the owner itself when inline.
        private readonly Dictionary<string, string> _xobjects = new(StringComparer.Ordinal);

        private bool _objectsSeen;

        /// <summary>The keys (<c>obj:N G R</c>) of the images some page draws from its resources.</summary>
        public HashSet<string> Drawn { get; } = new(StringComparer.Ordinal);

        public void OnPage(JsonNode? page)
        {
            // qpdf writes "pages" before "qpdf": the images drawn must be known as the objects go by.
            if (_objectsSeen)
                throw new InvalidDataException("qpdf's description of the PDF lists its pages after its objects.");
            if (Key(page?["object"]) is { } pageKey)
                _pageTree.Add(pageKey);
            if (page?["images"] is not JsonArray images)
                return;
            foreach (var image in images)
            {
                if (Key(image?["object"]) is { } imageKey)
                    Drawn.Add(imageKey);
            }
        }

        public void OnObject(string key, JsonNode? value)
        {
            _objectsSeen = true;
            if ((value?["value"] ?? value?["stream"]?["dict"]) is not JsonObject dictionary)
                return;
            if (_pageTree.Contains(key) || Is(dictionary, "/Type", "/Page") || Is(dictionary, "/Type", "/Pages"))
            {
                _pageTree.Add(key);
                if (dictionary["/Resources"] is JsonObject inline)
                {
                    _resources[key] = key;
                    if (Holder(key, inline["/XObject"]) is { } holder)
                        _xobjects[key] = holder;
                }
                else if (Key(dictionary["/Resources"]) is { } resources)
                {
                    _resources[key] = resources;
                }
            }
            else if (Holder(key, dictionary["/XObject"]) is { } holder)
            {
                _xobjects[key] = holder;
            }
        }

        /// <summary>
        /// Of <paramref name="images"/>, the ones something other than the pages' resources refers to, directly or through
        /// the dictionaries that hold them. Reads the description once more.
        /// </summary>
        public HashSet<string> UsedElsewhere(string describedPath, IEnumerable<string> images, CancellationToken ct)
        {
            // The pages' resources dictionaries that are objects of their own, and the objects that hold the pages' XObject
            // dictionaries: a page tree object, a resources dictionary, or an XObject dictionary of its own.
            var resources = new HashSet<string>(StringComparer.Ordinal);
            var holders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (node, dictionary) in _resources)
            {
                if (dictionary != node)
                    resources.Add(dictionary);
                if (_xobjects.TryGetValue(dictionary, out var holder))
                    holders.Add(holder);
            }

            var xobjects = holders.Where(holder => !_pageTree.Contains(holder) && !resources.Contains(holder)).ToHashSet(StringComparer.Ordinal);
            var targets = images.ToHashSet(StringComparer.Ordinal);
            var elsewhere = new HashSet<string>(StringComparer.Ordinal);
            var held = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            QpdfJsonFile.Read(describedPath, (element, key, value) =>
            {
                if (element != QpdfJsonFile.Objects)
                    return;
                foreach (var reference in References(value))
                {
                    if (targets.Contains(reference))
                    {
                        if (!holders.Contains(key))
                            elsewhere.Add(reference);
                        else if (held.TryGetValue(key, out var list))
                            list.Add(reference);
                        else
                            held[key] = [reference];
                    }
                    else if ((resources.Contains(reference) && !_pageTree.Contains(key))
                        || (xobjects.Contains(reference) && !_pageTree.Contains(key) && !resources.Contains(key)))
                    {
                        // A dictionary of the pages' resources that something else uses as well.
                        elsewhere.Add(reference);
                    }
                }
            }, ct);

            // What a shared dictionary holds is used elsewhere too.
            foreach (var shared in resources.Where(elsewhere.Contains).ToList())
            {
                if (_xobjects.TryGetValue(shared, out var holder))
                    elsewhere.Add(holder);
            }

            foreach (var (holder, list) in held)
            {
                if (elsewhere.Contains(holder))
                    elsewhere.UnionWith(list);
            }

            elsewhere.IntersectWith(targets);
            return elsewhere;
        }

        // The object holding an XObject dictionary given as value: the one it refers to, or the owner when inline.
        private static string? Holder(string owner, JsonNode? value) => value is JsonObject ? owner : Key(value);

        // "obj:N G R" for a reference ("N G R"), null for anything else.
        private static string? Key(JsonNode? value) =>
            value is JsonValue text && text.TryGetValue<string>(out var reference) && IsReference(reference) ? "obj:" + reference : null;

        private static bool IsReference(string text)
        {
            var parts = text.Split(' ');
            return parts.Length == 3 && parts[2] == "R" && parts[0].Length is > 0 and <= 10 && parts[1].Length is > 0 and <= 5
                && parts[0].All(char.IsAsciiDigit) && parts[1].All(char.IsAsciiDigit);
        }

        // Every reference anywhere in an object's value, as a key.
        private static IEnumerable<string> References(JsonNode? value)
        {
            var pending = new Stack<JsonNode?>();
            pending.Push(value);
            while (pending.TryPop(out var node))
            {
                if (node is JsonObject entries)
                {
                    foreach (var (_, child) in entries)
                        pending.Push(child);
                }
                else if (node is JsonArray items)
                {
                    foreach (var child in items)
                        pending.Push(child);
                }
                else if (Key(node) is { } key)
                {
                    yield return key;
                }
            }
        }
    }

    /// <summary>An image to replace, as found on the first read: its key, the SHA-256 of its dictionary, its JPEG.</summary>
    private sealed record Match(string Key, string DictionaryHash, PdfShrunkImage Image);

    // The dictionary and stored data file of an image stream; null for any other object.
    private static (JsonObject Dictionary, string DataPath)? ImageData(JsonNode? value, string prefix)
    {
        if (value?["stream"] is not JsonObject stream || stream["dict"] is not JsonObject dictionary || !Is(dictionary, "/Subtype", "/Image")
            || stream["datafile"] is not JsonValue file || !file.TryGetValue<string>(out var dataPath))
            return null;
        // Only files qpdf wrote for this description: nothing else is read.
        if (!dataPath.StartsWith(prefix + "-", StringComparison.Ordinal) || dataPath.AsSpan(prefix.Length + 1).ContainsAny('\\', '/'))
            throw new InvalidDataException("qpdf's description of the PDF names a stream file outside its folder.");
        return (dictionary, dataPath);
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

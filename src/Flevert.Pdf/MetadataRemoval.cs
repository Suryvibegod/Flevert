using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flevert.Pdf;

/// <summary>
/// Turns qpdf's description of a PDF (<see cref="QpdfJsonFile"/>) into the update <c>qpdf --update-from-json</c> applies
/// for <c>Remove hidden info</c> (docs/specs/pdf.md): only the objects that change, each whole.
/// </summary>
/// <remarks>
/// <para>What goes, wherever it is in the file:</para>
/// <list type="bullet">
/// <item>the document information: replaced by <c>/Producer (Flevert)</c> alone, like every PDF Flevert makes;</item>
/// <item>XMP (<c>/Metadata</c>) of the document, of pages, images and anything else, and the streams themselves;</item>
/// <item>private application data (<c>/PieceInfo</c>), which may hold a whole earlier version of the document;</item>
/// <item>attachments: the embedded files name tree, every file specification's embedded file (<c>/EF</c>), embedded
/// file streams, and file attachment annotations;</item>
/// <item>JavaScript: the document's name tree, and every JavaScript action, wherever it is (open action, additional
/// actions of the document, pages, annotations and form fields, links, bookmarks, chained <c>/Next</c> actions), as
/// well as any script (<c>/JS</c>) of another action.</item>
/// </list>
/// <para>
/// Every reference to an object that goes is dropped from the dictionary or array holding it, and an object that only
/// loses keys is written again without them. qpdf writes only what the trailer still reaches, so an object nothing
/// refers to any more (an embedded file, an XMP packet, a script) is not in the output at all; the object itself is
/// replaced by null as well, so it stays out even if something still reached it. Stream data is never
/// in the update: qpdf keeps the data of a stream whose dictionary the update replaces.
/// </para>
/// </remarks>
internal static class MetadataRemoval
{
    /// <summary>The only document information left: what made this copy.</summary>
    public const string Producer = "Flevert";

    // Keys whose values are metadata, attachments or scripts, in any dictionary. Each has only that meaning in PDF.
    private static readonly HashSet<string> RemovedKeys = new(StringComparer.Ordinal)
    {
        "/Metadata", "/PieceInfo", "/EmbeddedFiles", "/JavaScript", "/EF", "/JS",
    };

    /// <summary>Writes to <paramref name="updatePath"/> the update for the PDF that <paramref name="describedPath"/> describes.</summary>
    /// <exception cref="InvalidDataException">The description is not complete or not in the form qpdf writes.</exception>
    public static void WriteUpdate(string describedPath, string updatePath, CancellationToken ct)
    {
        // First pass: what goes whole, so the second can drop every reference to it, whatever comes first in the file.
        var removed = new HashSet<string>(StringComparer.Ordinal);
        JsonNode? header = null;
        JsonObject? trailer = null;
        QpdfJsonFile.Read(describedPath, (element, key, value) =>
        {
            if (element == QpdfJsonFile.Header)
            {
                (header ??= new JsonObject())[key] = value;
            }
            else if (key == "trailer")
            {
                trailer = value?["value"] as JsonObject;
            }
            else if (Reference(key) is { } reference && Goes(value))
            {
                removed.Add(reference);
            }
        }, ct);

        if (header?["maxobjectid"] is not JsonValue maxObject || !maxObject.TryGetValue<long>(out var maxObjectId) || trailer is null)
            throw new InvalidDataException("qpdf's description of the PDF has no object count or no trailer.");

        // The information dictionary the trailer refers to is rewritten where it is; one written inside the trailer, or
        // none, becomes a new object the trailer refers to.
        var info = trailer["/Info"] is JsonValue infoValue && infoValue.TryGetValue<string>(out var infoReference) && IsReference(infoReference)
            ? infoReference
            : null;

        using var file = new FileStream(updatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { MaxDepth = QpdfJsonFile.MaxDepth });
        json.WriteStartObject();
        json.WriteStartArray("qpdf");
        header!.WriteTo(json);
        json.WriteStartObject();

        QpdfJsonFile.Read(describedPath, (element, key, value) =>
        {
            if (element != QpdfJsonFile.Objects || Reference(key) is not { } reference || reference == info)
                return;
            if (removed.Contains(reference))
                WriteEntry(json, key, "value", null);
            else if (value?["value"] is JsonObject dictionary && CleanObject(dictionary, removed))
                WriteEntry(json, key, "value", dictionary);
            else if (value?["value"] is JsonArray array && CleanArray(array, removed))
                WriteEntry(json, key, "value", array);
            else if (value?["stream"]?["dict"] is JsonObject streamDictionary && CleanObject(streamDictionary, removed))
                WriteEntry(json, key, "stream", new JsonObject { ["dict"] = streamDictionary.DeepClone() }); // no "data": qpdf keeps the stream's own
        }, ct);

        var newInfo = info ?? string.Create(CultureInfo.InvariantCulture, $"{maxObjectId + 1} 0 R");
        WriteEntry(json, "obj:" + newInfo, "value", new JsonObject { ["/Producer"] = "u:" + Producer });
        if (info is null)
        {
            trailer["/Info"] = newInfo;
            WriteEntry(json, "trailer", "value", trailer);
        }

        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
    }

    // "key": {"kind": content}, as qpdf JSON writes an object ("value" or "stream") or the trailer ("value").
    private static void WriteEntry(Utf8JsonWriter json, string key, string kind, JsonNode? content)
    {
        json.WritePropertyName(key);
        json.WriteStartObject();
        json.WritePropertyName(kind);
        if (content is null)
            json.WriteNullValue();
        else
            content.WriteTo(json);
        json.WriteEndObject();
    }

    // "obj:12 0 R" → "12 0 R"; null for "trailer" or anything else.
    private static string? Reference(string key) =>
        key.StartsWith("obj:", StringComparison.Ordinal) && IsReference(key[4..]) ? key[4..] : null;

    // qpdf JSON writes a reference as "N G R"; names start with "/", strings with "u:" or "b:".
    private static bool IsReference(string value)
    {
        var parts = value.Split(' ');
        return parts.Length == 3 && parts[2] == "R"
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    // An object that goes whole: a JavaScript action, a file attachment annotation, an embedded file or an XMP stream.
    private static bool Goes(JsonNode? entry) =>
        entry?["value"] is JsonObject value
            ? GoesWhole(value)
            : entry?["stream"]?["dict"] is JsonObject dictionary && (Is(dictionary, "/Type", "/EmbeddedFile") || Is(dictionary, "/Type", "/Metadata"));

    private static bool GoesWhole(JsonObject dictionary) =>
        Is(dictionary, "/S", "/JavaScript") || Is(dictionary, "/Subtype", "/FileAttachment");

    private static bool Is(JsonObject dictionary, string key, string name) =>
        dictionary[key] is JsonValue value && value.TryGetValue<string>(out var text) && text == name;

    // Removes, in place, the keys and values that go, at any depth, and tells whether anything did.
    private static bool CleanObject(JsonObject dictionary, HashSet<string> removed)
    {
        var changed = false;
        foreach (var (key, value) in dictionary.ToList())
        {
            if (RemovedKeys.Contains(key) || Dropped(value, removed))
            {
                dictionary.Remove(key);
                changed = true;
            }
            else if (value is JsonObject inner)
            {
                changed |= CleanObject(inner, removed);
            }
            else if (value is JsonArray array)
            {
                changed |= CleanArray(array, removed);
            }
        }

        return changed;
    }

    private static bool CleanArray(JsonArray array, HashSet<string> removed)
    {
        var changed = false;
        for (var i = array.Count - 1; i >= 0; i--)
        {
            var item = array[i];
            if (Dropped(item, removed))
            {
                array.RemoveAt(i);
                changed = true;
            }
            else if (item is JsonObject inner)
            {
                changed |= CleanObject(inner, removed);
            }
            else if (item is JsonArray nested)
            {
                changed |= CleanArray(nested, removed);
            }
        }

        return changed;
    }

    // A reference to an object that goes, or a dictionary written in place that goes whole.
    private static bool Dropped(JsonNode? value, HashSet<string> removed) => value switch
    {
        JsonValue reference when reference.TryGetValue<string>(out var text) => removed.Contains(text),
        JsonObject dictionary => GoesWhole(dictionary),
        _ => false,
    };
}

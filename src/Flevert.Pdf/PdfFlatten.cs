using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Lock filled forms</c> (docs/specs/pdf.md, v0.2.2): writes <c>&lt;name&gt; (flattened).pdf</c>, in which filled form
/// fields and comments (notes, stamps, highlights) are part of the page: they print and show the same everywhere, and
/// nobody can change the answers in a viewer. Text stays text and drawings stay vector, never a picture of the page as
/// Stirling-PDF makes it. A PDF without form fields or comments is skipped.
/// </summary>
/// <remarks>
/// <para>
/// qpdf flattens (<c>--flatten-annotations=all</c>): it draws each visible annotation's appearance into the page content
/// and removes it, and removes the form (<c>/AcroForm</c>) once every field is drawn, so no field is left to fill
/// (success criterion 5). pdfium's <c>FPDFPage_Flatten</c>, which the spec named, leaves the form's fields in the
/// catalog, and draws nothing for a field without an appearance; qpdf runs out of process with the limits of every qpdf
/// operation, so no pdfium worker is needed (deviation noted in tasks/pdf/todo.md).
/// </para>
/// <para>
/// A form that asks viewers to draw its fields (<c>/NeedAppearances</c>) gets them drawn by qpdf first
/// (<c>--generate-appearances</c>), but only when qpdf can draw every answer: it writes them in the single-byte encoding
/// of the field's font, and a letter outside it (most Vietnamese ones) becomes "?". Flevert counts an answer qpdf can draw
/// only when it is printable ASCII (<see cref="CanRedraw"/>), the one encoding qpdf always has, in a field of one line
/// (qpdf 12.4.2 draws a field of several lines on one line, cut at its edge). Otherwise the file fails
/// rather than writing wrong or missing text (<see cref="CannotRedrawMessage"/>), even when the fields have drawings:
/// in a form that asks viewers to draw them, those may be the blank form's, kept by the tool that filled it (pypdf,
/// pdftk). A form that does not ask is locked as the PDF draws it, its text still text; an answer with no drawing at
/// all, which Flevert would have to draw, fails the file too.
/// </para>
/// <para>
/// What there is to flatten is read from qpdf's description of the PDF without stream data (as Remove metadata reads it):
/// a field (<c>/FT</c>), or an annotation with an appearance (<c>/AP</c> and <c>/Rect</c>); and for each widget, its
/// field's type and answer (inherited through <c>/Parent</c>) and whether it has an appearance. The description, and the
/// update when there is one, go in a private folder under the temporary folder, deleted when the operation ends.
/// </para>
/// </remarks>
public sealed class PdfFlatten : IOperation
{
    internal const string NothingMessage = "This PDF has no filled forms or comments to lock.";

    // For every refusal: letters qpdf cannot write, a field of several lines, or an answer with no drawing.
    internal const string CannotRedrawMessage =
        "This form has answers Flevert cannot draw as they are: open it in a PDF reader, save it, and try again.";

    // A text field's flag (/Ff) for several lines: bit 13.
    private const long MultiLine = 1 << 12;

    // A field's /Parent chain longer than this is a loop or a hostile PDF.
    private const int MaxFieldDepth = 32;

    private readonly Qpdf _qpdf;

    public PdfFlatten(Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
    }

    public string Id => "pdf.flatten";

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // qpdf processes, mostly reading and writing (PdfRotate.MaxParallel).
    public int MaxParallel => 4;

    /// <summary>Where the operation's private folders go: the user's temporary folder, or a test's.</summary>
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    public async Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var pages = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);
        var scratch = Path.Combine(Path.GetFullPath(ScratchRoot), $"flevert-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        try
        {
            var described = Path.Combine(scratch, "pdf.json");
            await _qpdf.TransformAsync(inputPath, described, ["--json-output=2", "--json-stream-data=none"], ct).ConfigureAwait(false);
            FormFacts facts;
            try
            {
                facts = await Task.Run(() => FormFacts.Read(described, ct), ct).ConfigureAwait(false);
            }
            catch (InvalidDataException e)
            {
                throw new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage, e);
            }

            if (!facts.Something)
                throw new OperationSkippedException(NothingMessage);
            var plan = facts.Plan();
            if (plan.Refuse)
                throw new PdfException(PdfError.CannotRedrawForm, CannotRedrawMessage);
            string? update = null;
            if (plan.Update is { } changed)
            {
                update = Path.Combine(scratch, "update.json");
                WriteUpdate(update, facts.Header, changed.Key, changed.Value);
            }

            var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (flattened).pdf");
            var output = await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
            {
                string[] options = plan.Redraw ? ["--generate-appearances", "--flatten-annotations=all"] : ["--flatten-annotations=all"];
                var repaired = update is null
                    ? await _qpdf.TransformAsync(inputPath, temp, [Qpdf.KeepStreamsAsTheyAre, .. options], token).ConfigureAwait(false)
                    : await _qpdf.UpdateFromJsonAsync(inputPath, update, temp, options, token).ConfigureAwait(false);
                await PdfRepair.CheckAsync(_qpdf, repaired, pages, temp, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (signed)
                OutputNote.Add(PdfSignature.NotValidNote);
            return output;
        }
        finally
        {
            try
            {
                // Only qpdf's description and Flevert's update are in it: nobody else can add to it, so nothing in it is a link.
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder, readable only by this user, SYSTEM and administrators.
            }
        }
    }

    /// <summary>
    /// Whether qpdf can draw <paramref name="answer"/> as it is: printable ASCII, which every font encoding qpdf writes
    /// holds. Anything else may come out as "?" (qpdf draws only in the font's single-byte encoding), or on one line where
    /// it had several.
    /// </summary>
    internal static bool CanRedraw(string answer) => answer.All(c => c is >= ' ' and <= '~');

    // {"qpdf": [header, {"obj:N G R": {"value": dictionary}}]}: the one object that changes, named by its reference ("N G R").
    private static void WriteUpdate(string path, JsonObject header, string key, JsonObject value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { MaxDepth = QpdfJsonFile.MaxDepth });
        new JsonObject { ["qpdf"] = new JsonArray(header.DeepClone(), new JsonObject { ["obj:" + key] = new JsonObject { ["value"] = value.DeepClone() } }) }.WriteTo(json);
    }

    /// <summary>How to flatten: drawing the fields again or not, the object to change first, or not at all.</summary>
    internal sealed record FlattenPlan(bool Redraw, bool Refuse, KeyValuePair<string, JsonObject>? Update);

    /// <summary>What qpdf's description says about the PDF's form, read in one pass.</summary>
    internal sealed class FormFacts
    {
        private readonly Dictionary<string, Field> _fields = new(StringComparer.Ordinal);
        private readonly List<(string Key, bool Drawn)> _widgets = [];
        // Objects that may hold the form: those with /AcroForm (the catalog) or /NeedAppearances (the form itself).
        private readonly Dictionary<string, JsonObject> _holders = new(StringComparer.Ordinal);
        private string? _root;

        public bool Something { get; private set; }

        public JsonObject Header { get; } = [];

        private sealed record Field(string? Type, JsonNode? Value, JsonNode? Flags, string? Parent);

        public static FormFacts Read(string describedPath, CancellationToken ct)
        {
            var facts = new FormFacts();
            QpdfJsonFile.Read(describedPath, (element, key, value) =>
            {
                if (element == QpdfJsonFile.Header)
                {
                    facts.Header[key] = value?.DeepClone();
                    return;
                }

                if (element != QpdfJsonFile.Objects || value is not JsonObject entry)
                    return;
                if (key == "trailer")
                {
                    facts._root = Text(entry["value"]?["/Root"]);
                    return;
                }

                if (!key.StartsWith("obj:", StringComparison.Ordinal))
                    return;
                var reference = key[4..];
                var dictionary = entry["value"] as JsonObject ?? (entry["stream"] as JsonObject)?["dict"] as JsonObject;
                if (dictionary is null)
                    return;
                if (dictionary.ContainsKey("/FT") || (dictionary.ContainsKey("/AP") && dictionary.ContainsKey("/Rect")))
                    facts.Something = true;
                if (entry["value"] is JsonObject && (dictionary.ContainsKey("/AcroForm") || dictionary.ContainsKey("/NeedAppearances")))
                    facts._holders[reference] = (JsonObject)dictionary.DeepClone();
                if (dictionary.ContainsKey("/FT") || dictionary.ContainsKey("/V") || dictionary.ContainsKey("/Parent"))
                    facts._fields[reference] = new Field(Text(dictionary["/FT"]), dictionary["/V"]?.DeepClone(), dictionary["/Ff"]?.DeepClone(), Text(dictionary["/Parent"]));
                if (Text(dictionary["/Subtype"]) == "/Widget")
                    facts._widgets.Add((reference, dictionary["/AP"] is { } appearance && (appearance is not JsonObject ap || ap.ContainsKey("/N"))));
            }, ct);
            return facts;
        }

        public FlattenPlan Plan()
        {
            var (needAppearances, form) = Form();
            var answers = _widgets.Select(widget => (Answer: Answer(widget.Key, out var lines), Lines: lines, widget.Drawn)).Where(widget => widget.Answer.Length > 0).ToList();
            // A form that asks to be drawn is drawn again by qpdf, only when it can draw every answer. Its drawings may be
            // the blank form's (pypdf and pdftk fill a form by its answers and keep them): locked as drawn, an answer
            // would be gone.
            if (needAppearances)
            {
                var redraw = answers.All(widget => !widget.Lines && widget.Answer.All(CanRedraw));
                return new FlattenPlan(Redraw: redraw, Refuse: !redraw, Update: null);
            }

            // Locked as drawn: an answer without a drawing would be lost, or left as a field to fill.
            if (answers.Any(widget => !widget.Drawn))
                return new FlattenPlan(Redraw: false, Refuse: true, Update: null);
            return new FlattenPlan(Redraw: false, Refuse: false, Update: form);
        }

        // Whether the form asks viewers to draw its fields, and the object to write without that request (when it has the key).
        private (bool NeedAppearances, KeyValuePair<string, JsonObject>? Update) Form()
        {
            if (_root is null || !_holders.TryGetValue(_root, out var catalog))
                return (false, null);
            var (key, form) = catalog["/AcroForm"] switch
            {
                JsonObject inline => (_root, inline),
                JsonValue value when Text(value) is { } reference && _holders.TryGetValue(reference, out var held) => (reference, held),
                _ => (null, null),
            };
            if (key is null || form is null || !form.ContainsKey("/NeedAppearances"))
                return (false, null);
            // Only a direct false says the drawings are current: a reference (to true, usually) or anything else asks for them
            // to be drawn, and locking a stale drawing would lose the answer.
            var need = !(form["/NeedAppearances"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && !on);
            form.Remove("/NeedAppearances");
            return (need, new KeyValuePair<string, JsonObject>(key, key == _root ? catalog : form));
        }

        // The answers of a widget's field to draw: its text or choices, inherited through /Parent; none for other fields.
        // Several lines: a text field of several lines (flag bit 13), which qpdf draws on one line, cut at the field's edge.
        private string[] Answer(string widget, out bool lines)
        {
            string? type = null;
            JsonNode? value = null;
            JsonNode? flags = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var at = widget; at is not null && seen.Add(at) && seen.Count <= MaxFieldDepth && _fields.TryGetValue(at, out var field); at = field.Parent)
            {
                type ??= field.Type;
                value ??= field.Value;
                flags ??= field.Flags;
            }

            lines = type == "/Tx" && flags is JsonValue ff && ff.TryGetValue<long>(out var bits) && (bits & MultiLine) != 0;
            if (type is not ("/Tx" or "/Ch"))
                return [];
            JsonNode?[] values = value is JsonArray array ? [.. array] : [value];
            return [.. values.Select(Decode).Where(text => text.Length > 0)];
        }

        // qpdf JSON writes text as "u:<text>" and other strings as "b:<hex>"; anything else (a name, a reference) is no answer to draw.
        private static string Decode(JsonNode? node)
        {
            if (Text(node) is not { } text)
                return "";
            if (text.StartsWith("u:", StringComparison.Ordinal))
                return text[2..];
            if (!text.StartsWith("b:", StringComparison.Ordinal))
                return "";
            try
            {
                var bytes = Convert.FromHexString(text.AsSpan(2));
                // Bytes outside ASCII cannot be drawn as they are: one that is not ASCII stands for a letter no check here knows.
                return bytes.All(b => b is >= 0x20 and <= 0x7E) ? System.Text.Encoding.ASCII.GetString(bytes) : "\u0080";
            }
            catch (FormatException)
            {
                return "\u0080";
            }
        }

        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}

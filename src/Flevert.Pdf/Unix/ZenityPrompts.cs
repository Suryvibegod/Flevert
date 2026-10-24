using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// Asks for PDF passwords on Linux in a zenity form: a masked field, a second one to protect, and whether the answer is
/// for every file left when the batch has more (docs/specs/pdf.md, "Mật khẩu"). The password comes back through a pipe
/// into bytes and characters that are wiped, never through a command line or a string, as on Windows.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ZenityPasswordPrompt(string zenity = ZenityProgress.Zenity) : IPasswordPrompt, IUnixQuestion
{
    private readonly ZenityQuestion _question = new();

    public bool Asking => _question.Asking;

    public void Report(BatchProgress progress) => _question.Report(progress);

    public async Task<PasswordAnswer> AskAsync(PasswordQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        var protect = question.Purpose == PasswordPurpose.Protect;
        var name = Path.GetFileName(question.InputPath);
        var heading = protect
            ? $"Choose a password for a protected copy of {name}. It will open only with this password, and Flevert cannot recover it if it is forgotten."
            : $"Type the password of {name}. Flevert writes a copy that opens without one.";
        string? problem = null;
        while (true)
        {
            var files = _question.FilesLeft(question.AnsweredInProgress);
            List<string> arguments =
            [
                "--forms", protect ? "--title=Flevert — Protect with password" : "--title=Flevert — Remove password",
                "--text=" + ZenityQuestion.Text(heading, problem), ZenityQuestion.Separator,
                protect ? "--ok-label=Protect" : "--ok-label=Remove password", "--add-password=Password",
            ];
            if (protect)
                arguments.Add("--add-password=Password again");
            arguments.AddRange(ZenityQuestion.ForAll(files));

            using var reply = await _question.AskAsync(arguments, zenity, ct).ConfigureAwait(false);
            if (reply.ExitCode != 0)
                return new PasswordAnswer(null, 1);
            if (ZenityQuestion.Fields(reply.Output, (protect ? 2 : 1) + ZenityQuestion.ForAllFields(files)) is not { } fields)
            {
                problem = ZenityQuestion.UnreadableMessage;
                continue;
            }

            var password = fields[0];
            var again = protect ? fields[1] : [];
            var forAll = ZenityQuestion.ChoseAll(fields.ElementAtOrDefault(protect ? 2 : 1), files);
            problem = PasswordRules.Problem(password, again, question.Purpose);
            // Only the password itself is kept, by the PdfPassword that takes it over; the second entry goes now.
            foreach (var other in fields.Skip(1))
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(other.AsSpan()));
            if (problem is null)
                return new PasswordAnswer(new PdfPassword(password), forAll ? files : 1);
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(password.AsSpan()));
        }
    }
}

/// <summary>Asks for the watermark's text on Linux in a zenity form, with the checks of the Windows dialog.</summary>
[SupportedOSPlatform("linux")]
public sealed class ZenityWatermarkPrompt(string zenity = ZenityProgress.Zenity) : IWatermarkPrompt, IUnixQuestion
{
    private readonly ZenityQuestion _question = new();

    public bool Asking => _question.Asking;

    public void Report(BatchProgress progress) => _question.Report(progress);

    public async Task<WatermarkAnswer> AskAsync(WatermarkQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        var heading = $"Type the text to write across every page of {Path.GetFileName(question.InputPath)}, such as what the copy is for. " +
            "It marks the copy; anyone with a PDF editor can still remove it.";
        string? problem = null;
        while (true)
        {
            var files = _question.FilesLeft(question.AnsweredInProgress);
            List<string> arguments =
            [
                "--forms", "--title=Flevert — Add watermark", "--text=" + ZenityQuestion.Text(heading, problem), ZenityQuestion.Separator,
                "--ok-label=Add watermark", "--add-entry=Text",
            ];
            arguments.AddRange(ZenityQuestion.ForAll(files));

            using var reply = await _question.AskAsync(arguments, zenity, ct).ConfigureAwait(false);
            if (reply.ExitCode != 0)
                return new WatermarkAnswer(null, 1);
            if (ZenityQuestion.Fields(reply.Output, 1 + ZenityQuestion.ForAllFields(files)) is not { } fields)
            {
                problem = ZenityQuestion.UnreadableMessage;
                continue;
            }

            var text = new string(fields[0]);
            var forAll = ZenityQuestion.ChoseAll(fields.ElementAtOrDefault(1), files);
            problem = WatermarkPrompts.Problem(text);
            if (problem is null)
                return new WatermarkAnswer(text, forAll ? files : 1);
        }
    }
}

/// <summary>What the two zenity questions share: the open flag, the files left, the form's fields.</summary>
[SupportedOSPlatform("linux")]
internal sealed class ZenityQuestion
{
    private BatchProgress? _latest;
    private int _asking;

    public bool Asking => Volatile.Read(ref _asking) == 1;

    public void Report(BatchProgress progress) => Volatile.Write(ref _latest, progress);

    // Files of the batch not finished and not already answered: what an answer for all would cover.
    public int FilesLeft(int answeredInProgress) =>
        Volatile.Read(ref _latest) is { } progress ? Math.Max(progress.Total - progress.Completed - answeredInProgress, 1) : 1;

    public async Task<ZenityReply> AskAsync(IEnumerable<string> arguments, string zenity, CancellationToken ct)
    {
        Volatile.Write(ref _asking, 1);
        try
        {
            return await ZenityDialog.AskAsync(arguments, ct, zenity).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _asking, 0);
        }
    }

    // zenity reads markup: names show as they are. The reason the last answer was refused goes under the heading.
    public static string Text(string heading, string? problem) =>
        Shown(heading) + (problem is null ? "" : "\n\n" + Shown(problem));

    // Between fields: a character no one types or pastes into a field, so a password pasted with a line break stays one
    // field instead of moving the others along.
    public const string Separator = "--separator=\u001e";

    public const string UnreadableMessage = "Flevert could not read the answer. Type it again.";

    public static IEnumerable<string> ForAll(int files) =>
        files > 1 ? ["--add-combo=Use it for", $"--combo-values=All {files} files|This file only"] : [];

    public static int ForAllFields(int files) => files > 1 ? 1 : 0;

    // As on Windows, where the box starts ticked: anything but "This file only" is for all.
    public static bool ChoseAll(char[]? choice, int files) =>
        files > 1 && !(choice ?? []).AsSpan().SequenceEqual("This file only");

    /// <summary>
    /// The form's <paramref name="count"/> fields, as characters (a password is wiped by whoever keeps it), or <c>null</c>
    /// when the output does not hold exactly that many.
    /// </summary>
    public static List<char[]>? Fields(ReadOnlySpan<byte> output, int count)
    {
        var chars = new char[Encoding.UTF8.GetCharCount(output)];
        Encoding.UTF8.GetChars(output, chars);
        try
        {
            var rest = chars.AsSpan();
            if (rest.EndsWith('\n'))
                rest = rest[..^1];
            if (rest.Count('\u001e') != count - 1)
                return null;
            var fields = new List<char[]>();
            while (true)
            {
                var end = rest.IndexOf('\u001e');
                fields.Add((end < 0 ? rest : rest[..end]).ToArray());
                if (end < 0)
                    return fields;
                rest = rest[(end + 1)..];
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()));
        }
    }

    private static string Shown(string text) => Markup.ForZenity(Markup.Escape(text));
}

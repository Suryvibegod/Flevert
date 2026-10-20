using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Pdf;

/// <summary>
/// pdfium is not thread-safe: one process-wide lock is held around every call into it, closing handles included. The
/// library is initialised on first use and never torn down (it lives as long as the process).
/// </summary>
internal static class Pdfium
{
    public static readonly Lock Gate = new();

    private static bool _initialized;

    /// <summary>Call while holding <see cref="Gate"/>.</summary>
    public static unsafe void EnsureInitialized()
    {
        if (_initialized)
            return;
        var config = new PdfiumNative.LibraryConfig { Version = 2 };
        PdfiumNative.FPDF_InitLibraryWithConfig(&config);
        _initialized = true;
    }
}

/// <summary>An FPDF_DOCUMENT and the file it reads from, which must stay open until the document is closed.</summary>
internal sealed class DocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly FileSource _source;

    public DocumentHandle(nint document, FileSource source) : base(ownsHandle: true)
    {
        SetHandle(document);
        _source = source;
    }

    protected override bool ReleaseHandle()
    {
        lock (Pdfium.Gate)
            PdfiumNative.FPDF_CloseDocument(handle);
        _source.Dispose();
        return true;
    }
}

/// <summary>An FPDF_PAGE. Keeps its document open: pdfium requires pages to be closed before their document.</summary>
internal sealed class PageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly DocumentHandle _document;

    private PageHandle(DocumentHandle document) : base(ownsHandle: true) => _document = document;

    /// <summary>Call while holding <see cref="Pdfium.Gate"/>. Throws when pdfium cannot load the page.</summary>
    public static PageHandle Load(DocumentHandle document, int pageIndex)
    {
        var page = new PageHandle(document);
        var added = false;
        document.DangerousAddRef(ref added);
        var native = PdfiumNative.FPDF_LoadPage(document.DangerousGetHandle(), pageIndex);
        if (native == 0)
        {
            document.DangerousRelease();
            page.SetHandleAsInvalid();
            throw new PdfException(PdfError.DamagedPage, $"Page {pageIndex + 1} of this PDF is damaged and cannot be read.");
        }

        page.SetHandle(native);
        return page;
    }

    protected override bool ReleaseHandle()
    {
        lock (Pdfium.Gate)
            PdfiumNative.FPDF_ClosePage(handle);
        _document.DangerousRelease();
        return true;
    }
}

/// <summary>An FPDF_BITMAP over a buffer Flevert allocated; destroying it leaves the buffer alone.</summary>
internal sealed class BitmapHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public BitmapHandle() : base(ownsHandle: true) { }

    /// <summary>Call while holding <see cref="Pdfium.Gate"/>, with <paramref name="pixels"/> pinned until disposed.</summary>
    public static unsafe BitmapHandle Create(int width, int height, byte* pixels, int stride)
    {
        var bitmap = new BitmapHandle();
        var native = PdfiumNative.FPDFBitmap_CreateEx(width, height, PdfiumNative.BitmapBgra, pixels, stride);
        if (native == 0)
        {
            bitmap.SetHandleAsInvalid();
            throw new InvalidOperationException($"pdfium could not create a {width} x {height} bitmap.");
        }

        bitmap.SetHandle(native);
        return bitmap;
    }

    protected override bool ReleaseHandle()
    {
        lock (Pdfium.Gate)
            PdfiumNative.FPDFBitmap_Destroy(handle);
        return true;
    }
}

/// <summary>
/// Feeds pdfium a file through FPDF_LoadCustomDocument, read with a .NET file handle: .NET opens Unicode and <c>\\?\</c>
/// long paths, and the length checked against the size limit is that of the very file pdfium reads.
/// </summary>
internal sealed unsafe class FileSource : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly long _length;
    private GCHandle _self;
    private PdfiumNative.FileAccess* _access;

    public FileSource(SafeFileHandle file, long length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, uint.MaxValue);
        _file = file;
        _length = length;
        _self = GCHandle.Alloc(this);
        _access = (PdfiumNative.FileAccess*)NativeMemory.Alloc((nuint)sizeof(PdfiumNative.FileAccess));
        *_access = new PdfiumNative.FileAccess
        {
            FileLength = (uint)length,
            GetBlock = &GetBlock,
            Param = GCHandle.ToIntPtr(_self),
        };
    }

    public PdfiumNative.FileAccess* Access => _access;

    public void Dispose()
    {
        if (_access is null)
            return;
        NativeMemory.Free(_access);
        _access = null;
        _self.Free();
        _file.Dispose();
    }

    // Called by pdfium, on the thread that holds the gate, whenever it needs bytes. Nothing may be thrown back into
    // native code: any failure is reported as 0, which pdfium treats as a read error.
    [UnmanagedCallersOnly]
    private static int GetBlock(nint param, uint position, byte* buffer, uint size)
    {
#pragma warning disable CA1031 // An exception must not unwind through pdfium's frames.
        try
        {
            var source = (FileSource)GCHandle.FromIntPtr(param).Target!;
            return source.Read(position, new Span<byte>(buffer, checked((int)size))) ? 1 : 0;
        }
        catch (Exception)
        {
            return 0;
        }
#pragma warning restore CA1031
    }

    private bool Read(long position, Span<byte> buffer)
    {
        if (position + buffer.Length > _length)
            return false;
        while (!buffer.IsEmpty)
        {
            var read = RandomAccess.Read(_file, buffer, position);
            if (read == 0)
                return false; // the file shrank, which FileShare.Read should prevent
            buffer = buffer[read..];
            position += read;
        }

        return true;
    }
}

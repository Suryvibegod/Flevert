using System.Runtime.InteropServices;

// pdfium.dll is loaded from this assembly's folder only (the Cli applies the same rule to everything it loads), never
// from the current directory, which Explorer sets to the folder of the clicked file.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]

namespace Flevert.Pdf;

/// <summary>
/// The pdfium functions Flevert uses, declared from the public headers of chromium/8100 (fpdfview.h). Every call must
/// be made while holding <see cref="Pdfium.Gate"/>: pdfium is not thread-safe.
/// </summary>
internal static unsafe partial class PdfiumNative
{
    private const string Library = "pdfium";

    public const uint ErrSuccess = 0;
    public const uint ErrUnknown = 1;
    public const uint ErrFile = 2;
    public const uint ErrFormat = 3;
    public const uint ErrPassword = 4;
    public const uint ErrSecurity = 5;
    public const uint ErrPage = 6;

    public const int BitmapBgra = 4;

    // Render annotations that need no user interaction, as a viewer shows the page.
    public const int RenderAnnotations = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraryConfig
    {
        public int Version;
        public nint UserFontPaths;
        public nint Isolate;
        public uint V8EmbedderSlot;
    }

    // FPDF_FILEACCESS: unsigned long is 32 bits on Windows, so pdfium reads at most 4 GB this way.
    [StructLayout(LayoutKind.Sequential)]
    public struct FileAccess
    {
        public uint FileLength;
        public delegate* unmanaged<nint, uint, byte*, uint, int> GetBlock;
        public nint Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeF
    {
        public float Width;
        public float Height;
    }

    [LibraryImport(Library)]
    public static partial void FPDF_InitLibraryWithConfig(LibraryConfig* config);

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadCustomDocument(FileAccess* fileAccess, byte* password);

    [LibraryImport(Library)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Library)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, SizeF* size);

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Library)]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    /// <summary>The number of annotations on the page (fpdf_annot.h), or -1 on an error.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFPage_GetAnnotCount(nint page);

    // Reading text: fpdf_text.h.

    [LibraryImport(Library)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Library)]
    public static partial void FPDFText_ClosePage(nint textPage);

    /// <summary>The number of characters on the page, or -1 on an error.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFText_CountChars(nint textPage);

    /// <summary>Writes <paramref name="count"/> UTF-16 code units and a terminating NUL into <paramref name="result"/>,
    /// which holds at least <paramref name="count"/> + 1; returns how many it wrote, the NUL included.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFText_GetText(nint textPage, int startIndex, int count, ushort* result);

    // Reading a page's images: fpdf_edit.h.

    public const int PageObjectImage = 3;
    public const int PageObjectForm = 5;

    public const int BitmapGray = 1;
    public const int BitmapBgrx = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct ImageMetadata
    {
        public uint Width;
        public uint Height;
        public float HorizontalDpi;
        public float VerticalDpi;
        public uint BitsPerPixel;
        public int Colorspace;
        public int MarkedContentId;
    }

    [LibraryImport(Library)]
    public static partial int FPDFPage_CountObjects(nint page);

    [LibraryImport(Library)]
    public static partial nint FPDFPage_GetObject(nint page, int index);

    [LibraryImport(Library)]
    public static partial int FPDFPageObj_GetType(nint pageObject);

    [LibraryImport(Library)]
    public static partial int FPDFFormObj_CountObjects(nint formObject);

    [LibraryImport(Library)]
    public static partial nint FPDFFormObj_GetObject(nint formObject, uint index);

    /// <summary>The DPI is the image's pixels over the size its matrix draws it at; <paramref name="page"/> gives the bit depth and colorspace.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFImageObj_GetImageMetadata(nint imageObject, nint page, ImageMetadata* metadata);

    /// <summary>The image's stream data as stored, filters not applied; returns its length (unsigned long: 32 bits on Windows).</summary>
    [LibraryImport(Library)]
    public static partial uint FPDFImageObj_GetImageDataRaw(nint imageObject, void* buffer, uint bufferLength);

    [LibraryImport(Library)]
    public static partial int FPDFImageObj_GetImageFilterCount(nint imageObject);

    /// <summary>The name of filter <paramref name="index"/>, NUL-terminated ASCII; returns its length with the NUL.</summary>
    [LibraryImport(Library)]
    public static partial uint FPDFImageObj_GetImageFilter(nint imageObject, int index, void* buffer, uint bufferLength);

    /// <summary>The image's pixels, decoded and converted to gray or BGR by pdfium, without its mask or matrix; the caller destroys it.</summary>
    [LibraryImport(Library)]
    public static partial nint FPDFImageObj_GetBitmap(nint imageObject);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_GetWidth(nint bitmap);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_GetHeight(nint bitmap);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_GetFormat(nint bitmap);

    [LibraryImport(Library)]
    public static partial void* FPDFBitmap_GetBuffer(nint bitmap);

    // Creating documents: fpdf_edit.h, fpdf_save.h.

    public const int BitmapBgr = 2;

    // FPDF_SaveAsCopy: write the whole document, never an update appended to an older one.
    public const uint SaveNoIncremental = 1 << 1;

    /// <summary>
    /// FPDF_FILEWRITE (version 1), followed by a field of Flevert's own: pdfium hands the callback a pointer to this
    /// struct, and only reads the first two fields.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileWrite
    {
        public int Version;
        public delegate* unmanaged<FileWrite*, void*, uint, int> WriteBlock;
        public nint Param;
    }

    // fpdf_doc.h. The length in bytes of the tag's UTF-16LE text with its NUL; 0 when there is no Info dictionary.
    [LibraryImport(Library)]
    public static partial uint FPDF_GetMetaText(nint document, byte* tag, void* buffer, uint bufferLength);

    [LibraryImport(Library)]
    public static partial nint FPDFPage_New(nint document, int pageIndex, double width, double height);

    [LibraryImport(Library)]
    public static partial void FPDFPage_Delete(nint document, int pageIndex);

    [LibraryImport(Library)]
    public static partial nint FPDFPageObj_NewImageObj(nint document);

    [LibraryImport(Library)]
    public static partial void FPDFPageObj_Destroy(nint pageObject);

    [LibraryImport(Library)]
    public static partial int FPDFImageObj_LoadJpegFileInline(nint* pages, int count, nint imageObject, FileAccess* fileAccess);

    [LibraryImport(Library)]
    public static partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObject, nint bitmap);

    [LibraryImport(Library)]
    public static partial int FPDFImageObj_GetImagePixelSize(nint imageObject, uint* width, uint* height);

    [LibraryImport(Library)]
    public static partial int FPDFImageObj_SetMatrix(nint imageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Library)]
    public static partial int FPDFPage_InsertObject(nint page, nint pageObject);

    [LibraryImport(Library)]
    public static partial int FPDFPage_GenerateContent(nint page);

    [LibraryImport(Library)]
    public static partial int FPDF_SaveAsCopy(nint document, FileWrite* fileWrite, uint flags);

    // Editing pages of an existing document (v0.2.2 PDF tools): fpdfview.h, fpdf_edit.h, fpdf_ppo.h, fpdf_flatten.h.

    /// <summary>FS_RECTF: note the order, left, top, right, bottom.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    public const int FontTrueType = 2;

    // FPDFPage_Flatten: flatten for printing; the result says whether there was anything to flatten.
    public const int FlattenForPrint = 1;
    public const int FlattenFailed = 0;
    public const int FlattenNothingToDo = 2;

    [LibraryImport(Library)]
    public static partial nint FPDF_CreateNewDocument();

    /// <summary>The page's crop box (within its media box), in its own space, before its /Rotate.</summary>
    [LibraryImport(Library)]
    public static partial int FPDF_GetPageBoundingBox(nint page, RectF* rect);

    /// <summary>The page's /Rotate in quarter turns clockwise: 0 to 3.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFPage_GetRotation(nint page);

    /// <summary>A text object in one of the 14 standard fonts, named in ASCII (<c>Helvetica</c>), which is not embedded.</summary>
    [LibraryImport(Library)]
    public static partial nint FPDFPageObj_NewTextObj(nint document, byte* font, float fontSize);

    [LibraryImport(Library)]
    public static partial nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, int cid);

    [LibraryImport(Library)]
    public static partial void FPDFFont_Close(nint font);

    [LibraryImport(Library)]
    public static partial nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);

    /// <summary><paramref name="text"/> is UTF-16, ended by a NUL.</summary>
    [LibraryImport(Library)]
    public static partial int FPDFText_SetText(nint textObject, ushort* text);

    [LibraryImport(Library)]
    public static partial void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Library)]
    public static partial int FPDFPageObj_SetFillColor(nint pageObject, uint red, uint green, uint blue, uint alpha);

    [LibraryImport(Library)]
    public static partial int FPDFPageObj_GetBounds(nint pageObject, float* left, float* bottom, float* right, float* top);

    [LibraryImport(Library)]
    public static partial int FPDFPage_Flatten(nint page, int flag);

    /// <summary>
    /// A new document of sheets <paramref name="width"/> by <paramref name="height"/>, each holding up to
    /// <paramref name="columns"/> x <paramref name="rows"/> pages of the source in reading order, each page a form XObject
    /// drawing it as shown, scaled to its cell and centred in it. One copier for the whole run: what pages share (fonts,
    /// images) is copied once. size_t: 64 bits.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint FPDF_ImportNPagesToOne(nint source, float width, float height, nuint columns, nuint rows);

    [LibraryImport(Library)]
    public static partial void FPDFPage_SetMediaBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Library)]
    public static partial void FPDFPage_SetCropBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Library)]
    public static partial void FPDFPage_SetBleedBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Library)]
    public static partial void FPDFPage_SetTrimBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Library)]
    public static partial void FPDFPage_SetArtBox(nint page, float left, float bottom, float right, float top);

    /// <summary>FS_MATRIX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Matrix
    {
        public float A;
        public float B;
        public float C;
        public float D;
        public float E;
        public float F;
    }

    /// <summary>
    /// Wraps the page's content in <c>q &lt;clip&gt; re W* n &lt;matrix&gt; cm … Q</c>: the clip in the page's new space,
    /// then the matrix. Annotations are not moved.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int FPDFPage_TransFormWithClip(nint page, Matrix* matrix, RectF* clip);

    [LibraryImport(Library)]
    public static partial int FPDFPage_RemoveAnnot(nint page, int index);
}

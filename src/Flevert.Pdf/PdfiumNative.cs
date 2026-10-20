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
}

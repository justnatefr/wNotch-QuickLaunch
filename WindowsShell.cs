using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace QuickLaunch;

/// <summary>
/// The bits of the Windows shell the plugin uses: an app's icon as a PNG, and the standard
/// "Open" dialog. Everything here returns null instead of throwing when it cannot help, and does
/// nothing on other operating systems.
/// </summary>
public static class WindowsShell
{
    /// <summary>Picture files the notch can draw.</summary>
    public const string PictureFilter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico)\0*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico\0All files (*.*)\0*.*\0";

    /// <summary>Things worth pinning.</summary>
    public const string AppFilter = "Programs and shortcuts (*.exe;*.lnk;*.url;*.bat;*.cmd)\0*.exe;*.lnk;*.url;*.bat;*.cmd\0All files (*.*)\0*.*\0";

    /// <summary>
    /// The icon Explorer shows for <paramref name="target"/> (a file, shortcut, folder, or a link
    /// whose scheme has a registered handler), as PNG bytes about <paramref name="size"/> pixels square.
    /// </summary>
    public static byte[]? IconPng(string target, int size = 64)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return RunSta(() =>
        {
            string path = Environment.ExpandEnvironmentVariables(target);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                // A link: use the icon of the program that opens it, e.g. the browser for https.
                path = HandlerOf(target) ?? "";
                if (path.Length == 0)
                {
                    return null;
                }
            }

            return OperatingSystem.IsWindows() ? ShellIcon(path, size) : null;
        });
    }

    /// <summary>Shows the Windows "Open" dialog and returns the chosen file, or null when cancelled.</summary>
    /// <param name="filter">Pairs of description and pattern, each ending in \0, e.g. <see cref="PictureFilter"/>.</param>
    public static string? PickFile(string title, string filter)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return RunSta(() =>
        {
            const int maxPath = 32768;
            IntPtr buffer = Marshal.AllocHGlobal(maxPath * 2);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    lpstrFilter = filter + "\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = maxPath,
                    lpstrTitle = title,
                    Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnDontAddToRecent,
                };

                return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        });
    }

    // The shell's COM objects expect a single-threaded apartment, which thread-pool threads are not.
    private static T? RunSta<T>(Func<T?> work)
        where T : class
    {
        T? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception e) when (e is COMException or ExternalException or ArgumentException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                result = null;
            }
        })
        {
            IsBackground = true,
            Name = "Quick launch shell",
        };

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        thread.Join();
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? ShellIcon(string path, int size)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory? factory) != 0 || factory is null)
        {
            return null;
        }

        try
        {
            if (factory.GetImage(new NativeSize(size, size), SiigbfIconOnly | SiigbfBiggerSizeOk, out IntPtr bitmap) != 0 || bitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return BitmapToPng(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static byte[]? BitmapToPng(IntPtr bitmap)
    {
        if (GetObjectW(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap info) == 0 || info.bmWidth <= 0 || info.bmHeight == 0)
        {
            return null;
        }

        int width = info.bmWidth;
        int height = Math.Abs(info.bmHeight);
        var header = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height, // top row first
            biPlanes = 1,
            biBitCount = 32,
        };

        byte[] bgra = new byte[width * height * 4];
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, bgra, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            DeleteDC(dc);
        }

        return Png.Encode(width, height, ToStraightRgba(bgra));
    }

    /// <summary>Shell bitmaps are BGRA, usually with premultiplied alpha; PNG wants straight RGBA.</summary>
    internal static byte[] ToStraightRgba(byte[] bgra)
    {
        bool anyAlpha = false;
        bool premultiplied = true;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            anyAlpha |= a != 0;
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a)
            {
                premultiplied = false;
            }
        }

        byte[] rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
            if (!anyAlpha)
            {
                // An old-style bitmap without an alpha channel: everything is opaque.
                a = 255;
            }
            else if (premultiplied && a is > 0 and < 255)
            {
                r = (byte)Math.Min(255, r * 255 / a);
                g = (byte)Math.Min(255, g * 255 / a);
                b = (byte)Math.Min(255, b * 255 / a);
            }

            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = a;
        }

        return rgba;
    }

    /// <summary>The program registered for a link's scheme, e.g. the browser for https://.</summary>
    private static string? HandlerOf(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) || uri.IsFile)
        {
            return null;
        }

        uint length = 1024;
        var buffer = new StringBuilder((int)length);
        int hr = AssocQueryStringW(AssocfIsProtocol, AssocstrExecutable, uri.Scheme, null, buffer, ref length);
        string exe = buffer.ToString();
        return hr == 0 && File.Exists(exe) ? exe : null;
    }

    // ---- Interop ----------------------------------------------------------------------------

    private const int SiigbfBiggerSizeOk = 0x1;
    private const int SiigbfIconOnly = 0x4;
    private const int AssocfIsProtocol = 0x1000;
    private const int AssocstrExecutable = 2;
    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnDontAddToRecent = 0x02000000;

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Cx, int Cy);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(int flags, int str, string assoc, string? extra, StringBuilder output, ref uint length);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}

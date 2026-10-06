using System.Buffers.Binary;
using System.IO.Compression;
using QuickLaunch;

// Exit code 1 when an icon that every Windows machine has cannot be read.
int failures = 0;

// The PNG writer itself, on a known picture: red, green, blue and a see-through pixel.
{
    byte[] known = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0];
    byte[] sample = Png.Encode(2, 2, known);
    File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "icon-png-writer.png"), sample);
    (int w, int h, int opaque, int transparent, int colourful) = Inspect(sample);
    bool ok = (w, h, opaque, transparent, colourful) == (2, 2, 3, 1, 3);
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} PNG writer: {w}x{h}, {opaque} opaque, {transparent} transparent, {colourful} coloured");
    failures += ok ? 0 : 1;
}

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Not Windows: skipping the icon checks.");
    return failures == 0 ? 0 : 1;
}

string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
string? shortcut = Directory.Exists(startMenu)
    ? Directory.EnumerateFiles(startMenu, "*.lnk", SearchOption.AllDirectories).FirstOrDefault()
    : null;

Check("program", Path.Combine(windows, "System32", "notepad.exe"), required: true);
Check("folder", windows, required: true);
Check("environment variable", @"%WINDIR%\System32\cmd.exe", required: true);
if (shortcut is not null)
{
    Check("Start menu shortcut", shortcut, required: true);
}

Check("link", "https://example.com", required: false);
Check("missing file", @"C:\does\not\exist.exe", required: false, expectNull: true);

Console.WriteLine(failures == 0 ? "All icon checks passed." : $"{failures} icon check(s) failed.");
return failures == 0 ? 0 : 1;

void Check(string what, string target, bool required, bool expectNull = false)
{
    byte[]? png = WindowsShell.IconPng(target);
    if (png is null)
    {
        bool ok = expectNull || !required;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}: no icon ({target})");
        failures += ok ? 0 : 1;
        return;
    }

    try
    {
        (int width, int height, int opaque, int transparent, int colourful) = Inspect(png);
        bool ok = !expectNull && width >= 32 && height >= 32 && opaque > 0 && colourful > 0;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}: {width}x{height}, {png.Length} bytes, {opaque} opaque, {transparent} transparent, {colourful} coloured pixels ({target})");
        failures += ok ? 0 : 1;
        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, $"icon-{what.Replace(' ', '-')}.png"), png);
    }
    catch (Exception e)
    {
        Console.WriteLine($"FAIL {what}: not a readable PNG: {e.Message}");
        failures++;
    }
}

// Reads back the PNG the plugin wrote (unfiltered RGBA rows, as Png.Encode writes them).
static (int Width, int Height, int Opaque, int Transparent, int Colourful) Inspect(byte[] png)
{
    if (!png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
    {
        throw new InvalidDataException("bad signature");
    }

    int width = 0, height = 0;
    using var idat = new MemoryStream();
    for (int at = 8; at < png.Length;)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
        string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
        ReadOnlySpan<byte> data = png.AsSpan(at + 8, length);
        if (type == "IHDR")
        {
            width = BinaryPrimitives.ReadInt32BigEndian(data);
            height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        }
        else if (type == "IDAT")
        {
            idat.Write(data);
        }

        at += 12 + length;
    }

    idat.Position = 0;
    using var pixels = new MemoryStream();
    using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
    {
        zlib.CopyTo(pixels);
    }

    byte[] raw = pixels.ToArray();
    int stride = (width * 4) + 1;
    if (raw.Length != stride * height)
    {
        throw new InvalidDataException($"expected {stride * height} bytes of pixels, got {raw.Length}");
    }

    int opaque = 0, transparent = 0, colourful = 0;
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int i = (y * stride) + 1 + (x * 4);
            byte r = raw[i], g = raw[i + 1], b = raw[i + 2], a = raw[i + 3];
            opaque += a == 255 ? 1 : 0;
            transparent += a == 0 ? 1 : 0;
            colourful += a > 0 && (Math.Abs(r - g) > 16 || Math.Abs(g - b) > 16 || Math.Abs(r - b) > 16) ? 1 : 0;
        }
    }

    return (width, height, opaque, transparent, colourful);
}

#:package Svg.Skia@3.0.6

// Renders src/Marginal.App/Assets/icon.svg into the two files the app embeds:
//
//   app.ico       the exe's own icon on Windows: Explorer, the taskbar, the
//                 Start Menu shortcut and the Installed-apps entry
//   app-icon.png  256 px, which the Linux installer writes into the hicolor
//                 theme for the .desktop entry
//
// AlbumWall's tools/make_icon.py does the same with ImageMagick; this needs
// only the .NET SDK, on any of the three platforms:
//
//   dotnet run tools/make-icon.cs
//
// Every size is drawn FROM THE SVG AT THAT SIZE, not shrunk from the big one:
// a 16 px frame resampled from 256 px is mush. The outputs are committed, so
// building the app never needs this.

using SkiaSharp;
using Svg.Skia;

var assets = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string
    ?? Directory.GetCurrentDirectory(), "..", "src", "Marginal.App", "Assets"));
var svgPath = Path.Combine(assets, "icon.svg");

// What Windows asks an .ico for, smallest to largest. 256 is also the Linux icon.
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

using var svg = new SKSvg();
var picture = svg.Load(svgPath) ?? throw new InvalidOperationException($"could not read {svgPath}");
var bounds = picture.CullRect;

var pngs = sizes.Select(size =>
{
    using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / bounds.Width, size / bounds.Height);
        canvas.DrawPicture(picture);
    }
    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    return (Size: size, Bytes: data.ToArray());
}).ToList();

File.WriteAllBytes(Path.Combine(assets, "app-icon.png"), pngs[^1].Bytes);

// An .ico of PNG frames (Windows Vista onwards): a 6-byte header, a 16-byte
// directory entry per frame, then the frames. A width or height of 256 is
// written as 0.
using (var ico = new BinaryWriter(File.Create(Path.Combine(assets, "app.ico"))))
{
    ico.Write((ushort)0); ico.Write((ushort)1); ico.Write((ushort)pngs.Count);
    var offset = 6 + 16 * pngs.Count;
    foreach (var (size, bytes) in pngs)
    {
        ico.Write((byte)(size >= 256 ? 0 : size)); ico.Write((byte)(size >= 256 ? 0 : size));
        ico.Write((byte)0); ico.Write((byte)0);           // no palette, reserved
        ico.Write((ushort)1); ico.Write((ushort)32);      // planes, bits per pixel
        ico.Write(bytes.Length); ico.Write(offset);
        offset += bytes.Length;
    }
    foreach (var (_, bytes) in pngs) ico.Write(bytes);
}

Console.WriteLine($"app.ico ({string.Join(", ", sizes)} px) and app-icon.png written to {assets}");

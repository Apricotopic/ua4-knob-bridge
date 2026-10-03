using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 2)
    throw new ArgumentException("Usage: IconBuilder <source-png> <target-ico>");

using var source = new Bitmap(args[0]);
Rectangle markBounds = FindBlueMarkBounds(source);
Rectangle wordBounds = FindWordmarkBounds(source, markBounds.Bottom);
using var mark = source.Clone(markBounds, PixelFormat.Format32bppArgb);
using var wordmark = source.Clone(wordBounds, PixelFormat.Format32bppArgb);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
List<byte[]> pngs = sizes.Select(size => RenderPng(mark, wordmark, size)).ToList();

using var output = new FileStream(args[1], FileMode.Create, FileAccess.Write, FileShare.None);
using var writer = new BinaryWriter(output);
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)pngs.Count);
int offset = 6 + pngs.Count * 16;
for (int index = 0; index < pngs.Count; index++)
{
    int size = sizes[index];
    writer.Write((byte)(size == 256 ? 0 : size));
    writer.Write((byte)(size == 256 ? 0 : size));
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(pngs[index].Length);
    writer.Write(offset);
    offset += pngs[index].Length;
}
foreach (byte[] png in pngs) writer.Write(png);

Console.WriteLine($"Created {args[1]} with stacked Shanling mark and wordmark; sizes={string.Join(',', sizes)}");

static Rectangle FindBlueMarkBounds(Bitmap source)
{
    int left = source.Width, top = source.Height, right = -1, bottom = -1;
    for (int y = 0; y < source.Height; y++)
    for (int x = 0; x < source.Width; x++)
    {
        Color pixel = source.GetPixel(x, y);
        if (!IsBlueMark(pixel)) continue;
        left = Math.Min(left, x); top = Math.Min(top, y);
        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
    }
    if (right < left) throw new InvalidDataException("No blue Shanling mark was found.");
    return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
}

static Rectangle FindWordmarkBounds(Bitmap source, int afterY)
{
    int left = source.Width, top = source.Height, right = -1, bottom = -1;
    for (int y = afterY; y < source.Height; y++)
    for (int x = 0; x < source.Width; x++)
    {
        Color pixel = source.GetPixel(x, y);
        if (pixel.A < 32 || IsBlueMark(pixel)) continue;
        left = Math.Min(left, x); top = Math.Min(top, y);
        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
    }
    if (right < left) throw new InvalidDataException("No Shanling wordmark was found.");
    return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
}

static bool IsBlueMark(Color pixel) =>
    pixel.A >= 32 && pixel.B >= pixel.R * 1.15 && pixel.B >= pixel.G * 1.05;

static byte[] RenderPng(Image mark, Image wordmark, int size)
{
    using var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (Graphics graphics = Graphics.FromImage(output))
    {
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        // Tuned against 32 px and 48 px Windows taskbar rendering: preserve
        // the complete wordmark while moving the visual center downward.
        DrawCentered(graphics, mark, size, size * 0.99f, size * 0.20f);
        DrawCentered(graphics, wordmark, size, size * 0.90f, size * 0.78f);
    }
    using var stream = new MemoryStream();
    output.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}

static void DrawCentered(Graphics graphics, Image source, int canvasSize, float targetWidth, float top)
{
    float scale = targetWidth / source.Width;
    int width = (int)Math.Round(source.Width * scale);
    int height = (int)Math.Round(source.Height * scale);
    graphics.DrawImage(source, (canvasSize - width) / 2, (int)Math.Round(top), width, height);
}

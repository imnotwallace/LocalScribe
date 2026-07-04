using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

// LocalScribe mark: rounded accent-blue tile + white microphone + baseline (transcript) strokes.
static Bitmap Render(int s)
{
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    float pad = s * 0.06f, r = s * 0.22f;
    var tile = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
    using (var path = RoundedRect(tile, r))
    using (var fill = new LinearGradientBrush(tile, Color.FromArgb(0x2B, 0x88, 0xD8),
               Color.FromArgb(0x1A, 0x5F, 0xB4), LinearGradientMode.ForwardDiagonal))
        g.FillPath(fill, path);

    using var white = new SolidBrush(Color.White);
    // Mic capsule.
    float mw = s * 0.20f, mh = s * 0.34f, cx = s * 0.5f, top = s * 0.24f;
    g.FillPath(white, RoundedRect(new RectangleF(cx - mw / 2, top, mw, mh), mw / 2));
    // Mic arc + stand.
    using var pen = new Pen(white, MathF.Max(1f, s * 0.045f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    float ar = s * 0.16f, ay = top + mh - ar;
    g.DrawArc(pen, cx - ar, ay, 2 * ar, 2 * ar, 20, 140);
    g.DrawLine(pen, cx, ay + ar, cx, ay + ar + s * 0.08f);
    g.DrawLine(pen, cx - s * 0.10f, ay + ar + s * 0.08f, cx + s * 0.10f, ay + ar + s * 0.08f);
    return bmp;
}

static GraphicsPath RoundedRect(RectangleF b, float r)
{
    var p = new GraphicsPath();
    p.AddArc(b.X, b.Y, 2 * r, 2 * r, 180, 90);
    p.AddArc(b.Right - 2 * r, b.Y, 2 * r, 2 * r, 270, 90);
    p.AddArc(b.Right - 2 * r, b.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
    p.AddArc(b.X, b.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
    p.CloseFigure();
    return p;
}

int[] sizes = { 16, 32, 48, 64, 128, 256 };
var frames = sizes.Select(Render).ToArray();
var pngs = frames.Select(f => { using var ms = new MemoryStream(); f.Save(ms, ImageFormat.Png); return ms.ToArray(); }).ToArray();

string outPath = args.Length > 0 ? args[0] : "LocalScribe.ico";
using var fs = new FileStream(outPath, FileMode.Create);
using var w = new BinaryWriter(fs);
w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);   // ICONDIR
int offset = 6 + 16 * sizes.Length;
for (int i = 0; i < sizes.Length; i++)                                 // ICONDIRENTRY[]
{
    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));                   // width  (0 == 256)
    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));                   // height
    w.Write((byte)0); w.Write((byte)0);                               // colors, reserved
    w.Write((short)1); w.Write((short)32);                            // planes, bpp
    w.Write(pngs[i].Length); w.Write(offset);
    offset += pngs[i].Length;
}
foreach (var png in pngs) w.Write(png);
Console.WriteLine($"Wrote {outPath} ({sizes.Length} frames)");

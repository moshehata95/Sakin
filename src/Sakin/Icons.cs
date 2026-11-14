using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Sakin;

/// <summary>
/// The mark: a still horizon with a point resting above it. Drawn in code so the
/// tray icon can change with state (orange = working, grey = paused) and stay crisp
/// at every DPI.
/// </summary>
internal static class Icons
{
    static readonly Color Ink = Color.FromArgb(0x1D, 0x1C, 0x1A);
    static readonly Color Paper = Color.FromArgb(0xEC, 0xE8, 0xDF);
    static readonly Color Signal = Color.FromArgb(0xE4, 0x53, 0x1F);
    static readonly Color Muted = Color.FromArgb(0x96, 0x91, 0x8A);

    public static Bitmap Draw(int s, bool paused = false)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float r = s * 0.24f;
        using (var path = Rounded(new RectangleF(0, 0, s, s), r))
        using (var b = new SolidBrush(Ink)) g.FillPath(b, path);

        float th = Math.Max(1.6f, s * 0.075f);
        using (var pen = new Pen(Paper, th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, s * 0.25f, s * 0.64f, s * 0.75f, s * 0.64f);

        float d = Math.Max(3.2f, s * 0.22f);
        using (var b = new SolidBrush(paused ? Muted : Signal))
            g.FillEllipse(b, s * 0.5f - d / 2, s * 0.40f - d / 2, d, d);
        return bmp;
    }

    public static Icon Tray(bool paused, int size)
    {
        using var bmp = Draw(size, paused);
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        return icon;
    }

    static GraphicsPath Rounded(RectangleF rc, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(rc.X, rc.Y, d, d, 180, 90);
        p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
        p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
        p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Writes a multi-size .ico (PNG frames). Used once by the build script.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var frames = sizes.Select(sz =>
        {
            using var b = Draw(sz);
            using var ms = new MemoryStream();
            b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) w.Write(f);
    }
}

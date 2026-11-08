// Builds the Steam library artwork for "Rolling Quest VR" from the game's own art (itch.io page images and the
// in-game logo). Usage: MakeArt.exe <sourceDir> <outDir>
//   header.jpg  920x430   wide capsule / SteamVR library tile     capsule.jpg 600x900   portrait capsule
//   hero.jpg    3840x1240 library background                      logo.png    transparent logo shown over the hero
//   icon.png    256x256   shortcut icon
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeArt
{
    static readonly Color Orange = Color.FromArgb(255, 140, 26);

    static Bitmap Canvas(int w, int h, out Graphics g)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(b);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return b;
    }

    // Scales the image to cover the rectangle (cropping the overflow), centred at (fx, fy) of the source.
    static void Cover(Graphics g, Image src, Rectangle dst, float fx = 0.5f, float fy = 0.5f)
    {
        float s = Math.Max((float)dst.Width / src.Width, (float)dst.Height / src.Height);
        float w = dst.Width / s, h = dst.Height / s;
        float x = Math.Max(0, Math.Min(src.Width - w, src.Width * fx - w / 2)), y = Math.Max(0, Math.Min(src.Height - h, src.Height * fy - h / 2));
        g.DrawImage(src, dst, new RectangleF(x, y, w, h), GraphicsUnit.Pixel);
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
        p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
        p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Orange "VR" badge with a dark outline, its top-left at (x, y), the given height.
    static RectangleF Badge(Graphics g, float x, float y, float h)
    {
        using (var f = new Font("Arial Black", h * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            var sz = g.MeasureString("VR", f);
            var r = new RectangleF(x, y, sz.Width + h * 0.35f, h);
            using (var path = Rounded(r, h * 0.22f))
            using (var fill = new SolidBrush(Orange))
            using (var edge = new Pen(Color.FromArgb(200, 0, 0, 0), Math.Max(2f, h * 0.05f)))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("VR", f, Brushes.White, new RectangleF(r.X, r.Y + h * 0.03f, r.Width, r.Height), sf);
            return r;
        }
    }

    static void Save(Bitmap b, string path)
    {
        if (path.EndsWith(".png")) { b.Save(path, ImageFormat.Png); return; }
        var enc = Array.Find(ImageCodecInfo.GetImageEncoders(), e => e.MimeType == "image/jpeg");
        var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
        using (var flat = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(flat)) g.DrawImage(b, 0, 0, b.Width, b.Height);
            flat.Save(path, enc, ps);
        }
        Console.WriteLine("  " + Path.GetFileName(path) + " " + b.Width + "x" + b.Height);
    }

    static int Main(string[] a)
    {
        string src = a[0], outDir = a[1];
        Directory.CreateDirectory(outDir);
        var banner = Image.FromFile(Path.Combine(src, "img7.png"));    // 1920x800 title art
        var cover = Image.FromFile(Path.Combine(src, "img8.png"));     // 1024x1024 title art
        var shot = Image.FromFile(Path.Combine(src, "img1.jpg"));      // gameplay screenshot
        var logo = Image.FromFile(Path.Combine(src, "RollingQuestLogo.png"));   // in-game white logo, 2048x170

        Graphics g;
        // Wide capsule: the title banner with the badge in the lower right.
        using (var b = Canvas(920, 430, out g))
        {
            Cover(g, banner, new Rectangle(0, 0, 920, 430));
            Badge(g, 920 - 150, 430 - 92, 70);
            g.Dispose(); Save(b, Path.Combine(outDir, "header.jpg"));
        }
        // Portrait capsule: blurred-looking backdrop of the cover, the full cover square in the middle, badge below.
        using (var b = Canvas(600, 900, out g))
        {
            // The square cover in the upper part; above it the starry sky is stretched, below it the lava is mirrored,
            // so the art continues without seams.
            // Night sky above, glowing lava-red below; the cover square fades into both.
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 600, 900), Color.Black, Color.Black, 90f))
            {
                var blend = new ColorBlend(4);
                blend.Colors = new[] { Color.FromArgb(4, 2, 6), Color.FromArgb(10, 4, 6), Color.FromArgb(120, 25, 8), Color.FromArgb(45, 8, 2) };
                blend.Positions = new[] { 0f, 0.3f, 0.8f, 1f };
                bg.InterpolationColors = blend;
                g.FillRectangle(bg, 0, 0, 600, 900);
            }
            using (var sq = new Bitmap(600, 600, PixelFormat.Format32bppArgb))
            {
                using (var sg = Graphics.FromImage(sq)) { sg.InterpolationMode = InterpolationMode.HighQualityBicubic; sg.DrawImage(cover, new Rectangle(0, 0, 600, 600)); }
                const int fade = 110;
                for (int y = 0; y < 600; y++)
                {
                    float t = Math.Min(1f, Math.Min((y + 1f) / fade, (600f - y) / fade));
                    if (t >= 1f) continue;
                    t = t * t * (3 - 2 * t);
                    for (int x = 0; x < 600; x++) { var c = sq.GetPixel(x, y); sq.SetPixel(x, y, Color.FromArgb((int)(255 * t), c)); }
                }
                g.DrawImage(sq, 0, 110);
            }
            float bw;
            using (var f = new Font("Arial Black", 110 * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel)) bw = g.MeasureString("VR", f).Width + 110 * 0.35f;
            Badge(g, (600 - bw) / 2, 735, 110);
            g.Dispose(); Save(b, Path.Combine(outDir, "capsule.jpg"));
        }
        // Library background (no logo; Steam draws logo.png over it).
        using (var b = Canvas(3840, 1240, out g))
        {
            Cover(g, shot, new Rectangle(0, 0, 3840, 1240), 0.5f, 0.5f);
            g.Dispose(); Save(b, Path.Combine(outDir, "hero.jpg"));
        }
        // Logo: the game's own logo plus the badge, transparent.
        using (var b = Canvas(2048 + 60 + 330, 240, out g))
        {
            using (var sh = new ImageAttributes())
            {
                // dark shadow copy for legibility on bright backgrounds
                var cm = new ColorMatrix(new[] { new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0.6f, 0 }, new float[] { 0, 0, 0, 0, 1 } });
                sh.SetColorMatrix(cm);
                g.DrawImage(logo, new Rectangle(6, 41, 2048, 170), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, sh);
            }
            g.DrawImage(logo, new Rectangle(0, 35, 2048, 170));
            Badge(g, 2048 + 60, 20, 200);
            g.Dispose(); Save(b, Path.Combine(outDir, "logo.png"));
        }
        // Icon: the cover's title area with a small badge.
        using (var b = Canvas(256, 256, out g))
        {
            g.DrawImage(cover, new Rectangle(0, 0, 256, 256), new Rectangle(0, 300, 1024, 1024 - 300), GraphicsUnit.Pixel);
            Badge(g, 256 - 112, 256 - 66, 58);
            g.Dispose(); Save(b, Path.Combine(outDir, "icon.png"));
        }
        return 0;
    }
}

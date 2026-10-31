using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace IconGen;

/// <summary>
/// 把整幅方形插画重新裁成 Windows 11 的圆角磁贴（squircle）。
///
/// 原图是一张 AI 生成的 3D 插画，奶油色背景铺满整个画布、四角是直角，
/// 放进任务栏/开始菜单的圆角格子里就显得"方"。这里做三件事：
///   1. 用超椭圆（|x|^n + |y|^n = 1，n≈4）把轮廓裁成 Windows 的 squircle；
///   2. 四周留 4–6% 透明边，磁贴不会顶到格子边缘；
///   3. 小尺寸放大主体（16–32px 时细节本来就是糊的，不如让机器人占满）。
///
/// 4× 超采样后再缩回去，等于给边缘做抗锯齿——GDI+ 的裁剪路径本身不抗锯齿。
/// </summary>
internal static class Program
{
    private const double SuperellipsePower = 4.0;
    private static readonly PointF Focus = new(0.52f, 0.45f);

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: icongen <source.png> <repo-root>");
            return 2;
        }

        var sourcePath = args[0];
        var root = args[1];
        using var source = new Bitmap(sourcePath);

        var written = 0;
        var assetsDir = Path.Combine(root, "DutyAgent.Client.Package", "Assets");
        foreach (var file in Directory.GetFiles(assetsDir, "*.png"))
        {
            var name = Path.GetFileName(file);
            // 只重做真正会出现在任务栏/开始菜单/商店页的那几张。
            // MedTile/WideTile 是 Win10 时代的大磁贴，Win11 不展示，保持原样。
            if (!name.StartsWith("AppList", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("StoreLogo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var size = ResolveSize(name);
            if (size <= 0)
            {
                Console.WriteLine($"  skip (no size in name): {name}");
                continue;
            }

            Render(source, size, file);
            written++;
        }

        Render(source, 512, Path.Combine(root, "icon.png"));
        Render(source, 512, Path.Combine(root, "duty-agent-ui", "src", "assets", "icon.png"));
        written += 2;

        Preview(source, Path.Combine(root, ".msix-work", "icon-preview.png"));
        Console.WriteLine($"written {written} files");
        return 0;
    }

    /// <summary>对比图：上排原图（直角整幅），下排新图标（squircle），同一个深色底。</summary>
    private static void Preview(Bitmap source, string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128 };
        const int pad = 24;
        var width = sizes.Sum(s => s + pad) + pad;
        var height = 128 * 2 + pad * 3;

        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(0x20, 0x20, 0x20));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            for (var row = 0; row < 2; row++)
            {
                var x = pad;
                var top = pad + row * (128 + pad);
                foreach (var size in sizes)
                {
                    using var cell = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                    using (var cg = Graphics.FromImage(cell))
                    {
                        cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        if (row == 0)
                        {
                            cg.DrawImage(source, new Rectangle(0, 0, size, size));
                        }
                        else
                        {
                            using var masked = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                            using (var mg = Graphics.FromImage(masked))
                            {
                                mg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                var margin = size * (size >= 48 ? 0.06 : 0.05);
                                var content = size - margin * 2;
                                using var clip = Superellipse(new RectangleF((float)margin, (float)margin, (float)content, (float)content));
                                mg.SetClip(clip);
                                var zoom = size <= 24 ? 1.22 : size <= 32 ? 1.12 : size <= 44 ? 1.04 : 1.0;
                                var draw = content * zoom;
                                var left = size / 2.0 - Focus.X * draw;
                                var top2 = size / 2.0 - Focus.Y * draw;
                                mg.DrawImage(source, new Rectangle((int)left, (int)top2, (int)draw, (int)draw));
                            }

                            cg.DrawImage(masked, new Rectangle(0, 0, size, size));
                        }
                    }

                    g.DrawImage(cell, new Rectangle(x, top + (128 - size), size, size));
                    x += size + pad;
                }
            }
        }

        sheet.Save(path, ImageFormat.Png);
    }

    /// <summary>targetsize-48 / scale-125(=44×1.25) 之类，从文件名推尺寸。</summary>
    private static int ResolveSize(string name)
    {
        var m = Regex.Match(name, @"targetsize-(\d+)");
        if (m.Success)
        {
            return int.Parse(m.Groups[1].Value);
        }

        var s = Regex.Match(name, @"scale-(\d+)");
        if (s.Success)
        {
            // scale 变体的基准是 44（AppList）/ 50（StoreLogo）。
            var factor = int.Parse(s.Groups[1].Value) / 100.0;
            var baseline = name.StartsWith("StoreLogo", StringComparison.OrdinalIgnoreCase) ? 50 : 44;
            return (int)Math.Round(baseline * factor);
        }

        if (name.Equals("AppList.png", StringComparison.OrdinalIgnoreCase))
        {
            return 44;
        }

        if (name.Equals("StoreLogo.png", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        return 0;
    }

    private static void Render(Bitmap source, int size, string path)
    {
        // 小尺寸多留一点边、放大主体；大尺寸按标准网格。
        var marginRatio = size >= 48 ? 0.06 : size >= 32 ? 0.05 : 0.04;
        var zoom = size <= 24 ? 1.22 : size <= 32 ? 1.12 : size <= 44 ? 1.04 : 1.0;
        var supersample = size <= 64 ? 4 : 2;

        var big = size * supersample;
        using var canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var margin = big * marginRatio;
            var content = big - margin * 2;
            using var clip = Superellipse(new RectangleF((float)margin, (float)margin, (float)content, (float)content));
            g.SetClip(clip);

            var draw = content * zoom;
            var centerX = big / 2.0;
            var centerY = big / 2.0;
            var left = centerX - Focus.X * draw;
            var top = centerY - Focus.Y * draw;
            g.DrawImage(source, new Rectangle((int)left, (int)top, (int)draw, (int)draw));
        }

        using var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, size, size), new Rectangle(0, 0, big, big), GraphicsUnit.Pixel);
        }

        output.Save(path, ImageFormat.Png);
    }

    /// <summary>Windows 11 的图标轮廓是超椭圆，不是普通圆角矩形。</summary>
    private static GraphicsPath Superellipse(RectangleF rect)
    {
        const int steps = 240;
        var points = new PointF[steps];
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var a = rect.Width / 2;
        var b = rect.Height / 2;
        var exponent = 2.0 / SuperellipsePower;

        for (var i = 0; i < steps; i++)
        {
            var t = 2 * Math.PI * i / steps;
            var ct = Math.Cos(t);
            var st = Math.Sin(t);
            var x = cx + a * Math.Sign(ct) * Math.Pow(Math.Abs(ct), exponent);
            var y = cy + b * Math.Sign(st) * Math.Pow(Math.Abs(st), exponent);
            points[i] = new PointF((float)x, (float)y);
        }

        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }
}

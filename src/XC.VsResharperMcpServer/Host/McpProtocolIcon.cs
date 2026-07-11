using System.Windows;
using System.Windows.Media;
using JetBrains.Application.Icons.ImageSourceIcons;
using JetBrains.UI.Icons;

namespace XC.VsResharperMcpServer.Host
{
    // The official Model Context Protocol mark (https://modelcontextprotocol.io/favicon.svg,
    // 180x180, originally a black rounded-square background + white interlocking-link glyph),
    // reproduced here as pure WPF vector geometry rather than an embedded raster/SVG asset - no
    // image-conversion tooling, no extra shipped file, and it stays crisp at any status bar
    // DPI/scale. The three path "d" strings below are copied verbatim from the official SVG; WPF's
    // Geometry.Parse mini-language is a documented superset of the same M/L/C path-command syntax
    // SVG uses, so they parse unmodified. Wrapped as an ImageSourceIconId
    // (JetBrains.Application.Icons.ImageSourceIcons -  a real, [ShellComponent]-backed IconId
    // subtype with its own registered IIconIdOwner, confirmed via decompilation, not a custom
    // automation content type needing per-frontend view registration like the earlier
    // RichTextAutomation attempt that silently failed to render - see docs/DEVNOTES.md).
    //
    // Background dropped to transparent (not the official mark's solid black) after the first live
    // render showed a solid black square against ReSharper's actual status bar - see
    // docs/DEVNOTES.md. Glyph stroke stays white (the official mark's actual color) - a first attempt
    // switched it to black assuming a light/grey status bar, but the real status bar is dark, so white
    // is correct. NOT theme-aware: ImageSourceIconIdOwner.TryGetImage (decompiled) ignores its own
    // IconTheme parameter and always returns the same fixed ImageSource, so this fixed
    // white-on-transparent glyph will have poor contrast if VS ever renders a light-themed status bar
    // - not addressed this round.
    public static class McpProtocolIcon
    {
        // Original SVG is 180x180; DrawingImage's intrinsic size comes from Drawing.Bounds, which
        // WPF computes POST-transform - so scaling the DrawingGroup itself down to a standard small
        // status-bar-icon footprint (16x16, matching the size of the "R#" icon already visible at the
        // left of the status bar) is enough, no need to rescale the path coordinates themselves.
        // First live test rendered this at its native 180x180 size (way oversized, overflowing the
        // status bar) since nothing constrained it - see docs/DEVNOTES.md.
        private const double SourceSize = 180.0;
        private const double TargetSize = 16.0;

        private static readonly string[] GlyphPaths =
        {
            "M23.5996 85.2532L86.2021 22.6507C94.8457 14.0071 108.86 14.0071 117.503 22.6507" +
            "C126.147 31.2942 126.147 45.3083 117.503 53.9519L70.2254 101.23",
            "M70.8789 100.578L117.504 53.952C126.148 45.3083 140.163 45.3083 148.806 53.952" +
            "L149.132 54.278C157.776 62.9216 157.776 76.9357 149.132 85.5792L92.5139 142.198" +
            "C89.6327 145.079 89.6327 149.75 92.5139 152.631L104.14 164.257",
            "M101.853 38.3013L55.553 84.6011C46.9094 93.2447 46.9094 107.258 55.553 115.902" +
            "C64.1966 124.546 78.2106 124.546 86.8543 115.902L133.154 69.6025"
        };

        private static IconId _cached;

        public static IconId Instance => _cached ?? (_cached = Build());

        private static IconId Build()
        {
            var group = new DrawingGroup();

            // Transparent, not Brushes.Black - still needed to anchor DrawingGroup's Bounds at a
            // consistent (0,0)-(180,180) origin/size (the glyph paths alone don't span the full
            // canvas, so omitting this rect entirely would shift Bounds - and therefore the
            // ScaleTransform math below - unpredictably).
            group.Children.Add(new GeometryDrawing(
                Brushes.Transparent,
                null,
                new RectangleGeometry(new Rect(0, 0, SourceSize, SourceSize), 24, 24)));

            var pen = new Pen(Brushes.White, 11.0667)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };

            foreach (var pathData in GlyphPaths)
                group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(pathData)));

            var scale = TargetSize / SourceSize;
            group.Transform = new ScaleTransform(scale, scale);

            var image = new DrawingImage(group);
            image.Freeze();

            return new ImageSourceIconId(image);
        }
    }
}

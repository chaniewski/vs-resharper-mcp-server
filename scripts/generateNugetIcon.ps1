# Renders the same official MCP mark vector geometry used for the VS status bar indicator
# (src/XC.VsResharperMcpServer/Host/McpProtocolIcon.cs) to a PNG for use as the NuGet package icon.
#
# Unlike the status bar variant (transparent background + white glyph, since it sits directly on
# ReSharper's own status bar without its own background), the package icon uses the official mark's
# real colors (black rounded-square background + white glyph) - a package icon is a self-contained
# badge shown against an arbitrary host UI (nuget.org, Visual Studio's NuGet browser), so it needs its
# own background the way the status bar variant deliberately doesn't.
#
# No external image-conversion tooling is available on this machine (no ImageMagick/Inkscape/
# rsvg-convert/cairosvg - see docs/DEVNOTES.md) - renders directly via WPF's RenderTargetBitmap +
# PngBitmapEncoder instead, using PresentationCore/PresentationFramework/WindowsBase (.NET Framework
# GAC assemblies, always available, no new dependency).

Add-Type -ReferencedAssemblies PresentationCore, PresentationFramework, WindowsBase, System.Xaml -TypeDefinition @"
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class McpIconRenderer
{
    private const double SourceSize = 180.0;

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

    public static void Render(string outputPath, int pixelSize)
    {
        var group = new DrawingGroup();

        // Real official-mark background (black), unlike the transparent status bar variant.
        group.Children.Add(new GeometryDrawing(
            Brushes.Black,
            null,
            new RectangleGeometry(new Rect(0, 0, SourceSize, SourceSize), 24, 24)));

        var pen = new Pen(Brushes.White, 11.0667)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        foreach (var pathData in GlyphPaths)
            group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(pathData)));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var scale = pixelSize / SourceSize;
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(group);
        }

        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var stream = new FileStream(outputPath, FileMode.Create))
            encoder.Save(stream);
    }
}
"@

$outputPath = Join-Path $PSScriptRoot "..\assets\icon.png"
$outputPath = [System.IO.Path]::GetFullPath($outputPath)
[McpIconRenderer]::Render($outputPath, 256)
Write-Host "Rendered icon to $outputPath"
Get-Item $outputPath | Select-Object Name, Length

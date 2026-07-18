using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using HDRSnip.Models;
using HDRSnip.Views;

namespace HDRSnip.Services;

/// <summary>
/// Renders UI windows and marketing art for Microsoft Store listing upload.
/// </summary>
public static class StoreAssetExporter
{
    public static void Export(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var shots = Path.Combine(outputDir, "screenshots");
        var logos = Path.Combine(outputDir, "logos");
        Directory.CreateDirectory(shots);
        Directory.CreateDirectory(logos);

        var config = AppConfig.Load();
        config.OpenEditorAfterCapture = false;

        // 1) Settings
        var settings = new SettingsWindow(config)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false
        };
        settings.Show();
        settings.UpdateLayout();
        ForceRender(settings);
        SaveWindow(settings, Path.Combine(shots, "01-settings.png"), 1920, 1080);
        settings.Close();

        // 2) Mode toolbar
        var toolbar = new SnipToolbarWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false
        };
        toolbar.Show();
        toolbar.UpdateLayout();
        ForceRender(toolbar);
        SaveWindowOnCanvas(toolbar, Path.Combine(shots, "02-toolbar.png"), 1920, 1080, "Choose snip mode");
        toolbar.Close();

        // 3) Editor with sample capture
        var sample = CreateSampleCaptureBitmap(1280, 720);
        var capture = new ScreenCaptureService(config);
        var editor = new EditorWindow(sample, wasHdr: true, capture, savedPath: null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Width = 960,
            Height = 640,
            ShowInTaskbar = false
        };
        editor.Show();
        editor.UpdateLayout();
        ForceRender(editor);
        SaveWindow(editor, Path.Combine(shots, "03-editor.png"), 1920, 1080);
        editor.Close();

        // 4) Marketing feature board (counts as landscape screenshot)
        SaveFeatureBoard(Path.Combine(shots, "04-features.png"), 1920, 1080);

        // Logos
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
        if (!File.Exists(logoPath))
        {
            // From project when running from bin
            logoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "logo.png"));
        }

        // Prefer packaged resource stream
        ExportLogosFromResource(logos);

        File.WriteAllText(Path.Combine(outputDir, "LISTING-COPY.txt"), ListingCopy);
        MessageBox.Show(
            $"Store assets exported to:\n{outputDir}\n\nUpload screenshots + logos from that folder.",
            "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void ExportLogosFromResource(string logosDir)
    {
        BitmapSource? logo = null;
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/logo.png");
            logo = new BitmapImage(uri);
            logo.Freeze();
        }
        catch
        {
            // fallback solid
        }

        SaveLogoArt(logosDir, "poster-9x16-720x1080.png", 720, 1080, logo);
        SaveLogoArt(logosDir, "poster-9x16-1440x2160.png", 1440, 2160, logo);
        SaveLogoArt(logosDir, "boxart-1x1-1080.png", 1080, 1080, logo);
        SaveLogoArt(logosDir, "boxart-1x1-2160.png", 2160, 2160, logo);
        SaveLogoArt(logosDir, "tile-300x300.png", 300, 300, logo, darkOnly: true);
        SaveLogoArt(logosDir, "tile-150x150.png", 150, 150, logo, darkOnly: true);
        SaveLogoArt(logosDir, "tile-71x71.png", 71, 71, logo, darkOnly: true);
        SaveLogoArt(logosDir, "superhero-16x9-1920x1080.png", 1920, 1080, logo, includeTitle: false);
    }

    private static void SaveLogoArt(
        string dir, string name, int w, int h, BitmapSource? logo,
        bool darkOnly = false, bool includeTitle = true)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(
                Color.FromRgb(0x12, 0x12, 0x14),
                Color.FromRgb(0x1E, 0x1E, 0x24),
                90);
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

            // Accent corner glow
            var accent = new RadialGradientBrush(Color.FromArgb(60, 76, 194, 255), Color.FromArgb(0, 0, 0, 0))
            {
                Center = new Point(0.85, 0.15),
                RadiusX = 0.55,
                RadiusY = 0.45
            };
            dc.DrawRectangle(accent, null, new Rect(0, 0, w, h));

            double logoSize = Math.Min(w, h) * (darkOnly ? 0.62 : 0.42);
            if (logo is not null)
            {
                double lx = (w - logoSize) / 2;
                double ly = includeTitle ? h * 0.22 : (h - logoSize) / 2;
                // Opaque plate so PNG alpha never shows as a checkerboard in Store assets.
                var plate = new Rect(lx - logoSize * 0.06, ly - logoSize * 0.06, logoSize * 1.12, logoSize * 1.12);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), null, plate, logoSize * 0.12, logoSize * 0.12);
                dc.DrawImage(logo, new Rect(lx, ly, logoSize, logoSize));
            }

            if (includeTitle && !darkOnly)
            {
                var title = new FormattedText(
                    "HDRSnip",
                    System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    h * 0.055,
                    Brushes.White,
                    VisualTreeHelper.GetDpi(dv).PixelsPerDip);
                dc.DrawText(title, new Point((w - title.Width) / 2, h * 0.72));

                var sub = new FormattedText(
                    "HDR-aware snipping for Windows",
                    System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    h * 0.028,
                    new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    VisualTreeHelper.GetDpi(dv).PixelsPerDip);
                dc.DrawText(sub, new Point((w - sub.Width) / 2, h * 0.80));
            }
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        SavePng(bmp, Path.Combine(dir, name));
    }

    private static void SaveFeatureBoard(string path, int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)), null, new Rect(0, 0, w, h));
            var title = Fmt("Screenshots that stay sharp in HDR", 42, FontWeights.SemiBold, Colors.White);
            dc.DrawText(title, new Point(80, 80));
            var sub = Fmt("Open-source snipping tool with DXGI FP16 capture + tone mapping", 22, FontWeights.Normal, Color.FromRgb(0x99, 0x99, 0x99));
            dc.DrawText(sub, new Point(80, 140));

            string[] features =
            [
                "Ctrl+Shift+S rectangular snip",
                "HDR → SDR tone mapping (OBS-style)",
                "Copy to clipboard + toast to edit",
                "Adjustable SDR white (nits)",
                "System tray · multi-monitor"
            ];
            double y = 240;
            foreach (var f in features)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26)), null, new Rect(80, y, w - 160, 72), 12, 12);
                var t = Fmt("  " + f, 24, FontWeights.Normal, Colors.White);
                dc.DrawText(t, new Point(100, y + 20));
                y += 92;
            }
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        SavePng(bmp, path);
    }

    private static FormattedText Fmt(string text, double size, FontWeight weight, Color color) =>
        new(text,
            System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            1.25);

    private static BitmapSource CreateSampleCaptureBitmap(int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(30, 60, 90), Color.FromRgb(20, 20, 30), 45), null, new Rect(0, 0, w, h));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, new Rect(60, 60, w - 120, 120), 8, 8);
            var t = Fmt("Sample HDR UI text — stays readable after tone mapping", 28, FontWeights.SemiBold, Color.FromRgb(20, 20, 20));
            dc.DrawText(t, new Point(90, 100));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(76, 194, 255)), null, new Rect(60, 220, 280, 48), 8, 8);
            var b = Fmt("  Primary action", 20, FontWeights.Normal, Colors.Black);
            dc.DrawText(b, new Point(70, 230));
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private static void ForceRender(Window window)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void SaveWindow(Window window, string path, int canvasW, int canvasH)
    {
        window.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        window.Arrange(new Rect(window.DesiredSize));
        window.UpdateLayout();

        int ww = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int wh = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var rtb = new RenderTargetBitmap(ww, wh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);

        // Place on branded canvas
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), null, new Rect(0, 0, canvasW, canvasH));
            double scale = Math.Min((canvasW - 160.0) / ww, (canvasH - 160.0) / wh);
            double dw = ww * scale, dh = wh * scale;
            double dx = (canvasW - dw) / 2, dy = (canvasH - dh) / 2;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null,
                new Rect(dx - 8, dy - 8, dw + 16, dh + 16), 12, 12);
            dc.DrawImage(rtb, new Rect(dx, dy, dw, dh));
        }

        var outBmp = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
        outBmp.Render(dv);
        SavePng(outBmp, path);
    }

    private static void SaveWindowOnCanvas(Window window, string path, int canvasW, int canvasH, string caption)
    {
        window.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        window.Arrange(new Rect(window.DesiredSize));
        window.UpdateLayout();
        int ww = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int wh = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var rtb = new RenderTargetBitmap(ww, wh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), null, new Rect(0, 0, canvasW, canvasH));
            var cap = Fmt(caption, 36, FontWeights.SemiBold, Colors.White);
            dc.DrawText(cap, new Point((canvasW - cap.Width) / 2, 120));
            double scale = 2.2;
            double dw = ww * scale, dh = wh * scale;
            double dx = (canvasW - dw) / 2, dy = (canvasH - dh) / 2;
            dc.DrawImage(rtb, new Rect(dx, dy, dw, dh));
        }

        var outBmp = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
        outBmp.Render(dv);
        SavePng(outBmp, path);
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    public const string ListingCopy = """
PRODUCT NAME
HDRSnip

DESCRIPTION
(paste into Description — already filled if you used STORE-SUBMISSION.md)

HDRSnip is an open-source snipping tool built for Windows HDR displays.

When HDR is enabled, the built-in Snipping Tool and Print Screen often produce washed-out or overexposed screenshots. HDRSnip captures the desktop in high dynamic range (FP16 scRGB via DXGI), then tone-maps to a normal SDR image so UI text and colors stay readable when you paste or share.

Features
• Rectangular snip with frozen HDR-correct preview
• Fullscreen capture (monitor under cursor)
• Copies to clipboard by default
• Toast notification — click to open the editor and save
• Windows/OBS-style tone mapping with adjustable SDR white level
• System tray + hotkeys (Ctrl+Shift+S)

Open source (MIT): https://github.com/mattcam98/HDRSnip

WHAT'S NEW IN THIS VERSION
(leave blank for first submission)

PRODUCT FEATURES (one per field, up to 20)
HDR-correct screenshots when Windows HDR is on
DXGI FP16 capture with OBS-style tone mapping
Rectangular snip with frozen preview overlay
Copy to clipboard by default
Toast notification to open editor and save
Adjustable SDR white level (nits)
System tray app with global hotkeys
Ctrl+Shift+S rectangular · Ctrl+Shift+PrtSc fullscreen
Open source (MIT) on GitHub

SHORT DESCRIPTION
HDR-aware snipping for Windows — sharp, readable screenshots when HDR is on. Copy instantly, tap the toast to edit or save.

KEYWORDS (max 7, Enter after each)
screenshot
snipping tool
HDR
screen capture
clipboard
tone mapping
productivity

COPYRIGHT AND TRADEMARK INFO
© 2026 HDRSnip Open Source. MIT License. https://github.com/mattcam98/HDRSnip

ADDITIONAL LICENSE TERMS
(leave blank — MIT via GitHub is enough; Store Standard Application License Terms apply)

DEVELOPED BY
HDRSnip Open Source

SHORT TITLE
HDRSnip

VOICE TITLE
H D R Snip

UPLOAD FILES FROM THIS FOLDER
screenshots\01-settings.png
screenshots\02-toolbar.png
screenshots\03-editor.png
screenshots\04-features.png

logos\poster-9x16-720x1080.png          → 9:16 Poster art (720x1080)
logos\poster-9x16-1440x2160.png         → 9:16 Poster art (optional larger)
logos\boxart-1x1-1080.png               → 1:1 Box art
logos\tile-300x300.png                  → 1:1 App tile icon 300x300
logos\tile-150x150.png                  → 150x150
logos\tile-71x71.png                    → 71x71
logos\superhero-16x9-1920x1080.png      → optional 16:9 Super hero art (no title in image)

Xbox-only images: skip (Desktop-only app)
Trailers: skip for v1
""";
}

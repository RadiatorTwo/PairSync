using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using PairSync.Desktop;
using PairSync.Desktop.Controls;
using PairSync.Desktop.ViewModels;
using PairSync.Storage.Settings;

namespace PairSync.UiTests;

/// <summary>
/// Renders the design system and the shell. Set <c>PAIRSYNC_UI_SNAPSHOTS</c> to a folder to keep the frames
/// for a visual check against the mockups.
/// </summary>
public sealed class DesignSystemTests(HeadlessFixture ui)
{
    private static Color Token(string key, ThemeVariant variant) =>
        Avalonia.Application.Current!.TryGetResource(key, variant, out var value) ? (Color)value! : throw new KeyNotFoundException(key);

    [Fact]
    public Task Tokens_match_the_handoff() => ui.RunAsync(() =>
    {
        Assert.Equal(Color.Parse("#F2F2F3"), Token("BgColor", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#5980A6"), Token("AccentColor", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#4D7196"), Token("AccentPressedColor", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#E3E9F0"), Token("Accent100Color", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#FFFFFF"), Token("OnAccentColor", ThemeVariant.Light));
        Assert.Equal(Color.FromArgb(0x47, 0x1D, 0x1F, 0x20), Token("ScrimColor", ThemeVariant.Light));

        Assert.Equal(Color.Parse("#16181B"), Token("BgColor", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#7EA6CC"), Token("AccentColor", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#9DBBD9"), Token("AccentPressedColor", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#0E1620"), Token("OnAccentColor", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#C5D6E8"), Token("Accent900Color", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#16181B"), Token("OnAccent900Color", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#99000000"), Token("ScrimColor", ThemeVariant.Dark));
    });

    [Fact]
    public Task Theme_switches_at_runtime_without_leftovers() => ui.RunAsync(() =>
    {
        var app = Avalonia.Application.Current!;
        var window = new Window { Width = 200, Height = 100, Content = new Border { Classes = { "panel" } } };
        window.Show();
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Dark;
            using (var pixels = Pixels.From(window.CaptureRenderedFrame()!))
                Assert.Equal(Color.Parse("#16181B"), pixels[2, 2]);
            app.RequestedThemeVariant = ThemeVariant.Light;
            using (var pixels = Pixels.From(window.CaptureRenderedFrame()!))
                Assert.Equal(Color.Parse("#F2F2F3"), pixels[2, 2]);
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
            window.Close();
        }
    });

    [Fact]
    public Task Fluent_palettes_come_from_the_tokens() => ui.RunAsync(() =>
    {
        var app = Avalonia.Application.Current!;
        Assert.True(app.TryGetResource("SystemAccentColor", ThemeVariant.Dark, out var accent));
        Assert.Equal(Color.Parse("#7EA6CC"), accent);
        Assert.True(app.TryGetResource("SystemAccentColor", ThemeVariant.Light, out accent));
        Assert.Equal(Color.Parse("#5980A6"), accent);
    });

    [Fact]
    public Task Qr_code_is_black_on_white_in_dark_mode() => ui.RunAsync(() =>
    {
        var app = Avalonia.Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        try
        {
            using var qr = Codes.QrCode("PSI2 test code");
            var writeable = new WriteableBitmap(qr.PixelSize, qr.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var target = writeable.Lock())
                qr.CopyPixels(new PixelRect(qr.PixelSize), target.Address, target.RowBytes * qr.PixelSize.Height, target.RowBytes);
            using var pixels = Pixels.From(writeable);
            var seen = new HashSet<Color>();
            for (var y = 0; y < qr.PixelSize.Height; y++)
            {
                for (var x = 0; x < qr.PixelSize.Width; x++)
                    seen.Add(pixels[x, y]);
            }
            Assert.Equal([Colors.Black, Colors.White], seen.OrderBy(c => c.R));
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
        }
    });

    /// <summary>DARK_MODE.md: every color comes from a token; only Tokens.axaml holds color values.</summary>
    [Fact]
    public void No_fixed_colors_outside_the_tokens()
    {
        var desktop = Path.GetFullPath(Path.Combine(SourceFolder(), "..", "..", "src", "PairSync.Desktop"));
        var pattern = new System.Text.RegularExpressions.Regex(@"#[0-9A-Fa-f]{6}|Colors\.|Brushes\.|StaticResource \w*(Brush|Color|Shadow)\b");
        var found = Directory.EnumerateFiles(desktop, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && Path.GetFileName(f) != "Tokens.axaml")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(l => pattern.IsMatch(l.line))
            .Select(l => $"{Path.GetRelativePath(desktop, l.f)}:{l.i + 1}: {l.line.Trim()}")
            .ToList();
        Assert.Empty(found);
    }

    private static string SourceFolder([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [Fact]
    public Task Embedded_fonts_resolve() => ui.RunAsync(() =>
    {
        foreach (var key in new[] { "BodyFont", "HeadingFont", "MonoFont" })
        {
            var family = (FontFamily)Avalonia.Application.Current!.FindResource(key)!;
            Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphs), key);
            Assert.Equal(family.Name, glyphs.FamilyName);
        }
        var heading = (FontFamily)Avalonia.Application.Current!.FindResource("HeadingFont")!;
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(heading, FontStyle.Normal, FontWeight.SemiBold), out var semiBold));
        Assert.Equal(FontWeight.SemiBold, semiBold.Weight);
    });

    [Fact]
    public Task Rubric_is_upper_case_but_keeps_its_text() => ui.RunAsync(() =>
    {
        var rubric = new Rubric { Text = "Active transfers" };
        var window = new Window { Content = rubric, Width = 300, Height = 100 };
        window.Show();

        Assert.Equal("Active transfers", rubric.Text);
        Assert.Equal("ACTIVE TRANSFERS", rubric.TextLayout.TextLines[0].TextRuns[0].Text.ToString());
        window.Close();
    });

    [Fact]
    public Task Registration_marks_draw_at_the_corners() => ui.RunAsync(() =>
    {
        var card = new Card { Classes = { "connected" }, Width = 100, Height = 60, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Content = new TextBlock { Text = "laptop-win11" } };
        var window = new Window
        {
            Width = 200,
            Height = 120,
            Content = new Border { Padding = new Thickness(40, 30), Child = card },
        };
        window.Show();
        var frame = window.CaptureRenderedFrame()!;
        Save(frame, "marks");

        using var pixels = Pixels.From(frame);
        var accent = Color.Parse("#5980A6");
        // Arm of the top-left "+", outside the card: 3 px left of its corner at (40, 30).
        Assert.Equal(accent, pixels[37, 30]);
        Assert.Equal(accent, pixels[40, 27]);
        // Bottom-right corner (139, 89): arm beyond the border.
        Assert.Equal(accent, pixels[142, 89]);
        window.Close();
    });

    [Fact]
    public Task Gallery_renders() => ui.RunAsync(() =>
    {
        var segments = new SegmentedControl { ItemsSource = new[] { "Keep running in tray", "Minimize", "Quit" }, SelectedIndex = 0 };
        var gallery = new StackPanel
        {
            Margin = new Thickness(28, 24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Classes = { "h1" }, Text = "Design system" },
                new Rubric { Text = "Buttons" },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 16,
                    Children =
                    {
                        new Button { Classes = { "primary" }, Content = "Send to laptop-win11" },
                        new Button { Content = "Pause" },
                        new Button { Classes = { "accent" }, Content = "Pair…" },
                        new Button { Classes = { "quiet" }, Content = "Cancel" },
                        new Button { Classes = { "link" }, Content = "Open folder" },
                        new Button { Content = "Disabled", IsEnabled = false },
                    },
                },
                new Rubric { Text = "When the window is closed" },
                segments,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 24,
                    Children =
                    {
                        new RadioButton { Content = "Keep both (rename incoming copy)", IsChecked = true, GroupName = "g" },
                        new RadioButton { Content = "Replace", GroupName = "g" },
                        new CheckBox { Content = "settings.json", IsChecked = true },
                        new CheckBox { Content = "themes/", IsChecked = false },
                        new CheckBox { Classes = { "switch" }, Content = "Start with the desktop session", IsChecked = true },
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children =
                    {
                        new Chip { Content = "Read" }, new Chip { Content = "Write" },
                        new Chip { Classes = { "off" }, Content = "Install programs" }, new Chip { Classes = { "strong" }, Content = "Blocked" },
                    },
                },
                new ProgressBar { Value = 0.415 },
                new ProgressBar { Classes = { "resumed" }, Value = 0.7 },
                new UniformGrid
                {
                    Columns = 4,
                    Children =
                    {
                        DeviceCard("connected", "laptop-win11", "Connected"),
                        DeviceCard("offline", "nas-box", "Offline"),
                        DeviceCard("found", "device-7F2A", "Found on the LAN. Not paired, no access."),
                    },
                },
                new Border { Classes = { "banner" }, Child = new TextBlock { Text = "No system tray found.", TextWrapping = TextWrapping.Wrap } },
                new TextBlock { Classes = { "code" }, Text = "4827 1930 5561" },
            },
        };
        var window = new Window { Width = 1280, Height = 800, Content = gallery };
        window.Show();
        Save(window.CaptureRenderedFrame()!, "gallery");
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Save(window.CaptureRenderedFrame()!, "gallery-dark");
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        window.Close();
    });

    [Fact]
    public Task Shell_renders() => ui.RunAsync(() =>
    {
        using var settings = new TempSettings();
        settings.Store.Update(s => s with { DeviceName = "workstation-cachyos" });
        var shell = App.CreateShell(settings.Store, trayAvailable: false, new FakeAutostart(), () => { });
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Save(window.CaptureRenderedFrame()!, "shell-overview");
        shell.Navigate(AppPage.Settings);
        _ = shell.Dialogs.ShowAsync(new ConfirmDialogViewModel("Remove laptop-win11?",
            "Unfinished transfers with this device are canceled. To exchange files again, both devices have to pair anew.", "Remove"));
        Save(window.CaptureRenderedFrame()!, "shell-settings-dialog");
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Save(window.CaptureRenderedFrame()!, "shell-settings-dialog-dark");
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        window.DataContext = null;
        window.Close();
    });

    private static Card DeviceCard(string kind, string name, string state) => new()
    {
        Classes = { kind },
        Margin = new Thickness(0, 0, 14, 0),
        Content = new StackPanel
        {
            Spacing = 10,
            Children = { new TextBlock { Classes = { "mono" }, Text = name }, new TextBlock { Text = state, TextWrapping = TextWrapping.Wrap } },
        },
    };

    private static void Save(WriteableBitmap frame, string name)
    {
        if (Environment.GetEnvironmentVariable("PAIRSYNC_UI_SNAPSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name + ".png"), new PngBitmapEncoderOptions());
        }
    }

    /// <summary>Reads pixels of a captured frame (BGRA or RGBA, premultiplied; the tested pixels are opaque).</summary>
    private sealed class Pixels : IDisposable
    {
        private readonly ILockedFramebuffer _buffer;

        private Pixels(ILockedFramebuffer buffer) => _buffer = buffer;

        public static Pixels From(WriteableBitmap bitmap) => new(bitmap.Lock());

        public unsafe Color this[int x, int y]
        {
            get
            {
                var p = (byte*)_buffer.Address + (y * _buffer.RowBytes) + (x * 4);
                return _buffer.Format == PixelFormat.Rgba8888
                    ? Color.FromArgb(p[3], p[0], p[1], p[2])
                    : Color.FromArgb(p[3], p[2], p[1], p[0]);
            }
        }

        public void Dispose() => _buffer.Dispose();
    }
}

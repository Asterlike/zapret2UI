using System.IO;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Zapret2UI;

/// <summary>
/// Lightweight in-house toast notifications: a small topmost popup in the bottom-right corner of the
/// work area — visible even when the main window is hidden in the tray (unlike an in-window overlay).
/// Replaces the Windows balloon tips with a quieter, on-brand toast plus a soft synthesized sound.
/// Toasts stack upward and auto-dismiss. All calls must be made on the UI thread.
/// </summary>
public sealed class ToastNotifier
{
    private readonly List<ToastWindow> _open = new();
    private static byte[]? _blipWav;
    private static SoundPlayer? _player;   // kept alive so async Play() isn't cut off by GC

    /// <summary>Show a toast in the bottom-right corner. <paramref name="sound"/> plays the soft blip.</summary>
    public void Show(string title, string message, bool sound)
    {
        // Cap the stack so a burst of events can't wallpaper the screen — drop the oldest.
        if (_open.Count >= 4) { try { _open[0].Close(); } catch { /* removed via Closed */ } }

        var toast = new ToastWindow(title, message);
        toast.Closed += (_, _) => { _open.Remove(toast); Reflow(); };
        toast.ContentRendered += (_, _) => Reflow();   // real ActualHeight is known only after layout
        _open.Add(toast);
        toast.Show();
        Reflow();
        if (sound) PlayBlip();
    }

    /// <summary>Re-stack open toasts up from the bottom-right corner (newest lowest).</summary>
    private void Reflow()
    {
        var wa = SystemParameters.WorkArea;
        double y = wa.Bottom - 14;
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            var t = _open[i];
            double h = t.ActualHeight > 1 ? t.ActualHeight : 76;
            t.Left = wa.Right - t.Width - 14;
            y -= h;
            t.Top = y;
            y -= 10;   // gap between toasts
        }
    }

    private static void PlayBlip()
    {
        try
        {
            _blipWav ??= BuildBlipWav();
            _player = new SoundPlayer(new MemoryStream(_blipWav));
            _player.Play();   // async on a worker thread
        }
        catch { /* sound is best-effort */ }
    }

    /// <summary>Synthesize a short, soft, low-amplitude "blip" WAV (16-bit PCM mono) — quiet by design,
    /// no bundled asset, no NuGet dependency. A raised-sine envelope avoids click artifacts.</summary>
    private static byte[] BuildBlipWav()
    {
        const int sr = 44100;
        int n = (int)(sr * 0.085);
        var samples = new short[n];
        for (int i = 0; i < n; i++)
        {
            double env = Math.Sin(Math.PI * i / n);                 // 0 → 1 → 0
            double tone = Math.Sin(2 * Math.PI * 720.0 * i / sr);   // soft mid tone
            samples[i] = (short)(tone * env * 0.16 * short.MaxValue);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = n * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);          // PCM, mono
        w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>A single auto-dismissing toast popup window (borderless, transparent, click-to-close).</summary>
internal sealed class ToastWindow : Window
{
    private readonly TranslateTransform _slide = new(0, 12);
    private bool _closing;

    public ToastWindow(string title, string message)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;            // never steal focus from the foreground app
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 340;
        Opacity = 0;
        // NB: a non-identity RenderTransform on a top-level Window throws at Show()
        // ("Недопустимое преобразование для Window") — the slide-in lives on the content Border instead.

        Brush accent = Res("AccentBrush", Color.FromRgb(0x8B, 0x7F, 0xF5));
        Brush surface = Res("SurfaceBrush", Color.FromRgb(0x1E, 0x21, 0x2B));
        Brush border = Res("BorderBrush2", Color.FromRgb(0x33, 0x37, 0x45));
        Brush fg = Res("FgBrush", Color.FromRgb(0xE8, 0xEA, 0xF0));
        Brush muted = Res("FgMutedBrush", Color.FromRgb(0x9A, 0xA1, 0xB0));

        var stripe = new Border { Width = 4, Background = accent, CornerRadius = new CornerRadius(2) };
        var titleTb = new TextBlock { Text = title, Foreground = fg, FontWeight = FontWeights.SemiBold, FontSize = 13 };
        var msgTb = new TextBlock
        {
            Text = message, Foreground = muted, FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
        };
        var text = new StackPanel { Margin = new Thickness(12, 0, 4, 0) };
        text.Children.Add(titleTb);
        text.Children.Add(msgTb);

        var row = new DockPanel();
        DockPanel.SetDock(stripe, Dock.Left);
        row.Children.Add(stripe);
        row.Children.Add(text);

        Content = new Border
        {
            Background = surface,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 11, 14, 12),
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
            RenderTransform = _slide,   // slide-in target (can't live on the Window itself)
        };

        MouseLeftButtonUp += (_, _) => Dismiss();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var dur = TimeSpan.FromMilliseconds(220);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        timer.Tick += (_, _) => { timer.Stop(); Dismiss(); };
        timer.Start();
    }

    /// <summary>Fade out, then close (idempotent).</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => { try { Close(); } catch { /* already closing */ } };
        BeginAnimation(OpacityProperty, fade);
    }

    private static Brush Res(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

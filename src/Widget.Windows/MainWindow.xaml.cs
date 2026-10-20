using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using LimitsWidget.Core;
using Forms = System.Windows.Forms;
using WpfPoint = System.Windows.Point;
using Color = System.Windows.Media.Color;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LimitsWidget.Windows;
public partial class MainWindow : Window
{
    private readonly ICodexSource source = new LimitsWidget.Adapters.CodexSource();
    private readonly Settings settings = Settings.Load();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem topmostMenu;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly bool smoke;
    private readonly bool compositionSmoke;
    private bool backdropEnabled;
    private readonly bool hasSavedPosition;
    private QuotaSnapshot? snapshot;
    private string? lastAccount;
    private readonly HashSet<string> rolloverAttempts = [];
    private bool busy, paused, locked, suspended, exiting, selecting;
    private DateTimeOffset nextRead = DateTimeOffset.MinValue;
    public MainWindow(bool smoke, bool compositionSmoke = false)
    {
        hasSavedPosition = settings.Left is not null || settings.Top is not null;
        InitializeComponent(); this.smoke = smoke; this.compositionSmoke = compositionSmoke; Topmost = settings.Topmost;
        NoiseLayer.Background = FrostedBackdrop.CreateNoise();
        SourceInitialized += (_, _) =>
        {
            backdropEnabled = FrostedBackdrop.Apply(this);
            if (!backdropEnabled) GlassSurface.Background = new SolidColorBrush(Color.FromRgb(35, 39, 46));
            UpdateCorners();
        };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(UpdateCorners, DispatcherPriority.Render);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(UpdateCorners, DispatcherPriority.Render);
        if (settings.Left is double left && settings.Top is double top) { Left = left; Top = top; }
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Лимиты Codex", Visible = !smoke };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Показать", null, (_, _) => Dispatcher.Invoke(ShowCard));
        var pin = new Forms.ToolStripMenuItem("Поверх окон") { Checked = Topmost, CheckOnClick = true }; topmostMenu = pin;
        pin.CheckedChanged += (_, _) => { Topmost = pin.Checked; settings.Topmost = Topmost; SavePosition(); }; menu.Items.Add(pin);
        var pause = new Forms.ToolStripMenuItem("Приостановить обновление") { CheckOnClick = true }; pause.CheckedChanged += (_, _) => { paused = pause.Checked; UpdateFreshness(); if (!paused) nextRead = DateTimeOffset.MinValue; }; menu.Items.Add(pause);
        var notifications = new Forms.ToolStripMenuItem("Уведомления ниже 20%") { Checked = settings.Notifications, CheckOnClick = true }; notifications.CheckedChanged += (_, _) => { settings.Notifications = notifications.Checked; settings.Save(); }; menu.Items.Add(notifications);
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowCard);
        Loaded += async (_, _) => { PositionInitially(); ClampPosition(); await RefreshAsync(); if (smoke) await CaptureAndExitAsync(); else timer.Start(); };
        timer.Tick += async (_, _) => { UpdateFreshness(); if (DateTimeOffset.UtcNow >= nextRead) await RefreshAsync(); };
        LocationChanged += (_, _) => { if (IsLoaded) SavePosition(); };
        Closing += OnClosing;
        SystemEvents.SessionSwitch += OnSessionSwitch; SystemEvents.PowerModeChanged += OnPowerMode; SystemEvents.DisplaySettingsChanged += OnDisplayChange;
    }
    private void ShowCard() { Show(); ClampPosition(); Activate(); nextRead = DateTimeOffset.MinValue; _ = RefreshAsync(); }
    private void DragHeader(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) { DragMove(); ClampPosition(); SavePosition(); } }
    private void HideWindow(object sender, RoutedEventArgs e) => Hide();
    private void ToggleTopmost(object sender, RoutedEventArgs e) { topmostMenu.Checked = !Topmost; }
    private void SavePosition() { if (smoke) return; settings.Left = Left; settings.Top = Top; settings.Save(); }
    private void PositionInitially()
    {
        if (hasSavedPosition) return;
        var secondary = Forms.Screen.AllScreens.FirstOrDefault(screen => !screen.Primary);
        nint handle = new WindowInteropHelper(this).Handle;
        if (secondary is null || handle == 0 || !GetWindowRect(handle, out var rectangle)) return;
        var area = secondary.WorkingArea;
        SetWindowPos(handle, 0, Math.Max(area.Left, area.Right - (rectangle.Right - rectangle.Left) - 24), area.Top + 24, 0, 0, 0x0015);
    }
    private void ClampPosition()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var rectangle)) return;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        int x = Math.Clamp(rectangle.Left, area.Left, Math.Max(area.Left, area.Right - (rectangle.Right - rectangle.Left)));
        int y = Math.Clamp(rectangle.Top, area.Top, Math.Max(area.Top, area.Bottom - (rectangle.Bottom - rectangle.Top)));
        if (x != rectangle.Left || y != rectangle.Top) SetWindowPos(handle, 0, x, y, 0, 0, 0x0015); // no size/z-order/activation
    }
    private async Task RefreshAsync()
    {
        if (busy || paused || locked || suspended || exiting) return;
        busy = true;
        try
        {
            var fresh = await source.ReadQuotaAsync(lifetime.Token);
            if (lastAccount is not null && lastAccount != fresh.AccountNamespace) { settings.Bucket = null; ClearRings(); }
            lastAccount = fresh.AccountNamespace;
            snapshot = fresh; selecting = true;
            var options = fresh.Buckets.Select(value => new BucketOption(value)).ToList(); Buckets.ItemsSource = options;
            var bucket = QuotaRules.Select(fresh.Buckets, settings.Bucket);
            bool showSelector = fresh.Buckets.Count > 1 || bucket is null && fresh.Buckets.Count > 0;
            Buckets.Visibility = showSelector ? Visibility.Visible : Visibility.Collapsed; ClampPosition();
            Buckets.SelectedItem = options.FirstOrDefault(option => option.Bucket.Id == bucket?.Id); selecting = false;
            RenderBucket(bucket); Status.Text = fresh.Buckets.Count == 0 ? "Лимиты недоступны" : bucket is null ? "Выберите источник квоты" : "Обновлено " + fresh.CapturedAt.ToLocalTime().ToString("HH:mm");
            Notify(bucket); nextRead = DateTimeOffset.UtcNow.AddMinutes(IsVisible ? 1 : 5); UpdateFreshness();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Do not expose provider messages: they may contain private paths/account data.
            snapshot = null; ClearRings(); Buckets.ItemsSource = null;
            Status.Text = "Нет актуальных данных · проверьте вход Codex"; nextRead = DateTimeOffset.UtcNow.AddMinutes(IsVisible ? 1 : 5);
        }
        finally { busy = false; selecting = false; }
    }
    private void ClearRings() => RenderBucket(null);
    private void RenderBucket(QuotaBucket? bucket)
    {
        QuotaTitle.Text = bucket is null || bucket.Id == "codex" ? "Codex" : bucket.Name ?? "Квота Codex";
        QuotaTitle.ToolTip = QuotaTitle.Text;
        RenderWindow(bucket?.Primary, PrimaryArc, PrimaryValue, PrimaryLabel, PrimaryReset, "Первое окно");
        RenderWindow(bucket?.Secondary, SecondaryArc, SecondaryValue, SecondaryLabel, SecondaryReset, "Второе окно");
    }
    private static void RenderWindow(QuotaWindow? value, System.Windows.Shapes.Path arc, TextBlock percent, TextBlock label, TextBlock reset, string fallback)
    {
        percent.Text = value is null ? "—" : value.Remaining.ToString("0") + "%";
        label.Text = value?.DurationMinutes switch { 300 => "5 ч", 10080 => "7 д", int minutes => minutes % 1440 == 0 ? $"{minutes / 1440} д" : minutes % 60 == 0 ? $"{minutes / 60} ч" : $"{minutes} мин", _ => "—" };
        reset.Text = FormatReset(value, DateTimeOffset.UtcNow);
        arc.Stroke = value is null ? new SolidColorBrush(Color.FromRgb(115, 131, 152)) : BatteryBrush(value.Remaining);
        double remaining = value?.Remaining ?? 0;
        if (remaining <= 0) { arc.Data = null; return; }
        if (remaining >= 99.999) { arc.Data = new EllipseGeometry(new WpfPoint(56, 56), 46, 46); return; }
        double angle = remaining / 100 * 2 * Math.PI;
        var geometry = new StreamGeometry(); using (var context = geometry.Open()) { context.BeginFigure(new WpfPoint(56, 10), false, false); context.ArcTo(new WpfPoint(56 + 46 * Math.Sin(angle), 56 - 46 * Math.Cos(angle)), new System.Windows.Size(46, 46), 0, angle > Math.PI, SweepDirection.Clockwise, true, false); } geometry.Freeze(); arc.Data = geometry;
        System.Windows.Automation.AutomationProperties.SetName(percent, label.Text + ": осталось " + percent.Text);
    }
    private static string FormatReset(QuotaWindow? value, DateTimeOffset now)
    {
        if (value?.ResetsAt is not long unix) return "Нет времени\nсброса";
        var resetAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        var remaining = resetAt - now;
        string countdown;
        if (remaining <= TimeSpan.Zero) countdown = "сейчас";
        else if (remaining < TimeSpan.FromHours(1)) countdown = $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} мин";
        else if (remaining < TimeSpan.FromDays(1)) countdown = $"{(int)Math.Ceiling(remaining.TotalHours)} ч";
        else countdown = $"{(int)Math.Ceiling(remaining.TotalDays)} д";
        return $"Сброс {resetAt.ToLocalTime():dd.MM}\n{resetAt.ToLocalTime():HH:mm} ({countdown})";
    }
    private static System.Windows.Media.Brush BatteryBrush(double remaining)
    {
        // The hue follows remaining quota continuously: full green, half yellow, empty red.
        var green = Color.FromRgb(52, 199, 89);
        var yellow = Color.FromRgb(255, 214, 10);
        var red = Color.FromRgb(255, 69, 58);
        double progress = Math.Clamp(remaining, 0, 100) / 50;
        Color color = progress >= 1 ? Blend(yellow, green, progress - 1) : Blend(red, yellow, progress);
        var brush = new LinearGradientBrush(Blend(color, System.Windows.Media.Colors.White, 0.12), color, new WpfPoint(0, 0), new WpfPoint(1, 1));
        brush.Freeze();
        return brush;
    }
    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
    private void BucketChanged(object sender, SelectionChangedEventArgs e)
    {
        if (selecting || snapshot is null) return;
        if (Buckets.SelectedItem is BucketOption option) { var bucket = option.Bucket; settings.Bucket = bucket.Id; if (!smoke) settings.Save(); RenderBucket(bucket); Status.Text = "Обновлено " + snapshot.CapturedAt.ToLocalTime().ToString("HH:mm"); Notify(bucket); UpdateFreshness(); }
    }
    private void Notify(QuotaBucket? bucket)
    {
        if (smoke || paused || locked || suspended || !settings.Notifications || snapshot is null || bucket is null) return;
        List<string> warnings = [];
        foreach (var (window, slot) in new[] { (bucket.Primary, "primary"), (bucket.Secondary, "secondary") })
        {
            if (!QuotaRules.ClaimNotification(snapshot, bucket, window, slot, settings.NotifiedWindows)) continue;
            string period = window!.DurationMinutes switch { 300 => "5 часов", 10080 => "Неделя", _ => "Лимит" };
            warnings.Add($"{period}: осталось {window.Remaining:0}%.");
        }
        if (warnings.Count == 0 || !settings.Save()) return;
        try { tray.ShowBalloonTip(8000, "Лимиты Codex", string.Join("\n", warnings), Forms.ToolTipIcon.Warning); } catch (System.ComponentModel.Win32Exception) { }
    }
    private void UpdateFreshness()
    {
        if (paused || locked || suspended)
        {
            PrimaryArc.Opacity = SecondaryArc.Opacity = 0.4;
            Status.Text = paused ? "Обновление приостановлено" : locked ? "Пауза · компьютер заблокирован" : "Пауза · режим сна";
            return;
        }
        if (snapshot is null) return;
        var now = DateTimeOffset.UtcNow; var bucket = QuotaRules.Select(snapshot.Buckets, settings.Bucket);
        PrimaryReset.Text = FormatReset(bucket?.Primary, now);
        SecondaryReset.Text = FormatReset(bucket?.Secondary, now);
        bool rollover = false;
        foreach (var (window, slot) in new[] { (bucket?.Primary, "primary"), (bucket?.Secondary, "secondary") })
        {
            if (window?.ResetsAt is not long reset || DateTimeOffset.FromUnixTimeSeconds(reset) > now) continue;
            rollover = true;
            if (rolloverAttempts.Add($"{snapshot.AccountNamespace}|{bucket?.Id}|{slot}|{reset}")) nextRead = DateTimeOffset.MinValue;
        }
        bool stale = rollover || now - snapshot.CapturedAt > TimeSpan.FromMinutes(2);
        PrimaryArc.Opacity = SecondaryArc.Opacity = stale || paused || locked || suspended ? 0.4 : 1;
        if (stale) Status.Text = "Снимок устарел · ожидается обновление";
        else if (bucket is not null) Status.Text = "Обновлено " + snapshot.CapturedAt.ToLocalTime().ToString("HH:mm");
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.Invoke(() => { if (e.Reason == SessionSwitchReason.SessionLock) locked = true; else if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false; UpdateFreshness(); if (e.Reason == SessionSwitchReason.SessionUnlock) { nextRead = DateTimeOffset.MinValue; _ = RefreshAsync(); } });
    private void OnPowerMode(object sender, PowerModeChangedEventArgs e) => Dispatcher.Invoke(() => { if (e.Mode == PowerModes.Suspend) suspended = true; else if (e.Mode == PowerModes.Resume) suspended = false; UpdateFreshness(); if (e.Mode == PowerModes.Resume) { nextRead = DateTimeOffset.MinValue; _ = RefreshAsync(); } });
    private void OnDisplayChange(object? sender, EventArgs e) => Dispatcher.Invoke(ClampPosition);
    private async Task CaptureAndExitAsync()
    {
        UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        string directory = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(directory, "smoke-widget.png"))) encoder.Save(stream);
        File.WriteAllText(Path.Combine(directory, "smoke-status.txt"), $"quota={(snapshot is null ? "unavailable" : "available")} buckets={snapshot?.Buckets.Count ?? 0} backdrop={(backdropEnabled ? "acrylic" : "fallback")}");
        if (compositionSmoke) await CaptureCompositionAsync(directory);
        await ExitAsync();
    }
    private async Task CaptureCompositionAsync(string directory)
    {
        // Exercise the transitions that used to restore a rectangular native frame.
        Left -= 16; Top += 8;
        Hide(); Show();
        bool pinned = Topmost; Topmost = !pinned; Topmost = pinned;
        UpdateCorners();
        // A synthetic surface beneath our own card makes desktop blur verifiable
        // without saving any other application or personal desktop content.
        var pattern = new System.Windows.Controls.Canvas { Background = new SolidColorBrush(Color.FromRgb(37, 76, 129)) };
        var colors = new[] { Color.FromRgb(210, 105, 64), Color.FromRgb(63, 149, 140), Color.FromRgb(105, 78, 159) };
        for (int x = 0; x < (int)ActualWidth + 48; x += 24)
        {
            var stripe = new System.Windows.Shapes.Rectangle { Width = 12, Height = ActualHeight + 48, Fill = new SolidColorBrush(colors[(x / 24) % colors.Length]) };
            System.Windows.Controls.Canvas.SetLeft(stripe, x); pattern.Children.Add(stripe);
        }
        var text = new TextBlock { Text = "A a 1 2 3\nFROSTED GLASS\nA a 1 2 3", FontSize = 22, Foreground = System.Windows.Media.Brushes.White };
        System.Windows.Controls.Canvas.SetLeft(text, 25); System.Windows.Controls.Canvas.SetTop(text, 60); pattern.Children.Add(text);
        var background = new Window { Width = ActualWidth + 48, Height = ActualHeight + 48, Left = Left - 24, Top = Top - 24, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Content = pattern };
        try
        {
            Topmost = true; background.Show(); background.Activate();
            SetWindowPos(new WindowInteropHelper(background).Handle, new WindowInteropHelper(this).Handle, 0, 0, 0, 0, 0x0013);
            await Task.Delay(600);
            if (GetWindowRect(new WindowInteropHelper(this).Handle, out var rect))
            {
                using var capture = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
                using var graphics = System.Drawing.Graphics.FromImage(capture);
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, capture.Size);
                capture.Save(Path.Combine(directory, "smoke-composited.png"), System.Drawing.Imaging.ImageFormat.Png);
                using var edges = new System.Drawing.Bitmap(capture.Width + 48, capture.Height + 48);
                using var edgeGraphics = System.Drawing.Graphics.FromImage(edges);
                edgeGraphics.CopyFromScreen(rect.Left - 24, rect.Top - 24, 0, 0, edges.Size);
                edges.Save(Path.Combine(directory, "smoke-edges.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        finally { background.Close(); }
    }
    private async Task ExitAsync()
    {
        if (exiting) return; exiting = true; timer.Stop(); lifetime.Cancel();
        SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerMode; SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
        tray.Visible = false; tray.Dispose(); await source.DisposeAsync(); lifetime.Dispose(); Close(); System.Windows.Application.Current.Shutdown();
    }
    private void UpdateCorners()
    {
        FrostedBackdrop.UpdateCorners(this);
        ClampPosition();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private sealed record BucketOption(QuotaBucket Bucket)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Bucket.Name) ? Bucket.Id : Bucket.Name;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}

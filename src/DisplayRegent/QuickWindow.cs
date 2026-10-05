using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DisplayRegent;

// One shared display controller; this window is only its small, spatial interface.
internal sealed class QuickWindow : Window
{
    private readonly MainWindow controller;
    private TextBlock notice = new();
    private Brush ink = Brushes.White, muted = Brushes.Gray;
    private bool fading;
    private bool pointerHasEntered;
    private readonly DispatcherTimer leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    internal System.Drawing.Point? TrayAnchor;
    private static readonly string[] DarkColors = { "#9BBEED", "#D9B78F", "#ADCFB6", "#C7ADE2", "#EFAAA3", "#A4D7DB" };
    private static readonly string[] LightColors = { "#436A9D", "#926A36", "#467859", "#765797", "#A35C54", "#397C82" };
    internal QuickWindow(MainWindow controller)
    {
        this.controller = controller;
        Title = "Display Regent · Quick widget"; Width = 500; Height = 350;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        Deactivated += (_, _) => { if (!controller.QuickPending) FadeAway(); };
        MouseEnter += (_, _) => { pointerHasEntered = true; leaveTimer.Stop(); if (fading) { fading = false; BeginAnimation(OpacityProperty, Fade(1)); } };
        MouseLeave += (_, _) => { if (pointerHasEntered && !IsMouseOver && !controller.QuickPending) { leaveTimer.Stop(); leaveTimer.Start(); } };
        leaveTimer.Tick += (_, _) => { leaveTimer.Stop(); if (pointerHasEntered && !IsMouseOver && !controller.QuickPending) FadeAway(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) leaveTimer.Stop(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (controller.QuickPending) controller.QuickRevert(); else FadeAway(); e.Handled = true; } };
        SourceInitialized += (_, _) => ApplyGlass();
        Render();
    }
    private static SolidColorBrush Color(string value) => new((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
    private Brush Accent(int number) => Color((controller.QuickLight ? LightColors : DarkColors)[(number - 1) % DarkColors.Length]);
    private static DoubleAnimation Fade(double to) => new(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
    private TextBlock Text(string value, double size = 12) => new() { Text = value, FontSize = size, Foreground = ink, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private Button Frame(string number, string title, Brush accent, Action action, double width, double height, bool enabled = true, bool on = true)
    {
        var content = new Grid { Width = width, Height = height };
        var wash = new Border { Background = accent, Opacity = 0, Margin = new Thickness(4) }; content.Children.Add(wash);
        content.Children.Add(new LineFrame(accent) { Opacity = on ? .9 : .36, IsHitTestVisible = false });
        var numeral = Text(number, Math.Clamp(Math.Min(width, height) * .24, 16, 35)); numeral.FontFamily = new FontFamily("Georgia"); numeral.Opacity = on ? .88 : .43; content.Children.Add(numeral);
        var name = Text(title, Math.Clamp(width / 14, 10, 13)); name.Margin = new Thickness(12); name.Opacity = 0; content.Children.Add(name);
        var button = new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = Cursors.Hand, IsEnabled = enabled, Focusable = true };
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        var template = new ControlTemplate(typeof(Button)); var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); template.VisualTree = presenter; button.Template = template;
        void Hover(bool hover) { numeral.BeginAnimation(OpacityProperty, Fade(hover ? 0 : on ? .88 : .43)); name.BeginAnimation(OpacityProperty, Fade(hover ? 1 : 0)); wash.BeginAnimation(OpacityProperty, Fade(hover ? .09 : 0)); }
        button.MouseEnter += (_, _) => Hover(true); button.MouseLeave += (_, _) => Hover(false);
        button.GotKeyboardFocus += (_, _) => Hover(true); button.LostKeyboardFocus += (_, _) => Hover(false);
        button.Click += (_, _) => action(); return button;
    }
    internal void Render()
    {
        // During confirmation, update only the countdown. Preserve hover animations.
        if (controller.QuickPending && Content != null) { UpdateNotice(); return; }
        ink = Color(controller.QuickLight ? "#243047" : "#EBEEF7"); muted = Color(controller.QuickLight ? "#59677B" : "#A4AFC4");
        var glass = Color(controller.QuickLight ? "#BAF4F6FA" : "#A0141C2B");
        var root = new Grid { Margin = new Thickness(24, 18, 24, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(47) }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        var brand = Text("DISPLAY REGENT", 10); brand.Foreground = muted; brand.HorizontalAlignment = HorizontalAlignment.Left; root.Children.Add(brand);
        var canvas = new Canvas { ClipToBounds = false, Background = Brushes.Transparent }; Grid.SetRow(canvas, 1); root.Children.Add(canvas);
        var displays = controller.QuickDisplays;
        if (displays.Count > 0)
        {
            double minX = displays.Min(d => d.X), minY = displays.Min(d => d.Y);
            double desktopWidth = displays.Max(d => d.X + d.Width) - minX, desktopHeight = displays.Max(d => d.Y + d.Height) - minY;
            double scale = Math.Min(418 / Math.Max(1, desktopWidth), 188 / Math.Max(1, desktopHeight));
            double offsetX = (442 - desktopWidth * scale) / 2, offsetY = (212 - desktopHeight * scale) / 2;
            foreach (var d in displays)
            {
                var shared = displays.Where(x => x.X == d.X && x.Y == d.Y && x.Width == d.Width && x.Height == d.Height && x.Enabled).OrderBy(x => x.Number).ToList();
                int slots = d.Enabled ? Math.Max(1, shared.Count) : 1, slot = d.Enabled ? Math.Max(0, shared.IndexOf(d)) : 0;
                double width = Math.Max(28, d.Width * scale / slots), height = Math.Max(28, d.Height * scale);
                var tile = Frame(d.Number.ToString(), d.Name + "\n" + d.Width + " × " + d.Height, Accent(d.Number), () => { if (controller.QuickPending) controller.QuickKeep(); else { controller.QuickToggle(d, !d.Enabled); controller.QuickApply(); } }, width, height, on: d.Enabled);
                Canvas.SetLeft(tile, offsetX + (d.X - minX) * scale + slot * width); Canvas.SetTop(tile, offsetY + (d.Y - minY) * scale); canvas.Children.Add(tile);
            }
        }
        var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetRow(footer, 2);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int index = 0; index < 3; index++)
        {
            var scene = controller.QuickScenes.ElementAtOrDefault(index); int number = index + 1;
            var preset = Frame(number.ToString(), scene?.Name ?? "Set scene", muted, () => { if (scene == null) controller.BringForward(); else if (!controller.QuickPending) controller.QuickScene(scene); }, 61, 33);
            preset.Margin = new Thickness(0, 0, 10, 0); presets.Children.Add(preset);
        }
        footer.Children.Add(presets);
        var full = new Button { Content = "Full panel ↗", Foreground = muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8), FontSize = 11, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        var fullTemplate = new ControlTemplate(typeof(Button)); fullTemplate.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)); full.Template = fullTemplate; full.Click += (_, _) => controller.BringForward(); Grid.SetColumn(full, 1); footer.Children.Add(full); root.Children.Add(footer);
        notice = Text("", 10); notice.Foreground = muted; Grid.SetRow(notice, 3); root.Children.Add(notice); UpdateNotice();
        Content = new Border { Margin = new Thickness(5), Background = glass, BorderBrush = Color(controller.QuickLight ? "#68A4B3CA" : "#686F829E"), BorderThickness = new Thickness(.7), CornerRadius = new CornerRadius(10), Child = root };
        if (IsVisible) ApplyGlass();
    }
    private void UpdateNotice()
    {
        string status = controller.QuickStatus;
        notice.Text = controller.QuickPending ? $"Click a screen to keep · Esc to undo · {controller.QuickSeconds}s" : status.StartsWith("Changes stay") || status.StartsWith("Layout kept") || status.StartsWith("Previous layout") || status.StartsWith("Draft ready") ? "" : status;
        notice.ToolTip = notice.Text.Length > 80 ? notice.Text : null;
    }
    private static System.Collections.Generic.IEnumerable<Button> Buttons(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is Button button) yield return button; foreach (var nested in Buttons(child)) yield return nested; }
    }
    internal void PreviewHover()
    {
        UpdateLayout(); var button = Buttons(this).First();
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
    }
    internal async System.Threading.Tasks.Task CheckMinimalSurface()
    {
        UpdateLayout(); var buttons = Buttons(this).ToList();
        if (buttons.Count != controller.QuickDisplays.Count + 4) throw new InvalidOperationException("Widget has unexpected controls.");
        PreviewHover(); await System.Threading.Tasks.Task.Delay(240);
        var grid = (Grid)buttons.First().Content; var labels = grid.Children.OfType<TextBlock>().ToList();
        if (labels.Count != 2 || labels[0].Opacity > .01 || labels[1].Opacity < .99) throw new InvalidOperationException("Hover title did not crossfade with the number.");
        Console.WriteLine("PASS only monitor frames, three preset frames and Full panel; centered hover titles crossfade with numbers.");
    }
    private void FadeAway()
    {
        leaveTimer.Stop();
        if (fading) return; fading = true;
        var animation = Fade(0); animation.Completed += (_, _) => { if (fading) { Hide(); fading = false; } }; BeginAnimation(OpacityProperty, animation);
    }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { internal int State, Flags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { internal int Attribute; internal IntPtr Data; internal int Size; }
    [DllImport("user32.dll")] private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    private void ApplyGlass()
    {
        var handle = new WindowInteropHelper(this).Handle; if (handle == IntPtr.Zero) return;
        var accent = new AccentPolicy { State = 3, Flags = 0, GradientColor = 0 };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try { Marshal.StructureToPtr(accent, pointer, false); var data = new CompositionData { Attribute = 19, Data = pointer, Size = Marshal.SizeOf<AccentPolicy>() }; SetWindowCompositionAttribute(handle, ref data); }
        catch (EntryPointNotFoundException) { /* The translucent surface remains usable without OS blur. */ }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    internal void OpenAtTray()
    {
        bool opening = !IsVisible; if (opening) pointerHasEntered = false; leaveTimer.Stop(); fading = false; BeginAnimation(OpacityProperty, null); Opacity = opening ? 0 : 1; Show(); UpdateLayout();
        var screen = TrayAnchor.HasValue ? Forms.Screen.FromPoint(TrayAnchor.Value) : Forms.Screen.PrimaryScreen ?? Forms.Screen.FromPoint(Forms.Cursor.Position);
        var area = screen.WorkingArea; var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, new IntPtr(-1), area.Right - 520, area.Bottom - 370, 0, 0, 0x0011);
        double dpi = GetDpiForWindow(handle) / 96d; int width = (int)Math.Ceiling(ActualWidth * dpi), height = (int)Math.Ceiling(ActualHeight * dpi);
        int anchor = TrayAnchor?.X ?? area.Right - 38;
        int x = Math.Clamp(anchor - width + (int)(29 * dpi), area.Left, Math.Max(area.Left, area.Right - width));
        SetWindowPos(handle, new IntPtr(-1), x, Math.Max(area.Top, area.Bottom - height - 8), width, height, 0x0040);
        ApplyGlass(); Activate(); if (opening) BeginAnimation(OpacityProperty, Fade(1));
    }
}

internal sealed class LineFrame : FrameworkElement
{
    private readonly Brush brush;
    internal LineFrame(Brush brush) { this.brush = brush; }
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; var pen = new Pen(brush, .7); double c = Math.Min(18, Math.Min(w, h) / 4);
        dc.DrawLine(pen, new Point(c, 3), new Point(w-c, 3)); dc.DrawLine(pen, new Point(c, h-3), new Point(w-c, h-3));
        dc.DrawLine(pen, new Point(3, c), new Point(3, h-c)); dc.DrawLine(pen, new Point(w-3, c), new Point(w-3, h-c));
        foreach (var corner in new[] { (0d,0d,1d,1d), (w,0d,-1d,1d), (0d,h,1d,-1d), (w,h,-1d,-1d) })
        {
            Point P(double a,double b) => new(corner.Item1+a*corner.Item3,corner.Item2+b*corner.Item4);
            var line = new StreamGeometry(); using(var ctx=line.Open()) { ctx.BeginFigure(P(c,3),false,false); ctx.BezierTo(P(2,3),P(13,11),P(3,c),true,false); }
            dc.DrawGeometry(null,pen,line);
            var flourish = new StreamGeometry(); using(var ctx=flourish.Open()) { ctx.BeginFigure(P(c*.7,6),false,false); ctx.BezierTo(P(5,2),P(2,5),P(6,c*.7),true,false); }
            dc.DrawGeometry(null,new Pen(brush,.45),flourish);
            dc.DrawLine(pen,P(4,4),P(8,8)); dc.DrawLine(pen,P(4,4),P(9,5)); dc.DrawLine(pen,P(4,4),P(5,9));
        }
    }
}

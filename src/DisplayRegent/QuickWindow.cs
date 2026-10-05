using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace DisplayRegent;

// The tray widget shares the full panel's draft and recovery controller.
// Opening either surface never creates a second display configuration.
internal sealed class QuickWindow : Window
{
    private readonly MainWindow controller;
    private StackPanel body = new();
    private Polygon flag = new();
    private Brush ink = Brushes.White, muted = Brushes.Gray, edge = Brushes.Gray, paper = Brushes.Black;
    private static readonly string[] Colors = { "#9BBEED", "#D9B78F", "#ADCFB6", "#C7ADE2", "#EFAAA3", "#A4D7DB" };
    internal QuickWindow(MainWindow controller)
    {
        this.controller = controller;
        Title = "Display Regent · Quick widget"; Width = 344;
        SizeToContent = SizeToContent.Height; MaxHeight = 620;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        Deactivated += (_, _) => { if (!controller.QuickPending) Hide(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (controller.QuickPending) controller.QuickRevert(); else Hide(); e.Handled = true; } };
        Render();
    }
    private static Brush Color(string value) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
    private TextBlock Text(string value, double size = 12, bool subtle = false) => new() { Text = value, FontSize = size, Foreground = subtle ? muted : ink, TextWrapping = TextWrapping.Wrap };
    private Button Button(string value, Action action, bool enabled = true)
    {
        var button = new Button { Content = value, Style = (Style)controller.Resources[typeof(Button)], Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 6, 4), FontSize = 12, IsEnabled = enabled };
        button.Click += (_, _) => action(); return button;
    }
    internal void Render()
    {
        ink = Color(controller.QuickLight ? "#243047" : "#EBEEF7"); muted = Color(controller.QuickLight ? "#59677B" : "#A4AFC4");
        paper = Color(controller.QuickLight ? "#F4F6FA" : "#141C2B"); edge = Color(controller.QuickLight ? "#CBD4E3" : "#35435B");
        body = new StackPanel { Margin = new Thickness(16, 13, 16, 12) }; flag = new Polygon();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = Button("×", Hide); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var brand = new StackPanel(); brand.Children.Add(new TextBlock { Text = "♜  Display Regent", FontFamily = new FontFamily("Georgia"), FontSize = 18, Foreground = ink }); brand.Children.Add(Text("KNIGHT AI+AV  ·  QUICK WIDGET", 9, true)); header.Children.Add(brand); body.Children.Add(header);
        var displays = controller.QuickDisplays;
        var canvas = new Canvas { Height = 82, ClipToBounds = true, Background = Color(controller.QuickLight ? "#FFFFFF" : "#1B2638"), Margin = new Thickness(0, 0, 0, 10) };
        if (displays.Count > 0)
        {
            double minX = displays.Min(d => d.X), minY = displays.Min(d => d.Y);
            double width = displays.Max(d => d.X + d.Width) - minX, height = displays.Max(d => d.Y + d.Height) - minY;
            double scale = Math.Min(284 / Math.Max(1, width), 62 / Math.Max(1, height));
            foreach (var d in displays)
            {
                var tile = new Border { Width = Math.Max(18, d.Width * scale), Height = Math.Max(18, d.Height * scale), BorderBrush = Color(Colors[(d.Number - 1) % Colors.Length]), BorderThickness = new Thickness(1), Background = paper, Opacity = d.Enabled ? 1 : .4, CornerRadius = new CornerRadius(2), Child = new TextBlock { Text = d.Number.ToString(), Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                Canvas.SetLeft(tile, 10 + (d.X - minX) * scale); Canvas.SetTop(tile, 10 + (d.Y - minY) * scale); canvas.Children.Add(tile);
            }
        }
        body.Children.Add(canvas);
        foreach (var d in displays)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            var primary = Button(d.Primary ? "★" : "☆", () => controller.QuickPrimary(d), !controller.QuickPending); primary.ToolTip = "Make this the primary screen"; DockPanel.SetDock(primary, Dock.Right); row.Children.Add(primary);
            var toggle = new CheckBox { IsChecked = d.Enabled, IsEnabled = !controller.QuickPending, Foreground = ink, VerticalContentAlignment = VerticalAlignment.Center };
            var label = new StackPanel(); label.Children.Add(new TextBlock { Text = $"{d.Number}  {d.Name}", Foreground = Color(Colors[(d.Number - 1) % Colors.Length]), FontSize = 12 }); label.Children.Add(Text($"{d.Width} × {d.Height}" + (d.MirrorOf != "" ? " · mirrored" : ""), 10, true)); toggle.Content = label;
            toggle.Click += (_, _) => controller.QuickToggle(d, toggle.IsChecked == true); row.Children.Add(toggle); body.Children.Add(row);
        }
        var scenes = new WrapPanel { Margin = new Thickness(0, 3, 0, 5) };
        foreach (var scene in controller.QuickScenes) scenes.Children.Add(Button(scene.Name, () => controller.QuickScene(scene), !controller.QuickPending));
        if (scenes.Children.Count == 0) scenes.Children.Add(Text("Create your scenes in the full panel.", 11, true)); body.Children.Add(scenes);
        var actions = new WrapPanel();
        if (controller.QuickPending) { actions.Children.Add(Button($"Keep · {controller.QuickSeconds}s", controller.QuickKeep)); actions.Children.Add(Button("Revert", controller.QuickRevert)); }
        else { actions.Children.Add(Button("Apply", controller.QuickApply, controller.QuickDirty)); actions.Children.Add(Button("Refresh", controller.QuickRefresh)); }
        actions.Children.Add(Button("Full panel ↗", controller.BringForward)); body.Children.Add(actions);
        body.Children.Add(Text(controller.QuickPending ? "Confirm within 20 seconds, or your previous layout returns." : controller.QuickStatus.Replace("opens this panel", "opens this widget"), 10, true));
        var border = new Border { Background = paper, BorderBrush = edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 590 } };
        var outer = new Grid { Margin = new Thickness(3) }; outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) }); outer.Children.Add(border);
        flag.Points = new PointCollection { new(0, 0), new(18, 0), new(9, 11) }; flag.Fill = paper; flag.Stroke = edge; flag.StrokeThickness = 1; flag.HorizontalAlignment = HorizontalAlignment.Right; flag.Margin = new Thickness(0, -1, 20, 0); Grid.SetRow(flag, 1); outer.Children.Add(flag);
        Content = outer;
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    internal void OpenAtTray()
    {
        Show(); UpdateLayout();
        // Taskbar work-area exclusion anchors the flag immediately above the tray.
        var screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.FromPoint(Forms.Cursor.Position);
        var area = screen.WorkingArea; var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, new IntPtr(-1), area.Right - 360, area.Bottom - 400, 0, 0, 0x0011);
        double dpi = GetDpiForWindow(handle) / 96d;
        int width = (int)Math.Ceiling(ActualWidth * dpi), height = (int)Math.Ceiling(ActualHeight * dpi);
        SetWindowPos(handle, new IntPtr(-1), Math.Max(area.Left, area.Right - width - 8), Math.Max(area.Top, area.Bottom - height), width, height, 0x0040);
        Activate();
    }
}

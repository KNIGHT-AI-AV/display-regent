using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DisplayRegent;

internal sealed class MainWindow : Window
{
    private readonly Preferences prefs = Store.Load();
    private List<Display> displays = new();
    private Display? selected;
    private readonly Grid root = new();
    private readonly Canvas map = new() { ClipToBounds = false, Background = Brushes.Transparent };
    private readonly StackPanel inspector = new();
    private readonly StackPanel presets = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock count = new();
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer confirmTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer hotplugTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HashSet<int> hotkeys = new();
    private Forms.NotifyIcon? tray;
    private HwndSource? hwnd;
    private Snapshot? before;
    private string? ticket;
    private int seconds;
    private readonly Stopwatch confirmationClock = new();
    private bool dirty, exiting, capturing, demo;
    private double scale = 0.1;
    private Point dragStart;
    private int originalX, originalY;
    private Border? dragging;
    private bool moved;
    private SolidColorBrush ink = Brushes.White, muted = Brushes.Gray, surface = Brushes.Black, panel = Brushes.Black, line = Brushes.Gray;
    private static readonly string[] Colors = { "#9BBEED", "#D9B78F", "#ADCFB6", "#C7ADE2", "#EFAAA3", "#A4D7DB", "#D4CC95", "#C8B9B1" };
    public string? CaptureDirectory;
    public bool AcceptanceCheck;
    private QuickWindow? quick;
    private bool quickOperation;
    internal IReadOnlyList<Display> QuickDisplays => displays;
    internal IReadOnlyList<Preset> QuickScenes => prefs.Presets;
    internal bool QuickPending => before != null;
    internal bool QuickDirty => dirty && !demo;
    internal int QuickSeconds => seconds;
    internal string QuickStatus => status.Text;
    internal bool QuickLight => prefs.Theme == "light";
    internal void QuickToggle(Display d, bool enabled) { if (before != null) return; d.Enabled = enabled; if (!enabled) d.Primary = false; if (!displays.Any(x => x.Enabled && x.Primary)) { var first = displays.FirstOrDefault(x => x.Enabled); if (first != null) first.Primary = true; } Changed(); }
    internal void QuickPrimary(Display d) { if (before != null) return; foreach (var x in displays) x.Primary = x == d; d.Enabled = true; d.MirrorOf = ""; Changed(); }
    internal void QuickApply() { quickOperation = true; Apply(); quick?.Render(); }
    internal void QuickScene(Preset p) { quickOperation = true; try { ApplyPreset(p); } catch (Exception e) { status.Text = e.Message; } quick?.Render(); }
    internal void QuickKeep() { try { Keep(); } catch (Exception e) { status.Text = e.Message; } quick?.Render(); }
    internal void QuickRevert() { Revert(); quick?.Render(); }
    internal void QuickRefresh() { if (before == null) Refresh(); quick?.Render(); }
    internal void ShowQuick() { quickOperation = true; Hide(); quick ??= new QuickWindow(this); quick.Render(); quick.OpenAtTray(); }
    private void ShowQuickFromTray() { quick ??= new QuickWindow(this); quick.TrayAnchor = Forms.Cursor.Position; ShowQuick(); }

    public MainWindow(bool capture, bool demonstration)
    {
        capturing = capture; demo = demonstration;
        Title = "Display Regent"; Width = 960; Height = 720; MinWidth = 800; MinHeight = 650;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Topmost = prefs.Topmost; ShowInTaskbar = true;
        try { Icon = BitmapFrame.Create(System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("regent.ico")!); } catch { }
        SetTheme(); BuildShell();
        Loaded += (_, _) => { Refresh(); if (!capturing && !AcceptanceCheck && !prefs.Initialized) CreateStarterScenes(); if (CaptureDirectory != null) Capture(); else if (AcceptanceCheck) Dispatcher.BeginInvoke(RunAcceptance); };
        SourceInitialized += (_, _) =>
        {
            hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); hwnd.AddHook(MessageHook);
            if (!capturing) { SetupTray(); RegisterKeys(); SetupShowSignal(); }
        };
        Closing += (_, e) => { if (!exiting && !capturing) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { foreach (var key in hotkeys) Native.UnregisterHotKey(hwnd!.Handle, key); tray?.Dispose(); };
        map.SizeChanged += (_, _) => DrawMap();
        confirmTimer.Tick += (_, _) => { seconds = Math.Max(0, 20 - (int)confirmationClock.Elapsed.TotalSeconds); if (seconds <= 0) Revert(); else DrawActions(); };
        hotplugTimer.Tick += (_, _) => { hotplugTimer.Stop(); if (before == null && !dirty) Refresh(); else if (before == null) status.Text = "Display connections changed. Refresh before applying your draft."; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (before != null) Revert(); else Hide(); } };
    }
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private void SetTheme()
    {
        bool light = prefs.Theme == "light";
        ink = Brush(light ? "#243047" : "#EBEEF7"); muted = Brush(light ? "#59677B" : "#A4AFC4");
        surface = Brush(light ? "#F4F6FA" : "#141C2B"); panel = Brush(light ? "#FFFFFF" : "#1B2638"); line = Brush(light ? "#CBD4E3" : "#35435B");
        Background = surface; Foreground = ink;
        Resources[SystemColors.WindowBrushKey] = panel; Resources[SystemColors.WindowTextBrushKey] = ink;
        Resources[SystemColors.HighlightBrushKey] = Brush("#547AA8");
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, panel));
        buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, line));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 9, 14, 9)));
        buttonStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "buttonBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7)); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.AppendChild(content); template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#9BBEED"), "buttonBorder")); template.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "buttonBorder")); template.Triggers.Add(focus);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, 0.4)); template.Triggers.Add(disabled);
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, template)); Resources[typeof(Button)] = buttonStyle;
        ThemeControls();
    }
    private void ThemeControls()
    {
        string bg = panel.Color.ToString(), fg = ink.Color.ToString(), edge = line.Color.ToString(), accent = prefs.Theme == "light" ? "#DCE9FC" : "#304869";
        string xaml = $@"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
<Style TargetType='ComboBoxItem'><Setter Property='Foreground' Value='{fg}'/><Setter Property='Padding' Value='10,8'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'><Border x:Name='bg' Background='{bg}' Padding='{{TemplateBinding Padding}}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bg' Property='Background' Value='{accent}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='bg' Property='Background' Value='{accent}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ComboBox'><Setter Property='Foreground' Value='{fg}'/><Setter Property='Background' Value='{bg}'/><Setter Property='BorderBrush' Value='{edge}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'><Grid><Border Background='{{TemplateBinding Background}}' BorderBrush='{{TemplateBinding BorderBrush}}' BorderThickness='1' CornerRadius='6'/><ToggleButton Focusable='False' IsChecked='{{Binding IsDropDownOpen, RelativeSource={{RelativeSource TemplatedParent}}, Mode=TwoWay}}'><ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='Transparent'><TextBlock Text='⌄' Foreground='{fg}' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0'/></Border></ControlTemplate></ToggleButton.Template></ToggleButton><ContentPresenter IsHitTestVisible='False' Content='{{TemplateBinding SelectionBoxItem}}' ContentTemplate='{{TemplateBinding SelectionBoxItemTemplate}}' Margin='10,8,30,8' VerticalAlignment='Center'/><Popup x:Name='PART_Popup' Placement='Bottom' IsOpen='{{TemplateBinding IsDropDownOpen}}' AllowsTransparency='True' Focusable='False'><Border Background='{bg}' BorderBrush='{edge}' BorderThickness='1' CornerRadius='6' MinWidth='{{Binding ActualWidth, RelativeSource={{RelativeSource TemplatedParent}}}}'><ScrollViewer MaxHeight='260' CanContentScroll='True'><ItemsPresenter/></ScrollViewer></Border></Popup></Grid><ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter Property='BorderBrush' Value='#9BBEED'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='.45'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ScrollBar'><Setter Property='Width' Value='8'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Grid Background='Transparent'><Track x:Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True' Minimum='{{TemplateBinding Minimum}}' Maximum='{{TemplateBinding Maximum}}' Value='{{TemplateBinding Value}}' ViewportSize='{{TemplateBinding ViewportSize}}'><Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='{edge}' CornerRadius='4' Margin='2,0'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton></Track></Grid></ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>";
        var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
        Resources.MergedDictionaries.Clear(); Resources.MergedDictionaries.Add(resources);
    }
    private TextBlock Text(string value, double size = 14, bool subtle = false) => new() { Text = value, FontSize = size, Foreground = subtle ? muted : ink, TextWrapping = TextWrapping.Wrap };
    private Button Button(string label, Action action, bool accent = false)
    {
        var b = new Button { Content = label, Style = (Style)Resources[typeof(Button)], Margin = new Thickness(0, 0, 8, 0), MinHeight = 36, Background = accent ? Brush(prefs.Theme == "light" ? "#DCE9FC" : "#304869") : panel };
        b.Click += (_, _) => { try { action(); } catch (Exception e) { status.Text = e.Message; } }; return b;
    }
    private void BuildShell()
    {
        foreach (FrameworkElement reusable in new FrameworkElement[] { map, inspector, presets, actions, count, status })
        {
            if (reusable.Parent is Panel parent) parent.Children.Remove(reusable);
            else if (reusable.Parent is ContentControl owner) owner.Content = null;
        }
        root.Children.Clear(); root.RowDefinitions.Clear(); root.ColumnDefinitions.Clear(); root.Margin = new Thickness(28, 18, 28, 24);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titlebar = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        titlebar.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is TextBlock || e.OriginalSource == titlebar) DragMove(); };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(controls, Dock.Right);
        controls.Children.Add(Button(prefs.Theme == "dark" ? "Light" : "Dark", ToggleTheme));
        controls.Children.Add(Button("Settings", Settings)); controls.Children.Add(Button("×", () => { if (before != null) Revert(); Hide(); }));
        titlebar.Children.Add(controls);
        var branding = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        branding.Children.Add(Text("♜", 28));
        var wordmark = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        wordmark.Children.Add(new TextBlock { Text = "Display Regent", FontFamily = new FontFamily("Georgia"), FontSize = 23, Foreground = ink });
        wordmark.Children.Add(Text("Knight AI+AV", 10, true)); branding.Children.Add(wordmark); titlebar.Children.Add(branding);
        root.Children.Add(titlebar);
        var introduction = new DockPanel { Margin = new Thickness(0, 0, 0, 18) }; Grid.SetRow(introduction, 1);
        count.Foreground = muted; count.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(count, Dock.Right); introduction.Children.Add(count);
        var heading = new StackPanel(); heading.Children.Add(Text("Your screens. Your arrangement.", 25)); heading.Children.Add(Text("Select a screen to edit it. Drag its frame to arrange your desktop.", 13, true)); introduction.Children.Add(heading); root.Children.Add(introduction);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(244) }); Grid.SetRow(body, 2);
        var mapPanel = new Border { Background = panel, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 18, 0) };
        var mapGrid = new Grid(); mapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); mapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mapGrid.Children.Add(map);
        var arrange = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14) }; Grid.SetRow(arrange, 1);
        arrange.Children.Add(Button("↔ Arrange", () => { Layout.Arrange(displays, false); Changed(); })); arrange.Children.Add(Button("↕ Stack", () => { Layout.Arrange(displays, true); Changed(); })); arrange.Children.Add(Button("Identify", Identify));
        mapGrid.Children.Add(arrange); mapPanel.Child = mapGrid; body.Children.Add(mapPanel);
        inspector.Margin = new Thickness(0, 8, 0, 0); var scroll = new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll); root.Children.Add(body);
        var presetRegion = new StackPanel { Margin = new Thickness(0, 20, 0, 16) }; Grid.SetRow(presetRegion, 3);
        presetRegion.Children.Add(Text("Scenes", 16)); presets.Orientation = Orientation.Vertical; presets.Margin = new Thickness(0, 9, 0, 0); presetRegion.Children.Add(presets); root.Children.Add(presetRegion);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetRow(footer, 4);
        status.Foreground = muted; status.FontSize = 12; status.Margin = new Thickness(0, 0, 16, 0); status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(status); Grid.SetColumn(actions, 1); footer.Children.Add(actions); root.Children.Add(footer);
        Content = root; DrawActions(); DrawPresets(); DrawInspector(); DrawMap();
    }
    private void Refresh()
    {
        try
        {
            string? id = selected?.Id;
            displays = demo ? DemoDisplays() : DisplayService.Discover(prefs);
            selected = displays.FirstOrDefault(d => d.Id == id) ?? displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
            dirty = false; status.Text = "Changes stay in draft until you apply. Ctrl + Alt + M opens this panel.";
            DrawMap(); DrawInspector(); DrawActions();
        }
        catch (Exception e) { status.Text = e.Message; }
    }
    private List<Display> DemoDisplays() => new()
    {
        new() { Id = "demo-1", Name = "Studio 4K", Number = 1, Width = 3840, Height = 2160, X = 0, Y = 0, Enabled = true, Primary = true, Connection = "DisplayPort", Refresh = 60, Resolutions = new() { new(3840,2160), new(2560,1440) } },
        new() { Id = "demo-2", Name = "Portrait companion", Number = 2, Width = 1080, Height = 1920, X = 3840, Y = 0, Enabled = true, Connection = "HDMI", Refresh = 60, Resolutions = new() { new(1080,1920) } },
        new() { Id = "demo-3", Name = "Upper display", Number = 3, Width = 2560, Height = 1440, X = 640, Y = -1440, Enabled = false, Connection = "HDMI", Refresh = 60, Resolutions = new() { new(2560,1440) } }
    };
    private void Changed()
    {
        foreach (var clone in displays.Where(d => d.MirrorOf != ""))
        {
            var master = displays.FirstOrDefault(d => d.Id == clone.MirrorOf);
            if (master != null) { clone.X = master.X; clone.Y = master.Y; clone.Width = master.Width; clone.Height = master.Height; }
        }
        dirty = true; status.Text = "Draft ready. Apply when the layout looks right."; DrawMap(); DrawInspector(); DrawActions();
    }
    private void DrawMap()
    {
        if (dragging != null) return;
        map.Children.Clear(); count.Text = $"{displays.Count(d => d.Enabled)} on / {displays.Count} connected";
        if (displays.Count == 0 || map.ActualWidth < 100 || map.ActualHeight < 100) return;
        double minX = displays.Min(d => d.X), minY = displays.Min(d => d.Y);
        double width = displays.Max(d => d.X + d.Width) - minX, height = displays.Max(d => d.Y + d.Height) - minY;
        scale = Math.Min((map.ActualWidth - 52) / width, (map.ActualHeight - 54) / height);
        double offsetX = (map.ActualWidth - width * scale) / 2, offsetY = (map.ActualHeight - height * scale) / 2;
        foreach (var d in displays)
        {
            if (d.MirrorOf != "" && displays.Any(master => master.Id == d.MirrorOf)) continue;
            var color = Brush(Colors[(d.Number - 1) % Colors.Length]);
            var tile = new Border
            {
                Width = d.Width * scale, Height = d.Height * scale, BorderBrush = color,
                BorderThickness = new Thickness(selected == d ? 1.8 : 1), CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb((byte)(d.Enabled ? 28 : 8), color.Color.R, color.Color.G, color.Color.B)),
                Opacity = d.Enabled ? 1 : 0.6, Cursor = Cursors.Hand,
                ToolTip = $"{d.Name}\n{d.Width} × {d.Height}\n{d.Connection}\nDesktop position {d.X}, {d.Y}",
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, 1)
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, $"Display {d.Number}, {d.Name}, {(d.Enabled ? "on" : "off")}");
            tile.Focusable = true;
            var contents = new Grid { ClipToBounds = true };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6) };
            double size = Math.Min(32, Math.Max(18, tile.Height / 4));
            text.Children.Add(new TextBlock { Text = d.Number.ToString(), FontFamily = new FontFamily("Georgia"), FontSize = size, Foreground = prefs.Theme == "light" ? ink : color, HorizontalAlignment = HorizontalAlignment.Center });
            if (tile.Width > 95 && tile.Height > 100)
            {
                text.Children.Add(new TextBlock { Text = d.Name, Foreground = ink, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Max(40, tile.Width - 18), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
                text.Children.Add(new TextBlock { Text = $"{d.Width} × {d.Height}", Foreground = muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            }
            if (tile.Height > 76) text.Children.Add(new TextBlock { Text = d.Enabled ? (d.Primary ? "✦ Primary" : "On") : "Off", Foreground = prefs.Theme == "light" ? ink : color, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var clone in displays.Where(c => c.MirrorOf == d.Id))
            {
                var mirror = Button($"{clone.Number}  {clone.Name} · {(clone.Enabled ? "Mirrored" : "Off")}", () => { selected = clone; DrawInspector(); });
                mirror.Padding = new Thickness(5, 2, 5, 2); mirror.MinHeight = 24; mirror.FontSize = 10; mirror.Margin = new Thickness(0, 5, 0, 0);
                mirror.BorderBrush = Brush(Colors[(clone.Number - 1) % Colors.Length]); text.Children.Add(mirror);
            }
            contents.Children.Add(text);
            // Four restrained calligraphic corners, with a tiny spear tip.
            var ornament = new Ornament(color) { IsHitTestVisible = false }; contents.Children.Add(ornament);
            tile.Child = contents; Canvas.SetLeft(tile, offsetX + (d.X - minX) * scale); Canvas.SetTop(tile, offsetY + (d.Y - minY) * scale); map.Children.Add(tile);
            tile.MouseEnter += (_, _) => { if (dragging == null && SystemParameters.ClientAreaAnimation) AnimateTile(tile, 1.018); };
            tile.MouseLeave += (_, _) => { if (dragging == null) AnimateTile(tile, 1); };
            tile.MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is FrameworkElement element && FindButton(element)) return;
                if (before != null) return;
                selected = d; dragStart = e.GetPosition(map); originalX = d.X; originalY = d.Y; dragging = tile; moved = false;
                AnimateTile(tile, 1); tile.CaptureMouse(); DrawInspector(); e.Handled = true;
            };
            tile.MouseMove += (_, e) =>
            {
                if (dragging != tile || e.LeftButton != MouseButtonState.Pressed) return;
                Point now = e.GetPosition(map); Vector delta = now - dragStart;
                if (delta.Length > 4) moved = true;
                if (!moved) return;
                int x = originalX + (int)Math.Round(delta.X / scale), y = originalY + (int)Math.Round(delta.Y / scale);
                int snap = (int)(12 / scale);
                foreach (var other in displays.Where(o => o != d))
                {
                    foreach (int edge in new[] { other.X + other.Width, other.X - d.Width, other.X }) if (Math.Abs(x - edge) < snap) x = edge;
                    foreach (int edge in new[] { other.Y + other.Height, other.Y - d.Height, other.Y }) if (Math.Abs(y - edge) < snap) y = edge;
                }
                d.X = x; d.Y = y;
                Canvas.SetLeft(tile, offsetX + (d.X - minX) * scale); Canvas.SetTop(tile, offsetY + (d.Y - minY) * scale);
            };
            tile.MouseLeftButtonUp += (_, e) => { if (dragging != tile) return; tile.ReleaseMouseCapture(); dragging = null; if (moved) Changed(); else { DrawMap(); DrawInspector(); } e.Handled = true; };
            tile.KeyDown += (_, e) => { if (before == null && (e.Key == Key.Enter || e.Key == Key.Space)) { selected = d; DrawMap(); DrawInspector(); e.Handled = true; } };
        }
    }
    private static bool FindButton(DependencyObject element)
    {
        while (element != null) { if (element is Button) return true; element = VisualTreeHelper.GetParent(element); }
        return false;
    }
    private void AnimateTile(Border tile, double to)
    {
        var transform = (ScaleTransform)tile.RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(130)));
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(130)));
    }
    private void DrawInspector()
    {
        inspector.Children.Clear(); var d = selected;
        if (d == null) { inspector.Children.Add(Text("No connected displays", 18)); return; }
        inspector.IsEnabled = before == null;
        var selectors = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var monitor in displays)
        {
            var select = Button(monitor.Number.ToString(), () => { selected = monitor; DrawMap(); DrawInspector(); });
            select.ToolTip = monitor.Name; select.Padding = new Thickness(12, 6, 12, 6); select.BorderBrush = Brush(Colors[(monitor.Number - 1) % Colors.Length]); selectors.Children.Add(select);
        }
        inspector.Children.Add(selectors);
        inspector.Children.Add(Text($"Display {d.Number}", 12, true)); inspector.Children.Add(Text(d.Name, 20));
        inspector.Children.Add(new TextBlock { Text = d.Connection + (d.Enabled && d.Refresh > 0 ? $"  ·  {d.Refresh:0.##} Hz" : ""), Foreground = muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 20) });
        var toggle = new CheckBox { Content = "Use this display", IsChecked = d.Enabled, Foreground = ink, FontSize = 15, Margin = new Thickness(0, 0, 0, 16) };
        toggle.Click += (_, _) => { d.Enabled = toggle.IsChecked == true; if (!d.Enabled) d.Primary = false; if (!displays.Any(x => x.Enabled && x.Primary)) { var first = displays.FirstOrDefault(x => x.Enabled); if (first != null) first.Primary = true; } Changed(); }; inspector.Children.Add(toggle);
        var primary = Button(d.Primary ? "✦ Primary display" : "Make primary", () =>
        {
            if (d.MirrorOf != "")
            {
                string oldMaster = d.MirrorOf; d.MirrorOf = "";
                foreach (var member in displays.Where(p => p.Id == oldMaster || p.MirrorOf == oldMaster)) member.MirrorOf = d.Id;
            }
            foreach (var p in displays) p.Primary = p == d; d.Enabled = true; Changed();
        }); primary.IsEnabled = !d.Primary; inspector.Children.Add(primary);
        inspector.Children.Add(new TextBlock { Text = "Desktop mode", Foreground = muted, FontSize = 12, Margin = new Thickness(0, 18, 0, 6) });
        var masters = displays.Where(p => p != d && p.MirrorOf == "" && p.Enabled).ToList();
        var modes = new[] { "Extend desktop" }.Concat(masters.Select(p => $"Mirror {p.Number} · {p.Name}")).ToArray();
        var mirrorMode = new ComboBox { ItemsSource = modes, SelectedIndex = d.MirrorOf == "" ? 0 : masters.FindIndex(m => m.Id == d.MirrorOf) + 1, Padding = new Thickness(8), Foreground = ink, Background = panel };
        mirrorMode.SelectionChanged += (_, _) =>
        {
            string target = mirrorMode.SelectedIndex > 0 ? masters[mirrorMode.SelectedIndex - 1].Id : "";
            if (target == d.MirrorOf) return;
            if (displays.Any(p => p.MirrorOf == d.Id)) { status.Text = "Extend the displays that mirror this screen first."; DrawInspector(); return; }
            d.MirrorOf = target; if (target != "") { d.Primary = false; d.Enabled = true; }
            Changed();
        }; inspector.Children.Add(mirrorMode);
        inspector.Children.Add(new TextBlock { Text = "Resolution", Foreground = muted, FontSize = 12, Margin = new Thickness(0, 22, 0, 6) });
        var resolution = new ComboBox { ItemsSource = d.Resolutions, SelectedItem = d.Resolutions.FirstOrDefault(r => r.Width == d.Width && r.Height == d.Height), Padding = new Thickness(8), MinHeight = 36, Foreground = ink, Background = panel, IsEnabled = d.MirrorOf == "" };
        resolution.SelectionChanged += (_, _) => { if (resolution.SelectedItem is Resolution r && (r.Width != d.Width || r.Height != d.Height)) { d.Width = r.Width; d.Height = r.Height; Changed(); } }; inspector.Children.Add(resolution);
        inspector.Children.Add(new TextBlock { Text = "Desktop position", Foreground = muted, FontSize = 12, Margin = new Thickness(0, 20, 0, 6) });
        var coordinates = new StackPanel { Orientation = Orientation.Horizontal };
        var x = new TextBox { Text = d.X.ToString(), Width = 83, Padding = new Thickness(8), Background = panel, Foreground = ink, BorderBrush = line, ToolTip = "Horizontal desktop position in pixels" };
        var y = new TextBox { Text = d.Y.ToString(), Width = 83, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8), Background = panel, Foreground = ink, BorderBrush = line, ToolTip = "Vertical desktop position in pixels" };
        coordinates.Children.Add(x); coordinates.Children.Add(y); inspector.Children.Add(coordinates);
        var place = Button("Set position", () => { if (!int.TryParse(x.Text, out int px) || !int.TryParse(y.Text, out int py) || Math.Abs((long)px) > 100000 || Math.Abs((long)py) > 100000) throw new InvalidOperationException("Enter a position between −100000 and 100000 pixels."); d.X = px; d.Y = py; Changed(); }); place.IsEnabled = d.MirrorOf == ""; place.Margin = new Thickness(0, 8, 0, 0); inspector.Children.Add(place);
        inspector.Children.Add(new TextBlock { Text = "Frame sizes follow resolution. Positions follow your Windows desktop. Turning a display off removes its desktop signal; it does not power off the monitor.", Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 22, 0, 0) });
    }
    private void DrawPresets()
    {
        presets.Children.Clear(); var row = new WrapPanel();
        foreach (var p in prefs.Presets)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 6) };
            var b = Button(p.Name + (p.Hotkey > 0 ? $"  ·  Ctrl Alt {p.Hotkey}" : ""), () => ApplyPreset(p)); b.IsEnabled = before == null; group.Children.Add(b);
            var delete = Button("×", () => { prefs.Presets.Remove(p); Store.Save(prefs); RegisterKeys(); DrawPresets(); }); delete.ToolTip = "Delete " + p.Name; delete.IsEnabled = before == null; group.Children.Add(delete); row.Children.Add(group);
        }
        var save = Button("+ Save scene", SavePreset); save.IsEnabled = before == null; row.Children.Add(save); presets.Children.Add(row);
        if (prefs.Presets.Count == 0) presets.Children.Add(Text("Save a setup for work, gaming or one screen. Give it a keyboard shortcut.", 12, true));
    }
    private void DrawActions()
    {
        quick?.Render();
        actions.Children.Clear();
        if (before != null)
        { actions.Children.Add(Button("Revert", Revert)); actions.Children.Add(Button($"Keep changes · {seconds}s", Keep, true)); }
        else
        { actions.Children.Add(Button("Refresh", Refresh)); var apply = Button("Apply layout", Apply, true); apply.IsEnabled = dirty && !demo; actions.Children.Add(apply); }
    }
    private void Apply()
    {
        if (before != null || demo) return;
        string? activeTicket = null; Snapshot? original = null;
        try
        {
            var requested = DisplayService.Build(displays); DisplayService.Validate(requested);
            var normalized = Layout.Normalize(displays);
            foreach (var item in normalized) { var draft = displays.First(d => d.Id == item.Id); draft.X = item.X; draft.Y = item.Y; draft.Width = item.Width; draft.Height = item.Height; draft.MirrorOf = item.MirrorOf; draft.Primary = item.Primary; }
            original = DisplayService.Query(); activeTicket = Recovery.Start(original);
            before = original; ticket = activeTicket;
            var elapsed = Stopwatch.StartNew(); DisplayService.Apply(requested); Recovery.Arm(activeTicket);
            DisplayService.Verify(displays); elapsed.Stop();
            seconds = 20; confirmationClock.Restart(); confirmTimer.Start(); DrawActions(); DrawInspector(); DrawPresets();
            status.Text = $"Layout applied. Keep it, or it will revert automatically."; if (quickOperation) ShowQuick(); else BringForward();
        }
        catch (Exception e)
        {
            if (original != null)
            {
                try { DisplayService.Apply(original); if (activeTicket != null) Recovery.Confirm(activeTicket); }
                catch { /* The independently running recovery process will retry. */ }
            }
            before = null; ticket = null; confirmTimer.Stop(); Refresh(); status.Text = e.Message; DrawActions(); DrawInspector(); DrawPresets();
        }
    }
    private void Keep()
    {
        if (before == null || ticket == null) return;
        DisplayService.Verify(displays);
        Recovery.Confirm(ticket); confirmTimer.Stop(); before = null; ticket = null;
        prefs.Remembered = displays.Select(d => d.Copy()).ToList(); Store.Save(prefs);
        Refresh(); DrawPresets(); status.Text = "Layout kept. Your screens are ready.";
    }
    private void Revert()
    {
        if (before == null) return;
        try
        {
            DisplayService.Apply(before); if (ticket != null) Recovery.Confirm(ticket);
            confirmTimer.Stop(); before = null; ticket = null; Refresh(); DrawPresets(); status.Text = "Previous layout restored."; if (quickOperation) { quick?.Render(); if (quick?.IsVisible == true) ShowQuick(); } else BringForward();
        }
        catch (Exception e) { status.Text = "Recovery is retrying. " + e.Message; }
    }
    private void ApplyPreset(Preset preset)
    {
        if (before != null) return;
        Refresh(); Layout.MatchPreset(displays, preset); Changed(); Apply();
    }
    private void SavePreset()
    {
        Layout.Normalize(displays);
        var dialog = Dialog("Save this scene", out var body);
        body.Children.Add(Text("Scene name", 13));
        var name = new TextBox { Text = "My setup", Padding = new Thickness(10), MaxLength = 36, Margin = new Thickness(0, 8, 0, 16) }; body.Children.Add(name);
        body.Children.Add(Text("Shortcut", 13));
        var options = new[] { "No shortcut" }.Concat(Enumerable.Range(1, 9).Select(i => $"Ctrl + Alt + {i}")).ToArray();
        var key = new ComboBox { ItemsSource = options, SelectedIndex = 0, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 18) }; body.Children.Add(key);
        var error = Text("", 12); body.Children.Add(error);
        body.Children.Add(Button("Save scene", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "Give this scene a name."; return; }
            if (prefs.Presets.Any(p => p.Name.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase))) { error.Text = "A scene already uses this name."; return; }
            if (key.SelectedIndex > 0 && prefs.Presets.Any(p => p.Hotkey == key.SelectedIndex)) { error.Text = "That shortcut belongs to another scene."; return; }
            prefs.Presets.Add(new Preset { Name = name.Text.Trim(), Hotkey = key.SelectedIndex, Displays = displays.Select(d => d.Copy()).ToList() }); Store.Save(prefs); RegisterKeys(); DrawPresets(); dialog.Close();
        }, true)); dialog.ShowDialog();
    }
    private void CreateStarterScenes()
    {
        try
        {
            Layout.Normalize(displays);
            if (prefs.Presets.Count == 0)
            {
                prefs.Presets.Add(new Preset { Name = "Everyday", Hotkey = 1, Displays = displays.Select(d => d.Copy()).ToList() });
                var focus = displays.Select(d => d.Copy()).ToList(); var primary = focus.FirstOrDefault(d => d.Primary) ?? focus.First(d => d.Enabled);
                foreach (var d in focus) { d.Enabled = d.Id == primary.Id; d.Primary = d.Id == primary.Id; d.MirrorOf = ""; }
                prefs.Presets.Add(new Preset { Name = "Focus", Hotkey = 2, Displays = focus });
                var desk = displays.Select(d => d.Copy()).ToList(); foreach (var d in desk.Where(d => d.MirrorOf != "")) d.Enabled = false;
                if (desk.Count(d => d.Enabled) > 1 && desk.Count(d => d.Enabled) < displays.Count(d => d.Enabled)) prefs.Presets.Add(new Preset { Name = "Desk", Hotkey = 3, Displays = desk });
            }
            prefs.Initialized = true; prefs.Remembered = displays.Select(d => d.Copy()).ToList(); Store.Save(prefs); RegisterKeys(); DrawPresets();
        }
        catch (Exception e) { status.Text = "Save your first scene when the display layout is ready. " + e.Message; }
    }
    private Window Dialog(string title, out StackPanel body)
    {
        body = new StackPanel { Margin = new Thickness(26) }; body.Children.Add(Text(title, 22));
        body.Children.Add(new Border { Height = 18 });
        var dialog = new Window { Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 390, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Background = surface, Foreground = ink, Content = body, Topmost = Topmost };
        dialog.Resources.MergedDictionaries.Add(Resources); return dialog;
    }
    private void Settings()
    {
        var dialog = Dialog("Just the essentials", out var body);
        var top = new CheckBox { Content = "Keep panel above other windows", IsChecked = prefs.Topmost, Foreground = ink, Margin = new Thickness(0, 0, 0, 18) };
        top.Click += (_, _) => { prefs.Topmost = top.IsChecked == true; Topmost = prefs.Topmost; Store.Save(prefs); }; body.Children.Add(top);
        var startup = new CheckBox { Content = "Open in the tray when I sign in", IsChecked = prefs.Startup, Foreground = ink, Margin = new Thickness(0, 0, 0, 18) };
        startup.Click += (_, _) =>
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (startup.IsChecked == true) run.SetValue("DisplayRegent", $"\"{Environment.ProcessPath}\" --tray"); else run.DeleteValue("DisplayRegent", false);
            prefs.Startup = startup.IsChecked == true; Store.Save(prefs);
        }; body.Children.Add(startup);
        body.Children.Add(Text("Ctrl + Alt + M opens the panel. Scene shortcuts use Ctrl + Alt + 1–9. Each switch has a 20-second recovery check.", 13, true));
        body.Children.Add(new Border { Height = 18 }); body.Children.Add(Button("Open local data", () => Process.Start(new ProcessStartInfo(Store.DirectoryPath) { UseShellExecute = true })));
        body.Children.Add(new Border { Height = 18 }); body.Children.Add(Text("Display Regent 0.1.0 preview\nMIT licensed. No accounts, telemetry or network access.", 12, true)); dialog.ShowDialog();
    }
    private void ToggleTheme() { prefs.Theme = prefs.Theme == "dark" ? "light" : "dark"; if (!capturing) Store.Save(prefs); SetTheme(); BuildShell(); }
    private void Identify()
    {
        if (demo) return;
        // Screen bounds are in physical pixels, WPF positions in DIPs. A native
        // helper positions each small window after its HWND is created.
        foreach (var d in DisplayService.Discover(prefs).Where(d => d.Enabled))
        {
            var label = new Window { Width = 210, Height = 125, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Background = panel, Content = new TextBlock { Text = d.Number.ToString(), FontFamily = new FontFamily("Georgia"), FontSize = 65, Foreground = Brush(Colors[(d.Number - 1) % Colors.Length]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            label.SourceInitialized += (_, _) => PositionWindow(new WindowInteropHelper(label).Handle, d.X + d.Width / 2 - 105, d.Y + d.Height / 2 - 62, 210, 125);
            label.Show(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; timer.Tick += (_, _) => { timer.Stop(); label.Close(); }; timer.Start();
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private static void PositionWindow(IntPtr hwnd, int x, int y, int w, int h, bool onTop = true) => SetWindowPos(hwnd, new IntPtr(onTop ? -1 : -2), x, y, w, h, 0x0010);
    internal void BringForward()
    {
        quickOperation = false; quick?.Hide();
        Show(); WindowState = WindowState.Normal;
        var primary = Forms.Screen.PrimaryScreen;
        if (primary != null)
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new IntPtr(Topmost ? -1 : -2), primary.WorkingArea.Left + 16, primary.WorkingArea.Top + 16, 0, 0, 0x0011);
            double dpi = GetDpiForWindow(handle) / 96d;
            int width = Math.Min(primary.WorkingArea.Width, (int)(Width * dpi)), height = Math.Min(primary.WorkingArea.Height, (int)(Height * dpi));
            PositionWindow(handle, primary.WorkingArea.Left + Math.Max(0, (primary.WorkingArea.Width - width) / 2), primary.WorkingArea.Top + Math.Max(0, (primary.WorkingArea.Height - height) / 2), width, height, Topmost);
        }
        Activate();
    }
    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Quick widget", null, (_, _) => Dispatcher.Invoke(ShowQuick));
        menu.Items.Add("Full panel", null, (_, _) => Dispatcher.Invoke(BringForward));
        menu.Items.Add("Refresh displays", null, (_, _) => Dispatcher.Invoke(Refresh)); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(() => { if (before != null) Revert(); exiting = true; Close(); Application.Current.Shutdown(); }));
        tray = new Forms.NotifyIcon { Text = "Display Regent", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowQuickFromTray); };
    }
    private void RegisterKeys()
    {
        if (hwnd == null || capturing) return;
        foreach (var id in hotkeys) Native.UnregisterHotKey(hwnd.Handle, id); hotkeys.Clear();
        Register(100, 0x4d, "Ctrl + Alt + M");
        foreach (var p in prefs.Presets.Where(p => p.Hotkey > 0)) Register(p.Hotkey, (uint)(0x30 + p.Hotkey), $"Ctrl + Alt + {p.Hotkey}");
    }
    private void Register(int id, uint key, string name)
    {
        if (Native.RegisterHotKey(hwnd!.Handle, id, 0x4003, key)) hotkeys.Add(id);
        else status.Text = $"{name} is used by another app. This shortcut was not registered.";
    }
    private IntPtr MessageHook(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x312)
        {
            handled = true; int id = w.ToInt32();
            try { if (id == 100) ShowQuick(); else { var preset = prefs.Presets.FirstOrDefault(p => p.Hotkey == id); if (preset != null) { ShowQuick(); QuickScene(preset); } } }
            catch (Exception e) { status.Text = e.Message; ShowQuick(); }
        }
        if (message == 0x7e || message == 0x219) { hotplugTimer.Stop(); hotplugTimer.Start(); }
        return IntPtr.Zero;
    }
    private void SetupShowSignal()
    {
        var signal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "Local\\DisplayRegentShow");
        System.Threading.ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Dispatcher.BeginInvoke(ShowQuick), null, -1, false);
    }
    private async void Capture()
    {
        try
        {
            Directory.CreateDirectory(CaptureDirectory!);
            if (demo) prefs.Presets = new List<Preset> { new() { Name = "Everyday", Displays = displays.Select(d => d.Copy()).ToList() }, new() { Name = "Focus", Displays = displays.Select(d => { var copy = d.Copy(); copy.Enabled = d.Primary; return copy; }).ToList() } };
            foreach (string theme in new[] { "dark", "light" })
            {
                prefs.Theme = theme; SetTheme(); BuildShell();
                await System.Threading.Tasks.Task.Delay(250); UpdateLayout(); DrawMap(); UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)ActualWidth * 2, (int)ActualHeight * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(this);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(CaptureDirectory!, $"regent-{(demo ? "demo" : "actual")}-{theme}.png")); png.Save(file);
                quick ??= new QuickWindow(this); quick.Render(); quick.Show(); quick.UpdateLayout();
                var widget = new RenderTargetBitmap((int)Math.Ceiling(quick.ActualWidth * 2), (int)Math.Ceiling(quick.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32); widget.Render(quick);
                var widgetPng = new PngBitmapEncoder(); widgetPng.Frames.Add(BitmapFrame.Create(widget));
                using var widgetFile = File.Create(Path.Combine(CaptureDirectory!, $"widget-{(demo ? "demo" : "actual")}-{theme}.png")); widgetPng.Save(widgetFile);
                quick.PreviewHover(); await System.Threading.Tasks.Task.Delay(240); quick.UpdateLayout();
                var hovered = new RenderTargetBitmap((int)Math.Ceiling(quick.ActualWidth * 2), (int)Math.Ceiling(quick.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32); hovered.Render(quick);
                var hoveredPng = new PngBitmapEncoder(); hoveredPng.Frames.Add(BitmapFrame.Create(hovered));
                using var hoveredFile = File.Create(Path.Combine(CaptureDirectory!, $"widget-{(demo ? "demo" : "actual")}-hover-{theme}.png")); hoveredPng.Save(hoveredFile); quick.Hide();
            }
        }
        finally { quick?.Close(); exiting = true; Close(); Application.Current.Shutdown(); }
    }
    private async void RunAcceptance()
    {
        string file = Path.Combine(Store.DirectoryPath, "preferences.json");
        byte[]? originalPreferences = File.Exists(file) ? File.ReadAllBytes(file) : null;
        try
        {
            if (displays.Count == 0 || selected == null) throw new InvalidOperationException("No displays in UI.");
            ToggleTheme(); ToggleTheme(); UpdateLayout(); Console.WriteLine("PASS light/dark controls rebuild without losing logical children.");
            if (tray?.Visible != true) throw new InvalidOperationException("Tray icon missing.");
            Console.WriteLine("PASS tray icon created.");
            if (!hotkeys.Contains(100)) throw new InvalidOperationException("Panel hotkey could not register.");
            Console.WriteLine("PASS Ctrl + Alt + M registered.");
            Hide(); ShowQuick(); quick!.UpdateLayout();
            if (IsVisible || !quick.IsVisible || quick.ShowInTaskbar || quick.ActualWidth > 520) throw new InvalidOperationException("Default widget is not compact or full panel opened.");
            quick.Render(); quick.Render();
            await quick.CheckMinimalSurface();
            bool widgetState = selected.Enabled; QuickToggle(selected, !widgetState);
            if (selected.Enabled == widgetState || !dirty) throw new InvalidOperationException("Widget toggle did not update shared draft.");
            QuickRefresh(); BringForward();
            if (!IsVisible || quick.IsVisible) throw new InvalidOperationException("Full panel did not replace widget.");
            Console.WriteLine("PASS compact tray widget, repeated rendering, shared draft and explicit full-panel navigation.");
            bool oldState = selected.Enabled;
            var check = inspector.Children.OfType<CheckBox>().First(); check.IsChecked = !oldState; check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (selected.Enabled == oldState || !dirty) throw new InvalidOperationException("Display toggle did not update draft.");
            Refresh(); Console.WriteLine("PASS display checkbox updates draft; Refresh restores actual state.");
            int originalCount = prefs.Presets.Count;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().First(w => w.Title == "Save this scene");
                var body = (StackPanel)dialog.Content; body.Children.OfType<TextBox>().First().Text = "Acceptance check";
                body.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }), DispatcherPriority.Background);
            SavePreset();
            if (prefs.Presets.Count != originalCount + 1) throw new InvalidOperationException("Scene was not saved.");
            var test = prefs.Presets.Last(); var loaded = Store.Load(); if (!loaded.Presets.Any(p => p.Name == "Acceptance check" && p.Displays.Count == displays.Count)) throw new InvalidOperationException("Scene persistence failed.");
            prefs.Presets.Remove(test); Store.Save(prefs); DrawPresets(); Console.WriteLine("PASS save-scene dialog and persisted display configuration.");
            Identify(); Console.WriteLine("PASS monitor identification windows created.");
            Console.WriteLine("UI acceptance checks passed.");
        }
        catch (Exception e) { Console.Error.WriteLine("UI acceptance failed: " + e.Message); Environment.ExitCode = 1; }
        finally
        {
            if (originalPreferences == null) { if (File.Exists(file)) File.Delete(file); } else File.WriteAllBytes(file, originalPreferences);
            exiting = true; Close(); foreach (Window w in Application.Current.Windows.OfType<Window>().ToList()) w.Close(); Application.Current.Shutdown(Environment.ExitCode);
        }
    }
}

internal sealed class Ornament : FrameworkElement
{
    private readonly Brush brush;
    public Ornament(Brush color) { brush = color; }
    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(brush, .8);
        foreach (var corner in new[] { (0d, 0d, 1d, 1d), (ActualWidth, 0d, -1d, 1d), (0d, ActualHeight, 1d, -1d), (ActualWidth, ActualHeight, -1d, -1d) })
        {
            double x = corner.Item1, y = corner.Item2, sx = corner.Item3, sy = corner.Item4;
            Point P(double a, double b) => new(x + a * sx, y + b * sy);
            var curve = new StreamGeometry(); using (var c = curve.Open()) { c.BeginFigure(P(22, 5), false, false); c.BezierTo(P(4, 5), P(18, 17), P(5, 22), true, false); }
            dc.DrawGeometry(null, pen, curve);
            var tip = new StreamGeometry(); using (var c = tip.Open()) { c.BeginFigure(P(4, 4), true, true); c.LineTo(P(11, 6), true, false); c.LineTo(P(6, 11), true, false); }
            dc.DrawGeometry(brush, null, tip);
        }
    }
}

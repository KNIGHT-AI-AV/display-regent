using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace DisplayRegent;

internal static class Program
{
    private static Mutex? mutex;
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            if (args.Any(a => a is "--install-silent" or "--uninstall" or "--remove-after") || Path.GetFileName(Environment.ProcessPath!).EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                return RegentSetup.Program.Run(args);
            if (args.Length == 3 && args[0] == "--watchdog") return Recovery.Watch(args[1], int.Parse(args[2]));
            if (args.Contains("--probe"))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { displays = DisplayService.Discover(Store.Load()).Select(d => new { d.Number, d.Name, d.Connection, d.Width, d.Height, d.X, d.Y, d.Enabled, d.Primary, d.Refresh, resolutions = d.Resolutions }), nativeSizes = new { path = Marshal.SizeOf<Native.DisplayPath>(), mode = Marshal.SizeOf<Native.Mode>(), devMode = Marshal.SizeOf<Native.DevMode>(), preferred = Marshal.SizeOf<Native.PreferredMode>() } }, Store.Json));
                return 0;
            }
            if (args.Contains("--validate-current"))
            {
                var current = DisplayService.Discover(Store.Load());
                DisplayService.Validate(DisplayService.Build(current));
                Console.WriteLine("Current display layout passed Windows validation."); return 0;
            }
            if (args.Contains("--exercise-switch")) return ExerciseSwitch();
            if (args.Contains("--exercise-recovery"))
            {
                var current = DisplayService.Discover(Store.Load());
                if (current.Count(d => d.Enabled) < 2) throw new InvalidOperationException("Recovery test requires two active displays.");
                var desired = current.Select(d => d.Copy()).ToList(); var primary = desired.First(d => d.Primary);
                foreach (var d in desired) d.Enabled = d.Id == primary.Id;
                var requested = DisplayService.Build(desired); DisplayService.Validate(requested);
                string recoveryTicket = Recovery.Start(DisplayService.Query());
                DisplayService.Apply(requested); Recovery.Arm(recoveryTicket); DisplayService.Verify(desired);
                Console.WriteLine("Main app is exiting with primary-only active. Independent recovery must restore the original layout when the main app exits.");
                return 0;
            }
            bool capture = args.Contains("--capture");
            if (!capture)
            {
                mutex = new Mutex(true, "Local\\DisplayRegent", out bool created);
                if (!created)
                {
                    EventWaitHandle.TryOpenExisting("Local\\DisplayRegentShow", out var existing);
                    existing?.Set(); existing?.Dispose(); return 0;
                }
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow(capture, args.Contains("--demo"));
            if (capture) window.CaptureDirectory = args.SkipWhile(a => a != "--capture").Skip(1).FirstOrDefault() ?? Path.Combine(Store.DirectoryPath, "captures");
            app.MainWindow = window;
            if (args.Contains("--ui-check")) window.AcceptanceCheck = true;
            if (capture || args.Contains("--ui-check") || args.Contains("--full")) window.Show();
            else { window.Opacity = 0; window.Show(); window.Hide(); window.Opacity = 1; if (!args.Contains("--tray")) window.ShowQuick(); }
            return app.Run();
        }
        catch (Exception e)
        {
            if (args.Length > 0) Console.Error.WriteLine(e.Message);
            else MessageBox.Show(e.Message, "Display Regent", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        finally { mutex?.Dispose(); }
    }
    private static int ExerciseSwitch()
    {
        var prefs = Store.Load(); var current = DisplayService.Discover(prefs);
        if (current.Count(d => d.Enabled) < 2) throw new InvalidOperationException("The switching test requires two active displays.");
        var desired = current.Select(d => d.Copy()).ToList();
        var primary = desired.First(d => d.Primary);
        foreach (var d in desired) d.Enabled = d.Id == primary.Id;
        var requested = DisplayService.Build(desired); DisplayService.Validate(requested);
        var original = DisplayService.Query(); var ticket = Recovery.Start(original);
        try
        {
            var time = Stopwatch.StartNew(); DisplayService.Apply(requested); Recovery.Arm(ticket);
            DisplayService.Verify(desired); time.Stop();
            Console.WriteLine($"Primary-only switch verified in {time.ElapsedMilliseconds} ms; restoring in 2 seconds.");
            Thread.Sleep(2000);
            DisplayService.Apply(original); DisplayService.Verify(current); Recovery.Confirm(ticket);
            Console.WriteLine("Original active targets, resolutions and positions restored and verified.");
            void Exercise(string name, List<Display> next)
            {
                var config = DisplayService.Build(next); DisplayService.Validate(config);
                var checkTicket = Recovery.Start(original);
                try { DisplayService.Apply(config); Recovery.Arm(checkTicket); DisplayService.Verify(next); Console.WriteLine(name + " verified."); }
                finally { DisplayService.Apply(original); DisplayService.Verify(current); Recovery.Confirm(checkTicket); }
            }
            var extended = current.Select(d => d.Copy()).ToList(); foreach (var d in extended) d.MirrorOf = ""; Layout.Arrange(extended, false); Exercise("All displays extended", extended);
            var newPrimary = current.Select(d => d.Copy()).ToList(); var other = newPrimary.FirstOrDefault(d => d.Enabled && !d.Primary && d.MirrorOf == "");
            if (other != null) { foreach (var d in newPrimary) d.Primary = d == other; Exercise("Secondary display made primary", newPrimary); }
            var lower = current.Select(d => d.Copy()).ToList(); var main = lower.First(d => d.Primary);
            var resolution = main.Resolutions.FirstOrDefault(r => r.Width == 1920 && r.Height == 1080);
            if (resolution != null) { main.Width = resolution.Width; main.Height = resolution.Height; foreach (var d in lower.Where(d => d.MirrorOf == main.Id)) { d.Width = main.Width; d.Height = main.Height; } Exercise("Primary resolution changed to 1920 × 1080", lower); }
            Console.WriteLine("Hardware suite completed; original configuration restored after every case."); return 0;
        }
        catch { try { DisplayService.Apply(original); Recovery.Confirm(ticket); } catch { /* Watchdog remains armed. */ } throw; }
    }
}

internal static class Recovery
{
    public static string Start(Snapshot original)
    {
        Directory.CreateDirectory(Store.DirectoryPath);
        string ticket = Path.Combine(Store.DirectoryPath, "recovery-" + Guid.NewGuid().ToString("N"));
        DisplayService.SaveSnapshot(ticket + ".bin", original);
        var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--watchdog", ticket, Environment.ProcessId.ToString() } });
        if (process == null) throw new IOException("Could not start display recovery. No changes were applied.");
        for (int i = 0; i < 500 && !File.Exists(ticket + ".ready") && !process.HasExited; i++) Thread.Sleep(20);
        if (!File.Exists(ticket + ".ready")) { Confirm(ticket); throw new IOException("Display recovery did not start. No changes were applied."); }
        process.Dispose(); return ticket;
    }
    public static void Arm(string ticket) => File.WriteAllText(ticket + ".armed", "armed");
    public static void Confirm(string ticket) => File.WriteAllText(ticket + ".keep", "keep");
    public static int Watch(string ticket, int parentId)
    {
        // Keep recovery private to this user's app-data directory. A command line
        // cannot ask this process to deserialize or modify arbitrary paths.
        if (!Path.GetFullPath(ticket).StartsWith(Store.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 1;
        try
        {
            var original = DisplayService.LoadSnapshot(ticket + ".bin");
            Process? parent = null; try { parent = Process.GetProcessById(parentId); } catch (ArgumentException) { }
            using var observedParent = parent;
            bool ParentAlive() => observedParent != null && !observedParent.HasExited;
            File.WriteAllText(ticket + ".ready", "ready");
            var waiting = Stopwatch.StartNew();
            while (!File.Exists(ticket + ".armed") && !File.Exists(ticket + ".keep") && waiting.Elapsed < TimeSpan.FromSeconds(30) && ParentAlive()) Thread.Sleep(100);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(ticket + ".keep") && timer.Elapsed < TimeSpan.FromSeconds(25) && ParentAlive()) Thread.Sleep(100);
            if (!File.Exists(ticket + ".keep"))
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { DisplayService.Apply(original); File.WriteAllText(ticket + ".restored", "restored"); break; }
                    catch when (attempt < 2) { Thread.Sleep(500); }
                }
            }
            // The main app consumes .restored; do not remove it prematurely.
            foreach (var extension in new[] { ".bin", ".ready", ".armed", ".keep" }) if (File.Exists(ticket + extension)) File.Delete(ticket + extension);
            return 0;
        }
        catch (Exception e) { File.WriteAllText(ticket + ".error", e.Message); return 1; }
    }
}

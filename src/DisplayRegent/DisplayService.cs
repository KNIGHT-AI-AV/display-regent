using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DisplayRegent;

internal sealed record Snapshot(Native.DisplayPath[] Paths, Native.Mode[] Modes);
internal static class DisplayService
{
    public static Snapshot Query(bool all = false)
    {
        uint flags = all ? 1u : 2u;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Native.Check(Native.GetDisplayConfigBufferSizes(flags, out uint paths, out uint modes), "Read displays");
            var p = new Native.DisplayPath[paths]; var m = new Native.Mode[modes];
            int error = Native.QueryDisplayConfig(flags, ref paths, p, ref modes, m, IntPtr.Zero);
            if (error == 122) continue; // The topology changed between the two calls.
            Native.Check(error, "Read displays");
            return new(p.Take((int)paths).ToArray(), m.Take((int)modes).ToArray());
        }
        throw new IOException("Displays are changing. Wait a moment and refresh.");
    }
    public static List<Display> Discover(Preferences preferences)
    {
        var snapshot = Query(true);
        var displays = new List<Display>();
        foreach (var group in snapshot.Paths.Where(p => p.Target.Available).GroupBy(p => $"{p.Target.Adapter.Key}:{p.Target.Id}"))
        {
            var path = group.OrderByDescending(p => (p.Flags & 1) != 0).First();
            var target = new Native.TargetName { Header = Native.DeviceHeader<Native.TargetName>(2, path.Target.Adapter, path.Target.Id) };
            Native.Check(Native.GetTargetName(ref target), "Read monitor name");
            var source = new Native.SourceName { Header = Native.DeviceHeader<Native.SourceName>(1, path.Source.Adapter, path.Source.Id) };
            Native.GetSourceName(ref source);
            var preferred = new Native.PreferredMode { Header = Native.DeviceHeader<Native.PreferredMode>(3, path.Target.Adapter, path.Target.Id) };
            Native.GetPreferred(ref preferred);
            string id = string.IsNullOrWhiteSpace(target.DevicePath) ? group.Key : target.DevicePath;
            var saved = preferences.Remembered.FirstOrDefault(d => d.Id == id);
            bool active = (path.Flags & 1) != 0;
            var d = new Display
            {
                Id = id, Path = path, Name = string.IsNullOrWhiteSpace(target.Name) ? "Display" : target.Name,
                SourceName = source.Name ?? "", Enabled = active,
                Width = preferred.Width > 0 ? (int)preferred.Width : saved?.Width ?? 1920,
                Height = preferred.Height > 0 ? (int)preferred.Height : saved?.Height ?? 1080,
                X = saved?.X ?? displays.Sum(x => x.Width), Y = saved?.Y ?? 0,
                Refresh = path.Target.Refresh.Denominator == 0 ? 0 : (double)path.Target.Refresh.Numerator / path.Target.Refresh.Denominator,
                Connection = path.Target.OutputTechnology switch { 5 => "HDMI", 10 => "DisplayPort", 11 => "Internal DisplayPort", 6 => "Internal", 0 => "VGA", 4 => "DVI", 15 => "Wireless", 16 => "Indirect display", 0x80000000 => "Internal", _ => "Display connection" }
            };
            if (active && path.Source.ModeIndex < snapshot.Modes.Length)
            {
                var mode = snapshot.Modes[path.Source.ModeIndex].Source;
                d.Width = (int)mode.Width; d.Height = (int)mode.Height; d.X = mode.Position.X; d.Y = mode.Position.Y;
                d.Primary = d.X == 0 && d.Y == 0;
            }
            var choices = new HashSet<Resolution> { new(d.Width, d.Height) };
            // An inactive path may share a source with an active monitor. Only use
            // EnumDisplaySettings for active sources, never advertise another target's modes.
            if (active && !string.IsNullOrEmpty(d.SourceName))
            {
                var dm = new Native.DevMode { Size = (ushort)Marshal.SizeOf<Native.DevMode>(), DeviceName = "", FormName = "" };
                for (int i = 0; Native.EnumDisplaySettings(d.SourceName, i, ref dm); i++)
                    if (dm.BitsPerPel == 32) choices.Add(new((int)dm.Width, (int)dm.Height));
            }
            if (preferred.Width > 0) choices.Add(new((int)preferred.Width, (int)preferred.Height));
            d.Resolutions = choices.OrderByDescending(r => (long)r.Width * r.Height).ToList();
            displays.Add(d);
        }
        displays = displays.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        for (int i = 0; i < displays.Count; i++) displays[i].Number = i + 1;
        foreach (var group in displays.Where(d => d.Enabled).GroupBy(d => $"{d.Path.Source.Adapter.Key}:{d.Path.Source.Id}"))
        {
            var master = group.First();
            foreach (var clone in group.Skip(1)) { clone.MirrorOf = master.Id; clone.Primary = false; }
        }
        // First-seen inactive outputs have no OS position: park them beside the desktop.
        int right = displays.Where(d => d.Enabled).Select(d => d.X + d.Width).DefaultIfEmpty(0).Max();
        foreach (var d in displays.Where(d => !d.Enabled && !preferences.Remembered.Any(s => s.Id == d.Id))) { d.X = right; d.Y = 0; right += d.Width; }
        return displays;
    }
    public static Snapshot Build(List<Display> desired)
    {
        var enabled = Layout.Normalize(desired);
        var all = Query(true);
        var paths = new List<Native.DisplayPath>(); var modes = new List<Native.Mode>();
        var groups = enabled.GroupBy(d => d.MirrorOf == "" ? d.Id : d.MirrorOf).OrderByDescending(g => g.Any(d => d.Primary)).ToList();
        var assignment = new Dictionary<string, string>();
        var routes = new Dictionary<string, List<Native.DisplayPath>>();
        foreach (var d in enabled)
            routes[d.Id] = all.Paths.Where(p => p.Target.Available && p.Target.Id == d.Path.Target.Id && p.Target.Adapter.Key == d.Path.Target.Adapter.Key).OrderByDescending(p => (p.Flags & 1) != 0).ToList();
        string SourceKey(Native.DisplayPath p) => $"{p.Source.Adapter.Key}:{p.Source.Id}";
        bool Assign(int i, HashSet<string> used)
        {
            if (i == groups.Count) return true;
            var group = groups[i];
            foreach (string key in routes[group.First().Id].Select(SourceKey).Distinct())
            {
                if (used.Contains(key) || group.Any(d => !routes[d.Id].Any(p => SourceKey(p) == key))) continue;
                used.Add(key); assignment[group.Key] = key;
                if (Assign(i + 1, used)) return true;
                used.Remove(key); assignment.Remove(group.Key);
            }
            return false;
        }
        if (!Assign(0, new HashSet<string>())) throw new InvalidOperationException("Windows has no compatible display routes for this selection. Mirrored screens must share an adapter.");
        foreach (var group in groups)
        {
            uint sourceIndex = (uint)modes.Count; var master = group.First(d => d.MirrorOf == "");
            foreach (var d in group)
            {
                var path = routes[d.Id].First(p => SourceKey(p) == assignment[group.Key]);
                path.Flags = 1; path.Source.ModeIndex = sourceIndex;
                // Preserve exact timings on unchanged active routes, so a topology
                // switch does not silently reset HDR-capable/high-refresh targets.
                var originalPath = routes[d.Id].First(p => SourceKey(p) == assignment[group.Key]);
                bool keepTiming = (originalPath.Flags & 1) != 0 && originalPath.Target.ModeIndex < all.Modes.Length && originalPath.Source.ModeIndex < all.Modes.Length &&
                    all.Modes[originalPath.Source.ModeIndex].Source.Width == d.Width && all.Modes[originalPath.Source.ModeIndex].Source.Height == d.Height;
                if (modes.Count == sourceIndex) modes.Add(new Native.Mode { Type = 1, Id = path.Source.Id, Adapter = path.Source.Adapter,
                    Source = new Native.SourceMode { Width = (uint)master.Width, Height = (uint)master.Height, PixelFormat = 4, Position = new Native.Point { X = master.X, Y = master.Y } } });
                if (keepTiming) { path.Target.ModeIndex = (uint)modes.Count; modes.Add(all.Modes[originalPath.Target.ModeIndex]); }
                else path.Target.ModeIndex = Native.InvalidMode;
                paths.Add(path);
            }
        }
        return new(paths.ToArray(), modes.ToArray());
    }
    public static void Validate(Snapshot s) => Native.Check(Native.SetDisplayConfig((uint)s.Paths.Length, s.Paths, (uint)s.Modes.Length, s.Modes, 0x20 | 0x40 | 0x400), "Validate layout");
    public static void Apply(Snapshot s) => Native.Check(Native.SetDisplayConfig((uint)s.Paths.Length, s.Paths, (uint)s.Modes.Length, s.Modes, 0x20 | 0x80 | 0x200 | 0x400), "Apply layout");
    public static void Verify(List<Display> desired)
    {
        var actual = Discover(new Preferences()); var expected = Layout.Normalize(desired);
        var enabled = actual.Where(d => d.Enabled).ToList();
        if (enabled.Count != expected.Count || expected.Any(e => !enabled.Any(a => a.Id == e.Id && a.Width == e.Width && a.Height == e.Height && a.X == e.X && a.Y == e.Y)) ||
            expected.Any(e => expected.Any(f => e.Id != f.Id && ((e.MirrorOf == "" ? e.Id : e.MirrorOf) == (f.MirrorOf == "" ? f.Id : f.MirrorOf)) !=
                (enabled.First(a => a.Id == e.Id).Path.Source.Adapter.Key == enabled.First(a => a.Id == f.Id).Path.Source.Adapter.Key && enabled.First(a => a.Id == e.Id).Path.Source.Id == enabled.First(a => a.Id == f.Id).Path.Source.Id))))
            throw new InvalidOperationException("The graphics driver adjusted the requested layout. The previous layout will be restored.");
    }
    public static void SaveSnapshot(string path, Snapshot s)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x52454754); writer.Write(s.Paths.Length);
        foreach (var p in s.Paths) writer.Write(Bytes(p));
        writer.Write(s.Modes.Length); foreach (var m in s.Modes) writer.Write(Bytes(m));
    }
    public static Snapshot LoadSnapshot(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadInt32() != 0x52454754) throw new IOException("Invalid recovery snapshot.");
        int n = r.ReadInt32(); if (n < 1 || n > 128) throw new IOException("Invalid recovery path count.");
        var p = new Native.DisplayPath[n]; for (int i = 0; i < n; i++) p[i] = FromBytes<Native.DisplayPath>(r.ReadBytes(Marshal.SizeOf<Native.DisplayPath>()));
        n = r.ReadInt32(); if (n < 0 || n > 384) throw new IOException("Invalid recovery mode count.");
        var m = new Native.Mode[n]; for (int i = 0; i < n; i++) m[i] = FromBytes<Native.Mode>(r.ReadBytes(Marshal.SizeOf<Native.Mode>()));
        return new(p, m);
    }
    private static byte[] Bytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>(); var pointer = Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(value, pointer, false); var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, size); return bytes; }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    private static T FromBytes<T>(byte[] bytes) where T : struct
    {
        if (bytes.Length != Marshal.SizeOf<T>()) throw new IOException("Truncated recovery snapshot.");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); return Marshal.PtrToStructure<T>(pointer); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}

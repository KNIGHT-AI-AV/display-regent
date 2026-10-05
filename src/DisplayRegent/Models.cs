using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DisplayRegent;

internal record Resolution(int Width, int Height)
{
    public override string ToString() => $"{Width} × {Height}";
}
internal sealed class Display
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Display";
    public string Connection { get; set; } = "";
    public string SourceName { get; set; } = "";
    public int Number { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool Enabled { get; set; }
    public bool Primary { get; set; }
    public string MirrorOf { get; set; } = "";
    public double Refresh { get; set; }
    public List<Resolution> Resolutions { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public Native.DisplayPath Path;
    public Display Copy() => (Display)MemberwiseClone();
}
internal sealed class Preset
{
    public string Name { get; set; } = "";
    public int Hotkey { get; set; }
    public List<Display> Displays { get; set; } = new();
}
internal sealed class Preferences
{
    public string Theme { get; set; } = "dark";
    public bool Topmost { get; set; } = true;
    public bool Startup { get; set; }
    public bool Initialized { get; set; }
    public List<Preset> Presets { get; set; } = new();
    public List<Display> Remembered { get; set; } = new();
}
internal static class Store
{
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplayRegent");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "preferences.json");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static void Save(Preferences preferences)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(preferences, Json));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
internal static class Layout
{
    public static List<Display> Normalize(IEnumerable<Display> displays)
    {
        var result = displays.Where(d => d.Enabled).Select(d => d.Copy()).ToList();
        if (result.Count == 0) throw new InvalidOperationException("Keep at least one display on.");
        foreach (var group in result.Where(d => d.MirrorOf != "").GroupBy(d => d.MirrorOf))
        {
            var master = result.FirstOrDefault(d => d.Id == group.Key) ?? group.First();
            master.MirrorOf = "";
            foreach (var clone in group.Where(d => d != master)) clone.MirrorOf = master.Id;
        }
        foreach (var clone in result.Where(d => d.MirrorOf != ""))
        {
            var master = result.First(d => d.Id == clone.MirrorOf);
            if (master.MirrorOf != "") throw new InvalidOperationException("A mirrored display must follow an independent display.");
            clone.X = master.X; clone.Y = master.Y; clone.Width = master.Width; clone.Height = master.Height;
        }
        var primary = result.FirstOrDefault(d => d.Primary) ?? result[0];
        if (primary.MirrorOf != "") primary = result.First(d => d.Id == primary.MirrorOf);
        int x = primary.X, y = primary.Y;
        foreach (var d in result)
        {
            d.Primary = d.Id == primary.Id;
            d.X -= x; d.Y -= y;
            if (d.Width < 320 || d.Height < 200 || d.Width > 16384 || d.Height > 16384) throw new InvalidOperationException("Choose a supported display resolution.");
        }
        for (int a = 0; a < result.Count; a++)
        for (int b = a + 1; b < result.Count; b++)
        {
            var p = result[a]; var q = result[b];
            if ((p.MirrorOf == "" ? p.Id : p.MirrorOf) == (q.MirrorOf == "" ? q.Id : q.MirrorOf)) continue;
            if (p.X < q.X + q.Width && p.X + p.Width > q.X && p.Y < q.Y + q.Height && p.Y + p.Height > q.Y)
                throw new InvalidOperationException("Displays overlap. Drag them apart or use Arrange horizontally / vertically.");
        }
        // Keep the extended desktop connected; disconnected islands trap pointer movement.
        var connected = new HashSet<string> { primary.Id };
        bool progress;
        do
        {
            progress = false;
            foreach (var p in result.Where(d => connected.Contains(d.Id)).ToList())
            foreach (var q in result.Where(d => !connected.Contains(d.Id)))
            {
                bool touchX = (p.X + p.Width == q.X || q.X + q.Width == p.X) && p.Y < q.Y + q.Height && q.Y < p.Y + p.Height;
                bool touchY = (p.Y + p.Height == q.Y || q.Y + q.Height == p.Y) && p.X < q.X + q.Width && q.X < p.X + p.Width;
                bool clone = (p.MirrorOf == "" ? p.Id : p.MirrorOf) == (q.MirrorOf == "" ? q.Id : q.MirrorOf);
                if (touchX || touchY || clone) { connected.Add(q.Id); progress = true; }
            }
        } while (progress);
        if (connected.Count != result.Count) throw new InvalidOperationException("Make active display edges touch. Use Arrange to reconnect the layout.");
        return result;
    }
    public static void Arrange(List<Display> displays, bool vertical)
    {
        int position = 0;
        foreach (var d in displays.Where(d => d.MirrorOf == "").OrderBy(d => vertical ? d.Y : d.X).ThenBy(d => d.Number))
        { d.X = vertical ? 0 : position; d.Y = vertical ? position : 0; position += vertical ? d.Height : d.Width; }
        foreach (var d in displays.Where(d => d.MirrorOf != "")) { var master = displays.FirstOrDefault(m => m.Id == d.MirrorOf); if (master != null) { d.X = master.X; d.Y = master.Y; d.Width = master.Width; d.Height = master.Height; } }
    }
    public static void MatchPreset(List<Display> current, Preset preset)
    {
        if (preset.Displays.Any(d => d.Enabled && !current.Any(c => c.Id == d.Id)))
            throw new InvalidOperationException("A display in this preset is disconnected. Reconnect it before applying this preset.");
        foreach (var d in current)
        {
            var saved = preset.Displays.FirstOrDefault(p => p.Id == d.Id);
            d.Enabled = saved?.Enabled ?? false; d.Primary = saved?.Primary ?? false;
            if (saved != null) { d.X = saved.X; d.Y = saved.Y; d.Width = saved.Width; d.Height = saved.Height; d.MirrorOf = saved.MirrorOf; }
        }
        Normalize(current);
    }
}

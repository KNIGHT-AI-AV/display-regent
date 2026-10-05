using DisplayRegent;
using System.Runtime.InteropServices;

int passed = 0;
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
Display D(string id, int x = 0, int y = 0, bool on = true, bool primary = false) => new() { Id = id, X = x, Y = y, Width = 1920, Height = 1080, Enabled = on, Primary = primary };

Test("Native CCD ABI sizes match Win32 x64", () => { Assert(Marshal.SizeOf<Native.DisplayPath>() == 72); Assert(Marshal.SizeOf<Native.Mode>() == 64); Assert(Marshal.SizeOf<Native.DevMode>() == 220); Assert(Marshal.SizeOf<Native.PreferredMode>() == 80); Assert(Marshal.SizeOf<Native.TargetName>() == 420); Assert(Marshal.SizeOf<Native.SourceName>() == 84); });
Test("All-off is rejected", () => Reject(() => Layout.Normalize(new[] { D("a", on: false) })));
Test("Primary is translated to desktop origin without mutating draft", () => { var a = D("a", 1920, primary: true); var b = D("b", 0); var result = Layout.Normalize(new[] { a,b }); Assert(result[0].X == 0 && result[1].X == -1920 && a.X == 1920); });
Test("Disabled targets are omitted", () => Assert(Layout.Normalize(new[] { D("a", primary:true), D("b",on:false) }).Count == 1));
Test("Overlapping extended displays are rejected", () => Reject(() => Layout.Normalize(new[] { D("a"), D("b",100) })));
Test("Disconnected desktop islands are rejected", () => Reject(() => Layout.Normalize(new[] { D("a"), D("b",4000) })));
Test("Stacked monitors with partial edge contact are accepted", () => Assert(Layout.Normalize(new[] { D("a",primary:true), D("b",400,-1080) }).Count == 2));
Test("Corner-only adjacency is rejected", () => Reject(() => Layout.Normalize(new[] { D("a"), D("b",1920,1080) })));
Test("Mirror groups share coordinates and resolution", () => { var a = D("a",primary:true); var b = D("b",700); b.MirrorOf = "a"; b.Width = 1280; var result = Layout.Normalize(new[] { a,b }); Assert(result[1].X == 0 && result[1].Width == 1920); });
Test("Disabling mirror master promotes surviving target", () => { var a = D("a",on:false); var b = D("b"); b.MirrorOf = "a"; Assert(Layout.Normalize(new[] { a,b })[0].MirrorOf == ""); });
Test("Arrange respects mirror groups", () => { var a = D("a"); var b = D("b"); b.MirrorOf="a"; var c=D("c"); var list=new List<Display>{a,b,c}; Layout.Arrange(list,true); Assert(a.Y==b.Y && c.Y==1080); });
Test("Preset fails when an enabled physical monitor is missing", () => Reject(() => Layout.MatchPreset(new(){D("a")},new(){Displays=new(){D("b")}})));
Test("Preset does not activate an unrecorded connected monitor", () => { var list = new List<Display>{D("a"),D("b",1920)}; Layout.MatchPreset(list,new(){Displays=new(){D("a",primary:true)}}); Assert(!list[1].Enabled); });
Test("Preset restores the mirror relation", () => { var list=new List<Display>{D("a"),D("b",1920)}; var clone=D("b"); clone.MirrorOf="a"; Layout.MatchPreset(list,new(){Displays=new(){D("a",primary:true),clone}}); Assert(list[1].MirrorOf=="a"); });
Test("Corrupt recovery files are rejected", () => { string f=Path.GetTempFileName(); try { File.WriteAllBytes(f,new byte[16]); try {DisplayService.LoadSnapshot(f);}catch(IOException){return;} throw new Exception("Expected invalid snapshot"); } finally {File.Delete(f);} });
Test("Recovery snapshots preserve complete native union bytes", () => { var m=new Native.Mode{Type=1,Id=7,Source=new(){Width=3840,Height=2160,PixelFormat=4,Position=new(){X=-1920,Y=40}}}; var p=new Native.DisplayPath{Flags=1,Target=new(){Available=true,Id=13},Source=new(){Id=7}}; string f=Path.GetTempFileName(); try{DisplayService.SaveSnapshot(f,new(new[]{p},new[]{m}));var s=DisplayService.LoadSnapshot(f);Assert(s.Paths[0].Target.Id==13 && s.Paths[0].Target.Available && s.Modes[0].Source.Position.X==-1920 && s.Modes[0].Source.Width==3840);}finally{File.Delete(f);} });
Console.WriteLine($"{passed} checks passed.");

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RegentSetup;
internal static class Program
{
    internal static readonly string Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DisplayRegent");
    internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DisplayRegent";
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--remove-after")
            {
                try { Process.GetProcessById(int.Parse(args[1])).WaitForExit(15000); } catch (ArgumentException) { }
                VerifyTarget(); if (Directory.Exists(Target)) Directory.Delete(Target, true);
                Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue("DisplayRegent", false);
                string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Display Regent.lnk"); if (File.Exists(shortcut)) File.Delete(shortcut);
                return 0;
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--uninstall"))
            {
                if (MessageBox.Show("Remove Display Regent? Your saved scenes and settings will stay in your local app data.", "Display Regent", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                EnsureClosed();
                string temporary = Path.Combine(Path.GetTempPath(), "DisplayRegent-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Environment.ProcessPath!, temporary);
                Process.Start(new ProcessStartInfo(temporary) { UseShellExecute=false, CreateNoWindow=true, ArgumentList={"--remove-after", Environment.ProcessId.ToString()} }); return 0;
            }
            if (args.Contains("--install-silent")) { Install(); return 0; }
            Application.Run(new InstallerWindow()); return 0;
        }
        catch (Exception e) { if (args.Contains("--install-silent")) Console.Error.WriteLine(e.Message); else MessageBox.Show(e.Message,"Display Regent",MessageBoxButtons.OK,MessageBoxIcon.Warning); return 1; }
    }
    private static void VerifyTarget()
    {
        string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Target).StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(Target) != "DisplayRegent") throw new IOException("Invalid installation directory.");
        if (Directory.Exists(Target) && (File.GetAttributes(Target) & FileAttributes.ReparsePoint) != 0) throw new IOException("The installation directory is a link. Choose a normal directory before installing.");
    }
    private static void EnsureClosed()
    {
        foreach (var process in Process.GetProcessesByName("DisplayRegent"))
        {
            using (process) try { if (string.Equals(process.MainModule?.FileName, Path.Combine(Target,"DisplayRegent.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException("Quit Display Regent from its tray menu, then try again."); } catch (System.ComponentModel.Win32Exception) { throw new IOException("Close Display Regent before installing or uninstalling."); }
        }
    }
    internal static void Install()
    {
        VerifyTarget(); EnsureClosed();
        string staging = Target + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            File.Copy(Environment.ProcessPath!,Path.Combine(staging,"DisplayRegent.exe"));
            using (var license=Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE")!)
            using (var output=File.Create(Path.Combine(staging,"LICENSE.txt"))) license.CopyTo(output);
            if (!File.Exists(Path.Combine(staging,"DisplayRegent.exe"))) throw new IOException("The app is missing from the installer.");
            Directory.CreateDirectory(Target);
            foreach (string file in Directory.GetFiles(staging,"*",SearchOption.AllDirectories))
            {
                string destination=Path.Combine(Target,Path.GetRelativePath(staging,file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file,destination,true);
            }
            File.Copy(Environment.ProcessPath!,Path.Combine(Target,"Uninstall.exe"),true);
            using var key=Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("DisplayName","Display Regent"); key.SetValue("DisplayVersion","0.1.1 preview"); key.SetValue("Publisher","Knight AI+AV contributors");
            key.SetValue("InstallLocation",Target); key.SetValue("DisplayIcon",Path.Combine(Target,"DisplayRegent.exe"));
            key.SetValue("UninstallString",$"\"{Path.Combine(Target,"Uninstall.exe")}\" --uninstall"); key.SetValue("NoModify",1,RegistryValueKind.DWord); key.SetValue("NoRepair",1,RegistryValueKind.DWord);
            key.SetValue("URLInfoAbout","https://www.knightaiav.com/display-regent/");
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut=shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Display Regent.lnk"));
            shortcut.TargetPath=Path.Combine(Target,"DisplayRegent.exe"); shortcut.WorkingDirectory=Target; shortcut.Description="Arrange and switch Windows displays"; shortcut.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        finally { if (Directory.Exists(staging) && Path.GetFullPath(staging).StartsWith(Target + ".staging-",StringComparison.OrdinalIgnoreCase)) Directory.Delete(staging,true); }
    }
}
internal sealed class InstallerWindow : Form
{
    private readonly Button install = new() { Text="Install Display Regent", Height=46, Dock=DockStyle.Bottom, FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(48,72,105), ForeColor=Color.White };
    private readonly Label progress = new() { AutoSize=false, Height=42, Dock=DockStyle.Bottom, ForeColor=Color.FromArgb(164,175,196) };
    public InstallerWindow()
    {
        Text="Display Regent"; Size=new Size(530,465); FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(20,28,43); ForeColor=Color.FromArgb(235,238,247); Font=new Font("Segoe UI",10); Padding=new Padding(28);
        Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Controls.Add(install); Controls.Add(progress);
        var title=new Label { Text="Display Regent", Font=new Font("Georgia",28), Dock=DockStyle.Top, Height=70 };
        var text=new Label { Text="A little reign over every screen, by Knight AI+AV.\r\n\r\nA minimalist glass widget for your Windows displays. Save scenes, switch screens and arrange your desktop.\r\n\r\nFree and open source under the MIT license.\r\nNo account. No runtime download. No subscription.\r\n\r\nVersion 0.1.1 preview · Signing pending\r\nInstalls for your Windows user without administrator access.", Dock=DockStyle.Top, Height=210 };
        var license=new LinkLabel { Text="Read the MIT license", Dock=DockStyle.Top, Height=30, LinkColor=Color.FromArgb(155,190,237), ActiveLinkColor=Color.White };
        license.LinkClicked+=(_,_) => { using var reader=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE")!);MessageBox.Show(reader.ReadToEnd(),"MIT license"); };
        Controls.Add(license);Controls.Add(text);Controls.Add(title);
        install.Click+=async(_,_) =>
        {
            if (install.Text=="Open Display Regent") { Process.Start(new ProcessStartInfo(Path.Combine(Program.Target,"DisplayRegent.exe")){UseShellExecute=true});Close();return; }
            install.Enabled=false;progress.Text="Installing…";
            try { Program.Install(); await Task.Yield(); progress.Text="Installed. Find Display Regent in Start or your system tray.";install.Text="Open Display Regent"; }
            catch(Exception e){progress.Text=e.Message;}
            finally{install.Enabled=true;}
        };
    }
}

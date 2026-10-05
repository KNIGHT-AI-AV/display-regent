# Architecture

The app is C# WPF on .NET 8. WinForms is used only for the Windows notification
icon and work-area lookup. Both app and installer ship their Windows runtime.
There is no WebView, server, browser engine or NuGet library dependency.

`QueryDisplayConfig(QDC_ALL_PATHS)` discovers available active and inactive
targets, then groups duplicates by adapter and target. Friendly names and
preferred resolution use `DisplayConfigGetDeviceInfo`. Enabled source modes
supply desktop coordinates and dimensions. Shared source routes identify mirrors.

The layout normalizer refuses all-off, overlapping extended desktops, isolated
desktop islands, and missing required preset monitors. It translates the primary
source to (0,0), synchronizes mirror groups and promotes surviving mirror targets.
A backtracking assignment gives each independent desktop group one compatible
source route. Mirror members must share a route source.

`SetDisplayConfig` validates supplied source modes and compatible target timing.
Unchanged active target timing is retained. Windows selects timing for new routes
or changed resolution. The app applies and saves the topology, then queries again
to verify enabled targets, dimensions, positions and mirror relations.

A pre-change native snapshot is stored in a local binary recovery file. The app
starts a second process, checks its ready signal, then applies the new topology.
The main app asks for confirmation for 20 seconds. The second process waits 25
seconds after arming and restores the snapshot unless confirmation exists. It
also attempts restoration if the main app exits while applying. Physical removal
or graphics-driver failure can prevent recovery; errors are kept locally.

Presets store stable monitor device identities, not just Windows display numbers.
The numbers are stable within the current connected target set and can change
when that set changes. Hotkeys report collisions and use MOD_NOREPEAT. No timer
polls the desktop while idle; WM_DISPLAYCHANGE / WM_DEVICECHANGE trigger a short
debounce. The tray app continues while its panel is hidden.

The app executable also serves as its installer and uses no downloaded components.
The setup filename opens the installer; installation copies the complete executable.
It rejects a reparse-point install directory. It checks
for a running installed app rather than killing it. The per-user uninstaller
preserves preferences and removes only the known installation directory.

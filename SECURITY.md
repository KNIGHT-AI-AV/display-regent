# Security policy

Do not post private monitor identifiers, local preferences, recovery snapshots,
credentials or private paths in public issues. Report sensitive bugs through
GitHub private vulnerability reporting when available on this repository.

The desktop app does not make network calls. It uses user32 CCD APIs to read and
set Windows display configuration, stores user preferences in local app data,
and optionally registers a per-user sign-in startup entry. No administrator
rights are requested. The installer writes only the current user's application
folder, Start menu shortcut and uninstall registry entry.

The initial release is an unsigned preview. Trusted signing is pending. The
maintainer controls releases and signing; third-party PRs do not publish builds.

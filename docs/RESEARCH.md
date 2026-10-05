# Implementation research

Reviewed October 4, 2026. Only original application code and original visual
assets are included. No binaries or code from the compared utilities are bundled.

- [Microsoft QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig): active/all-path enumeration, mode indexes and topology-change retry behavior.
- [Microsoft SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig): validation, application, persistence, omitted timings, driver adjustments and WDDM limits.
- [Microsoft DisplayConfigGetDeviceInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-displayconfiggetdeviceinfo): friendly names, source names and preferred resolution.
- [MultiMonitorTool](https://www.nirsoft.net/utils/multi_monitor_tool.html): target identity persistence and real driver/version limitations reinforced the need for post-apply verification and rollback.
- [DisplayFusion monitor configuration](https://www.displayfusion.com/Features/MonitorConfig/): profile and hotkey workflows informed saved scenes and click-to-apply controls.
- [MIT license at OSI](https://opensource.org/license/mit): selected for permissive use, modification and redistribution with attribution and warranty terms.
- [Microsoft Windows signing options](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options): Store MSIX signing is free after certification; direct distribution requires another trusted signing path. Self-signing is development-only and is not represented as public trust.
- [SignPath Foundation terms](https://signpath.org/terms): a potential free signing route for qualifying open-source projects, subject to application, acceptance, maintainer identity and build-provenance requirements. It is not an automatically available certificate.

No new hosting, DNS, Railway service or paid resource is required for this app.
The existing KnightAIAV.com website serves the static page; Knight AI+AV GitHub
Releases serves downloads. No GitHub Pages site is used. This avoids tying
the app's ability to switch monitors to any online service.

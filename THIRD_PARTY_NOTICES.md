# Third-party notices

Windows Ultimate's own code is covered by [LICENSE.md](LICENSE.md). It is built on the components below, which keep their own licences. The full licence texts are in the [`licenses/`](licenses/) folder.

*Composants tiers : Windows Ultimate repose sur les composants ci-dessous, qui gardent leur propre licence. Les textes complets sont dans le dossier [`licenses/`](licenses/).*

## In the Windows application and its installer

| Component | Version | Licence | Copyright | Full text |
|---|---|---|---|---|
| .NET runtime (`Microsoft.NETCore.App`), shipped with the app (self-contained build) | 10.0 | MIT | © .NET Foundation and Contributors | [dotnet-runtime-LICENSE.txt](licenses/dotnet-runtime-LICENSE.txt) |
| Components inside the .NET runtime (zlib, Unicode data, and others listed by Microsoft) | – | various (MIT, BSD, zlib, Unicode…) | their respective authors | [dotnet-runtime-THIRD-PARTY-NOTICES.txt](licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt) |
| Windows Desktop runtime / WPF (`Microsoft.WindowsDesktop.App`) | 10.0 | MIT | © .NET Foundation and Contributors | [dotnet-wpf-LICENSE.txt](licenses/dotnet-wpf-LICENSE.txt) |
| `System.Management`, `System.Diagnostics.EventLog`, `System.ServiceProcess.ServiceController` (NuGet) | 10.0.14 | MIT | © .NET Foundation and Contributors | [dotnet-runtime-LICENSE.txt](licenses/dotnet-runtime-LICENSE.txt), same third-party notices as the runtime |
| Inno Setup (the installer and uninstaller are built with it) | 6 | Inno Setup License | © Jordan Russell, © Martijn Laan | [inno-setup-LICENSE.txt](licenses/inno-setup-LICENSE.txt) |

The app also calls components that are part of Windows itself (WMI, PowerShell, `powercfg`, `netsh`, `ipconfig`, DISM, SFC, chkdsk) and, on NVIDIA systems, the `nvml.dll` library installed by the NVIDIA driver. None of them is included in Windows Ultimate.

## On the website

| Component | Licence | Copyright | Full text |
|---|---|---|---|
| Inter (font, via Fontsource) | SIL Open Font License 1.1 | © The Inter Project Authors | [inter-OFL.txt](licenses/inter-OFL.txt) |
| JetBrains Mono (font, via Fontsource) | SIL Open Font License 1.1 | © The JetBrains Mono Project Authors | [jetbrains-mono-OFL.txt](licenses/jetbrains-mono-OFL.txt) |
| Lenis (smooth scrolling) | MIT | © darkroom.engineering | [lenis-LICENSE.txt](licenses/lenis-LICENSE.txt) |
| Astro (site generator; a small part runs in the page) | MIT | © Fred K. Schott and contributors | [astro-LICENSE.txt](licenses/astro-LICENSE.txt) |

## Used only to build or test (not distributed)

xUnit and the .NET test SDK (tests, Apache 2.0 / MIT), sharp (image generation for the website, Apache 2.0), GitHub Actions (build and release).

## Trademarks

Windows, Microsoft Defender, Microsoft Store and .NET are trademarks of Microsoft Corporation. NVIDIA, AMD and Intel are trademarks of their respective owners. Windows Ultimate is an independent project, not affiliated with or endorsed by any of them.

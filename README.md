# HardeningKitty Panski UI

A Windows GUI for [HardeningKitty](https://github.com/scipag/HardeningKitty) by scipag.
It checks Windows hardening, classifies open findings, fixes them selectively, and can roll the changes back.

[![Latest release](https://img.shields.io/github/v/release/drw1337/HardeningKitty-Panski-UI)](../../releases/latest)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)
![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> Dedicated to Panski † – *Gewidmet Panski †*

![Audit tab](docs/screenshots/1-audit.png)

## Features

- **Audit:** HardeningKitty audit with the matching finding list for your OS, score, history and CSV export.
  On request, HardeningKitty is downloaded from GitHub and its Authenticode signature (scip AG) is verified.
- **Fix:** Apply findings by classification (safe, needs review, manual). A restore point is created before
  any change, and a backup can be restored.
- **Privacy:** 18 groups covering registry policies, services and scheduled tasks, with backup, check and restore.
- **Automation:** a scheduled task running as SYSTEM (by default on the 3rd Wednesday at 12:00), also runnable via `/auto`.
- German and English UI, switchable at runtime.

## Screenshots

### Audit

Pick a finding list (the one matching your Windows version is preselected), run the audit and see the score.
Click an item to see all details on the right (see the screenshot at the top).

### Fix

Open items are grouped into *safe*, *needs review* and *manual*. Safe items are preselected.
A backup and (optionally) a Windows restore point are created before anything is changed.
Items you deliberately do not want hardened can be marked as an exception.

![Fix tab](docs/screenshots/2-fix.png)

### Privacy

Telemetry, ads and privacy settings that go beyond HardeningKitty, applied as policies.
Each group shows its individual settings and their current status.

![Privacy tab](docs/screenshots/3-privacy.png)

### Automation

Windows updates reset some settings. A scheduled task re-applies the selected privacy settings,
re-checks the fixed HardeningKitty items and can create a full audit report once a month.

![Automation tab](docs/screenshots/4-automation.png)

## Download

The current executable is available under **[Releases](../../releases/latest)**.
Download `HardeningKittyUI.exe`, put it in a folder of its own and start it. It asks for administrator rights.
The program keeps its settings, reports, logs and backups in subfolders next to the EXE.

The SHA-256 checksum of each release is listed in the release notes. To verify it:

```bat
certutil -hashfile HardeningKittyUI.exe SHA256
```

## Important notes

- The application needs **administrator rights** and **changes system settings**. Fixing, privacy changes and automation
  are meant to be used only with the restore point and backup in place. Try them in a VM or test environment first.
- The EXE is **not signed**. Windows SmartScreen may show "Windows protected your PC" – click *More info* → *Run anyway*.
  Antivirus software may also warn about it.
- The UI was built for personal use. You are free to fork and adapt it.

## Build it yourself

Requirements: Windows with the .NET Framework 4.x that ships with it (no Visual Studio needed).

```bat
cd Quellcode
build.cmd
```

The EXE is written to `Programm\HardeningKittyUI.exe`. `build.cmd` fails if the EXE is currently running.

Icons and images are already in the repo (`app.ico`, `Quellcode\Bilder\*.png`). `Bilder\erzeugen.cmd` regenerates them,
but it needs the master image (not included in the repo) and the IconSet tool. You only need it if you change the icon.

## Contributing

This is a personal project. Pull requests are not accepted. Issues and discussions are open for bug reports and questions.
Forking is explicitly welcome, under the terms of the license.

## License

MIT License, see [LICENSE](LICENSE).

## Credits

- Checks and finding lists: [HardeningKitty](https://github.com/scipag/HardeningKitty) by Michael Schneider and scipag (MIT license).
- Development: Maximilian Meyn

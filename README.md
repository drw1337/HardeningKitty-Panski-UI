# HardeningKitty Panski UI

A Windows GUI (WinForms, .NET Framework 4.8) for [HardeningKitty](https://github.com/scipag/HardeningKitty) by scip AG.
It checks Windows hardening, classifies open findings, fixes them selectively, and can roll the changes back.

> Dedicated to Panski † – *Gewidmet Panski †*

## Features

- **Check:** HardeningKitty audit with the matching finding list for your OS, score, history and CSV export.
  On request, HardeningKitty is downloaded from GitHub and its Authenticode signature ("scip AG") is verified.
- **Fix:** Apply findings by classification (uncritical, needs confirmation, manual). A restore point is created before
  any change, and a backup can be restored.
- **Privacy:** 18 groups covering registry policies, services and scheduled tasks, with backup, check and restore.
- **Automation:** a scheduled task running as SYSTEM (by default on the 3rd Wednesday at 12:00), also runnable via `/auto`.
- German and English UI, switchable at runtime.

## Download

The current executable is available under **[Releases](../../releases/latest)**.

## Important notes

- The application needs **administrator rights** and **changes system settings**. Fixing, privacy changes and automation
  are meant to be used only with the restore point and backup in place. Try them in a VM or test environment first.
- The EXE is **not signed**. Windows SmartScreen or antivirus software may warn about it.
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

- Checks and finding lists: [HardeningKitty](https://github.com/scipag/HardeningKitty) by Michael Schneider and scip AG (MIT license).
- Development: Maximilian Meyn

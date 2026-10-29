@echo off
rem Erzeugt app.ico und die Bilder fuer Kopfzeile, Info-Dialog und Widmung aus dem Hauptbild (Wappen).
rem Das Hauptbild liegt NICHT im Repo, sondern im Nachbarordner ..\..\..\HardeningKitty-UI-Quellbilder\
rem Werkzeug: ..\..\..\_Vorlage-Windows-Tool\Werkzeuge\IconSet.exe   Farben: Kachel #262626, Unterlage #FAF7F2
setlocal
set T="%~dp0..\..\..\_Vorlage-Windows-Tool\Werkzeuge\IconSet.exe"
set W="%~dp0..\..\..\HardeningKitty-UI-Quellbilder\Monochromes- Tuxedo-Katzenwappen-2.png"
set U=--unterlage #FAF7F2:2.5
set B=%~dp0

%T% ico %W% "%B%..\app.ico" --kachel #262626 --kachelrand #3A3A3A --kachel-ab 32 %U% --rand 8 --rand-klein 0 || exit /b 1
for %%S in (44 66 88) do %T% png %W% "%B%kopf-%%S.png" %%S %U% --rand 2 || exit /b 1
for %%S in (48 72 96) do %T% png %W% "%B%info-%%S.png" %%S %U% --rand 2 || exit /b 1
for %%S in (18 28 36 56) do %T% png %W% "%B%widmung-%%S.png" %%S %U% --rand 0 || exit /b 1
%T% vorschau "%TEMP%\HardeningKittyUI-Symbolset.png" --titel "HardeningKitty Panski UI" "%B%..\app.ico" "%B%kopf-88.png" "%B%info-96.png" "%B%widmung-18.png" "%B%widmung-36.png"
echo Fertig. Danach build.cmd ausfuehren.

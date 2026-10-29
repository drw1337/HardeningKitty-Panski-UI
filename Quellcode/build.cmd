@echo off
rem Baut das Tool als ..\Programm\%EXE%.exe aus allen .cs-Dateien in diesem Ordner.
rem Braucht nur das in Windows enthaltene .NET Framework 4.x (kein Visual Studio).
rem Optional: app.ico in diesem Ordner wird als Programmsymbol eingebunden (Datei, Fenster, Kopfzeile).
rem           Erzeugen mit ..\..\_Vorlage-Windows-Tool\Werkzeuge\IconMaker.exe app.ico #RRGGBB <Glyphe>
rem           oder aus einem Bild mit ..\..\_Vorlage-Windows-Tool\Werkzeuge\IconSet.exe.
rem Optional: jede PNG in Bilder\ wird unter ihrem Dateinamen eingebettet (AppHost.LoadImage("name.png")).
setlocal

rem ---- Pro Projekt anpassen ---------------------------------------------------------------
set EXE=HardeningKittyUI
set REFS=/reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.ServiceProcess.dll /reference:Microsoft.CSharp.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll
rem -------------------------------------------------------------------------------------------

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set OUT=%~dp0..\Programm
if not exist "%OUT%" mkdir "%OUT%"
set ICON=
if exist "%~dp0app.ico" set ICON=/win32icon:"%~dp0app.ico" /resource:"%~dp0app.ico",app.ico
set RES=
for %%F in ("%~dp0Bilder\*.png") do call set RES=%%RES%% /resource:"%%~fF",%%~nxF

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /win32manifest:"%~dp0app.manifest" %ICON% %RES% %REFS% ^
  /out:"%OUT%\%EXE%.exe" "%~dp0*.cs"

if errorlevel 1 (echo. & echo BUILD FEHLGESCHLAGEN & exit /b 1)
echo Fertig: %OUT%\%EXE%.exe

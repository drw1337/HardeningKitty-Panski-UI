// Kitty.cs – Anbindung an HardeningKitty (https://github.com/scipag/HardeningKitty)
//
// HardeningKitty ist ein PowerShell-Modul und wird bewusst unverändert genutzt: Prüflogik und die
// signierten Prüflisten kommen direkt vom Hersteller und lassen sich so jederzeit aktualisieren.
// Es gibt keinen direkten .NET-Weg dafür, deshalb wird powershell.exe als externer Prozess gestartet
// (ohne Fenster, Ausgabe umgeleitet, alles im Log).
//
// Enthält: Pfade/Einstellungen, CSV-Lesen/-Schreiben, Prüflisten-Katalog, Systemerkennung,
// Modul finden/herunterladen/prüfen, Aufrufe (Audit, Config-Backup, HailMary), Berichte, Einstufung,
// Ausnahmen und das Härtungsprofil für die Automatik.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MeynTools;

namespace HardeningKittyUI
{
    static class Paths
    {
        public static string Sub(string relative)
        {
            string p = Path.Combine(AppHost.ProgramFolder, relative);
            Directory.CreateDirectory(p);
            return p;
        }

        public static string Kitty { get { return Path.Combine(AppHost.ProgramFolder, "HardeningKitty"); } }
        public static string Reports { get { return Sub("Berichte"); } }
        public static string Backups { get { return Sub("Sicherungen"); } }
        public static string Settings { get { return Sub("Einstellungen"); } }
        public static string Stamp() { return DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"); }
    }

    // Einfache Einstellungsdatei Einstellungen\Einstellungen.ini (Schlüssel=Wert)
    static class Settings
    {
        static string FilePath { get { return Path.Combine(Paths.Settings, "Einstellungen.ini"); } }

        static Dictionary<string, string> Load()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0 && !line.StartsWith("#")) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch (Exception ex) { Log.Write("Einstellungen lesen: " + ex.Message); }
            return d;
        }

        public static string Get(string key, string def)
        {
            string v;
            return Load().TryGetValue(key, out v) ? v : def;
        }

        public static bool GetBool(string key, bool def) { return Get(key, def ? "1" : "0") == "1"; }

        public static void Set(string key, string value)
        {
            var d = Load();
            d[key] = value;
            var sb = new StringBuilder("# Einstellungen von " + AppHost.Name + " – wird vom Programm verwaltet\r\n");
            foreach (var kv in d.OrderBy(k => k.Key)) sb.Append(kv.Key + "=" + kv.Value + "\r\n");
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        public static void SetBool(string key, bool value) { Set(key, value ? "1" : "0"); }
    }

    class CsvRow
    {
        public string Raw;
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string this[string column] { get { string v; return Values.TryGetValue(column, out v) ? v : ""; } }
    }

    // CSV im Format von HardeningKitty: Komma als Trenner, Felder in "…" dürfen Kommas, "" und Zeilenumbrüche enthalten.
    // Raw enthält jede Zeile unverändert, damit Teillisten exakt wie das Original geschrieben werden.
    class CsvTable
    {
        public string HeaderRaw = "";
        public List<string> Columns = new List<string>();
        public readonly List<CsvRow> Rows = new List<CsvRow>();

        public static CsvTable Load(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
            var t = new CsvTable();
            int i = 0;
            bool first = true;
            while (i < text.Length)
            {
                int start = i;
                List<string> fields = ParseRecord(text, ref i);
                string raw = text.Substring(start, i - start).TrimEnd('\r', '\n');
                if (raw.Trim().Length == 0) continue;
                if (first) { t.HeaderRaw = raw; t.Columns = fields; first = false; continue; }
                var row = new CsvRow { Raw = raw };
                for (int c = 0; c < t.Columns.Count && c < fields.Count; c++) row.Values[t.Columns[c]] = fields[c];
                t.Rows.Add(row);
            }
            return t;
        }

        static List<string> ParseRecord(string s, ref int i)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            while (i < s.Length)
            {
                char c = s[i++];
                if (quoted)
                {
                    if (c != '"') sb.Append(c);
                    else if (i < s.Length && s[i] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Length = 0; }
                else if (c == '\n') break;
                else if (c != '\r') sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
        }

        // UTF-8 mit BOM, damit Import-Csv in Windows PowerShell 5.1 Umlaute richtig liest
        public static void WriteRaw(string path, string headerRaw, IEnumerable<string> rawRows)
        {
            var sb = new StringBuilder(headerRaw + "\r\n");
            foreach (string r in rawRows) sb.Append(r + "\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
    }

    // Eine Prüfliste von HardeningKitty; Rechner- und Benutzerteil (…_machine.csv / …_user.csv) werden zusammengefasst
    class FindingList
    {
        public string Key, Display, MachineFile, UserFile;
        public override string ToString() { return Display; }
    }

    static class Catalog
    {
        public static List<FindingList> Load(string listsFolder)
        {
            var map = new Dictionary<string, FindingList>();
            if (!Directory.Exists(listsFolder)) return new List<FindingList>();
            foreach (string file in Directory.GetFiles(listsFolder, "finding_list_*.csv"))
            {
                string n = Path.GetFileNameWithoutExtension(file).Substring("finding_list_".Length);
                bool user = n.EndsWith("_user"), machine = n.EndsWith("_machine");
                string key = user ? n.Substring(0, n.Length - 5) : machine ? n.Substring(0, n.Length - 8) : n;
                FindingList l;
                if (!map.TryGetValue(key, out l)) map[key] = l = new FindingList { Key = key, Display = Pretty(key) };
                if (user) l.UserFile = file; else l.MachineFile = file;
            }
            return map.Values.OrderBy(l => Rank(l.Key)).ThenByDescending(l => l.Key, StringComparer.Ordinal).ToList();
        }

        static int Rank(string key)
        {
            if (key.StartsWith("msft_security_baseline_windows")) return 0;
            if (key.StartsWith("cis_microsoft_windows")) return 1;
            if (key.StartsWith("bsi_")) return 2;
            if (key == "0x6d69636b") return 3;
            if (key.StartsWith("msft_") || key.StartsWith("cis_")) return 4;
            if (key.StartsWith("dod_")) return 5;
            return 6;
        }

        static string Pretty(string key)
        {
            if (key == "0x6d69636b") return Lang.T("HardeningKitty-Standardliste (0x6d69636b)", "HardeningKitty default list (0x6d69636b)");
            string[][] prefixes =
            {
                new[] { "msft_security_baseline_", "Microsoft Security Baseline – " },
                new[] { "cis_microsoft_", "CIS Benchmark – Microsoft " },
                new[] { "bsi_sisyphus_", "BSI SiSyPHuS – " },
                new[] { "dod_microsoft_", "DoD STIG – Microsoft " },
                new[] { "dod_", "DoD STIG – " },
                new[] { "microsoft_", "Microsoft – " }
            };
            string prefix = "", rest = key;
            foreach (string[] p in prefixes)
                if (rest.StartsWith(p[0])) { prefix = p[1]; rest = rest.Substring(p[0].Length); break; }
            return prefix + string.Join(" ", rest.Split('_').Select(Word));
        }

        static string Word(string w)
        {
            if (w.Length == 0) return w;
            if (Regex.IsMatch(w, @"^(\d+h\d|dc|hd|nd|ne|stig|tls|v\d+r\d+|v\d+)$")) return w.ToUpperInvariant();
            return char.ToUpperInvariant(w[0]) + w.Substring(1);
        }
    }

    static class SystemInfo
    {
        public static int Build;
        public static string DisplayVersion = "", Edition = "", ProductType = "WinNT";
        public static bool IsAdmin;

        public static bool IsServer { get { return !ProductType.Equals("WinNT", StringComparison.OrdinalIgnoreCase); } }
        public static bool IsDomainController { get { return ProductType.Equals("LanmanNT", StringComparison.OrdinalIgnoreCase); } }

        public static void Load()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (k != null)
                    {
                        int.TryParse(Convert.ToString(k.GetValue("CurrentBuild")), out Build);
                        DisplayVersion = Convert.ToString(k.GetValue("DisplayVersion") ?? k.GetValue("ReleaseId") ?? "");
                        Edition = Convert.ToString(k.GetValue("EditionID") ?? "");
                    }
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions"))
                    if (k != null) ProductType = Convert.ToString(k.GetValue("ProductType") ?? "WinNT");
            }
            catch (Exception ex) { Log.Write("Systemerkennung: " + ex.Message); }
            IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Log.Write("System: " + Describe() + ", Edition " + Edition + ", Adminrechte: " + (IsAdmin ? "ja" : "nein"));
        }

        static string ServerYear()
        {
            if (Build >= 26100) return "2025";
            if (Build >= 20348) return "2022";
            if (Build >= 17763) return "2019";
            if (Build >= 14393) return "2016";
            return "";
        }

        public static string Describe()
        {
            if (IsServer)
                return "Windows Server " + ServerYear() + " (Build " + Build + (IsDomainController ? Lang.T(", Domänencontroller", ", domain controller") : Lang.T(", Mitgliedsserver", ", member server")) + ")";
            return "Windows " + (Build >= 22000 ? "11" : "10") + " " + Edition + " " + DisplayVersion + " (Build " + Build + ")";
        }

        // Passende Prüfliste: Microsoft Security Baseline für diese Version, sonst die neueste der Produktreihe
        public static FindingList Recommend(List<FindingList> lists)
        {
            string dv = DisplayVersion.ToLowerInvariant();
            var patterns = new List<string[]>();
            if (!IsServer && Build >= 22000)
            {
                patterns.Add(new[] { "msft_security_baseline_windows_11_" + dv, "" });
                patterns.Add(new[] { "msft_security_baseline_windows_11_", "" });
                patterns.Add(new[] { "cis_microsoft_windows_11_", "" });
            }
            else if (!IsServer)
            {
                patterns.Add(new[] { "msft_security_baseline_windows_10_" + dv, "" });
                patterns.Add(new[] { "msft_security_baseline_windows_10_", "" });
                patterns.Add(new[] { "cis_microsoft_windows_10_", "" });
            }
            else
            {
                patterns.Add(new[] { "msft_security_baseline_windows_server_" + ServerYear(), IsDomainController ? "_dc" : "_member" });
                patterns.Add(new[] { "cis_microsoft_windows_server_" + ServerYear(), "" });
            }
            patterns.Add(new[] { "0x6d69636b", "" });
            foreach (string[] p in patterns)
            {
                FindingList hit = lists.Where(l => l.Key.StartsWith(p[0]) && l.Key.Contains(p[1]))
                                       .OrderByDescending(l => l.Key, StringComparer.Ordinal).FirstOrDefault();
                if (hit != null) return hit;
            }
            return lists.FirstOrDefault();
        }
    }

    class KittyModule
    {
        public string Folder, Version;
        public bool IsOwnCopy;
        public string Manifest { get { return Path.Combine(Folder, "HardeningKitty.psd1"); } }
        public string ListsFolder { get { return Path.Combine(Folder, "lists"); } }
        // Ab 0.9.4 verweigert HailMary veränderte Listen (unsere Teilliste) ohne -AllowCustomList
        public bool NeedsAllowCustomList { get { return Kitty.CompareVersions(Version, "0.9.4") >= 0; } }
    }

    static class Kitty
    {
        const string Repo = "scipag/HardeningKitty";

        // Eigene Kopie im Programmordner, sonst ein im System installiertes Modul (z. B. vom alten Skript)
        public static KittyModule Find()
        {
            KittyModule m = Probe(Paths.Kitty, true);
            if (m != null) return m;
            string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WindowsPowerShell\Modules\HardeningKitty");
            m = Probe(sys, false);
            if (m != null) return m;
            if (Directory.Exists(sys))
                foreach (string d in Directory.GetDirectories(sys).OrderByDescending(x => x, StringComparer.Ordinal))
                    if ((m = Probe(d, false)) != null) return m;
            return null;
        }

        static KittyModule Probe(string folder, bool own)
        {
            string psd1 = Path.Combine(folder, "HardeningKitty.psd1");
            if (!File.Exists(psd1) || !File.Exists(Path.Combine(folder, "HardeningKitty.psm1"))) return null;
            Match m = Regex.Match(File.ReadAllText(psd1), @"ModuleVersion\s*=\s*'([^']+)'");
            return new KittyModule { Folder = folder, Version = m.Success ? m.Groups[1].Value : "?", IsOwnCopy = own };
        }

        static string Clean(string v) { return Regex.Replace(v ?? "", @"^[vV]\.?", ""); }

        public static int CompareVersions(string a, string b)
        {
            Version va, vb;
            if (Version.TryParse(Clean(a), out va) && Version.TryParse(Clean(b), out vb)) return va.CompareTo(vb);
            return string.CompareOrdinal(a, b);
        }

        static WebClient Web()
        {
            // Ohne Zielframework-Angabe nutzt .NET sonst nur TLS 1.0 – GitHub verlangt TLS 1.2
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "HardeningKittyUI/" + AppHost.Version;
            if (wc.Proxy != null) wc.Proxy.Credentials = CredentialCache.DefaultCredentials;
            return wc;
        }

        // Neueste Version auf GitHub, z. B. Tag „v.0.9.4“
        public static string LatestTag()
        {
            string json;
            using (WebClient wc = Web()) json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) throw new InvalidOperationException(Lang.T("GitHub hat keine Versionsangabe geliefert.", "GitHub did not return a version."));
            Log.Write("GitHub: neueste HardeningKitty-Version " + m.Groups[1].Value);
            return m.Groups[1].Value;
        }

        public static string TagToVersion(string tag) { return Clean(tag); }

        // Lädt eine Version von GitHub, prüft sie und ersetzt die eigene Kopie im Programmordner.
        // Reihenfolge: erst vollständig neu anlegen und prüfen, dann die alte Kopie beiseitelegen (nicht löschen).
        public static string Install(string tag, Action<string> status)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "HardeningKittyUI_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                string url = "https://github.com/" + Repo + "/archive/refs/tags/" + tag + ".zip";
                string zip = Path.Combine(tmp, "hardeningkitty.zip");
                status(Lang.T("Lade HardeningKitty " + TagToVersion(tag) + " herunter …", "Downloading HardeningKitty " + TagToVersion(tag) + " …"));
                Log.Write("Download: " + url);
                using (WebClient wc = Web()) wc.DownloadFile(url, zip);
                Log.Write("  " + new FileInfo(zip).Length + " Bytes, SHA-256 " + Sha256(zip));

                status(Lang.T("Entpacke und prüfe …", "Extracting and verifying …"));
                string extracted = Path.Combine(tmp, "x");
                ZipFile.ExtractToDirectory(zip, extracted);
                string src = Directory.GetFiles(extracted, "HardeningKitty.psd1", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault();
                if (src == null) throw new InvalidOperationException(Lang.T("Im Download fehlt HardeningKitty.psd1 – die Datei ist unvollständig oder hat ein unerwartetes Format.",
                                                                       "HardeningKitty.psd1 is missing from the download – the file is incomplete or has an unexpected format."));
                string psm1 = Path.Combine(src, "HardeningKitty.psm1");
                string lists = Path.Combine(src, "lists");
                int listCount = Directory.Exists(lists) ? Directory.GetFiles(lists, "*.csv").Length : 0;
                if (!File.Exists(psm1) || listCount == 0) throw new InvalidOperationException(Lang.T("Der Download ist unvollständig: Modul oder Prüflisten fehlen.",
                                                                                            "The download is incomplete: module or finding lists are missing."));
                Log.Write("  " + listCount + " Prüflisten im Download");
                Log.Write("  Signatur HardeningKitty.psm1: " + AuthenticodeCheck.Verify(psm1));

                string staging = Paths.Kitty + ".neu";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                foreach (string f in new[] { "HardeningKitty.psd1", "HardeningKitty.psm1", "LICENSE", "README.md" })
                    if (File.Exists(Path.Combine(src, f))) File.Copy(Path.Combine(src, f), Path.Combine(staging, f));
                CopyFolder(lists, Path.Combine(staging, "lists"));

                string old = null;
                if (Directory.Exists(Paths.Kitty))
                {
                    KittyModule cur = Probe(Paths.Kitty, true);
                    old = Paths.Kitty + "_alt_" + (cur != null ? cur.Version : "unbekannt") + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    Directory.Move(Paths.Kitty, old);
                    Log.Write("  bisherige Kopie beiseitegelegt: " + old);
                }
                try { Directory.Move(staging, Paths.Kitty); }
                catch
                {
                    if (old != null) Directory.Move(old, Paths.Kitty);
                    throw;
                }
                KittyModule installed = Probe(Paths.Kitty, true);
                if (installed == null) throw new InvalidOperationException(Lang.T("HardeningKitty wurde kopiert, ist aber nicht lesbar.", "HardeningKitty was copied but cannot be read."));
                Log.Write("HardeningKitty " + installed.Version + " installiert in " + Paths.Kitty);
                return installed.Version;
            }
            finally
            {
                try { Directory.Delete(tmp, true); }
                catch (Exception ex) { Log.Write("Temporärer Ordner nicht gelöscht: " + ex.Message); }
            }
        }

        // Kurzer Listenname für Ordner- und Dateinamen (.NET 4.8 erlaubt höchstens 260 Zeichen pro Pfad)
        public static string Short(string listName)
        {
            string s = listName.StartsWith("finding_list_") ? listName.Substring(13) : listName;
            return s.Replace("msft_security_baseline_", "msft_").Replace("cis_microsoft_", "cis_").Replace("microsoft_", "ms_").Replace("windows_", "win");
        }

        public static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(from)) CopyFolder(d, Path.Combine(to, Path.GetFileName(d)));
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
        }

        // ---- Aufrufe von Invoke-HardeningKitty ----

        static string Q(string s) { return PowerShell.Quote(s); }

        static string Invoke(KittyModule m, string args)
        {
            return "Import-Module " + Q(m.Manifest) + " -Force -ErrorAction Stop; Invoke-HardeningKitty " + args + " -SkipLanguageWarning";
        }

        public static string Audit(KittyModule m, string list, string report, bool withInfo, Action<string> onLine)
        {
            return PowerShell.Run(Invoke(m, "-Mode Audit -FileFindingList " + Q(list) + " -Report -ReportFile " + Q(report)
                                             + (withInfo ? "" : " -SkipMachineInformation -SkipUserInformation")), onLine);
        }

        // Liest die aktuellen Werte und schreibt sie im Listenformat – HailMary mit dieser Datei stellt sie wieder her
        public static string Backup(KittyModule m, string list, string backupFile, Action<string> onLine)
        {
            return PowerShell.Run(Invoke(m, "-Mode Config -FileFindingList " + Q(list) + " -Backup -BackupFile " + Q(backupFile)
                                             + " -SkipMachineInformation -SkipUserInformation"), onLine);
        }

        // Setzt alle Einträge der Liste auf den empfohlenen Wert. Der Wiederherstellungspunkt wird vom Tool selbst angelegt.
        public static string HailMary(KittyModule m, string list, string report, Action<string> onLine)
        {
            return PowerShell.Run(Invoke(m, "-Mode HailMary -FileFindingList " + Q(list) + " -Report -ReportFile " + Q(report)
                                             + " -SkipRestorePoint -SkipMachineInformation -SkipUserInformation"
                                             + (m.NeedsAllowCustomList ? " -AllowCustomList" : "")), onLine);
        }

        // Fehlermeldungen von HardeningKitty („[!] Zeit - Text“) für Hinweise an den Benutzer
        public static string Errors(string output)
        {
            var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("[!]") && !l.Contains(" ID ")).ToList();
            return string.Join("\n", lines.Take(3).Select(l => Regex.Replace(l, @"^\[!\]\s*[\d\.\:/ ]+-\s*", "")));
        }
    }

    // Startet Windows PowerShell 5.1 ohne Fenster
    static class PowerShell
    {
        static string Exe
        {
            get
            {
                string dir = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative")
                    : Environment.SystemDirectory;
                return Path.Combine(dir, @"WindowsPowerShell\v1.0\powershell.exe");
            }
        }

        public static string Quote(string s) { return "'" + s.Replace("'", "''") + "'"; }

        public static string Run(string script, Action<string> onLine)
        {
            string full = "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
                          "$PSDefaultParameterValues['Export-Csv:Encoding'] = 'UTF8'; " + script;
            Log.Write("PowerShell: " + script);
            var psi = new ProcessStartInfo(Exe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                                                Convert.ToBase64String(Encoding.Unicode.GetBytes(full)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath()
            };
            var sb = new StringBuilder();
            object sync = new object();
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler handler = (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (sync) sb.AppendLine(e.Data);
                    if (onLine != null) onLine(e.Data);
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                string output;
                lock (sync) output = sb.ToString();
                Log.Write("  beendet mit Code " + p.ExitCode + ", " + output.Split('\n').Length + " Zeilen Ausgabe");
                string errors = Kitty.Errors(output);
                if (errors != "") Log.Write("  Meldungen: " + errors.Replace("\n", " | "));
                return output;
            }
        }
    }

    // Prüft die Authenticode-Signatur einer Datei über die Windows-Vertrauensprüfung (WinVerifyTrust)
    static class AuthenticodeCheck
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class FileInfoData
        {
            public uint cbStruct = (uint)Marshal.SizeOf(typeof(FileInfoData));
            public string pcwszFilePath;
            public IntPtr hFile = IntPtr.Zero;
            public IntPtr pgKnownSubject = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class TrustData
        {
            public uint cbStruct = (uint)Marshal.SizeOf(typeof(TrustData));
            public IntPtr pPolicyCallbackData = IntPtr.Zero;
            public IntPtr pSIPClientData = IntPtr.Zero;
            public uint dwUIChoice = 2;            // WTD_UI_NONE
            public uint fdwRevocationChecks = 0;   // WTD_REVOKE_NONE
            public uint dwUnionChoice = 1;         // WTD_CHOICE_FILE
            public IntPtr pFile;
            public uint dwStateAction = 0;
            public IntPtr hWVTStateData = IntPtr.Zero;
            public string pwszURLReference = null;
            public uint dwProvFlags = 0;
            public uint dwUIContext = 0;
            public IntPtr pSignatureSettings = IntPtr.Zero;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, TrustData data);

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        // Liefert eine Beschreibung bei gültiger Signatur, sonst Ausnahme mit Erklärung
        public static string Verify(string path)
        {
            var file = new FileInfoData { pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfoData)));
            int rc;
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                rc = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, new TrustData { pFile = pFile });
            }
            finally { Marshal.FreeHGlobal(pFile); }

            string signer = "";
            try { signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).GetNameInfo(X509NameType.SimpleName, false); }
            catch (Exception ex) { Log.Write("  Signaturgeber nicht lesbar: " + ex.Message); }

            if (rc == 0) return "gültig" + (signer != "" ? ", signiert von " + signer : "");   // nur fürs Log
            string code = "0x" + ((uint)rc).ToString("X8");
            string why;
            switch ((uint)rc)
            {
                case 0x800B0100: why = Lang.T("Die Datei ist nicht signiert.", "The file is not signed."); break;
                case 0x80096010: why = Lang.T("Die Datei wurde nach dem Signieren verändert.", "The file was modified after signing."); break;
                case 0x800B0109: why = Lang.T("Das Zertifikat stammt nicht von einer vertrauenswürdigen Stelle.", "The certificate is not from a trusted authority."); break;
                case 0x800B0101: why = Lang.T("Das Zertifikat ist abgelaufen.", "The certificate has expired."); break;
                default: why = Lang.T("Die Signatur konnte nicht bestätigt werden.", "The signature could not be verified."); break;
            }
            throw new InvalidOperationException(Lang.T("Signaturprüfung fehlgeschlagen: ", "Signature check failed: ") + why + " (" + code + ")\n" +
                                                Lang.T("Aus Sicherheitsgründen wurde nichts installiert.", "For security reasons nothing was installed."));
        }
    }

    enum Stufe { Unkritisch, Rueckfrage, Manuell }

    class Finding
    {
        public CsvRow Row;
        public string HeaderRaw, ListFile;
        public bool IsUserList;
        public string Result = "", Recommended = "", TestResult = "";   // aus dem Bericht
        public Stufe Level;
        public string Hint = "";

        public string Id { get { return Row["ID"]; } }
        public string Category { get { return Row["Category"]; } }
        public string Name { get { return Row["Name"]; } }
        public string Method { get { return Row["Method"]; } }
        public string Severity { get { return Row["Severity"]; } }
        public bool Passed { get { return TestResult == "Passed"; } }
        public bool Failed { get { return TestResult == "Failed"; } }
        public bool IsRegistry { get { return Method == "Registry" || Method == "RegistryList"; } }
        public string ListName { get { return Path.GetFileNameWithoutExtension(ListFile); } }
        public string Key { get { return ListName + "|" + Id + "|" + Name; } }
        public string RecommendedText { get { return Recommended != "" ? Recommended : Row["RecommendedValue"]; } }
    }

    static class Reports
    {
        // Verbindet Prüfliste und Bericht (über ID + Name; IDs sind in manchen Listen nicht eindeutig)
        public static List<Finding> Join(string listFile, string reportFile, bool user)
        {
            CsvTable list = CsvTable.Load(listFile);
            var byKey = new Dictionary<string, Queue<CsvRow>>();
            if (File.Exists(reportFile))
                foreach (CsvRow r in CsvTable.Load(reportFile).Rows)
                {
                    Queue<CsvRow> q;
                    string k = r["ID"] + "|" + r["Name"];
                    if (!byKey.TryGetValue(k, out q)) byKey[k] = q = new Queue<CsvRow>();
                    q.Enqueue(r);
                }
            var result = new List<Finding>();
            foreach (CsvRow row in list.Rows)
            {
                var f = new Finding { Row = row, HeaderRaw = list.HeaderRaw, ListFile = listFile, IsUserList = user };
                Queue<CsvRow> q;
                if (byKey.TryGetValue(row["ID"] + "|" + row["Name"], out q) && q.Count > 0)
                {
                    CsvRow r = q.Dequeue();
                    f.Result = r["Result"];
                    f.Recommended = r["Recommended"];
                    f.TestResult = r["TestResult"];
                }
                Einstufung.Apply(f);
                result.Add(f);
            }
            return result;
        }
    }

    class AuditResult
    {
        public readonly List<Finding> Findings = new List<Finding>();
        public string ListKey, ListDisplay, Folder;
        public DateTime Time;
        public int Passed, Low, Medium, High, NotChecked;

        public int Total { get { return Passed + Low + Medium + High; } }
        public int Open { get { return Low + Medium + High; } }

        // Berechnung wie in HardeningKitty: bestanden 4, niedrig 2, mittel 1, hoch 0 Punkte → Note 1 bis 6
        public double Score
        {
            get
            {
                if (Total == 0 || Passed == 0) return 1.0;
                return Math.Round((Passed * 4.0 + Low * 2.0 + Medium) / (Total * 4.0) * 5 + 1, 2);
            }
        }

        public void Count()
        {
            Passed = Low = Medium = High = NotChecked = 0;
            foreach (Finding f in Findings)
            {
                if (f.Passed) Passed++;
                else if (!f.Failed) NotChecked++;
                else if (f.Severity == "High") High++;
                else if (f.Severity == "Low") Low++;
                else Medium++;
            }
        }

        public static string FormatScore(double s) { return s.ToString("0.00", CultureInfo.GetCultureInfo(Lang.IsEnglish ? "en-US" : "de-DE")); }
    }

    // Einstufung offener Punkte: was lässt sich gefahrlos automatisch beheben, was braucht eine bewusste Entscheidung?
    // Die Regeln sind bewusst vorsichtig: Im Zweifel „mit Rückfrage“.
    static class Einstufung
    {
        // Methoden, die HardeningKitty im HailMary-Modus setzen kann
        static readonly string[] Settable = { "Registry", "RegistryList", "secedit", "auditpol", "accountpolicy", "accesschk",
                                              "MpPreference", "MpPreferenceAsr", "WindowsOptionalFeature", "FirewallRule",
                                              "Processmitigation", "bcdedit", "ScheduledTask" };

        // Operatoren, bei denen das Setzen des Listenwerts die Empfehlung sicher erfüllt
        static readonly string[] SafeOperators = { "=", ">=", "<=", "=|0", "<=!0" };

        // Dienste, die auf normalen Arbeitsplätzen niemand vermisst
        static readonly string[] HarmlessServices = { "Xbox", "Xbl", "Windows Media Player Network", "Simple TCP", "Infrared",
            "Windows Error Reporting", "Problem Reports", "Windows Event Collector", "iSCSI", "Link-Layer Topology",
            "Downloaded Maps Manager", "Remote Access Auto Connection", "Special Administration Console", "RPC) Locator" };

        class Rule
        {
            public string Text; public bool OnCategory; public Stufe Level; public string Hint;
            public Rule(string text, bool onCategory, Stufe level, string hint) { Text = text; OnCategory = onCategory; Level = level; Hint = hint; }
        }

        static string T(string de, string en) { return Lang.T(de, en); }

        static Rule[] rules;
        static bool rulesEnglish;

        // Reihenfolge zählt: die erste passende Regel gewinnt (spezielle vor allgemeinen). Je Sprache einmal aufgebaut.
        static Rule[] Rules
        {
            get
            {
                if (rules != null && rulesEnglish == Lang.IsEnglish) return rules;
                string net = T("Betrifft Netzwerkfreigaben und -protokolle – ältere NAS, Drucker oder Server sind danach eventuell nicht mehr erreichbar.",
                               "Affects network shares and protocols – older NAS devices, printers or servers may no longer be reachable.");
                string remote = T("Betrifft die Fernwartung (Remotedesktop, WinRM, Remoteunterstützung).",
                                  "Affects remote administration (Remote Desktop, WinRM, Remote Assistance).");
                string deep = T("Tiefgreifende Sicherheitsfunktion – kann ältere Treiber, virtuelle Maschinen oder Anmeldeverfahren beeinträchtigen.",
                                "Deep security feature – may affect older drivers, virtual machines or sign-in methods.");
                string logon = T("Ändert Anmelde- bzw. Netzwerksicherheit – vor allem in Umgebungen mit älteren Geräten vorher testen.",
                                 "Changes sign-in or network security – test first, especially in environments with older devices.");
                string log = T("Nur zusätzliche Protokollierung bzw. Größe der Ereignisprotokolle – keine Auswirkung auf die Arbeit.",
                               "Only additional logging or event log size – no effect on daily work.");
                string privacy = T("Datenschutz bzw. Werbung/Vorschläge von Microsoft – keine Auswirkung auf die tägliche Arbeit.",
                                   "Privacy or Microsoft ads/suggestions – no effect on daily work.");
                string safe = T("Gilt als unkritisch: keine spürbare Auswirkung auf die tägliche Arbeit.",
                                "Considered safe: no noticeable effect on daily work.");
                string msAccount = T("Kann die Anmeldung mit Microsoft-Konten blockieren.", "May block sign-in with Microsoft accounts.");
                string print = T("Kann das Drucken über ältere Druckserver oder Treiber verhindern.", "May prevent printing via older print servers or drivers.");
                const Stufe A = Stufe.Rueckfrage, U = Stufe.Unkritisch;
                rules = new[]
                {
                    new Rule("MSS: (AutoAdminLogon)", false, A, T("Schaltet die automatische Anmeldung ab (z. B. bei Kiosk-Rechnern).", "Turns off automatic sign-in (e.g. on kiosk computers).")),
                    new Rule("Accounts: Block Microsoft accounts", false, A, msAccount),
                    new Rule("Microsoft account", false, A, msAccount),
                    new Rule("Devices: Prevent users from installing printer drivers", false, A, T("Benutzer können keine Druckertreiber mehr installieren.", "Users can no longer install printer drivers.")),
                    new Rule("System cryptography", false, A, T("Kann bei jeder Verwendung privater Schlüssel eine Abfrage erzwingen.", "May force a prompt each time a private key is used.")),
                    new Rule("Apply UAC restrictions to local accounts", false, A, T("Fernverwaltung mit lokalen Administratorkonten wird eingeschränkt.", "Remote administration with local administrator accounts is restricted.")),
                    new Rule("Configure SMB v1", false, A, net),
                    new Rule("NetBT NodeType", false, A, net),

                    new Rule("User Rights Assignment", true, A, T("Benutzerrechte – eine falsche Zuweisung kann Dienste, Hyper-V oder Anmeldungen stören.", "User rights – a wrong assignment can break services, Hyper-V or sign-ins.")),
                    new Rule("Account Policies", true, A, T("Kennwort- und Kontosperrungsrichtlinie – betrifft alle lokalen Konten.", "Password and account lockout policy – affects all local accounts.")),
                    new Rule("Windows Firewall", true, A, T("Firewall-Profile – z. B. „lokale Regeln nicht anwenden“ kann Programme blockieren.", "Firewall profiles – e.g. “do not apply local rules” may block programs.")),
                    new Rule("Advanced Audit Policy", true, U, log),
                    new Rule("MSS (Legacy)", true, U, T("Feinheiten des Netzwerkstapels (TCP/IP) – praktisch ohne Nebenwirkungen.", "Network stack details (TCP/IP) – practically no side effects.")),
                    new Rule("Scheduled Task", true, U, safe),

                    new Rule("Remote Desktop", false, A, remote),
                    new Rule("WinRM", false, A, remote),
                    new Rule("Windows Remote Shell", false, A, remote),
                    new Rule("Remote Assistance", false, A, remote),
                    new Rule("BitLocker", false, A, T("BitLocker-Richtlinien (z. B. PIN beim Start) wirken sich auf den Startvorgang aus.", "BitLocker policies (e.g. PIN at startup) affect the boot process.")),
                    new Rule("Device Guard", false, A, deep),
                    new Rule("Local Security Authority", false, A, deep),
                    new Rule("Credentials Delegation", false, A, deep),
                    new Rule("Kerberos", false, A, deep),
                    new Rule("KDC", false, A, deep),
                    new Rule("LAPS", false, A, T("Lokale Administratorkennwörter (LAPS) – nur mit eingerichteter LAPS-Umgebung sinnvoll.", "Local administrator passwords (LAPS) – only useful with a LAPS environment in place.")),
                    new Rule("Net Logon", false, A, deep),
                    new Rule("Security Account Manager", false, A, deep),
                    new Rule("Remote Procedure Call", false, A, deep),
                    new Rule("Lanman", false, A, net),
                    new Rule("Network Provider", false, A, net),
                    new Rule("SSL Configuration", false, A, net),
                    new Rule("Disable IPv6", false, A, net),
                    new Rule("DNS Client", false, A, net),
                    new Rule("Windows Connection Manager", false, A, T("Kann parallele Verbindungen (z. B. WLAN neben LAN) unterbinden.", "May prevent simultaneous connections (e.g. Wi-Fi next to LAN).")),
                    new Rule("Point and Print", false, A, print),
                    new Rule("Printers", true, A, print),
                    new Rule("Print Spooler", false, A, T("Ohne Druckwarteschlange kann nicht gedruckt werden.", "Without the print spooler, printing is not possible.")),
                    new Rule("Sleep Settings", false, A, T("Kann den Energiesparmodus (Standby) abschalten oder beim Aufwachen ein Kennwort verlangen.", "May disable sleep (standby) or require a password on wake-up.")),
                    new Rule("Windows Update", false, A, T("Ändert das Update- und Neustartverhalten.", "Changes update and restart behavior.")),
                    new Rule("Device Installation", false, A, T("Kann die Installation neuer Geräte oder Treiber blockieren.", "May block installation of new devices or drivers.")),
                    new Rule("Logon:", false, A, T("Ändert die Anmeldung (z. B. PIN-Anmeldung, Netzwerkauswahl auf dem Sperrbildschirm).", "Changes sign-in (e.g. PIN sign-in, network selection on the lock screen).")),
                    new Rule("Windows Hello", false, A, T("Ändert die Anmeldung mit Windows Hello (PIN, Gesicht, Fingerabdruck).", "Changes Windows Hello sign-in (PIN, face, fingerprint).")),
                    new Rule("OneDrive", false, A, T("Kann OneDrive als Speicherort sperren.", "May block OneDrive as a storage location.")),
                    new Rule("Store:", false, A, T("Schränkt den Microsoft Store ein.", "Restricts the Microsoft Store.")),
                    new Rule("Desktop App Installer", false, A, T("Kann winget bzw. den App Installer einschränken.", "May restrict winget or App Installer.")),
                    new Rule("App Package Deployment", false, A, T("Kann die Installation von Apps einschränken.", "May restrict app installation.")),
                    new Rule("Camera", false, A, T("Kann die Kamera sperren.", "May block the camera.")),
                    new Rule("Location and Sensors", false, A, T("Apps erhalten keinen Standort mehr (z. B. automatische Zeitzone).", "Apps no longer get location (e.g. automatic time zone).")),
                    new Rule("Windows Sandbox", false, A, T("Schränkt die Windows-Sandbox ein.", "Restricts Windows Sandbox.")),
                    new Rule("Microsoft Defender Application Guard", false, A, T("Ändert Application Guard (isolierte Browserfenster).", "Changes Application Guard (isolated browser windows).")),
                    new Rule("Internet Explorer", false, A, T("Betrifft Internet Explorer bzw. den IE-Modus in Microsoft Edge (ältere Webanwendungen).", "Affects Internet Explorer or IE mode in Microsoft Edge (older web applications).")),
                    new Rule("Interactive logon", false, A, logon),
                    new Rule("Network access", false, A, logon),
                    new Rule("Network security", false, A, logon),
                    new Rule("Microsoft network", false, A, net),
                    new Rule("Domain member", false, A, logon),
                    new Rule("User Account Control", false, A, T("Ändert die Benutzerkontensteuerung (Rückfragen bei Adminrechten).", "Changes User Account Control (prompts for admin rights).")),
                    new Rule("Accounts:", false, A, logon),

                    new Rule("Audit Process Creation", false, U, log),
                    new Rule("Event Log Service", false, U, log),
                    new Rule("Windows PowerShell", false, U, T("Zusätzliche Protokollierung von PowerShell – keine Auswirkung auf die Arbeit.", "Additional PowerShell logging – no effect on daily work.")),
                    new Rule("Audit:", false, U, log),
                    new Rule("System objects", false, U, safe),
                    new Rule("Microsoft Defender Antivirus", false, U, T("Schutzfunktionen des Virenscanners.", "Antivirus protection features.")),
                    new Rule("Windows Defender SmartScreen", false, U, T("Warnt vor unbekannten Downloads und Webseiten.", "Warns about unknown downloads and websites.")),
                    new Rule("App and browser protection", false, U, safe),
                    new Rule("Internet Communication Management", false, U, privacy),
                    new Rule("Data Collection", false, U, privacy),
                    new Rule("Cloud Content", false, U, privacy),
                    new Rule("Windows Ink Workspace", false, U, privacy),
                    new Rule("Windows Game Recording", false, U, privacy),
                    new Rule("RSS Feeds", false, U, safe),
                    new Rule("News and interests", false, U, privacy),
                    new Rule("Widg", false, U, privacy),
                    new Rule("Search:", false, U, privacy),
                    new Rule("App Privacy", false, U, privacy),
                    new Rule("AutoPlay", false, U, T("Kein automatisches Ausführen von USB-Sticks und CDs.", "No automatic execution of USB drives and CDs.")),
                    new Rule("File Explorer", false, U, safe),
                    new Rule("Credential User Interface", false, U, safe),
                    new Rule("Biometrics", false, U, safe),
                    new Rule("Windows Logon Options", false, U, safe),
                    new Rule("Windows Installer", false, U, safe),
                    new Rule("Early Launch Antimalware", false, U, safe),
                    new Rule("Kernel DMA Protection", false, U, safe),
                    new Rule("Personalization", false, U, safe),
                    new Rule("Allow Online Tips", false, U, privacy),
                    new Rule("Notifications", false, U, privacy),
                    new Rule("Remove Personalized Website", false, U, privacy),
                    new Rule("Regional and Language Options", false, U, privacy),
                    new Rule("Fonts", false, U, privacy),
                    new Rule("Link-Layer Topology Discovery", false, U, safe),
                    new Rule("Turn on Responder", false, U, safe),
                    new Rule("Peer-to-Peer", false, U, safe),
                    new Rule("Windows Connect Now", false, U, safe),
                    new Rule("WLAN Service", false, U, safe),
                    new Rule("Push To Install", false, U, privacy),
                    new Rule("Messaging", false, U, privacy),
                    new Rule("Connect:", false, U, safe),
                    new Rule("Software Protection Platform", false, U, privacy),
                    new Rule("Locale Services", false, U, safe),
                    new Rule("Troubleshooting and Diagnostics", false, U, privacy),
                    new Rule("Windows Performance PerfTrack", false, U, privacy),
                    new Rule("App and Device Inventory", false, U, privacy),
                    new Rule("Delivery Optimization", false, U, privacy),
                    new Rule("Group Policy", false, U, safe),
                    new Rule("OS Policies", false, U, privacy),
                    new Rule("Time Providers", false, U, safe),
                    new Rule("User Profiles", false, U, privacy),
                    new Rule("App runtime", false, U, safe),
                    new Rule("sudo", false, U, safe),
                    new Rule("Structured Exception Handling", false, U, safe),
                    new Rule("WDigest", false, U, safe),
                    new Rule("Certificate Padding", false, U, safe)
                };
                rulesEnglish = Lang.IsEnglish;
                return rules;
            }
        }

        public static void Apply(Finding f)
        {
            string op = f.Row["Operator"];
            if (!Settable.Contains(f.Method))
            {
                f.Level = Stufe.Manuell;
                f.Hint = T("HardeningKitty kann diese Prüfung nur auswerten, nicht setzen (Methode „" + f.Method + "“). Bitte von Hand einstellen.",
                           "HardeningKitty can only check this setting, not apply it (method “" + f.Method + "”). Please configure it manually.");
                return;
            }
            // Überwachung „enthält Success“: HailMary schaltet dabei „Failure“ ab – nur gefahrlos, wenn bisher keine Fehlerüberwachung aktiv ist
            bool auditOnlyAdds = f.Method == "auditpol" && op == "contains" && f.Result.IndexOf("Failure", StringComparison.OrdinalIgnoreCase) < 0;
            if (!SafeOperators.Contains(op) && !auditOnlyAdds)
            {
                f.Level = Stufe.Manuell;
                f.Hint = T("Empfohlen ist ein Wertebereich (Operator „" + op + "“). Automatisches Setzen wäre hier unzuverlässig – bitte von Hand einstellen.",
                           "The recommendation is a range of values (operator “" + op + "”). Applying it automatically would be unreliable – please configure it manually.");
                return;
            }
            if (f.Method == "MpPreferenceAsr")
            {
                f.Level = Stufe.Rueckfrage;
                f.Hint = T("Regel zur Verringerung der Angriffsfläche (ASR) – kann Office-Makros, Skripte oder Verwaltungswerkzeuge blockieren. Am besten zuerst im Überwachungsmodus testen.",
                           "Attack surface reduction (ASR) rule – may block Office macros, scripts or admin tools. Best tested in audit mode first.");
                return;
            }
            if (f.Category == "System Services")
            {
                bool harmless = HarmlessServices.Any(s => f.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
                f.Level = harmless ? Stufe.Unkritisch : Stufe.Rueckfrage;
                f.Hint = harmless ? T("Deaktiviert einen Dienst, der auf normalen Arbeitsplätzen nicht gebraucht wird.", "Disables a service that typical workstations do not need.")
                                  : T("Deaktiviert einen Dienst – vorher prüfen, ob er auf diesem Rechner gebraucht wird.", "Disables a service – check first whether this computer needs it.");
                return;
            }
            foreach (Rule r in Rules)
            {
                bool hit = r.OnCategory ? f.Category.StartsWith(r.Text, StringComparison.OrdinalIgnoreCase)
                                        : f.Name.IndexOf(r.Text, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit) { f.Level = r.Level; f.Hint = r.Hint; return; }
            }
            f.Level = Stufe.Rueckfrage;
            f.Hint = T("Keine pauschale Einschätzung möglich – bitte anhand der Beschreibung entscheiden.", "No general assessment possible – please decide based on the description.");
        }

        public static string Title(Stufe s)
        {
            switch (s)
            {
                case Stufe.Unkritisch: return T("Unkritisch", "Safe");
                case Stufe.Rueckfrage: return T("Mit Rückfrage", "Needs review");
                default: return T("Manuell", "Manual");
            }
        }
    }

    // Bewusst nicht gehärtete Punkte (pro Rechner): Einstellungen\Ausnahmen.txt
    static class Ausnahmen
    {
        static HashSet<string> keys;
        static string FilePath { get { return Path.Combine(Paths.Settings, "Ausnahmen.txt"); } }

        static HashSet<string> Keys
        {
            get
            {
                if (keys == null)
                {
                    keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        if (File.Exists(FilePath))
                            foreach (string l in File.ReadAllLines(FilePath, Encoding.UTF8))
                                if (l.Trim() != "" && !l.StartsWith("#")) keys.Add(l.Trim());
                    }
                    catch (Exception ex) { Log.Write("Ausnahmen lesen: " + ex.Message); }
                }
                return keys;
            }
        }

        public static bool Contains(Finding f) { return Keys.Contains(f.Key); }

        public static void Set(Finding f, bool on)
        {
            if (on) Keys.Add(f.Key); else Keys.Remove(f.Key);
            File.WriteAllText(FilePath, "# Bewusst nicht gehärtete Punkte (Liste|ID|Name) – werden nicht vorgeschlagen und von der Automatik nicht gesetzt\r\n" +
                                        string.Join("\r\n", Keys.OrderBy(k => k)) + "\r\n", Encoding.UTF8);
            Log.Write("Ausnahme " + (on ? "gesetzt: " : "aufgehoben: ") + f.Key);
            if (on) Haertungsprofil.Remove(f);
        }
    }

    // Erfolgreich behobene Punkte, die die Automatik regelmäßig prüft und bei Bedarf erneut setzt.
    // Pro Prüfliste eine Datei im Originalformat: Einstellungen\Haertungsprofil\<Liste>.csv
    static class Haertungsprofil
    {
        public static string Folder { get { return Paths.Sub(@"Einstellungen\Haertungsprofil"); } }

        public static string[] Files() { return Directory.GetFiles(Folder, "*.csv"); }

        public static int Count()
        {
            int n = 0;
            foreach (string f in Files())
                try { n += CsvTable.Load(f).Rows.Count; }
                catch (Exception ex) { Log.Write("Profil lesen: " + ex.Message); }
            return n;
        }

        public static void Add(IEnumerable<Finding> findings)
        {
            foreach (var g in findings.GroupBy(f => f.ListName))
            {
                string path = Path.Combine(Folder, g.Key + ".csv");
                var rows = new List<string>();
                var known = new HashSet<string>();
                if (File.Exists(path))
                    foreach (CsvRow r in CsvTable.Load(path).Rows) { rows.Add(r.Raw); known.Add(r["ID"] + "|" + r["Name"]); }
                int added = 0;
                foreach (Finding f in g)
                    if (known.Add(f.Id + "|" + f.Name)) { rows.Add(f.Row.Raw); added++; }
                CsvTable.WriteRaw(path, g.First().HeaderRaw, rows);
                Log.Write("Härtungsprofil " + g.Key + ": " + added + " Einträge ergänzt, jetzt " + rows.Count);
            }
        }

        public static void Remove(Finding f)
        {
            RemoveFrom(Path.Combine(Folder, f.ListName + ".csv"), f.Id, f.Name);
        }

        // Nach dem Zurückspielen einer Sicherung: Eintrag aus allen Profildateien entfernen
        public static void RemoveEverywhere(string id, string name)
        {
            foreach (string file in Files()) RemoveFrom(file, id, name);
        }

        static void RemoveFrom(string path, string id, string name)
        {
            if (!File.Exists(path)) return;
            CsvTable t = CsvTable.Load(path);
            var rest = t.Rows.Where(r => !(r["ID"] == id && r["Name"] == name)).Select(r => r.Raw).ToList();
            if (rest.Count == t.Rows.Count) return;
            CsvTable.WriteRaw(path, t.HeaderRaw, rest);
            Log.Write("Härtungsprofil " + Path.GetFileNameWithoutExtension(path) + ": Eintrag entfernt (" + id + " " + name + ")");
        }
    }
}

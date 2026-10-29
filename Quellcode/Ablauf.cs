// Ablauf.cs – Arbeitsabläufe, die Programm und Automatik gemeinsam nutzen:
//   Ablauf     – Prüfen, Beheben (Sicherung → Setzen → Prüfen) und Sicherung zurückspielen
//   Verlauf    – Score-Verlauf aller Prüfungen (Berichte\Verlauf.csv)
//   Automatik  – geplante Aufgabe einrichten; das Programm läuft dafür aus C:\Program Files\HardeningKitty UI
//   AutoLauf   – der Lauf ohne Oberfläche (Aufruf mit /auto)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using MeynTools;

namespace HardeningKittyUI
{
    class FixResult
    {
        public string Folder;
        public int Ok, Failed;
        public readonly List<Finding> Fixed = new List<Finding>();
        public readonly List<string> Problems = new List<string>();
    }

    static class Ablauf
    {
        static readonly Regex ResultLine = new Regex(@"^\[.\] ID ");

        public static AuditResult Audit(KittyModule m, FindingList list, bool withUser, Action<string> progress)
        {
            string folder = Path.Combine(Paths.Reports, Paths.Stamp() + "_" + Kitty.Short(list.Key));
            Directory.CreateDirectory(folder);
            var res = new AuditResult { ListKey = list.Key, ListDisplay = list.Display, Folder = folder, Time = DateTime.Now };
            var files = new List<string>();
            if (list.MachineFile != null) files.Add(list.MachineFile);
            if (list.UserFile != null && (withUser || list.MachineFile == null)) files.Add(list.UserFile);

            Log.Write("---- Prüfung: " + list.Display + " ----");
            var output = new StringBuilder();
            var info = new StringBuilder("Liste=" + list.Key + "\r\nAnzeige=" + list.Display + "\r\nZeit=" + res.Time.ToString("s") + "\r\n");
            string outputFile = Path.Combine(folder, "HardeningKitty-Ausgabe.txt");
            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                bool user = file == list.UserFile;
                string part = user ? Lang.T("Benutzereinstellungen", "user settings") : Lang.T("Rechnereinstellungen", "computer settings");
                int total = CsvTable.Load(file).Rows.Count, done = 0;
                string report = Path.Combine(folder, user ? "bericht_benutzer.csv" : "bericht_rechner.csv");
                Log.Write(part + ": " + Path.GetFileName(file) + " (" + total + " Prüfungen)");
                progress(Lang.T("Prüfe ", "Checking ") + part + " …");
                string text = Kitty.Audit(m, file, report, i == 0, line =>
                {
                    if (!ResultLine.IsMatch(line)) return;
                    done++;
                    if (done % 5 == 0 || done == total) progress(Lang.T("Prüfe ", "Checking ") + part + " … " + done + Lang.T(" von ", " of ") + total);
                });
                output.AppendLine(text);
                File.WriteAllText(outputFile, output.ToString(), Encoding.UTF8);
                Match score = Regex.Match(text, @"Your HardeningKitty score is:[^\r\n]*");
                Log.Write("  " + (score.Success ? score.Value.Trim() : "keine Auswertung in der Ausgabe"));
                if (!File.Exists(report))
                {
                    string err = Kitty.Errors(text);
                    throw new InvalidOperationException(Lang.T("HardeningKitty hat keinen Bericht erzeugt", "HardeningKitty did not create a report") + (err != "" ? ":\n" + err : ".") +
                                                        Lang.T("\n\nDie vollständige Ausgabe steht in:\n", "\n\nThe full output is in:\n") + outputFile);
                }
                res.Findings.AddRange(Reports.Join(file, report, user));
                info.Append("Datei=" + (user ? "user" : "machine") + "|" + file + "|" + report + "\r\n");
            }
            res.Count();
            File.WriteAllText(Path.Combine(folder, "pruefung.txt"), info.ToString(), Encoding.UTF8);
            Verlauf.Add(res);
            Log.Write("Ergebnis: Score " + AuditResult.FormatScore(res.Score) + ", " + res.Total + " Prüfungen, " + res.Passed + " bestanden, " +
                      res.High + " hoch, " + res.Medium + " mittel, " + res.Low + " niedrig, " + res.NotChecked + " nicht geprüft");
            return res;
        }

        // Letzte Prüfung aus dem Berichtsordner wieder einlesen (damit nach dem Start nicht neu geprüft werden muss)
        public static AuditResult LoadLast()
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(Paths.Reports).GetDirectories()
                    .Where(d => File.Exists(Path.Combine(d.FullName, "pruefung.txt")))
                    .OrderByDescending(d => d.Name, StringComparer.Ordinal).FirstOrDefault();
                if (dir == null) return null;
                var res = new AuditResult { Folder = dir.FullName, Time = dir.CreationTime };
                foreach (string line in File.ReadAllLines(Path.Combine(dir.FullName, "pruefung.txt"), Encoding.UTF8))
                {
                    if (line.StartsWith("Liste=")) res.ListKey = line.Substring(6);
                    else if (line.StartsWith("Anzeige=")) res.ListDisplay = line.Substring(8);
                    else if (line.StartsWith("Zeit=")) DateTime.TryParse(line.Substring(5), CultureInfo.InvariantCulture, DateTimeStyles.None, out res.Time);
                    else if (line.StartsWith("Datei="))
                    {
                        string[] p = line.Substring(6).Split('|');
                        if (p.Length == 3 && File.Exists(p[1]) && File.Exists(p[2])) res.Findings.AddRange(Reports.Join(p[1], p[2], p[0] == "user"));
                    }
                }
                if (res.Findings.Count == 0) return null;
                res.Count();
                Log.Write("Letzte Prüfung geladen: " + dir.Name + " (Score " + AuditResult.FormatScore(res.Score) + ")");
                return res;
            }
            catch (Exception ex)
            {
                Log.Write("Letzte Prüfung nicht lesbar: " + ex.Message);
                return null;
            }
        }

        // Behebt die ausgewählten Punkte. Reihenfolge: 1. alles sichern  2. setzen (HailMary mit Teilliste)  3. erneut prüfen
        public static FixResult Fix(KittyModule m, List<Finding> selected, string art, Action<string> progress)
        {
            Sicherung bk = Sicherung.Create(art);
            var res = new FixResult { Folder = bk.Folder };
            var output = new StringBuilder();
            string outputFile = Path.Combine(bk.Folder, "HardeningKitty-Ausgabe.txt");
            var groups = selected.GroupBy(f => f.ListFile).ToList();
            Log.Write("---- Beheben: " + selected.Count + " Punkte, Sicherung in " + bk.Folder + " ----");
            foreach (Finding f in selected) Log.Write("  " + f.ListName + " ID " + f.Id + " " + f.Name + " (" + Einstufung.Title(f.Level) + ")");

            // 1. Sichern: Registry direkt (exakt, inkl. „war nicht vorhanden“), alles andere über HardeningKitty (Config -Backup)
            progress(Lang.T("Sichere die aktuellen Werte …", "Backing up current values …"));
            foreach (Finding f in selected.Where(x => x.IsRegistry))
                bk.AddRegistry(f.Row["RegistryPath"], f.Method == "RegistryList" ? null : f.Row["RegistryItem"]);
            bk.Save();
            foreach (var g in groups)
            {
                var other = g.Where(f => !f.IsRegistry).ToList();
                if (other.Count == 0) continue;
                string name = Kitty.Short(g.First().ListName);
                string list = Path.Combine(bk.Folder, "auswahl_sonstige_" + name + ".csv");
                string backupCsv = Path.Combine(bk.Folder, "hk_sicherung_" + name + ".csv");
                CsvTable.WriteRaw(list, g.First().HeaderRaw, other.Select(f => f.Row.Raw));
                output.AppendLine(Kitty.Backup(m, list, backupCsv, null));
                File.WriteAllText(outputFile, output.ToString(), Encoding.UTF8);
                if (!File.Exists(backupCsv))
                    throw new InvalidOperationException(Lang.T("HardeningKitty konnte die aktuellen Werte nicht sichern. Es wurde nichts geändert.",
                                                               "HardeningKitty could not back up the current values. Nothing was changed.") + "\n\nDetails: " + outputFile);
            }

            // 2. Setzen und 3. prüfen
            foreach (var g in groups)
            {
                List<Finding> items = g.ToList();
                string name = Kitty.Short(items[0].ListName);
                string list = Path.Combine(bk.Folder, "auswahl_" + name + ".csv");
                CsvTable.WriteRaw(list, items[0].HeaderRaw, items.Select(f => f.Row.Raw));
                int done = 0;
                progress(Lang.T("Setze " + items.Count + " Einstellungen …", "Applying " + items.Count + " settings …"));
                output.AppendLine(Kitty.HailMary(m, list, Path.Combine(bk.Folder, "gesetzt_" + name + ".csv"), line =>
                {
                    if (ResultLine.IsMatch(line)) progress(Lang.T("Setze Einstellungen … ", "Applying settings … ") + (++done) + Lang.T(" von ", " of ") + items.Count);
                }));
                progress(Lang.T("Prüfe das Ergebnis …", "Verifying the result …"));
                string verify = Path.Combine(bk.Folder, "pruefung_" + name + ".csv");
                output.AppendLine(Kitty.Audit(m, list, verify, false, null));
                File.WriteAllText(outputFile, output.ToString(), Encoding.UTF8);

                // Die Teilliste hat dieselbe Reihenfolge wie die Auswahl
                List<Finding> after = Reports.Join(list, verify, items[0].IsUserList);
                for (int i = 0; i < items.Count && i < after.Count; i++)
                {
                    items[i].Result = after[i].Result;
                    items[i].TestResult = after[i].TestResult;
                    if (after[i].Passed) { res.Ok++; res.Fixed.Add(items[i]); }
                    else
                    {
                        res.Failed++;
                        res.Problems.Add(items[i].Name + " (Ist: " + (after[i].Result == "" ? "–" : after[i].Result) + ")");
                    }
                }
            }
            File.WriteAllText(Path.Combine(bk.Folder, "liesmich.txt"),
                "Sicherung vor dem Beheben von " + selected.Count + " Punkten (" + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ").\r\n" +
                "Zurückspielen: im Programm „Sicherung zurückspielen …“ und diesen Ordner wählen.\r\n\r\n" +
                "einstellungen.txt     Registry-Werte vorher (exakt, inkl. nicht vorhandener Werte)\r\n" +
                "hk_sicherung_*.csv    übrige Einstellungen vorher (Format von HardeningKitty)\r\n" +
                "auswahl_*.csv         die gesetzten Punkte\r\n" +
                "pruefung_*.csv        Prüfung danach\r\n", Encoding.UTF8);
            Haertungsprofil.Add(res.Fixed);
            Log.Write("---- Beheben beendet: " + res.Ok + " erfolgreich, " + res.Failed + " nicht übernommen ----");
            foreach (string p in res.Problems) Log.Write("  nicht übernommen: " + p);
            return res;
        }

        public static List<string> Restore(KittyModule m, string folder, Action<string> progress)
        {
            var msgs = new List<string>();
            Log.Write("---- Sicherung zurückspielen: " + folder + " ----");

            // Den jetzigen Stand vorher ebenfalls sichern – so lässt sich auch das Zurückspielen rückgängig machen
            progress(Lang.T("Sichere den jetzigen Stand …", "Backing up the current state …"));
            Sicherung now = Sicherung.SnapshotLike(folder, "vor-dem-Zurückspielen");
            progress(Lang.T("Spiele Registry, Dienste und Aufgaben zurück …", "Restoring registry, services and tasks …"));
            msgs.AddRange(Sicherung.Restore(folder));

            foreach (string hk in Directory.GetFiles(folder, "hk_sicherung_*.csv"))
            {
                string name = Path.GetFileNameWithoutExtension(hk).Substring("hk_sicherung_".Length);
                if (m == null) { msgs.Add("✘ " + Lang.T("HardeningKitty fehlt – " + Path.GetFileName(hk) + " wurde nicht zurückgespielt.", "HardeningKitty is missing – " + Path.GetFileName(hk) + " was not restored.")); continue; }
                CsvTable t = CsvTable.Load(hk);
                var rows = t.Rows.Where(r => r["Method"] != "Registry" && r["Method"] != "RegistryList").ToList();
                if (rows.Count == 0) continue;
                string restoreList = Path.Combine(now.Folder, "zurueckspielen_" + name + ".csv");
                CsvTable.WriteRaw(restoreList, t.HeaderRaw, rows.Select(r => r.Raw));
                progress(Lang.T("Sichere den jetzigen Stand (" + rows.Count + " weitere Einstellungen) …", "Backing up the current state (" + rows.Count + " more settings) …"));
                Kitty.Backup(m, restoreList, Path.Combine(now.Folder, "hk_sicherung_" + name + ".csv"), null);
                progress(Lang.T("Spiele " + rows.Count + " Einstellungen über HardeningKitty zurück …", "Restoring " + rows.Count + " settings via HardeningKitty …"));
                string report = Path.Combine(now.Folder, "zurueckgespielt_" + name + ".csv");
                Kitty.HailMary(m, restoreList, report, null);
                int failed = File.Exists(report) ? CsvTable.Load(report).Rows.Count(r => r["TestResult"] == "Failed") : rows.Count;
                msgs.Add((failed == 0 ? "✔ " : "✘ ") + Lang.T((rows.Count - failed) + " von " + rows.Count + " weiteren Einstellungen zurückgesetzt (", (rows.Count - failed) + " of " + rows.Count + " more settings restored (") + name + ").");
            }

            // Zurückgespielte Punkte nicht mehr automatisch erneut setzen
            int removed = 0;
            foreach (string sel in Directory.GetFiles(folder, "auswahl_*.csv").Where(f => !Path.GetFileName(f).StartsWith("auswahl_sonstige_")))
                foreach (CsvRow r in CsvTable.Load(sel).Rows)
                {
                    Haertungsprofil.RemoveEverywhere(r["ID"], r["Name"]);
                    removed++;
                }
            if (removed > 0) msgs.Add(Lang.T("Die " + removed + " zurückgespielten Punkte wurden aus dem Härtungsprofil der Automatik entfernt.", "The " + removed + " restored items were removed from the automation hardening profile."));
            string groupsFile = Path.Combine(folder, "gruppen.txt");
            if (File.Exists(groupsFile))
            {
                var restored = File.ReadAllText(groupsFile).Split(',').Select(s => s.Trim()).ToList();
                var keep = Datenschutz.Selected().Where(g => !restored.Contains(g.Id)).ToList();
                Datenschutz.SaveSelection(keep);
                msgs.Add(Lang.T("Die zurückgespielten Datenschutz-Gruppen wurden aus der Auswahl der Automatik entfernt.", "The restored privacy groups were removed from the automation selection."));
            }
            msgs.Add(Lang.T("Der Stand vor dem Zurückspielen liegt in: ", "The state before restoring is saved in: ") + now.Folder);
            Log.Write("---- Zurückspielen beendet ----");
            return msgs;
        }
    }

    static class Verlauf
    {
        static string FilePath { get { return Path.Combine(Paths.Reports, "Verlauf.csv"); } }

        public static void Add(AuditResult r)
        {
            try
            {
                if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "Zeit;Liste;Score;Prüfungen;Bestanden;Niedrig;Mittel;Hoch;NichtGeprüft\r\n", new UTF8Encoding(true));
                File.AppendAllText(FilePath, string.Join(";", r.Time.ToString("yyyy-MM-dd HH:mm"), r.ListKey, r.Score.ToString("0.00", CultureInfo.GetCultureInfo("de-DE")),
                    r.Total, r.Passed, r.Low, r.Medium, r.High, r.NotChecked) + "\r\n", Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("Verlauf nicht gespeichert: " + ex.Message); }
        }

        static List<string[]> Entries()
        {
            try
            {
                if (File.Exists(FilePath)) return File.ReadAllLines(FilePath, Encoding.UTF8).Skip(1).Select(l => l.Split(';')).Where(p => p.Length >= 3).ToList();
            }
            catch (Exception ex) { Log.Write("Verlauf nicht lesbar: " + ex.Message); }
            return new List<string[]>();
        }

        static DateTime When(string[] p)
        {
            DateTime t;
            return DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : DateTime.MinValue;
        }

        // „vorher 4,54 am 21.03.2026“ für dieselbe Liste
        public static string Previous(AuditResult r)
        {
            string[] prev = Entries().Where(p => p[1] == r.ListKey && When(p) < r.Time.AddMinutes(-1)).LastOrDefault();
            return prev == null ? "" : Lang.T("vorher " + prev[2] + " am ", "previously " + prev[2].Replace(",", ".") + " on ") + When(prev).ToString("d");
        }

        public static DateTime LastAudit()
        {
            return Entries().Select(When).DefaultIfEmpty(DateTime.MinValue).Max();
        }
    }

    static class Automatik
    {
        public const string TaskName = "HardeningKitty UI - Härtung prüfen";

        public static string InstallFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HardeningKitty UI"); }
        }

        static string ExeName { get { return Path.GetFileName(Assembly.GetEntryAssembly().Location); } }

        public static bool IsInstalledCopy
        {
            get
            {
                return string.Equals(Path.GetFullPath(AppHost.ProgramFolder).TrimEnd('\\'), Path.GetFullPath(InstallFolder).TrimEnd('\\'),
                                     StringComparison.OrdinalIgnoreCase);
            }
        }

        // Ordner, in dem die Automatik ihre Daten hat (Logs, Berichte, Status)
        public static string DataFolder { get { return IsInstalledCopy ? AppHost.ProgramFolder : InstallFolder; } }

        public static string StatusFile { get { return Path.Combine(DataFolder, @"Einstellungen\Automatik-Status.txt"); } }

        static string Esc(string s) { return SecurityElement.Escape(s); }

        public static string TaskXml(string exe, bool monthly, bool atBoot)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n");
            sb.Append("<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n");
            sb.Append("  <RegistrationInfo><Author>" + Esc(AppHost.Author) + "</Author><Description>" +
                      Esc("Wendet die gewählten Datenschutz-Einstellungen erneut an, prüft behobene HardeningKitty-Punkte und setzt zurückgesetzte wieder. " +
                          "Monatlich zusätzlich ein Prüfbericht. Eingerichtet mit " + AppHost.Name + ".") + "</Description></RegistrationInfo>\n");
            sb.Append("  <Triggers>\n");
            if (monthly)
                sb.Append("    <CalendarTrigger><StartBoundary>" + DateTime.Today.ToString("yyyy-MM-dd") + "T12:00:00</StartBoundary><Enabled>true</Enabled>" +
                          "<ScheduleByMonthDayOfWeek><Weeks><Week>3</Week></Weeks><DaysOfWeek><Wednesday /></DaysOfWeek><Months>" +
                          "<January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December />" +
                          "</Months></ScheduleByMonthDayOfWeek></CalendarTrigger>\n");
            if (atBoot) sb.Append("    <BootTrigger><Enabled>true</Enabled><Delay>PT10M</Delay></BootTrigger>\n");
            sb.Append("  </Triggers>\n");
            sb.Append("  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n");
            sb.Append("  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                      "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable>" +
                      "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><Enabled>true</Enabled>" +
                      "<ExecutionTimeLimit>PT2H</ExecutionTimeLimit><Priority>7</Priority></Settings>\n");
            sb.Append("  <Actions Context=\"Author\"><Exec><Command>" + Esc(exe) + "</Command><Arguments>/auto</Arguments><WorkingDirectory>" +
                      Esc(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\n");
            sb.Append("</Task>\n");
            return sb.ToString();
        }

        // Kopiert das Programm nach C:\Program Files\HardeningKitty UI (dort können nur Administratoren Dateien ändern –
        // wichtig, weil die Aufgabe als SYSTEM läuft) und richtet die geplante Aufgabe ein
        public static void Setup(bool monthly, bool atBoot, Action<string> progress)
        {
            if (!IsInstalledCopy)
            {
                progress(Lang.T("Kopiere das Programm nach ", "Copying the program to ") + InstallFolder + " …");
                Directory.CreateDirectory(InstallFolder);
                string target = Path.Combine(InstallFolder, ExeName);
                File.Copy(Assembly.GetEntryAssembly().Location, target, true);
                Log.Write("Programm kopiert nach " + target);
                if (Directory.Exists(Paths.Kitty))
                {
                    Kitty.CopyFolder(Paths.Kitty, Path.Combine(InstallFolder, "HardeningKitty"));
                    Log.Write("HardeningKitty kopiert nach " + Path.Combine(InstallFolder, "HardeningKitty"));
                }
                SyncSettings();
            }
            progress(Lang.T("Richte die geplante Aufgabe ein …", "Setting up the scheduled task …"));
            Aufgaben.Register(TaskName, TaskXml(Path.Combine(InstallFolder, ExeName), monthly, atBoot), false);
        }

        // Nach einem Update von HardeningKitty auch die Kopie der Automatik aktualisieren
        public static void SyncKitty()
        {
            if (IsInstalledCopy || !Directory.Exists(InstallFolder) || !Directory.Exists(Paths.Kitty)) return;
            try
            {
                Kitty.CopyFolder(Paths.Kitty, Path.Combine(InstallFolder, "HardeningKitty"));
                Log.Write("HardeningKitty auch in die Automatik übernommen: " + InstallFolder);
            }
            catch (Exception ex) { Log.Write("HardeningKitty NICHT in die Automatik übernommen: " + ex); }
        }

        // Übernimmt Auswahl, Ausnahmen und Härtungsprofil in die Kopie unter Program Files
        public static bool SyncSettings()
        {
            if (IsInstalledCopy || !Directory.Exists(InstallFolder)) return true;
            try
            {
                string dst = Path.Combine(InstallFolder, "Einstellungen");
                string dstProfile = Path.Combine(dst, "Haertungsprofil");
                Directory.CreateDirectory(dstProfile);
                foreach (string f in new[] { "Einstellungen.ini", "Ausnahmen.txt", "Sprache.txt" })
                    if (File.Exists(Path.Combine(Paths.Settings, f))) File.Copy(Path.Combine(Paths.Settings, f), Path.Combine(dst, f), true);
                foreach (string f in Directory.GetFiles(dstProfile, "*.csv"))
                    if (!File.Exists(Path.Combine(Haertungsprofil.Folder, Path.GetFileName(f)))) File.Delete(f);
                foreach (string f in Haertungsprofil.Files()) File.Copy(f, Path.Combine(dstProfile, Path.GetFileName(f)), true);
                Log.Write("Einstellungen in die Automatik übernommen: " + dst);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Einstellungen NICHT in die Automatik übernommen: " + ex);
                return false;
            }
        }
    }

    // Lauf ohne Oberfläche – wird von der geplanten Aufgabe mit /auto gestartet
    static class AutoLauf
    {
        public static int Run()
        {
            var lines = new List<string>();
            int problems = 0;
            Log.Write("==================== Automatischer Lauf ====================");
            Log.Write(AppHost.Name + " " + AppHost.Version + " auf " + Environment.MachineName + " als " + Environment.UserDomainName + "\\" + Environment.UserName);
            try
            {
                SystemInfo.Load();
                if (!SystemInfo.IsAdmin) throw new InvalidOperationException(Lang.T("Keine Administratorrechte – die Aufgabe muss als SYSTEM laufen.", "No administrator rights – the task must run as SYSTEM."));

                // 1. Datenschutz-Einstellungen
                if (Settings.GetBool("Automatik.Datenschutz", true))
                {
                    List<TweakGroup> groups = Datenschutz.Selected();
                    if (groups.Count > 0)
                    {
                        string folder;
                        List<string> msgs = Datenschutz.Apply(groups, out folder);
                        if (msgs[0].StartsWith("✘")) problems++;
                        lines.Add(Lang.T("Datenschutz: ", "Privacy: ") + msgs[0].TrimStart('✔', '✘', ' '));
                    }
                }

                // 2. Härtungsprofil: behobene Punkte prüfen und zurückgesetzte erneut setzen
                KittyModule m = Kitty.Find();
                if (m == null) { lines.Add(Lang.T("HardeningKitty fehlt im Programmordner – Prüfungen übersprungen.", "HardeningKitty is missing in the program folder – checks skipped.")); problems++; }
                if (m != null && Settings.GetBool("Automatik.Profil", true))
                    foreach (string file in Haertungsprofil.Files())
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (name.EndsWith("_user"))
                        {
                            lines.Add(Lang.T("Profil " + name + ": Benutzereinstellungen werden nur im Programm gesetzt (die Aufgabe läuft als SYSTEM).", "Profile " + name + ": user settings are only applied in the program (the task runs as SYSTEM)."));
                            continue;
                        }
                        string folder = Path.Combine(Paths.Reports, Paths.Stamp() + "_Automatik_" + Kitty.Short(name));
                        Directory.CreateDirectory(folder);
                        string report = Path.Combine(folder, "pruefung.csv");
                        File.WriteAllText(Path.Combine(folder, "HardeningKitty-Ausgabe.txt"), Kitty.Audit(m, file, report, false, null), Encoding.UTF8);
                        List<Finding> findings = Reports.Join(file, report, false);   // Profildatei heißt wie die Originalliste → gleiche Schlüssel
                        List<Finding> reset = findings.Where(f => f.Failed && !Ausnahmen.Contains(f)).ToList();
                        if (reset.Count == 0)
                        {
                            lines.Add(Lang.T("Härtungsprofil " + name + ": alle " + findings.Count + " Einstellungen sind noch gesetzt.", "Hardening profile " + name + ": all " + findings.Count + " settings are still applied."));
                            continue;
                        }
                        Log.Write(reset.Count + " Einstellungen wurden zurückgesetzt:");
                        foreach (Finding f in reset) Log.Write("  " + f.Id + " " + f.Name + " (Ist: " + f.Result + ")");
                        FixResult fix = Ablauf.Fix(m, reset, "Automatik", s => Log.Write("  " + s));
                        if (fix.Failed > 0) problems++;
                        lines.Add(Lang.T("Härtungsprofil " + name + ": " + reset.Count + " zurückgesetzte Einstellungen erneut gesetzt, " + fix.Ok + " erfolgreich",
                                         "Hardening profile " + name + ": " + reset.Count + " reverted settings re-applied, " + fix.Ok + " successful") +
                                  (fix.Failed > 0 ? ", " + fix.Failed + Lang.T(" nicht übernommen (Gruppenrichtlinie?)", " not applied (group policy?)") : "") + ".");
                    }

                // 3. Monatlicher Prüfbericht
                if (m != null && Settings.GetBool("Automatik.Bericht", true) && Verlauf.LastAudit() < DateTime.Now.AddDays(-25))
                {
                    List<FindingList> lists = Catalog.Load(m.ListsFolder);
                    string key = Settings.Get("Liste", "");
                    FindingList list = lists.FirstOrDefault(l => l.Key == key) ?? SystemInfo.Recommend(lists);
                    if (list != null)
                    {
                        AuditResult r = Ablauf.Audit(m, list, false, s => { });
                        lines.Add(Lang.T("Prüfbericht ", "Audit report ") + list.Display + ": Score " + AuditResult.FormatScore(r.Score) + ", " + r.Open + Lang.T(" offene Punkte.", " open items."));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("FEHLER im automatischen Lauf: " + ex);
                lines.Add(Lang.T("Fehler: ", "Error: ") + ex.Message);
                problems++;
            }

            string status = (problems == 0 ? "✔ " : "✘ ") + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + "\r\n" + string.Join("\r\n", lines);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Automatik.StatusFile));
                File.WriteAllText(Automatik.StatusFile, status, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("Status nicht gespeichert: " + ex.Message); }
            Log.Write("Automatischer Lauf beendet: " + status.Replace("\r\n", " | "));
            return problems == 0 ? 0 : 1;
        }
    }
}

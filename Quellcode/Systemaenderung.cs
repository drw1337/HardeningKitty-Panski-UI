// Systemaenderung.cs – alles, was das System direkt ändert (ohne PowerShell):
//   RegistryPfad          – Pfade wie „HKLM:\Software\…“ öffnen, Werte kodieren
//   Sicherung             – Registry-Werte, Dienst-Starttypen und Aufgaben sichern und zurückspielen
//   Dienste               – Starttyp über den Dienststeuerungs-Manager (advapi32) ändern
//   Aufgaben              – Aufgabenplanung über die COM-Schnittstelle „Schedule.Service“
//   Wiederherstellungspunkt – Systemwiederherstellung über WMI (root\default:SystemRestore)
//   Datenschutz           – Telemetrie- und Datenschutz-Gruppen (Registry-Richtlinien, Dienste, Aufgaben)

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using MeynTools;

namespace HardeningKittyUI
{
    static class RegistryPfad
    {
        // Akzeptiert „HKLM:\…“, „HKLM\…“ und „HKEY_LOCAL_MACHINE\…“
        static void Split(string path, out RegistryHive hive, out string sub)
        {
            string p = path.Replace('/', '\\').Trim();
            int i = p.IndexOf('\\');
            string h = (i < 0 ? p : p.Substring(0, i)).TrimEnd(':').ToUpperInvariant();
            sub = i < 0 ? "" : p.Substring(i + 1).Trim('\\');
            switch (h)
            {
                case "HKLM": case "HKEY_LOCAL_MACHINE": hive = RegistryHive.LocalMachine; break;
                case "HKCU": case "HKEY_CURRENT_USER": hive = RegistryHive.CurrentUser; break;
                case "HKU": case "HKEY_USERS": hive = RegistryHive.Users; break;
                default: throw new ArgumentException("Unbekannter Registry-Pfad: " + path);
            }
        }

        public static RegistryKey Open(string path, bool write)
        {
            RegistryHive hive; string sub;
            Split(path, out hive, out sub);
            using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                return root.OpenSubKey(sub, write);
        }

        public static RegistryKey Create(string path)
        {
            RegistryHive hive; string sub;
            Split(path, out hive, out sub);
            using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                return root.CreateSubKey(sub);
        }

        public static void DeleteKeyIfEmpty(string path)
        {
            RegistryHive hive; string sub;
            Split(path, out hive, out sub);
            using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (RegistryKey k = root.OpenSubKey(sub))
            {
                if (k == null || k.ValueCount > 0 || k.SubKeyCount > 0) return;
                k.Close();
                root.DeleteSubKey(sub, false);
            }
        }

        static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
        static string UnB64(string s) { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }

        public static string Encode(RegistryValueKind kind, object v)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord: return unchecked((uint)Convert.ToInt32(v)).ToString();
                case RegistryValueKind.QWord: return unchecked((ulong)Convert.ToInt64(v)).ToString();
                case RegistryValueKind.MultiString: return B64(string.Join("\0", (string[])v));
                case RegistryValueKind.Binary: return Convert.ToBase64String((byte[])v);
                default: return B64(Convert.ToString(v));
            }
        }

        public static object Decode(RegistryValueKind kind, string s)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord: return unchecked((int)uint.Parse(s));
                case RegistryValueKind.QWord: return unchecked((long)ulong.Parse(s));
                case RegistryValueKind.MultiString: return s == "" ? new string[0] : UnB64(s).Split('\0');
                case RegistryValueKind.Binary: return Convert.FromBase64String(s);
                default: return UnB64(s);
            }
        }
    }

    // Sicherung im Ordner Programm\Sicherungen\<Zeit>_<Art>\einstellungen.txt (eine Zeile pro Eintrag, Tab-getrennt):
    //   KEY  <Pfad> <vorhanden 1/0>
    //   VAL  <Pfad> <Name> <vorhanden 1/0> <Typ> <Daten>
    //   ALL  <Pfad>                      (Schlüssel enthielt genau die gesicherten Werte – übrige beim Zurückspielen entfernen)
    //   SVC  <Dienst> <Start 0-4, -1 = fehlt> <verzögert 1/0>
    //   TASK <Aufgabe> <1 aktiv, 0 deaktiviert, -1 fehlt>
    class Sicherung
    {
        public const string FileName = "einstellungen.txt";
        public string Folder;
        readonly List<string> lines = new List<string>();
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Sicherung Create(string art)
        {
            string folder = Path.Combine(Paths.Backups, Paths.Stamp() + "_" + art);
            int n = 2;
            while (Directory.Exists(folder)) folder = Path.Combine(Paths.Backups, Paths.Stamp() + "_" + art + "_" + n++);
            Directory.CreateDirectory(folder);
            Log.Write("Sicherung angelegt: " + folder);
            return new Sicherung { Folder = folder };
        }

        public static bool IsBackupFolder(string folder)
        {
            return File.Exists(Path.Combine(folder, FileName)) || Directory.GetFiles(folder, "hk_sicherung_*.csv").Length > 0;
        }

        void Add(string line) { if (seen.Add(line.Split('\t').Take(3).Aggregate((a, b) => a + "\t" + b))) lines.Add(line); }

        // valueName = null: alle Werte des Schlüssels (für Listen-Einstellungen)
        public void AddRegistry(string path, string valueName)
        {
            using (RegistryKey k = RegistryPfad.Open(path, false))
            {
                Add("KEY\t" + path + "\t" + (k != null ? "1" : "0"));
                IEnumerable<string> names = valueName != null ? new[] { valueName } : k != null ? k.GetValueNames() : new string[0];
                if (valueName == null) Add("ALL\t" + path);
                foreach (string n in names)
                {
                    object v = k == null ? null : k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (v == null) Add("VAL\t" + path + "\t" + n + "\t0\t\t");
                    else
                    {
                        RegistryValueKind kind = k.GetValueKind(n);
                        Add("VAL\t" + path + "\t" + n + "\t1\t" + kind + "\t" + RegistryPfad.Encode(kind, v));
                    }
                }
            }
        }

        public void AddService(string name)
        {
            Add("SVC\t" + name + "\t" + Dienste.StartType(name) + "\t" + (Dienste.IsDelayed(name) ? "1" : "0"));
        }

        public void AddTask(string path)
        {
            Add("TASK\t" + path + "\t" + Aufgaben.State(path));
        }

        public void Save()
        {
            var all = new List<string>
            {
                "# Sicherung von " + AppHost.Name + " " + AppHost.Version + " – " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " auf " + Environment.MachineName,
                "# Zurückspielen: im Programm „Sicherung zurückspielen …“ und diesen Ordner wählen."
            };
            all.AddRange(lines);
            File.WriteAllLines(Path.Combine(Folder, FileName), all, Encoding.UTF8);
            Log.Write("  " + lines.Count + " Einträge gesichert");
        }

        static List<string[]> Read(string folder)
        {
            string file = Path.Combine(folder, FileName);
            if (!File.Exists(file)) return new List<string[]>();
            return File.ReadAllLines(file, Encoding.UTF8).Where(l => l != "" && !l.StartsWith("#")).Select(l => l.Split('\t')).ToList();
        }

        // Neue Sicherung mit denselben Einträgen (aktueller Stand) – macht auch das Zurückspielen umkehrbar
        public static Sicherung SnapshotLike(string folder, string art)
        {
            Sicherung s = Create(art);
            foreach (string[] p in Read(folder))
            {
                try
                {
                    switch (p[0])
                    {
                        case "VAL": s.AddRegistry(p[1], p[2]); break;
                        case "ALL": s.AddRegistry(p[1], null); break;
                        case "SVC": s.AddService(p[1]); break;
                        case "TASK": s.AddTask(p[1]); break;
                    }
                }
                catch (Exception ex) { Log.Write("  Stand von " + p[1] + " nicht lesbar: " + ex.Message); }
            }
            s.Save();
            return s;
        }

        public static List<string> Restore(string folder)
        {
            var msgs = new List<string>();
            List<string[]> entries = Read(folder);
            if (entries.Count == 0) return msgs;
            Log.Write("Spiele Sicherung zurück: " + folder);
            int regOk = 0, svcOk = 0, taskOk = 0;
            var errors = new List<string>();

            // 1. Registry-Werte
            foreach (string[] p in entries.Where(e => e[0] == "VAL"))
            {
                try
                {
                    if (p[3] == "1")
                    {
                        var kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), p[4]);
                        using (RegistryKey k = RegistryPfad.Create(p[1])) k.SetValue(p[2], RegistryPfad.Decode(kind, p[5]), kind);
                    }
                    else
                        using (RegistryKey k = RegistryPfad.Open(p[1], true))
                            if (k != null) k.DeleteValue(p[2], false);
                    regOk++;
                }
                catch (Exception ex) { errors.Add("Registry " + p[1] + "\\" + p[2] + ": " + ex.Message); Log.Write("  FEHLER " + p[1] + "\\" + p[2] + ": " + ex); }
            }
            // 2. Bei Listen-Einstellungen nachträglich hinzugekommene Werte entfernen
            foreach (string[] p in entries.Where(e => e[0] == "ALL"))
            {
                var keep = new HashSet<string>(entries.Where(e => e[0] == "VAL" && e[1] == p[1]).Select(e => e[2]), StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (RegistryKey k = RegistryPfad.Open(p[1], true))
                        if (k != null)
                            foreach (string n in k.GetValueNames().Where(n => !keep.Contains(n))) { k.DeleteValue(n, false); regOk++; }
                }
                catch (Exception ex) { errors.Add("Registry " + p[1] + ": " + ex.Message); Log.Write("  FEHLER " + p[1] + ": " + ex); }
            }
            // 3. Vorher nicht vorhandene Schlüssel wieder entfernen, sofern leer
            foreach (string[] p in entries.Where(e => e[0] == "KEY" && e[2] == "0"))
            {
                try { RegistryPfad.DeleteKeyIfEmpty(p[1]); }
                catch (Exception ex) { Log.Write("  Schlüssel " + p[1] + " nicht entfernt: " + ex.Message); }
            }
            // 4. Dienste
            foreach (string[] p in entries.Where(e => e[0] == "SVC" && e[2] != "-1"))
            {
                try
                {
                    int start = int.Parse(p[2]);
                    Dienste.SetStart(p[1], start, p[3] == "1");
                    if (start == 2) Dienste.TryStart(p[1]);
                    svcOk++;
                }
                catch (Exception ex) { errors.Add(Lang.T("Dienst ", "Service ") + p[1] + ": " + ex.Message); Log.Write("  FEHLER Dienst " + p[1] + ": " + ex); }
            }
            // 5. Geplante Aufgaben
            foreach (string[] p in entries.Where(e => e[0] == "TASK" && e[2] != "-1"))
            {
                try { Aufgaben.SetEnabled(p[1], p[2] == "1"); taskOk++; }
                catch (Exception ex) { errors.Add(Lang.T("Aufgabe ", "Task ") + p[1] + ": " + ex.Message); Log.Write("  FEHLER Aufgabe " + p[1] + ": " + ex); }
            }

            Log.Write("  " + regOk + " Registry-Werte, " + svcOk + " Dienste, " + taskOk + " Aufgaben zurückgesetzt, " + errors.Count + " Fehler");
            string summary = Lang.T(regOk + " Registry-Werte, " + svcOk + " Dienste und " + taskOk + " Aufgaben zurückgesetzt",
                                    regOk + " registry values, " + svcOk + " services and " + taskOk + " tasks restored");
            msgs.Add((errors.Count == 0 ? "✔ " : "✘ ") + summary + (errors.Count > 0 ? ", " + errors.Count + Lang.T(" Fehler:", " errors:") : "."));
            msgs.AddRange(errors.Take(5).Select(e => "   " + e));
            return msgs;
        }
    }

    static class Dienste
    {
        const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";
        const uint NoChange = 0xFFFFFFFF;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint errorControl, string binary, string group,
                                               IntPtr tag, string dependencies, string user, string password, string displayName);

        [StructLayout(LayoutKind.Sequential)]
        struct DelayedAutoStartInfo { public int Delayed; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref DelayedAutoStartInfo info);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr handle);

        // Starttyp: 2 automatisch, 3 manuell, 4 deaktiviert; -1 = Dienst gibt es auf diesem Rechner nicht
        public static int StartType(string name)
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ServicesKey + name))
            {
                if (k == null || k.GetValue("Type") == null) return -1;
                object v = k.GetValue("Start");
                return v is int ? (int)v : -1;
            }
        }

        public static bool IsDelayed(string name)
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ServicesKey + name))
                return k != null && k.GetValue("DelayedAutostart") is int && (int)k.GetValue("DelayedAutostart") == 1;
        }

        public static void Disable(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
            }
            catch (Exception ex) { Log.Write("  Dienst " + name + " ließ sich nicht anhalten (startet nach dem nächsten Neustart nicht mehr): " + ex.Message); }
            SetStart(name, 4, false);
        }

        public static void SetStart(string name, int start, bool delayed)
        {
            IntPtr scm = OpenSCManager(null, null, 0x0001);   // SC_MANAGER_CONNECT
            if (scm == IntPtr.Zero) throw new Win32Exception();
            try
            {
                IntPtr svc = OpenService(scm, name, 0x0003);  // SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG
                if (svc == IntPtr.Zero) throw new Win32Exception();
                try
                {
                    if (!ChangeServiceConfig(svc, NoChange, (uint)start, NoChange, null, null, IntPtr.Zero, null, null, null, null))
                        throw new Win32Exception();
                    if (start == 2)
                    {
                        var info = new DelayedAutoStartInfo { Delayed = delayed ? 1 : 0 };
                        if (!ChangeServiceConfig2(svc, 3, ref info)) Log.Write("  Verzögerter Start für " + name + " nicht gesetzt: " + new Win32Exception().Message);
                    }
                    Log.Write("  Dienst " + name + ": Starttyp " + start + (delayed ? " (verzögert)" : ""));
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public static void TryStart(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                    if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            }
            catch (Exception ex) { Log.Write("  Dienst " + name + " nicht gestartet (startet beim nächsten Neustart): " + ex.Message); }
        }
    }

    static class Aufgaben
    {
        static dynamic Service()
        {
            dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            s.Connect();
            return s;
        }

        static dynamic Get(string path)
        {
            int i = path.LastIndexOf('\\');
            string folder = i <= 0 ? "\\" : path.Substring(0, i), name = path.Substring(i + 1);
            try { return Service().GetFolder(folder).GetTask(name); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (COMException ex)
            {
                if ((uint)ex.ErrorCode == 0x80070002 || (uint)ex.ErrorCode == 0x80070003) return null;
                throw;
            }
        }

        // 1 aktiv, 0 deaktiviert, -1 nicht vorhanden
        public static int State(string path)
        {
            dynamic t = Get(path);
            if (t == null) return -1;
            return (bool)t.Enabled ? 1 : 0;
        }

        public static void SetEnabled(string path, bool enabled)
        {
            dynamic t = Get(path);
            if (t == null) return;
            t.Enabled = enabled;
            Log.Write("  Aufgabe " + path + ": " + (enabled ? "aktiviert" : "deaktiviert"));
        }

        // Flags 6 = TASK_CREATE_OR_UPDATE, Anmeldetyp 5 = Dienstkonto; flags 1 = nur prüfen
        public static void Register(string name, string xml, bool validateOnly)
        {
            Service().GetFolder("\\").RegisterTask(name, xml, validateOnly ? 1 : 6, "SYSTEM", null, 5, null);
            if (!validateOnly) Log.Write("Geplante Aufgabe eingerichtet: " + name);
        }

        public static void Delete(string name)
        {
            Service().GetFolder("\\").DeleteTask(name, 0);
            Log.Write("Geplante Aufgabe entfernt: " + name);
        }

        public static void Run(string name)
        {
            dynamic t = Get("\\" + name);
            if (t == null) throw new InvalidOperationException("Die Aufgabe ist nicht eingerichtet.");
            t.Run(null);
            Log.Write("Geplante Aufgabe gestartet: " + name);
        }

        public class Info { public bool Exists, Enabled; public DateTime Next, Last; public int LastResult; public string Command = ""; }

        public static Info Describe(string name)
        {
            var info = new Info();
            dynamic t = Get("\\" + name);
            if (t == null) return info;
            info.Exists = true;
            info.Enabled = (bool)t.Enabled;
            info.Next = (DateTime)t.NextRunTime;
            info.Last = (DateTime)t.LastRunTime;
            info.LastResult = (int)t.LastTaskResult;
            try { info.Command = (string)t.Definition.Actions.Item(1).Path; }
            catch (Exception ex) { Log.Write("Aufgabe: Befehl nicht lesbar: " + ex.Message); }
            return info;
        }
    }

    static class Wiederherstellungspunkt
    {
        static DateTime Newest()
        {
            DateTime newest = DateTime.MinValue;
            try
            {
                using (var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"), new ObjectQuery("SELECT CreationTime FROM SystemRestore")))
                    foreach (ManagementObject o in s.Get())
                    {
                        DateTime t = ManagementDateTimeConverter.ToDateTime((string)o["CreationTime"]);
                        if (t > newest) newest = t;
                    }
            }
            catch (Exception ex) { Log.Write("Wiederherstellungspunkte nicht lesbar: " + ex.Message); }
            return newest;
        }

        // true, wenn ein aktueller Wiederherstellungspunkt vorhanden ist (neu angelegt oder jünger als 24 Stunden)
        public static bool Create(string description, out string message)
        {
            DateTime before = Newest();
            try
            {
                using (var mc = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null))
                {
                    ManagementBaseObject inp = mc.GetMethodParameters("CreateRestorePoint");
                    inp["Description"] = description;
                    inp["RestorePointType"] = 12;   // MODIFY_SETTINGS
                    inp["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE
                    ManagementBaseObject outp = mc.InvokeMethod("CreateRestorePoint", inp, null);
                    uint rc = Convert.ToUInt32(outp["ReturnValue"]);
                    Log.Write("Wiederherstellungspunkt: Rückgabe " + rc);
                    if (rc != 0)
                    {
                        message = Lang.T("Windows konnte keinen Wiederherstellungspunkt anlegen (Code " + rc + "). Meist ist der Computerschutz für Laufwerk C: " +
                                         "ausgeschaltet (Systemsteuerung → System → Computerschutz).",
                                         "Windows could not create a restore point (code " + rc + "). Usually System Protection is turned off for drive C: " +
                                         "(Control Panel → System → System Protection).");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Wiederherstellungspunkt FEHLER: " + ex);
                message = Lang.T("Windows konnte keinen Wiederherstellungspunkt anlegen: ", "Windows could not create a restore point: ") + ex.Message.Trim();
                return false;
            }
            DateTime after = Newest();
            if (after > before)
            {
                message = "✔ " + Lang.T("Wiederherstellungspunkt angelegt (", "Restore point created (") + after.ToString("g") + ").";
                return true;
            }
            bool recent = before > DateTime.Now.AddHours(-24);
            message = Lang.T("Windows hat keinen neuen Wiederherstellungspunkt angelegt – es erlaubt nur einen pro 24 Stunden",
                             "Windows did not create a new restore point – it only allows one every 24 hours") +
                      (before > DateTime.MinValue ? Lang.T(" (letzter: ", " (last: ") + before.ToString("g") + ")" : "") + ".";
            Log.Write(message);
            return recent;
        }
    }

    class Tweak
    {
        public string Kind, Path, Name, Label;
        public int Value;

        public static Tweak Reg(string path, string name, int value, string label) { return new Tweak { Kind = "REG", Path = path, Name = name, Value = value, Label = label }; }
        public static Tweak Svc(string name, string label) { return new Tweak { Kind = "SVC", Name = name, Label = label }; }
        public static Tweak Task(string path, string label) { return new Tweak { Kind = "TASK", Path = path, Label = label }; }

        public string Describe()
        {
            if (Kind == "REG") return Path + "\\" + Name + " = " + Value;
            if (Kind == "SVC") return "Dienst " + Name + " deaktivieren";
            return "Aufgabe " + Path + " deaktivieren";
        }

        // 1 gesetzt, 0 offen, -1 auf diesem Rechner nicht vorhanden
        public int State()
        {
            try
            {
                if (Kind == "REG")
                    using (RegistryKey k = RegistryPfad.Open(Path, false))
                    {
                        object v = k == null ? null : k.GetValue(Name);
                        return v is int && (int)v == Value ? 1 : 0;
                    }
                if (Kind == "SVC")
                {
                    int s = Dienste.StartType(Name);
                    return s < 0 ? -1 : s == 4 ? 1 : 0;
                }
                int t = Aufgaben.State(Path);
                return t < 0 ? -1 : t == 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                Log.Write("Status " + Describe() + ": " + ex.Message);
                return 0;
            }
        }

        public void Apply()
        {
            if (Kind == "REG")
                using (RegistryKey k = RegistryPfad.Create(Path)) k.SetValue(Name, Value, RegistryValueKind.DWord);
            else if (Kind == "SVC") Dienste.Disable(Name);
            else Aufgaben.SetEnabled(Path, false);
            Log.Write("  gesetzt: " + Describe());
        }

        public void BackupTo(Sicherung s)
        {
            if (Kind == "REG") s.AddRegistry(Path, Name);
            else if (Kind == "SVC") s.AddService(Name);
            else s.AddTask(Path);
        }
    }

    class TweakGroup
    {
        public string Id, Title, Description;
        public Stufe Level;
        public Tweak[] Items;
    }

    static class Datenschutz
    {
        const string Pol = @"HKLM\SOFTWARE\Policies\Microsoft\";
        const string PolWin = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\";
        const string CvPol = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\";

        static string T(string de, string en) { return Lang.T(de, en); }

        static TweakGroup[] groups;
        static bool groupsEnglish;

        // Je Sprache einmal aufgebaut (Titel, Beschreibungen und Bezeichnungen sind zweisprachig)
        public static TweakGroup[] Groups
        {
            get
            {
                if (groups == null || groupsEnglish != Lang.IsEnglish) { groups = Build(); groupsEnglish = Lang.IsEnglish; }
                return groups;
            }
        }

        static TweakGroup[] Build()
        {
            return new[]
            {
                new TweakGroup
                {
                    Id = "telemetrie", Level = Stufe.Unkritisch, Title = T("Telemetrie und Diagnosedaten minimieren", "Minimize telemetry and diagnostic data"),
                    Description = T("Diagnosedaten auf das Minimum, keine Feedback-Fragen, Programm zur Verbesserung der Benutzerfreundlichkeit (CEIP) und " +
                                    "Kompatibilitäts-Telemetrie aus. Hinweis: Windows Pro/Home senden mindestens die „erforderlichen“ Diagnosedaten.",
                                    "Diagnostic data set to minimum, no feedback prompts, Customer Experience Improvement Program (CEIP) and compatibility " +
                                    "telemetry turned off. Note: Windows Pro/Home always send at least the “required” diagnostic data."),
                    Items = new[]
                    {
                        Tweak.Reg(PolWin + "DataCollection", "AllowTelemetry", 0, T("Diagnosedaten: Minimum", "Diagnostic data: minimum")),
                        Tweak.Reg(PolWin + "DataCollection", "DoNotShowFeedbackNotifications", 1, T("Keine Feedback-Benachrichtigungen", "No feedback notifications")),
                        Tweak.Reg(PolWin + "DataCollection", "LimitDiagnosticLogCollection", 1, T("Diagnoseprotokolle begrenzen", "Limit diagnostic log collection")),
                        Tweak.Reg(PolWin + "DataCollection", "LimitDumpCollection", 1, T("Speicherabbilder begrenzen", "Limit dump collection")),
                        Tweak.Reg(PolWin + "DataCollection", "DisableOneSettingsDownloads", 1, T("Keine OneSettings-Downloads", "No OneSettings downloads")),
                        Tweak.Reg(PolWin + "DataCollection", "AllowDeviceNameInTelemetry", 0, T("Gerätename nicht übermitteln", "Do not send device name")),
                        Tweak.Reg(Pol + @"SQMClient\Windows", "CEIPEnable", 0, T("CEIP aus", "CEIP off")),
                        Tweak.Reg(Pol + @"Messenger\Client", "CEIP", 2, T("Messenger-CEIP aus", "Messenger CEIP off")),
                        Tweak.Reg(PolWin + "AppCompat", "AITEnable", 0, T("Anwendungs-Telemetrie aus", "Application telemetry off")),
                        Tweak.Reg(PolWin + "AppCompat", "DisableInventory", 1, T("Inventarsammlung aus", "Inventory collector off")),
                        Tweak.Task(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", T("Aufgabe Compatibility Appraiser", "Task Compatibility Appraiser")),
                        Tweak.Task(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater", T("Aufgabe ProgramDataUpdater", "Task ProgramDataUpdater")),
                        Tweak.Task(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", T("Aufgabe CEIP Consolidator", "Task CEIP Consolidator")),
                        Tweak.Task(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", T("Aufgabe USB-CEIP", "Task USB CEIP")),
                        Tweak.Task(@"\Microsoft\Windows\Feedback\Siuf\DmClient", T("Aufgabe Feedback DmClient", "Task Feedback DmClient")),
                        Tweak.Task(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload", T("Aufgabe Feedback DmClientOnScenarioDownload", "Task Feedback DmClientOnScenarioDownload")),
                        Tweak.Task(@"\Microsoft\Windows\Autochk\Proxy", T("Aufgabe Autochk-Proxy", "Task Autochk proxy")),
                        Tweak.Task(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", T("Aufgabe Datenträgerdiagnose-Sammler", "Task disk diagnostic data collector"))
                    }
                },
                new TweakGroup
                {
                    Id = "fehlerberichte", Level = Stufe.Unkritisch, Title = T("Windows-Fehlerberichterstattung abschalten", "Turn off Windows Error Reporting"),
                    Description = T("Absturzberichte werden nicht mehr an Microsoft gesendet; die zugehörigen Dienste werden deaktiviert.",
                                    "Crash reports are no longer sent to Microsoft; the related services are disabled."),
                    Items = new[]
                    {
                        Tweak.Reg(PolWin + "Windows Error Reporting", "Disabled", 1, T("Fehlerberichterstattung aus", "Error reporting off")),
                        Tweak.Reg(PolWin + "Windows Error Reporting", "DontSendAdditionalData", 1, T("Keine Zusatzdaten senden", "Do not send additional data")),
                        Tweak.Svc("WerSvc", T("Dienst Windows-Fehlerberichterstattung", "Service Windows Error Reporting")),
                        Tweak.Svc("wercplsupport", T("Dienst Problemberichte (Systemsteuerung)", "Service Problem Reports (Control Panel)"))
                    }
                },
                new TweakGroup
                {
                    Id = "werbung", Level = Stufe.Unkritisch, Title = T("Werbung, Tipps und vorgeschlagene Apps", "Ads, tips and suggested apps"),
                    Description = T("Werbe-ID, Consumer-Funktionen (automatisch installierte Apps), Tipps, Online-Schriftarten, Store-Vorschläge beim Öffnen " +
                                    "unbekannter Dateien und Widgets/Neuigkeiten aus.",
                                    "Advertising ID, consumer features (automatically installed apps), tips, online fonts, Store suggestions for unknown file " +
                                    "types and widgets/news turned off."),
                    Items = new[]
                    {
                        Tweak.Reg(PolWin + "AdvertisingInfo", "DisabledByGroupPolicy", 1, T("Werbe-ID aus", "Advertising ID off")),
                        Tweak.Reg(PolWin + "CloudContent", "DisableWindowsConsumerFeatures", 1, T("Keine automatisch installierten Apps", "No automatically installed apps")),
                        Tweak.Reg(PolWin + "CloudContent", "DisableConsumerAccountStateContent", 1, T("Keine Konto-Hinweise", "No account notices")),
                        Tweak.Reg(PolWin + "CloudContent", "DisableSoftLanding", 1, T("Keine Windows-Tipps", "No Windows tips")),
                        Tweak.Reg(PolWin + "CloudContent", "DisableCloudOptimizedContent", 1, T("Keine Cloud-Inhalte", "No cloud-optimized content")),
                        Tweak.Reg(CvPol + "Explorer", "AllowOnlineTips", 0, T("Keine Online-Tipps", "No online tips")),
                        Tweak.Reg(Pol + "WindowsInkWorkspace", "AllowSuggestedAppsInWindowsInkWorkspace", 0, T("Ink-Arbeitsbereich ohne App-Vorschläge", "Ink Workspace without app suggestions")),
                        Tweak.Reg(PolWin + "System", "EnableFontProviders", 0, T("Keine Online-Schriftarten", "No online fonts")),
                        Tweak.Reg(PolWin + "Explorer", "NoUseStoreOpenWith", 1, T("Keine Store-Suche bei unbekannten Dateien", "No Store lookup for unknown files")),
                        Tweak.Reg(Pol + "Dsh", "AllowNewsAndInterests", 0, T("Widgets/Neuigkeiten aus", "Widgets/news off"))
                    }
                },
                new TweakGroup
                {
                    Id = "suche", Level = Stufe.Unkritisch, Title = T("Websuche und Cortana in der Windows-Suche", "Web search and Cortana in Windows Search"),
                    Description = T("Die Windows-Suche durchsucht nur noch den Rechner: keine Bing-Ergebnisse, keine Cloud-Suche, kein Cortana, kein Standort für die Suche.",
                                    "Windows Search only searches this computer: no Bing results, no cloud search, no Cortana, no location for search."),
                    Items = new[]
                    {
                        Tweak.Reg(PolWin + "Windows Search", "AllowCortana", 0, T("Cortana aus", "Cortana off")),
                        Tweak.Reg(PolWin + "Windows Search", "DisableWebSearch", 1, T("Keine Websuche", "No web search")),
                        Tweak.Reg(PolWin + "Windows Search", "ConnectedSearchUseWeb", 0, T("Keine Webergebnisse", "No web results")),
                        Tweak.Reg(PolWin + "Windows Search", "AllowCloudSearch", 0, T("Keine Cloud-Suche", "No cloud search")),
                        Tweak.Reg(PolWin + "Windows Search", "AllowSearchToUseLocation", 0, T("Suche ohne Standort", "Search without location"))
                    }
                },
                new TweakGroup
                {
                    Id = "aktivitaet", Level = Stufe.Unkritisch, Title = T("Aktivitätsverlauf und geräteübergreifende Zwischenablage", "Activity history and cross-device clipboard"),
                    Description = T("Windows speichert keinen Aktivitätsverlauf mehr und lädt ihn nicht hoch; die Zwischenablage wird nicht zwischen Geräten abgeglichen.",
                                    "Windows no longer stores or uploads activity history; the clipboard is not synced between devices."),
                    Items = new[]
                    {
                        Tweak.Reg(PolWin + "System", "EnableActivityFeed", 0, T("Aktivitätsverlauf aus", "Activity history off")),
                        Tweak.Reg(PolWin + "System", "PublishUserActivities", 0, T("Aktivitäten nicht veröffentlichen", "Do not publish activities")),
                        Tweak.Reg(PolWin + "System", "UploadUserActivities", 0, T("Aktivitäten nicht hochladen", "Do not upload activities")),
                        Tweak.Reg(PolWin + "System", "AllowCrossDeviceClipboard", 0, T("Keine geräteübergreifende Zwischenablage", "No cross-device clipboard"))
                    }
                },
                new TweakGroup
                {
                    Id = "ki", Level = Stufe.Unkritisch, Title = T("KI-Funktionen: Recall, Click to Do und Copilot", "AI features: Recall, Click to Do and Copilot"),
                    Description = T("Recall (Bildschirmverlauf) und Click to Do werden gesperrt, Copilot per Richtlinie ausgeschaltet. " +
                                    "Je nach Windows-Version wirkt die Copilot-Richtlinie nur teilweise; die Copilot-App lässt sich zusätzlich deinstallieren.",
                                    "Recall (screen history) and Click to Do are blocked, Copilot is turned off by policy. Depending on the Windows version " +
                                    "the Copilot policy only works partially; the Copilot app can additionally be uninstalled."),
                    Items = new[]
                    {
                        Tweak.Reg(PolWin + "WindowsAI", "DisableAIDataAnalysis", 1, T("Recall-Schnappschüsse aus", "Recall snapshots off")),
                        Tweak.Reg(PolWin + "WindowsAI", "AllowRecallEnablement", 0, T("Recall nicht aktivierbar", "Recall cannot be enabled")),
                        Tweak.Reg(PolWin + "WindowsAI", "DisableClickToDo", 1, T("Click to Do aus", "Click to Do off")),
                        Tweak.Reg(PolWin + "WindowsCopilot", "TurnOffWindowsCopilot", 1, T("Copilot aus", "Copilot off"))
                    }
                },
                new TweakGroup
                {
                    Id = "eingabe", Level = Stufe.Unkritisch, Title = T("Eingabe, Sprache und Freihand", "Input, speech and inking"),
                    Description = T("Keine Online-Spracherkennung und keine Übermittlung von Eingabe- und Freihanddaten zur „Personalisierung“.",
                                    "No online speech recognition and no transmission of typing and inking data for “personalization”."),
                    Items = new[]
                    {
                        Tweak.Reg(Pol + "InputPersonalization", "AllowInputPersonalization", 0, T("Eingabe-Personalisierung aus", "Input personalization off")),
                        Tweak.Reg(PolWin + "TextInput", "AllowLinguisticDataCollection", 0, T("Keine Sprachdaten-Sammlung", "No linguistic data collection")),
                        Tweak.Reg(Pol + "Speech", "AllowSpeechModelUpdate", 0, T("Keine Sprachmodell-Updates", "No speech model updates"))
                    }
                },
                new TweakGroup
                {
                    Id = "internet", Level = Stufe.Unkritisch, Title = T("Alte Internetfunktionen von Windows und Online-Diagnose", "Legacy Windows internet features and online diagnostics"),
                    Description = T("Fotodruck-/Veröffentlichungs-Assistent, HTTP-Druck, Suchassistent-Updates, Registrierungs- und Verbindungsassistent sowie " +
                                    "die Online-Abfrage der Microsoft-Diagnosetools aus.",
                                    "Photo printing/publishing wizard, HTTP printing, Search Companion updates, registration and connection wizard as well as " +
                                    "online queries of the Microsoft diagnostic tools turned off."),
                    Items = new[]
                    {
                        Tweak.Reg(CvPol + "Explorer", "NoOnlinePrintsWizard", 1, T("Kein Online-Fotodruck", "No online photo printing")),
                        Tweak.Reg(CvPol + "Explorer", "NoPublishingWizard", 1, T("Kein Veröffentlichungs-Assistent", "No publishing wizard")),
                        Tweak.Reg(Pol + @"Windows NT\Printers", "DisableHTTPPrinting", 1, T("Kein Drucken über HTTP", "No printing over HTTP")),
                        Tweak.Reg(Pol + "SearchCompanion", "DisableContentFileUpdates", 1, T("Keine Suchassistent-Updates", "No Search Companion updates")),
                        Tweak.Reg(PolWin + "Registration Wizard Control", "NoRegistration", 1, T("Kein Registrierungs-Assistent", "No registration wizard")),
                        Tweak.Reg(PolWin + "Internet Connection Wizard", "ExitOnMSICW", 1, T("Kein Internetverbindungs-Assistent", "No Internet Connection Wizard")),
                        Tweak.Reg(PolWin + @"ScriptedDiagnosticsProvider\Policy", "DisableQueryRemoteServer", 0, T("Keine Online-Diagnose (MSDT)", "No online diagnostics (MSDT)")),
                        Tweak.Reg(PolWin + @"WDI\{9c5a40da-b965-4fc3-8781-88dd50a6299d}", "ScenarioExecutionEnabled", 0, T("Leistungsdiagnose (PerfTrack) aus", "Performance diagnostics (PerfTrack) off"))
                    }
                },
                new TweakGroup
                {
                    Id = "netzwerk", Level = Stufe.Unkritisch, Title = T("Selten genutzte Netzwerkfunktionen", "Rarely used network features"),
                    Description = T("Peer-to-Peer-Netzwerk, Windows Connect Now (WPS-Einrichtung), Netzwerkübersicht (LLTD), iSCSI, Ereignissammlung " +
                                    "und Kartenmanager aus. Für normale Arbeitsplätze ohne Nebenwirkung.",
                                    "Peer-to-peer networking, Windows Connect Now (WPS setup), network map (LLTD), iSCSI, event collection and maps " +
                                    "manager turned off. No side effects on typical workstations."),
                    Items = new[]
                    {
                        Tweak.Reg(Pol + "Peernet", "Disabled", 1, T("Peer-to-Peer-Netzwerk aus", "Peer-to-peer networking off")),
                        Tweak.Reg(PolWin + @"WCN\Registrars", "EnableRegistrars", 0, T("Windows Connect Now aus", "Windows Connect Now off")),
                        Tweak.Reg(PolWin + @"WCN\Registrars", "DisableUPnPRegistrar", 0, "WCN: UPnP"),
                        Tweak.Reg(PolWin + @"WCN\Registrars", "DisableInBand802DOT11Registrar", 0, T("WCN: WLAN", "WCN: Wi-Fi")),
                        Tweak.Reg(PolWin + @"WCN\Registrars", "DisableFlashConfigRegistrar", 0, T("WCN: USB-Stick", "WCN: USB drive")),
                        Tweak.Reg(PolWin + @"WCN\Registrars", "DisableWPDRegistrar", 0, "WCN: WPD"),
                        Tweak.Reg(PolWin + @"WCN\UI", "DisableWcnUi", 1, T("WCN-Assistent aus", "WCN wizard off")),
                        Tweak.Svc("lltdsvc", T("Dienst Netzwerkübersicht (LLTD)", "Service Link-Layer Topology Discovery")),
                        Tweak.Svc("p2pimsvc", T("Dienst Peer Networking Identity Manager", "Service Peer Networking Identity Manager")),
                        Tweak.Svc("p2psvc", T("Dienst Peer Networking Grouping", "Service Peer Networking Grouping")),
                        Tweak.Svc("PNRPAutoReg", T("Dienst PNRP-Namensveröffentlichung", "Service PNRP Machine Name Publication")),
                        Tweak.Svc("PNRPsvc", T("Dienst PNRP", "Service PNRP")),
                        Tweak.Svc("MSiSCSI", T("Dienst iSCSI-Initiator", "Service iSCSI Initiator")),
                        Tweak.Svc("Wecsvc", T("Dienst Windows-Ereignissammlung", "Service Windows Event Collector")),
                        Tweak.Svc("MapsBroker", T("Dienst Kartenmanager", "Service Downloaded Maps Manager"))
                    }
                },
                new TweakGroup
                {
                    Id = "zeit", Level = Stufe.Unkritisch, Title = T("Zeitsynchronisierung per Richtlinie einschalten", "Enable time synchronization by policy"),
                    Description = T("Der Windows-NTP-Client wird per Richtlinie aktiviert (Empfehlung aus CIS).", "The Windows NTP client is enabled by policy (CIS recommendation)."),
                    Items = new[] { Tweak.Reg(Pol + @"W32Time\TimeProviders\NtpClient", "Enabled", 1, T("NTP-Client an", "NTP client on")) }
                },
                new TweakGroup
                {
                    Id = "lieferung", Level = Stufe.Unkritisch, Title = T("Updates nicht mit anderen Rechnern teilen", "Do not share updates with other computers"),
                    Description = T("Die Übermittlungsoptimierung lädt Updates nur noch von Microsoft und teilt sie nicht mit anderen Rechnern (weder im Internet noch im LAN).",
                                    "Delivery Optimization only downloads updates from Microsoft and does not share them with other computers (neither on the internet nor on the LAN)."),
                    Items = new[] { Tweak.Reg(PolWin + "DeliveryOptimization", "DODownloadMode", 0, T("Übermittlungsoptimierung: nur HTTP", "Delivery Optimization: HTTP only")) }
                },
                new TweakGroup
                {
                    Id = "xbox", Level = Stufe.Unkritisch, Title = T("Xbox-Dienste und Spielaufzeichnung", "Xbox services and game recording"),
                    Description = T("Xbox-Dienste, Spielstand-Synchronisierung und Game DVR aus. Nicht auf Rechnern, auf denen gespielt wird.",
                                    "Xbox services, game save sync and Game DVR turned off. Not for computers used for gaming."),
                    Items = new[]
                    {
                        Tweak.Svc("XblAuthManager", T("Dienst Xbox Live-Authentifizierung", "Service Xbox Live Auth Manager")),
                        Tweak.Svc("XblGameSave", T("Dienst Xbox Live-Spielspeicherung", "Service Xbox Live Game Save")),
                        Tweak.Svc("XboxGipSvc", T("Dienst Xbox-Zubehör", "Service Xbox Accessory Management")),
                        Tweak.Svc("XboxNetApiSvc", T("Dienst Xbox Live-Netzwerk", "Service Xbox Live Networking")),
                        Tweak.Task(@"\Microsoft\XblGameSave\XblGameSaveTask", T("Aufgabe Xbox-Spielspeicherung", "Task Xbox game save")),
                        Tweak.Reg(PolWin + "GameDVR", "AllowGameDVR", 0, T("Spielaufzeichnung aus", "Game recording off"))
                    }
                },
                new TweakGroup
                {
                    Id = "diagtrack", Level = Stufe.Rueckfrage, Title = T("Telemetrie-Dienst „DiagTrack“ abschalten", "Turn off the “DiagTrack” telemetry service"),
                    Description = T("Deaktiviert den Dienst „Benutzererfahrung und Telemetrie im verbundenen Modus“. Nicht bei Geräten, die über Intune, " +
                                    "Windows Autopatch oder Update-Berichte verwaltet werden – dort werden die Diagnosedaten gebraucht.",
                                    "Disables the “Connected User Experiences and Telemetry” service. Not for devices managed via Intune, Windows Autopatch " +
                                    "or update reports – they need the diagnostic data."),
                    Items = new[] { Tweak.Svc("DiagTrack", T("Dienst DiagTrack", "Service DiagTrack")) }
                },
                new TweakGroup
                {
                    Id = "standort", Level = Stufe.Rueckfrage, Title = T("Standortdienst abschalten", "Turn off location service"),
                    Description = T("Apps erhalten keinen Standort mehr. Die automatische Zeitzone und „Mein Gerät suchen“ funktionieren dann nicht.",
                                    "Apps no longer get location. Automatic time zone and “Find my device” will no longer work."),
                    Items = new[]
                    {
                        Tweak.Svc("lfsvc", T("Dienst Geolocation", "Service Geolocation")),
                        Tweak.Reg(PolWin + "LocationAndSensors", "DisableLocation", 1, T("Standort per Richtlinie aus", "Location off by policy"))
                    }
                },
                new TweakGroup
                {
                    Id = "bluetooth", Level = Stufe.Rueckfrage, Title = T("Bluetooth abschalten", "Turn off Bluetooth"),
                    Description = T("Nur für Rechner ohne Bluetooth-Geräte – Bluetooth-Maus, -Tastatur und -Headset funktionieren danach nicht mehr.",
                                    "Only for computers without Bluetooth devices – Bluetooth mouse, keyboard and headset will stop working."),
                    Items = new[]
                    {
                        Tweak.Svc("BTAGService", T("Dienst Bluetooth-Audiogateway", "Service Bluetooth Audio Gateway")),
                        Tweak.Svc("bthserv", T("Dienst Bluetooth-Unterstützung", "Service Bluetooth Support"))
                    }
                },
                new TweakGroup
                {
                    Id = "push", Level = Stufe.Rueckfrage, Title = T("Push-Benachrichtigungen über die Microsoft-Cloud", "Push notifications via the Microsoft cloud"),
                    Description = T("Benachrichtigungen vieler Apps (z. B. Kalender, Mail, Teams) können danach ausbleiben.",
                                    "Notifications of many apps (e.g. Calendar, Mail, Teams) may stop appearing."),
                    Items = new[]
                    {
                        Tweak.Svc("WpnService", T("Dienst Windows-Pushbenachrichtigungen", "Service Windows Push Notifications")),
                        Tweak.Reg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\PushNotifications", "NoCloudApplicationNotification", 1, T("Keine Cloud-Benachrichtigungen", "No cloud notifications"))
                    }
                },
                new TweakGroup
                {
                    Id = "store", Level = Stufe.Rueckfrage, Title = T("Microsoft Store sperren", "Block Microsoft Store"),
                    Description = T("Store-Apps (Rechner, Fotos, App Installer/winget …) erhalten danach keine Updates über den Store mehr. " +
                                    "„Store entfernen“ wirkt nur unter Enterprise/Education.",
                                    "Store apps (Calculator, Photos, App Installer/winget …) no longer receive updates through the Store. " +
                                    "“Remove Store” only works on Enterprise/Education."),
                    Items = new[]
                    {
                        Tweak.Reg(Pol + "WindowsStore", "RemoveWindowsStore", 1, T("Store entfernen", "Remove Store")),
                        Tweak.Reg(Pol + "WindowsStore", "RequirePrivateStoreOnly", 1, T("Nur privater Store", "Private Store only")),
                        Tweak.Reg(Pol + "PushToInstall", "DisablePushToInstall", 1, T("Keine Installation per Push", "No push-to-install")),
                        Tweak.Svc("InstallService", T("Dienst Microsoft Store-Installation", "Service Microsoft Store Install")),
                        Tweak.Svc("PushToInstall", T("Dienst PushToInstall", "Service PushToInstall"))
                    }
                },
                new TweakGroup
                {
                    Id = "kamera", Level = Stufe.Rueckfrage, Title = T("Kamera sperren", "Block camera"),
                    Description = T("Sperrt die Kamera für alle Programme (Videokonferenzen und Windows Hello Gesichtserkennung gehen dann nicht).",
                                    "Blocks the camera for all programs (video calls and Windows Hello face recognition will not work)."),
                    Items = new[] { Tweak.Reg(Pol + "Camera", "AllowCamera", 0, T("Kamera aus", "Camera off")) }
                }
            };
        }

        // Auswahl (für Programm und Automatik); ohne gespeicherte Auswahl alle unkritischen Gruppen
        public static List<TweakGroup> Selected()
        {
            string saved = Settings.Get("Datenschutz.Auswahl", null);
            if (saved == null) return Groups.Where(g => g.Level == Stufe.Unkritisch).ToList();
            var ids = saved.Split(',').Select(s => s.Trim()).ToList();
            return Groups.Where(g => ids.Contains(g.Id)).ToList();
        }

        public static void SaveSelection(IEnumerable<TweakGroup> selection)
        {
            Settings.Set("Datenschutz.Auswahl", string.Join(",", selection.Select(g => g.Id)));
        }

        // Wendet die offenen Einstellungen an (vorher gesichert) und prüft danach jede einzeln
        public static List<string> Apply(List<TweakGroup> selection, out string backupFolder)
        {
            backupFolder = null;
            var msgs = new List<string>();
            var todo = selection.SelectMany(g => g.Items).Where(t => t.State() == 0).ToList();
            Log.Write("Datenschutz: " + selection.Count + " Gruppen, " + todo.Count + " Einstellungen offen");
            if (todo.Count == 0)
            {
                msgs.Add("✔ " + T("Alle ausgewählten Einstellungen sind bereits gesetzt.", "All selected settings are already applied."));
                return msgs;
            }

            Sicherung bk = Sicherung.Create("Datenschutz");
            foreach (Tweak t in todo) t.BackupTo(bk);
            bk.Save();
            File.WriteAllText(Path.Combine(bk.Folder, "gruppen.txt"), string.Join(",", selection.Select(g => g.Id)), Encoding.UTF8);
            backupFolder = bk.Folder;

            var errors = new Dictionary<Tweak, string>();
            foreach (Tweak t in todo)
            {
                try { t.Apply(); }
                catch (Exception ex)
                {
                    errors[t] = ex.Message.Trim();
                    Log.Write("  FEHLER " + t.Describe() + ": " + ex);
                }
            }
            var failed = todo.Where(t => t.State() == 0).ToList();
            foreach (Tweak t in failed) Log.Write("  Prüfung fehlgeschlagen: " + t.Describe());
            if (failed.Count == 0) msgs.Add("✔ " + T(todo.Count + " Einstellungen gesetzt und geprüft.", todo.Count + " settings applied and verified."));
            else
            {
                msgs.Add("✘ " + T(failed.Count + " von " + todo.Count + " Einstellungen wurden nicht übernommen:", failed.Count + " of " + todo.Count + " settings were not applied:"));
                foreach (Tweak t in failed.Take(6))
                    msgs.Add("   " + t.Label + " – " + (errors.ContainsKey(t) ? errors[t] : T("eventuell durch eine Gruppenrichtlinie überschrieben", "possibly overridden by a group policy")));
            }
            return msgs;
        }
    }
}

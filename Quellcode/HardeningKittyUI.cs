// HardeningKitty Panski UI – gewidmet meinem Kater Panski († 29.07.2026)
// Prüft die Windows-Härtung mit HardeningKitty (scipag), stuft die offenen Punkte ein und behebt sie gezielt –
// mit Sicherung vorher, Prüfung danach und einer Automatik, die Rücksetzungen durch Windows-Updates erkennt.
//
// Reiter:
//   Prüfen       – HardeningKitty aktuell halten (GitHub), Prüfliste wählen, Prüfung mit Score und Ergebnisliste
//   Beheben      – offene Punkte nach Einstufung (unkritisch / mit Rückfrage / manuell), Ausnahmen, Sicherung zurückspielen
//   Datenschutz  – Telemetrie, Werbung, KI-Funktionen usw.
//   Automatik    – geplante Aufgabe als SYSTEM: monatlich und optional nach jedem Neustart
//
// Breite, größenveränderbare Oberfläche: jede Seite wird in Entwurfsgröße angelegt (Make.Page) und wächst über Anchor mit.
// Aufruf mit /auto: Lauf ohne Oberfläche (für die geplante Aufgabe)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using MeynTools;

[assembly: AssemblyTitle("HardeningKitty Panski UI")]
[assembly: AssemblyDescription("Windows-Härtung mit HardeningKitty prüfen, offene Punkte gezielt beheben (mit Sicherung), Telemetrie und Datenschutz einstellen und monatlich automatisch nachprüfen.")]
[assembly: AssemblyCompany("Maximilian Meyn")]
[assembly: AssemblyProduct("HardeningKitty Panski UI")]
[assembly: AssemblyCopyright("© 2026 Maximilian Meyn")]
[assembly: AssemblyVersion("3.2.0.0")]
[assembly: AssemblyFileVersion("3.2.0.0")]

namespace HardeningKittyUI
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Farben des Tuxedo-Katers Panski: Anthrazit, Grau, Weiß – dazu Cognac als warme Akzentfarbe
            Theme.Accent = Color.FromArgb(143, 90, 60);
            Theme.Header = Color.FromArgb(38, 38, 38);
            Theme.HeaderSubtitle = Color.FromArgb(196, 192, 186);
            AppHost.DescriptionEnglish = "Check Windows hardening with HardeningKitty, fix open items selectively (with backup), configure telemetry and privacy " +
                                         "and re-check automatically every month.";
            if (args.Any(a => a.Equals("/auto", StringComparison.OrdinalIgnoreCase)))
            {
                Log.Init(AppHost.Name);
                Lang.Load();
                return AutoLauf.Run();
            }
            // Wappen (Quellcode\Bilder, erzeugt mit Bilder\erzeugen.cmd) in Kopfzeile, Info-Dialog und als Widmung in der Fußzeile
            AppHost.HeaderImages = Images("kopf", 44, 66, 88);
            AppHost.AboutImages = Images("info", 48, 72, 96);
            AppHost.DedicationImages = Images("widmung", 18, 28, 36, 56);
            AppHost.DedicationGerman = "Gewidmet Panski †";
            AppHost.DedicationEnglish = "Dedicated to Panski †";
            AppHost.Run(() => new MainForm());
            return 0;
        }

        static Image[] Images(string name, params int[] sizes)
        {
            Image[] images = sizes.Select(s => AppHost.LoadImage(name + "-" + s + ".png")).Where(i => i != null).ToArray();
            return images.Length > 0 ? images : null;
        }
    }

    class MainForm : AppForm
    {
        const string KittyRepo = "https://github.com/scipag/HardeningKitty";
        static readonly Size PageSize = new Size(1028, 596);
        const int X = 12, W = 1004;
        const int ListW = 640, CardX = X + ListW + 12, CardW = W - ListW - 12;   // Liste links, Detailkarte rechts
        const AnchorStyles TL = AnchorStyles.Top | AnchorStyles.Left, TLR = TL | AnchorStyles.Right, TR = AnchorStyles.Top | AnchorStyles.Right;
        const AnchorStyles BL = AnchorStyles.Bottom | AnchorStyles.Left, BR = AnchorStyles.Bottom | AnchorStyles.Right, BLR = BL | AnchorStyles.Right;
        const AnchorStyles All = TLR | AnchorStyles.Bottom, TRB = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;

        KittyModule kitty;
        List<FindingList> lists = new List<FindingList>();
        FindingList recommended;
        AuditResult last;
        string latestTag;
        bool filling;

        // Prüfen
        StatusText lblKitty, lblListHint;
        Label lblScore, lblScoreCounts, lblScoreSub;
        ImageList auditImages;
        ComboBox cboList;
        CheckBox chkUser, chkOnlyOpen;
        ListView lvAudit;
        DetailCard cardAudit;
        Button btnUpdate, btnAudit;
        // Beheben
        ListView lvFix;
        ListViewGroup gSafe, gAsk, gManual, gException;
        Label lblFixCount;
        DetailCard cardFix;
        StatusText txtFixState;
        CheckBox chkRestorePoint;
        Button btnFix, btnRestore;
        // Datenschutz
        ListView lvPrivacy;
        ListViewGroup gPrivSafe, gPrivAsk;
        DetailCard cardPrivacy;
        StatusText txtPrivacyState;
        Button btnPrivacyApply, btnPrivacyRestore;
        // Automatik
        CheckBox chkMonthly, chkBoot, chkAutoPrivacy, chkAutoProfile, chkAutoReport;
        StatusText txtAutoState;
        Button btnAutoSetup, btnAutoRun, btnAutoRemove;

        class PrivacyRow { public TweakGroup Group; public int[] States; }

        static string T(string de, string en) { return Lang.T(de, en); }

        public MainForm() : base(Lang.T("Windows-Härtung prüfen, gezielt beheben und dauerhaft absichern",
                                        "Check Windows hardening, fix it selectively and keep it in place"), new Size(1040, 620), true)
        {
            AppHost.Credits.Clear();
            AppHost.AddCredit("HardeningKitty", T("Prüflogik und Prüflisten stammen aus HardeningKitty von Michael Schneider und der scip AG (MIT-Lizenz). " +
                                                  "Vielen Dank! Verbesserungen, neue Prüflisten und Fehlermeldungen gern direkt im Projekt beitragen.",
                                                  "The checks and finding lists come from HardeningKitty by Michael Schneider and scip AG (MIT license). " +
                                                  "Thank you! Improvements, new finding lists and bug reports are welcome directly in the project."), KittyRepo);

            var tabs = new TabControl { Location = new Point(20, 8), Size = new Size(1040, 632), Padding = new Point(14, 5), Anchor = All };
            tabs.TabPages.Add(Wrap(T("Prüfen", "Audit"), BuildAuditPage()));
            tabs.TabPages.Add(Wrap(T("Beheben", "Fix"), BuildFixPage()));
            tabs.TabPages.Add(Wrap(T("Datenschutz", "Privacy"), BuildPrivacyPage()));
            tabs.TabPages.Add(Wrap(T("Automatik", "Automation"), BuildAutoPage()));
            Content.Controls.Add(tabs);
            Shown += (s, e) => RunInBackground(T("Lese den Systemzustand …", "Reading system state …"), Startup);
            FinishLayout();
        }

        // ---------------------------------------------------------------- Bausteine

        static TabPage Wrap(string title, Panel page)
        {
            var tp = new TabPage(title) { BackColor = Theme.Background, UseVisualStyleBackColor = false };
            Make.Host(tp, page);
            return tp;
        }

        static TC Put<TC>(Panel page, TC c, AnchorStyles anchor) where TC : Control
        {
            c.Anchor = anchor;
            page.Controls.Add(c);
            return c;
        }

        static CheckBox Check(string text, int y, bool on)
        {
            return new CheckBox { Text = text, Location = new Point(X, y), Size = new Size(W, 24), Checked = on, ForeColor = Theme.Text };
        }

        static ListView List(int y, int height, bool checkBoxes, string[] columns, int[] percent)
        {
            var lv = new ListView
            {
                Location = new Point(X, y), Size = new Size(W, height), View = View.Details, FullRowSelect = true, CheckBoxes = checkBoxes,
                HideSelection = false, BorderStyle = BorderStyle.FixedSingle, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            foreach (string c in columns) lv.Columns.Add(c);
            Action fit = () =>
            {
                int w = lv.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
                if (w <= 0) return;
                lv.BeginUpdate();
                for (int i = 0; i < percent.Length; i++) lv.Columns[i].Width = w * percent[i] / 100;
                lv.EndUpdate();
            };
            // Nach dem Verkleinern erst anpassen, wenn das Fenster fertig umgebrochen ist – sonst bleibt eine horizontale Bildlaufleiste stehen
            lv.Resize += (s, e) => { if (lv.IsHandleCreated) lv.BeginInvoke(fit); };
            lv.HandleCreated += (s, e) => fit();
            return lv;
        }

        // Mehrzeiliger Text (markier- und kopierbar, bei Bedarf mit Bildlaufleiste)
        // Meldungsfeld: ✔/✘ am Zeilenanfang erscheinen als Windows-Symbole, langer Text scrollt, Kontextmenü „Kopieren“
        static StatusText InfoBox(int y, int height)
        {
            return new StatusText { Location = new Point(X, y), Size = new Size(W, height), BackColor = Theme.Background, ForeColor = Theme.Text };
        }

        static void Show(Control c, string text, StatusKind kind)
        {
            c.Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            c.ForeColor = kind == StatusKind.Info ? Theme.Text : Theme.For(kind);
        }

        static string SeverityText(string s) { return s == "High" ? T("hoch", "high") : s == "Low" ? T("niedrig", "low") : T("mittel", "medium"); }

        static void OpenFolder(string folder)
        {
            Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        static DetailCard Card(int y, int height, string placeholder)
        {
            var card = new DetailCard { Location = new Point(CardX, y), Size = new Size(CardW, height) };
            card.ShowPlaceholder(placeholder);
            return card;
        }

        static Color LevelColor(Stufe s) { return s == Stufe.Unkritisch ? Theme.Success : s == Stufe.Rueckfrage ? Theme.Warning : Theme.TextMuted; }

        // Werte lesbar machen: leer = nicht gesetzt, Listen (a;b;c) mit Leerzeichen, damit sie umbrechen
        static string Value(string v) { return string.IsNullOrEmpty(v) ? T("nicht gesetzt", "not set") : v.Replace(";", "; "); }

        // Vollständige Angaben zu einem Punkt in der Detailkarte
        static void ShowFinding(DetailCard card, Finding f)
        {
            bool exception = f.Failed && Ausnahmen.Contains(f);
            string badge; Color color; string text;
            if (f.Passed) { badge = T("✔ bestanden", "✔ passed"); color = Theme.Success; text = T("Die Einstellung entspricht der Empfehlung.", "The setting matches the recommendation."); }
            else if (!f.Failed) { badge = T("nicht geprüft", "not checked"); color = Theme.TextMuted; text = T("Diese Prüfung lief nicht – meist fehlen Administratorrechte.", "This check did not run – usually administrator rights are missing."); }
            else if (exception) { badge = T("Ausnahme", "Exception"); color = Theme.TextMuted; text = T("Bewusst nicht gehärtet. ", "Deliberately not hardened. ") + f.Hint; }
            else { badge = Einstufung.Title(f.Level); color = LevelColor(f.Level); text = f.Hint; }
            var fields = new List<DetailCard.Field>
            {
                new DetailCard.Field(T("Aktueller Wert", "Current value"), Value(f.Result)),
                new DetailCard.Field(T("Empfohlen", "Recommended"), Value(f.RecommendedText))
            };
            if (f.Failed) fields.Add(new DetailCard.Field(T("Schweregrad", "Severity"), SeverityText(f.Severity)));
            fields.Add(new DetailCard.Field(T("Kategorie", "Category"), f.Category));
            fields.Add(new DetailCard.Field("ID", f.Id));
            fields.Add(new DetailCard.Field(T("Prüfmethode", "Method"), f.Method));
            fields.Add(new DetailCard.Field(T("Gilt für", "Applies to"), f.IsUserList ? T("Benutzer", "User") : T("Computer", "Computer")));
            card.ShowDetails(f.Name, badge, color, null, fields, text, Theme.Text, null);
        }

        // ---------------------------------------------------------------- Reiter „Prüfen“

        Panel BuildAuditPage()
        {
            Panel p = Make.Page(PageSize);
            Put(p, Make.Section("HardeningKitty", X, 12, 130), TL);
            Put(p, Make.Link(T("von scip AG auf GitHub", "by scip AG on GitHub"), KittyRepo, 146, 15), TL);
            lblKitty = Put(p, new StatusText { Location = new Point(X, 38), Size = new Size(W - 190, 40), BackColor = Theme.Background, Text = "…" }, TLR);
            btnUpdate = Put(p, Make.Secondary(T("Auf Updates prüfen", "Check for updates"), X + W - 180, 36, 180, 32), TR);
            btnUpdate.Click += (s, e) => OnUpdate();

            Put(p, Make.Section(T("Prüfliste", "Finding list"), X, 86, W), TLR);
            cboList = Put(p, new ComboBox { Location = new Point(X, 114), Size = new Size(W - 224, 26), DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 20, FormattingEnabled = true }, TLR);
            cboList.Format += (s, e) =>
            {
                var l = e.ListItem as FindingList;
                if (l != null) e.Value = l.Display + (l == recommended ? T("   (empfohlen)", "   (recommended)") : "");
            };
            cboList.SelectedIndexChanged += (s, e) => OnListChanged();
            btnAudit = Put(p, Make.Primary(T("Prüfung starten", "Start audit"), X + W - 210, 110, 210, 34), TR);
            btnAudit.Enabled = false;
            btnAudit.Click += (s, e) => OnAudit();
            lblListHint = Put(p, new StatusText { Location = new Point(X, 146), Size = new Size(W, 20), BackColor = Theme.Background, ForeColor = Theme.TextMuted }, TLR);
            chkUser = Put(p, Check(T("Auch Benutzereinstellungen prüfen (gelten für das angemeldete Administratorkonto)",
                                     "Also check user settings (they apply to the signed-in administrator account)"), 168, Settings.GetBool("Benutzerliste", true)), TLR);

            Put(p, Make.Section(T("Ergebnis", "Result"), X, 202, W), TLR);
            lblScore = Put(p, new Label { Location = new Point(X, 228), AutoSize = true, Font = Theme.Large, ForeColor = Theme.TextMuted,
                                          Text = T("Noch keine Prüfung durchgeführt.", "No audit has been run yet.") }, TL);
            // Zahlen neben dem Score in normaler Schrift; Position folgt der Breite des Scores (ShowAudit)
            lblScoreCounts = Put(p, new Label { Location = new Point(X + 200, 231), Size = new Size(W - 200, 22), ForeColor = Theme.Text }, TLR);
            lblScoreSub = Put(p, Make.Text("", X, 256, W, 20), TLR);
            chkOnlyOpen = Put(p, new CheckBox { Text = T("Nur offene Punkte anzeigen", "Show open items only"), Location = new Point(X, 278), Size = new Size(400, 24), Checked = true }, TL);
            chkOnlyOpen.CheckedChanged += (s, e) => FillAuditList();
            lvAudit = Put(p, List(304, 248, false, new[] { T("Ergebnis", "Result"), "ID", T("Einstellung", "Setting"), T("Ist", "Current"), T("Soll", "Recommended") },
                                  new[] { 16, 11, 45, 14, 14 }), All);
            lvAudit.Width = ListW;
            auditImages = Symbols.ListImages(lvAudit);
            lvAudit.SmallImageList = auditImages;
            cardAudit = Put(p, Card(304, 248, T("Einen Punkt anklicken, um alle Angaben vollständig zu sehen.", "Click an item to see all details.")), TRB);
            lvAudit.SelectedIndexChanged += (s, e) =>
            {
                if (lvAudit.SelectedItems.Count > 0) ShowFinding(cardAudit, (Finding)lvAudit.SelectedItems[0].Tag);
            };

            var btnFolder = Put(p, Make.Secondary(T("Berichtsordner", "Report folder"), X, 560, 170, 32), BL);
            btnFolder.Click += (s, e) => OpenFolder(last != null ? last.Folder : Paths.Reports);
            var btnExport = Put(p, Make.Secondary(T("Bericht speichern unter …", "Save report as …"), 190, 560, 220, 32), BL);
            btnExport.Click += (s, e) => OnExport();
            var btnOutput = Put(p, Make.Secondary(T("Ausgabe von HardeningKitty", "HardeningKitty output"), 418, 560, 220, 32), BL);
            btnOutput.Click += (s, e) => OnShowOutput();
            return p;
        }

        void Startup()
        {
            SystemInfo.Load();
            LoadKitty();
            AuditResult r = Ablauf.LoadLast();
            Ui(() =>
            {
                ApplyAdminState();
                if (r != null) ShowAudit(r);
            });
            RefreshPrivacy();
            RefreshAuto();
            try { latestTag = Kitty.LatestTag(); }
            catch (Exception ex) { Log.Write("GitHub nicht erreichbar: " + ex.Message); }
            Ui(ShowKitty);
        }

        void LoadKitty()
        {
            kitty = Kitty.Find();
            lists = kitty != null ? Catalog.Load(kitty.ListsFolder) : new List<FindingList>();
            recommended = SystemInfo.Recommend(lists);
            if (kitty == null) Log.Write("HardeningKitty nicht gefunden");
            else Log.Write("HardeningKitty " + kitty.Version + " in " + kitty.Folder + ", " + lists.Count + " Prüflisten, empfohlen: " + (recommended != null ? recommended.Key : "–"));
            Ui(() =>
            {
                filling = true;
                cboList.Items.Clear();
                foreach (FindingList l in lists) cboList.Items.Add(l);
                string saved = Settings.Get("Liste", "");
                FindingList sel = lists.FirstOrDefault(l => l.Key == saved) ?? recommended;
                if (sel != null) cboList.SelectedItem = sel;
                filling = false;
                OnListChanged();
                ShowKitty();
            });
        }

        void ShowKitty()
        {
            string latest = latestTag != null ? Kitty.TagToVersion(latestTag) : null;
            if (kitty == null)
            {
                Show(lblKitty, "✘ " + T("HardeningKitty ist noch nicht vorhanden – bitte herunterladen.", "HardeningKitty is not available yet – please download it.") +
                               (latest != null ? T("\nAktuell auf GitHub: Version ", "\nLatest on GitHub: version ") + latest : ""), StatusKind.Error);
                btnUpdate.Text = T("Herunterladen", "Download");
            }
            else
            {
                string where = kitty.IsOwnCopy ? T("im Programmordner", "in the program folder") : T("System-Modul in ", "system module in ") + kitty.Folder;
                if (latest == null)
                {
                    Show(lblKitty, "Version " + kitty.Version + " (" + where + ")\n" + T("GitHub ist nicht erreichbar – Aktualität unbekannt.", "GitHub is not reachable – unknown whether it is current."), StatusKind.Info);
                    btnUpdate.Text = T("Auf Updates prüfen", "Check for updates");
                }
                else if (Kitty.CompareVersions(kitty.Version, latest) < 0)
                {
                    Show(lblKitty, "Version " + kitty.Version + " (" + where + ")\n" + T("Auf GitHub gibt es die neuere Version ", "A newer version is available on GitHub: ") + latest + ".", StatusKind.Warning);
                    btnUpdate.Text = T("Auf " + latest + " aktualisieren", "Update to " + latest);
                }
                else if (!kitty.IsOwnCopy)
                {
                    Show(lblKitty, "✔ Version " + kitty.Version + T(" ist aktuell (", " is up to date (") + where + ").\n" +
                                   T("Für künftige Updates wird eine eigene Kopie im Programmordner angelegt.", "For future updates, a copy will be created in the program folder."), StatusKind.Success);
                    btnUpdate.Text = T("Eigene Kopie anlegen", "Create own copy");
                }
                else
                {
                    Show(lblKitty, "✔ Version " + kitty.Version + T(" ist aktuell (GitHub: ", " is up to date (GitHub: ") + latest + ").", StatusKind.Success);
                    btnUpdate.Text = T("Auf Updates prüfen", "Check for updates");
                }
            }
            btnAudit.Enabled = kitty != null && lists.Count > 0;
        }

        void OnUpdate()
        {
            RunInBackground(T("Frage GitHub nach der neuesten Version …", "Asking GitHub for the latest version …"), () =>
            {
                try { latestTag = Kitty.LatestTag(); }
                catch (Exception ex)
                {
                    Log.Write("GitHub nicht erreichbar: " + ex);
                    Ui(() => MessageBox.Show(this, T("GitHub ist nicht erreichbar:\n", "GitHub is not reachable:\n") + ex.Message +
                                                   T("\n\nBitte Internetverbindung bzw. Proxy prüfen.", "\n\nPlease check the internet connection or proxy."),
                        AppHost.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning));
                    return;
                }
                string latest = Kitty.TagToVersion(latestTag);
                if (kitty != null && kitty.IsOwnCopy && Kitty.CompareVersions(kitty.Version, latest) >= 0)
                {
                    Ui(ShowKitty);
                    return;
                }
                bool go = false;
                Ui(() => go = Confirm(T("HardeningKitty " + latest + " von GitHub (github.com/scipag/HardeningKitty) herunterladen?\n\n" +
                                        "Die digitale Signatur wird geprüft. Eine vorhandene Kopie bleibt als Ordner „HardeningKitty_alt_…“ erhalten.",
                                        "Download HardeningKitty " + latest + " from GitHub (github.com/scipag/HardeningKitty)?\n\n" +
                                        "The digital signature is verified. An existing copy is kept as folder “HardeningKitty_alt_…”."),
                                      T("HardeningKitty herunterladen", "Download HardeningKitty")));
                if (!go) return;
                string version = Kitty.Install(latestTag, s => SetFooterStatus(s));
                Automatik.SyncKitty();
                LoadKitty();
                Ui(() => MessageBox.Show(this, "HardeningKitty " + version + T(" ist bereit.", " is ready."), AppHost.Name, MessageBoxButtons.OK, MessageBoxIcon.Information));
            });
        }

        void OnListChanged()
        {
            if (filling) return;
            var l = cboList.SelectedItem as FindingList;
            if (l == null) { lblListHint.Text = ""; return; }
            string parts = l.MachineFile != null && l.UserFile != null ? T("Rechner- und Benutzereinstellungen", "computer and user settings")
                         : l.MachineFile != null ? T("nur Rechnereinstellungen", "computer settings only") : T("nur Benutzereinstellungen", "user settings only");
            lblListHint.Text = (l == recommended ? T("✔ Passend für ", "✔ Suitable for ") : T("Dieser Rechner: ", "This computer: ")) + SystemInfo.Describe() +
                               T(". Die Liste prüft ", ". The list checks ") + parts + ".";
            chkUser.Enabled = l.MachineFile != null && l.UserFile != null;
        }

        void OnAudit()
        {
            var l = cboList.SelectedItem as FindingList;
            if (kitty == null || l == null) return;
            bool user = chkUser.Enabled && chkUser.Checked;
            Settings.Set("Liste", l.Key);
            Settings.SetBool("Benutzerliste", chkUser.Checked);
            RunInBackground(T("Prüfung läuft …", "Audit running …"), () =>
            {
                Automatik.SyncSettings();
                AuditResult r = Ablauf.Audit(kitty, l, user, s => SetFooterStatus(s));
                Ui(() => ShowAudit(r));
            });
        }

        void ShowAudit(AuditResult r)
        {
            last = r;
            r.Count();
            int exceptions = r.Findings.Count(f => f.Failed && Ausnahmen.Contains(f));
            lblScore.Text = "Score " + AuditResult.FormatScore(r.Score) + T(" von 6", " of 6");
            lblScore.ForeColor = r.Score >= 5 ? Theme.Success : r.Score >= 3.5 ? Theme.Warning : Theme.Error;
            lblScoreCounts.Left = lblScore.Right + 10;
            lblScoreCounts.Width = lblScore.Parent.ClientSize.Width - lblScoreCounts.Left - X;
            lblScoreCounts.Text = r.Passed + T(" von ", " of ") + r.Total + T(" bestanden, ", " passed, ") + r.Open + T(" offen", " open") +
                                  (r.Open > 0 ? " (" + r.High + T(" hoch, ", " high, ") + r.Medium + T(" mittel, ", " medium, ") + r.Low + T(" niedrig)", " low)") : "");
            // Unterzeile als Sätze: Liste und Zeitpunkt, dann Vergleich, Ausnahmen, nicht Geprüftes
            var sub = new List<string> { r.ListDisplay + T(", geprüft am ", ", audited on ") + r.Time.ToString("g") };
            string prev = Verlauf.Previous(r);
            if (prev != "") sub.Add(prev.Substring(0, 1).ToUpper() + prev.Substring(1));
            if (exceptions > 0) sub.Add(exceptions + T(" Punkte als Ausnahme", " items marked as exception"));
            if (r.NotChecked > 0) sub.Add(r.NotChecked + T(" nicht geprüft", " not checked") + (SystemInfo.IsAdmin ? "" : T(" (ohne Adminrechte)", " (no admin rights)")));
            lblScoreSub.Text = string.Join(". ", sub) + ".";
            FillAuditList();
            FillFixList();
        }

        void FillAuditList()
        {
            if (last == null) return;
            lvAudit.BeginUpdate();
            lvAudit.Items.Clear(); cardAudit.Reset();
            lvAudit.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (Finding f in last.Findings)
            {
                if (chkOnlyOpen.Checked && !f.Failed) continue;
                ListViewGroup g;
                if (!groups.TryGetValue(f.Category, out g))
                {
                    g = new ListViewGroup(f.Category);
                    groups[f.Category] = g;
                    lvAudit.Groups.Add(g);
                }
                bool exception = f.Failed && Ausnahmen.Contains(f);
                string result = f.Passed ? T("bestanden", "passed") : f.Failed ? SeverityText(f.Severity) + (exception ? T(" (Ausnahme)", " (exception)") : "")
                              : T("nicht geprüft", "not checked");
                Color color = f.Passed ? Theme.Success : !f.Failed || exception ? Theme.TextMuted
                            : f.Severity == "High" ? Theme.Error : f.Severity == "Medium" ? Theme.Warning : Theme.Text;
                // Symbol und Farbe nur in der Ergebnis-Spalte, der Rest der Zeile bleibt normaler Text
                var item = new ListViewItem(new[] { result, f.Id, f.Name, f.Result, f.RecommendedText }, g)
                {
                    Tag = f, ToolTipText = f.Name, UseItemStyleForSubItems = false,
                    ImageKey = Symbols.Key(auditImages, f.Passed ? "✔" : f.Failed ? "✘" : "–", color)
                };
                item.SubItems[0].ForeColor = color;
                if (!f.Failed || exception)
                    for (int i = 1; i < item.SubItems.Count; i++) item.SubItems[i].ForeColor = Theme.TextMuted;
                lvAudit.Items.Add(item);
            }
            lvAudit.EndUpdate();
        }

        void OnExport()
        {
            if (last == null) { SetFooterStatus(T("Es liegt noch keine Prüfung vor.", "No audit available yet.")); return; }
            using (var dlg = new SaveFileDialog
            {
                Title = T("Bericht speichern", "Save report"),
                Filter = T("Tabelle für Excel (*.csv)|*.csv|Ausgabe von HardeningKitty (*.txt)|*.txt", "Spreadsheet for Excel (*.csv)|*.csv|HardeningKitty output (*.txt)|*.txt"),
                FileName = "HardeningKitty_" + Environment.MachineName + "_" + last.Time.ToString("yyyy-MM-dd_HHmm")
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (dlg.FilterIndex == 2) File.Copy(Path.Combine(last.Folder, "HardeningKitty-Ausgabe.txt"), dlg.FileName, true);
                else File.WriteAllText(dlg.FileName, ExportCsv(), new UTF8Encoding(true));
                Log.Write("Bericht gespeichert: " + dlg.FileName);
                SetFooterStatus(T("Gespeichert: ", "Saved: ") + dlg.FileName);
            }
        }

        static string CsvField(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        string ExportCsv()
        {
            var sb = new StringBuilder(T("Ergebnis;Schwere;Einstufung;Ausnahme;Liste;ID;Kategorie;Einstellung;Ist;Soll;Hinweis\r\n",
                                         "Result;Severity;Rating;Exception;List;ID;Category;Setting;Current;Recommended;Note\r\n"));
            foreach (Finding f in last.Findings)
                sb.Append(string.Join(";", new[]
                {
                    f.Passed ? T("bestanden", "passed") : f.Failed ? T("offen", "open") : T("nicht geprüft", "not checked"),
                    f.Failed ? SeverityText(f.Severity) : "", f.Failed ? Einstufung.Title(f.Level) : "", f.Failed && Ausnahmen.Contains(f) ? T("ja", "yes") : "",
                    f.ListName, f.Id, f.Category, f.Name, f.Result, f.RecommendedText, f.Failed ? f.Hint : ""
                }.Select(CsvField)) + "\r\n");
            return sb.ToString();
        }

        void OnShowOutput()
        {
            string file = last != null ? Path.Combine(last.Folder, "HardeningKitty-Ausgabe.txt") : null;
            if (file == null || !File.Exists(file)) { SetFooterStatus(T("Es liegt noch keine Ausgabe vor.", "No output available yet.")); return; }
            Process.Start("notepad.exe", "\"" + file + "\"");
        }

        // ---------------------------------------------------------------- Reiter „Beheben“

        Panel BuildFixPage()
        {
            Panel p = Make.Page(PageSize);
            Put(p, Make.Section(T("Offene Punkte beheben", "Fix open items"), X, 12, W), TLR);
            Put(p, Make.Text(T("Unkritische Punkte sind vorausgewählt. Punkte „mit Rückfrage“ können Funktionen einschränken – Hinweis lesen und bewusst auswählen. " +
                               "Vor jeder Änderung wird automatisch gesichert.",
                               "Safe items are preselected. Items that “need review” may restrict features – read the note and select them deliberately. " +
                               "A backup is created automatically before every change."), X, 36, W, 40), TLR);
            lvFix = Put(p, List(80, 342, true, new[] { T("Einstellung", "Setting"), T("Ist", "Current"), T("Soll", "Recommended"), T("Schwere", "Severity") },
                                new[] { 58, 14, 14, 14 }), All);
            lvFix.Width = ListW;
            cardFix = Put(p, Card(80, 342, T("Einen Punkt anklicken, um Einstufung, Hinweis und alle Angaben zu sehen.", "Click an item to see its rating, note and all details.")), TRB);
            gSafe = new ListViewGroup("");
            gAsk = new ListViewGroup("");
            gManual = new ListViewGroup("");
            gException = new ListViewGroup("");
            lvFix.Groups.AddRange(new[] { gSafe, gAsk, gManual, gException });
            lvFix.ItemCheck += (s, e) =>
            {
                if (filling) return;
                var f = (Finding)lvFix.Items[e.Index].Tag;
                if (f.Level == Stufe.Manuell || Ausnahmen.Contains(f)) e.NewValue = e.CurrentValue;
            };
            lvFix.ItemChecked += (s, e) => { if (!filling) UpdateFixCount(); };
            lvFix.SelectedIndexChanged += (s, e) =>
            {
                if (lvFix.SelectedItems.Count == 0) return;
                ShowFinding(cardFix, (Finding)lvFix.SelectedItems[0].Tag);
            };

            var btnSafe = Put(p, Make.Secondary(T("Unkritische wählen", "Select safe items"), X, 436, 180, 30), BL);
            btnSafe.Click += (s, e) => SelectFix(true);
            var btnNone = Put(p, Make.Secondary(T("Keine wählen", "Select none"), 200, 436, 140, 30), BL);
            btnNone.Click += (s, e) => SelectFix(false);
            var btnException = Put(p, Make.Secondary(T("Ausnahme an/aus", "Toggle exception"), 348, 436, 180, 30), BL);
            btnException.Click += (s, e) => OnException();
            lblFixCount = Put(p, new Label { Location = new Point(X + W - 320, 442), Size = new Size(320, 20), TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.TextMuted }, BR);
            chkRestorePoint = Put(p, new CheckBox { Text = T("Vorher einen Windows-Wiederherstellungspunkt anlegen", "Create a Windows restore point first"),
                                                    Location = new Point(X, 472), Size = new Size(500, 24), Checked = true }, BL);
            btnFix = Put(p, Make.Primary(T("Ausgewählte beheben", "Fix selected"), X, 500, 240, 36), BL);
            btnFix.Enabled = false;
            btnFix.Click += (s, e) => OnFix();
            btnRestore = Put(p, Make.Secondary(T("Sicherung zurückspielen …", "Restore backup …"), X + W - 220, 502, 220, 32), BR);
            btnRestore.Click += (s, e) => OnRestore(txtFixState);
            txtFixState = Put(p, InfoBox(542, 54), BLR);
            return p;
        }

        static int SeverityRank(string s) { return s == "High" ? 0 : s == "Medium" ? 1 : 2; }

        void FillFixList()
        {
            filling = true;
            lvFix.BeginUpdate();
            lvFix.Items.Clear(); cardFix.Reset();
            var counts = new Dictionary<ListViewGroup, int> { { gSafe, 0 }, { gAsk, 0 }, { gManual, 0 }, { gException, 0 } };
            if (last != null)
                foreach (Finding f in last.Findings.Where(x => x.Failed).OrderBy(x => x.Level).ThenBy(x => SeverityRank(x.Severity)))
                {
                    bool exception = Ausnahmen.Contains(f);
                    ListViewGroup g = exception ? gException : f.Level == Stufe.Unkritisch ? gSafe : f.Level == Stufe.Rueckfrage ? gAsk : gManual;
                    counts[g]++;
                    var item = new ListViewItem(new[] { f.Name, f.Result, f.RecommendedText, SeverityText(f.Severity) }, g)
                    {
                        Tag = f, ToolTipText = f.Hint, Checked = !exception && f.Level == Stufe.Unkritisch
                    };
                    if (exception || f.Level == Stufe.Manuell) item.ForeColor = Theme.TextMuted;
                    lvFix.Items.Add(item);
                }
            gSafe.Header = T("Unkritisch – kann automatisch behoben werden (", "Safe – can be fixed automatically (") + counts[gSafe] + ")";
            gAsk.Header = T("Mit Rückfrage – kann Funktionen einschränken (", "Needs review – may restrict features (") + counts[gAsk] + ")";
            gManual.Header = T("Manuell – nicht automatisch behebbar (", "Manual – cannot be fixed automatically (") + counts[gManual] + ")";
            gException.Header = T("Ausnahmen – bewusst nicht gehärtet (", "Exceptions – deliberately not hardened (") + counts[gException] + ")";
            lvFix.EndUpdate();
            filling = false;
            UpdateFixCount();
        }

        void SelectFix(bool safeOnly)
        {
            filling = true;
            foreach (ListViewItem i in lvFix.Items)
            {
                var f = (Finding)i.Tag;
                i.Checked = safeOnly && f.Level == Stufe.Unkritisch && !Ausnahmen.Contains(f);
            }
            filling = false;
            UpdateFixCount();
        }

        void UpdateFixCount()
        {
            var sel = lvFix.CheckedItems.Cast<ListViewItem>().Select(i => (Finding)i.Tag).ToList();
            int ask = sel.Count(f => f.Level == Stufe.Rueckfrage);
            lblFixCount.Text = sel.Count + T(" ausgewählt", " selected") + (ask > 0 ? T(", davon ", ", ") + ask + T(" mit Rückfrage", " need review") : "");
            lblFixCount.ForeColor = ask > 0 ? Theme.Warning : Theme.TextMuted;
            btnFix.Enabled = sel.Count > 0 && kitty != null && SystemInfo.IsAdmin;
        }

        void OnException()
        {
            var sel = lvFix.SelectedItems.Cast<ListViewItem>().Select(i => (Finding)i.Tag).ToList();
            if (sel.Count == 0)
            {
                Show(txtFixState, T("Bitte zuerst einen oder mehrere Punkte in der Liste markieren (anklicken), dann „Ausnahme an/aus“.",
                                    "Please first highlight one or more items in the list (click), then “Toggle exception”."), StatusKind.Warning);
                return;
            }
            bool on = !Ausnahmen.Contains(sel[0]);
            foreach (Finding f in sel) Ausnahmen.Set(f, on);
            bool synced = Automatik.SyncSettings();
            ShowAudit(last);
            Show(txtFixState, (on ? T("Als Ausnahme markiert: " + sel.Count + " Punkt(e). Sie werden nicht mehr vorgeschlagen und von der Automatik nicht gesetzt.",
                                      "Marked as exception: " + sel.Count + " item(s). They are no longer suggested and not applied by the automation.")
                                  : T("Ausnahme aufgehoben: " + sel.Count + " Punkt(e).", "Exception removed: " + sel.Count + " item(s).")) +
                              (synced ? "" : "\n✘ " + T("Die Änderung konnte nicht in die Automatik übernommen werden (Details im Log).",
                                                         "The change could not be passed on to the automation (details in the log).")),
                 synced ? StatusKind.Info : StatusKind.Error);
        }

        void OnFix()
        {
            var sel = lvFix.CheckedItems.Cast<ListViewItem>().Select(i => (Finding)i.Tag).ToList();
            if (sel.Count == 0 || kitty == null) return;
            var ask = sel.Where(f => f.Level == Stufe.Rueckfrage).ToList();
            bool restorePoint = chkRestorePoint.Checked;
            string text = T(sel.Count + " Einstellungen werden auf den empfohlenen Wert gesetzt.", sel.Count + " settings will be set to the recommended value.") + "\n\n" +
                          string.Join("\n", sel.Take(8).Select(f => "• " + f.Name)) + (sel.Count > 8 ? T("\n• … und ", "\n• … and ") + (sel.Count - 8) + T(" weitere", " more") : "") +
                          (ask.Count > 0 ? T("\n\nAchtung: " + ask.Count + " davon können Funktionen einschränken (Einstufung „mit Rückfrage“).",
                                             "\n\nCaution: " + ask.Count + " of them may restrict features (rated “needs review”).") : "") +
                          T("\n\nVorher werden die aktuellen Werte im Ordner „Sicherungen“ gespeichert", "\n\nThe current values are saved to the “Sicherungen” folder first") +
                          (restorePoint ? T(" und ein Wiederherstellungspunkt angelegt", " and a restore point is created") : "") +
                          T(". Manche Änderungen wirken erst nach einem Neustart.\n\nFortfahren?", ". Some changes only take effect after a restart.\n\nContinue?");
            if (!Confirm(text, T("Ausgewählte beheben", "Fix selected"))) return;
            RunInBackground(T("Behebe …", "Fixing …"), () => DoFix(sel, restorePoint));
        }

        void DoFix(List<Finding> sel, bool restorePoint)
        {
            string rpMessage = "";
            if (restorePoint)
            {
                SetFooterStatus(T("Lege einen Wiederherstellungspunkt an (kann etwas dauern) …", "Creating a restore point (may take a while) …"));
                string msg;
                bool ok = Wiederherstellungspunkt.Create("HardeningKitty UI", out msg);
                rpMessage = "\n" + msg;
                if (!ok)
                {
                    bool go = false;
                    Ui(() => go = Confirm(msg + T("\n\nTrotzdem fortfahren? Die Einstellungen werden in jedem Fall vorher gesichert.",
                                                  "\n\nContinue anyway? The settings are backed up first in any case."), T("Wiederherstellungspunkt", "Restore point")));
                    if (!go)
                    {
                        Ui(() => Show(txtFixState, T("Abgebrochen – es wurde nichts geändert.", "Cancelled – nothing was changed."), StatusKind.Warning));
                        return;
                    }
                }
            }
            FixResult res = Ablauf.Fix(kitty, sel, "Beheben", s => SetFooterStatus(s));
            bool synced = Automatik.SyncSettings();
            string text = (res.Failed == 0
                    ? "✔ " + T(res.Ok + " Einstellungen gesetzt und geprüft.", res.Ok + " settings applied and verified.")
                    : "✘ " + T(res.Ok + " von " + (res.Ok + res.Failed) + " Einstellungen übernommen, " + res.Failed + " nicht – z. B. weil eine Gruppenrichtlinie sie überschreibt:\n",
                               res.Ok + " of " + (res.Ok + res.Failed) + " settings applied, " + res.Failed + " not – e.g. because a group policy overrides them:\n") +
                      string.Join("\n", res.Problems.Take(4).Select(p => "   " + p))) +
                T("\nSicherung: ", "\nBackup: ") + res.Folder + rpMessage +
                (synced ? "" : "\n✘ " + T("Das Härtungsprofil konnte nicht in die Automatik übernommen werden (Details im Log).",
                                           "The hardening profile could not be passed on to the automation (details in the log)."));
            Ui(() =>
            {
                ShowAudit(last);
                lblScoreSub.Text += T(" Nach dem Beheben neu berechnet.", " Recalculated after fixing.");
                Show(txtFixState, text, res.Failed == 0 && synced ? StatusKind.Success : StatusKind.Error);
            });
        }

        void OnRestore(StatusText target)
        {
            string folder;
            using (var dlg = new FolderBrowserDialog { Description = T("Sicherung wählen (ein Unterordner von „Sicherungen“)", "Choose a backup (a subfolder of “Sicherungen”)"),
                                                       SelectedPath = Paths.Backups, ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                folder = dlg.SelectedPath;
            }
            if (!Sicherung.IsBackupFolder(folder))
            {
                Show(target, "✘ " + T("Im gewählten Ordner liegt keine Sicherung dieses Programms.\nBitte einen Unterordner von „Sicherungen“ wählen, z. B. „2026-10-29_21-40-00_Beheben“.",
                                      "The selected folder does not contain a backup of this program.\nPlease choose a subfolder of “Sicherungen”, e.g. “2026-10-29_21-40-00_Beheben”."),
                     StatusKind.Error);
                return;
            }
            if (!Confirm(T("Die Einstellungen aus der Sicherung\n" + folder + "\nwerden zurückgespielt.\n\nDer jetzige Stand wird vorher ebenfalls gesichert. " +
                           "Zurückgespielte Punkte nimmt das Programm aus der Automatik heraus.\n\nFortfahren?",
                           "The settings from the backup\n" + folder + "\nwill be restored.\n\nThe current state is backed up first as well. " +
                           "Restored items are removed from the automation.\n\nContinue?"), T("Sicherung zurückspielen", "Restore backup")))
                return;
            RunInBackground(T("Spiele die Sicherung zurück …", "Restoring the backup …"), () =>
            {
                List<string> msgs = Ablauf.Restore(kitty, folder, s => SetFooterStatus(s));
                if (!Automatik.SyncSettings()) msgs.Add("✘ " + T("Die Änderung konnte nicht in die Automatik übernommen werden (Details im Log).",
                                                                  "The change could not be passed on to the automation (details in the log)."));
                msgs.Add(T("Für ein aktuelles Ergebnis bitte erneut prüfen. Manche Einstellungen wirken erst nach einem Neustart.",
                           "Please run the audit again for a current result. Some settings only take effect after a restart."));
                Ui(() => Show(target, string.Join("\n", msgs), msgs.Any(m => m.StartsWith("✘")) ? StatusKind.Error : StatusKind.Success));
                RefreshPrivacy();
                RefreshAuto();
            });
        }

        // ---------------------------------------------------------------- Reiter „Datenschutz“

        Panel BuildPrivacyPage()
        {
            Panel p = Make.Page(PageSize);
            Put(p, Make.Section(T("Telemetrie, Werbung und Datenschutz", "Telemetry, ads and privacy"), X, 12, W), TLR);
            Put(p, Make.Text(T("Datenschutz-Einstellungen, die über HardeningKitty hinausgehen. Gesetzt wird per Richtlinie; die Automatik prüft sie nach Windows-Updates erneut. " +
                               "Abwählen nimmt eine Gruppe nur aus der Automatik – zurückgesetzt wird über „Sicherung zurückspielen“.",
                               "Privacy settings that go beyond HardeningKitty. They are applied as policies and re-checked by the automation after Windows updates. " +
                               "Unchecking a group only removes it from the automation – use “Restore backup” to revert it."), X, 36, W, 40), TLR);
            lvPrivacy = Put(p, List(80, 370, true, new[] { T("Bereich", "Area"), T("Einstufung", "Rating"), "Status" }, new[] { 58, 20, 22 }), All);
            lvPrivacy.Width = ListW;
            cardPrivacy = Put(p, Card(80, 370, T("Eine Gruppe anklicken, um Beschreibung und Einzelpunkte zu sehen.", "Click a group to see its description and individual items.")), TRB);
            gPrivSafe = new ListViewGroup(T("Unkritisch – empfohlen", "Safe – recommended"));
            gPrivAsk = new ListViewGroup(T("Mit Rückfrage – kann Funktionen einschränken", "Needs review – may restrict features"));
            lvPrivacy.Groups.AddRange(new[] { gPrivSafe, gPrivAsk });
            lvPrivacy.SelectedIndexChanged += (s, e) => ShowPrivacyDetail();
            btnPrivacyApply = Put(p, Make.Primary(T("Ausgewählte anwenden", "Apply selected"), X, 464, 240, 36), BL);
            btnPrivacyApply.Click += (s, e) => OnPrivacyApply();
            var btnPrivacyCheck = Put(p, Make.Secondary(T("Status prüfen", "Check status"), 260, 466, 150, 32), BL);
            btnPrivacyCheck.Click += (s, e) => RunInBackground(T("Prüfe den Status …", "Checking status …"), RefreshPrivacy);
            btnPrivacyRestore = Put(p, Make.Secondary(T("Sicherung zurückspielen …", "Restore backup …"), X + W - 220, 466, 220, 32), BR);
            btnPrivacyRestore.Click += (s, e) => OnRestore(txtPrivacyState);
            txtPrivacyState = Put(p, InfoBox(508, 88), BLR);
            return p;
        }

        void RefreshPrivacy()
        {
            var rows = Datenschutz.Groups.Select(g => new PrivacyRow { Group = g, States = g.Items.Select(t => t.State()).ToArray() }).ToList();
            List<TweakGroup> selected = Datenschutz.Selected();
            Log.Write("Datenschutz-Status: " + string.Join(", ", rows.Select(r => r.Group.Id + " " + r.States.Count(x => x == 1) + "/" + r.States.Count(x => x >= 0))));
            Ui(() =>
            {
                filling = true;
                lvPrivacy.BeginUpdate();
                lvPrivacy.Items.Clear(); cardPrivacy.Reset();
                foreach (PrivacyRow r in rows)
                {
                    int applicable = r.States.Count(x => x >= 0), done = r.States.Count(x => x == 1);
                    string status = applicable == 0 ? T("nicht vorhanden", "not present") : done == applicable ? T("gesetzt", "applied")
                                  : done == 0 ? T("offen", "open") : done + T(" von ", " of ") + applicable + T(" gesetzt", " applied");
                    var item = new ListViewItem(new[] { r.Group.Title, Einstufung.Title(r.Group.Level), status }, r.Group.Level == Stufe.Unkritisch ? gPrivSafe : gPrivAsk)
                    {
                        Tag = r, Checked = selected.Contains(r.Group), UseItemStyleForSubItems = false
                    };
                    item.SubItems[2].ForeColor = applicable > 0 && done == applicable ? Theme.Success : Theme.Text;
                    lvPrivacy.Items.Add(item);
                }
                lvPrivacy.EndUpdate();
                filling = false;
            });
        }

        void ShowPrivacyDetail()
        {
            if (lvPrivacy.SelectedItems.Count == 0) return;
            var r = (PrivacyRow)lvPrivacy.SelectedItems[0].Tag;
            int applicable = r.States.Count(x => x >= 0), done = r.States.Count(x => x == 1);
            var items = r.Group.Items.Select((t, i) => r.States[i] == 1 ? new DetailCard.Item("✔", t.Label, Theme.Success)
                                                     : r.States[i] < 0 ? new DetailCard.Item("–", t.Label + T(" (nicht vorhanden)", " (not present)"), Theme.TextMuted)
                                                     : new DetailCard.Item("○", t.Label, Theme.Text));
            cardPrivacy.ShowDetails(r.Group.Title, Einstufung.Title(r.Group.Level), LevelColor(r.Group.Level),
                                    applicable == 0 ? T("Auf diesem Rechner nicht vorhanden", "Not present on this computer")
                                                    : done + T(" von ", " of ") + applicable + T(" Einstellungen gesetzt", " settings applied"),
                                    null, r.Group.Description, Theme.Text, items);
        }

        void OnPrivacyApply()
        {
            var groups = lvPrivacy.CheckedItems.Cast<ListViewItem>().Select(i => ((PrivacyRow)i.Tag).Group).ToList();
            if (groups.Count == 0)
            {
                if (!Confirm(T("Keine Gruppe ausgewählt. Die Automatik wendet dann keine Datenschutz-Einstellungen mehr an; bereits gesetzte bleiben bestehen.\n\nAuswahl so speichern?",
                               "No group selected. The automation will then no longer apply privacy settings; settings already applied remain.\n\nSave this selection?"),
                             T("Datenschutz", "Privacy"))) return;
                Datenschutz.SaveSelection(groups);
                bool ok = Automatik.SyncSettings();
                Show(txtPrivacyState, ok ? T("Auswahl gespeichert: keine Gruppe.", "Selection saved: no group.")
                                         : "✘ " + T("Auswahl gespeichert, aber nicht in die Automatik übernommen (Details im Log).", "Selection saved, but not passed on to the automation (details in the log)."),
                     ok ? StatusKind.Info : StatusKind.Error);
                return;
            }
            var risky = groups.Where(g => g.Level == Stufe.Rueckfrage).ToList();
            string text = T("Diese Bereiche werden per Richtlinie gesetzt:\n\n", "These areas will be set by policy:\n\n") + string.Join("\n", groups.Select(g => "• " + g.Title)) +
                          (risky.Count > 0 ? T("\n\nAchtung, mit möglichen Einschränkungen:\n", "\n\nCaution, may restrict features:\n") + string.Join("\n", risky.Select(g => "• " + g.Title + ": " + g.Description)) : "") +
                          T("\n\nDie vorherigen Werte werden im Ordner „Sicherungen“ gespeichert. Manche Änderungen wirken erst nach einem Neustart.\n\nFortfahren?",
                            "\n\nThe previous values are saved to the “Sicherungen” folder. Some changes only take effect after a restart.\n\nContinue?");
            if (!Confirm(text, T("Datenschutz-Einstellungen anwenden", "Apply privacy settings"))) return;
            RunInBackground(T("Setze die Datenschutz-Einstellungen …", "Applying privacy settings …"), () =>
            {
                string folder;
                List<string> msgs = Datenschutz.Apply(groups, out folder);
                Datenschutz.SaveSelection(groups);
                if (folder != null) msgs.Add(T("Sicherung: ", "Backup: ") + folder);
                if (!Automatik.SyncSettings()) msgs.Add("✘ " + T("Die Auswahl konnte nicht in die Automatik übernommen werden (Details im Log).",
                                                                  "The selection could not be passed on to the automation (details in the log)."));
                msgs.Add(T("Die Auswahl gilt auch für die Automatik.", "The selection also applies to the automation."));
                Ui(() => Show(txtPrivacyState, string.Join("\n", msgs), msgs.Any(m => m.StartsWith("✘")) ? StatusKind.Error : StatusKind.Success));
                RefreshPrivacy();
                RefreshAuto();
            });
        }

        // ---------------------------------------------------------------- Reiter „Automatik“

        Panel BuildAutoPage()
        {
            Panel p = Make.Page(PageSize);
            Put(p, Make.Section(T("Regelmäßig prüfen und erneut absichern", "Check regularly and re-apply"), X, 12, W), TLR);
            Put(p, Make.Text(T("Windows-Updates setzen manche Einstellungen zurück. Eine geplante Aufgabe (Konto SYSTEM) wendet die ausgewählten Datenschutz-Einstellungen " +
                               "erneut an, prüft die behobenen HardeningKitty-Punkte und setzt zurückgesetzte wieder. Ausnahmen bleiben unangetastet.",
                               "Windows updates reset some settings. A scheduled task (SYSTEM account) re-applies the selected privacy settings, checks the fixed " +
                               "HardeningKitty items and re-applies any that were reset. Exceptions are left untouched."), X, 36, W, 40), TLR);
            Put(p, Make.Section(T("Zeitpunkt", "Schedule"), X, 82, W), TLR);
            chkMonthly = Put(p, Check(T("Monatlich am 3. Mittwoch um 12 Uhr (nach dem Patchday; verpasste Läufe werden nachgeholt)",
                                        "Monthly on the 3rd Wednesday at noon (after Patch Tuesday; missed runs are caught up)"), 106, Settings.GetBool("Automatik.Monatlich", true)), TLR);
            chkBoot = Put(p, Check(T("Zusätzlich 10 Minuten nach jedem Systemstart (erkennt Rücksetzungen durch Funktionsupdates)",
                                     "Additionally 10 minutes after every system start (detects resets caused by feature updates)"), 130, Settings.GetBool("Automatik.Systemstart", false)), TLR);
            Put(p, Make.Section(T("Umfang", "Scope"), X, 164, W), TLR);
            chkAutoPrivacy = Put(p, Check(T("Datenschutz-Einstellungen erneut anwenden (Auswahl im Reiter „Datenschutz“)", "Re-apply privacy settings (selection in the “Privacy” tab)"),
                                          188, Settings.GetBool("Automatik.Datenschutz", true)), TLR);
            chkAutoProfile = Put(p, Check(T("Behobene HardeningKitty-Punkte prüfen und bei Bedarf erneut setzen", "Check fixed HardeningKitty items and re-apply them if needed"),
                                          212, Settings.GetBool("Automatik.Profil", true)), TLR);
            chkAutoReport = Put(p, Check(T("Einmal im Monat einen vollständigen Prüfbericht erstellen (Prüfliste aus dem Reiter „Prüfen“)",
                                           "Create a full audit report once a month (finding list from the “Audit” tab)"), 236, Settings.GetBool("Automatik.Bericht", true)), TLR);
            foreach (CheckBox c in new[] { chkAutoPrivacy, chkAutoProfile, chkAutoReport }) c.CheckedChanged += (s, e) => SaveAutoScope();

            Put(p, Make.Section("Status", X, 272, W), TLR);
            txtAutoState = Put(p, InfoBox(296, 200), All);
            txtAutoState.Text = "…";
            Put(p, Make.Text(T("Die Aufgabe startet das Programm aus „" + Automatik.InstallFolder + "“ (nur Administratoren können dort ändern). " +
                               "Es wird beim Einrichten dorthin kopiert und danach automatisch aktuell gehalten.",
                               "The task starts the program from “" + Automatik.InstallFolder + "” (only administrators can change files there). " +
                               "It is copied there during setup and kept up to date automatically."), X, 502, W, 40), BLR);
            btnAutoSetup = Put(p, Make.Primary(T("Aufgabe einrichten", "Set up task"), X, 548, 240, 36), BL);
            btnAutoSetup.Click += (s, e) => OnAutoSetup();
            btnAutoRun = Put(p, Make.Secondary(T("Jetzt ausführen", "Run now"), 260, 550, 160, 32), BL);
            btnAutoRun.Click += (s, e) => OnAutoRun();
            btnAutoRemove = Put(p, Make.Secondary(T("Aufgabe entfernen", "Remove task"), 428, 550, 170, 32), BL);
            btnAutoRemove.Click += (s, e) => OnAutoRemove();
            var btnAutoLog = Put(p, Make.Secondary(T("Protokolle", "Logs"), X + W - 170, 550, 170, 32), BR);
            btnAutoLog.Click += (s, e) =>
            {
                string logs = Path.Combine(Automatik.DataFolder, "Logs");
                OpenFolder(Directory.Exists(logs) ? logs : Directory.Exists(Automatik.DataFolder) ? Automatik.DataFolder : AppHost.ProgramFolder);
            };
            btnAutoRun.Enabled = btnAutoRemove.Enabled = false;
            return p;
        }

        void SaveAutoScope()
        {
            Settings.SetBool("Automatik.Datenschutz", chkAutoPrivacy.Checked);
            Settings.SetBool("Automatik.Profil", chkAutoProfile.Checked);
            Settings.SetBool("Automatik.Bericht", chkAutoReport.Checked);
            if (!Automatik.SyncSettings())
                Show(txtAutoState, "✘ " + T("Die Änderung konnte nicht in die Automatik übernommen werden (Details im Log).",
                                             "The change could not be passed on to the automation (details in the log)."), StatusKind.Error);
        }

        void RefreshAuto()
        {
            Aufgaben.Info info;
            string error = null, status = null;
            try { info = Aufgaben.Describe(Automatik.TaskName); }
            catch (Exception ex)
            {
                info = new Aufgaben.Info();
                error = ex.Message;
                Log.Write("Aufgabenplanung nicht lesbar: " + ex);
            }
            try { if (File.Exists(Automatik.StatusFile)) status = File.ReadAllText(Automatik.StatusFile, Encoding.UTF8); }
            catch (Exception ex) { Log.Write("Status der Automatik nicht lesbar: " + ex.Message); }
            int profile = Haertungsprofil.Count(), groups = Datenschutz.Selected().Count;
            Log.Write("Automatik: Aufgabe " + (info.Exists ? (info.Enabled ? "aktiv" : "deaktiviert") : "nicht eingerichtet") +
                      ", Profil " + profile + " Einträge, " + groups + " Datenschutz-Gruppen");
            Ui(() =>
            {
                var sb = new StringBuilder();
                StatusKind kind;
                if (error != null) { sb.Append("✘ " + T("Die Aufgabenplanung ist nicht lesbar: ", "Task Scheduler cannot be read: ") + error); kind = StatusKind.Error; }
                else if (!info.Exists) { sb.Append(T("Die Aufgabe ist noch nicht eingerichtet.", "The task has not been set up yet.")); kind = StatusKind.Warning; }
                else
                {
                    sb.Append((info.Enabled ? "✔ " + T("Die Aufgabe ist eingerichtet", "The task is set up") : "✘ " + T("Die Aufgabe ist deaktiviert", "The task is disabled")) +
                              (info.Next.Year > 2000 ? T(" – nächster Lauf ", " – next run ") + info.Next.ToString("g") : "") + ".");
                    if (info.Last.Year > 2000)
                        sb.Append(T("\nLetzter Start ", "\nLast start ") + info.Last.ToString("g") + " – " +
                                  (info.LastResult == 0 ? T("ohne Fehler beendet", "finished without errors") : info.LastResult == 0x41301 ? T("läuft gerade", "currently running")
                                   : T("Ergebniscode 0x", "result code 0x") + ((uint)info.LastResult).ToString("X")) + ".");
                    if (info.Command != "" && !info.Command.StartsWith(Automatik.InstallFolder, StringComparison.OrdinalIgnoreCase))
                        sb.Append(T("\nHinweis: Die Aufgabe startet „" + info.Command + "“ – bitte „Aufgabe aktualisieren“.",
                                    "\nNote: the task starts “" + info.Command + "” – please click “Update task”."));
                    kind = info.Enabled ? StatusKind.Success : StatusKind.Warning;
                }
                sb.Append(T("\nUmfang: " + groups + " Datenschutz-Gruppen, " + profile + " Einträge im Härtungsprofil.",
                            "\nScope: " + groups + " privacy groups, " + profile + " entries in the hardening profile."));
                if (status != null) sb.Append(T("\n\nLetzter automatischer Lauf: ", "\n\nLast automatic run: ") + status);
                Show(txtAutoState, sb.ToString(), kind);
                btnAutoSetup.Text = info.Exists ? T("Aufgabe aktualisieren", "Update task") : T("Aufgabe einrichten", "Set up task");
                btnAutoRun.Enabled = btnAutoRemove.Enabled = info.Exists && SystemInfo.IsAdmin;
            });
        }

        void OnAutoSetup()
        {
            bool monthly = chkMonthly.Checked, boot = chkBoot.Checked;
            if (!monthly && !boot) { Show(txtAutoState, T("Bitte mindestens einen Zeitpunkt auswählen.", "Please select at least one schedule."), StatusKind.Warning); return; }
            string when = (monthly ? T("monatlich am 3. Mittwoch", "monthly on the 3rd Wednesday") : "") + (monthly && boot ? T(" und ", " and ") : "") +
                          (boot ? T("nach jedem Systemstart", "after every system start") : "");
            if (!Confirm(T("Die geplante Aufgabe „" + Automatik.TaskName + "“ wird eingerichtet. Sie läuft als SYSTEM " + when + ".\n\n",
                           "The scheduled task “" + Automatik.TaskName + "” will be set up. It runs as SYSTEM " + when + ".\n\n") +
                         (Automatik.IsInstalledCopy ? "" : T("Dafür wird das Programm samt HardeningKitty nach\n" + Automatik.InstallFolder + "\nkopiert.\n\n",
                                                             "For this, the program including HardeningKitty is copied to\n" + Automatik.InstallFolder + ".\n\n")) +
                         T("Fortfahren?", "Continue?"), T("Automatik einrichten", "Set up automation")))
                return;
            Settings.SetBool("Automatik.Monatlich", monthly);
            Settings.SetBool("Automatik.Systemstart", boot);
            SaveAutoScope();
            RunInBackground(T("Richte die Automatik ein …", "Setting up the automation …"), () =>
            {
                Automatik.Setup(monthly, boot, s => SetFooterStatus(s));
                Aufgaben.Info info = Aufgaben.Describe(Automatik.TaskName);
                RefreshAuto();
                Ui(() =>
                {
                    if (info.Exists)
                        MessageBox.Show(this, T("Die Automatik ist eingerichtet.\n\nZum Testen „Jetzt ausführen“ klicken und nach ein bis zwei Minuten den Status ansehen.",
                                                        "The automation is set up.\n\nTo test it, click “Run now” and check the status after one or two minutes."),
                            AppHost.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show(this, T("Die Aufgabe ist nach dem Einrichten nicht vorhanden. Details im Log.", "The task does not exist after setup. Details in the log."),
                            AppHost.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            });
        }

        void OnAutoRun()
        {
            if (!Confirm(T("Die Automatik jetzt einmal ausführen? Sie läuft als SYSTEM im Hintergrund und ändert dabei die ausgewählten Einstellungen.",
                           "Run the automation once now? It runs as SYSTEM in the background and changes the selected settings."), T("Jetzt ausführen", "Run now"))) return;
            RunInBackground(T("Starte die Aufgabe …", "Starting the task …"), () =>
            {
                Aufgaben.Run(Automatik.TaskName);
                Ui(() => Show(txtAutoState, T("Die Aufgabe wurde gestartet. Das Ergebnis erscheint hier nach ein bis zwei Minuten (Programm neu öffnen).",
                                              "The task was started. The result appears here after one or two minutes (reopen the program)."), StatusKind.Info));
            });
        }

        void OnAutoRemove()
        {
            if (!Confirm(T("Die geplante Aufgabe „" + Automatik.TaskName + "“ entfernen?\n\nGesetzte Einstellungen bleiben bestehen. Der Ordner\n" + Automatik.InstallFolder +
                           "\nbleibt ebenfalls erhalten und kann bei Bedarf von Hand gelöscht werden.",
                           "Remove the scheduled task “" + Automatik.TaskName + "”?\n\nApplied settings remain. The folder\n" + Automatik.InstallFolder +
                           "\nis also kept and can be deleted manually if needed."), T("Aufgabe entfernen", "Remove task"))) return;
            RunInBackground(T("Entferne die Aufgabe …", "Removing the task …"), () =>
            {
                Aufgaben.Delete(Automatik.TaskName);
                RefreshAuto();
            });
        }

        void ApplyAdminState()
        {
            if (SystemInfo.IsAdmin) return;
            foreach (Button b in new[] { btnFix, btnRestore, btnPrivacyApply, btnPrivacyRestore, btnAutoSetup, btnAutoRun, btnAutoRemove }) b.Enabled = false;
            string text = T("Das Programm läuft ohne Administratorrechte: Prüfen ist möglich (aber unvollständig), Ändern nicht. Bitte das Programm als Administrator starten.",
                            "The program is running without administrator rights: auditing is possible (but incomplete), changes are not. Please start the program as administrator.");
            Show(txtFixState, text, StatusKind.Warning);
            Show(txtPrivacyState, text, StatusKind.Warning);
        }
    }
}

// AppFrame – gemeinsames Grundgerüst für Windows-Tools von Maximilian Meyn
// Wird unverändert in jedes Projekt kopiert (Quellcode\AppFrame.cs). Änderungen hier in der Vorlage pflegen.
//
// Enthält:
//   AppHost   – Programmstart, Fehlerbehandlung, Programminfos aus den Assembly-Attributen, Danksagungen (Credits)
//   Lang      – Sprache Deutsch/Englisch: Lang.T("Deutsch", "English"); Umschalten über die Fußzeile
//   Log       – Tagesprotokoll in <Programmordner>\Logs (Ersatz: %TEMP%\<Name>\Logs), immer Deutsch
//   Theme     – Farben und Schriften
//   AppForm   – Hauptfenster mit Kopfzeile, Inhaltsbereich, Fußzeile (Version, Log, Info, Sprache, Kaffee, Autor);
//               wahlweise mit fester Größe oder größenveränderbar (breite Listen-Oberflächen)
//   Make      – Fabrikmethoden für einheitliche Buttons, Überschriften und Texte
//   ImageBox  – Bild in mehreren Größen, zeichnet jeweils die passendste (Kopfzeile, Info-Dialog, Widmung)
//   DetailCard – Detailkarte für den gewählten Listeneintrag (Titel, Marke, Unterzeile, Wertefelder, Text, Einzelpunkte)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MeynTools
{
    static class AppHost
    {
        public const string Author = "Maximilian Meyn";
        public const string DonateUrl = "https://buymeacoffee.com/meyn";

        static bool restartUi;

        static Assembly App { get { return Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly(); } }

        public static string Name
        {
            get
            {
                var a = (AssemblyTitleAttribute)Attribute.GetCustomAttribute(App, typeof(AssemblyTitleAttribute));
                return a != null ? a.Title : "Tool";
            }
        }

        public static string Description
        {
            get
            {
                var a = (AssemblyDescriptionAttribute)Attribute.GetCustomAttribute(App, typeof(AssemblyDescriptionAttribute));
                return a != null ? a.Description : "";
            }
        }

        // Beschreibung für den Info-Dialog in der aktuellen Sprache (ohne Angabe: AssemblyDescription)
        public static string DescriptionEnglish { get; set; }

        public static string Version { get { return App.GetName().Version.ToString(3); } }

        public static string ProgramFolder { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        // Projekte, auf denen das Tool aufbaut – erscheinen im Info-Dialog mit Link
        public class Credit { public string Name, Text, Url; }
        public static readonly List<Credit> Credits = new List<Credit>();

        public static void AddCredit(string name, string text, string url)
        {
            Credits.Add(new Credit { Name = name, Text = text, Url = url });
        }

        // Programmsymbol: app.ico wird von build.cmd als Ressource „app.ico“ eingebettet (alle Größen)
        public static Icon LoadIcon(int size)
        {
            try
            {
                using (Stream s = App.GetManifestResourceStream("app.ico"))
                    if (s != null) return new Icon(s, size, size);
            }
            catch (Exception ex) { Log.Write("Icon: " + ex.Message); }
            return null;
        }

        // Bild aus Quellcode\Bilder\ (build.cmd bettet jede PNG dort unter ihrem Dateinamen ein); null, wenn es fehlt
        public static Image LoadImage(string name)
        {
            try
            {
                using (Stream s = App.GetManifestResourceStream(name))
                    if (s != null) using (var img = Image.FromStream(s)) return new Bitmap(img);
            }
            catch (Exception ex) { Log.Write("Bild " + name + ": " + ex.Message); }
            return null;
        }

        // Optional: eigenes Bild in der Kopfzeile und im Info-Dialog statt des Programmsymbols
        // (mehrere Größen desselben Motivs angeben – gezeichnet wird jeweils die passendste)
        public static Image[] HeaderImages { get; set; }
        public static Image[] AboutImages { get; set; }

        // Optional: diskrete Widmung – kleines Bild in der Fußzeile (Tooltip, Klick öffnet den Info-Dialog)
        // und eine Zeile unten im Info-Dialog
        public static string DedicationGerman { get; set; }
        public static string DedicationEnglish { get; set; }
        public static string DedicationText
        {
            get { return DedicationGerman == null ? null : Lang.T(DedicationGerman, DedicationEnglish ?? DedicationGerman); }
        }
        public static Image[] DedicationImages { get; set; }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Link nicht geöffnet (" + url + "): " + ex.Message); }
        }

        public static void Run(Func<Form> createMainForm)
        {
            Log.Init(Name);
            Lang.Load();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Fatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject as Exception);

            Log.Write("==================== Programmstart ====================");
            Log.Write(Name + " " + Version + "  –  entwickelt von " + Author);
            Log.Write("Rechner:  " + Environment.MachineName + "   Benutzer: " + Environment.UserDomainName + "\\" + Environment.UserName);
            Log.Write("Windows:  " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64 Bit)" : " (32 Bit)")
                      + "   Prozess: " + (Environment.Is64BitProcess ? "64 Bit" : "32 Bit"));
            Log.Write("Programm: " + Application.ExecutablePath);
            Log.Write("Sprache:  " + (Lang.IsEnglish ? "Englisch" : "Deutsch"));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Beim Sprachwechsel wird das Hauptfenster neu aufgebaut – ohne Neustart und ohne erneute UAC-Abfrage
            do
            {
                restartUi = false;
                Application.Run(createMainForm());
            } while (restartUi);
            Log.Write("Programmende");
        }

        public static void RestartUi(Form mainForm)
        {
            restartUi = true;
            mainForm.Close();
        }

        static void Fatal(Exception ex)
        {
            Log.Write("UNERWARTETER FEHLER: " + ex);
            MessageBox.Show(Lang.T("Unerwarteter Fehler:\n", "Unexpected error:\n") + (ex != null ? ex.Message : "?") +
                            Lang.T("\n\nDetails im Log:\n", "\n\nDetails in the log:\n") + Log.FilePath,
                Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Oberflächensprache: Deutsch, wenn Windows deutsch ist, sonst Englisch; Wahl wird in Einstellungen\Sprache.txt gespeichert
    static class Lang
    {
        public static bool IsEnglish { get; private set; }

        static string FilePath { get { return Path.Combine(AppHost.ProgramFolder, @"Einstellungen\Sprache.txt"); } }

        public static string T(string german, string english) { return IsEnglish ? english : german; }

        public static void Load()
        {
            IsEnglish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de";
            try
            {
                if (File.Exists(FilePath)) IsEnglish = File.ReadAllText(FilePath).Trim().ToLowerInvariant() == "en";
            }
            catch (Exception ex) { Log.Write("Sprache nicht lesbar: " + ex.Message); }
        }

        public static void Set(bool english)
        {
            IsEnglish = english;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, english ? "en" : "de");
            }
            catch (Exception ex) { Log.Write("Sprache nicht gespeichert: " + ex.Message); }
            Log.Write("Sprache gewechselt: " + (english ? "Englisch" : "Deutsch"));
        }
    }

    static class Log
    {
        static readonly object Sync = new object();
        public static string FilePath { get; private set; }

        public static void Init(string appName)
        {
            string safeName = new string(appName.Where(c => char.IsLetterOrDigit(c)).ToArray());
            string fileName = safeName + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log";
            string[] dirs =
            {
                Path.Combine(AppHost.ProgramFolder, "Logs"),
                Path.Combine(Path.GetTempPath(), safeName, "Logs")
            };
            foreach (string dir in dirs)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, fileName);
                    File.AppendAllText(path, "", Encoding.UTF8);
                    FilePath = path;
                    return;
                }
                catch { }
            }
        }

        public static void Write(string message)
        {
            if (FilePath == null) return;
            lock (Sync)
            {
                try
                {
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    enum StatusKind { Info, Success, Warning, Error }

    static class Theme
    {
        // Akzentfarbe pro Tool in Main() setzen, z. B. Theme.Accent = Color.FromArgb(0, 95, 184);
        public static Color Accent = Color.FromArgb(0, 95, 184);
        // Kopfzeile: ohne Angabe in der Akzentfarbe; mit eigener Farbe bekommt sie unten eine feine Linie in der Akzentfarbe
        public static Color? Header = null;
        public static Color HeaderSubtitle = Color.FromArgb(225, 235, 245);
        public static readonly Color Background = Color.FromArgb(243, 243, 243);
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Color.FromArgb(218, 218, 218);
        public static readonly Color Text = Color.FromArgb(32, 32, 32);
        public static readonly Color TextMuted = Color.FromArgb(96, 96, 96);
        public static readonly Color Success = Color.FromArgb(16, 124, 16);
        public static readonly Color Warning = Color.FromArgb(157, 93, 0);
        public static readonly Color Error = Color.FromArgb(196, 43, 28);

        public static readonly Font Normal = new Font("Segoe UI", 9.75F);
        public static readonly Font Bold = new Font("Segoe UI", 9.75F, FontStyle.Bold);
        public static readonly Font Large = new Font("Segoe UI", 11F, FontStyle.Bold);
        public static readonly Font Section = new Font("Segoe UI Semibold", 10.5F);
        public static readonly Font Title = new Font("Segoe UI Semibold", 15F);
        public static readonly Font Small = new Font("Segoe UI", 8.25F);
        public static readonly Font Mono = new Font("Consolas", 12F);
        public static readonly Font CardTitle = new Font("Segoe UI Semibold", 11.25F);

        // Windows-Symbolschrift (Segoe Fluent Icons unter Windows 11, sonst Segoe MDL2 Assets); null, wenn keine vorhanden ist
        public static readonly Font Icons = FindIconFont(9.75F);

        static Font FindIconFont(float size)
        {
            try
            {
                var installed = new System.Drawing.Text.InstalledFontCollection().Families;
                foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                    if (installed.Any(f => f.Name == name)) return new Font(name, size);
            }
            catch (Exception ex) { Log.Write("Symbolschrift nicht gefunden: " + ex.Message); }
            return null;
        }

        public static Color For(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Success: return Success;
                case StatusKind.Warning: return Warning;
                case StatusKind.Error: return Error;
                default: return Text;
            }
        }
    }

    // Hauptfenster: Kopfzeile (Name + Untertitel), Inhalt (Content), Fußzeile (Status, Log, Info, Sprache, Kaffee, Autor)
    class AppForm : Form
    {
        protected readonly Panel Content = new Panel();
        readonly Label footerStatus = new Label();
        readonly ToolTip tips = new ToolTip();

        public AppForm(string subtitle, Size contentSize) : this(subtitle, contentSize, false) { }

        // resizable = true: Fenster lässt sich vergrößern (Mindestgröße = Startgröße); Inhalte mit Anchor an den Rändern ausrichten
        public AppForm(string subtitle, Size contentSize, bool resizable)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppHost.Name;
            Font = Theme.Normal;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;
            MaximizeBox = resizable;
            Icon appIcon = AppHost.LoadIcon(32);
            if (appIcon != null) Icon = appIcon;

            // Kopfzeile: Symbol (oder eigenes Bild), Name, Untertitel
            var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Theme.Header ?? Theme.Accent };
            if (Theme.Header.HasValue)
                header.Paint += (s, e) => { using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, 0, header.Height - 2, header.Width, 2); };
            int textLeft = 18;
            Icon headerIcon = AppHost.LoadIcon(64);
            Image[] headerImages = AppHost.HeaderImages ?? (headerIcon != null ? new Image[] { headerIcon.ToBitmap() } : null);
            if (headerImages != null)
            {
                header.Controls.Add(new ImageBox(headerImages) { Location = new Point(18, 12), Size = new Size(44, 44) });
                textLeft = 72;
            }
            header.Controls.Add(new Label { Text = AppHost.Name, Font = Theme.Title, ForeColor = Color.White, AutoSize = true, Location = new Point(textLeft, 8) });
            header.Controls.Add(new Label { Text = subtitle, ForeColor = Theme.HeaderSubtitle, AutoSize = true, Location = new Point(textLeft + 2, 42) });

            // Fußzeile: links Version/Status, rechts Log · Info · Sprache · Kaffee · „Mit ♥ von …“
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Theme.Surface, Padding = new Padding(12, 0, 8, 0) };
            footer.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Theme.Border), 0, 0, footer.Width, 0);
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            right.Controls.Add(FooterLink("Log", OpenLog, Lang.T("Protokoll öffnen", "Open log")));
            right.Controls.Add(FooterLink("Info", ShowAbout, Lang.T("Über dieses Programm", "About this program")));
            right.Controls.Add(FooterLink(Lang.IsEnglish ? "DE" : "EN", SwitchLanguage, Lang.T("Switch to English", "Auf Deutsch umschalten")));
            var coffee = new CoffeeLink { Margin = new Padding(2, 0, 8, 0) };
            tips.SetToolTip(coffee, Lang.T("Gefällt dir das Tool? Spendier mir einen Kaffee", "Like this tool? Buy me a coffee"));
            right.Controls.Add(coffee);
            right.Controls.Add(FooterText(Lang.T("Mit", "Made with"), Theme.TextMuted, new Padding(0, 1, 0, 0)));
            right.Controls.Add(FooterText("♥", Theme.Accent, new Padding(0, 1, 0, 0)));
            right.Controls.Add(FooterText(Lang.T("von ", "by ") + AppHost.Author, Theme.TextMuted, new Padding(0, 1, 0, 0)));
            if (AppHost.DedicationImages != null)
            {
                var dedication = new ImageBox(AppHost.DedicationImages) { Size = new Size(18, 18), Margin = new Padding(6, 0, 0, 0), Cursor = Cursors.Hand };
                dedication.Click += (s, e) => ShowAbout();
                tips.SetToolTip(dedication, AppHost.DedicationText);
                right.Controls.Add(dedication);
            }
            footerStatus.Dock = DockStyle.Fill;
            footerStatus.TextAlign = ContentAlignment.MiddleLeft;
            footerStatus.Font = Theme.Small;
            footerStatus.ForeColor = Theme.TextMuted;
            footerStatus.Text = "v" + AppHost.Version;
            footer.Controls.Add(footerStatus);
            footer.Controls.Add(right);

            // Inhalt
            Content.Dock = DockStyle.Fill;
            Content.Padding = new Padding(20, 16, 20, 12);
            // Endgültige Größe schon jetzt setzen: Anchor (Right/Bottom) merkt sich die Abstände beim Einfügen
            Content.Size = new Size(contentSize.Width + 40, contentSize.Height + 28);

            Controls.Add(Content);   // Fill zuerst, damit Kopf- und Fußzeile ihren Platz behalten
            Controls.Add(footer);
            Controls.Add(header);
            ClientSize = new Size(contentSize.Width + 40, contentSize.Height + 28 + header.Height + footer.Height);
            if (resizable) Load += (s, e) => MinimumSize = Size;
        }

        // Am Ende des Konstruktors der abgeleiteten Klasse aufrufen
        protected void FinishLayout()
        {
            ResumeLayout(false);
            PerformLayout();
        }

        LinkLabel FooterLink(string text, Action onClick, string tip)
        {
            var link = new LinkLabel
            {
                Text = text, AutoSize = true, Font = Theme.Small, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 1, 8, 0)
            };
            link.LinkClicked += (s, e) => onClick();
            tips.SetToolTip(link, tip);
            return link;
        }

        static Label FooterText(string text, Color color, Padding margin)
        {
            return new Label { Text = text, AutoSize = true, Font = Theme.Small, ForeColor = color, Margin = margin };
        }

        // Dezentes Kaffeetassen-Symbol, führt zur Buy-me-a-coffee-Seite
        class CoffeeLink : Control
        {
            bool hover;

            public CoffeeLink()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                Size = new Size(18, 18);
                Cursor = Cursors.Hand;
                BackColor = Color.Transparent;
                MouseEnter += (s, e) => { hover = true; Invalidate(); };
                MouseLeave += (s, e) => { hover = false; Invalidate(); };
                Click += (s, e) => AppHost.OpenUrl(AppHost.DonateUrl);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float k = Width / 18f;
                Color c = hover ? Theme.Accent : Theme.TextMuted;
                using (var pen = new Pen(c, 1.4f * k))
                using (var brush = new SolidBrush(Color.FromArgb(hover ? 60 : 35, c)))
                {
                    // Dampf
                    g.DrawBezier(pen, 6 * k, 2 * k, 5 * k, 3.5f * k, 7 * k, 4.5f * k, 6 * k, 6 * k);
                    g.DrawBezier(pen, 9.5f * k, 2 * k, 8.5f * k, 3.5f * k, 10.5f * k, 4.5f * k, 9.5f * k, 6 * k);
                    // Tasse mit Henkel und Untertasse
                    var cup = new GraphicsPath();
                    cup.AddLine(3 * k, 8 * k, 13 * k, 8 * k);
                    cup.AddBezier(13 * k, 8 * k, 13 * k, 13 * k, 11 * k, 15 * k, 8 * k, 15 * k);
                    cup.AddBezier(8 * k, 15 * k, 5 * k, 15 * k, 3 * k, 13 * k, 3 * k, 8 * k);
                    cup.CloseFigure();
                    g.FillPath(brush, cup);
                    g.DrawPath(pen, cup);
                    g.DrawArc(pen, 12 * k, 9 * k, 4 * k, 3.5f * k, -90, 180);
                    g.DrawLine(pen, 2 * k, 16.5f * k, 14 * k, 16.5f * k);
                }
            }
        }

        void OpenLog()
        {
            if (Log.FilePath != null && File.Exists(Log.FilePath)) Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
        }

        void SwitchLanguage()
        {
            if (!Content.Enabled) return;   // nicht während einer Hintergrundarbeit
            Lang.Set(!Lang.IsEnglish);
            AppHost.RestartUi(this);
        }

        void ShowAbout()
        {
            using (var f = new AboutForm()) f.ShowDialog(this);
        }

        // Info-Dialog: Name, Version, Beschreibung, Autor, Kaffee, Danksagungen mit Links
        class AboutForm : Form
        {
            public AboutForm()
            {
                AutoScaleDimensions = new SizeF(96F, 96F);
                AutoScaleMode = AutoScaleMode.Dpi;
                Text = Lang.T("Info", "About");
                Font = Theme.Normal;
                BackColor = Theme.Surface;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                const int w = 440;
                int y = 18;

                Icon icon = AppHost.LoadIcon(64);
                Image[] images = AppHost.AboutImages ?? (icon != null ? new Image[] { icon.ToBitmap() } : null);
                if (images != null) Controls.Add(new ImageBox(images) { Location = new Point(20, y), Size = new Size(48, 48) });
                Controls.Add(new Label { Text = AppHost.Name, Font = Theme.Title, AutoSize = true, Location = new Point(80, y) });
                Controls.Add(new Label { Text = Lang.T("Version ", "Version ") + AppHost.Version, ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(82, y + 32) });
                y += 66;

                string description = Lang.IsEnglish && !string.IsNullOrEmpty(AppHost.DescriptionEnglish) ? AppHost.DescriptionEnglish : AppHost.Description;
                y = AddText(description, y, Theme.Text) + 10;

                var author = new FlowLayoutPanel { Location = new Point(20, y), Size = new Size(w, 22), WrapContents = false };
                author.Controls.Add(new Label { Text = Lang.T("Mit", "Made with"), AutoSize = true, Margin = new Padding(0) });
                author.Controls.Add(new Label { Text = "♥", ForeColor = Theme.Accent, AutoSize = true, Margin = new Padding(0) });
                author.Controls.Add(new Label { Text = Lang.T("von ", "by ") + AppHost.Author + "   © " + DateTime.Now.Year, AutoSize = true, Margin = new Padding(0) });
                Controls.Add(author);
                y += 26;
                y = AddLink("☕  " + Lang.T("Einen Kaffee spendieren", "Buy me a coffee"), AppHost.DonateUrl, y) + 12;

                if (AppHost.Credits.Count > 0)
                {
                    Controls.Add(new Label { Text = Lang.T("Danke an", "Thanks to"), Font = Theme.Section, AutoSize = true, Location = new Point(20, y) });
                    y += 26;
                    foreach (AppHost.Credit c in AppHost.Credits)
                    {
                        Controls.Add(new Label { Text = c.Name, Font = Theme.Bold, AutoSize = true, Location = new Point(20, y) });
                        y = AddText(c.Text, y + 20, Theme.TextMuted);
                        y = AddLink(c.Url, c.Url, y) + 8;
                    }
                }

                y += 8;
                var ok = Make.Primary("OK", 20 + w - 110, y, 110, 32);
                ok.DialogResult = DialogResult.OK;
                Controls.Add(ok);

                // Widmung: leise links neben dem OK-Knopf
                if (!string.IsNullOrEmpty(AppHost.DedicationText))
                {
                    int x = 20;
                    if (AppHost.DedicationImages != null)
                    {
                        Controls.Add(new ImageBox(AppHost.DedicationImages) { Location = new Point(x, y + 2), Size = new Size(28, 28) });
                        x += 36;
                    }
                    Controls.Add(new Label { Text = AppHost.DedicationText, ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(x, y + 7) });
                }
                AcceptButton = CancelButton = ok;
                ClientSize = new Size(w + 40, y + 48);
            }

            int AddText(string text, int y, Color color)
            {
                var l = new Label { Text = text, ForeColor = color, Location = new Point(20, y), MaximumSize = new Size(440, 0), AutoSize = true };
                Controls.Add(l);
                return y + TextRenderer.MeasureText(text, Font, new Size(440, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
            }

            int AddLink(string text, string url, int y)
            {
                var l = new LinkLabel { Text = text, AutoSize = true, Location = new Point(20, y), LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline };
                l.LinkClicked += (s, e) => AppHost.OpenUrl(url);
                Controls.Add(l);
                return y + 22;
            }
        }

        // Kurzer Statustext in der Fußzeile
        protected void SetFooterStatus(string text)
        {
            Ui(() => footerStatus.Text = text);
        }

        // Führt Code im UI-Thread aus (aus Hintergrund-Threads aufrufen)
        protected void Ui(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) Invoke(action); else action();
        }

        // Führt längere Arbeit im Hintergrund aus; der Inhalt ist währenddessen gesperrt
        protected void RunInBackground(string busyText, Action work)
        {
            Content.Enabled = false;
            UseWaitCursor = true;
            SetFooterStatus(busyText);
            new Thread(() =>
            {
                try { work(); }
                catch (Exception ex)
                {
                    Log.Write("FEHLER: " + ex);
                    Ui(() => MessageBox.Show(this, Lang.T("Fehler: ", "Error: ") + ex.Message + Lang.T("\n\nDetails im Log.", "\n\nDetails in the log."), AppHost.Name,
                        MessageBoxButtons.OK, MessageBoxIcon.Error));
                }
                finally
                {
                    Ui(() => { Content.Enabled = true; UseWaitCursor = false; footerStatus.Text = "v" + AppHost.Version; });
                }
            }) { IsBackground = true }.Start();
        }

        protected bool Confirm(string text, string title)
        {
            return MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
    }

    // Einheitliche Bausteine für den Inhaltsbereich
    static class Make
    {
        public static Label Section(string text, int x, int y, int width)
        {
            return new Label { Text = text, Font = Theme.Section, ForeColor = Theme.Text, Location = new Point(x, y), Size = new Size(width, 22) };
        }

        public static Label Text(string text, int x, int y, int width, int height)
        {
            return new Label { Text = text, ForeColor = Theme.TextMuted, Location = new Point(x, y), Size = new Size(width, height) };
        }

        public static Button Primary(string text, int x, int y, int width, int height)
        {
            var b = new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(width, height), Font = Theme.Bold,
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(Theme.Accent, 0.1F);
            b.EnabledChanged += (s, e) => b.BackColor = b.Enabled ? Theme.Accent : Theme.Border;
            return b;
        }

        public static Button Secondary(string text, int x, int y, int width, int height)
        {
            var b = new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.Text, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Theme.Background;
            return b;
        }

        public static LinkLabel Link(string text, string url, int x, int y)
        {
            var l = new LinkLabel
            {
                Text = text, AutoSize = true, Location = new Point(x, y), LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            l.LinkClicked += (s, e) => AppHost.OpenUrl(url);
            return l;
        }

        // Für größenveränderbare Fenster: eine Seite in fester Entwurfsgröße anlegen. Steuerelemente mit Anchor werden relativ zu
        // dieser Größe platziert; mit Host() in ihren Container (z. B. TabPage) setzen, dann wächst sie mit dem Fenster mit.
        public static Panel Page(Size designSize)
        {
            return new Panel { Size = designSize, BackColor = Theme.Background, Tag = designSize };
        }

        // Setzt eine mit Page() angelegte Seite in einen Container. Bewusst kein Dock = Fill: Ein Container ist beim Aufbau oft noch
        // winzig (TabPage 200×100) – die Seite würde schrumpfen und Anchor-Abstände gingen verloren. Sie wird nie kleiner als ihre Entwurfsgröße.
        public static void Host(Control container, Panel page)
        {
            var design = (Size)page.Tag;
            page.Location = Point.Empty;
            container.Controls.Add(page);
            EventHandler fit = (s, e) =>
            {
                Size c = container.ClientSize;
                page.Size = new Size(Math.Max(design.Width, c.Width), Math.Max(design.Height, c.Height));
            };
            container.Resize += fit;
            container.HandleCreated += fit;
        }
    }

    // Statussymbole: Die Programme schreiben einfache Zeichen (✔ ✘ ✗ ○ – ! i) in ihre Texte – gezeigt werden sie als Glyphen der
    // Windows-Symbolschrift (Segoe Fluent Icons). Ohne Symbolschrift erscheint das Zeichen selbst.
    static class Symbols
    {
        public static readonly string[] Leading = { "✔", "✘", "✗" };

        public static string Glyph(string symbol)
        {
            if (Theme.Icons == null) return null;
            switch (symbol)
            {
                case "✔": return "";   // Häkchen
                case "✘": case "✗": return "";   // Kreuz
                case "○": return "";   // Kreis (offen)
                case "–": return "";   // Strich
                case "!": return "";   // Warndreieck
                case "i": return "";   // Info
            }
            return null;
        }

        // Symbol nach Statusfarbe (Erfolg, Warnung, Fehler, sonst Info)
        public static string ForColor(Color c)
        {
            return c == Theme.Success ? "✔" : c == Theme.Warning ? "!" : c == Theme.Error ? "✘" : "i";
        }

        // Farbe eines führenden Zeichens: ✔ grün, ✘ rot, sonst die übergebene Textfarbe
        public static Color ColorFor(string symbol, Color text)
        {
            return symbol == "✔" ? Theme.Success : symbol == "✘" || symbol == "✗" ? Theme.Error : text;
        }

        // Trennt ein führendes ✔/✘/✗ vom Text; null, wenn keins vorhanden ist
        public static string Split(string line, out string rest)
        {
            foreach (string s in Leading)
                if (line.StartsWith(s)) { rest = line.Substring(s.Length).TrimStart(); return s; }
            rest = line;
            return null;
        }

        public static void Draw(Graphics g, string symbol, Color color, Rectangle r)
        {
            string glyph = Glyph(symbol);
            TextRenderer.DrawText(g, glyph ?? symbol, glyph != null ? Theme.Icons : Theme.Normal, r, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        // Kleines Bild eines Symbols (z. B. für eine ImageList), Kantenglättung auf transparentem Grund
        // In Listen gefüllte Varianten (Kreis mit Haken/Kreuz) – dünne Linien sind bei 16 px kaum zu erkennen
        static string FilledGlyph(string symbol)
        {
            if (Theme.Icons == null) return null;
            switch (symbol)
            {
                case "✔": return "";   // Kreis mit Häkchen
                case "✘": case "✗": return "";   // Kreis mit Kreuz
                case "!": return "";   // Warndreieck
            }
            return Glyph(symbol);
        }

        public static Bitmap Image(string symbol, Color color, int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            string glyph = FilledGlyph(symbol);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                FontFamily family = glyph != null ? Theme.Icons.FontFamily : Theme.Normal.FontFamily;
                path.AddString(glyph ?? symbol, family, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
                // Symbol exakt einpassen und mittig setzen (Schriftmaße haben sonst unterschiedlich viel Rand)
                RectangleF b = path.GetBounds();
                if (b.Width > 0 && b.Height > 0)
                {
                    float target = size * (glyph == "" ? 0.5f : 0.8f);
                    float scale = target / Math.Max(b.Width, b.Height);
                    using (var m = new Matrix())
                    {
                        m.Translate(size / 2f, size / 2f);
                        m.Scale(scale, scale);
                        m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
                        path.Transform(m);
                    }
                    g.FillPath(brush, path);
                }
            }
            return bmp;
        }

        // Fügt einer ImageList das Symbol in dieser Farbe hinzu (einmalig) und gibt den Schlüssel zurück – für ListViewItem.ImageKey
        public static string Key(ImageList list, string symbol, Color color)
        {
            string key = symbol + color.ToArgb();
            if (!list.Images.ContainsKey(key)) list.Images.Add(key, Image(symbol, color, list.ImageSize.Width));
            return key;
        }

        // ImageList für eine Liste: Größe passend zur Schrift (16 px bei 100 %)
        public static ImageList ListImages(Control owner)
        {
            int size;
            using (Graphics g = owner.CreateGraphics()) size = (int)Math.Round(16 * g.DpiX / 96f);
            return new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
        }
    }

    // Statusmeldung: mehrzeiliger Text, Zeilen mit führendem ✔/✘ bekommen ein Windows-Symbol, Folgezeilen werden eingerückt.
    // Ersetzt Label/TextBox für Ergebnisse; scrollt bei langem Text, Kontextmenü „Kopieren“. Farbe über ForeColor.
    class StatusText : Panel
    {
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
        const int Indent = 22;

        public StatusText()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add(Lang.T("Kopieren", "Copy"), null, (s, e) => { if (Text.Length > 0) Clipboard.SetText(Text); });
            ContextMenuStrip = menu;
        }

        public override string Text
        {
            get { return base.Text; }
            set { base.Text = value ?? ""; Relayout(); }
        }

        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

        void Relayout()
        {
            int h = Render(null);
            AutoScrollMinSize = h > ClientSize.Height ? new Size(0, h) : Size.Empty;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Render(e.Graphics);
        }

        int Render(Graphics g)
        {
            int w = Math.Max(40, ClientSize.Width - (VerticalScroll.Visible ? 0 : 2));
            int y = AutoScrollPosition.Y, top = y, lineH = TextRenderer.MeasureText("Ag", Font).Height;
            bool indent = false;
            foreach (string raw in Text.Replace("\r\n", "\n").Split('\n'))
            {
                string rest;
                string symbol = Symbols.Split(raw, out rest);
                if (symbol != null) indent = true;
                int x = indent ? Indent : 0;
                if (symbol != null && g != null) Symbols.Draw(g, symbol, Symbols.ColorFor(symbol, ForeColor), new Rectangle(-1, y, 18, lineH));
                int h = rest.Length == 0 ? lineH : TextRenderer.MeasureText(rest, Font, new Size(w - x, int.MaxValue), Wrap).Height;
                if (g != null && rest.Length > 0) TextRenderer.DrawText(g, rest, Font, new Rectangle(x, y, w - x, h), ForeColor, Wrap);
                y += h;
            }
            return y - top;
        }
    }

    // Bild in mehreren Größen (z. B. 18 und 36 px): zeichnet die kleinste, die die Fläche füllt – exakt, wenn sie passt,
    // sonst hochwertig verkleinert. So bleiben nachgeschärfte kleine Bilder scharf und hohe DPI-Stufen sauber.
    class ImageBox : Control
    {
        readonly Image[] images;

        public ImageBox(Image[] images)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            this.images = images.Where(i => i != null).OrderBy(i => i.Width).ToArray();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (images.Length == 0) return;
            Image img = images.FirstOrDefault(i => i.Width >= Width && i.Height >= Height) ?? images.Last();
            float scale = Math.Min((float)Width / img.Width, (float)Height / img.Height);
            int w = (int)Math.Round(img.Width * scale), h = (int)Math.Round(img.Height * scale);
            var r = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            Graphics g = e.Graphics;
            g.InterpolationMode = w == img.Width ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, r);
        }
    }

    // Detailkarte für den gewählten Listeneintrag – neben oder unter einer Liste. Zeichnet selbst, im Stil eines Windows-Eigenschaftsfensters:
    //   Titel (halbfett, umbrechend)
    //   [Symbol] Status (in der Statusfarbe; Symbol aus Segoe Fluent Icons, nach Farbe oder führendem ✔/✘)
    //   Unterzeile (grau)
    //   Bezeichnung    Wert              (je Feld eine Zeile, Werte umbrechend)
    //   Text (z. B. Hinweis oder Beschreibung)
    //   [Symbol] Einzelpunkt …           (✔ ✘ ○ – als Windows-Symbole; ein- oder zweispaltig)
    // Lange Inhalte werden nie abgeschnitten: die Karte scrollt dann.
    class DetailCard : Panel
    {
        public class Field
        {
            public string Label, Value;
            public Field(string label, string value) { Label = label; Value = value; }
        }

        public class Item
        {
            public string Symbol, Text; public Color Color;
            public Item(string symbol, string text, Color color) { Symbol = symbol; Text = text; Color = color; }
        }

        const int Pad = 14;
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

        string placeholder = "", title, badge, subtitle, text;
        Color badgeColor, textColor;
        List<Field> fields = new List<Field>();
        List<Item> items = new List<Item>();
        bool relayouting;

        public DetailCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            AutoScroll = true;
        }

        // Hinweis, solange nichts gewählt ist
        public void ShowPlaceholder(string hint)
        {
            placeholder = hint;
            title = null;
            Relayout();
        }

        // Zurück zum Hinweis (z. B. wenn die Liste neu gefüllt wird)
        public void Reset()
        {
            title = null;
            Relayout();
        }

        public void ShowDetails(string title, string badge, Color badgeColor, string subtitle,
                                IEnumerable<Field> fields, string text, Color textColor, IEnumerable<Item> items)
        {
            this.title = title;
            this.badge = string.IsNullOrEmpty(badge) ? null : badge;
            this.badgeColor = badgeColor;
            this.subtitle = subtitle;
            this.fields = fields != null ? fields.ToList() : new List<Field>();
            this.text = text;
            this.textColor = textColor;
            this.items = items != null ? items.ToList() : new List<Item>();
            Relayout();
        }

        void Relayout()
        {
            if (relayouting) return;
            relayouting = true;
            try
            {
                AutoScrollPosition = Point.Empty;
                AutoScrollMinSize = new Size(0, Render(null));
            }
            finally { relayouting = false; }
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
        }

        // Mausrad funktioniert auch, ohne dass vorher in die Karte geklickt wurde
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (VerticalScroll.Visible) Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Render(e.Graphics);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        static string StatusSymbol(Color c) { return Symbols.ForColor(c); }

        static int DrawSymbol(Graphics g, string symbol, Color color, int x, int y, int lineHeight)
        {
            if (symbol == "○") color = Theme.TextMuted;   // offene Punkte zurückhaltend, sonst wirkt der Kreis wie ein Optionsfeld
            if (g != null) Symbols.Draw(g, symbol, color, new Rectangle(x, y, 18, lineHeight));
            return 22;
        }

        // Gibt die Höhe des Inhalts zurück; zeichnet, wenn g != null
        int Render(Graphics g)
        {
            int w = Math.Max(60, ClientSize.Width - 2 * Pad);
            int oy = AutoScrollPosition.Y;
            int y = Pad + 2;

            if (title == null)
            {
                y += Draw(g, placeholder, Theme.Normal, Theme.TextMuted, Pad, y, oy, w);
                return y + Pad;
            }

            // Titel, darunter Status (Symbol + Text in der Statusfarbe) und Unterzeile
            y += Draw(g, title, Theme.CardTitle, Theme.Text, Pad, y, oy, w) + 4;
            if (badge != null)
            {
                string symbol = StatusSymbol(badgeColor), label = badge;
                foreach (string s in new[] { "✔", "✘", "✗" })
                    if (label.StartsWith(s)) { symbol = s; label = label.Substring(s.Length).TrimStart(); }
                int lineH = TextRenderer.MeasureText("Ag", Theme.Normal).Height;
                int x = Pad + DrawSymbol(g, symbol, badgeColor, Pad - 2, y + oy, lineH) - 2;
                y += Draw(g, label, Theme.Normal, badgeColor, x, y, oy, w - (x - Pad)) + 2;
            }
            if (!string.IsNullOrEmpty(subtitle))
                y += Draw(g, subtitle, Theme.Normal, Theme.TextMuted, Pad, y, oy, w) + 2;

            // Eigenschaften: Bezeichnung links (grau), Wert rechts – wie im Eigenschaften-Fenster von Windows
            if (fields.Count > 0)
            {
                y += 14;
                int labelW = fields.Max(f => TextRenderer.MeasureText(f.Label, Theme.Normal).Width) + 16;
                labelW = Math.Min(labelW, w * 2 / 5);
                foreach (Field f in fields)
                {
                    int h = Draw(g, f.Label, Theme.Normal, Theme.TextMuted, Pad, y, oy, labelW - 8);
                    h = Math.Max(h, Draw(g, string.IsNullOrEmpty(f.Value) ? "–" : f.Value, Theme.Normal, Theme.Text, Pad + labelW, y, oy, w - labelW));
                    y += h + 5;
                }
            }

            if (!string.IsNullOrEmpty(text))
                y += 12 + Draw(g, text, Theme.Normal, textColor, Pad, y + 12, oy, w) + 2;

            // Einzelpunkte, bei genug Breite zweispaltig
            if (items.Count > 0)
            {
                y += 12;
                int cols = w >= 460 ? 2 : 1, colW = (w - 16 * (cols - 1)) / cols, rowY = y;
                int perCol = (items.Count + cols - 1) / cols, colBottom = y;
                int lineH = TextRenderer.MeasureText("Ag", Theme.Normal).Height;
                for (int c = 0; c < cols; c++)
                {
                    int yy = rowY;
                    for (int i = c * perCol; i < Math.Min(items.Count, (c + 1) * perCol); i++)
                    {
                        int x = Pad + c * (colW + 16);
                        DrawSymbol(g, items[i].Symbol, items[i].Color, x - 2, yy + oy, lineH);
                        yy += Draw(g, items[i].Text, Theme.Normal, items[i].Color == Theme.TextMuted ? Theme.TextMuted : Theme.Text, x + 20, yy, oy, colW - 20) + 4;
                    }
                    colBottom = Math.Max(colBottom, yy);
                }
                y = colBottom;
            }
            return y + Pad;
        }

        static int Draw(Graphics g, string s, Font f, Color c, int x, int y, int oy, int w)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            Size sz = TextRenderer.MeasureText(s, f, new Size(w, int.MaxValue), Wrap);
            if (g != null) TextRenderer.DrawText(g, s, f, new Rectangle(x, y + oy, w, sz.Height), c, Wrap);
            return sz.Height;
        }
    }
}

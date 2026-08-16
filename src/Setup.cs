using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// AM-IMG-Setup — installer for AM-IMG, visual design per the "Systemic Logic"
// design system (Electric Blue on white, Inter/Segoe UI, minimal corporate).
namespace UsbImager.Setup
{
    // ---------- design tokens ----------
    internal static class Ui
    {
        public static readonly Color Bg = ColorTranslator.FromHtml("#F9F9FF");
        public static readonly Color Card = Color.White;
        public static readonly Color Ink = ColorTranslator.FromHtml("#141B2B");
        public static readonly Color InkSoft = ColorTranslator.FromHtml("#434656");
        public static readonly Color InkFaint = ColorTranslator.FromHtml("#737688");
        public static readonly Color Primary = ColorTranslator.FromHtml("#0052FF");
        public static readonly Color PrimaryDark = ColorTranslator.FromHtml("#003EC7");
        public static readonly Color Outline = ColorTranslator.FromHtml("#C3C5D9");
        public static readonly Color Track = ColorTranslator.FromHtml("#E9EDFF");
        public static readonly Color Ok = ColorTranslator.FromHtml("#0E9F6E");

        private static string family;
        public static Font F(float size, FontStyle style)
        {
            if (family == null)
            {
                family = "Segoe UI";
                foreach (FontFamily ff in FontFamily.Families)
                    if (ff.Name == "Inter") { family = "Inter"; break; }
            }
            return new Font(family, size, style);
        }

        public static Button PrimaryButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Primary;
            b.ForeColor = Color.White;
            b.Font = F(9.75f, FontStyle.Bold);
            b.Size = new Size(130, 36);
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.MouseOverBackColor = PrimaryDark;
            return b;
        }

        public static Button SecondaryButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Outline;
            b.BackColor = Color.White;
            b.ForeColor = Ink;
            b.Font = F(9.75f, FontStyle.Regular);
            b.Size = new Size(110, 36);
            b.Cursor = Cursors.Hand;
            return b;
        }
    }

    // slim 6px progress bar, Electric Blue on pale track
    internal class SlimBar : Control
    {
        private double value; // 0..1
        public double Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }
        public SlimBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer, true); Height = 6; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(Ui.Track))
                g.FillRectangle(track, 0, 0, Width, Height);
            int w = (int)(Width * value);
            if (w > 0)
                using (var fill = new SolidBrush(Ui.Primary))
                    g.FillRectangle(fill, 0, 0, w, Height);
        }
    }

    // ---------- install/uninstall logic ----------
    internal class Installer
    {
        public const string AppName = "AM-IMG";
        public const string Version = "1.0.0";
        public const string Publisher = "Anirban Majumdar";
        public const string ExeName = "AM-IMG.exe";

        public bool TestMode;          // redirects everything under a temp dir + HKCU
        public string TestRoot;

        public string InstallDir
        {
            get
            {
                if (TestMode) return Path.Combine(TestRoot, "app");
                return Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles), AppName);
            }
        }

        private string StartMenuDir
        {
            get
            {
                if (TestMode) return Path.Combine(TestRoot, "startmenu");
                return Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonPrograms), AppName);
            }
        }

        private string DesktopDir
        {
            get
            {
                if (TestMode) return Path.Combine(TestRoot, "desktop");
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
        }

        private RegistryKey UninstallRoot()
        {
            var hive = TestMode ? Registry.CurrentUser : Registry.LocalMachine;
            return hive.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" +
                (TestMode ? AppName + "-Test" : AppName));
        }

        public string InstalledExe { get { return Path.Combine(InstallDir, ExeName); } }

        // steps: (description, action) — run in order, progress = i/steps
        public void Install(bool desktopShortcut, Action<int, int, string> onStep)
        {
            int total = desktopShortcut ? 6 : 5;
            int n = 0;

            onStep(++n, total, "Creating application folder");
            Directory.CreateDirectory(InstallDir);

            onStep(++n, total, "Installing " + ExeName);
            ExtractPayload(InstalledExe);

            onStep(++n, total, "Installing uninstaller");
            string uninst = Path.Combine(InstallDir, "Uninstall.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, uninst, true);

            onStep(++n, total, "Creating Start Menu shortcut");
            Directory.CreateDirectory(StartMenuDir);
            CreateShortcut(Path.Combine(StartMenuDir, AppName + ".lnk"), InstalledExe,
                "AM-IMG USB Drive Imager");

            if (desktopShortcut)
            {
                onStep(++n, total, "Creating Desktop shortcut");
                Directory.CreateDirectory(DesktopDir);
                CreateShortcut(Path.Combine(DesktopDir, AppName + ".lnk"), InstalledExe,
                    "AM-IMG USB Drive Imager");
            }

            onStep(++n, total, "Registering with Windows (Apps & Features)");
            using (var k = UninstallRoot())
            {
                k.SetValue("DisplayName", AppName + " — USB Drive Imager");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", Publisher);
                k.SetValue("DisplayIcon", InstalledExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024),
                    RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public void Uninstall(Action<string> onStep)
        {
            onStep("Removing shortcuts");
            TryDelete(Path.Combine(StartMenuDir, AppName + ".lnk"));
            TryDeleteDir(StartMenuDir, false);
            TryDelete(Path.Combine(DesktopDir, AppName + ".lnk"));

            onStep("Removing registry entries");
            try
            {
                var hive = TestMode ? Registry.CurrentUser : Registry.LocalMachine;
                hive.DeleteSubKeyTree(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" +
                    (TestMode ? AppName + "-Test" : AppName), false);
            }
            catch { }

            onStep("Removing program files");
            TryDelete(InstalledExe);
            // Uninstall.exe (this process) cannot delete itself now; hand the
            // final sweep to cmd after we exit.
            if (!TestMode)
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c timeout /t 3 /nobreak >nul & rd /s /q \"" + InstallDir + "\"");
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            else
            {
                TryDelete(Path.Combine(InstallDir, "Uninstall.exe"));
                TryDeleteDir(InstallDir, true);
            }
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        private static void TryDeleteDir(string d, bool recurse)
        { try { if (Directory.Exists(d)) Directory.Delete(d, recurse); } catch { } }

        private void ExtractPayload(string target)
        {
            using (Stream s = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("AM-IMG.exe"))
            {
                if (s == null) throw new InvalidOperationException(
                    "Installer payload missing — this setup file is corrupt; download it again.");
                using (var f = new FileStream(target, FileMode.Create, FileAccess.Write))
                    s.CopyTo(f);
            }
        }

        private static void CreateShortcut(string lnkPath, string target, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            try
            {
                dynamic sc = shell.CreateShortcut(lnkPath);
                sc.TargetPath = target;
                sc.WorkingDirectory = Path.GetDirectoryName(target);
                sc.Description = description;
                sc.IconLocation = target + ",0";
                sc.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
    }

    // ---------- installer window ----------
    internal class SetupForm : Form
    {
        private readonly bool uninstallMode;
        private readonly Installer inst = new Installer();

        private Panel screen;
        private CheckBox chkDesktop;
        private CheckBox chkLaunch;
        private SlimBar bar;
        private Label lblStep;
        private Label lblLog;

        public SetupForm(bool uninstall)
        {
            uninstallMode = uninstall;
            Text = uninstall ? "AM-IMG — Uninstall" : "AM-IMG Setup";
            BackColor = Ui.Bg;
            Font = Ui.F(9.75f, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(640, 430);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ShowWelcome();
        }

        private Panel NewScreen()
        {
            if (screen != null) Controls.Remove(screen);
            screen = new Panel();
            screen.BackColor = Ui.Card;
            screen.SetBounds(24, 24, 592, 382);
            Controls.Add(screen);
            return screen;
        }

        private Label L(Panel p, string text, float size, FontStyle style, Color color,
            int x, int y, int w, int h)
        {
            var l = new Label();
            l.Text = text;
            l.Font = Ui.F(size, style);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.SetBounds(x, y, w, h);
            p.Controls.Add(l);
            return l;
        }

        // ----- screen 1: welcome -----
        private void ShowWelcome()
        {
            var p = NewScreen();

            var logo = new PictureBox();
            logo.SetBounds(40, 36, 56, 56);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            try
            {
                Stream ls = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
                if (ls != null) logo.Image = Image.FromStream(ls);
                else logo.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap();
            }
            catch { }
            p.Controls.Add(logo);

            L(p, "AM-IMG", 21.75f, FontStyle.Bold, Ui.Ink, 112, 38, 300, 40);
            L(p, "DISK IMAGING SOFTWARE  ·  V" + Installer.Version + "  ·  BY " +
                 Installer.Publisher.ToUpperInvariant(), 8.25f, FontStyle.Bold,
                 Ui.InkFaint, 114, 78, 460, 16);

            if (!uninstallMode)
            {
                L(p, "Welcome. This wizard installs AM-IMG on your computer.", 12f,
                    FontStyle.Regular, Ui.InkSoft, 40, 128, 520, 26);
                L(p, "AM-IMG creates a fast sector-by-sector image of a USB pendrive into a " +
                     "single .img / .img.gz file and can restore it back — far faster than " +
                     "copying thousands of small files.", 9.75f, FontStyle.Regular,
                     Ui.InkSoft, 40, 158, 520, 58);

                L(p, "INSTALL LOCATION", 8.25f, FontStyle.Bold, Ui.InkFaint, 40, 224, 200, 16);
                var pathBox = new TextBox();
                pathBox.Text = inst.InstallDir;
                pathBox.ReadOnly = true;
                pathBox.Font = new Font("Consolas", 9f);
                pathBox.ForeColor = Ui.InkSoft;
                pathBox.BackColor = ColorTranslator.FromHtml("#F1F3FF");
                pathBox.BorderStyle = BorderStyle.FixedSingle;
                pathBox.SetBounds(40, 242, 520, 24);
                p.Controls.Add(pathBox);

                chkDesktop = new CheckBox();
                chkDesktop.Text = "Create a Desktop shortcut";
                chkDesktop.Checked = true;
                chkDesktop.ForeColor = Ui.InkSoft;
                chkDesktop.SetBounds(40, 278, 300, 22);
                p.Controls.Add(chkDesktop);

                var install = Ui.PrimaryButton("Install");
                install.Location = new Point(430, 326);
                install.Click += delegate { StartInstall(); };
                p.Controls.Add(install);

                var cancel = Ui.SecondaryButton("Cancel");
                cancel.Location = new Point(310, 326);
                cancel.Click += delegate { Close(); };
                p.Controls.Add(cancel);
                AcceptButton = install;
            }
            else
            {
                L(p, "Uninstall AM-IMG from this computer?", 12f, FontStyle.Regular,
                    Ui.InkSoft, 40, 140, 520, 26);
                L(p, "Your image files (.img / .img.gz) are yours and will not be touched.",
                    9.75f, FontStyle.Regular, Ui.InkFaint, 40, 172, 520, 22);

                var un = Ui.PrimaryButton("Uninstall");
                un.Location = new Point(430, 326);
                un.Click += delegate { StartUninstall(); };
                p.Controls.Add(un);

                var cancel = Ui.SecondaryButton("Cancel");
                cancel.Location = new Point(310, 326);
                cancel.Click += delegate { Close(); };
                p.Controls.Add(cancel);
            }
        }

        // ----- screen 2: progress -----
        private void ShowProgress(string title)
        {
            var p = NewScreen();
            L(p, title, 16.5f, FontStyle.Bold, Ui.Ink, 40, 60, 520, 34);
            bar = new SlimBar();
            bar.SetBounds(40, 130, 520, 6);
            p.Controls.Add(bar);
            lblStep = L(p, "", 9.75f, FontStyle.Regular, Ui.InkSoft, 40, 150, 520, 22);
            lblLog = L(p, "", 8.25f, FontStyle.Regular, Ui.InkFaint, 40, 186, 520, 150);
            lblLog.Font = new Font("Consolas", 8.25f);
            ControlBox = false;
        }

        // ----- screen 3: done -----
        private void ShowDone(bool wasUninstall, string error)
        {
            var p = NewScreen();
            ControlBox = true;
            if (error == null)
            {
                L(p, wasUninstall ? "Uninstalled." : "Installation complete.",
                    21.75f, FontStyle.Bold, Ui.Ink, 40, 70, 520, 44);
                L(p, wasUninstall
                        ? "AM-IMG has been removed from this computer."
                        : "AM-IMG is ready. Find it in the Start Menu" +
                          (chkDesktop != null && chkDesktop.Checked ? " or on your Desktop." : "."),
                    11.25f, FontStyle.Regular, Ui.InkSoft, 40, 126, 520, 26);

                if (!wasUninstall)
                {
                    chkLaunch = new CheckBox();
                    chkLaunch.Text = "Launch AM-IMG now";
                    chkLaunch.Checked = true;
                    chkLaunch.ForeColor = Ui.InkSoft;
                    chkLaunch.SetBounds(40, 170, 300, 22);
                    p.Controls.Add(chkLaunch);
                }
            }
            else
            {
                L(p, wasUninstall ? "Uninstall failed." : "Installation failed.",
                    21.75f, FontStyle.Bold, ColorTranslator.FromHtml("#BA1A1A"), 40, 70, 520, 44);
                var err = L(p, error, 9.75f, FontStyle.Regular, Ui.InkSoft, 40, 126, 520, 120);
                err.AutoEllipsis = true;
            }

            var finish = Ui.PrimaryButton("Finish");
            finish.Location = new Point(430, 326);
            finish.Click += delegate
            {
                if (error == null && !wasUninstall && chkLaunch != null && chkLaunch.Checked)
                {
                    try { Process.Start(inst.InstalledExe); }
                    catch (Exception ex)
                    { MessageBox.Show(this, "Could not launch: " + ex.Message, "AM-IMG"); }
                }
                Close();
            };
            p.Controls.Add(finish);
            AcceptButton = finish;
        }

        private async void StartInstall()
        {
            bool desktop = chkDesktop.Checked;
            ShowProgress("Installing AM-IMG…");
            string error = null;
            try
            {
                await Task.Run(delegate
                {
                    inst.Install(desktop, delegate(int step, int total, string what)
                    {
                        BeginInvoke((Action)delegate
                        {
                            bar.Value = (double)step / total;
                            lblStep.Text = what + "…";
                            lblLog.Text += string.Format("[{0}/{1}] {2}\r\n", step, total, what);
                        });
                        System.Threading.Thread.Sleep(150); // let the state be visible
                    });
                });
            }
            catch (Exception ex) { error = ex.Message; }
            ShowDone(false, error);
        }

        private async void StartUninstall()
        {
            ShowProgress("Removing AM-IMG…");
            string error = null;
            try
            {
                await Task.Run(delegate
                {
                    inst.Uninstall(delegate(string what)
                    {
                        BeginInvoke((Action)delegate
                        {
                            lblStep.Text = what + "…";
                            lblLog.Text += what + "\r\n";
                        });
                        System.Threading.Thread.Sleep(250);
                    });
                });
                if (bar != null) BeginInvoke((Action)delegate { bar.Value = 1; });
            }
            catch (Exception ex) { error = ex.Message; }
            ShowDone(true, error);
        }
    }

    internal static class SetupProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // silent test hooks used by the build pipeline (no admin needed)
            if (args.Length >= 2 && args[0] == "/testinstall")
                return TestRoundTrip(args[1]);
            if (args.Length > 0 && args[0] == "/smoke")
            {
                try
                {
                    using (var f = new SetupForm(false))
                    {
                        f.CreateControl();
                        if (args.Length > 1) RenderTo(f, args[1]);
                    }
                    using (var f = new SetupForm(true)) f.CreateControl();
                    return 0;
                }
                catch { return 1; }
            }

            bool uninstall = args.Length > 0 &&
                args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(uninstall));
            return 0;
        }

        private static void RenderTo(Form f, string path)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-32000, -32000);
            f.Opacity = 0;
            f.Show();
            Application.DoEvents();
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            f.Hide();
        }

        private static int TestRoundTrip(string root)
        {
            var log = new System.Text.StringBuilder();
            try
            {
                var inst = new Installer();
                inst.TestMode = true;
                inst.TestRoot = root;
                Directory.CreateDirectory(root);

                inst.Install(true, delegate(int s, int t, string w)
                { log.AppendLine(string.Format("[{0}/{1}] {2}", s, t, w)); });

                bool exeOk = File.Exists(inst.InstalledExe) &&
                             new FileInfo(inst.InstalledExe).Length > 10000;
                bool unOk = File.Exists(Path.Combine(inst.InstallDir, "Uninstall.exe"));
                bool smOk = File.Exists(Path.Combine(root, "startmenu", "AM-IMG.lnk"));
                bool dtOk = File.Exists(Path.Combine(root, "desktop", "AM-IMG.lnk"));
                bool regOk;
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AM-IMG-Test"))
                    regOk = k != null && (string)k.GetValue("Publisher") == Installer.Publisher;
                log.AppendLine("exe=" + exeOk + " uninst=" + unOk + " startmenu=" + smOk +
                               " desktop=" + dtOk + " registry=" + regOk);

                inst.Uninstall(delegate(string w) { log.AppendLine(w); });
                bool cleanFiles = !File.Exists(inst.InstalledExe) &&
                                  !File.Exists(Path.Combine(root, "startmenu", "AM-IMG.lnk")) &&
                                  !File.Exists(Path.Combine(root, "desktop", "AM-IMG.lnk"));
                bool cleanReg;
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AM-IMG-Test"))
                    cleanReg = k == null;
                log.AppendLine("cleanFiles=" + cleanFiles + " cleanReg=" + cleanReg);

                bool all = exeOk && unOk && smOk && dtOk && regOk && cleanFiles && cleanReg;
                log.AppendLine(all ? "SETUP TEST PASSED" : "SETUP TEST FAILED");
                File.WriteAllText(Path.Combine(root, "setup-test.log"), log.ToString());
                return all ? 0 : 1;
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION: " + ex);
                try { File.WriteAllText(Path.Combine(root, "setup-test.log"), log.ToString()); }
                catch { }
                return 2;
            }
        }
    }
}

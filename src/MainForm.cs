using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UsbImager
{
    public class MainForm : Form
    {
        private ComboBox cmbDrive;
        private Button btnRefresh;
        private TextBox txtImage;
        private Button btnBrowse;
        private CheckBox chkCompress;
        private CheckBox chkVerify;
        private Button btnRead;
        private Button btnWrite;
        private Button btnCancel;
        private ProgressBar bar;
        private Label lblStatus;
        private Label lblDetail;

        private List<DiskInfo> disks = new List<DiskInfo>();
        private CancellationTokenSource cts;
        private bool busy;

        // speed calculation
        private long lastBytes;
        private DateTime lastTick;
        private double speedBps;

        private const int WM_DEVICECHANGE = 0x0219;

        public MainForm()
        {
            Text = "AM-IMG — USB Drive Imager (by Anirban Majumdar)";
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(560, 235);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var lblDrive = new Label();
            lblDrive.Text = "USB drive:";
            lblDrive.SetBounds(12, 16, 70, 20);
            Controls.Add(lblDrive);

            cmbDrive = new ComboBox();
            cmbDrive.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDrive.SetBounds(90, 12, 380, 24);
            Controls.Add(cmbDrive);

            btnRefresh = new Button();
            btnRefresh.Text = "Refresh";
            btnRefresh.SetBounds(478, 11, 70, 25);
            btnRefresh.Click += delegate { RefreshDrives(); };
            Controls.Add(btnRefresh);

            var lblImg = new Label();
            lblImg.Text = "Image file:";
            lblImg.SetBounds(12, 50, 70, 20);
            Controls.Add(lblImg);

            txtImage = new TextBox();
            txtImage.SetBounds(90, 46, 380, 24);
            Controls.Add(txtImage);

            btnBrowse = new Button();
            btnBrowse.Text = "Browse…";
            btnBrowse.SetBounds(478, 45, 70, 25);
            btnBrowse.Click += delegate { BrowseImage(); };
            Controls.Add(btnBrowse);

            chkCompress = new CheckBox();
            chkCompress.Text = "Compress image (.img.gz) — much smaller file when the drive is not full";
            chkCompress.SetBounds(90, 76, 458, 20);
            chkCompress.Checked = true;
            Controls.Add(chkCompress);

            chkVerify = new CheckBox();
            chkVerify.Text = "Verify after operation (recommended)";
            chkVerify.SetBounds(90, 98, 458, 20);
            chkVerify.Checked = true;
            Controls.Add(chkVerify);

            btnRead = new Button();
            btnRead.Text = "Read  (Drive → Image)";
            btnRead.SetBounds(90, 126, 170, 30);
            btnRead.Click += delegate { StartRead(); };
            Controls.Add(btnRead);

            btnWrite = new Button();
            btnWrite.Text = "Write  (Image → Drive)";
            btnWrite.SetBounds(270, 126, 170, 30);
            btnWrite.Click += delegate { StartWrite(); };
            Controls.Add(btnWrite);

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Enabled = false;
            btnCancel.SetBounds(450, 126, 98, 30);
            btnCancel.Click += delegate { if (cts != null) cts.Cancel(); };
            Controls.Add(btnCancel);

            bar = new ProgressBar();
            bar.SetBounds(12, 168, 536, 20);
            bar.Maximum = 1000;
            Controls.Add(bar);

            lblStatus = new Label();
            lblStatus.Text = "Select your USB drive and an image file.";
            lblStatus.SetBounds(12, 194, 536, 18);
            Controls.Add(lblStatus);

            lblDetail = new Label();
            lblDetail.Text = "";
            lblDetail.ForeColor = Color.Gray;
            lblDetail.SetBounds(12, 212, 536, 18);
            Controls.Add(lblDetail);

            Load += delegate { RefreshDrives(); };
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DEVICECHANGE && !busy)
            {
                // debounce: refresh shortly after any device arrival/removal
                var t = new System.Windows.Forms.Timer();
                t.Interval = 800;
                t.Tick += delegate { t.Stop(); t.Dispose(); if (!busy) RefreshDrives(); };
                t.Start();
            }
        }

        private void RefreshDrives()
        {
            try
            {
                int prev = cmbDrive.SelectedIndex;
                disks = DriveList.GetSafeDisks();
                cmbDrive.Items.Clear();
                foreach (var d in disks) cmbDrive.Items.Add(d.DisplayName);
                if (disks.Count > 0)
                    cmbDrive.SelectedIndex = (prev >= 0 && prev < disks.Count) ? prev : 0;
                lblStatus.Text = disks.Count > 0
                    ? "Ready. Only USB / removable drives are listed — the system disk is never shown."
                    : "No USB drive detected. Plug in a pendrive and it will appear automatically.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Could not list drives: " + ex.Message;
            }
        }

        private DiskInfo SelectedDisk()
        {
            int i = cmbDrive.SelectedIndex;
            if (i < 0 || i >= disks.Count) return null;
            return disks[i];
        }

        private void BrowseImage()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Choose image file (existing file to write, or new file to create)";
                dlg.Filter = "Disk images (*.img;*.img.gz)|*.img;*.img.gz;*.gz|All files (*.*)|*.*";
                dlg.OverwritePrompt = false; // we confirm ourselves on Read
                dlg.CheckFileExists = false;
                dlg.FileName = SuggestName();
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    txtImage.Text = dlg.FileName;
            }
        }

        private string SuggestName()
        {
            var d = SelectedDisk();
            string baseName = "usb-backup";
            if (d != null && !string.IsNullOrEmpty(d.Model))
            {
                baseName = d.Model.Trim();
                foreach (char c in Path.GetInvalidFileNameChars())
                    baseName = baseName.Replace(c, '_');
                baseName = baseName.Replace(' ', '_');
            }
            return string.Format("{0}_{1:yyyy-MM-dd}.img{2}",
                baseName, DateTime.Now, chkCompress.Checked ? ".gz" : "");
        }

        // ---------------- READ ----------------
        private void StartRead()
        {
            var disk = SelectedDisk();
            if (disk == null) { Warn("Select a USB drive first."); return; }
            string path = txtImage.Text.Trim();
            if (path.Length == 0) { BrowseImage(); path = txtImage.Text.Trim(); if (path.Length == 0) return; }

            if (File.Exists(path))
            {
                if (MessageBox.Show(this,
                    "The file already exists and will be OVERWRITTEN:\n\n" + path + "\n\nContinue?",
                    "AM-IMG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }

            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(root))
                {
                    var di = new DriveInfo(root);
                    if (!chkCompress.Checked && di.AvailableFreeSpace < disk.Size)
                    {
                        if (MessageBox.Show(this, string.Format(
                            "The destination has {0} free but the image will be {1} " +
                            "(a raw image is always the FULL size of the drive).\n\n" +
                            "Tip: enable compression, or choose another destination.\n\nContinue anyway?",
                            Format.Bytes(di.AvailableFreeSpace), Format.Bytes(disk.Size)),
                            "AM-IMG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    }
                }
            }
            catch { }

            bool compress = chkCompress.Checked;
            bool verify = chkVerify.Checked;
            RunOperation(delegate(IProgress<ProgressInfo> p, CancellationToken tk)
            {
                return Engine.ReadToImage(disk, path, compress, verify, p, tk);
            }, string.Format("Imaging {0} → {1}", disk.DisplayName, Path.GetFileName(path)), path, false);
        }

        // ---------------- WRITE ----------------
        private void StartWrite()
        {
            var disk = SelectedDisk();
            if (disk == null) { Warn("Select a USB drive first."); return; }
            string path = txtImage.Text.Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                Warn("Choose an existing image file to write.");
                return;
            }

            string letters = disk.Letters.Count > 0 ? string.Join(", ", disk.Letters.ToArray()) : "none";
            if (MessageBox.Show(this, string.Format(
                "⚠  This will PERMANENTLY ERASE ALL DATA on:\n\n" +
                "     {0}\n     Size: {1}\n     Drive letter(s): {2}\n\n" +
                "and replace it with the contents of:\n\n     {3}\n\nAre you absolutely sure?",
                (disk.Model ?? "").Trim(), Format.Bytes(disk.Size), letters, path),
                "AM-IMG — Confirm write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            bool verify = chkVerify.Checked;
            RunOperation(delegate(IProgress<ProgressInfo> p, CancellationToken tk)
            {
                return Engine.WriteFromImage(disk, path, verify, p, tk);
            }, string.Format("Writing {0} → {1}", Path.GetFileName(path), disk.DisplayName), path, true);
        }

        // ---------------- shared runner ----------------
        private async void RunOperation(Func<IProgress<ProgressInfo>, CancellationToken, OpResult> op,
            string title, string imagePath, bool wasWrite)
        {
            SetBusy(true);
            lblStatus.Text = title;
            lblDetail.Text = "";
            bar.Value = 0;
            lastBytes = 0; speedBps = 0; lastTick = DateTime.UtcNow;
            cts = new CancellationTokenSource();
            var token = cts.Token;

            var progress = new Progress<ProgressInfo>(OnProgress);
            try
            {
                OpResult r = await Task.Run(delegate { return op(progress, token); });
                bar.Value = bar.Maximum;
                string msg = string.Format("Done in {0:mm\\:ss} — {1} processed{2}. SHA-256: {3}…",
                    r.Elapsed, Format.Bytes(r.Bytes),
                    r.Verified ? ", verified OK" : "", r.Sha256.Substring(0, 12));
                lblStatus.Text = msg;
                lblDetail.Text = r.Warning ?? "";
                MessageBox.Show(this,
                    (wasWrite ? "Write completed successfully."
                              : "Image created successfully:\n" + imagePath) +
                    (r.Verified ? "\n\nVerification passed — the copy is exact." : "") +
                    (r.Warning != null ? "\n\nNote: " + r.Warning : ""),
                    "AM-IMG", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Cancelled.";
                if (wasWrite)
                    MessageBox.Show(this, "Write was cancelled midway — the drive is now in an " +
                        "incomplete state and must be re-written or reformatted before use.",
                        "AM-IMG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    TryDeletePartial(imagePath);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Failed: " + ex.Message;
                MessageBox.Show(this, ex.Message, "AM-IMG — Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (!wasWrite) TryDeletePartial(imagePath);
            }
            finally
            {
                cts.Dispose(); cts = null;
                SetBusy(false);
                RefreshDrives();
            }
        }

        private void TryDeletePartial(string imagePath)
        {
            try
            {
                if (File.Exists(imagePath) &&
                    MessageBox.Show(this, "Delete the incomplete image file?\n" + imagePath,
                        "AM-IMG", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                        == DialogResult.Yes)
                    File.Delete(imagePath);
            }
            catch { }
        }

        private void OnProgress(ProgressInfo p)
        {
            var now = DateTime.UtcNow;
            double dt = (now - lastTick).TotalSeconds;
            if (dt >= 0.5)
            {
                double inst = (p.Done - lastBytes) / dt;
                if (inst >= 0)
                    speedBps = speedBps <= 0 ? inst : (0.6 * speedBps + 0.4 * inst);
                lastBytes = p.Done;
                lastTick = now;
            }
            if (p.Total > 0)
            {
                int v = (int)Math.Min((long)bar.Maximum, p.Done * bar.Maximum / p.Total);
                bar.Value = Math.Max(0, v);
                string eta = "";
                if (speedBps > 1 && p.Done <= p.Total)
                {
                    var rem = TimeSpan.FromSeconds((p.Total - p.Done) / speedBps);
                    eta = string.Format(" — ETA {0:mm\\:ss}", rem);
                }
                lblStatus.Text = string.Format("{0}… {1} / {2} ({3:0.0}%) — {4}/s{5}",
                    p.Phase, Format.Bytes(p.Done), Format.Bytes(p.Total),
                    100.0 * p.Done / p.Total, Format.Bytes((long)speedBps), eta);
            }
            else
            {
                lblStatus.Text = string.Format("{0}… {1} — {2}/s",
                    p.Phase, Format.Bytes(p.Done), Format.Bytes((long)speedBps));
            }
        }

        private void SetBusy(bool b)
        {
            busy = b;
            cmbDrive.Enabled = !b;
            btnRefresh.Enabled = !b;
            txtImage.Enabled = !b;
            btnBrowse.Enabled = !b;
            chkCompress.Enabled = !b;
            chkVerify.Enabled = !b;
            btnRead.Enabled = !b;
            btnWrite.Enabled = !b;
            btnCancel.Enabled = b;
            ControlBox = !b;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                Warn("An operation is running — cancel it first.");
            }
            base.OnFormClosing(e);
        }

        private void Warn(string msg)
        {
            MessageBox.Show(this, msg, "AM-IMG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("AM-IMG")]
[assembly: AssemblyProduct("AM-IMG USB Drive Imager")]
[assembly: AssemblyDescription("Fast raw imaging of USB drives: drive to .img/.img.gz and back, with verification.")]
[assembly: AssemblyCompany("Anirban Majumdar")]
[assembly: AssemblyCopyright("© 2026 Anirban Majumdar")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]

namespace UsbImager
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Hidden smoke-test hook used by the build pipeline: construct the
            // full UI + enumerate drives, then exit without showing a window.
            if (args.Length > 0 && args[0] == "--smoke")
            {
                try
                {
                    using (var f = new MainForm())
                    {
                        var disks = DriveList.GetSafeDisks();
                        if (args.Length > 1)
                        {
                            f.StartPosition = FormStartPosition.Manual;
                            f.Location = new System.Drawing.Point(-32000, -32000);
                            f.Opacity = 0;
                            f.Show();
                            Application.DoEvents();
                            using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                            {
                                f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                                bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                            }
                            f.Hide();
                        }
                        else f.CreateControl();
                        Console.WriteLine("SMOKE OK, safe disks: " + disks.Count);
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("SMOKE FAIL: " + ex);
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Management;

namespace UsbImager
{
    public class DiskInfo
    {
        public int Index;
        public string Model;
        public long Size;
        public List<string> Letters = new List<string>();
        public bool IsUsb;
        public bool IsRemovable;
        public bool IsSystem;   // hosts the OS / boot partitions

        public string DevicePath
        {
            get { return @"\\.\PHYSICALDRIVE" + Index; }
        }

        public string DisplayName
        {
            get
            {
                string letters = Letters.Count > 0
                    ? " [" + string.Join(", ", Letters.ToArray()) + "]"
                    : " [no letter]";
                return string.Format("Disk {0}: {1}{2} — {3}",
                    Index, (Model ?? "Unknown").Trim(), letters, Format.Bytes(Size));
            }
        }
    }

    public static class Format
    {
        public static string Bytes(long v)
        {
            if (v < 0) return "?";
            double d = v;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
            return string.Format("{0:0.##} {1}", d, u[i]);
        }
    }

    public static class DriveList
    {
        // Returns all physical disks with USB/system flags resolved.
        // Detection is defense-in-depth: the MSFT_Disk storage API is the
        // authority (BusType 7 = USB, IsBoot/IsSystem), with Win32_DiskDrive
        // heuristics as fallback, because InterfaceType lies for many USB
        // bridges (e.g. SanDisk sticks report "SCSI").
        public static List<DiskInfo> GetDisks()
        {
            var disks = new Dictionary<int, DiskInfo>();

            using (var s = new ManagementObjectSearcher(@"root\cimv2",
                "SELECT Index, Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    var d = new DiskInfo();
                    d.Index = Convert.ToInt32(mo["Index"]);
                    d.Model = Convert.ToString(mo["Model"]);
                    d.Size = mo["Size"] == null ? -1 : Convert.ToInt64(mo["Size"]);
                    string itype = Convert.ToString(mo["InterfaceType"]) ?? "";
                    string mtype = Convert.ToString(mo["MediaType"]) ?? "";
                    d.IsRemovable = mtype.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0;
                    d.IsUsb = itype.Equals("USB", StringComparison.OrdinalIgnoreCase)
                           || (d.Model ?? "").IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0;
                    disks[d.Index] = d;
                }
            }

            AttachLetters(disks);
            RefineWithStorageApi(disks);
            MarkSystemDiskFallback(disks);

            var list = new List<DiskInfo>(disks.Values);
            list.Sort(delegate(DiskInfo a, DiskInfo b) { return a.Index.CompareTo(b.Index); });
            return list;
        }

        // Only disks that are safe targets: USB or removable, never the system disk.
        public static List<DiskInfo> GetSafeDisks()
        {
            var all = GetDisks();
            var safe = new List<DiskInfo>();
            foreach (var d in all)
                if ((d.IsUsb || d.IsRemovable) && !d.IsSystem)
                    safe.Add(d);
            return safe;
        }

        private static void AttachLetters(Dictionary<int, DiskInfo> disks)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\cimv2",
                    "SELECT DeviceID, DiskIndex FROM Win32_DiskPartition"))
                {
                    foreach (ManagementObject part in s.Get())
                    {
                        int idx = Convert.ToInt32(part["DiskIndex"]);
                        DiskInfo d;
                        if (!disks.TryGetValue(idx, out d)) continue;
                        string devId = Convert.ToString(part["DeviceID"]).Replace("\\", "\\\\").Replace("'", "");
                        string assoc = "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + devId +
                                       "'} WHERE AssocClass=Win32_LogicalDiskToPartition";
                        using (var ls = new ManagementObjectSearcher(@"root\cimv2", assoc))
                        {
                            foreach (ManagementObject ld in ls.Get())
                            {
                                string letter = Convert.ToString(ld["DeviceID"]);
                                if (!string.IsNullOrEmpty(letter) && !d.Letters.Contains(letter))
                                    d.Letters.Add(letter);
                            }
                        }
                    }
                }
            }
            catch { /* letters are cosmetic + used for locking; lock falls back gracefully */ }
        }

        private static void RefineWithStorageApi(Dictionary<int, DiskInfo> disks)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                    "SELECT Number, BusType, IsBoot, IsSystem FROM MSFT_Disk"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        int idx = Convert.ToInt32(mo["Number"]);
                        DiskInfo d;
                        if (!disks.TryGetValue(idx, out d)) continue;
                        int bus = Convert.ToInt32(mo["BusType"]);
                        if (bus == 7) d.IsUsb = true;               // 7 = USB
                        bool isBoot = mo["IsBoot"] != null && Convert.ToBoolean(mo["IsBoot"]);
                        bool isSys = mo["IsSystem"] != null && Convert.ToBoolean(mo["IsSystem"]);
                        if (isBoot || isSys) d.IsSystem = true;
                    }
                }
            }
            catch { /* older namespace missing: fallback below still protects C: */ }
        }

        private static void MarkSystemDiskFallback(Dictionary<int, DiskInfo> disks)
        {
            // Whatever the storage API said, always also flag the disk that
            // holds the Windows volume (usually C:).
            try
            {
                string sysLetter = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                                              .Substring(0, 2).ToUpperInvariant();
                foreach (var d in disks.Values)
                    foreach (var l in d.Letters)
                        if (l.ToUpperInvariant() == sysLetter)
                            d.IsSystem = true;
            }
            catch { }
        }
    }
}

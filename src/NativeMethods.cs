using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbImager
{
    internal static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;

        public const uint FSCTL_LOCK_VOLUME = 0x00090018;
        public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        public static SafeFileHandle OpenRaw(string path, bool write)
        {
            uint access = GENERIC_READ;
            if (write) access |= GENERIC_WRITE;
            SafeFileHandle h = CreateFile(path, access,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException(string.Format(
                    "Cannot open {0} ({1}). Run the program as Administrator.",
                    path, new Win32Exception(err).Message));
            }
            return h;
        }

        public static bool Ioctl(SafeFileHandle h, uint code)
        {
            int returned;
            return DeviceIoControl(h, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
        }

        public static long GetDeviceLength(SafeFileHandle h)
        {
            IntPtr buf = Marshal.AllocHGlobal(8);
            try
            {
                int returned;
                if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0,
                        buf, 8, out returned, IntPtr.Zero))
                    throw new IOException("Cannot query device size ("
                        + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
                return Marshal.ReadInt64(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static int GetSectorSize(SafeFileHandle h)
        {
            // DISK_GEOMETRY_EX starts with DISK_GEOMETRY:
            //   LARGE_INTEGER Cylinders (8) + MediaType (4) + TracksPerCylinder (4)
            //   + SectorsPerTrack (4) + BytesPerSector (4)  => BytesPerSector at offset 20
            IntPtr buf = Marshal.AllocHGlobal(256);
            try
            {
                int returned;
                if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0,
                        buf, 256, out returned, IntPtr.Zero))
                    return 512; // sensible default for USB media
                int bps = Marshal.ReadInt32(buf, 20);
                if (bps < 512 || bps > 65536 || (bps & (bps - 1)) != 0) return 512;
                return bps;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UsbImager
{
    public class ProgressInfo
    {
        public string Phase;        // "Reading", "Writing", "Verifying"
        public long Done;           // bytes processed so far (see TotalNote)
        public long Total;          // -1 if unknown
        public ProgressInfo(string phase, long done, long total)
        { Phase = phase; Done = done; Total = total; }
    }

    public class OpResult
    {
        public long Bytes;              // raw bytes moved (incl. padding on write)
        public TimeSpan Elapsed;
        public string Sha256;           // hash of the data stream
        public bool Verified;
        public string Warning;          // non-fatal notes (e.g. lock failed on read)
    }

    public static class Engine
    {
        public const int ChunkSize = 4 * 1024 * 1024; // 4 MiB, multiple of any sector size
        private static readonly byte[] GzMagic = { 0x1f, 0x8b };

        // ---------------- core copy loop (unit-tested) ----------------
        // Reads from input until EOF (or inputLength bytes if >= 0), writes to
        // output. If padTo > 0 the final short block is zero-padded up to a
        // multiple of padTo (required for raw device writes). If maxOutput >= 0,
        // exceeding it throws (image larger than target drive). The hash covers
        // exactly the bytes written to output (including padding).
        public static long CopyCore(Stream input, Stream output, long inputLength,
            int padTo, long maxOutput, HashAlgorithm hasher,
            Action<long> onProgress, CancellationToken token)
        {
            byte[] buffer = new byte[ChunkSize];
            long readTotal = 0;
            long written = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                int want = buffer.Length;
                if (inputLength >= 0)
                {
                    long remaining = inputLength - readTotal;
                    if (remaining <= 0) break;
                    if (remaining < want) want = (int)remaining;
                }

                // Fill the buffer fully; decompression streams return short reads.
                int got = 0;
                while (got < want)
                {
                    int n = input.Read(buffer, got, want - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got == 0) break;
                readTotal += got;

                int outLen = got;
                if (padTo > 0 && (outLen % padTo) != 0)
                {
                    int padded = ((outLen / padTo) + 1) * padTo;
                    Array.Clear(buffer, outLen, padded - outLen);
                    outLen = padded;
                }

                if (maxOutput >= 0 && written + outLen > maxOutput)
                    throw new IOException(string.Format(
                        "The image is larger than the target drive ({0} available).",
                        Format.Bytes(maxOutput)));

                if (hasher != null) hasher.TransformBlock(buffer, 0, outLen, null, 0);
                output.Write(buffer, 0, outLen);
                written += outLen;
                if (onProgress != null) onProgress(written);

                if (got < want) break; // true EOF
            }

            if (hasher != null) hasher.TransformFinalBlock(new byte[0], 0, 0);
            return written;
        }

        public static bool IsGzipFile(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var b = new byte[2];
                int n = fs.Read(b, 0, 2);
                return n == 2 && b[0] == GzMagic[0] && b[1] == GzMagic[1];
            }
        }

        private static string Hex(byte[] h)
        {
            var sb = new System.Text.StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ---------------- volume locking ----------------
        private static List<SafeFileHandle> LockVolumes(DiskInfo disk, bool required, out string warning)
        {
            warning = null;
            var handles = new List<SafeFileHandle>();
            foreach (string letter in disk.Letters)
            {
                SafeFileHandle h = null;
                try
                {
                    h = NativeMethods.OpenRaw(@"\\.\" + letter, true);
                    bool locked = false;
                    for (int i = 0; i < 10 && !locked; i++)
                    {
                        locked = NativeMethods.Ioctl(h, NativeMethods.FSCTL_LOCK_VOLUME);
                        if (!locked) Thread.Sleep(150);
                    }
                    if (!locked)
                    {
                        if (required)
                        {
                            h.Dispose();
                            foreach (var x in handles) x.Dispose();
                            throw new IOException(string.Format(
                                "Volume {0} is in use and cannot be locked. Close all Explorer windows " +
                                "and programs using the drive, then try again.", letter));
                        }
                        warning = string.Format("Volume {0} could not be locked; files open on the drive " +
                            "may make the image inconsistent. Best: close programs using it and retry.", letter);
                    }
                    else if (required)
                    {
                        NativeMethods.Ioctl(h, NativeMethods.FSCTL_DISMOUNT_VOLUME);
                    }
                    handles.Add(h);
                }
                catch (IOException) { throw; }
                catch
                {
                    if (h != null) h.Dispose();
                    // opening the volume failed (rare); for reads we proceed unlocked
                    if (required) throw;
                }
            }
            return handles;
        }

        // ---------------- READ: drive -> image file ----------------
        public static OpResult ReadToImage(DiskInfo disk, string imagePath, bool compress,
            bool verify, IProgress<ProgressInfo> progress, CancellationToken token)
        {
            var result = new OpResult();
            var sw = Stopwatch.StartNew();
            List<SafeFileHandle> locks = null;
            string warn;
            try
            {
                locks = LockVolumes(disk, false, out warn);
                result.Warning = warn;

                using (SafeFileHandle dh = NativeMethods.OpenRaw(disk.DevicePath, false))
                {
                    long length = NativeMethods.GetDeviceLength(dh);
                    using (var device = new FileStream(dh, FileAccess.Read, ChunkSize))
                    using (var file = new FileStream(imagePath, FileMode.Create, FileAccess.Write,
                                                     FileShare.None, ChunkSize))
                    using (var hasher = SHA256.Create())
                    {
                        Stream target = file;
                        GZipStream gz = null;
                        if (compress)
                            target = gz = new GZipStream(file, CompressionLevel.Fastest, true);
                        try
                        {
                            CopyCore(device, target, length, 0, -1, hasher,
                                delegate(long done)
                                {
                                    if (progress != null)
                                        progress.Report(new ProgressInfo("Reading", done, length));
                                }, token);
                        }
                        finally { if (gz != null) gz.Dispose(); }
                        result.Bytes = length;
                        result.Sha256 = Hex(hasher.Hash);
                    }
                }
            }
            finally
            {
                if (locks != null) foreach (var h in locks) h.Dispose();
            }

            if (verify)
            {
                token.ThrowIfCancellationRequested();
                string imgHash = HashImageContent(imagePath, result.Bytes, progress, token);
                if (imgHash != result.Sha256)
                    throw new IOException("VERIFY FAILED: the image file does not match the data read " +
                        "from the drive. The image may be corrupt — do not rely on it.");
                result.Verified = true;
            }

            result.Elapsed = sw.Elapsed;
            return result;
        }

        // Hash the (decompressed) content of an image file.
        private static string HashImageContent(string imagePath, long expectedTotal,
            IProgress<ProgressInfo> progress, CancellationToken token)
        {
            bool gz = IsGzipFile(imagePath);
            using (var file = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                                             FileShare.Read, ChunkSize))
            using (var hasher = SHA256.Create())
            {
                Stream src = file;
                GZipStream gzs = null;
                if (gz) src = gzs = new GZipStream(file, CompressionMode.Decompress, true);
                try
                {
                    long fileLen = file.Length;
                    CopyCore(src, Stream.Null, -1, 0, -1, hasher,
                        delegate(long done)
                        {
                            // for gz report compressed progress (position of base stream)
                            long d = gz ? file.Position : done;
                            long t = gz ? fileLen : expectedTotal;
                            if (progress != null)
                                progress.Report(new ProgressInfo("Verifying", d, t));
                        }, token);
                }
                finally { if (gzs != null) gzs.Dispose(); }
                return Hex(hasher.Hash);
            }
        }

        // ---------------- WRITE: image file -> drive ----------------
        public static OpResult WriteFromImage(DiskInfo disk, string imagePath,
            bool verify, IProgress<ProgressInfo> progress, CancellationToken token)
        {
            if (disk.IsSystem)
                throw new InvalidOperationException("Refusing to write to the system disk.");

            var result = new OpResult();
            var sw = Stopwatch.StartNew();
            bool gz = IsGzipFile(imagePath);
            long writtenPadded;
            string streamHash;

            List<SafeFileHandle> locks = null;
            string warn;
            try
            {
                locks = LockVolumes(disk, true, out warn); // locks + dismounts, throws if busy

                using (SafeFileHandle dh = NativeMethods.OpenRaw(disk.DevicePath, true))
                {
                    long deviceLen = NativeMethods.GetDeviceLength(dh);
                    int sector = NativeMethods.GetSectorSize(dh);

                    using (var file = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                                                     FileShare.Read, ChunkSize))
                    {
                        long fileLen = file.Length;
                        if (!gz && fileLen > deviceLen)
                            throw new IOException(string.Format(
                                "Image ({0}) is larger than the target drive ({1}).",
                                Format.Bytes(fileLen), Format.Bytes(deviceLen)));

                        using (var device = new FileStream(dh, FileAccess.Write, ChunkSize))
                        using (var hasher = SHA256.Create())
                        {
                            Stream src = file;
                            GZipStream gzs = null;
                            if (gz) src = gzs = new GZipStream(file, CompressionMode.Decompress, true);
                            try
                            {
                                writtenPadded = CopyCore(src, device, -1, sector, deviceLen, hasher,
                                    delegate(long done)
                                    {
                                        long d = gz ? file.Position : done;
                                        long t = gz ? fileLen : Math.Min(fileLen, deviceLen);
                                        if (progress != null)
                                            progress.Report(new ProgressInfo("Writing", d, t));
                                    }, token);
                            }
                            finally { if (gzs != null) gzs.Dispose(); }
                            device.Flush();
                            streamHash = Hex(hasher.Hash);
                        }
                    }
                }
            }
            finally
            {
                if (locks != null) foreach (var h in locks) h.Dispose();
            }

            result.Bytes = writtenPadded;
            result.Sha256 = streamHash;

            if (verify)
            {
                token.ThrowIfCancellationRequested();
                using (SafeFileHandle dh = NativeMethods.OpenRaw(disk.DevicePath, false))
                using (var device = new FileStream(dh, FileAccess.Read, ChunkSize))
                using (var hasher = SHA256.Create())
                {
                    CopyCore(device, Stream.Null, writtenPadded, 0, -1, hasher,
                        delegate(long done)
                        {
                            if (progress != null)
                                progress.Report(new ProgressInfo("Verifying", done, writtenPadded));
                        }, token);
                    if (Hex(hasher.Hash) != streamHash)
                        throw new IOException("VERIFY FAILED: data read back from the drive does not " +
                            "match the image. The drive may be faulty — do not trust this copy.");
                    result.Verified = true;
                }
            }

            result.Elapsed = sw.Elapsed;
            return result;
        }
    }
}

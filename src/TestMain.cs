using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace UsbImager
{
    // Console test harness for the AM-IMG engine. Exercises CopyCore (the code
    // path every read/write/verify goes through) against files, so no real
    // drive is ever touched.
    internal static class TestMain
    {
        private static int failures;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine("{0}  {1}", cond ? "PASS" : "FAIL", name);
            if (!cond) failures++;
        }

        private static byte[] RandomData(int size, int seed)
        {
            var b = new byte[size];
            new Random(seed).NextBytes(b);
            return b;
        }

        private static string Sha(byte[] data, int count)
        {
            using (var h = SHA256.Create())
            {
                byte[] d = h.ComputeHash(data, 0, count);
                var sb = new System.Text.StringBuilder();
                foreach (byte x in d) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string HexOf(HashAlgorithm h)
        {
            var sb = new System.Text.StringBuilder();
            foreach (byte x in h.Hash) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static int Main()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "amimg-tests");
            Directory.CreateDirectory(tmp);
            Console.WriteLine("AM-IMG engine self-test\n");

            // ---- T1: exact copy, hash correctness, odd size (not sector aligned)
            {
                byte[] src = RandomData(25 * 1024 * 1024 + 12345, 42);
                var input = new MemoryStream(src);
                var output = new MemoryStream();
                long progressLast = -1; bool monotonic = true;
                using (var hasher = SHA256.Create())
                {
                    long written = Engine.CopyCore(input, output, -1, 0, -1, hasher,
                        delegate(long done)
                        {
                            if (done < progressLast) monotonic = false;
                            progressLast = done;
                        }, CancellationToken.None);
                    Check(written == src.Length, "T1 length preserved (" + written + ")");
                    Check(HexOf(hasher) == Sha(src, src.Length), "T1 hash matches content");
                }
                byte[] outArr = output.ToArray();
                Check(outArr.Length == src.Length, "T1 output length");
                bool same = true;
                for (int i = 0; i < src.Length; i++) if (outArr[i] != src[i]) { same = false; break; }
                Check(same, "T1 byte-for-byte identical");
                Check(monotonic && progressLast == src.Length, "T1 progress monotonic and complete");
            }

            // ---- T2: sector padding pads final short block with zeros
            {
                byte[] src = RandomData(4 * 1024 * 1024 + 777, 7); // 777 bytes into a new chunk
                var output = new MemoryStream();
                using (var hasher = SHA256.Create())
                {
                    long written = Engine.CopyCore(new MemoryStream(src), output, -1, 512, -1,
                        hasher, null, CancellationToken.None);
                    Check(written == 4 * 1024 * 1024 + 1024, "T2 padded to sector multiple (" + written + ")");
                    byte[] outArr = output.ToArray();
                    bool dataOk = true;
                    for (int i = 0; i < src.Length; i++) if (outArr[i] != src[i]) { dataOk = false; break; }
                    bool padOk = true;
                    for (int i = src.Length; i < outArr.Length; i++) if (outArr[i] != 0) { padOk = false; break; }
                    Check(dataOk, "T2 data intact before padding");
                    Check(padOk, "T2 padding is zeros");
                    Check(HexOf(hasher) == Sha(outArr, outArr.Length), "T2 hash covers padded stream");
                }
            }

            // ---- T3: gzip round trip through the same streams the app uses
            {
                byte[] src = RandomData(9 * 1024 * 1024 + 31, 99);
                string gzPath = Path.Combine(tmp, "t3.img.gz");
                using (var f = new FileStream(gzPath, FileMode.Create))
                using (var gz = new GZipStream(f, CompressionLevel.Fastest))
                    Engine.CopyCore(new MemoryStream(src), gz, -1, 0, -1, null, null, CancellationToken.None);

                Check(Engine.IsGzipFile(gzPath), "T3 gz magic detected");

                var back = new MemoryStream();
                using (var f = new FileStream(gzPath, FileMode.Open))
                using (var gz = new GZipStream(f, CompressionMode.Decompress))
                using (var hasher = SHA256.Create())
                {
                    long written = Engine.CopyCore(gz, back, -1, 0, -1, hasher, null, CancellationToken.None);
                    Check(written == src.Length, "T3 decompressed length");
                    Check(HexOf(hasher) == Sha(src, src.Length), "T3 decompressed hash matches original");
                }
            }

            // ---- T4: gzip + sector padding (the write-to-drive path for .img.gz)
            {
                byte[] src = RandomData(3 * 1024 * 1024 + 100, 5);
                string gzPath = Path.Combine(tmp, "t4.img.gz");
                using (var f = new FileStream(gzPath, FileMode.Create))
                using (var gz = new GZipStream(f, CompressionLevel.Fastest))
                    Engine.CopyCore(new MemoryStream(src), gz, -1, 0, -1, null, null, CancellationToken.None);

                var device = new MemoryStream(); // stand-in for the raw device
                using (var f = new FileStream(gzPath, FileMode.Open))
                using (var gz = new GZipStream(f, CompressionMode.Decompress))
                {
                    long written = Engine.CopyCore(gz, device, -1, 4096, -1, null, null, CancellationToken.None);
                    Check(written % 4096 == 0, "T4 gz write is 4K-sector aligned (" + written + ")");
                    Check(written >= src.Length && written - src.Length < 4096, "T4 minimal padding");
                }
            }

            // ---- T5: oversize image rejected (image larger than device)
            {
                byte[] src = RandomData(2 * 1024 * 1024, 3);
                bool threw = false;
                try
                {
                    Engine.CopyCore(new MemoryStream(src), new MemoryStream(), -1, 512,
                        1024 * 1024, null, null, CancellationToken.None); // device only 1 MiB
                }
                catch (IOException) { threw = true; }
                Check(threw, "T5 oversize image throws IOException");
            }

            // ---- T6: cancellation stops the copy and throws
            {
                byte[] src = RandomData(64 * 1024 * 1024, 11);
                var ctsrc = new CancellationTokenSource();
                bool threw = false; long copied = 0;
                try
                {
                    Engine.CopyCore(new MemoryStream(src), new MemoryStream(), -1, 0, -1, null,
                        delegate(long done)
                        {
                            copied = done;
                            if (done >= 8 * 1024 * 1024) ctsrc.Cancel();
                        }, ctsrc.Token);
                }
                catch (OperationCanceledException) { threw = true; }
                Check(threw, "T6 cancellation throws OperationCanceledException");
                Check(copied < (long)src.Length, "T6 stopped early (" + copied + ")");
            }

            // ---- T7: inputLength cap (device read path: never read past device end)
            {
                byte[] src = RandomData(10 * 1024 * 1024, 21);
                var output = new MemoryStream();
                long cap = 6 * 1024 * 1024 + 512;
                long written = Engine.CopyCore(new MemoryStream(src), output, cap, 0, -1,
                    null, null, CancellationToken.None);
                Check(written == cap, "T7 reads exactly inputLength bytes (" + written + ")");
            }

            // ---- T8: drive enumeration finds disks, flags system disk, never lists it as safe
            {
                try
                {
                    var all = DriveList.GetDisks();
                    var safe = DriveList.GetSafeDisks();
                    Console.WriteLine("     disks found: " + all.Count + ", safe: " + safe.Count);
                    foreach (var d in all)
                        Console.WriteLine("       " + d.DisplayName +
                            (d.IsUsb ? " [USB]" : "") + (d.IsRemovable ? " [Removable]" : "") +
                            (d.IsSystem ? " [SYSTEM]" : ""));
                    bool sysFlagged = false, sysLeaked = false;
                    foreach (var d in all) if (d.IsSystem) sysFlagged = true;
                    foreach (var d in safe) if (d.IsSystem) sysLeaked = true;
                    Check(all.Count > 0, "T8 enumeration returns disks");
                    Check(sysFlagged, "T8 system disk is flagged");
                    Check(!sysLeaked, "T8 system disk never in safe list");
                }
                catch (Exception ex)
                {
                    Check(false, "T8 enumeration threw: " + ex.Message);
                }
            }

            try { Directory.Delete(tmp, true); } catch { }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}

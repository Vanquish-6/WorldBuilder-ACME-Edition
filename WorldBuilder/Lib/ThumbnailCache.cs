using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading.Tasks;

namespace WorldBuilder.Lib {
    /// <summary>
    /// Disk-based cache for object browser thumbnail images.
    /// Thumbnails are stored as PNG files keyed by object ID.
    /// </summary>
    public class ThumbnailCache {
        private readonly string _cacheDir;

        public ThumbnailCache() {
            _cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorldBuilder", "thumbnails");

            try {
                Directory.CreateDirectory(_cacheDir);
            }
            catch (Exception ex) {
                Console.WriteLine($"[ThumbnailCache] Failed to create cache directory: {ex.Message}");
            }
        }

        /// <summary>
        /// Try to load a cached thumbnail from disk.
        /// Returns null if no cached thumbnail exists for the given ID.
        /// </summary>
        public Bitmap? TryLoadCached(uint objectId) {
            var path = GetCachePath(objectId);
            if (!File.Exists(path)) return null;

            try {
                return new Bitmap(path);
            }
            catch (Exception ex) {
                Console.WriteLine($"[ThumbnailCache] Failed to load cached thumbnail 0x{objectId:X8}: {ex.Message}");
                // Delete corrupt cache file
                try { File.Delete(path); } catch { }
                return null;
            }
        }

        /// <summary>
        /// Save RGBA pixel data as a PNG to the cache directory.
        /// Runs on a background thread (fire-and-forget).
        /// </summary>
        public void SaveAsync(uint objectId, byte[] rgbaPixels, int width, int height) {
            Task.Run(() => {
                try {
                    var path = GetCachePath(objectId);
                    SaveRgbaAsPng(rgbaPixels, width, height, path);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[ThumbnailCache] Failed to save thumbnail 0x{objectId:X8}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Create an Avalonia Bitmap from raw RGBA pixel data (in-memory, no disk I/O).
        /// Uses WriteableBitmap for direct pixel copy -- no PNG encoding needed.
        /// </summary>
        public static WriteableBitmap CreateBitmapFromRgba(byte[] rgbaPixels, int width, int height) {
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888);
            using (var fb = bitmap.Lock()) {
                Marshal.Copy(rgbaPixels, 0, fb.Address, Math.Min(rgbaPixels.Length, fb.RowBytes * height));
            }
            return bitmap;
        }

        private string GetCachePath(uint objectId) {
            return Path.Combine(_cacheDir, $"{objectId:X8}.png");
        }

        private static void SaveRgbaAsPng(byte[] rgbaPixels, int width, int height, string path) {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            SaveRgbaAsPngStream(rgbaPixels, width, height, fs);
        }

        /// <summary>
        /// Write RGBA pixels as a PNG to a stream using a minimal PNG encoder.
        /// No external image library dependency required.
        /// </summary>
        private static void SaveRgbaAsPngStream(byte[] rgbaPixels, int width, int height, Stream output) {
            // PNG file structure: signature + IHDR + IDAT + IEND
            // leaveOpen: true so the caller can still use the stream after we're done
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

            // PNG signature
            writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            // IHDR chunk
            var ihdr = new byte[13];
            WriteInt32BE(ihdr, 0, width);
            WriteInt32BE(ihdr, 4, height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // color type: RGBA
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace
            WriteChunk(writer, "IHDR", ihdr);

            // IDAT chunk - filter rows + deflate
            using var idatStream = new MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(idatStream, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) {
                // zlib header (CMF + FLG)
                // We write raw deflate and wrap with zlib manually
            }

            // Use zlib-compatible compression
            var rawData = new MemoryStream();
            // zlib header
            rawData.WriteByte(0x78); // CMF: deflate, window size 32K
            rawData.WriteByte(0x01); // FLG: no dict, check bits

            using (var deflate = new System.IO.Compression.DeflateStream(rawData, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) {
                for (int y = 0; y < height; y++) {
                    deflate.WriteByte(0); // filter: None
                    deflate.Write(rgbaPixels, y * width * 4, width * 4);
                }
            }

            // Adler32 checksum of uncompressed data
            uint adler = ComputeAdler32(rgbaPixels, width, height);
            rawData.WriteByte((byte)(adler >> 24));
            rawData.WriteByte((byte)(adler >> 16));
            rawData.WriteByte((byte)(adler >> 8));
            rawData.WriteByte((byte)(adler));

            WriteChunk(writer, "IDAT", rawData.ToArray());

            // IEND chunk
            WriteChunk(writer, "IEND", Array.Empty<byte>());
        }

        private static uint ComputeAdler32(byte[] rgbaPixels, int width, int height) {
            uint a = 1, b = 0;
            for (int y = 0; y < height; y++) {
                // Filter byte (0 = None)
                a = (a + 0) % 65521;
                b = (b + a) % 65521;
                // Row data
                int offset = y * width * 4;
                for (int x = 0; x < width * 4; x++) {
                    a = (a + rgbaPixels[offset + x]) % 65521;
                    b = (b + a) % 65521;
                }
            }
            return (b << 16) | a;
        }

        private static void WriteChunk(BinaryWriter writer, string type, byte[] data) {
            // Length
            var lenBytes = new byte[4];
            WriteInt32BE(lenBytes, 0, data.Length);
            writer.Write(lenBytes);

            // Type
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            writer.Write(typeBytes);

            // Data
            writer.Write(data);

            // CRC32 over type + data
            uint crc = Crc32(typeBytes, data);
            var crcBytes = new byte[4];
            WriteInt32BE(crcBytes, 0, (int)crc);
            writer.Write(crcBytes);
        }

        private static void WriteInt32BE(byte[] buffer, int offset, int value) {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static readonly uint[] Crc32Table = GenerateCrc32Table();

        private static uint[] GenerateCrc32Table() {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) {
                uint c = i;
                for (int j = 0; j < 8; j++) {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
                table[i] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] type, byte[] data) {
            uint crc = 0xFFFFFFFF;
            foreach (var b in type) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in data) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }
}

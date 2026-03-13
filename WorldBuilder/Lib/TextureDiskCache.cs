using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace WorldBuilder.Lib {
    /// <summary>
    /// Disk-based cache for processed texture data (RGBA bytes after decompression/palette conversion).
    /// Keyed by (SurfaceId, PaletteId) to avoid re-decompressing DXT/INDEX16 textures every session.
    /// Thread-safe for concurrent reads and writes.
    /// </summary>
    public class TextureDiskCache {
        private readonly string _cacheDir;
        private readonly ConcurrentDictionary<string, byte[]> _memoryCache = new();
        private readonly ConcurrentDictionary<string, byte> _pendingWrites = new();

        /// <summary>
        /// Maximum number of entries to keep in the in-memory LRU layer.
        /// Entries beyond this are still on disk but must be read from file.
        /// </summary>
        private const int MaxMemoryCacheEntries = 512;

        public TextureDiskCache(string cacheDirectory) {
            _cacheDir = cacheDirectory;
            Directory.CreateDirectory(_cacheDir);
        }

        /// <summary>
        /// Gets the cache key for a texture identified by surface ID and palette ID.
        /// </summary>
        private static string GetCacheKey(uint surfaceId, uint paletteId) {
            return $"{surfaceId:X8}_{paletteId:X8}";
        }

        /// <summary>
        /// Gets the file path for a cache entry.
        /// Uses a two-level directory structure to avoid too many files in one folder.
        /// </summary>
        private string GetCachePath(string key) {
            var subDir = key.Substring(0, 2);
            var dir = Path.Combine(_cacheDir, subDir);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, key + ".texcache");
        }

        /// <summary>
        /// Tries to get cached texture data. Returns null if not cached.
        /// Checks in-memory cache first, then disk.
        /// </summary>
        public byte[]? TryGet(uint surfaceId, uint paletteId) {
            var key = GetCacheKey(surfaceId, paletteId);

            // Check memory cache first
            if (_memoryCache.TryGetValue(key, out var data)) {
                return (byte[])data.Clone();
            }

            // Check disk cache
            var path = GetCachePath(key);
            if (!File.Exists(path)) return null;

            try {
                data = File.ReadAllBytes(path);
                // Promote to memory cache (atomic add, bounded by MaxMemoryCacheEntries)
                if (_memoryCache.Count < MaxMemoryCacheEntries) {
                    _memoryCache.TryAdd(key, data);
                }
                return (byte[])data.Clone();
            }
            catch {
                // Corrupted cache entry, remove it
                try { File.Delete(path); } catch { }
                return null;
            }
        }

        /// <summary>
        /// Stores processed texture data in the cache.
        /// Writes to both memory and disk (disk write is fire-and-forget).
        /// </summary>
        public void Store(uint surfaceId, uint paletteId, byte[] data) {
            var key = GetCacheKey(surfaceId, paletteId);
            var copy = (byte[])data.Clone();

            // Store in memory cache (TryAdd is atomic; soft limit may be slightly exceeded under contention)
            if (_memoryCache.Count < MaxMemoryCacheEntries) {
                _memoryCache.TryAdd(key, copy);
            }

            // Write to disk asynchronously, skipping if another task is already writing this key
            if (!_pendingWrites.TryAdd(key, 0)) return;

            var path = GetCachePath(key);
            Task.Run(() => {
                try {
                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    fs.Write(copy, 0, copy.Length);
                }
                catch (IOException) {
                    // Another process/task holds the file -- safe to ignore; will be cached next session
                }
                catch (Exception ex) {
                    Console.WriteLine($"[TextureCache] Error writing cache file: {ex.Message}");
                }
                finally {
                    _pendingWrites.TryRemove(key, out _);
                }
            });
        }

        /// <summary>
        /// Clears all cached data (both memory and disk).
        /// </summary>
        public void Clear() {
            _memoryCache.Clear();
            try {
                if (Directory.Exists(_cacheDir)) {
                    Directory.Delete(_cacheDir, true);
                    Directory.CreateDirectory(_cacheDir);
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[TextureCache] Error clearing cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the approximate size of the disk cache in bytes.
        /// </summary>
        public long GetDiskCacheSize() {
            try {
                if (!Directory.Exists(_cacheDir)) return 0;
                long totalSize = 0;
                foreach (var file in Directory.EnumerateFiles(_cacheDir, "*.texcache", SearchOption.AllDirectories)) {
                    totalSize += new FileInfo(file).Length;
                }
                return totalSize;
            }
            catch {
                return 0;
            }
        }
    }
}

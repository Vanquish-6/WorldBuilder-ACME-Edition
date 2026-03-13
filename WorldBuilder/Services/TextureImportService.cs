using Avalonia.Media.Imaging;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using DatPixelFormat = DatReaderWriter.Enums.PixelFormat;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.Shared.Services;

namespace WorldBuilder.Services {

    /// <summary>
    /// Handles importing image files as custom textures, storing them in the project,
    /// and writing them to DATs during export.
    /// Custom textures are NOT written to the base DATs (to avoid corruption).
    /// They are only written to the export copy during ExportDats.
    /// </summary>
    public class TextureImportService {
        private readonly CustomTextureStore _store;
        private readonly Project _project;

        public CustomTextureStore Store => _store;

        public TextureImportService(CustomTextureStore store, Project project) {
            _store = store;
            _project = project;
            EnsureGidsAllocated();
        }

        private void EnsureGidsAllocated() {
            bool changed = false;
            foreach (var entry in _store.Entries) {
                if (entry.RenderSurfaceGid == 0) {
                    AllocateGidsForEntry(entry);
                    changed = true;
                }
            }
            if (changed) _store.Save();
        }

        private void AllocateGidsForEntry(CustomTextureEntry entry) {
            var existingRs = GetExistingRenderSurfaceIds();
            var existingSt = GetExistingSurfaceTextureIds();
            var allocatedRs = _store.Entries.Select(e => e.RenderSurfaceGid).Where(id => id != 0);
            var allocatedSt = _store.Entries.Select(e => e.SurfaceTextureGid).Where(id => id != 0);

            entry.RenderSurfaceGid = CustomTextureStore.AllocateGid(0x06000000, existingRs.Concat(allocatedRs));
            entry.SurfaceTextureGid = CustomTextureStore.AllocateGid(0x05000000, existingSt.Concat(allocatedSt));

            if (entry.Usage == CustomTextureUsage.DungeonSurface) {
                var existingSurf = GetExistingSurfaceIds();
                var allocatedSurf = _store.Entries.Select(e => e.SurfaceGid).Where(id => id != 0);
                entry.SurfaceGid = CustomTextureStore.AllocateSurfaceGid(existingSurf.Concat(allocatedSurf));
            }
        }

        private uint[] GetExistingRenderSurfaceIds() {
            try { return _project.DatReaderWriter.Dats.Portal.GetAllIdsOfType<RenderSurface>().ToArray(); }
            catch (Exception ex) {
                Console.WriteLine($"[TextureImport] Failed to read existing RenderSurface IDs: {ex.Message}");
                return Array.Empty<uint>();
            }
        }

        private uint[] GetExistingSurfaceTextureIds() {
            try { return _project.DatReaderWriter.Dats.Portal.GetAllIdsOfType<SurfaceTexture>().ToArray(); }
            catch (Exception ex) {
                Console.WriteLine($"[TextureImport] Failed to read existing SurfaceTexture IDs: {ex.Message}");
                return Array.Empty<uint>();
            }
        }

        private uint[] GetExistingSurfaceIds() {
            try { return _project.DatReaderWriter.Dats.Portal.GetAllIdsOfType<Surface>().ToArray(); }
            catch (Exception ex) {
                Console.WriteLine($"[TextureImport] Failed to read existing Surface IDs: {ex.Message}");
                return Array.Empty<uint>();
            }
        }

        public CustomTextureEntry ImportDungeonSurface(string imagePath, string name) {
            var entry = _store.Import(imagePath, name, CustomTextureUsage.DungeonSurface);
            AllocateGidsForEntry(entry);

            var storedPath = _store.GetImagePath(entry);
            using var img = Image.Load<Rgba32>(storedPath);
            entry.Width = img.Width;
            entry.Height = img.Height;

            _store.Save();
            return entry;
        }

        public CustomTextureEntry ImportTerrainReplacement(string imagePath, string name, TerrainTextureType terrainType) {
            var existing = _store.GetTerrainReplacement((int)terrainType);
            if (existing != null) {
                _store.Remove(existing.Id);
            }

            var entry = _store.Import(imagePath, name, CustomTextureUsage.TerrainReplace, (int)terrainType);
            AllocateGidsForEntry(entry);
            entry.Width = 512;
            entry.Height = 512;
            _store.Save();
            return entry;
        }

        /// <summary>
        /// Loads an image and converts to BGRA byte data for RenderSurface PFID_A8R8G8B8 format.
        /// </summary>
        public static byte[] LoadImageAsBgra(string imagePath, int targetWidth = 512, int targetHeight = 512) {
            using var img = Image.Load<Rgba32>(imagePath);

            if (img.Width != targetWidth || img.Height != targetHeight) {
                img.Mutate(x => x.Resize(targetWidth, targetHeight));
            }

            var bgra = new byte[targetWidth * targetHeight * 4];
            for (int y = 0; y < targetHeight; y++) {
                for (int x = 0; x < targetWidth; x++) {
                    var pixel = img[x, y];
                    int idx = (y * targetWidth + x) * 4;
                    bgra[idx + 0] = pixel.B;
                    bgra[idx + 1] = pixel.G;
                    bgra[idx + 2] = pixel.R;
                    bgra[idx + 3] = pixel.A;
                }
            }
            return bgra;
        }

        public static RenderSurface CreateRenderSurface(uint gid, byte[] bgraData, int width, int height) {
            return new RenderSurface {
                Id = gid,
                Width = width,
                Height = height,
                Format = DatPixelFormat.PFID_A8R8G8B8,
                SourceData = bgraData
            };
        }

        public static SurfaceTexture CreateSurfaceTexture(uint gid, uint renderSurfaceGid) {
            var st = new SurfaceTexture {
                Id = gid,
                Type = TextureType.Texture2D
            };
            st.Textures.Add(renderSurfaceGid);
            return st;
        }

        public static Surface CreateSurface(uint gid, uint surfaceTextureGid) {
            return new Surface {
                Id = gid,
                Type = SurfaceType.Base1Image,
                OrigTextureId = surfaceTextureGid,
                OrigPaletteId = 0,
                Translucency = 0f,
                Luminosity = 0f,
                Diffuse = 1f
            };
        }

        /// <summary>
        /// Writes all custom textures to DATs during export.
        /// Terrain replacements overwrite existing RenderSurface entries in-place
        /// (no new B-tree entries, no Region modification needed).
        /// Dungeon surfaces create new entries since they have no original to overwrite.
        /// </summary>
        public void WriteToDats(IDatReaderWriter writer, int? iteration = 0) {
            // Terrain replacements: overwrite existing RenderSurface in-place
            WriteTerrainReplacementsToDats(writer, iteration);

            // Dungeon surfaces: create new entries (these are genuinely new)
            foreach (var entry in _store.Entries.Where(e => e.Usage == CustomTextureUsage.DungeonSurface)) {
                var imagePath = _store.GetImagePath(entry);
                if (!File.Exists(imagePath)) continue;

                try {
                    var bgraData = LoadImageAsBgra(imagePath, entry.Width, entry.Height);

                    var rs = CreateRenderSurface(entry.RenderSurfaceGid, bgraData, entry.Width, entry.Height);
                    writer.TrySave(rs, iteration);

                    var st = CreateSurfaceTexture(entry.SurfaceTextureGid, entry.RenderSurfaceGid);
                    writer.TrySave(st, iteration);

                    if (entry.SurfaceGid != 0) {
                        var surf = CreateSurface(entry.SurfaceGid, entry.SurfaceTextureGid);
                        writer.TrySave(surf, iteration);
                    }

                    Console.WriteLine($"[TextureImport] Exported dungeon surface '{entry.Name}' (RS=0x{entry.RenderSurfaceGid:X8}, ST=0x{entry.SurfaceTextureGid:X8}, Surf=0x{entry.SurfaceGid:X8})");
                }
                catch (Exception ex) {
                    Console.WriteLine($"[TextureImport] Failed to write dungeon surface '{entry.Name}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Overwrites existing RenderSurface entries for terrain replacements.
        /// Instead of creating new GIDs and rewiring the Region, we find the original
        /// RenderSurface that each terrain type uses and overwrite its pixel data.
        /// This avoids inserting new B-tree entries which can corrupt the DAT.
        /// </summary>
        private void WriteTerrainReplacementsToDats(IDatReaderWriter writer, int? iteration) {
            var terrainReplacements = _store.GetTerrainReplacements().ToList();
            if (terrainReplacements.Count == 0) return;

            if (!writer.TryGet<Region>(0x13000000, out var region)) {
                Console.WriteLine("[TextureImport] Failed to load Region for terrain replacement");
                return;
            }

            foreach (var entry in terrainReplacements) {
                if (entry.ReplacesTerrainType == null) continue;
                var targetType = (TerrainTextureType)entry.ReplacesTerrainType.Value;

                var imagePath = _store.GetImagePath(entry);
                if (!File.Exists(imagePath)) continue;

                var desc = region.TerrainInfo.LandSurfaces.TexMerge.TerrainDesc
                    .FirstOrDefault(d => d.TerrainType == targetType);
                if (desc == null) {
                    Console.WriteLine($"[TextureImport] No TerrainDesc found for {targetType}");
                    continue;
                }

                var originalStId = desc.TerrainTex.TextureId;
                if (!writer.TryGet<SurfaceTexture>(originalStId, out var originalSt) || originalSt.Textures.Count == 0) {
                    Console.WriteLine($"[TextureImport] Failed to read SurfaceTexture 0x{originalStId:X8} for {targetType}");
                    continue;
                }

                var originalRsId = originalSt.Textures[^1];

                try {
                    var bgraData = LoadImageAsBgra(imagePath, entry.Width, entry.Height);
                    var rs = CreateRenderSurface(originalRsId, bgraData, entry.Width, entry.Height);
                    writer.TrySave(rs, iteration);

                    Console.WriteLine($"[TextureImport] Replaced terrain '{entry.Name}' by overwriting RS=0x{originalRsId:X8} (via ST=0x{originalStId:X8})");
                }
                catch (Exception ex) {
                    Console.WriteLine($"[TextureImport] Failed to replace terrain '{entry.Name}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Updates the Region's TerrainDesc for terrain replacements during export.
        /// No longer needed since we overwrite existing entries in-place, but kept
        /// for backward compatibility with dungeon surfaces or future use.
        /// </summary>
        public void UpdateRegionForTerrainReplacements(IDatReaderWriter writer, int? iteration = 0) {
            // Terrain replacements now overwrite existing RenderSurface entries in-place,
            // so no Region modification is needed. The existing TerrainDesc already points
            // to the correct SurfaceTexture/RenderSurface chain.
        }

        /// <summary>
        /// Generates an Avalonia thumbnail bitmap from a custom texture entry.
        /// </summary>
        public WriteableBitmap? GenerateThumbnail(CustomTextureEntry entry, int size = 64) {
            var imagePath = _store.GetImagePath(entry);
            if (!File.Exists(imagePath)) return null;

            try {
                using var img = Image.Load<Rgba32>(imagePath);
                if (img.Width != size || img.Height != size) {
                    img.Mutate(x => x.Resize(size, size));
                }

                var rgba = new byte[size * size * 4];
                for (int y = 0; y < size; y++) {
                    for (int x = 0; x < size; x++) {
                        var pixel = img[x, y];
                        int idx = (y * size + x) * 4;
                        rgba[idx + 0] = pixel.R;
                        rgba[idx + 1] = pixel.G;
                        rgba[idx + 2] = pixel.B;
                        rgba[idx + 3] = pixel.A;
                    }
                }

                var bitmap = new WriteableBitmap(
                    new Avalonia.PixelSize(size, size),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Rgba8888,
                    Avalonia.Platform.AlphaFormat.Premul);

                using (var fb = bitmap.Lock()) {
                    Marshal.Copy(rgba, 0, fb.Address, rgba.Length);
                }

                return bitmap;
            }
            catch {
                return null;
            }
        }

        /// <summary>
        /// Loads full-size RGBA data for a custom texture (for terrain atlas injection).
        /// </summary>
        public byte[]? LoadTextureRgba(CustomTextureEntry entry, int width = 512, int height = 512) {
            var imagePath = _store.GetImagePath(entry);
            if (!File.Exists(imagePath)) return null;

            try {
                using var img = Image.Load<Rgba32>(imagePath);
                if (img.Width != width || img.Height != height) {
                    img.Mutate(x => x.Resize(width, height));
                }

                var rgba = new byte[width * height * 4];
                for (int y = 0; y < height; y++) {
                    for (int x = 0; x < width; x++) {
                        var pixel = img[x, y];
                        int idx = (y * width + x) * 4;
                        rgba[idx + 0] = pixel.R;
                        rgba[idx + 1] = pixel.G;
                        rgba[idx + 2] = pixel.B;
                        rgba[idx + 3] = pixel.A;
                    }
                }
                return rgba;
            }
            catch {
                return null;
            }
        }
    }
}

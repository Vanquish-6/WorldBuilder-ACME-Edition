using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldBuilder.Shared.Services {

    public enum CustomTextureUsage {
        DungeonSurface,
        TerrainReplace,
        /// <summary>Replaces an existing RenderSurface by ID (e.g., monster/creature textures).</summary>
        RenderSurfaceReplace,
        /// <summary>Deprecated. Was used for unsafe new portal writes; remove entries from custom_textures.json.</summary>
        UiRenderSurface
    }

    public class CustomTextureEntry {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string ImageFileName { get; set; } = string.Empty;
        public CustomTextureUsage Usage { get; set; }

        /// <summary>
        /// For TerrainReplace: which TerrainTextureType (as int) this replaces.
        /// For RenderSurfaceReplace: the RenderSurface DID (0x05...) to overwrite.
        /// </summary>
        public int? ReplacesTerrainType { get; set; }

        /// <summary>
        /// For RenderSurfaceReplace: which RenderSurface DID (0x05...) to overwrite.
        /// </summary>
        public uint ReplacesRenderSurfaceId { get; set; }

        public uint RenderSurfaceGid { get; set; }
        public uint SurfaceTextureGid { get; set; }

        /// <summary>
        /// Only set for DungeonSurface usage.
        /// </summary>
        public uint SurfaceGid { get; set; }

        public int Width { get; set; } = 512;
        public int Height { get; set; } = 512;
    }

    /// <summary>
    /// Manages custom imported textures for a project. Stores image files in
    /// {ProjectDirectory}/custom_textures/ and metadata in custom_textures.json.
    /// </summary>
    public class CustomTextureStore {
        private static readonly JsonSerializerOptions _jsonOpts = new() {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _textureDir;
        private readonly string _metadataPath;
        private List<CustomTextureEntry> _entries = new();

        public IReadOnlyList<CustomTextureEntry> Entries => _entries.AsReadOnly();

        public CustomTextureStore(string projectDirectory) {
            _textureDir = Path.Combine(projectDirectory, "custom_textures");
            _metadataPath = Path.Combine(_textureDir, "custom_textures.json");
            Directory.CreateDirectory(_textureDir);
            Load();
        }

        private void Load() {
            if (File.Exists(_metadataPath)) {
                try {
                    var json = File.ReadAllText(_metadataPath);
                    _entries = JsonSerializer.Deserialize<List<CustomTextureEntry>>(json, _jsonOpts) ?? new();
                }
                catch {
                    _entries = new();
                }
            }
        }

        public void Save() {
            var json = JsonSerializer.Serialize(_entries, _jsonOpts);
            File.WriteAllText(_metadataPath, json);
        }

        public string GetImagePath(CustomTextureEntry entry) {
            return Path.Combine(_textureDir, entry.ImageFileName);
        }

        /// <summary>
        /// Imports an image file, copies it to the project, and registers it as a custom texture.
        /// Returns the new entry.
        /// </summary>
        public CustomTextureEntry Import(string sourceImagePath, string name, CustomTextureUsage usage, int? replacesTerrainType = null) {
            var entry = new CustomTextureEntry {
                Name = name,
                Usage = usage,
                ReplacesTerrainType = replacesTerrainType,
                ImageFileName = $"{Guid.NewGuid()}.png"
            };

            var destPath = GetImagePath(entry);
            File.Copy(sourceImagePath, destPath, overwrite: true);

            _entries.Add(entry);
            Save();
            return entry;
        }

        /// <summary>
        /// Assigns DAT GIDs to an entry. Called after GID allocation.
        /// </summary>
        public void AssignGids(CustomTextureEntry entry, uint renderSurfaceGid, uint surfaceTextureGid, uint surfaceGid = 0) {
            entry.RenderSurfaceGid = renderSurfaceGid;
            entry.SurfaceTextureGid = surfaceTextureGid;
            entry.SurfaceGid = surfaceGid;
            Save();
        }

        public void Remove(Guid entryId) {
            var entry = _entries.FirstOrDefault(e => e.Id == entryId);
            if (entry == null) return;

            var imagePath = GetImagePath(entry);
            if (File.Exists(imagePath)) {
                try { File.Delete(imagePath); } catch { }
            }

            _entries.Remove(entry);
            Save();
        }

        public IEnumerable<CustomTextureEntry> GetDungeonSurfaces() =>
            _entries.Where(e => e.Usage == CustomTextureUsage.DungeonSurface);

        public IEnumerable<CustomTextureEntry> GetTerrainReplacements() =>
            _entries.Where(e => e.Usage == CustomTextureUsage.TerrainReplace);

        public CustomTextureEntry? GetTerrainReplacement(int terrainType) =>
            _entries.FirstOrDefault(e => e.Usage == CustomTextureUsage.TerrainReplace && e.ReplacesTerrainType == terrainType);

        public IEnumerable<CustomTextureEntry> GetRenderSurfaceReplacements() =>
            _entries.Where(e => e.Usage == CustomTextureUsage.RenderSurfaceReplace);

        public CustomTextureEntry? GetRenderSurfaceReplacement(uint renderSurfaceId) =>
            _entries.FirstOrDefault(e => e.Usage == CustomTextureUsage.RenderSurfaceReplace && e.ReplacesRenderSurfaceId == renderSurfaceId);

        /// <summary>
        /// Allocates the next available GID in a range by scanning existing IDs.
        /// Uses a high sub-range (0xFFxxxx) to avoid conflicting with original DAT entries.
        /// </summary>
        public static uint AllocateGid(uint rangeBase, IEnumerable<uint> existingIds) {
            uint customBase = rangeBase | 0x00FF0000;
            uint maxExisting = existingIds
                .Where(id => id >= customBase)
                .DefaultIfEmpty(customBase)
                .Max();
            return maxExisting >= customBase ? maxExisting + 1 : customBase + 1;
        }

        /// <summary>
        /// For Surface IDs (0x0800xxxx range), uses a different allocation strategy
        /// since the range is only 0x08000000-0x0800FFFF.
        /// </summary>
        public static uint AllocateSurfaceGid(IEnumerable<uint> existingIds) {
            uint customBase = 0x0800F000;
            uint maxExisting = existingIds
                .Where(id => id >= customBase && id <= 0x0800FFFF)
                .DefaultIfEmpty(customBase)
                .Max();
            return maxExisting >= customBase ? maxExisting + 1 : customBase + 1;
        }
    }
}

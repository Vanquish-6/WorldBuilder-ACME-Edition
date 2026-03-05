using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Microsoft.Data.Sqlite;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Services;

namespace WorldBuilder.Shared.Models {
    public partial class Project : ObservableObject, IDisposable {
        private static JsonSerializerOptions _opts = new JsonSerializerOptions { WriteIndented = true };
        private string _filePath = string.Empty;

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private Guid _guid;

        [ObservableProperty] private bool _isHosting = false;

        [ObservableProperty] private string _remoteUrl = string.Empty;

        public string ProjectDirectory => Path.GetDirectoryName(FilePath) ?? string.Empty;
        public string DatDirectory => Path.Combine(ProjectDirectory, "dats");
        public string BaseDatDirectory => Path.Combine(DatDirectory, "base");
        public string DatabasePath => Path.Combine(ProjectDirectory, "project.db");

        [JsonIgnore] public string FilePath { get => _filePath; set => SetProperty(ref _filePath, value); }

        [JsonIgnore] public DocumentManager DocumentManager { get; set; }

        [JsonIgnore] public IDatReaderWriter DatReaderWriter { get; set; }

        [JsonIgnore] public CustomTextureStore CustomTextures { get; private set; }

        public static Project? FromDisk(string projectFilePath) {
            if (!File.Exists(projectFilePath)) {
                return null;
            }

            var project = JsonSerializer.Deserialize<Project>(File.ReadAllText(projectFilePath), _opts);
            if (project != null) {
                project.FilePath = projectFilePath;
                project.InitializeDatReaderWriter();
            }

            return project;
        }

        public static Project? Create(string projectName, string projectFilePath, string baseDatDirectory) {
            var projectDir = Path.GetDirectoryName(projectFilePath);
            if (!Directory.Exists(projectDir)) {
                Directory.CreateDirectory(projectDir);
            }

            var datDir = Path.Combine(projectDir, "dats");
            var baseDatDir = Path.Combine(datDir, "base");

            if (!Directory.Exists(baseDatDir)) {
                Directory.CreateDirectory(baseDatDir);
            }

            // Copy base dat files
            var datFiles = new[] {
                "client_cell_1.dat", "client_portal.dat", "client_highres.dat", "client_local_English.dat"
            };


            if (Directory.Exists(baseDatDirectory)) {
                foreach (var datFile in datFiles) {
                    var sourcePath = Path.Combine(baseDatDirectory, datFile);
                    var destPath = Path.Combine(baseDatDir, datFile);

                    if (File.Exists(sourcePath)) {
                        File.Copy(sourcePath, destPath, true);
                    }
                }
            }

            var project = new Project() { Name = projectName, FilePath = projectFilePath, Guid = Guid.NewGuid() };

            project.InitializeDatReaderWriter();
            project.Save();
            return project;
        }

        public Project() {
        }

        private void InitializeDatReaderWriter() {
            if (Directory.Exists(BaseDatDirectory)) {
                DatReaderWriter = new DefaultDatReaderWriter(BaseDatDirectory, DatAccessType.Read);
            }
            else {
                throw new DirectoryNotFoundException($"Base dat directory not found: {BaseDatDirectory}");
            }
            CustomTextures = new CustomTextureStore(ProjectDirectory);
        }

        public void Save() {
            var tmp = Path.GetTempFileName();
            try {
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, _opts));
                File.Move(tmp, FilePath, overwrite: true);
            }
            finally {
                if (File.Exists(tmp)) {
                    File.Delete(tmp);
                }
            }
        }

        /// <summary>
        /// ACE database connection settings for instance repositioning on export.
        /// Persisted per-project in the project JSON.
        /// </summary>
        public AceDbSettings? AceDb { get; set; }

        /// <summary>
        /// Called during export to write custom textures and update Region.
        /// Set by the UI layer (TextureImportService) since image decoding requires platform deps.
        /// </summary>
        [JsonIgnore]
        public Action<IDatReaderWriter, int?>? OnExportCustomTextures { get; set; }

        /// <summary>
        /// Called after DAT export with terrain change context so the UI layer can
        /// run the instance reposition workflow asynchronously.
        /// </summary>
        [JsonIgnore]
        public Func<RepositionContext, Task>? OnExportReposition { get; set; }

        public bool ExportDats(string exportDirectory, int portalIteration, Action<string>? onProgress = null) {
            if (!Directory.Exists(exportDirectory)) {
                Directory.CreateDirectory(exportDirectory);
            }

            // Copy base dats from project's base directory
            var datFiles = new[] {
                "client_cell_1.dat", "client_portal.dat", "client_highres.dat", "client_local_English.dat"
            };

            onProgress?.Invoke("Copying base DAT files...");
            foreach (var datFile in datFiles) {
                var sourcePath = Path.Combine(BaseDatDirectory, datFile);
                var destPath = Path.Combine(exportDirectory, datFile);

                if (File.Exists(sourcePath)) {
                    File.Copy(sourcePath, destPath, true);
                }
            }

            using var writer = new DefaultDatReaderWriter(exportDirectory, DatAccessType.ReadWrite);

            if (portalIteration == DatReaderWriter.Dats.Portal.Iteration.CurrentIteration) {
                portalIteration = 0;
            }

            var terrainDoc = DocumentManager.GetOrCreateDocumentAsync<TerrainDocument>("terrain").Result;

            // Collect all layers that are marked for export
            var exportLayers = new List<TerrainLayer>();
            if (terrainDoc.TerrainData.RootItems != null) {
                CollectExportLayers(terrainDoc.TerrainData.RootItems, exportLayers);
            }

            // No reverse -- iterate top-to-bottom for first-non-null per field
            // (CollectExportLayers already returns in tree order = top-to-bottom)

            // Identify all landblocks that need to be saved (modified in base OR any export layer)
            var modifiedLandblocks = new HashSet<ushort>(terrainDoc.TerrainData.Landblocks.Keys);
            var layerDocs = new Dictionary<string, LayerDocument>();

            foreach (var layer in exportLayers) {
                var layerDoc = DocumentManager.GetOrCreateDocumentAsync<LayerDocument>(layer.DocumentId).Result;
                if (layerDoc != null) {
                    layerDocs[layer.DocumentId] = layerDoc;
                    foreach (var lbKey in layerDoc.TerrainData.Landblocks.Keys) {
                        modifiedLandblocks.Add(lbKey);
                    }
                }
            }

            const int LANDBLOCK_SIZE = 81;

            onProgress?.Invoke($"Preparing {modifiedLandblocks.Count} terrain landblocks...");

            // Capture old terrain from base DATs for reposition delta calculation
            var oldTerrain = new Dictionary<ushort, TerrainEntry[]>();
            var newTerrain = new Dictionary<ushort, TerrainEntry[]>();

            foreach (var lbKey in modifiedLandblocks) {
                var baseLbId = (uint)(lbKey << 16) | 0xFFFF;
                if (DatReaderWriter.TryGet<LandBlock>(baseLbId, out var baseLb)) {
                    var entries = new TerrainEntry[LANDBLOCK_SIZE];
                    for (int i = 0; i < LANDBLOCK_SIZE; i++) {
                        entries[i] = new TerrainEntry(
                            baseLb.Terrain[i].Road,
                            baseLb.Terrain[i].Scenery,
                            (byte)baseLb.Terrain[i].Type,
                            baseLb.Height[i]);
                    }
                    oldTerrain[lbKey] = entries;
                }
            }

            // Process and save each modified landblock
            int terrainWritten = 0;
            foreach (var lbKey in modifiedLandblocks) {
                if (++terrainWritten % 1000 == 0) {
                    onProgress?.Invoke($"Writing terrain {terrainWritten} / {modifiedLandblocks.Count}...");
                }
                var lbId = (uint)(lbKey << 16) | 0xFFFF;

                var currentEntries = terrainDoc.GetLandblockInternal(lbKey);
                if (currentEntries == null) {
                    continue;
                }

                // Apply changes from each export layer using per-field masks (top-to-bottom)
                var resolved = new byte[LANDBLOCK_SIZE];
                foreach (var layer in exportLayers) {
                    if (!layerDocs.TryGetValue(layer.DocumentId, out var layerDoc)) continue;
                    if (!layerDoc.TerrainData.Landblocks.TryGetValue(lbKey, out var sparseCells)) continue;
                    layerDoc.TerrainData.FieldMasks.TryGetValue(lbKey, out var sparseMasks);

                    foreach (var (cellIdx, cellValue) in sparseCells) {
                        byte layerMask = (sparseMasks != null && sparseMasks.TryGetValue(cellIdx, out var m))
                            ? m
                            : TerrainFieldMask.All;

                        byte unclaimed = (byte)(layerMask & ~resolved[cellIdx]);
                        if (unclaimed == 0) continue;

                        var entry = new TerrainEntry(cellValue);
                        var current = currentEntries[cellIdx];

                        currentEntries[cellIdx] = new TerrainEntry(
                            road:    (unclaimed & TerrainFieldMask.Road) != 0    ? entry.Road    : current.Road,
                            scenery: (unclaimed & TerrainFieldMask.Scenery) != 0 ? entry.Scenery : current.Scenery,
                            type:    (unclaimed & TerrainFieldMask.Type) != 0    ? entry.Type    : current.Type,
                            height:  (unclaimed & TerrainFieldMask.Height) != 0  ? entry.Height  : current.Height
                        );

                        resolved[cellIdx] |= unclaimed;
                    }
                }

                // Snapshot the composited terrain for reposition
                var snapshot = new TerrainEntry[LANDBLOCK_SIZE];
                Array.Copy(currentEntries, snapshot, LANDBLOCK_SIZE);
                newTerrain[lbKey] = snapshot;

                if (!writer.TryGet<LandBlock>(lbId, out var lb)) {
                    continue;
                }

                for (var i = 0; i < LANDBLOCK_SIZE; i++) {
                    var entry = currentEntries[i];
                    lb.Terrain[i] = new() {
                        Road = entry.Road,
                        Scenery = entry.Scenery,
                        Type = (DatReaderWriter.Enums.TerrainTextureType)entry.Type
                    };
                    lb.Height[i] = entry.Height;
                }

                if (!writer.TrySave(lb, portalIteration)) {
                }
            }

            // Reposition DAT static objects (LandBlockInfo.Objects) to match new terrain heights
            float[]? repoHeightTable = null;
            if (DatReaderWriter.TryGet<Region>(0x13000000, out var repoRegion)) {
                repoHeightTable = repoRegion.LandDefs.LandHeightTable;
            }

            if (repoHeightTable != null && oldTerrain.Count > 0 && newTerrain.Count > 0) {
                onProgress?.Invoke("Repositioning DAT statics...");
                int repoCount = 0;
                foreach (var lbKey in modifiedLandblocks) {
                    if (!oldTerrain.TryGetValue(lbKey, out var oldEntries)) continue;
                    if (!newTerrain.TryGetValue(lbKey, out var newEntries2)) continue;

                    var infoId = (uint)(lbKey << 16) | 0xFFFE;
                    if (!writer.TryGet<LandBlockInfo>(infoId, out var lbi)) continue;
                    if (lbi.Objects == null || lbi.Objects.Count == 0) continue;

                    uint landblockX = (uint)(lbKey >> 8) & 0xFF;
                    uint landblockY = (uint)(lbKey & 0xFF);
                    bool anyMoved = false;

                    foreach (var stab in lbi.Objects) {
                        float localX = stab.Frame.Origin.X;
                        float localY = stab.Frame.Origin.Y;
                        if (localX < 0 || localX > 192f || localY < 0 || localY > 192f) continue;

                        float oldZ = TerrainHeightSampler.SampleHeightTriangle(
                            oldEntries, repoHeightTable, localX, localY, landblockX, landblockY);
                        float newZ = TerrainHeightSampler.SampleHeightTriangle(
                            newEntries2, repoHeightTable, localX, localY, landblockX, landblockY);

                        float delta = newZ - oldZ;
                        if (MathF.Abs(delta) < 0.01f) continue;

                        stab.Frame.Origin = new System.Numerics.Vector3(
                            stab.Frame.Origin.X,
                            stab.Frame.Origin.Y,
                            stab.Frame.Origin.Z + delta);
                        anyMoved = true;
                    }

                    if (anyMoved) {
                        writer.TrySave(lbi, portalIteration);
                        repoCount++;
                    }
                }
                onProgress?.Invoke($"Repositioned statics in {repoCount} landblocks");
            }

            onProgress?.Invoke("Writing static objects and dungeons...");

            // Export static object changes from LandblockDocuments, dungeon data, and portal table edits
            // Only save dirty documents -- streamed-in but unmodified docs must not overwrite
            // the base DAT content (which may have been repositioned above).
            foreach (var (docId, doc) in DocumentManager.ActiveDocs) {
                if (doc is LandblockDocument lbDoc) {
                    if (!lbDoc.IsDirty) continue;
                    lbDoc.SaveToDats(writer, portalIteration);
                }
                else if (doc is DungeonDocument dungeonDoc) {
                    dungeonDoc.SaveToDats(writer, portalIteration);
                }
                else if (doc is PortalDatDocument portalDoc) {
                    portalDoc.SaveToDats(writer, portalIteration);
                }
            }

            onProgress?.Invoke("Writing custom textures...");

            // Write custom imported textures and update Region for terrain replacements
            try {
                OnExportCustomTextures?.Invoke(writer, portalIteration);
            }
            catch (Exception ex) {
                Console.WriteLine($"[Export] Error writing custom textures: {ex.Message}");
            }

            // TODO: all other dat iterations
            writer.Dats.Portal.Iteration.CurrentIteration = portalIteration;

            onProgress?.Invoke("Running instance reposition...");

            // Instance reposition: build context and invoke hook if wired
            if (OnExportReposition != null && oldTerrain.Count > 0 && newTerrain.Count > 0) {
                float[]? heightTable = null;
                if (DatReaderWriter.TryGet<Region>(0x13000000, out var region)) {
                    heightTable = region.LandDefs.LandHeightTable;
                }

                if (heightTable != null) {
                    var ctx = new RepositionContext {
                        ModifiedLandblocks = modifiedLandblocks.ToArray(),
                        OldTerrain = oldTerrain,
                        NewTerrain = newTerrain,
                        LandHeightTable = heightTable,
                        ExportDirectory = exportDirectory
                    };
                    OnExportReposition(ctx).GetAwaiter().GetResult();
                }
            }

            return true;
        }

        private void CollectExportLayers(IEnumerable<TerrainLayerBase> items, List<TerrainLayer> result) {
            foreach (var item in items) {
                if (!item.IsExport) continue;

                if (item is TerrainLayerGroup group) {
                    CollectExportLayers(group.Children, result);
                }
                else if (item is TerrainLayer layer) {
                    result.Add(layer);
                }
            }
        }

        public void Dispose() {
            DatReaderWriter?.Dispose();
        }
    }
}

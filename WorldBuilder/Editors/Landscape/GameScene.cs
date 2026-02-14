using Chorizite.ACProtocol.Types;
using Chorizite.Core.Render;
using Chorizite.Core.Render.Vertex;
using Chorizite.OpenGLSDLBackend;
using DatReaderWriter.DBObjs;
using Silk.NET.OpenGL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape {
    public class GameScene : IDisposable {
        private const float PerspectiveProximityThreshold = 500f;
        private const int MaxLoadedLandblocks = 200;
        private const float SceneryDistanceThreshold = 600f;
        private const float DungeonDistanceThreshold = 400f;
        private const int MaxBatchSize = 30;

        private WorldBuilderSettings _settings => _terrainSystem.Settings;

        // Per-context state management
        private readonly ConcurrentDictionary<OpenGLRenderer, SceneContext> _contexts = new();
        private readonly TextureDiskCache _textureCache;

        private ThumbnailRenderService? _thumbnailService;
        public ThumbnailRenderService? ThumbnailService => _thumbnailService;
        private IDatReaderWriter _dats => _terrainSystem.Dats;
        private DocumentManager _documentManager => _terrainSystem.DocumentManager;
        private TerrainDocument _terrainDoc => _terrainSystem.TerrainDoc;
        private Region _region => _terrainSystem.Region;

        public TerrainDataManager DataManager { get; }
        public LandSurfaceManager SurfaceManager { get; }

        public PerspectiveCamera PerspectiveCamera { get; private set; }
        public OrthographicTopDownCamera TopDownCamera { get; private set; }
        public CameraManager CameraManager { get; private set; }

        private readonly Dictionary<ushort, List<StaticObject>> _sceneryObjects = new();
        private readonly Dictionary<ushort, List<StaticObject>> _dungeonStaticObjects = new();
        private readonly Dictionary<ushort, List<StaticObject>> _buildingStaticObjects = new();
        internal readonly TerrainSystem _terrainSystem;

        private const float WarmUpTimeBudgetMs = 12f;
        private const int MaxIntegratePerFrame = 8;
        private const int MaxUnloadsPerFrame = 2;
        private const int MaxGpuUploadsPerFrame = 4;
        private const float DocUpdateDistanceThresholdBase = 96f;
        private Vector3 _lastDocUpdatePosition = new(float.MinValue);
        private float _lastOrthoSize = -1f;
        private List<StaticObject>? _cachedStaticObjects;
        private bool _staticObjectsDirty = true;
        private bool _cachedShowStaticObjects = true;
        private bool _cachedShowScenery = true;
        private bool _cachedShowDungeons = true;
        private ushort? _cachedFocusedDungeonLB = null;

        // Reusable collections
        private readonly Dictionary<(uint Id, bool IsSetup), List<Matrix4x4>> _objectGroupBuffer = new();
        private readonly List<Matrix4x4> _tempInstanceTransforms = new();

        // Background loading
        private Task? _modelPrepTask;
        private const int MaxChunkUploadsPerFrame = 4;
        private readonly ConcurrentQueue<(TerrainChunk chunk, SceneContext context)> _chunkGenQueue = new();
        private Task? _chunkGenTask;
        private Task? _backgroundLoadTask;
        private readonly ConcurrentQueue<BackgroundLoadResult> _backgroundLoadResults = new();
        private readonly HashSet<ushort> _pendingLoadLandblocks = new();
        private readonly HashSet<ushort> _pendingSceneryRegen = new();
        private HashSet<ushort>? _lastVisibleLandblocks;

        public HashSet<ushort>? VisibleLandblocks => _lastVisibleLandblocks;

        // Expose object manager for tools (uses any available context)
        public StaticObjectManager? AnyObjectManager => _contexts.Values.FirstOrDefault()?.ObjectManager;
        internal EnvCellManager? _envCellManager => _contexts.Values.FirstOrDefault()?.EnvCellManager;

        private record BackgroundLoadResult(ushort LbKey, string DocId, List<StaticObject> Scenery, HashSet<(uint Id, bool IsSetup)> UniqueObjectIds, long LoadMs, long SceneryMs, int SceneryCount, PreparedEnvCellBatch? EnvCellBatch = null);

        private bool _disposed = false;
        private float _aspectRatio;

        // Rendering properties
        public float AmbientLightIntensity {
            get => _settings.Landscape.Rendering.LightIntensity;
            set => _settings.Landscape.Rendering.LightIntensity = value;
        }

        public bool ShowGrid {
            get => _settings.Landscape.Grid.ShowGrid;
            set => _settings.Landscape.Grid.ShowGrid = value;
        }

        public Vector3 LandblockGridColor {
            get => _settings.Landscape.Grid.LandblockColor;
            set => _settings.Landscape.Grid.LandblockColor = value;
        }

        public Vector3 CellGridColor {
            get => _settings.Landscape.Grid.CellColor;
            set => _settings.Landscape.Grid.CellColor = value;
        }

        public float GridLineWidth {
            get => _settings.Landscape.Grid.LineWidth;
            set => _settings.Landscape.Grid.LineWidth = value;
        }

        public float GridOpacity {
            get => _settings.Landscape.Grid.Opacity;
            set => _settings.Landscape.Grid.Opacity = value;
        }

        public Vector3 SphereColor {
            get => _settings.Landscape.Selection.SphereColor;
            set => _settings.Landscape.Selection.SphereColor = value;
        }

        public float SphereRadius {
            get => _settings.Landscape.Selection.SphereRadius;
            set => _settings.Landscape.Selection.SphereRadius = value;
        }

        public bool ShowStaticObjects {
            get => _settings.Landscape.Overlay.ShowStaticObjects;
            set => _settings.Landscape.Overlay.ShowStaticObjects = value;
        }

        public bool ShowScenery {
            get => _settings.Landscape.Overlay.ShowScenery;
            set => _settings.Landscape.Overlay.ShowScenery = value;
        }

        public bool ShowDungeons {
            get => _settings.Landscape.Overlay.ShowDungeons;
            set => _settings.Landscape.Overlay.ShowDungeons = value;
        }

        public bool ShowSlopeHighlight {
            get => _settings.Landscape.Overlay.ShowSlopeHighlight;
            set => _settings.Landscape.Overlay.ShowSlopeHighlight = value;
        }

        public float SlopeThreshold {
            get => _settings.Landscape.Overlay.SlopeThreshold;
            set => _settings.Landscape.Overlay.SlopeThreshold = value;
        }

        public Vector3 SlopeHighlightColor {
            get => _settings.Landscape.Overlay.SlopeHighlightColor;
            set => _settings.Landscape.Overlay.SlopeHighlightColor = value;
        }

        public float SlopeHighlightOpacity {
            get => _settings.Landscape.Overlay.SlopeHighlightOpacity;
            set => _settings.Landscape.Overlay.SlopeHighlightOpacity = value;
        }

        public float SphereHeightOffset { get; set; } = 0.0f;
        public Vector3 LightDirection { get; set; } = new Vector3(0.5f, 0.3f, -0.3f);
        public float SpecularPower { get; set; } = 32.0f;
        public Vector3 SphereGlowColor { get; set; } = new(0);
        public float SphereGlowIntensity { get; set; } = 1.0f;
        public float SphereGlowPower { get; set; } = 0.5f;

        public GameScene(TerrainSystem terrainSystem) {
            _terrainSystem = terrainSystem;
            var mapCenter = new Vector3(192f * 254f / 2f, 192f * 254f / 2f, 1000);
            PerspectiveCamera = new PerspectiveCamera(mapCenter, _settings);
            TopDownCamera = new OrthographicTopDownCamera(mapCenter, _settings);

            var camSettings = _settings.Landscape.Camera;
            if (camSettings.HasSavedPosition) {
                var savedPos = new Vector3(camSettings.SavedPositionX, camSettings.SavedPositionY, camSettings.SavedPositionZ);
                PerspectiveCamera.SetPosition(savedPos);
                if (!float.IsNaN(camSettings.SavedYaw) && !float.IsNaN(camSettings.SavedPitch)) {
                    PerspectiveCamera.SetYawPitch(camSettings.SavedYaw, camSettings.SavedPitch);
                }
                TopDownCamera.SetPosition(savedPos);
                if (!float.IsNaN(camSettings.SavedOrthoSize)) {
                    TopDownCamera.OrthographicSize = camSettings.SavedOrthoSize;
                }
            }

            CameraManager = new CameraManager(camSettings.SavedIs3D ? PerspectiveCamera : TopDownCamera);

            DataManager = new TerrainDataManager(terrainSystem, 16);
            SurfaceManager = new LandSurfaceManager(_dats, _region);

            var textureCacheDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "WorldBuilder", "TextureCache");
            _textureCache = new TextureDiskCache(textureCacheDir);
        }

        public static string GetEmbeddedResource(string filename, Assembly assembly) {
            using (Stream stream = assembly.GetManifestResourceStream(filename))
            using (StreamReader reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }

        public void AddStaticObject(string landblockId, StaticObject obj) {
            var doc = _documentManager.GetOrCreateDocumentAsync(landblockId, typeof(LandblockDocument)).GetAwaiter()
                .GetResult();
            if (doc is LandblockDocument lbDoc) {
                lbDoc.Apply(new StaticObjectUpdateEvent(obj, true));
            }
        }

        public void RemoveStaticObject(string landblockId, StaticObject obj) {
            var doc = _documentManager.GetOrCreateDocumentAsync(landblockId, typeof(LandblockDocument)).GetAwaiter()
                .GetResult();
            if (doc is LandblockDocument lbDoc) {
                lbDoc.Apply(new StaticObjectUpdateEvent(obj, false));
            }
        }

        public void Update(Vector3 cameraPosition, Matrix4x4 viewProjectionMatrix) {
            var sw = Stopwatch.StartNew();

            var frustum = new Frustum(viewProjectionMatrix);
            var requiredChunks = DataManager.GetRequiredChunks(cameraPosition);

            IntegrateBackgroundLoadResults();
            ProcessPendingSceneryRegen();

            float docUpdateThreshold = DocUpdateDistanceThresholdBase;
            bool zoomChanged = false;
            if (CameraManager?.Current is OrthographicTopDownCamera ortho) {
                if (ortho.OrthographicSize > 1800f) {
                    docUpdateThreshold = Math.Max(48f, DocUpdateDistanceThresholdBase * (1800f / ortho.OrthographicSize));
                }
                if (_lastOrthoSize > 0 && MathF.Abs(ortho.OrthographicSize - _lastOrthoSize) / _lastOrthoSize > 0.1f) {
                    zoomChanged = true;
                }
                _lastOrthoSize = ortho.OrthographicSize;
            }
            float distMoved = Vector3.Distance(cameraPosition, _lastDocUpdatePosition);
            if (distMoved >= docUpdateThreshold || zoomChanged || _lastDocUpdatePosition.X == float.MinValue) {
                _lastDocUpdatePosition = cameraPosition;
                KickOffBackgroundLoads(cameraPosition);
                UnloadOutOfRangeLandblocks(cameraPosition);
                UnloadDistantDungeons(cameraPosition);
                UnloadDistantChunks(cameraPosition);
                RefreshSceneryForNearbyLandblocks(cameraPosition);
                ProcessPendingSceneryRegen();
            }

            long staticMs = sw.ElapsedMilliseconds;

            foreach (var context in _contexts.Values) {
                foreach (var chunkId in requiredChunks) {
                    var chunkX = (uint)(chunkId >> 32);
                    var chunkY = (uint)(chunkId & 0xFFFFFFFF);
                    var chunk = DataManager.GetOrCreateChunk(chunkX, chunkY);

                    if (!context.GPUManager.HasRenderData(chunkId)) {
                        QueueChunkForGeneration(chunk, context);
                    }
                    else if (chunk.IsDirty) {
                        var dirtyLandblocks = chunk.DirtyLandblocks.ToList();
                        context.GPUManager.UpdateLandblocks(chunk, dirtyLandblocks, _terrainSystem);
                    }
                }
            }

            long totalMs = sw.ElapsedMilliseconds;
            if (totalMs > 200) {
                Console.WriteLine($"[GameScene.Update] {totalMs}ms total (statics: {staticMs}ms, terrain: {totalMs - staticMs}ms)");
            }
        }

        private void ProcessPendingUploads(SceneContext context) {
            WarmUpRenderData(context);
            context.EnvCellManager.ProcessUploads(maxPerFrame: 2);
            ProcessChunkUploads(context);
        }

        private void IntegrateBackgroundLoadResults() {
            int integrated = 0;
            while (integrated < MaxIntegratePerFrame && _backgroundLoadResults.TryDequeue(out var result)) {
                _sceneryObjects[result.LbKey] = result.Scenery;
                _pendingLoadLandblocks.Remove(result.LbKey);

                foreach (var (id, isSetup) in result.UniqueObjectIds) {
                    foreach (var context in _contexts.Values) {
                        if (context.ObjectManager.TryGetCachedRenderData(id) == null && !context.ObjectManager.IsKnownFailure(id)) {
                            context.ModelWarmupQueue.Enqueue((id, isSetup));
                        }
                    }
                }

                if (result.EnvCellBatch != null) {
                    foreach (var context in _contexts.Values) {
                        context.EnvCellManager.QueueForUpload(result.EnvCellBatch);
                    }

                    // Add dungeon static objects (only shown when dungeon is focused)
                    if (result.EnvCellBatch.DungeonStaticObjects.Count > 0) {
                        _dungeonStaticObjects[result.LbKey] = result.EnvCellBatch.DungeonStaticObjects;
                        foreach (var context in _contexts.Values) {
                            foreach (var obj in result.EnvCellBatch.DungeonStaticObjects) {
                                if (context.ObjectManager.TryGetCachedRenderData(obj.Id) == null && !context.ObjectManager.IsKnownFailure(obj.Id)) {
                                    context.ModelWarmupQueue.Enqueue((obj.Id, obj.IsSetup));
                                }
                            }
                        }
                    }

                    // Add building interior static objects (always shown with regular statics)
                    if (result.EnvCellBatch.BuildingStaticObjects.Count > 0) {
                        _buildingStaticObjects[result.LbKey] = result.EnvCellBatch.BuildingStaticObjects;
                        foreach (var context in _contexts.Values) {
                            foreach (var obj in result.EnvCellBatch.BuildingStaticObjects) {
                                if (context.ObjectManager.TryGetCachedRenderData(obj.Id) == null && !context.ObjectManager.IsKnownFailure(obj.Id)) {
                                    context.ModelWarmupQueue.Enqueue((obj.Id, obj.IsSetup));
                                }
                            }
                        }
                    }
                }

                _staticObjectsDirty = true;
                integrated++;
            }
        }

        private void KickOffBackgroundLoads(Vector3 cameraPosition) {
            if (_backgroundLoadTask != null && !_backgroundLoadTask.IsCompleted) return;

            var visibleLandblocks = GetProximateLandblocks(cameraPosition);
            _lastVisibleLandblocks = visibleLandblocks;
            var currentLoaded = _documentManager.ActiveDocs.Keys.Where(k => k.StartsWith("landblock_")).ToHashSet();

            var toLoad = visibleLandblocks
                .Where(lbKey => !currentLoaded.Contains($"landblock_{lbKey:X4}") && !_pendingLoadLandblocks.Contains(lbKey))
                .ToList();

            if (toLoad.Count == 0) return;

            var camPos2D = new Vector2(cameraPosition.X, cameraPosition.Y);
            toLoad.Sort((a, b) => {
                float distA = Vector2.Distance(camPos2D, LandblockCenter(a));
                float distB = Vector2.Distance(camPos2D, LandblockCenter(b));
                return distA.CompareTo(distB);
            });

            if (toLoad.Count > MaxBatchSize) {
                toLoad = toLoad.Take(MaxBatchSize).ToList();
            }

            foreach (var lbKey in toLoad) {
                _pendingLoadLandblocks.Add(lbKey);
            }

            var documentManager = _documentManager;
            var terrainSystem = _terrainSystem;
            var region = _region;
            var dats = _dats;
            var resultQueue = _backgroundLoadResults;
            var sceneryThreshold = GetEffectiveSceneryThreshold();
            var dungeonThreshold = GetEffectiveDungeonThreshold();
            var camPosCapture = cameraPosition;
            var envCellManager = _contexts.Values.FirstOrDefault()?.EnvCellManager;

            _backgroundLoadTask = Task.Run(() => {
                var batchSw = Stopwatch.StartNew();
                int loaded = 0;

                foreach (var lbKey in toLoad) {
                    var docId = $"landblock_{lbKey:X4}";
                    try {
                        var loadSw = Stopwatch.StartNew();
                        var doc = documentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                        long loadMs = loadSw.ElapsedMilliseconds;

                        if (doc != null) {
                            var lbCenter2D = LandblockCenter(lbKey);
                            float distFromCamera = Vector2.Distance(
                                new Vector2(camPosCapture.X, camPosCapture.Y), lbCenter2D);

                            List<StaticObject> scenery;
                            long sceneryMs = 0;
                            if (distFromCamera <= sceneryThreshold) {
                                var scenerySw = Stopwatch.StartNew();
                                scenery = GenerateSceneryThreadSafe(lbKey, doc, terrainSystem, region, dats);
                                sceneryMs = scenerySw.ElapsedMilliseconds;
                            }
                            else {
                                scenery = new List<StaticObject>();
                            }

                            var uniqueIds = new HashSet<(uint, bool)>();
                            foreach (var obj in doc.GetStaticObjects()) {
                                uniqueIds.Add((obj.Id, obj.IsSetup));
                            }
                            foreach (var obj in scenery) {
                                uniqueIds.Add((obj.Id, obj.IsSetup));
                            }

                            PreparedEnvCellBatch? envCellBatch = null;
                            if (envCellManager != null && distFromCamera <= dungeonThreshold && !envCellManager.HasLoadedCells(lbKey)) {
                                uint lbId = (uint)lbKey;
                                uint infoId = lbId << 16 | 0xFFFE;
                                if (dats.TryGet<LandBlockInfo>(infoId, out var lbi) && lbi.NumCells > 0) {
                                    var envCells = new List<EnvCell>();
                                    for (uint c = 0x0100; c < 0x0100 + lbi.NumCells; c++) {
                                        uint cellId = (lbId << 16) | c;
                                        if (dats.TryGet<EnvCell>(cellId, out var envCell)) {
                                            envCells.Add(envCell);
                                        }
                                    }
                                    if (envCells.Count > 0) {
                                        // Dungeon-only landblocks have cells but no buildings.
                                        // Building interiors have cells AND buildings on the surface.
                                        bool isDungeonOnly = lbi.Buildings == null || lbi.Buildings.Count == 0;
                                        envCellBatch = envCellManager.PrepareLandblockEnvCells(lbKey, lbId, envCells, isDungeonOnly);
                                    }
                                }
                            }

                            resultQueue.Enqueue(new BackgroundLoadResult(lbKey, docId, scenery, uniqueIds, loadMs, sceneryMs, scenery.Count, envCellBatch));
                            loaded++;
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[Statics] Error loading {docId} on background thread: {ex.Message}");
                        resultQueue.Enqueue(new BackgroundLoadResult(lbKey, docId, new List<StaticObject>(), new HashSet<(uint, bool)>(), 0, 0, 0));
                    }
                }
            });
        }

        private static Vector2 LandblockCenter(ushort lbKey) {
            int lbX = (lbKey >> 8) & 0xFF;
            int lbY = lbKey & 0xFF;
            return new Vector2(
                lbX * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2f,
                lbY * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2f);
        }

        private void UnloadOutOfRangeLandblocks(Vector3 cameraPosition) {
            var keepLoadedSet = GetUnloadBoundaryLandblocks(cameraPosition);
            var currentLoaded = _documentManager.ActiveDocs.Keys.Where(k => k.StartsWith("landblock_")).ToHashSet();

            var toUnload = currentLoaded
                .Where(docId => !keepLoadedSet.Contains(ushort.Parse(docId.Replace("landblock_", ""), System.Globalization.NumberStyles.HexNumber)))
                .Take(MaxUnloadsPerFrame)
                .ToList();

            foreach (var docId in toUnload) {
                try {
                    var sw = Stopwatch.StartNew();
                    var lbKey = ushort.Parse(docId.Replace("landblock_", ""), System.Globalization.NumberStyles.HexNumber);
                    if (_sceneryObjects.TryGetValue(lbKey, out var scenery)) {
                        foreach (var obj in scenery) {
                            foreach (var context in _contexts.Values) {
                                context.ObjectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                            }
                        }
                        _sceneryObjects.Remove(lbKey);
                    }

                    if (_documentManager.ActiveDocs.TryGetValue(docId, out var baseDoc) && baseDoc is LandblockDocument lbDoc) {
                        foreach (var obj in lbDoc.GetStaticObjects()) {
                            foreach (var context in _contexts.Values) {
                                context.ObjectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                            }
                        }
                    }

                    foreach (var context in _contexts.Values) {
                        context.EnvCellManager.UnloadLandblock(lbKey);
                    }

                    if (_dungeonStaticObjects.TryGetValue(lbKey, out var dungeonObjs)) {
                        foreach (var obj in dungeonObjs) {
                            foreach (var context in _contexts.Values) {
                                context.ObjectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                            }
                        }
                        _dungeonStaticObjects.Remove(lbKey);
                    }
                    if (_buildingStaticObjects.TryGetValue(lbKey, out var buildingObjs)) {
                        foreach (var obj in buildingObjs) {
                            foreach (var context in _contexts.Values) {
                                context.ObjectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                            }
                        }
                        _buildingStaticObjects.Remove(lbKey);
                    }

                    _ = _documentManager.CloseDocumentAsync(docId);
                    _staticObjectsDirty = true;
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Statics] Error unloading {docId}: {ex.Message}");
                }
            }

            if (toUnload.Count >= MaxUnloadsPerFrame) {
                _lastDocUpdatePosition = new Vector3(float.MinValue);
            }
        }

        private void UnloadDistantDungeons(Vector3 cameraPosition) {
            float unloadThreshold = DungeonDistanceThreshold * 1.5f;
            var camPos2D = new Vector2(cameraPosition.X, cameraPosition.Y);

            var manager = _contexts.Values.FirstOrDefault()?.EnvCellManager;
            if (manager == null) return;

            var toUnload = new List<ushort>();
            foreach (var lbKey in manager.GetLoadedLandblockKeys()) {
                float dist = Vector2.Distance(camPos2D, LandblockCenter(lbKey));
                if (dist > unloadThreshold) {
                    toUnload.Add(lbKey);
                }
            }

            foreach (var lbKey in toUnload) {
                foreach (var context in _contexts.Values) {
                    context.EnvCellManager.UnloadLandblock(lbKey);
                }

                if (_dungeonStaticObjects.TryGetValue(lbKey, out var dungeonObjs)) {
                    foreach (var obj in dungeonObjs) {
                        foreach (var context in _contexts.Values) {
                            context.ObjectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                        }
                    }
                    _dungeonStaticObjects.Remove(lbKey);
                }
                if (_buildingStaticObjects.TryGetValue(lbKey, out var buildingObjs2)) {
                    foreach (var obj in buildingObjs2) {
                        foreach (var context in _contexts.Values) {
                            context.ObjectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                        }
                    }
                    _buildingStaticObjects.Remove(lbKey);
                }
                _staticObjectsDirty = true;
            }
        }

        private void UnloadDistantChunks(Vector3 cameraPosition) {
            var chunksToUnload = DataManager.GetChunksToUnload(cameraPosition);
            foreach (var chunkId in chunksToUnload) {
                foreach (var context in _contexts.Values) {
                    context.ChunksInFlight.Remove(chunkId);
                    context.GPUManager.DisposeChunkResources(chunkId);
                }
                DataManager.RemoveChunk(chunkId);
            }
        }

        private void QueueChunkForGeneration(TerrainChunk chunk, SceneContext context) {
            var chunkId = chunk.GetChunkId();
            if (context.ChunksInFlight.Contains(chunkId) || context.GPUManager.HasRenderData(chunkId)) return;

            context.ChunksInFlight.Add(chunkId);
            _chunkGenQueue.Enqueue((chunk, context));

            if (_chunkGenTask == null || _chunkGenTask.IsCompleted) {
                var terrainSystem = _terrainSystem;
                _chunkGenTask = Task.Run(() => ProcessChunkGenQueue(terrainSystem));
            }
        }

        private void ProcessChunkGenQueue(TerrainSystem terrainSystem) {
            while (_chunkGenQueue.TryDequeue(out var item)) {
                var (chunk, context) = item;
                var chunkId = chunk.GetChunkId();
                try {
                    var prepared = TerrainGPUResourceManager.PrepareChunkGeometry(chunk, terrainSystem);
                    context.ChunkUploadQueue.Enqueue(prepared);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Terrain] Background chunk gen error for ({chunk.ChunkX},{chunk.ChunkY}): {ex.Message}");
                    context.ChunksInFlight.Remove(chunkId);
                }
            }
        }

        private void ProcessChunkUploads(SceneContext context) {
            int uploaded = 0;
            while (uploaded < MaxChunkUploadsPerFrame && context.ChunkUploadQueue.TryDequeue(out var prepared)) {
                try {
                    context.GPUManager.UploadChunkToGPU(prepared);
                    uploaded++;
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Terrain] Chunk upload error: {ex.Message}");
                }
            }
        }

        private void WarmUpRenderData(SceneContext context) {
            UploadPreparedModels(context);
            KickOffModelPreparation(context);
        }

        private void UploadPreparedModels(SceneContext context) {
            if (context.ModelUploadQueue.IsEmpty) return;

            var sw = Stopwatch.StartNew();
            int uploaded = 0;

            while (uploaded < MaxGpuUploadsPerFrame && context.ModelUploadQueue.TryDequeue(out var prepared)) {
                if (sw.ElapsedMilliseconds >= WarmUpTimeBudgetMs && uploaded > 0) {
                    var temp = new List<PreparedModelData> { prepared };
                    while (context.ModelUploadQueue.TryDequeue(out var remaining)) temp.Add(remaining);
                    foreach (var item in temp) context.ModelUploadQueue.Enqueue(item);
                    break;
                }

                if (context.ObjectManager.TryGetCachedRenderData(prepared.Id) != null) {
                    context.ModelsPreparing.Remove(prepared.Id);
                    continue;
                }

                try {
                    var data = context.ObjectManager.FinalizeGpuUpload(prepared);
                    uploaded++;

                    if (data != null && data.IsSetup && data.SetupParts != null) {
                        foreach (var (partId, _) in data.SetupParts) {
                            if (context.ObjectManager.TryGetCachedRenderData(partId) == null &&
                                !context.ObjectManager.IsKnownFailure(partId) &&
                                !context.ModelsPreparing.Contains(partId)) {
                                context.ModelWarmupQueue.Enqueue((partId, false));
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Statics] GPU upload error for 0x{prepared.Id:X8}: {ex.Message}");
                }
                finally {
                    context.ModelsPreparing.Remove(prepared.Id);
                }
            }
        }

        private void KickOffModelPreparation(SceneContext context) {
            if (context.ModelWarmupQueue.Count == 0) return;
            // Use global _modelPrepTask to avoid thread explosion, but handle per-context queues?
            // Actually, we can run one task per context batch.
            // Or shared task.
            if (_modelPrepTask != null && !_modelPrepTask.IsCompleted) return;

            var batch = new List<(uint Id, bool IsSetup)>();
            while (context.ModelWarmupQueue.Count > 0 && batch.Count < 16) {
                var (id, isSetup) = context.ModelWarmupQueue.Dequeue();
                if (context.ObjectManager.TryGetCachedRenderData(id) != null || context.ModelsPreparing.Contains(id)) continue;
                context.ModelsPreparing.Add(id);
                batch.Add((id, isSetup));
            }

            if (batch.Count == 0) return;

            var objectManager = context.ObjectManager;
            var resultQueue = context.ModelUploadQueue;

            _modelPrepTask = Task.Run(() => {
                foreach (var (id, isSetup) in batch) {
                    try {
                        var data = objectManager.PrepareModelData(id, isSetup);
                        if (data != null) {
                            resultQueue.Enqueue(data);
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[Statics] Background prep error for 0x{id:X8}: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Regenerates scenery for modified landblocks on a background thread.
        /// </summary>
        private void ProcessPendingSceneryRegen() {
            if (_pendingSceneryRegen.Count == 0) return;
            if (_backgroundLoadTask != null && !_backgroundLoadTask.IsCompleted) return;

            var toRegen = _pendingSceneryRegen.ToList();
            _pendingSceneryRegen.Clear();

            foreach (var lbKey in toRegen) {
                _pendingLoadLandblocks.Add(lbKey);
            }

            var documentManager = _documentManager;
            var terrainSystem = _terrainSystem;
            var region = _region;
            var dats = _dats;
            var resultQueue = _backgroundLoadResults;

            _backgroundLoadTask = Task.Run(() => {
                foreach (var lbKey in toRegen) {
                    var docId = $"landblock_{lbKey:X4}";
                    try {
                        var doc = documentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                        if (doc != null) {
                            var sw = Stopwatch.StartNew();
                            var scenery = GenerateSceneryThreadSafe(lbKey, doc, terrainSystem, region, dats);
                            long sceneryMs = sw.ElapsedMilliseconds;

                            var uniqueIds = new HashSet<(uint, bool)>();
                            foreach (var obj in doc.GetStaticObjects()) uniqueIds.Add((obj.Id, obj.IsSetup));
                            foreach (var obj in scenery) uniqueIds.Add((obj.Id, obj.IsSetup));

                            resultQueue.Enqueue(new BackgroundLoadResult(lbKey, docId, scenery, uniqueIds, 0, sceneryMs, scenery.Count));
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[Statics] Error regenerating scenery for {docId}: {ex.Message}");
                        resultQueue.Enqueue(new BackgroundLoadResult(lbKey, docId, new List<StaticObject>(), new HashSet<(uint, bool)>(), 0, 0, 0));
                    }
                }
            });
        }

        private static List<StaticObject> GenerateSceneryThreadSafe(
            ushort lbKey, LandblockDocument lbDoc,
            TerrainSystem terrainSystem, Region region, IDatReaderWriter dats) {

            var scenery = new List<StaticObject>();
            var lbId = (uint)lbKey;
            var lbTerrainEntries = terrainSystem.GetLandblockTerrain(lbKey);
            if (lbTerrainEntries == null) return scenery;

            var buildings = new HashSet<int>();
            var lbGlobalX = (lbId >> 8) & 0xFF;
            var lbGlobalY = lbId & 0xFF;

            foreach (var b in lbDoc.GetStaticObjects()) {
                var localX = b.Origin.X - lbGlobalX * 192f;
                var localY = b.Origin.Y - lbGlobalY * 192f;
                var cellX = (int)MathF.Floor(localX / 24f);
                var cellY = (int)MathF.Floor(localY / 24f);
                if (cellX >= 0 && cellX < 8 && cellY >= 0 && cellY < 8) {
                    buildings.Add(cellX * 9 + cellY);
                }
            }

            var blockCellX = (int)lbGlobalX * 8;
            var blockCellY = (int)lbGlobalY * 8;

            for (int i = 0; i < lbTerrainEntries.Length; i++) {
                var entry = lbTerrainEntries[i];
                var terrainType = entry.Type;
                var sceneType = entry.Scenery;

                if (terrainType >= region.TerrainInfo.TerrainTypes.Count) continue;

                var terrainInfo = region.TerrainInfo.TerrainTypes[(int)terrainType];
                if (sceneType >= terrainInfo.SceneTypes.Count) continue;

                var sceneInfoIdx = terrainInfo.SceneTypes[(int)sceneType];
                var sceneInfo = region.SceneInfo.SceneTypes[(int)sceneInfoIdx];

                if (sceneInfo.Scenes.Count == 0) continue;

                var cellX = i / 9;
                var cellY = i % 9;
                var globalCellX = (uint)(blockCellX + cellX);
                var globalCellY = (uint)(blockCellY + cellY);

                var cellMat = globalCellY * (712977289u * globalCellX + 1813693831u) - 1109124029u * globalCellX + 2139937281u;
                var offset = cellMat * 2.3283064e-10f;
                var sceneIdx = (int)(sceneInfo.Scenes.Count * offset);
                sceneIdx = Math.Clamp(sceneIdx, 0, sceneInfo.Scenes.Count - 1);
                var sceneId = sceneInfo.Scenes[sceneIdx];

                if (!dats.TryGet<Scene>(sceneId, out var scene) || scene.Objects.Count == 0) continue;
                if (entry.Road != 0) continue;
                if (buildings.Contains(i)) continue;

                var cellXMat = -1109124029 * (int)globalCellX;
                var cellYMat = 1813693831 * (int)globalCellY;
                var cellMat2 = 1360117743 * globalCellX * globalCellY + 1888038839;

                for (uint j = 0; j < scene.Objects.Count; j++) {
                    var obj = scene.Objects[(int)j];
                    if (obj.ObjectId == 0) continue;

                    var noise = (uint)(cellXMat + cellYMat - cellMat2 * (23399 + j)) * 2.3283064e-10f;
                    if (noise >= obj.Frequency) continue;

                    var localPos = SceneryHelpers.Displace(obj, globalCellX, globalCellY, j);
                    var lx = cellX * 24f + localPos.X;
                    var ly = cellY * 24f + localPos.Y;

                    if (lx < 0 || ly < 0 || lx >= 192f || ly >= 192f) continue;
                    if (TerrainGeometryGenerator.OnRoad(new Vector3(lx, ly, 0), lbTerrainEntries)) continue;

                    var lbOffset = new Vector3(lx, ly, 0);
                    var z = TerrainGeometryGenerator.GetHeight(region, lbTerrainEntries, lbGlobalX, lbGlobalY, lbOffset);
                    localPos.Z = z;
                    lbOffset.Z = z;

                    var normal = TerrainGeometryGenerator.GetNormal(region, lbTerrainEntries, lbGlobalX, lbGlobalY, lbOffset);
                    if (!SceneryHelpers.CheckSlope(obj, normal.Z)) continue;

                    Quaternion quat = obj.Align != 0
                        ? SceneryHelpers.ObjAlign(obj, normal, z, localPos)
                        : SceneryHelpers.RotateObj(obj, globalCellX, globalCellY, j, localPos);

                    var scaleVal = SceneryHelpers.ScaleObj(obj, globalCellX, globalCellY, j);
                    var scale = new Vector3(scaleVal);

                    var blockX = (lbId >> 8) & 0xFF;
                    var blockY = lbId & 0xFF;
                    var worldOrigin = new Vector3(blockX * 192f + lx, blockY * 192f + ly, z);

                    scenery.Add(new StaticObject {
                        Id = obj.ObjectId,
                        Origin = worldOrigin,
                        Orientation = quat,
                        IsSetup = (obj.ObjectId & 0x02000000) != 0,
                        Scale = scale
                    });
                }
            }

            return scenery;
        }

        private HashSet<ushort> GetProximateLandblocks(Vector3 cameraPosition) {
            var currentCamera = CameraManager?.Current;

            // Orthographic top-down: compute the exact visible rectangle and load landblocks within it
            if (currentCamera is OrthographicTopDownCamera orthoCamera && orthoCamera.ScreenSize.X > 0) {
                return GetVisibleLandblocksOrtho(cameraPosition, orthoCamera);
            }

            // Perspective camera: use fixed proximity radius
            return GetProximateLandblocksRadius(cameraPosition, PerspectiveProximityThreshold);
        }

        private HashSet<ushort> GetVisibleLandblocksOrtho(Vector3 cameraPosition, OrthographicTopDownCamera orthoCamera) {
            float aspectRatio = orthoCamera.ScreenSize.X / orthoCamera.ScreenSize.Y;
            float halfWidth = orthoCamera.OrthographicSize * aspectRatio / 2f;
            float halfHeight = orthoCamera.OrthographicSize / 2f;

            // Add one-landblock margin for smooth panning (objects don't pop in at edges)
            float margin = TerrainDataManager.LandblockLength;

            float visMinX = cameraPosition.X - halfWidth - margin;
            float visMaxX = cameraPosition.X + halfWidth + margin;
            float visMinY = cameraPosition.Y - halfHeight - margin;
            float visMaxY = cameraPosition.Y + halfHeight + margin;

            // Convert to landblock grid
            int lbMinX = Math.Clamp((int)(visMinX / TerrainDataManager.LandblockLength), 0, (int)TerrainDataManager.MapSize - 1);
            int lbMaxX = Math.Clamp((int)(visMaxX / TerrainDataManager.LandblockLength), 0, (int)TerrainDataManager.MapSize - 1);
            int lbMinY = Math.Clamp((int)(visMinY / TerrainDataManager.LandblockLength), 0, (int)TerrainDataManager.MapSize - 1);
            int lbMaxY = Math.Clamp((int)(visMaxY / TerrainDataManager.LandblockLength), 0, (int)TerrainDataManager.MapSize - 1);

            // If the visible area would load too many landblocks, shrink to a capped radius
            int gridWidth = lbMaxX - lbMinX + 1;
            int gridHeight = lbMaxY - lbMinY + 1;
            if (gridWidth * gridHeight > MaxLoadedLandblocks) {
                // Fall back to radius-based with a cap derived from max landblocks
                float cappedRadius = MathF.Sqrt(MaxLoadedLandblocks) * TerrainDataManager.LandblockLength / 2f;
                return GetProximateLandblocksRadius(cameraPosition, cappedRadius);
            }

            var proximate = new HashSet<ushort>();
            for (int lbX = lbMinX; lbX <= lbMaxX; lbX++) {
                for (int lbY = lbMinY; lbY <= lbMaxY; lbY++) {
                    proximate.Add((ushort)((lbX << 8) | lbY));
                }
            }

            return proximate;
        }

        private HashSet<ushort> GetUnloadBoundaryLandblocks(Vector3 cameraPosition) {
            var currentCamera = CameraManager?.Current;

            if (currentCamera is OrthographicTopDownCamera orthoCamera && orthoCamera.ScreenSize.X > 0) {
                // Use 3× landblock margin instead of 1× for unloading
                float aspectRatio = orthoCamera.ScreenSize.X / orthoCamera.ScreenSize.Y;
                float halfWidth = orthoCamera.OrthographicSize * aspectRatio / 2f;
                float halfHeight = orthoCamera.OrthographicSize / 2f;
                float unloadMargin = TerrainDataManager.LandblockLength * 3f;

                float visMinX = cameraPosition.X - halfWidth - unloadMargin;
                float visMaxX = cameraPosition.X + halfWidth + unloadMargin;
                float visMinY = cameraPosition.Y - halfHeight - unloadMargin;
                float visMaxY = cameraPosition.Y + halfHeight + unloadMargin;

                int lbMinX = Math.Clamp((int)(visMinX / TerrainDataManager.LandblockLength), 0, (int)TerrainDataManager.MapSize - 1);
                int lbMaxX = Math.Clamp((int)(visMaxX / TerrainDataManager.LandblockLength), 0, (int)TerrainDataManager.MapSize - 1);
                int lbMinY = Math.Clamp((int)(visMinY / TerrainDataManager.LandblockLength), 0, (int)TerrainDataManager.MapSize - 1);
                int lbMaxY = Math.Clamp((int)(visMaxY / TerrainDataManager.LandblockLength), 0, (int)TerrainDataManager.MapSize - 1);

                var keepLoaded = new HashSet<ushort>();
                for (int lbX = lbMinX; lbX <= lbMaxX; lbX++) {
                    for (int lbY = lbMinY; lbY <= lbMaxY; lbY++) {
                        keepLoaded.Add((ushort)((lbX << 8) | lbY));
                    }
                }
                return keepLoaded;
            }

            // Perspective: 50% larger radius for unloading than loading
            return GetProximateLandblocksRadius(cameraPosition, PerspectiveProximityThreshold * 1.5f);
        }

        private HashSet<ushort> GetProximateLandblocksRadius(Vector3 cameraPosition, float threshold) {
            var proximate = new HashSet<ushort>();
            var camLbX = (ushort)(cameraPosition.X / TerrainDataManager.LandblockLength);
            var camLbY = (ushort)(cameraPosition.Y / TerrainDataManager.LandblockLength);

            var lbd = (int)Math.Ceiling(threshold / TerrainDataManager.LandblockLength / 2f);
            for (int dx = -lbd; dx <= lbd; dx++) {
                for (int dy = -lbd; dy <= lbd; dy++) {
                    var lbX = (ushort)Math.Clamp(camLbX + dx, 0, TerrainDataManager.MapSize - 1);
                    var lbY = (ushort)Math.Clamp(camLbY + dy, 0, TerrainDataManager.MapSize - 1);
                    var lbKey = (ushort)((lbX << 8) | lbY);
                    var lbCenter = new Vector2(
                        lbX * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2,
                        lbY * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2);
                    var dist2D = Vector2.Distance(new Vector2(cameraPosition.X, cameraPosition.Y), lbCenter);
                    if (dist2D <= threshold) {
                        proximate.Add(lbKey);
                    }
                }
            }

            return proximate;
        }

        public void RegenerateChunks(IEnumerable<ulong> chunkIds) {
            foreach (var chunkId in chunkIds) {
                var chunkX = (uint)(chunkId >> 32);
                var chunkY = (uint)(chunkId & 0xFFFFFFFF);
                var chunk = DataManager.GetOrCreateChunk(chunkX, chunkY);

                // Re-queue generation for all contexts
                foreach (var context in _contexts.Values) {
                    QueueChunkForGeneration(chunk, context);
                }
            }
        }

        public void UpdateLandblocks(IEnumerable<uint> landblockIds) {
            var landblocksByChunk = new Dictionary<ulong, List<uint>>();

            foreach (var landblockId in landblockIds) {
                var landblockX = landblockId >> 8;
                var landblockY = landblockId & 0xFF;
                var chunk = DataManager.GetChunkForLandblock(landblockX, landblockY);

                if (chunk == null) continue;

                var chunkId = chunk.GetChunkId();
                if (!landblocksByChunk.ContainsKey(chunkId)) {
                    landblocksByChunk[chunkId] = new List<uint>();
                }

                landblocksByChunk[chunkId].Add(landblockId);

                var lbKey = (ushort)landblockId;
                if (_sceneryObjects.ContainsKey(lbKey)) {
                    _pendingSceneryRegen.Add(lbKey);
                }
            }

            ProcessPendingSceneryRegen();

            foreach (var kvp in landblocksByChunk) {
                var chunk = DataManager.GetChunk(kvp.Key);
                if (chunk != null) {
                    foreach (var context in _contexts.Values) {
                        context.GPUManager.UpdateLandblocks(chunk, kvp.Value, _terrainSystem);
                    }
                }
            }
        }

        private void RefreshSceneryForNearbyLandblocks(Vector3 cameraPosition) {
            float effectiveThreshold = GetEffectiveSceneryThreshold();
            if (effectiveThreshold < 100f) return;

            var camPos2D = new Vector2(cameraPosition.X, cameraPosition.Y);

            foreach (var kvp in _sceneryObjects) {
                if (kvp.Value.Count > 0 || _pendingSceneryRegen.Contains(kvp.Key) || _pendingLoadLandblocks.Contains(kvp.Key))
                    continue;

                var lbCenter = LandblockCenter(kvp.Key);
                float dist = Vector2.Distance(camPos2D, lbCenter);
                if (dist <= effectiveThreshold) {
                    _pendingSceneryRegen.Add(kvp.Key);
                }
            }
        }

        private float GetEffectiveSceneryThreshold() {
            if (CameraManager?.Current is OrthographicTopDownCamera ortho && ortho.OrthographicSize > 0) {
                return SceneryDistanceThreshold * Math.Clamp(1800f / ortho.OrthographicSize, 0f, 1f);
            }
            return SceneryDistanceThreshold;
        }

        private float GetEffectiveDungeonThreshold() {
            if (CameraManager?.Current is OrthographicTopDownCamera ortho && ortho.OrthographicSize > 0) {
                return DungeonDistanceThreshold * Math.Clamp(1800f / ortho.OrthographicSize, 0f, 1f);
            }
            return DungeonDistanceThreshold;
        }

        public IEnumerable<(TerrainChunk chunk, ChunkRenderData renderData)> GetRenderableChunks(Frustum frustum, SceneContext context) {
            foreach (var chunk in DataManager.GetAllChunks()) {
                if (!frustum.IntersectsBoundingBox(chunk.Bounds)) continue;

                var renderData = context.GPUManager.GetRenderData(chunk.GetChunkId());
                if (renderData != null) {
                    yield return (chunk, renderData);
                }
            }
        }

        public int GetLoadedChunkCount() => DataManager.GetAllChunks().Count();
        public int GetVisibleChunkCount(Frustum frustum, SceneContext context) => GetRenderableChunks(frustum, context).Count();

        public IEnumerable<StaticObject> GetAllStaticObjects() {
            var focusedLB = _contexts.Values.FirstOrDefault()?.EnvCellManager.FocusedDungeonLB;
            if (_staticObjectsDirty || _cachedStaticObjects == null
                || _cachedShowStaticObjects != ShowStaticObjects
                || _cachedShowScenery != ShowScenery
                || _cachedShowDungeons != ShowDungeons
                || _cachedFocusedDungeonLB != focusedLB) {
                var statics = new List<StaticObject>();
                if (ShowStaticObjects) {
                    foreach (var doc in _documentManager.ActiveDocs.Values.OfType<LandblockDocument>()) {
                        statics.AddRange(doc.GetStaticObjects());
                    }
                }
                if (ShowScenery) {
                    statics.AddRange(_sceneryObjects.Values.SelectMany(x => x));
                }
                // Building interior statics show when Dungeons toggle is on
                // (same toggle that reveals interior EnvCell geometry).
                if (ShowDungeons) {
                    statics.AddRange(_buildingStaticObjects.Values.SelectMany(x => x));
                }
                // Dungeon statics only show when a specific dungeon is focused,
                // otherwise they render as floating objects in the overworld view.
                if (ShowDungeons && focusedLB.HasValue) {
                    foreach (var kvp in _dungeonStaticObjects) {
                        if (kvp.Key != focusedLB.Value) continue;
                        statics.AddRange(kvp.Value);
                    }
                }
                _cachedStaticObjects = statics;
                _cachedShowStaticObjects = ShowStaticObjects;
                _cachedShowScenery = ShowScenery;
                _cachedShowDungeons = ShowDungeons;
                _cachedFocusedDungeonLB = focusedLB;
                _staticObjectsDirty = false;
            }
            return _cachedStaticObjects;
        }

        public void InvalidateStaticObjectsCache() {
            _staticObjectsDirty = true;
        }

        private SceneContext GetContext(OpenGLRenderer renderer) {
            var context = _contexts.GetOrAdd(renderer, r => {
                SurfaceManager.RegisterRenderer(r);
                return new SceneContext(r, _dats, _textureCache);
            });

            if (_thumbnailService == null) {
                lock (_contexts) {
                    if (_thumbnailService == null) {
                        _thumbnailService = new ThumbnailRenderService(renderer, context.ObjectManager);

                        var allObjects = GetAllStaticObjects();
                        var uniqueIds = new HashSet<(uint, bool)>();
                        foreach (var obj in allObjects) uniqueIds.Add((obj.Id, obj.IsSetup));

                        context.ModelWarmupQueue.Clear();
                        foreach (var item in uniqueIds) context.ModelWarmupQueue.Enqueue(item);
                    }
                }
            }
            return context;
        }

        public void Render(
            ICamera camera,
            OpenGLRenderer renderer,
            float aspectRatio,
            TerrainEditingContext editingContext,
            float width,
            float height) {

            var context = GetContext(renderer);
            var gl = renderer.GraphicsDevice.GL;

            ProcessPendingUploads(context);

            _aspectRatio = aspectRatio;
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.ClearColor(0.2f, 0.3f, 0.8f, 1.0f);
            gl.ClearDepth(1f);
            gl.Clear(
                ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            Matrix4x4 model = Matrix4x4.Identity;
            Matrix4x4 view = camera.GetViewMatrix();
            Matrix4x4 projection = camera.GetProjectionMatrix();
            Matrix4x4 viewProjection = view * projection;

            float cameraDistance = MathF.Abs(camera.Position.Z);
            if (camera is OrthographicTopDownCamera orthoCamera) {
                cameraDistance = orthoCamera.OrthographicSize;
            }

            var frustum = new Frustum(viewProjection);
            var renderableChunks = GetRenderableChunks(frustum, context);

            // Render terrain (with brush preview).
            // Terrain is pushed furthest back in the depth priority chain:
            //   Terrain (2,2) < Building EnvCells (1,1) < Static objects (0)
            // This ensures interior floors win over terrain, while exterior
            // GfxObj models win over interior EnvCell walls/ceilings.
            gl.Enable(EnableCap.PolygonOffsetFill);
            gl.PolygonOffset(2f, 2f);
            RenderTerrain(context, renderableChunks, model, camera, cameraDistance, width, height, editingContext);
            gl.Disable(EnableCap.PolygonOffsetFill);

            if (editingContext.ActiveVertices.Count > 0) {
                RenderActiveSpheres(context, editingContext, camera, model, viewProjection);
            }

            var renderSw = Stopwatch.StartNew();
            var allObjects = GetAllStaticObjects();
            var visibleObjects = new List<StaticObject>();
            foreach (var obj in allObjects) {
                var localBounds = context.ObjectManager.GetBounds(obj.Id, obj.IsSetup);
                Chorizite.Core.Lib.BoundingBox objBounds;
                if (localBounds.HasValue) {
                    var (localMin, localMax) = localBounds.Value;
                    var worldTransform = Matrix4x4.CreateScale(obj.Scale)
                        * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                        * Matrix4x4.CreateTranslation(obj.Origin);

                    var worldMin = new Vector3(float.MaxValue);
                    var worldMax = new Vector3(float.MinValue);
                    for (int ci = 0; ci < 8; ci++) {
                        var corner = new Vector3(
                            (ci & 1) == 0 ? localMin.X : localMax.X,
                            (ci & 2) == 0 ? localMin.Y : localMax.Y,
                            (ci & 4) == 0 ? localMin.Z : localMax.Z);
                        var worldCorner = Vector3.Transform(corner, worldTransform);
                        worldMin = Vector3.Min(worldMin, worldCorner);
                        worldMax = Vector3.Max(worldMax, worldCorner);
                    }
                    objBounds = new Chorizite.Core.Lib.BoundingBox(worldMin, worldMax);
                }
                else {
                    const float fallbackRadius = 50f;
                    objBounds = new Chorizite.Core.Lib.BoundingBox(
                        obj.Origin - new Vector3(fallbackRadius),
                        obj.Origin + new Vector3(fallbackRadius));
                }
                if (frustum.IntersectsBoundingBox(objBounds)) {
                    visibleObjects.Add(obj);
                }
            }
            if (visibleObjects.Count > 0) {
                RenderStaticObjects(context, visibleObjects, camera, viewProjection);
            }
            long renderStaticsMs = renderSw.ElapsedMilliseconds;
            if (renderStaticsMs > 200) {
                Console.WriteLine($"[GameScene.Render] Static objects: {renderStaticsMs}ms ({visibleObjects.Count} objects)");
            }

            // Render EnvCell geometry ? building interiors always render,
            // dungeon cells are gated by ShowDungeons toggle + focus filter.
            context.EnvCellManager.ShowDungeonCells = ShowDungeons;
            context.EnvCellManager.Render(viewProjection, camera, LightDirection, AmbientLightIntensity, SpecularPower);

            if (editingContext.ObjectSelection.HasSelection) {
                RenderSelectionHighlight(context, editingContext.ObjectSelection, camera, viewProjection);
            }

            if (editingContext.ObjectSelection.IsPlacementMode && editingContext.ObjectSelection.PlacementPreview.HasValue) {
                RenderPlacementPreview(context, editingContext.ObjectSelection.PlacementPreview.Value, camera, viewProjection);
            }

            _thumbnailService?.ProcessQueue(renderer);
        }

        private unsafe void RenderSelectionHighlight(SceneContext context, ObjectSelectionState selection, ICamera camera, Matrix4x4 viewProjection) {
            if (!selection.HasSelection) return;
            var gl = context.Renderer.GraphicsDevice.GL;

            // EnvCell (dungeon cell) highlight ? use cell bounding box corners
            if (selection.HasEnvCellSelection) {
                var cell = selection.SelectedEnvCell!;
                float r = EnvCellManager.CellBoundsRadius;
                float sphereR = r * 0.08f; // proportional sphere size
                var pos = cell.WorldPosition;
                var cellCorners = new List<Vector4>();

                // 8 corners of the bounding box
                for (int cx = -1; cx <= 1; cx += 2)
                    for (int cy = -1; cy <= 1; cy += 2)
                        for (int cz = -1; cz <= 1; cz += 2)
                            cellCorners.Add(new Vector4(pos.X + cx * r, pos.Y + cy * r, pos.Z + cz * r, sphereR));

                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                context.SphereShader.Bind();
                context.SphereShader.SetUniform("uViewProjection", viewProjection);
                context.SphereShader.SetUniform("uCameraPosition", camera.Position);
                context.SphereShader.SetUniform("uSphereColor", new Vector3(0.0f, 0.8f, 1.0f)); // Cyan for dungeon cells
                context.SphereShader.SetUniform("uLightDirection", Vector3.Normalize(LightDirection));
                context.SphereShader.SetUniform("uAmbientIntensity", 0.8f);
                context.SphereShader.SetUniform("uSpecularPower", SpecularPower);
                context.SphereShader.SetUniform("uGlowColor", new Vector3(0.0f, 0.8f, 1.0f));
                context.SphereShader.SetUniform("uGlowIntensity", 2.0f);
                context.SphereShader.SetUniform("uGlowPower", 0.3f);

                var cellArray = cellCorners.ToArray();
                gl.BindBuffer(GLEnum.ArrayBuffer, context.SphereInstanceVBO);
                unsafe {
                    fixed (Vector4* ptr = cellArray) {
                        gl.BufferData(GLEnum.ArrayBuffer, (nuint)(cellArray.Length * sizeof(Vector4)), ptr, GLEnum.DynamicDraw);
                    }
                }
                gl.BindVertexArray(context.SphereVAO);
                gl.DrawElementsInstanced(GLEnum.Triangles, (uint)context.SphereIndexCount, GLEnum.UnsignedInt, null, (uint)cellArray.Length);
                gl.BindVertexArray(0);
                gl.UseProgram(0);
                gl.Disable(EnableCap.Blend);
                return; // EnvCell selection handled, skip static object highlight
            }

            // Collect corners for all selected objects, with per-object sphere radius
            var allInstances = new List<Vector4>();
            foreach (var entry in selection.SelectedEntries.ToList()) {
                var obj = entry.Object;
                var bounds = context.ObjectManager.GetBounds(obj.Id, obj.IsSetup);
                if (bounds == null) continue;

                var (localMin, localMax) = bounds.Value;
                var extent = (localMax - localMin) * obj.Scale;
                float maxExtent = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
                float radius = Math.Clamp(maxExtent * 0.1f, 0.15f, SphereRadius * 0.5f);

                var worldTransform = Matrix4x4.CreateScale(obj.Scale)
                    * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                    * Matrix4x4.CreateTranslation(obj.Origin);

                Vector3 corner;
                corner = Vector3.Transform(new Vector3(localMin.X, localMin.Y, localMin.Z), worldTransform);
                allInstances.Add(new Vector4(corner, radius));
                corner = Vector3.Transform(new Vector3(localMax.X, localMin.Y, localMin.Z), worldTransform);
                allInstances.Add(new Vector4(corner, radius));
                corner = Vector3.Transform(new Vector3(localMin.X, localMax.Y, localMin.Z), worldTransform);
                allInstances.Add(new Vector4(corner, radius));
                corner = Vector3.Transform(new Vector3(localMax.X, localMax.Y, localMin.Z), worldTransform);
                allInstances.Add(new Vector4(corner, radius));
                corner = Vector3.Transform(new Vector3(localMin.X, localMin.Y, localMax.Z), worldTransform);
                allInstances.Add(new Vector4(corner, radius));
                corner = Vector3.Transform(new Vector3(localMax.X, localMin.Y, localMax.Z), worldTransform);
                allInstances.Add(new Vector4(corner, radius));
                corner = Vector3.Transform(new Vector3(localMin.X, localMax.Y, localMax.Z), worldTransform);
                allInstances.Add(new Vector4(corner, radius));
                corner = Vector3.Transform(new Vector3(localMax.X, localMax.Y, localMax.Z), worldTransform);
                allInstances.Add(new Vector4(corner, radius));
            }

            if (allInstances.Count == 0) return;

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            context.SphereShader.Bind();
            context.SphereShader.SetUniform("uViewProjection", viewProjection);
            context.SphereShader.SetUniform("uCameraPosition", camera.Position);
            context.SphereShader.SetUniform("uSphereColor", new Vector3(1.0f, 0.8f, 0.0f));
            context.SphereShader.SetUniform("uLightDirection", Vector3.Normalize(LightDirection));
            context.SphereShader.SetUniform("uAmbientIntensity", 0.8f);
            context.SphereShader.SetUniform("uSpecularPower", SpecularPower);
            context.SphereShader.SetUniform("uGlowColor", new Vector3(1.0f, 0.8f, 0.0f));
            context.SphereShader.SetUniform("uGlowIntensity", 2.0f);
            context.SphereShader.SetUniform("uGlowPower", 0.3f);

            var instanceArray = allInstances.ToArray();
            gl.BindBuffer(GLEnum.ArrayBuffer, context.SphereInstanceVBO);
            fixed (Vector4* ptr = instanceArray) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(instanceArray.Length * sizeof(Vector4)), ptr, GLEnum.DynamicDraw);
            }

            gl.BindVertexArray(context.SphereVAO);
            gl.DrawElementsInstanced(GLEnum.Triangles, (uint)context.SphereIndexCount, GLEnum.UnsignedInt, null, (uint)instanceArray.Length);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(EnableCap.Blend);
        }

        private void RenderPlacementPreview(SceneContext context, StaticObject previewObj, ICamera camera, Matrix4x4 viewProjection) {
            var renderData = context.ObjectManager.GetRenderData(previewObj.Id, previewObj.IsSetup);
            if (renderData == null) return;

            if (renderData.IsSetup && renderData.SetupParts != null) {
                foreach (var (partId, _) in renderData.SetupParts) {
                    context.ObjectManager.GetRenderData(partId, false);
                }
            }

            var objects = new List<StaticObject> { previewObj };
            RenderStaticObjects(context, objects, camera, viewProjection);
        }

        private void RenderTerrain(
            SceneContext context,
            IEnumerable<(TerrainChunk chunk, ChunkRenderData renderData)> renderableChunks,
            Matrix4x4 model,
            ICamera camera,
            float cameraDistance,
            float width,
            float height,
            TerrainEditingContext? editingContext = null) {

            context.TerrainShader.Bind();
            context.TerrainShader.SetUniform("xAmbient", AmbientLightIntensity);
            context.TerrainShader.SetUniform("xWorld", model);
            context.TerrainShader.SetUniform("xView", camera.GetViewMatrix());
            context.TerrainShader.SetUniform("xProjection", camera.GetProjectionMatrix());
            context.TerrainShader.SetUniform("uAlpha", 1f);
            context.TerrainShader.SetUniform("uShowLandblockGrid", ShowGrid ? 1 : 0);
            context.TerrainShader.SetUniform("uShowCellGrid", ShowGrid ? 1 : 0);
            context.TerrainShader.SetUniform("uLandblockGridColor", LandblockGridColor);
            context.TerrainShader.SetUniform("uCellGridColor", CellGridColor);
            context.TerrainShader.SetUniform("uGridLineWidth", GridLineWidth);
            context.TerrainShader.SetUniform("uGridOpacity", GridOpacity);
            context.TerrainShader.SetUniform("uCameraDistance", cameraDistance);
            context.TerrainShader.SetUniform("uScreenHeight", height);

            context.TerrainShader.SetUniform("uShowSlopeHighlight", ShowSlopeHighlight ? 1 : 0);
            context.TerrainShader.SetUniform("uSlopeThreshold", SlopeThreshold * MathF.PI / 180f);
            context.TerrainShader.SetUniform("uSlopeHighlightColor", SlopeHighlightColor);
            context.TerrainShader.SetUniform("uSlopeHighlightOpacity", SlopeHighlightOpacity);

            bool brushActive = editingContext?.BrushActive ?? false;
            context.TerrainShader.SetUniform("uBrushActive", brushActive ? 1 : 0);
            if (brushActive) {
                context.TerrainShader.SetUniform("uBrushCenter", editingContext!.BrushCenter);
                float worldRadius = (editingContext.BrushRadius * 12f) + 1f;
                context.TerrainShader.SetUniform("uBrushRadius", worldRadius);
            }

            int previewIdx = editingContext?.PreviewTextureAtlasIndex ?? -1;
            context.TerrainShader.SetUniform("uPreviewActive", previewIdx >= 0 ? 1 : 0);
            context.TerrainShader.SetUniform("uPreviewTexIndex", (float)previewIdx);

            SurfaceManager.GetTerrainAtlas(context.Renderer).Bind(0);
            context.TerrainShader.SetUniform("xOverlays", 0);
            SurfaceManager.GetAlphaAtlas(context.Renderer).Bind(1);
            context.TerrainShader.SetUniform("xAlphas", 1);

            foreach (var (_, renderData) in renderableChunks) {
                renderData.ArrayBuffer.Bind();
                renderData.VertexBuffer.Bind();
                renderData.IndexBuffer.Bind();
                GLHelpers.CheckErrors();
                context.Renderer.GraphicsDevice.DrawElements(Chorizite.Core.Render.Enums.PrimitiveType.TriangleList,
                    renderData.TotalIndexCount);
                renderData.ArrayBuffer.Unbind();
                renderData.VertexBuffer.Unbind();
                renderData.IndexBuffer.Unbind();
            }
        }

        private unsafe void RenderActiveSpheres(
            SceneContext context,
            TerrainEditingContext editingContext,
            ICamera camera,
            Matrix4x4 model,
            Matrix4x4 viewProjection) {

            var gl = context.Renderer.GraphicsDevice.GL;
            var activeVerts = editingContext.ActiveVertices.ToArray();
            if (activeVerts.Length == 0) return;

            int count = activeVerts.Length;
            var instances = new Vector4[count];
            for (int i = 0; i < count; i++) {
                try {
                    var vertex = activeVerts[i];
                    instances[i] = new Vector4(
                        vertex.X,
                        vertex.Y,
                        DataManager.GetHeightAtPosition(vertex.X, vertex.Y) + SphereHeightOffset,
                        SphereRadius);
                }
                catch {
                    instances[i] = new Vector4(0, 0, 0, SphereRadius);
                }
            }

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            context.SphereShader.Bind();
            context.SphereShader.SetUniform("uViewProjection", viewProjection);
            context.SphereShader.SetUniform("uCameraPosition", camera.Position);
            context.SphereShader.SetUniform("uSphereColor", SphereColor);
            Vector3 normLight = Vector3.Normalize(LightDirection);
            context.SphereShader.SetUniform("uLightDirection", normLight);
            context.SphereShader.SetUniform("uAmbientIntensity", AmbientLightIntensity);
            context.SphereShader.SetUniform("uSpecularPower", SpecularPower);
            context.SphereShader.SetUniform("uGlowColor", SphereGlowColor);
            context.SphereShader.SetUniform("uGlowIntensity", SphereGlowIntensity);
            context.SphereShader.SetUniform("uGlowPower", SphereGlowPower);

            gl.BindBuffer(GLEnum.ArrayBuffer, context.SphereInstanceVBO);
            fixed (Vector4* ptr = instances) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(count * sizeof(Vector4)), ptr, GLEnum.DynamicDraw);
            }

            gl.BindVertexArray(context.SphereVAO);
            gl.DrawElementsInstanced(GLEnum.Triangles, (uint)context.SphereIndexCount, GLEnum.UnsignedInt, null, (uint)count);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(EnableCap.Blend);
        }


        private unsafe void RenderStaticObjects(SceneContext context, List<StaticObject> objects, ICamera camera, Matrix4x4 viewProjection) {
            var gl = context.Renderer.GraphicsDevice.GL;
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            var objectManager = context.ObjectManager;
            objectManager._objectShader.Bind();
            objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            objectManager._objectShader.SetUniform("uCameraPosition", camera.Position);
            objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(LightDirection));
            objectManager._objectShader.SetUniform("uAmbientIntensity", AmbientLightIntensity);
            objectManager._objectShader.SetUniform("uSpecularPower", SpecularPower);

            foreach (var list in _objectGroupBuffer.Values) list.Clear();
            foreach (var obj in objects) {
                var key = (obj.Id, obj.IsSetup);
                if (!_objectGroupBuffer.TryGetValue(key, out var list)) {
                    list = new List<Matrix4x4>();
                    _objectGroupBuffer[key] = list;
                }
                list.Add(
                    Matrix4x4.CreateScale(obj.Scale)
                    * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                    * Matrix4x4.CreateTranslation(obj.Origin));
            }

            foreach (var group in _objectGroupBuffer) {
                if (group.Value.Count == 0) continue;
                var (id, isSetup) = group.Key;

                var renderData = objectManager.TryGetCachedRenderData(id);
                if (renderData == null) continue;

                if (isSetup) {
                    foreach (var (partId, partTransform) in renderData.SetupParts) {
                        var partRenderData = objectManager.TryGetCachedRenderData(partId);
                        if (partRenderData == null) continue;

                        _tempInstanceTransforms.Clear();
                        foreach (var instanceMatrix in group.Value) {
                            _tempInstanceTransforms.Add(partTransform * instanceMatrix);
                        }

                        RenderBatchedObject(context, partRenderData, _tempInstanceTransforms);
                    }
                }
                else {
                    RenderBatchedObject(context, renderData, group.Value);
                }
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(EnableCap.Blend);
        }

        private unsafe void RenderBatchedObject(SceneContext context, StaticObjectRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (instanceTransforms.Count == 0 || renderData.Batches.Count == 0) return;
            var gl = context.Renderer.GraphicsDevice.GL;

            int requiredFloats = instanceTransforms.Count * 16;

            if (context.InstanceUploadBuffer.Length < requiredFloats) {
                int newSize = Math.Max(requiredFloats, 256);
                newSize = (int)BitOperations.RoundUpToPowerOf2((uint)newSize);
                context.InstanceUploadBuffer = new float[newSize];
            }

            for (int i = 0; i < instanceTransforms.Count; i++) {
                var transform = instanceTransforms[i];
                int offset = i * 16;
                context.InstanceUploadBuffer[offset +  0] = transform.M11; context.InstanceUploadBuffer[offset +  1] = transform.M12;
                context.InstanceUploadBuffer[offset +  2] = transform.M13; context.InstanceUploadBuffer[offset +  3] = transform.M14;
                context.InstanceUploadBuffer[offset +  4] = transform.M21; context.InstanceUploadBuffer[offset +  5] = transform.M22;
                context.InstanceUploadBuffer[offset +  6] = transform.M23; context.InstanceUploadBuffer[offset +  7] = transform.M24;
                context.InstanceUploadBuffer[offset +  8] = transform.M31; context.InstanceUploadBuffer[offset +  9] = transform.M32;
                context.InstanceUploadBuffer[offset + 10] = transform.M33; context.InstanceUploadBuffer[offset + 11] = transform.M34;
                context.InstanceUploadBuffer[offset + 12] = transform.M41; context.InstanceUploadBuffer[offset + 13] = transform.M42;
                context.InstanceUploadBuffer[offset + 14] = transform.M43; context.InstanceUploadBuffer[offset + 15] = transform.M44;
            }

            if (context.InstanceVBO == 0) {
                gl.GenBuffers(1, out uint vbo);
                context.InstanceVBO = vbo;
            }

            gl.BindBuffer(GLEnum.ArrayBuffer, context.InstanceVBO);

            if (requiredFloats > context.InstanceBufferCapacity) {
                int newCapacity = Math.Max(requiredFloats, 256);
                newCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)newCapacity);
                context.InstanceBufferCapacity = newCapacity;
                fixed (float* ptr = context.InstanceUploadBuffer) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(newCapacity * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
            }
            else {
                fixed (float* ptr = context.InstanceUploadBuffer) {
                    gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(requiredFloats * sizeof(float)), ptr);
                }
            }

            gl.BindVertexArray(renderData.VAO);

            gl.BindBuffer(GLEnum.ArrayBuffer, context.InstanceVBO);
            for (int i = 0; i < 4; i++) {
                gl.EnableVertexAttribArray((uint)(3 + i));
                gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false, (uint)(16 * sizeof(float)), (void*)(i * 4 * sizeof(float)));
                gl.VertexAttribDivisor((uint)(3 + i), 1);
            }

            bool cullFaceEnabled = true;
            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;

                try {
                    if (batch.IsDoubleSided && cullFaceEnabled) {
                        gl.Disable(EnableCap.CullFace);
                        cullFaceEnabled = false;
                    }
                    else if (!batch.IsDoubleSided && !cullFaceEnabled) {
                        gl.Enable(EnableCap.CullFace);
                        cullFaceEnabled = true;
                    }

                    batch.TextureArray.Bind(0);
                    context.ObjectManager._objectShader.SetUniform("uTextureArray", 0);
                    context.ObjectManager._objectShader.SetUniform("uTextureIndex", (float)batch.TextureIndex);

                    gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                    gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, (uint)instanceTransforms.Count);
                }
                catch (Exception ex) {
                    Console.WriteLine($"Error rendering batch (texture index {batch.TextureIndex}): {ex.Message}");
                }
            }

            if (!cullFaceEnabled) {
                gl.Enable(EnableCap.CullFace);
            }

            gl.BindVertexArray(0);
        }

        public void SaveCameraState() {
            var cam = _settings.Landscape.Camera;
            var pos = CameraManager.Current.Position;
            cam.SavedPositionX = pos.X;
            cam.SavedPositionY = pos.Y;
            cam.SavedPositionZ = pos.Z;
            cam.SavedYaw = PerspectiveCamera.Yaw;
            cam.SavedPitch = PerspectiveCamera.Pitch;
            cam.SavedOrthoSize = TopDownCamera.OrthographicSize;
            cam.SavedIs3D = CameraManager.Current == PerspectiveCamera;
            _settings.Save();
        }

        public void Dispose() {
            SaveCameraState();
            if (!_disposed) {
                foreach (var kvp in _contexts) {
                    kvp.Value.Dispose();
                    SurfaceManager.UnregisterRenderer(kvp.Key);
                }
                _contexts.Clear();
                _thumbnailService?.Dispose();
                _disposed = true;
            }
        }
    }
}

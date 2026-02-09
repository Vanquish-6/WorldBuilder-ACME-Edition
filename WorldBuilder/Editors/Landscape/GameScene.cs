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
        private float ProximityThreshold = 500f; // 2D distance for loading

        private OpenGLRenderer _renderer => _terrainSystem.Renderer;
        private WorldBuilderSettings _settings => _terrainSystem.Settings;
        private GL _gl => _renderer.GraphicsDevice.GL;
        private IShader _terrainShader;
        private IShader _sphereShader;

        internal readonly StaticObjectManager _objectManager;
        private IDatReaderWriter _dats => _terrainSystem.Dats;
        private DocumentManager _documentManager => _terrainSystem.DocumentManager;
        private TerrainDocument _terrainDoc => _terrainSystem.TerrainDoc;
        private Region _region => _terrainSystem.Region;

        public TerrainDataManager DataManager { get; }
        public LandSurfaceManager SurfaceManager { get; }
        public TerrainGPUResourceManager GPUManager { get; }

        public PerspectiveCamera PerspectiveCamera { get; private set; }
        public OrthographicTopDownCamera TopDownCamera { get; private set; }
        public CameraManager CameraManager { get; private set; }

        private readonly Dictionary<ushort, List<StaticObject>> _sceneryObjects = new();
        internal readonly TerrainSystem _terrainSystem;

        // Static object background loading state
        private const float WarmUpTimeBudgetMs = 12f; // Max ms per frame for GPU model loading
        private const int MaxIntegratePerFrame = 8; // Max background results to integrate per frame
        private const int MaxUnloadsPerFrame = 2; // Max landblocks to unload per frame
        private const float DocUpdateDistanceThreshold = 96f; // Re-check after camera moves ~4 cells
        private Vector3 _lastDocUpdatePosition = new(float.MinValue);
        private readonly Queue<(uint Id, bool IsSetup)> _renderDataWarmupQueue = new();
        private List<StaticObject>? _cachedStaticObjects;
        private bool _staticObjectsDirty = true;
        private bool _cachedShowStaticObjects = true;
        private bool _cachedShowScenery = true;

        // Background loading pipeline
        private Task? _backgroundLoadTask;
        private readonly ConcurrentQueue<BackgroundLoadResult> _backgroundLoadResults = new();
        private readonly HashSet<ushort> _pendingLoadLandblocks = new(); // LB keys currently being loaded
        private readonly HashSet<ushort> _pendingSceneryRegen = new(); // LB keys needing scenery regen
        private HashSet<ushort>? _lastVisibleLandblocks;

        private record BackgroundLoadResult(ushort LbKey, string DocId, List<StaticObject> Scenery, HashSet<(uint Id, bool IsSetup)> UniqueObjectIds, long LoadMs, long SceneryMs, int SceneryCount);

        // Sphere rendering resources (from TerrainRenderer)
        private uint _sphereVAO;
        private uint _sphereVBO;
        private uint _sphereIBO;
        private uint _sphereInstanceVBO;
        private int _sphereIndexCount;

        private bool _disposed = false;
        private float _aspectRatio;

        // Rendering properties (from TerrainRenderer)
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
            CameraManager = new CameraManager(TopDownCamera);
            //CameraManager.AddCamera(PerspectiveCamera);

            DataManager = new TerrainDataManager(terrainSystem, 16);
            SurfaceManager = new LandSurfaceManager(_renderer, _dats, _region);
            GPUManager = new TerrainGPUResourceManager(_renderer);

            _objectManager = new StaticObjectManager(_renderer, _dats);

            // Initialize shaders
            var assembly = typeof(OpenGLRenderer).Assembly;
            _terrainShader = _renderer.GraphicsDevice.CreateShader("Landscape",
                GetEmbeddedResource("Chorizite.OpenGLSDLBackend.Shaders.Landscape.vert", assembly),
                GetEmbeddedResource("Chorizite.OpenGLSDLBackend.Shaders.Landscape.frag", assembly));
            _sphereShader = _renderer.GraphicsDevice.CreateShader("Sphere",
                GetEmbeddedResource("WorldBuilder.Shaders.Sphere.vert", typeof(GameScene).Assembly),
                GetEmbeddedResource("WorldBuilder.Shaders.Sphere.frag", typeof(GameScene).Assembly));

            InitializeSphereGeometry();
        }

        public static string GetEmbeddedResource(string filename, Assembly assembly) {
            using (Stream stream = assembly.GetManifestResourceStream(filename))
            using (StreamReader reader = new StreamReader(stream)) {
                string result = reader.ReadToEnd();
                return result;
            }
        }

        private unsafe void InitializeSphereGeometry() {
            var vertices = CreateSphere(8, 6);
            var indices = CreateSphereIndices(8, 6);
            _sphereIndexCount = indices.Length;

            _gl.GenVertexArrays(1, out _sphereVAO);
            _gl.BindVertexArray(_sphereVAO);

            _gl.GenBuffers(1, out _sphereVBO);
            _gl.BindBuffer(GLEnum.ArrayBuffer, _sphereVBO);
            fixed (VertexPositionNormal* ptr = vertices) {
                _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(vertices.Length * VertexPositionNormal.Size), ptr,
                    GLEnum.StaticDraw);
            }

            int stride = VertexPositionNormal.Size;
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, GLEnum.Float, false, (uint)stride, (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 3, GLEnum.Float, false, (uint)stride, (void*)(3 * sizeof(float)));

            _gl.GenBuffers(1, out _sphereInstanceVBO);
            _gl.BindBuffer(GLEnum.ArrayBuffer, _sphereInstanceVBO);
            _gl.BufferData(GLEnum.ArrayBuffer, 0, null, GLEnum.DynamicDraw);
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 3, GLEnum.Float, false, (uint)sizeof(Vector3), null);
            _gl.VertexAttribDivisor(2, 1);

            _gl.GenBuffers(1, out _sphereIBO);
            _gl.BindBuffer(GLEnum.ElementArrayBuffer, _sphereIBO);
            fixed (uint* iptr = indices) {
                _gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), iptr,
                    GLEnum.StaticDraw);
            }

            _gl.BindVertexArray(0);
        }

        private VertexPositionNormal[] CreateSphere(int longitudeSegments, int latitudeSegments) {
            var vertices = new List<VertexPositionNormal>();
            for (int lat = 0; lat <= latitudeSegments; lat++) {
                float theta = lat * MathF.PI / latitudeSegments;
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);
                for (int lon = 0; lon <= longitudeSegments; lon++) {
                    float phi = lon * 2 * MathF.PI / longitudeSegments;
                    float sinPhi = MathF.Sin(phi);
                    float cosPhi = MathF.Cos(phi);
                    float x = cosPhi * sinTheta;
                    float y = cosTheta;
                    float z = sinPhi * sinTheta;
                    Vector3 position = new Vector3(x, y, z);
                    Vector3 normal = Vector3.Normalize(position);
                    vertices.Add(new VertexPositionNormal(position, normal));
                }
            }

            return vertices.ToArray();
        }

        private uint[] CreateSphereIndices(int longitudeSegments, int latitudeSegments) {
            var indices = new List<uint>();
            for (int lat = 0; lat < latitudeSegments; lat++) {
                for (int lon = 0; lon < longitudeSegments; lon++) {
                    uint current = (uint)(lat * (longitudeSegments + 1) + lon);
                    uint next = current + (uint)(longitudeSegments + 1);
                    indices.Add(current);
                    indices.Add(next);
                    indices.Add(current + 1);
                    indices.Add(current + 1);
                    indices.Add(next);
                    indices.Add(next + 1);
                }
            }

            return indices.ToArray();
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

            // Pick up completed background loads (never blocks)
            IntegrateBackgroundLoadResults();

            // Process any pending scenery regenerations (from terrain editing)
            ProcessPendingSceneryRegen();

            // Check if we need to kick off new background loads
            float distMoved = Vector3.Distance(cameraPosition, _lastDocUpdatePosition);
            if (distMoved >= DocUpdateDistanceThreshold || _lastDocUpdatePosition.X == float.MinValue) {
                _lastDocUpdatePosition = cameraPosition;
                KickOffBackgroundLoads(cameraPosition);
                UnloadOutOfRangeLandblocks(cameraPosition);
            }

            // Incrementally warm up GPU render data (a few per frame, on main thread for GL context)
            WarmUpRenderData();

            long staticMs = sw.ElapsedMilliseconds;

            foreach (var chunkId in requiredChunks) {
                var chunkX = (uint)(chunkId >> 32);
                var chunkY = (uint)(chunkId & 0xFFFFFFFF);
                var chunk = DataManager.GetOrCreateChunk(chunkX, chunkY);

                if (!GPUManager.HasRenderData(chunkId)) {
                    GPUManager.CreateChunkResources(chunk, _terrainSystem);
                }
                else if (chunk.IsDirty) {
                    var dirtyLandblocks = chunk.DirtyLandblocks.ToList();
                    GPUManager.UpdateLandblocks(chunk, dirtyLandblocks, _terrainSystem);

                    // Queue scenery regeneration for dirty landblocks so scenery
                    // objects (trees, rocks) update their Z positions with new terrain heights
                    foreach (var lbId in dirtyLandblocks) {
                        var lbKey = (ushort)lbId;
                        if (_sceneryObjects.ContainsKey(lbKey)) {
                            _pendingSceneryRegen.Add(lbKey);
                        }
                    }
                }
            }

            long totalMs = sw.ElapsedMilliseconds;
            if (totalMs > 16) { // Log if frame took longer than ~60fps
                Console.WriteLine($"[GameScene.Update] {totalMs}ms total (statics: {staticMs}ms, terrain: {totalMs - staticMs}ms, warmup queue: {_renderDataWarmupQueue.Count})");
            }
        }

        /// <summary>
        /// Picks up results from background loading tasks and integrates them into the scene.
        /// Limited to MaxIntegratePerFrame per call to avoid frame spikes.
        /// </summary>
        private void IntegrateBackgroundLoadResults() {
            int integrated = 0;
            while (integrated < MaxIntegratePerFrame && _backgroundLoadResults.TryDequeue(out var result)) {
                _sceneryObjects[result.LbKey] = result.Scenery;
                _pendingLoadLandblocks.Remove(result.LbKey);

                // Queue render data creation for unique object IDs
                foreach (var (id, isSetup) in result.UniqueObjectIds) {
                    if (_objectManager.TryGetCachedRenderData(id) == null) {
                        _renderDataWarmupQueue.Enqueue((id, isSetup));
                    }
                }

                _staticObjectsDirty = true;
                integrated++;

                Console.WriteLine($"[Statics] Integrated landblock {result.DocId}: " +
                    $"doc={result.LoadMs}ms, scenery={result.SceneryMs}ms ({result.SceneryCount} objects), " +
                    $"unique models={result.UniqueObjectIds.Count}, warmup queue={_renderDataWarmupQueue.Count}");
            }
        }

        /// <summary>
        /// Determines which landblocks need loading and kicks off a background task.
        /// Never blocks the calling thread.
        /// </summary>
        private void KickOffBackgroundLoads(Vector3 cameraPosition) {
            // Don't start new loads if a batch is still running
            if (_backgroundLoadTask != null && !_backgroundLoadTask.IsCompleted) return;

            var visibleLandblocks = GetProximateLandblocks(cameraPosition);
            _lastVisibleLandblocks = visibleLandblocks;
            var currentLoaded = _documentManager.ActiveDocs.Keys.Where(k => k.StartsWith("landblock_")).ToHashSet();

            var toLoad = visibleLandblocks
                .Where(lbKey => !currentLoaded.Contains($"landblock_{lbKey:X4}") && !_pendingLoadLandblocks.Contains(lbKey))
                .ToList();

            if (toLoad.Count == 0) return;

            Console.WriteLine($"[Statics] {toLoad.Count} landblocks need loading, starting background task...");

            // Mark them as pending so we don't double-load
            foreach (var lbKey in toLoad) {
                _pendingLoadLandblocks.Add(lbKey);
            }

            // Capture references needed by the background thread
            var documentManager = _documentManager;
            var terrainSystem = _terrainSystem;
            var region = _region;
            var dats = _dats;
            var resultQueue = _backgroundLoadResults;

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
                            var scenerySw = Stopwatch.StartNew();
                            var scenery = GenerateSceneryThreadSafe(lbKey, doc, terrainSystem, region, dats);
                            long sceneryMs = scenerySw.ElapsedMilliseconds;

                            // Collect unique object IDs
                            var uniqueIds = new HashSet<(uint, bool)>();
                            foreach (var obj in doc.GetStaticObjects()) {
                                uniqueIds.Add((obj.Id, obj.IsSetup));
                            }
                            foreach (var obj in scenery) {
                                uniqueIds.Add((obj.Id, obj.IsSetup));
                            }

                            resultQueue.Enqueue(new BackgroundLoadResult(lbKey, docId, scenery, uniqueIds, loadMs, sceneryMs, scenery.Count));
                            loaded++;
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[Statics] Error loading {docId} on background thread: {ex.Message}");
                        // Remove from pending so it can be retried
                        resultQueue.Enqueue(new BackgroundLoadResult(lbKey, docId, new List<StaticObject>(), new HashSet<(uint, bool)>(), 0, 0, 0));
                    }
                }

                Console.WriteLine($"[Statics] Background batch complete: {loaded}/{toLoad.Count} landblocks in {batchSw.ElapsedMilliseconds}ms");
            });
        }

        /// <summary>
        /// Unloads landblock documents and scenery that are out of camera range.
        /// Limited to MaxUnloadsPerFrame to avoid frame spikes.
        /// </summary>
        private void UnloadOutOfRangeLandblocks(Vector3 cameraPosition) {
            var visibleLandblocks = _lastVisibleLandblocks ?? GetProximateLandblocks(cameraPosition);
            var currentLoaded = _documentManager.ActiveDocs.Keys.Where(k => k.StartsWith("landblock_")).ToHashSet();

            var toUnload = currentLoaded
                .Where(docId => !visibleLandblocks.Contains(ushort.Parse(docId.Replace("landblock_", ""), System.Globalization.NumberStyles.HexNumber)))
                .Take(MaxUnloadsPerFrame)
                .ToList();

            foreach (var docId in toUnload) {
                try {
                    var sw = Stopwatch.StartNew();

                    // Just remove scenery data - don't block on GPU cleanup or document close
                    var lbKey = ushort.Parse(docId.Replace("landblock_", ""), System.Globalization.NumberStyles.HexNumber);
                    if (_sceneryObjects.TryGetValue(lbKey, out var scenery)) {
                        foreach (var obj in scenery) {
                            _objectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                        }
                        _sceneryObjects.Remove(lbKey);
                    }

                    // Release static object render data
                    if (_documentManager.ActiveDocs.TryGetValue(docId, out var baseDoc) && baseDoc is LandblockDocument lbDoc) {
                        foreach (var obj in lbDoc.GetStaticObjects()) {
                            _objectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                        }
                    }

                    // Fire-and-forget the document close (DB/IO work)
                    _ = _documentManager.CloseDocumentAsync(docId);
                    _staticObjectsDirty = true;

                    Console.WriteLine($"[Statics] Unloaded {docId} in {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Statics] Error unloading {docId}: {ex.Message}");
                }
            }

            // If there are more to unload, force re-check next frame
            if (toUnload.Count >= MaxUnloadsPerFrame) {
                _lastDocUpdatePosition = new Vector3(float.MinValue);
            }
        }

        /// <summary>
        /// Incrementally creates GPU render data for queued objects using a time budget.
        /// Must run on the main thread (GL context). Stops as soon as the time budget is exceeded.
        /// </summary>
        private void WarmUpRenderData() {
            if (_renderDataWarmupQueue.Count == 0) return;

            var sw = Stopwatch.StartNew();
            int loaded = 0;
            int skipped = 0;
            while (_renderDataWarmupQueue.Count > 0) {
                // Check time budget BEFORE loading each model (except the first - always load at least one)
                if (loaded > 0 && sw.ElapsedMilliseconds >= WarmUpTimeBudgetMs) break;

                var (id, isSetup) = _renderDataWarmupQueue.Dequeue();
                if (_objectManager.TryGetCachedRenderData(id) != null) {
                    skipped++;
                    continue;
                }

                try {
                    var data = _objectManager.GetRenderData(id, isSetup);
                    loaded++;

                    // If this was a setup, queue its GfxObj parts for warmup too
                    if (data != null && data.IsSetup && data.SetupParts != null) {
                        foreach (var (partId, _) in data.SetupParts) {
                            if (_objectManager.TryGetCachedRenderData(partId) == null) {
                                _renderDataWarmupQueue.Enqueue((partId, false));
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Statics] WarmUp error for 0x{id:X8}: {ex.Message}");
                }
            }

            if (loaded > 0) {
                Console.WriteLine($"[Statics] WarmUp: {loaded} models in {sw.ElapsedMilliseconds}ms " +
                    $"(skipped {skipped} cached, remaining: {_renderDataWarmupQueue.Count})");
            }
        }

        /// <summary>
        /// Thread-safe version of GenerateScenery that doesn't access instance fields directly.
        /// </summary>
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
            var proximate = new HashSet<ushort>();
            var camLbX = (ushort)(cameraPosition.X / TerrainDataManager.LandblockLength);
            var camLbY = (ushort)(cameraPosition.Y / TerrainDataManager.LandblockLength);

            // Simple 2D grid search around camera (e.g., +/- 3 landblocks)
            var lbd = (int)Math.Ceiling(ProximityThreshold / 192f / 2f);
            for (int dx = -lbd; dx <= lbd; dx++) {
                for (int dy = -lbd; dy <= lbd; dy++) {
                    var lbX = (ushort)Math.Clamp(camLbX + dx, 0, TerrainDataManager.MapSize - 1);
                    var lbY = (ushort)Math.Clamp(camLbY + dy, 0, TerrainDataManager.MapSize - 1);
                    var lbKey = (ushort)((lbX << 8) | lbY);
                    var lbCenter = new Vector2(
                        lbX * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2,
                        lbY * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2);
                    var dist2D = Vector2.Distance(new Vector2(cameraPosition.X, cameraPosition.Y), lbCenter);
                    if (dist2D <= ProximityThreshold) {
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

                GPUManager.CreateChunkResources(chunk, _terrainSystem);
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

                // Mark scenery for background regeneration instead of blocking
                var lbKey = (ushort)landblockId;
                if (_sceneryObjects.ContainsKey(lbKey)) {
                    _pendingSceneryRegen.Add(lbKey);
                }
            }

            // Kick off background regen for any pending scenery
            ProcessPendingSceneryRegen();

            foreach (var kvp in landblocksByChunk) {
                var chunk = DataManager.GetChunk(kvp.Key);
                if (chunk != null) {
                    GPUManager.UpdateLandblocks(chunk, kvp.Value, _terrainSystem);
                }
            }
        }

        /// <summary>
        /// Regenerates scenery for modified landblocks on a background thread.
        /// </summary>
        private void ProcessPendingSceneryRegen() {
            if (_pendingSceneryRegen.Count == 0) return;
            if (_backgroundLoadTask != null && !_backgroundLoadTask.IsCompleted) return; // Wait for current batch

            var toRegen = _pendingSceneryRegen.ToList();
            _pendingSceneryRegen.Clear();

            // Mark them as pending loads so they aren't double-queued
            foreach (var lbKey in toRegen) {
                _pendingLoadLandblocks.Add(lbKey);
            }

            var documentManager = _documentManager;
            var terrainSystem = _terrainSystem;
            var region = _region;
            var dats = _dats;
            var resultQueue = _backgroundLoadResults;

            Console.WriteLine($"[Statics] Regenerating scenery for {toRegen.Count} landblocks in background...");

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

        /// <summary>
        /// Convenience wrapper for main-thread callers (e.g. UpdateLandblocks scenery regen).
        /// </summary>
        private List<StaticObject> GenerateScenery(ushort lbKey, LandblockDocument lbDoc) {
            return GenerateSceneryThreadSafe(lbKey, lbDoc, _terrainSystem, _region, _dats);
        }

        public IEnumerable<(TerrainChunk chunk, ChunkRenderData renderData)> GetRenderableChunks(Frustum frustum) {
            foreach (var chunk in DataManager.GetAllChunks()) {
                if (!frustum.IntersectsBoundingBox(chunk.Bounds)) continue;

                var renderData = GPUManager.GetRenderData(chunk.GetChunkId());
                if (renderData != null) {
                    yield return (chunk, renderData);
                }
            }
        }

        public int GetLoadedChunkCount() => DataManager.GetAllChunks().Count();
        public int GetVisibleChunkCount(Frustum frustum) => GetRenderableChunks(frustum).Count();

        public IEnumerable<StaticObject> GetAllStaticObjects() {
            if (_staticObjectsDirty || _cachedStaticObjects == null
                || _cachedShowStaticObjects != ShowStaticObjects
                || _cachedShowScenery != ShowScenery) {
                var statics = new List<StaticObject>();
                if (ShowStaticObjects) {
                    foreach (var doc in _documentManager.ActiveDocs.Values.OfType<LandblockDocument>()) {
                        statics.AddRange(doc.GetStaticObjects());
                    }
                }
                if (ShowScenery) {
                    statics.AddRange(_sceneryObjects.Values.SelectMany(x => x));
                }
                _cachedStaticObjects = statics;
                _cachedShowStaticObjects = ShowStaticObjects;
                _cachedShowScenery = ShowScenery;
                _staticObjectsDirty = false;
            }
            return _cachedStaticObjects;
        }

        /// <summary>
        /// Marks the static objects cache as dirty so it gets rebuilt next frame.
        /// Call this after modifying landblock documents or scenery.
        /// </summary>
        public void InvalidateStaticObjectsCache() {
            _staticObjectsDirty = true;
        }

        public void Render(
            ICamera camera,
            float aspectRatio,
            TerrainEditingContext editingContext,
            float width,
            float height) {
            _aspectRatio = aspectRatio;
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.DepthMask(true);
            _gl.ClearColor(0.2f, 0.3f, 0.8f, 1.0f);
            _gl.ClearDepth(1f);
            _gl.Clear(
                ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Back);

            Matrix4x4 model = Matrix4x4.Identity;
            Matrix4x4 view = camera.GetViewMatrix();
            Matrix4x4 projection = camera.GetProjectionMatrix();
            Matrix4x4 viewProjection = view * projection;

            float cameraDistance = MathF.Abs(camera.Position.Z);
            if (camera is OrthographicTopDownCamera orthoCamera) {
                cameraDistance = orthoCamera.OrthographicSize;
            }

            var frustum = new Frustum(viewProjection);
            var renderableChunks = GetRenderableChunks(frustum);

            // Render terrain
            RenderTerrain(renderableChunks, model, camera, cameraDistance, width, height);

            // Render active vertex spheres
            if (editingContext.ActiveVertices.Count > 0) {
                RenderActiveSpheres(editingContext, camera, model, viewProjection);
            }

            // Render static objects
            var renderSw = Stopwatch.StartNew();
            var staticObjects = GetAllStaticObjects().ToList();
            if (staticObjects.Count > 0) {
                RenderStaticObjects(staticObjects, camera, viewProjection);
            }
            long renderStaticsMs = renderSw.ElapsedMilliseconds;
            if (renderStaticsMs > 16) {
                Console.WriteLine($"[GameScene.Render] Static objects: {renderStaticsMs}ms ({staticObjects.Count} objects)");
            }

            // Render selection highlight
            if (editingContext.ObjectSelection.HasSelection) {
                RenderSelectionHighlight(editingContext.ObjectSelection, camera, viewProjection);
            }

            // Render placement preview
            if (editingContext.ObjectSelection.IsPlacementMode && editingContext.ObjectSelection.PlacementPreview.HasValue) {
                RenderPlacementPreview(editingContext.ObjectSelection.PlacementPreview.Value, camera, viewProjection);
            }
        }

        /// <summary>
        /// Renders a highlight around the selected object using corner spheres.
        /// </summary>
        private unsafe void RenderSelectionHighlight(ObjectSelectionState selection, ICamera camera, Matrix4x4 viewProjection) {
            if (!selection.HasSelection) return;

            // Collect corners for all selected objects
            var allCorners = new List<Vector3>();
            foreach (var entry in selection.SelectedEntries) {
                var obj = entry.Object;
                var bounds = _objectManager.GetBounds(obj.Id, obj.IsSetup);
                if (bounds == null) continue;

                var (localMin, localMax) = bounds.Value;
                var worldTransform = Matrix4x4.CreateScale(obj.Scale)
                    * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                    * Matrix4x4.CreateTranslation(obj.Origin);

                allCorners.Add(Vector3.Transform(new Vector3(localMin.X, localMin.Y, localMin.Z), worldTransform));
                allCorners.Add(Vector3.Transform(new Vector3(localMax.X, localMin.Y, localMin.Z), worldTransform));
                allCorners.Add(Vector3.Transform(new Vector3(localMin.X, localMax.Y, localMin.Z), worldTransform));
                allCorners.Add(Vector3.Transform(new Vector3(localMax.X, localMax.Y, localMin.Z), worldTransform));
                allCorners.Add(Vector3.Transform(new Vector3(localMin.X, localMin.Y, localMax.Z), worldTransform));
                allCorners.Add(Vector3.Transform(new Vector3(localMax.X, localMin.Y, localMax.Z), worldTransform));
                allCorners.Add(Vector3.Transform(new Vector3(localMin.X, localMax.Y, localMax.Z), worldTransform));
                allCorners.Add(Vector3.Transform(new Vector3(localMax.X, localMax.Y, localMax.Z), worldTransform));
            }

            if (allCorners.Count == 0) return;

            // Render spheres at all corners in one draw call
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            _sphereShader.Bind();
            _sphereShader.SetUniform("uViewProjection", viewProjection);
            _sphereShader.SetUniform("uCameraPosition", camera.Position);
            _sphereShader.SetUniform("uSphereColor", new Vector3(1.0f, 0.8f, 0.0f)); // Gold highlight
            _sphereShader.SetUniform("uLightDirection", Vector3.Normalize(LightDirection));
            _sphereShader.SetUniform("uAmbientIntensity", 0.8f);
            _sphereShader.SetUniform("uSpecularPower", SpecularPower);
            _sphereShader.SetUniform("uGlowColor", new Vector3(1.0f, 0.8f, 0.0f));
            _sphereShader.SetUniform("uGlowIntensity", 2.0f);
            _sphereShader.SetUniform("uGlowPower", 0.3f);
            _sphereShader.SetUniform("uSphereRadius", SphereRadius * 0.5f);

            var cornersArray = allCorners.ToArray();
            _gl.BindBuffer(GLEnum.ArrayBuffer, _sphereInstanceVBO);
            fixed (Vector3* posPtr = cornersArray) {
                _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(cornersArray.Length * sizeof(Vector3)), posPtr, GLEnum.DynamicDraw);
            }

            _gl.BindVertexArray(_sphereVAO);
            _gl.DrawElementsInstanced(GLEnum.Triangles, (uint)_sphereIndexCount, GLEnum.UnsignedInt, null, (uint)cornersArray.Length);
            _gl.BindVertexArray(0);
            _gl.UseProgram(0);
            _gl.Disable(EnableCap.Blend);
        }

        /// <summary>
        /// Renders a translucent preview of the object being placed.
        /// </summary>
        private void RenderPlacementPreview(StaticObject previewObj, ICamera camera, Matrix4x4 viewProjection) {
            // Load render data on demand (safe here since we're on the GL thread)
            var renderData = _objectManager.GetRenderData(previewObj.Id, previewObj.IsSetup);
            if (renderData == null) return;

            // For setup objects, also load each GfxObj part on demand
            if (renderData.IsSetup && renderData.SetupParts != null) {
                foreach (var (partId, _) in renderData.SetupParts) {
                    _objectManager.GetRenderData(partId, false);
                }
            }

            var objects = new List<StaticObject> { previewObj };
            RenderStaticObjects(objects, camera, viewProjection);
        }

        private void RenderTerrain(
            IEnumerable<(TerrainChunk chunk, ChunkRenderData renderData)> renderableChunks,
            Matrix4x4 model,
            ICamera camera,
            float cameraDistance,
            float width,
            float height) {
            _terrainShader.Bind();
            _terrainShader.SetUniform("xAmbient", AmbientLightIntensity);
            _terrainShader.SetUniform("xWorld", model);
            _terrainShader.SetUniform("xView", camera.GetViewMatrix());
            _terrainShader.SetUniform("xProjection", camera.GetProjectionMatrix());
            _terrainShader.SetUniform("uAlpha", 1f);
            _terrainShader.SetUniform("uShowLandblockGrid", ShowGrid ? 1 : 0);
            _terrainShader.SetUniform("uShowCellGrid", ShowGrid ? 1 : 0);
            _terrainShader.SetUniform("uLandblockGridColor", LandblockGridColor);
            _terrainShader.SetUniform("uCellGridColor", CellGridColor);
            _terrainShader.SetUniform("uGridLineWidth", GridLineWidth);
            _terrainShader.SetUniform("uGridOpacity", GridOpacity);
            _terrainShader.SetUniform("uCameraDistance", cameraDistance);
            _terrainShader.SetUniform("uScreenHeight", height);

            // Slope highlight uniforms
            _terrainShader.SetUniform("uShowSlopeHighlight", ShowSlopeHighlight ? 1 : 0);
            _terrainShader.SetUniform("uSlopeThreshold", SlopeThreshold * MathF.PI / 180f); // Convert degrees to radians
            _terrainShader.SetUniform("uSlopeHighlightColor", SlopeHighlightColor);
            _terrainShader.SetUniform("uSlopeHighlightOpacity", SlopeHighlightOpacity);

            SurfaceManager.TerrainAtlas.Bind(0);
            _terrainShader.SetUniform("xOverlays", 0);
            SurfaceManager.AlphaAtlas.Bind(1);
            _terrainShader.SetUniform("xAlphas", 1);

            foreach (var (_, renderData) in renderableChunks) {
                renderData.ArrayBuffer.Bind();
                renderData.VertexBuffer.Bind();
                renderData.IndexBuffer.Bind();
                GLHelpers.CheckErrors();
                _renderer.GraphicsDevice.DrawElements(Chorizite.Core.Render.Enums.PrimitiveType.TriangleList,
                    renderData.TotalIndexCount);
                renderData.ArrayBuffer.Unbind();
                renderData.VertexBuffer.Unbind();
                renderData.IndexBuffer.Unbind();
            }
        }

        private unsafe void RenderActiveSpheres(
            TerrainEditingContext editingContext,
            ICamera camera,
            Matrix4x4 model,
            Matrix4x4 viewProjection) {
            var activeVerts = editingContext.ActiveVertices.ToArray();
            if (activeVerts.Length == 0) return;

            int count = activeVerts.Length;
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++) {
                try {
                    var vertex = activeVerts[i];
                    positions[i] = new Vector3(
                        vertex.X,
                        vertex.Y,
                        DataManager.GetHeightAtPosition(vertex.X, vertex.Y) + SphereHeightOffset);
                }
                catch {
                    positions[i] = Vector3.Zero;
                }
            }

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            _sphereShader.Bind();
            _sphereShader.SetUniform("uViewProjection", viewProjection);
            _sphereShader.SetUniform("uCameraPosition", camera.Position);
            _sphereShader.SetUniform("uSphereColor", SphereColor);
            Vector3 normLight = Vector3.Normalize(LightDirection);
            _sphereShader.SetUniform("uLightDirection", normLight);
            _sphereShader.SetUniform("uAmbientIntensity", AmbientLightIntensity);
            _sphereShader.SetUniform("uSpecularPower", SpecularPower);
            _sphereShader.SetUniform("uGlowColor", SphereGlowColor);
            _sphereShader.SetUniform("uGlowIntensity", SphereGlowIntensity);
            _sphereShader.SetUniform("uGlowPower", SphereGlowPower);
            _sphereShader.SetUniform("uSphereRadius", SphereRadius);

            _gl.BindBuffer(GLEnum.ArrayBuffer, _sphereInstanceVBO);
            fixed (Vector3* posPtr = positions) {
                _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(count * sizeof(Vector3)), posPtr, GLEnum.DynamicDraw);
            }

            _gl.BindVertexArray(_sphereVAO);
            _gl.DrawElementsInstanced(GLEnum.Triangles, (uint)_sphereIndexCount, GLEnum.UnsignedInt, null, (uint)count);
            _gl.BindVertexArray(0);
            _gl.UseProgram(0);
            _gl.Disable(EnableCap.Blend);
        }


        private unsafe void RenderStaticObjects(List<StaticObject> objects, ICamera camera, Matrix4x4 viewProjection) {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);

            _objectManager._objectShader.Bind();
            _objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            _objectManager._objectShader.SetUniform("uCameraPosition", camera.Position);
            _objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(LightDirection));
            _objectManager._objectShader.SetUniform("uAmbientIntensity", AmbientLightIntensity);
            _objectManager._objectShader.SetUniform("uSpecularPower", SpecularPower);

            // Group objects by (Id, IsSetup)
            var groups = objects.GroupBy(o => (Id: o.Id, IsSetup: o.IsSetup))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(o => (
                        Transform: Matrix4x4.CreateScale(o.Scale) * Matrix4x4.CreateFromQuaternion(o.Orientation) * Matrix4x4.CreateTranslation(o.Origin),
                        Object: o
                    )).ToList()
                );

            foreach (var group in groups) {
                var (id, isSetup) = group.Key;

                // Non-blocking: only render objects whose render data is already cached
                var renderData = _objectManager.TryGetCachedRenderData(id);
                if (renderData == null) continue; // Will be loaded incrementally by WarmUpRenderData

                if (isSetup) {
                    // Setup objects - render each part
                    foreach (var (partId, partTransform) in renderData.SetupParts) {
                        var partRenderData = _objectManager.TryGetCachedRenderData(partId);
                        if (partRenderData == null) continue;

                        // Create instance data for each instance of this setup
                        var instanceData = group.Value.Select(inst =>
                            partTransform * inst.Transform
                        ).ToList();

                        RenderBatchedObject(partRenderData, instanceData);
                    }
                }
                else {
                    // Simple GfxObj - render directly
                    var instanceData = group.Value.Select(inst => inst.Transform).ToList();
                    RenderBatchedObject(renderData, instanceData);
                }
            }

            _gl.BindVertexArray(0);
            _gl.UseProgram(0);
            _gl.Disable(EnableCap.Blend);
        }

        private unsafe void RenderBatchedObject(StaticObjectRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (instanceTransforms.Count == 0 || renderData.Batches.Count == 0) return;

            // Create instance buffer - 16 floats per Matrix4x4
            uint instanceVBO;
            _gl.GenBuffers(1, out instanceVBO);
            _gl.BindBuffer(GLEnum.ArrayBuffer, instanceVBO);

            var instanceBuffer = new float[instanceTransforms.Count * 16];
            for (int i = 0; i < instanceTransforms.Count; i++) {
                var transform = instanceTransforms[i];
                float[] matrixData = new float[16] {
                    transform.M11, transform.M12, transform.M13, transform.M14,
                    transform.M21, transform.M22, transform.M23, transform.M24,
                    transform.M31, transform.M32, transform.M33, transform.M34,
                    transform.M41, transform.M42, transform.M43, transform.M44
                };
                Array.Copy(matrixData, 0, instanceBuffer, i * 16, 16);
            }

            fixed (float* ptr = instanceBuffer) {
                _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(instanceBuffer.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }

            _gl.BindVertexArray(renderData.VAO);

            // Set up instance attributes (mat4 takes 4 attribute slots)
            for (int i = 0; i < 4; i++) {
                _gl.EnableVertexAttribArray((uint)(3 + i));
                _gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false, (uint)(16 * sizeof(float)), (void*)(i * 4 * sizeof(float)));
                _gl.VertexAttribDivisor((uint)(3 + i), 1);
            }

            // Render each batch with its texture
            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;

                try {
                    // Bind the texture array for this batch
                    batch.TextureArray.Bind(0);
                    _objectManager._objectShader.SetUniform("uTextureArray", 0);

                    // Set the texture layer index
                    _objectManager._objectShader.SetUniform("uTextureIndex", (float)batch.TextureIndex);

                    // Bind the index buffer for this batch
                    _gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);

                    // Draw all instances with this batch
                    _gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, (uint)instanceTransforms.Count);
                }
                catch (Exception ex) {
                    Console.WriteLine($"Error rendering batch (texture index {batch.TextureIndex}): {ex.Message}");
                }
            }

            _gl.BindVertexArray(0);
            _gl.DeleteBuffer(instanceVBO);
        }

        public void Dispose() {
            if (!_disposed) {
                _gl.DeleteBuffer(_sphereVBO);
                _gl.DeleteBuffer(_sphereIBO);
                _gl.DeleteBuffer(_sphereInstanceVBO);
                _gl.DeleteVertexArray(_sphereVAO);
                _objectManager?.Dispose();
                GPUManager?.Dispose();
                _disposed = true;
            }
        }
    }
}
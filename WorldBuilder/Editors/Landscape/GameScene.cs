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
        private const float PerspectiveProximityThreshold = 500f; // 2D distance for perspective camera
        private const int MaxLoadedLandblocks = 200; // Cap to prevent memory explosion when zoomed out
        private const float SceneryDistanceThreshold = 600f; // Beyond this, skip scenery (trees/rocks) — too small to see
        private const float DungeonDistanceThreshold = 400f; // Beyond this, skip dungeon EnvCell loading — tighter than surface objects
        private const int MaxBatchSize = 30; // Max landblocks to load per background batch

        private OpenGLRenderer _renderer => _terrainSystem.Renderer;
        private WorldBuilderSettings _settings => _terrainSystem.Settings;
        private GL _gl => _renderer.GraphicsDevice.GL;
        private IShader _terrainShader;
        private IShader _sphereShader;

        internal readonly StaticObjectManager _objectManager;
        internal readonly EnvCellManager _envCellManager;
        private ThumbnailRenderService? _thumbnailService;
        public ThumbnailRenderService? ThumbnailService => _thumbnailService;
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
        private readonly Dictionary<ushort, List<StaticObject>> _dungeonStaticObjects = new();
        private readonly Dictionary<ushort, List<StaticObject>> _buildingStaticObjects = new();
        internal readonly TerrainSystem _terrainSystem;

        // Static object background loading state
        private const float WarmUpTimeBudgetMs = 12f; // Max ms per frame for GPU model upload
        private const int MaxIntegratePerFrame = 8; // Max background results to integrate per frame
        private const int MaxUnloadsPerFrame = 2; // Max landblocks to unload per frame
        private const int MaxGpuUploadsPerFrame = 4; // Max prepared models to upload to GPU per frame
        private const float DocUpdateDistanceThresholdBase = 96f; // Re-check after camera moves ~4 cells
        private Vector3 _lastDocUpdatePosition = new(float.MinValue);
        private float _lastOrthoSize = -1f; // Track zoom level for ortho camera reload trigger
        private readonly Queue<(uint Id, bool IsSetup)> _renderDataWarmupQueue = new();
        private List<StaticObject>? _cachedStaticObjects;
        private bool _staticObjectsDirty = true;
        private bool _cachedShowStaticObjects = true;
        private bool _cachedShowScenery = true;
        private bool _cachedShowDungeons = true;
        private ushort? _cachedFocusedDungeonLB = null;

        // Persistent instance buffer for static object instanced rendering (avoids per-frame alloc/dealloc)
        private uint _instanceVBO;
        private int _instanceBufferCapacity; // in floats
        private float[] _instanceUploadBuffer = Array.Empty<float>(); // reusable CPU-side buffer

        // Reusable collections for static object grouping (avoids per-frame LINQ allocations)
        private readonly Dictionary<(uint Id, bool IsSetup), List<Matrix4x4>> _objectGroupBuffer = new();
        private readonly List<Matrix4x4> _tempInstanceTransforms = new();

        // Two-phase model loading: CPU preparation on background thread, GPU upload on main thread
        private Task? _modelPrepTask;
        private readonly ConcurrentQueue<PreparedModelData> _preparedModelQueue = new();
        private readonly HashSet<uint> _modelsPreparing = new(); // IDs currently being prepared

        // Two-phase terrain chunk loading: geometry on background thread, GPU upload on GL thread
        private const int MaxChunkUploadsPerFrame = 4;
        private readonly ConcurrentQueue<PreparedChunkData> _chunkUploadQueue = new();
        private readonly ConcurrentQueue<TerrainChunk> _chunkGenQueue = new();
        private readonly HashSet<ulong> _chunksInFlight = new(); // Chunks queued or being generated
        private Task? _chunkGenTask; // Single background task for serialized chunk generation

        // Background loading pipeline
        private Task? _backgroundLoadTask;
        private readonly ConcurrentQueue<BackgroundLoadResult> _backgroundLoadResults = new();
        private readonly HashSet<ushort> _pendingLoadLandblocks = new(); // LB keys currently being loaded
        private readonly HashSet<ushort> _pendingSceneryRegen = new(); // LB keys needing scenery regen
        private HashSet<ushort>? _lastVisibleLandblocks;

        /// <summary>
        /// The set of landblock keys currently loaded / visible near the camera.
        /// Used by tools (e.g. bucket fill) to constrain operations to what the user can see.
        /// </summary>
        public HashSet<ushort>? VisibleLandblocks => _lastVisibleLandblocks;

        private record BackgroundLoadResult(ushort LbKey, string DocId, List<StaticObject> Scenery, HashSet<(uint Id, bool IsSetup)> UniqueObjectIds, long LoadMs, long SceneryMs, int SceneryCount, PreparedEnvCellBatch? EnvCellBatch = null);

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

            // Restore saved camera state
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
            //CameraManager.AddCamera(PerspectiveCamera);

            DataManager = new TerrainDataManager(terrainSystem, 16);
            SurfaceManager = new LandSurfaceManager(_renderer, _dats, _region);
            GPUManager = new TerrainGPUResourceManager(_renderer);

            // Create texture disk cache for processed RGBA data (avoids re-decompressing DXT/INDEX16 each session)
            var textureCacheDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "WorldBuilder", "TextureCache");
            var textureCache = new TextureDiskCache(textureCacheDir);
            _objectManager = new StaticObjectManager(_renderer, _dats, textureCache);
            _envCellManager = new EnvCellManager(_renderer, _dats, _objectManager._objectShader, textureCache);
            _thumbnailService = new ThumbnailRenderService(_gl, _objectManager);

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
            _gl.VertexAttribPointer(2, 4, GLEnum.Float, false, (uint)sizeof(Vector4), null);
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

            // Check if we need to kick off new background loads and unload distant data
            // Triggers on: camera movement beyond threshold, zoom level change, or first frame
            float docUpdateThreshold = DocUpdateDistanceThresholdBase;
            bool zoomChanged = false;
            if (CameraManager?.Current is OrthographicTopDownCamera ortho) {
                // Adaptive threshold: when zoomed out, re-check more often
                if (ortho.OrthographicSize > 1800f) {
                    docUpdateThreshold = Math.Max(48f, DocUpdateDistanceThresholdBase * (1800f / ortho.OrthographicSize));
                }
                // Detect significant zoom changes (>10% difference triggers reload)
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

                // Check if any loaded landblocks now deserve scenery that they didn't
                // get (loaded at distance with scenery skipped). Runs on every re-check
                // so panning back to an area also fills in scenery, not just zooming.
                RefreshSceneryForNearbyLandblocks(cameraPosition);

                // Process scenery regen immediately (don't wait for next frame's
                // ProcessPendingSceneryRegen call which already ran earlier this frame)
                ProcessPendingSceneryRegen();
            }

            // Incrementally warm up GPU render data (a few per frame, on main thread for GL context)
            WarmUpRenderData();

            // Process queued dungeon EnvCell GPU uploads (must happen on GL thread)
            _envCellManager.ProcessUploads(maxPerFrame: 2);

            // Thumbnail rendering moved to end of Render() where GL state is known-good

            long staticMs = sw.ElapsedMilliseconds;

            // Queue missing chunks for background generation (non-blocking)
            foreach (var chunkId in requiredChunks) {
                var chunkX = (uint)(chunkId >> 32);
                var chunkY = (uint)(chunkId & 0xFFFFFFFF);
                var chunk = DataManager.GetOrCreateChunk(chunkX, chunkY);

                if (!GPUManager.HasRenderData(chunkId)) {
                    // Queue for background generation instead of blocking
                    QueueChunkForGeneration(chunk);
                }
                else if (chunk.IsDirty) {
                    // Dirty updates are small (single landblock) — keep synchronous
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

            // Upload prepared chunks to GPU (GL thread, limited per frame)
            ProcessChunkUploads();

            long totalMs = sw.ElapsedMilliseconds;
            if (totalMs > 200) { // Log only significant frame delays
                Console.WriteLine($"[GameScene.Update] {totalMs}ms total (statics: {staticMs}ms, terrain: {totalMs - staticMs}ms, warmup queue: {_renderDataWarmupQueue.Count}, chunk gen: {_chunkGenQueue.Count}, chunk upload: {_chunkUploadQueue.Count})");
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
                    if (_objectManager.TryGetCachedRenderData(id) == null && !_objectManager.IsKnownFailure(id)) {
                        _renderDataWarmupQueue.Enqueue((id, isSetup));
                    }
                }

                // Upload dungeon EnvCell GPU data (must happen on GL thread)
                if (result.EnvCellBatch != null) {
                    _envCellManager.QueueForUpload(result.EnvCellBatch);

                    // Add dungeon static objects (only shown when dungeon is focused)
                    if (result.EnvCellBatch.DungeonStaticObjects.Count > 0) {
                        _dungeonStaticObjects[result.LbKey] = result.EnvCellBatch.DungeonStaticObjects;
                        foreach (var obj in result.EnvCellBatch.DungeonStaticObjects) {
                            if (_objectManager.TryGetCachedRenderData(obj.Id) == null && !_objectManager.IsKnownFailure(obj.Id)) {
                                _renderDataWarmupQueue.Enqueue((obj.Id, obj.IsSetup));
                            }
                        }
                    }

                    // Add building interior static objects (always shown with regular statics)
                    if (result.EnvCellBatch.BuildingStaticObjects.Count > 0) {
                        _buildingStaticObjects[result.LbKey] = result.EnvCellBatch.BuildingStaticObjects;
                        foreach (var obj in result.EnvCellBatch.BuildingStaticObjects) {
                            if (_objectManager.TryGetCachedRenderData(obj.Id) == null && !_objectManager.IsKnownFailure(obj.Id)) {
                                _renderDataWarmupQueue.Enqueue((obj.Id, obj.IsSetup));
                            }
                        }
                    }
                }

                _staticObjectsDirty = true;
                integrated++;

                Console.WriteLine($"[Statics] Integrated landblock {result.DocId}: " +
                    $"doc={result.LoadMs}ms, scenery={result.SceneryMs}ms ({result.SceneryCount} objects), " +
                    $"unique models={result.UniqueObjectIds.Count}, warmup queue={_renderDataWarmupQueue.Count}" +
                    (result.EnvCellBatch != null ? $", envCells={result.EnvCellBatch.Cells.Count}" : ""));
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

            // Sort by distance from camera — closest landblocks load first so the
            // user sees nearby objects immediately while distant ones stream in
            var camPos2D = new Vector2(cameraPosition.X, cameraPosition.Y);
            toLoad.Sort((a, b) => {
                float distA = Vector2.Distance(camPos2D, LandblockCenter(a));
                float distB = Vector2.Distance(camPos2D, LandblockCenter(b));
                return distA.CompareTo(distB);
            });

            // Limit batch size so the background task finishes quickly and we can
            // kick off the next batch with updated camera position
            if (toLoad.Count > MaxBatchSize) {
                // Only mark the ones we'll actually load as pending
                toLoad = toLoad.Take(MaxBatchSize).ToList();
            }

            Console.WriteLine($"[Statics] {toLoad.Count} landblocks queued for loading (of {visibleLandblocks.Count} visible)");

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
            var sceneryThreshold = GetEffectiveSceneryThreshold();
            var dungeonThreshold = GetEffectiveDungeonThreshold();
            var camPosCapture = cameraPosition;
            var envCellManager = _envCellManager;

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
                            // Only generate scenery (trees, rocks) for nearby landblocks.
                            // At distance, scenery objects are too small to see and are the
                            // heaviest part of loading (hundreds of objects per landblock).
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
                                scenery = new List<StaticObject>(); // Skip scenery for distant landblocks
                            }

                            // Collect unique object IDs
                            var uniqueIds = new HashSet<(uint, bool)>();
                            foreach (var obj in doc.GetStaticObjects()) {
                                uniqueIds.Add((obj.Id, obj.IsSetup));
                            }
                            foreach (var obj in scenery) {
                                uniqueIds.Add((obj.Id, obj.IsSetup));
                            }

                            // Load dungeon EnvCell geometry (CPU preparation on background thread)
                            // Only load for nearby landblocks — dungeons cluster underground
                            // and loading all of them causes heavy lag.
                            PreparedEnvCellBatch? envCellBatch = null;
                            if (distFromCamera <= dungeonThreshold && !envCellManager.HasLoadedCells(lbKey)) {
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
                        // Remove from pending so it can be retried
                        resultQueue.Enqueue(new BackgroundLoadResult(lbKey, docId, new List<StaticObject>(), new HashSet<(uint, bool)>(), 0, 0, 0));
                    }
                }

                Console.WriteLine($"[Statics] Background batch complete: {loaded}/{toLoad.Count} landblocks in {batchSw.ElapsedMilliseconds}ms");
            });
        }

        /// <summary>
        /// Returns the world-space center of a landblock given its key.
        /// </summary>
        private static Vector2 LandblockCenter(ushort lbKey) {
            int lbX = (lbKey >> 8) & 0xFF;
            int lbY = lbKey & 0xFF;
            return new Vector2(
                lbX * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2f,
                lbY * TerrainDataManager.LandblockLength + TerrainDataManager.LandblockLength / 2f);
        }

        /// <summary>
        /// Unloads landblock documents and scenery that are well outside camera range.
        /// Uses a larger boundary than loading (hysteresis) to prevent thrashing at edges.
        /// Limited to MaxUnloadsPerFrame to avoid frame spikes.
        /// </summary>
        private void UnloadOutOfRangeLandblocks(Vector3 cameraPosition) {
            // Compute a larger "keep loaded" set to prevent thrashing at boundaries.
            // Landblocks in the buffer zone (between load and unload boundaries) stay loaded
            // but won't be newly loaded — same principle as TerrainDataManager's LoadRange vs UnloadRange.
            var keepLoadedSet = GetUnloadBoundaryLandblocks(cameraPosition);
            var currentLoaded = _documentManager.ActiveDocs.Keys.Where(k => k.StartsWith("landblock_")).ToHashSet();

            var toUnload = currentLoaded
                .Where(docId => !keepLoadedSet.Contains(ushort.Parse(docId.Replace("landblock_", ""), System.Globalization.NumberStyles.HexNumber)))
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

                    // Unload dungeon EnvCell GPU data and static objects for this landblock
                    _envCellManager.UnloadLandblock(lbKey);
                    if (_dungeonStaticObjects.TryGetValue(lbKey, out var dungeonObjs)) {
                        foreach (var obj in dungeonObjs) {
                            _objectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                        }
                        _dungeonStaticObjects.Remove(lbKey);
                    }
                    if (_buildingStaticObjects.TryGetValue(lbKey, out var buildingObjs)) {
                        foreach (var obj in buildingObjs) {
                            _objectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                        }
                        _buildingStaticObjects.Remove(lbKey);
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
        /// Unloads dungeon EnvCell data for landblocks beyond the dungeon distance threshold.
        /// Dungeons use a tighter boundary than surface objects since they're underground
        /// and many cluster close together.
        /// </summary>
        private void UnloadDistantDungeons(Vector3 cameraPosition) {
            // Use 1.5x the dungeon threshold for hysteresis (same pattern as landblock unloading)
            float unloadThreshold = DungeonDistanceThreshold * 1.5f;
            var camPos2D = new Vector2(cameraPosition.X, cameraPosition.Y);

            // Collect landblock keys that have loaded dungeon cells but are too far
            var toUnload = new List<ushort>();
            foreach (var lbKey in _envCellManager.GetLoadedLandblockKeys()) {
                float dist = Vector2.Distance(camPos2D, LandblockCenter(lbKey));
                if (dist > unloadThreshold) {
                    toUnload.Add(lbKey);
                }
            }

            foreach (var lbKey in toUnload) {
                _envCellManager.UnloadLandblock(lbKey);
                if (_dungeonStaticObjects.TryGetValue(lbKey, out var dungeonObjs)) {
                    foreach (var obj in dungeonObjs) {
                        _objectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                    }
                    _dungeonStaticObjects.Remove(lbKey);
                }
                if (_buildingStaticObjects.TryGetValue(lbKey, out var buildingObjs2)) {
                    foreach (var obj in buildingObjs2) {
                        _objectManager.ReleaseRenderData(obj.Id, obj.IsSetup);
                    }
                    _buildingStaticObjects.Remove(lbKey);
                }
                _staticObjectsDirty = true;
                Console.WriteLine($"[Statics] Unloaded distant dungeon data for LB 0x{lbKey:X4}");
            }
        }

        /// <summary>
        /// Unloads terrain chunks that are far from the camera to free GPU memory.
        /// </summary>
        private void UnloadDistantChunks(Vector3 cameraPosition) {
            var chunksToUnload = DataManager.GetChunksToUnload(cameraPosition);
            foreach (var chunkId in chunksToUnload) {
                _chunksInFlight.Remove(chunkId);
                GPUManager.DisposeChunkResources(chunkId);
                DataManager.RemoveChunk(chunkId);
            }
        }

        /// <summary>
        /// Queues a terrain chunk for background geometry generation.
        /// Chunks are processed one at a time on a single background thread
        /// because LandSurfaceManager is not thread-safe.
        /// </summary>
        private void QueueChunkForGeneration(TerrainChunk chunk) {
            var chunkId = chunk.GetChunkId();
            if (_chunksInFlight.Contains(chunkId) || GPUManager.HasRenderData(chunkId)) return;

            _chunksInFlight.Add(chunkId);
            _chunkGenQueue.Enqueue(chunk);

            // Start background worker if not already running
            if (_chunkGenTask == null || _chunkGenTask.IsCompleted) {
                var terrainSystem = _terrainSystem;
                _chunkGenTask = Task.Run(() => ProcessChunkGenQueue(terrainSystem));
            }
        }

        /// <summary>
        /// Background worker that processes queued chunks sequentially.
        /// Serialized to avoid concurrent access to LandSurfaceManager.
        /// </summary>
        private void ProcessChunkGenQueue(TerrainSystem terrainSystem) {
            while (_chunkGenQueue.TryDequeue(out var chunk)) {
                var chunkId = chunk.GetChunkId();
                try {
                    var prepared = TerrainGPUResourceManager.PrepareChunkGeometry(chunk, terrainSystem);
                    _chunkUploadQueue.Enqueue(prepared);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Terrain] Background chunk gen error for ({chunk.ChunkX},{chunk.ChunkY}): {ex.Message}");
                    _chunksInFlight.Remove(chunkId);
                }
            }
        }

        /// <summary>
        /// Uploads prepared terrain chunks to the GPU (runs on GL thread).
        /// Limited to MaxChunkUploadsPerFrame per call to avoid frame spikes.
        /// </summary>
        private void ProcessChunkUploads() {
            int uploaded = 0;
            while (uploaded < MaxChunkUploadsPerFrame && _chunkUploadQueue.TryDequeue(out var prepared)) {
                try {
                    GPUManager.UploadChunkToGPU(prepared);
                    uploaded++;
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Terrain] Chunk upload error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Two-phase model warmup:
        /// Phase 1: Dispatch queued models to a background thread for CPU-side preparation
        ///          (DAT reads, texture decompression, vertex building).
        /// Phase 2: Upload prepared models to the GPU on the main thread (fast, ~1-5ms each).
        /// </summary>
        private void WarmUpRenderData() {
            // Phase 2: Upload prepared models to GPU (main thread, fast)
            UploadPreparedModels();

            // Phase 1: Kick off background preparation for queued models
            KickOffModelPreparation();
        }

        /// <summary>
        /// Uploads prepared model data to the GPU. Limited by time budget and count per frame.
        /// Each upload is fast (1-5ms) since all CPU work was done on background thread.
        /// </summary>
        private void UploadPreparedModels() {
            if (_preparedModelQueue.IsEmpty) return;

            var sw = Stopwatch.StartNew();
            int uploaded = 0;

            while (uploaded < MaxGpuUploadsPerFrame && _preparedModelQueue.TryDequeue(out var prepared)) {
                if (sw.ElapsedMilliseconds >= WarmUpTimeBudgetMs && uploaded > 0) {
                    // Re-enqueue for next frame -- put it back at the front
                    // (ConcurrentQueue doesn't support prepend, but this is rare)
                    var temp = new List<PreparedModelData> { prepared };
                    while (_preparedModelQueue.TryDequeue(out var remaining)) temp.Add(remaining);
                    foreach (var item in temp) _preparedModelQueue.Enqueue(item);
                    break;
                }

                if (_objectManager.TryGetCachedRenderData(prepared.Id) != null) {
                    _modelsPreparing.Remove(prepared.Id);
                    continue;
                }

                try {
                    var data = _objectManager.FinalizeGpuUpload(prepared);
                    uploaded++;

                    // If this was a setup, queue its GfxObj parts for warmup too
                    if (data != null && data.IsSetup && data.SetupParts != null) {
                        foreach (var (partId, _) in data.SetupParts) {
                            if (_objectManager.TryGetCachedRenderData(partId) == null &&
                                !_objectManager.IsKnownFailure(partId) &&
                                !_modelsPreparing.Contains(partId)) {
                                _renderDataWarmupQueue.Enqueue((partId, false));
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"[Statics] GPU upload error for 0x{prepared.Id:X8}: {ex.Message}");
                }
                finally {
                    _modelsPreparing.Remove(prepared.Id);
                }
            }

            if (uploaded > 0) {
                Console.WriteLine($"[Statics] GPU upload: {uploaded} models in {sw.ElapsedMilliseconds}ms " +
                    $"(prep queue: {_preparedModelQueue.Count}, warmup queue: {_renderDataWarmupQueue.Count})");
            }
        }

        /// <summary>
        /// Takes models from the warmup queue and dispatches them to a background thread
        /// for CPU-side preparation (DAT reads, texture decompression).
        /// </summary>
        private void KickOffModelPreparation() {
            if (_renderDataWarmupQueue.Count == 0) return;
            if (_modelPrepTask != null && !_modelPrepTask.IsCompleted) return;

            // Collect a batch of models to prepare
            var batch = new List<(uint Id, bool IsSetup)>();
            while (_renderDataWarmupQueue.Count > 0 && batch.Count < 16) {
                var (id, isSetup) = _renderDataWarmupQueue.Dequeue();
                if (_objectManager.TryGetCachedRenderData(id) != null || _modelsPreparing.Contains(id)) continue;
                _modelsPreparing.Add(id);
                batch.Add((id, isSetup));
            }

            if (batch.Count == 0) return;

            var objectManager = _objectManager;
            var resultQueue = _preparedModelQueue;

            _modelPrepTask = Task.Run(() => {
                var batchSw = Stopwatch.StartNew();
                int prepared = 0;

                foreach (var (id, isSetup) in batch) {
                    try {
                        var data = objectManager.PrepareModelData(id, isSetup);
                        if (data != null) {
                            resultQueue.Enqueue(data);
                            prepared++;
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[Statics] Background prep error for 0x{id:X8}: {ex.Message}");
                    }
                }

                if (prepared > 0) {
                    Console.WriteLine($"[Statics] Background prep: {prepared}/{batch.Count} models in {batchSw.ElapsedMilliseconds}ms");
                }
            });
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
            var currentCamera = CameraManager?.Current;

            // Orthographic top-down: compute the exact visible rectangle and load landblocks within it
            if (currentCamera is OrthographicTopDownCamera orthoCamera && orthoCamera.ScreenSize.X > 0) {
                return GetVisibleLandblocksOrtho(cameraPosition, orthoCamera);
            }

            // Perspective camera: use fixed proximity radius
            return GetProximateLandblocksRadius(cameraPosition, PerspectiveProximityThreshold);
        }

        /// <summary>
        /// Computes visible landblocks for orthographic top-down camera using the exact visible rectangle.
        /// Adds a one-landblock margin for smooth panning and caps total to prevent memory explosion.
        /// </summary>
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

        /// <summary>
        /// Computes a larger "keep loaded" boundary for unloading decisions.
        /// Uses a bigger margin than the load set to prevent thrashing at edges.
        /// </summary>
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

        /// <summary>
        /// Computes landblocks within a 2D distance radius of the camera (used for perspective and as fallback).
        /// </summary>
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
        /// Checks loaded landblocks that are now within scenery distance but were loaded
        /// without scenery (because they were distant at the time). Queues them for
        /// background scenery regeneration so trees/rocks appear when zooming in.
        /// </summary>
        private void RefreshSceneryForNearbyLandblocks(Vector3 cameraPosition) {
            float effectiveThreshold = GetEffectiveSceneryThreshold();
            if (effectiveThreshold < 100f) return; // Too zoomed out for scenery to matter

            var camPos2D = new Vector2(cameraPosition.X, cameraPosition.Y);

            foreach (var kvp in _sceneryObjects) {
                // Skip landblocks that already have scenery or are pending regen
                if (kvp.Value.Count > 0 || _pendingSceneryRegen.Contains(kvp.Key) || _pendingLoadLandblocks.Contains(kvp.Key))
                    continue;

                // Check if this landblock is now within scenery distance
                var lbCenter = LandblockCenter(kvp.Key);
                float dist = Vector2.Distance(camPos2D, lbCenter);
                if (dist <= effectiveThreshold) {
                    _pendingSceneryRegen.Add(kvp.Key);
                }
            }
        }

        /// <summary>
        /// Returns the effective scenery distance threshold scaled by zoom level.
        /// When zoomed out, scenery objects (trees, rocks) are sub-pixel and not worth generating.
        /// At default ortho zoom (1800), returns the full threshold. Scales down proportionally.
        /// </summary>
        private float GetEffectiveSceneryThreshold() {
            if (CameraManager?.Current is OrthographicTopDownCamera ortho && ortho.OrthographicSize > 0) {
                // At orthoSize 1800 (default): full threshold
                // At orthoSize 3600 (2x zoom out): half threshold
                // At orthoSize 9000+ : near zero — trees are invisible
                return SceneryDistanceThreshold * Math.Clamp(1800f / ortho.OrthographicSize, 0f, 1f);
            }
            return SceneryDistanceThreshold; // Perspective: always full threshold
        }

        /// <summary>
        /// Returns the effective dungeon loading distance threshold scaled by zoom level.
        /// Dungeons are underground so they're only relevant when viewing nearby.
        /// </summary>
        private float GetEffectiveDungeonThreshold() {
            if (CameraManager?.Current is OrthographicTopDownCamera ortho && ortho.OrthographicSize > 0) {
                return DungeonDistanceThreshold * Math.Clamp(1800f / ortho.OrthographicSize, 0f, 1f);
            }
            return DungeonDistanceThreshold;
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
            var focusedLB = _envCellManager.FocusedDungeonLB;
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

            // Render terrain (with brush preview).
            // Terrain is pushed furthest back in the depth priority chain:
            //   Terrain (2,2) < Building EnvCells (1,1) < Static objects (0)
            // This ensures interior floors win over terrain, while exterior
            // GfxObj models win over interior EnvCell walls/ceilings.
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(2f, 2f);
            RenderTerrain(renderableChunks, model, camera, cameraDistance, width, height, editingContext);
            _gl.Disable(EnableCap.PolygonOffsetFill);

            // Render active vertex spheres (used by road line preview and non-brush tools)
            if (editingContext.ActiveVertices.Count > 0) {
                RenderActiveSpheres(editingContext, camera, model, viewProjection);
            }

            // Render static objects (frustum-culled with accurate per-object bounds)
            var renderSw = Stopwatch.StartNew();
            var allObjects = GetAllStaticObjects();
            var visibleObjects = new List<StaticObject>();
            foreach (var obj in allObjects) {
                var localBounds = _objectManager.GetBounds(obj.Id, obj.IsSetup);
                Chorizite.Core.Lib.BoundingBox objBounds;
                if (localBounds.HasValue) {
                    // Transform local AABB to world space using all 8 corners for accuracy
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
                    // Fallback for objects whose bounds aren't loaded yet
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
                RenderStaticObjects(visibleObjects, camera, viewProjection);
            }
            long renderStaticsMs = renderSw.ElapsedMilliseconds;
            if (renderStaticsMs > 200) {
                Console.WriteLine($"[GameScene.Render] Static objects: {renderStaticsMs}ms ({visibleObjects.Count} objects)");
            }

            // Render EnvCell geometry — building interiors always render,
            // dungeon cells are gated by ShowDungeons toggle + focus filter.
            _envCellManager.ShowDungeonCells = ShowDungeons;
            _envCellManager.Render(viewProjection, camera, LightDirection, AmbientLightIntensity, SpecularPower);

            // Render selection highlight
            if (editingContext.ObjectSelection.HasSelection) {
                RenderSelectionHighlight(editingContext.ObjectSelection, camera, viewProjection);
            }

            // Render placement preview
            if (editingContext.ObjectSelection.IsPlacementMode && editingContext.ObjectSelection.PlacementPreview.HasValue) {
                RenderPlacementPreview(editingContext.ObjectSelection.PlacementPreview.Value, camera, viewProjection);
            }

            // Process thumbnail rendering at end of Render() where GL state is known-good.
            // Running during Update() produced blank FBOs because Avalonia's UI renderer
            // leaves GL state (programs, attributes, etc.) in an unknown configuration.
            _thumbnailService?.ProcessQueue();
        }

        /// <summary>
        /// Renders a highlight around the selected object using corner spheres.
        /// </summary>
        private unsafe void RenderSelectionHighlight(ObjectSelectionState selection, ICamera camera, Matrix4x4 viewProjection) {
            if (!selection.HasSelection) return;

            // EnvCell (dungeon cell) highlight — use cell bounding box corners
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

                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _sphereShader.Bind();
                _sphereShader.SetUniform("uViewProjection", viewProjection);
                _sphereShader.SetUniform("uCameraPosition", camera.Position);
                _sphereShader.SetUniform("uSphereColor", new Vector3(0.0f, 0.8f, 1.0f)); // Cyan for dungeon cells
                _sphereShader.SetUniform("uLightDirection", Vector3.Normalize(LightDirection));
                _sphereShader.SetUniform("uAmbientIntensity", 0.8f);
                _sphereShader.SetUniform("uSpecularPower", SpecularPower);
                _sphereShader.SetUniform("uGlowColor", new Vector3(0.0f, 0.8f, 1.0f));
                _sphereShader.SetUniform("uGlowIntensity", 2.0f);
                _sphereShader.SetUniform("uGlowPower", 0.3f);

                var cellArray = cellCorners.ToArray();
                _gl.BindBuffer(GLEnum.ArrayBuffer, _sphereInstanceVBO);
                unsafe {
                    fixed (Vector4* ptr = cellArray) {
                        _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(cellArray.Length * sizeof(Vector4)), ptr, GLEnum.DynamicDraw);
                    }
                }
                _gl.BindVertexArray(_sphereVAO);
                _gl.DrawElementsInstanced(GLEnum.Triangles, (uint)_sphereIndexCount, GLEnum.UnsignedInt, null, (uint)cellArray.Length);
                _gl.BindVertexArray(0);
                _gl.UseProgram(0);
                _gl.Disable(EnableCap.Blend);
                return; // EnvCell selection handled, skip static object highlight
            }

            // Collect corners for all selected objects, with per-object sphere radius
            var allInstances = new List<Vector4>();
            foreach (var entry in selection.SelectedEntries.ToList()) {
                var obj = entry.Object;
                var bounds = _objectManager.GetBounds(obj.Id, obj.IsSetup);
                if (bounds == null) continue;

                var (localMin, localMax) = bounds.Value;

                // Compute per-object sphere radius proportional to bounding box size
                var extent = (localMax - localMin) * obj.Scale;
                float maxExtent = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
                float radius = Math.Clamp(maxExtent * 0.1f, 0.15f, SphereRadius * 0.5f);

                var worldTransform = Matrix4x4.CreateScale(obj.Scale)
                    * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                    * Matrix4x4.CreateTranslation(obj.Origin);

                // Pack each corner as Vector4(x, y, z, radius)
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

            var instanceArray = allInstances.ToArray();
            _gl.BindBuffer(GLEnum.ArrayBuffer, _sphereInstanceVBO);
            fixed (Vector4* ptr = instanceArray) {
                _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(instanceArray.Length * sizeof(Vector4)), ptr, GLEnum.DynamicDraw);
            }

            _gl.BindVertexArray(_sphereVAO);
            _gl.DrawElementsInstanced(GLEnum.Triangles, (uint)_sphereIndexCount, GLEnum.UnsignedInt, null, (uint)instanceArray.Length);
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
            float height,
            TerrainEditingContext? editingContext = null) {
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

            // Brush preview uniforms
            bool brushActive = editingContext?.BrushActive ?? false;
            _terrainShader.SetUniform("uBrushActive", brushActive ? 1 : 0);
            if (brushActive) {
                _terrainShader.SetUniform("uBrushCenter", editingContext!.BrushCenter);
                // Scale radius to match PaintCommand.GetAffectedVertices: (radius * 12) + 1
                float worldRadius = (editingContext.BrushRadius * 12f) + 1f;
                _terrainShader.SetUniform("uBrushRadius", worldRadius);
            }

            // Texture preview uniforms
            int previewIdx = editingContext?.PreviewTextureAtlasIndex ?? -1;
            _terrainShader.SetUniform("uPreviewActive", previewIdx >= 0 ? 1 : 0);
            _terrainShader.SetUniform("uPreviewTexIndex", (float)previewIdx);

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

            _gl.BindBuffer(GLEnum.ArrayBuffer, _sphereInstanceVBO);
            fixed (Vector4* ptr = instances) {
                _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(count * sizeof(Vector4)), ptr, GLEnum.DynamicDraw);
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
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Back);

            _objectManager._objectShader.Bind();
            _objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            _objectManager._objectShader.SetUniform("uCameraPosition", camera.Position);
            _objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(LightDirection));
            _objectManager._objectShader.SetUniform("uAmbientIntensity", AmbientLightIntensity);
            _objectManager._objectShader.SetUniform("uSpecularPower", SpecularPower);

            // Group objects by (Id, IsSetup) using reusable collections (no LINQ allocations)
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

                // Non-blocking: only render objects whose render data is already cached
                var renderData = _objectManager.TryGetCachedRenderData(id);
                if (renderData == null) continue; // Will be loaded incrementally by WarmUpRenderData

                if (isSetup) {
                    // Setup objects - render each part
                    foreach (var (partId, partTransform) in renderData.SetupParts) {
                        var partRenderData = _objectManager.TryGetCachedRenderData(partId);
                        if (partRenderData == null) continue;

                        // Build instance transforms for this part using reusable list
                        _tempInstanceTransforms.Clear();
                        foreach (var instanceMatrix in group.Value) {
                            _tempInstanceTransforms.Add(partTransform * instanceMatrix);
                        }

                        RenderBatchedObject(partRenderData, _tempInstanceTransforms);
                    }
                }
                else {
                    // Simple GfxObj - render directly (group.Value is already the transform list)
                    RenderBatchedObject(renderData, group.Value);
                }
            }

            _gl.BindVertexArray(0);
            _gl.UseProgram(0);
            _gl.Disable(EnableCap.Blend);
        }

        private unsafe void RenderBatchedObject(StaticObjectRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (instanceTransforms.Count == 0 || renderData.Batches.Count == 0) return;

            int requiredFloats = instanceTransforms.Count * 16;

            // Ensure CPU-side upload buffer is large enough (grow-only)
            if (_instanceUploadBuffer.Length < requiredFloats) {
                int newSize = Math.Max(requiredFloats, 256); // minimum 16 instances
                // Round up to next power of 2 for stable growth
                newSize = (int)BitOperations.RoundUpToPowerOf2((uint)newSize);
                _instanceUploadBuffer = new float[newSize];
            }

            // Write matrices directly into the reusable buffer
            for (int i = 0; i < instanceTransforms.Count; i++) {
                var transform = instanceTransforms[i];
                int offset = i * 16;
                _instanceUploadBuffer[offset +  0] = transform.M11; _instanceUploadBuffer[offset +  1] = transform.M12;
                _instanceUploadBuffer[offset +  2] = transform.M13; _instanceUploadBuffer[offset +  3] = transform.M14;
                _instanceUploadBuffer[offset +  4] = transform.M21; _instanceUploadBuffer[offset +  5] = transform.M22;
                _instanceUploadBuffer[offset +  6] = transform.M23; _instanceUploadBuffer[offset +  7] = transform.M24;
                _instanceUploadBuffer[offset +  8] = transform.M31; _instanceUploadBuffer[offset +  9] = transform.M32;
                _instanceUploadBuffer[offset + 10] = transform.M33; _instanceUploadBuffer[offset + 11] = transform.M34;
                _instanceUploadBuffer[offset + 12] = transform.M41; _instanceUploadBuffer[offset + 13] = transform.M42;
                _instanceUploadBuffer[offset + 14] = transform.M43; _instanceUploadBuffer[offset + 15] = transform.M44;
            }

            // Lazily create the persistent instance VBO
            if (_instanceVBO == 0) {
                _gl.GenBuffers(1, out _instanceVBO);
            }

            _gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVBO);

            // Grow GPU buffer if needed (powers of 2), otherwise sub-update in place
            if (requiredFloats > _instanceBufferCapacity) {
                int newCapacity = Math.Max(requiredFloats, 256);
                newCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)newCapacity);
                _instanceBufferCapacity = newCapacity;
                fixed (float* ptr = _instanceUploadBuffer) {
                    _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(newCapacity * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
            }
            else {
                fixed (float* ptr = _instanceUploadBuffer) {
                    _gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(requiredFloats * sizeof(float)), ptr);
                }
            }

            _gl.BindVertexArray(renderData.VAO);

            // Rebind the persistent instance VBO and set up instance attributes for this VAO
            _gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVBO);
            for (int i = 0; i < 4; i++) {
                _gl.EnableVertexAttribArray((uint)(3 + i));
                _gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false, (uint)(16 * sizeof(float)), (void*)(i * 4 * sizeof(float)));
                _gl.VertexAttribDivisor((uint)(3 + i), 1);
            }

            // Render each batch with its texture, toggling backface culling per batch
            bool cullFaceEnabled = true; // Matches the Enable(CullFace) set in RenderStaticObjects
            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;

                try {
                    // Toggle backface culling: disable for double-sided batches, enable for single-sided
                    if (batch.IsDoubleSided && cullFaceEnabled) {
                        _gl.Disable(EnableCap.CullFace);
                        cullFaceEnabled = false;
                    }
                    else if (!batch.IsDoubleSided && !cullFaceEnabled) {
                        _gl.Enable(EnableCap.CullFace);
                        cullFaceEnabled = true;
                    }

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

            // Restore cull face state if it was disabled by the last batch
            if (!cullFaceEnabled) {
                _gl.Enable(EnableCap.CullFace);
            }

            _gl.BindVertexArray(0);
        }

        /// <summary>
        /// Saves the current camera position, rotation, and mode to settings for persistence.
        /// </summary>
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
                _gl.DeleteBuffer(_sphereVBO);
                _gl.DeleteBuffer(_sphereIBO);
                _gl.DeleteBuffer(_sphereInstanceVBO);
                _gl.DeleteVertexArray(_sphereVAO);
                if (_instanceVBO != 0) _gl.DeleteBuffer(_instanceVBO);
                _thumbnailService?.Dispose();
                _envCellManager?.Dispose();
                _objectManager?.Dispose();
                GPUManager?.Dispose();
                _disposed = true;
            }
        }
    }
}
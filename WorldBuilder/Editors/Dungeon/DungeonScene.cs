using Chorizite.Core.Lib;
using Chorizite.OpenGLSDLBackend;
using DatReaderWriter.DBObjs;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Rendering;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Simplified scene for the Dungeon Editor. Manages camera, EnvCell rendering,
    /// static object rendering, and GL state for a single dungeon landblock.
    /// Uses SceneContext (same as the landscape editor) for GPU resource management.
    /// </summary>
    public class DungeonScene : IDisposable {
        private readonly IDatReaderWriter _dats;
        private readonly WorldBuilderSettings _settings;
        private readonly TextureDiskCache _textureCache;

        public PerspectiveCamera Camera { get; }

        private OpenGLRenderer? _renderer;
        private SceneContext? _sceneContext;
        private bool _gpuInitialized;

        public EnvCellManager? EnvCellManager => _sceneContext?.EnvCellManager;

        public ThumbnailRenderService? ThumbnailService { get; private set; }

        private List<StaticObject> _dungeonStatics = new();
        private readonly Dictionary<(uint, bool), List<Matrix4x4>> _objectGroupBuffer = new();

        /// <summary>
        /// Set by the editor to highlight selected cells with wireframe boxes.
        /// Primary (first) is used for portal indicators.
        /// </summary>
        public IReadOnlyList<LoadedEnvCell>? SelectedCells { get; set; }

        /// <summary>
        /// Primary selected cell (first in SelectedCells). For backward compat with portal indicator logic.
        /// </summary>
        public LoadedEnvCell? SelectedCell => SelectedCells?.Count > 0 ? SelectedCells[0] : null;

        /// <summary>
        /// Set by the editor when a static object is selected, to render an AABB highlight.
        /// (worldMin, worldMax) of the selected object's bounding box.
        /// </summary>
        public (Vector3 Min, Vector3 Max)? SelectedObjectBounds { get; set; }

        /// <summary>
        /// Set by the editor when in object placement mode. Rendered as a ghost preview
        /// following the mouse cursor. Null when not in placement mode.
        /// </summary>
        public StaticObject? PlacementPreview { get; set; }

        /// <summary>
        /// Set by the editor when in room placement mode. Rendered as a wireframe preview
        /// showing where the room would be placed. Null when not in placement mode.
        /// </summary>
        public RoomPlacementPreviewData? RoomPlacementPreview { get; set; }

        /// <summary>
        /// Connection lines between portal centroids (from, to) in world space.
        /// Set by the editor before each render to show which cells connect to which.
        /// </summary>
        public IReadOnlyList<(Vector3 From, Vector3 To)>? ConnectionLines { get; set; }

        /// <summary>
        /// When true, connection lines are drawn. Toggle via toolbar.
        /// </summary>
        public bool ShowConnectionLines { get; set; } = true;

        private uint _lineVAO;
        private uint _lineVBO;
        private uint _objSelVAO;
        private uint _objSelVBO;
        private uint _connLineVAO;
        private uint _connLineVBO;
        private uint _portalConnVAO;
        private uint _portalConnVBO;
        private uint _portalOpenVAO;
        private uint _portalOpenVBO;

        // Reusable vertex lists to avoid per-frame allocations
        private readonly List<float> _selBoxVerts = new();
        private readonly List<float> _connLineVerts = new();
        private readonly List<float> _portalConnVerts = new();
        private readonly List<float> _portalOpenVerts = new();
        private readonly List<Vector3> _portalWorldVerts = new();
        private readonly List<Matrix4x4> _partTransformsBuffer = new();

        private ushort _loadedLandblockKey;
        private bool _hasLoadedCells;

        public DungeonScene(IDatReaderWriter dats, WorldBuilderSettings settings) {
            _dats = dats;
            _settings = settings;

            var textureCacheDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "ACME WorldBuilder", "TextureCache");
            _textureCache = new TextureDiskCache(textureCacheDir);

            Camera = new PerspectiveCamera(Vector3.Zero, settings);
        }

        /// <summary>
        /// Initialize GPU resources on the GL thread when the renderer becomes available.
        /// Uses the same SceneContext pattern as the landscape editor.
        /// </summary>
        public void InitGpu(OpenGLRenderer renderer) {
            if (_gpuInitialized && _renderer == renderer) return;

            _sceneContext?.Dispose();
            _renderer = renderer;
            _sceneContext = new SceneContext(renderer, _dats, _textureCache);
            _sceneContext.EnvCellManager.ShowDungeonCells = true;
            _sceneContext.EnvCellManager.AlwaysShowBuildingInteriors = false;
            ThumbnailService = new ThumbnailRenderService(renderer, _sceneContext.ObjectManager);
            _gpuInitialized = true;
        }

        /// <summary>
        /// Load a landblock's dungeon cells. Unloads any previously loaded landblock.
        /// Must call ProcessUploads on the GL thread afterwards.
        /// </summary>
        public bool LoadLandblock(ushort landblockKey) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null) return false;

            if (_hasLoadedCells) {
                ecm.UnloadLandblock(_loadedLandblockKey);
                _hasLoadedCells = false;
            }

            uint lbId = landblockKey;
            var envCells = new List<EnvCell>();

            uint lbiId = (lbId << 16) | 0xFFFE;
            if (!_dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) {
                return false;
            }

            for (uint i = 0; i < lbi.NumCells; i++) {
                uint cellId = (lbId << 16) | (0x0100 + i);
                if (_dats.TryGet<EnvCell>(cellId, out var cell)) {
                    envCells.Add(cell);
                }
            }

            if (envCells.Count == 0) return false;

            var batch = ecm.PrepareLandblockEnvCells(landblockKey, lbId, envCells, isDungeonOnly: true);
            if (batch != null) {
                ecm.QueueForUpload(batch);
                IntegrateStatics(batch);
            }

            _loadedLandblockKey = landblockKey;
            ecm.FocusedDungeonLB = landblockKey;
            _hasLoadedCells = true;
            return true;
        }

        /// <summary>
        /// Reload rendering from a DungeonDocument's cell list.
        /// Unloads existing cells and re-prepares from the document.
        /// </summary>
        public void RefreshFromDocument(DungeonDocument document) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null) return;

            if (_hasLoadedCells) {
                ecm.UnloadLandblock(_loadedLandblockKey);
                _hasLoadedCells = false;
            }

            var envCells = document.ToEnvCells();
            if (envCells.Count == 0) return;

            _loadedLandblockKey = document.LandblockKey;
            uint lbId = document.LandblockKey;

            var batch = ecm.PrepareLandblockEnvCells(_loadedLandblockKey, lbId, envCells, isDungeonOnly: true);
            if (batch != null) {
                ecm.QueueForUpload(batch);
                IntegrateStatics(batch);
            }

            ecm.FocusedDungeonLB = _loadedLandblockKey;
            _hasLoadedCells = true;
        }

        private void IntegrateStatics(PreparedEnvCellBatch batch) {
            _dungeonStatics.Clear();
            _dungeonStatics.AddRange(batch.DungeonStaticObjects);

            if (_sceneContext == null) return;
            foreach (var obj in _dungeonStatics) {
                if (_sceneContext.ObjectManager.TryGetCachedRenderData(obj.Id) == null &&
                    !_sceneContext.ObjectManager.IsKnownFailure(obj.Id)) {
                    _sceneContext.ModelWarmupQueue.Enqueue((obj.Id, obj.IsSetup));
                }
            }
        }

        /// <summary>
        /// Navigate the camera to a specific cell within the loaded dungeon.
        /// </summary>
        public void FocusCameraOnCell(ushort landblockKey, ushort cellId) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null || !_hasLoadedCells) return;

            var cells = ecm.GetLoadedCellsForLandblock(landblockKey);
            if (cells == null || cells.Count == 0) {
                FocusCamera();
                return;
            }

            uint fullCellId = ((uint)landblockKey << 16) | cellId;
            var target = cells.FirstOrDefault(c => c.CellId == fullCellId);
            if (target == null) {
                FocusCamera();
                return;
            }

            Camera.SetPosition(target.WorldPosition + new Vector3(0, -15f, 8f));
            Camera.LookAt(target.WorldPosition);
        }

        /// <summary>
        /// Navigate the camera to the center of the loaded dungeon cells.
        /// </summary>
        public void FocusCamera() {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null || !_hasLoadedCells) return;

            var cells = ecm.GetLoadedCellsForLandblock(_loadedLandblockKey);
            if (cells == null || cells.Count == 0) return;

            var center = Vector3.Zero;
            foreach (var cell in cells) {
                center += cell.WorldPosition;
            }
            center /= cells.Count;

            Camera.SetPosition(center + new Vector3(0, -20f, 10f));
            Camera.LookAt(center);
        }

        /// <summary>
        /// Process pending GPU uploads and render the dungeon cells.
        /// Must be called on the GL thread.
        /// </summary>
        private int _diagFrame;

        public void Render(float aspectRatio) {
            var ecm = _sceneContext?.EnvCellManager;
            if (_renderer == null || ecm == null) return;

            var gl = _renderer.GraphicsDevice.GL;

            // Drain any stale GL errors from previous frame (thumbnail service FBO, etc.)
            while (gl.GetError() != GLEnum.NoError) { }

            ecm.ProcessUploads(maxPerFrame: 8);

            // Explicitly set viewport to match camera screen size (FBO dimensions)
            int vpW = (int)Camera.ScreenSize.X;
            int vpH = (int)Camera.ScreenSize.Y;
            if (vpW > 0 && vpH > 0) {
                gl.Viewport(0, 0, (uint)vpW, (uint)vpH);
            }

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.ClearColor(0.05f, 0.03f, 0.08f, 1.0f);
            gl.ClearDepth(1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            var view = Camera.GetViewMatrix();
            var projection = Camera.GetProjectionMatrix();
            var viewProjection = view * projection;

            if (++_diagFrame % 300 == 1 && _hasLoadedCells) {
                Console.WriteLine($"[DungeonScene] Cells: {ecm.LoadedCellCount}, " +
                    $"Statics: {_dungeonStatics.Count}, " +
                    $"ModelsQueued: {_sceneContext?.ModelWarmupQueue.Count ?? 0}, " +
                    $"ModelsPreparing: {_sceneContext?.ModelsPreparing.Count ?? 0}, " +
                    $"ModelsUploading: {_sceneContext?.ModelUploadQueue.Count ?? 0}");
            }

            var lightDir = Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f));
            float ambient = 0.4f;
            float specular = 16f;

            ecm.Render(viewProjection, Camera, lightDir, ambient, specular);

            // Process model warmup/uploads for static objects
            ProcessModelUploads();

            // Render static objects (torches, furniture, etc.)
            if (_dungeonStatics.Count > 0) {
                RenderStaticObjects(gl, viewProjection);
            }

            // Render portal indicators on primary selected cell
            if (SelectedCell != null) {
                RenderPortalIndicators(gl, viewProjection);
            }

            // Render selection highlight on all selected cells
            if (SelectedCells != null && SelectedCells.Count > 0) {
                RenderSelectionBoxes(gl, viewProjection);
            }

            // Render selected object highlight
            if (SelectedObjectBounds.HasValue) {
                RenderObjectSelectionBox(gl, viewProjection, SelectedObjectBounds.Value.Min, SelectedObjectBounds.Value.Max);
            }

            // Render placement preview (ghost object following mouse)
            if (PlacementPreview.HasValue) {
                RenderPlacementPreview(gl, viewProjection, PlacementPreview.Value);
            }

            // Render room placement preview (wireframe ghost)
            if (RoomPlacementPreview.HasValue) {
                RenderRoomPlacementPreview(gl, viewProjection, RoomPlacementPreview.Value);
            }

            // Render connection lines between cells (shows what connects to what)
            if (ShowConnectionLines && ConnectionLines != null && ConnectionLines.Count > 0) {
                RenderConnectionLines(gl, viewProjection);
            }

            ThumbnailService?.ProcessQueue(_renderer);
        }

        private void ProcessModelUploads() {
            if (_sceneContext == null) return;
            var objectManager = _sceneContext.ObjectManager;

            int uploaded = 0;
            while (uploaded < 16 && _sceneContext.ModelUploadQueue.TryDequeue(out var preparedModel)) {
                var renderData = objectManager.FinalizeGpuUpload(preparedModel);
                uploaded++;

                // Setup objects are containers that reference GfxObj parts.
                // The Setup itself has no geometry -- each part must be separately
                // prepared and uploaded. Queue any parts that aren't cached yet.
                if (renderData?.IsSetup == true && renderData.SetupParts != null) {
                    foreach (var (partId, _) in renderData.SetupParts) {
                        if (objectManager.TryGetCachedRenderData(partId) == null &&
                            !objectManager.IsKnownFailure(partId) &&
                            !_sceneContext.ModelsPreparing.Contains(partId)) {
                            _sceneContext.ModelWarmupQueue.Enqueue((partId, false));
                        }
                    }
                }
            }

            int warmupCount = 0;
            while (warmupCount < 16 && _sceneContext.ModelWarmupQueue.Count > 0) {
                var (id, isSetup) = _sceneContext.ModelWarmupQueue.Dequeue();
                if (objectManager.TryGetCachedRenderData(id) != null) continue;
                if (_sceneContext.ModelsPreparing.Contains(id)) continue;
                _sceneContext.ModelsPreparing.Add(id);

                var localId = id;
                var localIsSetup = isSetup;
                var localCtx = _sceneContext;
                System.Threading.Tasks.Task.Run(() => {
                    var prepared = objectManager.PrepareModelData(localId, localIsSetup);
                    if (prepared != null) {
                        localCtx.ModelUploadQueue.Enqueue(prepared);
                    }
                    localCtx.ModelsPreparing.Remove(localId);
                });
                warmupCount++;
            }
        }

        private unsafe void RenderStaticObjects(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || _dungeonStatics.Count == 0) return;
            var objectManager = _sceneContext.ObjectManager;

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            objectManager._objectShader.Bind();
            objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            objectManager._objectShader.SetUniform("uCameraPosition", Camera.Position);
            objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            objectManager._objectShader.SetUniform("uAmbientIntensity", 0.4f);
            objectManager._objectShader.SetUniform("uSpecularPower", 16f);

            foreach (var list in _objectGroupBuffer.Values) list.Clear();

            foreach (var obj in _dungeonStatics) {
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

                        _partTransformsBuffer.Clear();
                        foreach (var instanceMatrix in group.Value) {
                            _partTransformsBuffer.Add(partTransform * instanceMatrix);
                        }

                        RenderBatchedObject(gl, partRenderData, _partTransformsBuffer);
                    }
                }
                else {
                    RenderBatchedObject(gl, renderData, group.Value);
                }
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(EnableCap.Blend);
        }

        private unsafe void RenderObjectSelectionBox(GL gl, Matrix4x4 viewProjection, Vector3 worldMin, Vector3 worldMax) {
            if (_sceneContext == null) return;
            if (worldMin.X >= worldMax.X || worldMin.Y >= worldMax.Y || worldMin.Z >= worldMax.Z) return;

            // Ensure clean GL state before using SphereShader
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            while (gl.GetError() != GLEnum.NoError) { }

            Vector3[] c = new Vector3[8];
            c[0] = new Vector3(worldMin.X, worldMin.Y, worldMin.Z);
            c[1] = new Vector3(worldMax.X, worldMin.Y, worldMin.Z);
            c[2] = new Vector3(worldMax.X, worldMax.Y, worldMin.Z);
            c[3] = new Vector3(worldMin.X, worldMax.Y, worldMin.Z);
            c[4] = new Vector3(worldMin.X, worldMin.Y, worldMax.Z);
            c[5] = new Vector3(worldMax.X, worldMin.Y, worldMax.Z);
            c[6] = new Vector3(worldMax.X, worldMax.Y, worldMax.Z);
            c[7] = new Vector3(worldMin.X, worldMax.Y, worldMax.Z);

            int[][] edges = { new[]{0,1}, new[]{1,2}, new[]{2,3}, new[]{3,0},
                              new[]{4,5}, new[]{5,6}, new[]{6,7}, new[]{7,4},
                              new[]{0,4}, new[]{1,5}, new[]{2,6}, new[]{3,7} };

            const float lineWidth = 0.08f;
            var camPos = Camera.Position;
            var verts = new List<float>();

            foreach (var e in edges) {
                var a = c[e[0]];
                var b = c[e[1]];
                var edgeDir = Vector3.Normalize(b - a);
                var midpoint = (a + b) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;
                var normal = toCamera;
                var a0 = a - sideDir; var a1 = a + sideDir;
                var b0 = b - sideDir; var b1 = b + sideDir;

                void V(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            int vertCount = verts.Count / 6;
            var data = CollectionsMarshal.AsSpan(verts);

            if (_objSelVAO == 0) {
                gl.GenVertexArrays(1, out _objSelVAO);
                gl.GenBuffers(1, out _objSelVBO);
            }

            gl.BindVertexArray(_objSelVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _objSelVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(1.0f, 0.7f, 0.2f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderPlacementPreview(GL gl, Matrix4x4 viewProjection, StaticObject previewObj) {
            if (_sceneContext == null) return;
            var objectManager = _sceneContext.ObjectManager;

            var renderData = objectManager.TryGetCachedRenderData(previewObj.Id);
            if (renderData == null) return;

            // Ensure setup parts are also cached
            if (renderData.IsSetup && renderData.SetupParts != null) {
                foreach (var (partId, _) in renderData.SetupParts) {
                    objectManager.GetRenderData(partId, false);
                }
            }

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);

            objectManager._objectShader.Bind();
            objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            objectManager._objectShader.SetUniform("uCameraPosition", Camera.Position);
            objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            objectManager._objectShader.SetUniform("uAmbientIntensity", 0.7f);
            objectManager._objectShader.SetUniform("uSpecularPower", 8f);

            var worldMatrix = Matrix4x4.CreateTranslation(previewObj.Origin)
                * Matrix4x4.CreateFromQuaternion(previewObj.Orientation)
                * Matrix4x4.CreateScale(previewObj.Scale);

            if (renderData.IsSetup && renderData.SetupParts != null) {
                foreach (var (partId, partTransform) in renderData.SetupParts) {
                    var partRenderData = objectManager.TryGetCachedRenderData(partId);
                    if (partRenderData == null) continue;
                    RenderBatchedObject(gl, partRenderData, new List<Matrix4x4> { partTransform * worldMatrix });
                }
            }
            else {
                RenderBatchedObject(gl, renderData, new List<Matrix4x4> { worldMatrix });
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Enable(EnableCap.CullFace);
        }

        private unsafe void RenderRoomPlacementPreview(GL gl, Matrix4x4 viewProjection, RoomPlacementPreviewData preview) {
            if (_sceneContext == null) return;
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(preview.EnvFileId, out var env)) return;
            if (!env.Cells.TryGetValue(preview.CellStructIndex, out var cellStruct)) return;
            if (cellStruct.VertexArray?.Vertices == null || cellStruct.VertexArray.Vertices.Count == 0) return;

            var boundsMin = new Vector3(float.MaxValue);
            var boundsMax = new Vector3(float.MinValue);
            foreach (var vtx in cellStruct.VertexArray.Vertices.Values) {
                boundsMin = Vector3.Min(boundsMin, vtx.Origin);
                boundsMax = Vector3.Max(boundsMax, vtx.Origin);
            }
            if (boundsMin.X >= boundsMax.X) return;

            var transform = Matrix4x4.CreateFromQuaternion(preview.Orientation) * Matrix4x4.CreateTranslation(preview.Origin);
            Vector3[] c = new Vector3[8];
            c[0] = Vector3.Transform(new Vector3(boundsMin.X, boundsMin.Y, boundsMin.Z), transform);
            c[1] = Vector3.Transform(new Vector3(boundsMax.X, boundsMin.Y, boundsMin.Z), transform);
            c[2] = Vector3.Transform(new Vector3(boundsMax.X, boundsMax.Y, boundsMin.Z), transform);
            c[3] = Vector3.Transform(new Vector3(boundsMin.X, boundsMax.Y, boundsMin.Z), transform);
            c[4] = Vector3.Transform(new Vector3(boundsMin.X, boundsMin.Y, boundsMax.Z), transform);
            c[5] = Vector3.Transform(new Vector3(boundsMax.X, boundsMin.Y, boundsMax.Z), transform);
            c[6] = Vector3.Transform(new Vector3(boundsMax.X, boundsMax.Y, boundsMax.Z), transform);
            c[7] = Vector3.Transform(new Vector3(boundsMin.X, boundsMax.Y, boundsMax.Z), transform);

            int[][] edges = { new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 3 }, new[] { 3, 0 },
                             new[] { 4, 5 }, new[] { 5, 6 }, new[] { 6, 7 }, new[] { 7, 4 },
                             new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 } };

            const float lineWidth = 0.15f;
            var camPos = Camera.Position;
            var verts = new List<float>();
            foreach (var e in edges) {
                var a = c[e[0]];
                var b = c[e[1]];
                var edgeDir = Vector3.Normalize(b - a);
                var midpoint = (a + b) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;
                var normal = toCamera;
                var a0 = a - sideDir; var a1 = a + sideDir;
                var b0 = b - sideDir; var b1 = b + sideDir;
                void V(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            int vertCount = verts.Count / 6;
            var data = CollectionsMarshal.AsSpan(verts);

            if (_lineVAO == 0) {
                gl.GenVertexArrays(1, out _lineVAO);
                gl.GenBuffers(1, out _lineVBO);
            }

            gl.BindVertexArray(_lineVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _lineVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(0.2f, 0.9f, 0.4f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        /// <summary>
        /// Queue a model for GPU warmup so it's ready to render as a preview.
        /// </summary>
        public void WarmupModel(uint id, bool isSetup) {
            if (_sceneContext == null) return;
            if (_sceneContext.ObjectManager.TryGetCachedRenderData(id) != null) return;
            if (_sceneContext.ObjectManager.IsKnownFailure(id)) return;
            _sceneContext.ModelWarmupQueue.Enqueue((id, isSetup));
        }

        public (Vector3 Min, Vector3 Max)? GetObjectBounds(uint id, bool isSetup) {
            return _sceneContext?.ObjectManager?.GetBounds(id, isSetup);
        }

        private unsafe void RenderPortalIndicators(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || SelectedCell == null) return;

            uint envFileId = SelectedCell.EnvironmentId;
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) return;

            var gpuKey = SelectedCell.GpuKey;
            ushort cellStructIdx = (ushort)gpuKey.CellStructure;
            if (!env.Cells.TryGetValue(cellStructIdx, out var cellStruct)) return;

            var portalIds = PortalSnapper.GetPortalPolygonIds(cellStruct);
            if (portalIds.Count == 0) return;

            var connectedPolys = new HashSet<ushort>();
            foreach (var p in SelectedCell.Portals) {
                connectedPolys.Add(p.PolygonId);
            }

            var transform = SelectedCell.WorldTransform;
            var camPos = Camera.Position;
            const float portalLineWidth = 0.12f;

            void AddPortalEdges(List<float> verts, ushort polyId) {
                if (!cellStruct.Polygons.TryGetValue(polyId, out var poly)) return;
                if (poly.VertexIds.Count < 3) return;

                _portalWorldVerts.Clear();
                foreach (var vid in poly.VertexIds) {
                    if (cellStruct.VertexArray.Vertices.TryGetValue((ushort)vid, out var vtx)) {
                        _portalWorldVerts.Add(Vector3.Transform(vtx.Origin, transform));
                    }
                }
                if (_portalWorldVerts.Count < 3) return;

                for (int i = 0; i < _portalWorldVerts.Count; i++) {
                    int next = (i + 1) % _portalWorldVerts.Count;
                    var a = _portalWorldVerts[i];
                    var b = _portalWorldVerts[next];
                    var edgeDir = Vector3.Normalize(b - a);
                    var mid = (a + b) * 0.5f;
                    var toCamera = Vector3.Normalize(camPos - mid);
                    var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * portalLineWidth;
                    var n = toCamera;
                    void PV(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z); }
                    PV(a - sideDir); PV(b - sideDir); PV(b + sideDir);
                    PV(a - sideDir); PV(b + sideDir); PV(a + sideDir);
                }
            }

            _portalConnVerts.Clear();
            _portalOpenVerts.Clear();
            foreach (var polyId in portalIds) {
                if (connectedPolys.Contains(polyId))
                    AddPortalEdges(_portalConnVerts, polyId);
                else
                    AddPortalEdges(_portalOpenVerts, polyId);
            }

            void DrawPortalVerts(List<float> verts, ref uint cachedVao, ref uint cachedVbo, Vector3 color) {
                if (verts.Count == 0) return;
                int vertCount = verts.Count / 6;
                var data = CollectionsMarshal.AsSpan(verts);

                if (cachedVao == 0) {
                    gl.GenVertexArrays(1, out cachedVao);
                    gl.GenBuffers(1, out cachedVbo);
                }
                gl.BindVertexArray(cachedVao);
                gl.BindBuffer(GLEnum.ArrayBuffer, cachedVbo);
                fixed (float* ptr = data) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
                for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
                gl.DisableVertexAttribArray(2);
                gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

                var shader = _sceneContext.SphereShader;
                shader.Bind();
                shader.SetUniform("uViewProjection", viewProjection);
                shader.SetUniform("uCameraPosition", Camera.Position);
                shader.SetUniform("uSphereColor", color);
                shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
                shader.SetUniform("uAmbientIntensity", 1.0f);
                shader.SetUniform("uSpecularPower", 0f);
                shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
                shader.SetUniform("uGlowIntensity", 0f);
                shader.SetUniform("uGlowPower", 1.0f);

                gl.Disable(EnableCap.DepthTest);
                gl.Disable(EnableCap.CullFace);
                gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
                gl.Enable(EnableCap.DepthTest);
                gl.Enable(EnableCap.CullFace);

                gl.BindVertexArray(0);
                gl.UseProgram(0);
            }

            if (_portalConnVerts.Count == 0 && _portalOpenVerts.Count == 0) return;

            DrawPortalVerts(_portalConnVerts, ref _portalConnVAO, ref _portalConnVBO, new Vector3(0.3f, 0.5f, 1.0f));
            DrawPortalVerts(_portalOpenVerts, ref _portalOpenVAO, ref _portalOpenVBO, new Vector3(0.2f, 0.9f, 0.3f));
        }

        private unsafe void RenderConnectionLines(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || ConnectionLines == null || ConnectionLines.Count == 0) return;

            const float lineWidth = 0.08f;
            var camPos = Camera.Position;
            _connLineVerts.Clear();

            foreach (var (from, to) in ConnectionLines) {
                var edgeDir = Vector3.Normalize(to - from);
                var midpoint = (from + to) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;
                var normal = toCamera;

                var a0 = from - sideDir; var a1 = from + sideDir;
                var b0 = to - sideDir; var b1 = to + sideDir;

                void V(Vector3 p) { _connLineVerts.Add(p.X); _connLineVerts.Add(p.Y); _connLineVerts.Add(p.Z); _connLineVerts.Add(normal.X); _connLineVerts.Add(normal.Y); _connLineVerts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            if (_connLineVerts.Count == 0) return;
            int vertCount = _connLineVerts.Count / 6;
            var data = CollectionsMarshal.AsSpan(_connLineVerts);

            if (_connLineVAO == 0) {
                gl.GenVertexArrays(1, out _connLineVAO);
                gl.GenBuffers(1, out _connLineVBO);
            }

            gl.BindVertexArray(_connLineVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _connLineVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(0.4f, 0.6f, 1.0f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderSelectionBoxes(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || SelectedCells == null) return;

            foreach (var cell in SelectedCells) {
                RenderSelectionBox(gl, viewProjection, cell);
            }
        }

        private unsafe void RenderSelectionBox(GL gl, Matrix4x4 viewProjection, LoadedEnvCell cell) {
            if (_sceneContext == null) return;
            var min = cell.LocalBoundsMin;
            var max = cell.LocalBoundsMax;
            if (min.X >= max.X) return;

            var transform = cell.WorldTransform;

            Vector3[] c = new Vector3[8];
            c[0] = Vector3.Transform(new Vector3(min.X, min.Y, min.Z), transform);
            c[1] = Vector3.Transform(new Vector3(max.X, min.Y, min.Z), transform);
            c[2] = Vector3.Transform(new Vector3(max.X, max.Y, min.Z), transform);
            c[3] = Vector3.Transform(new Vector3(min.X, max.Y, min.Z), transform);
            c[4] = Vector3.Transform(new Vector3(min.X, min.Y, max.Z), transform);
            c[5] = Vector3.Transform(new Vector3(max.X, min.Y, max.Z), transform);
            c[6] = Vector3.Transform(new Vector3(max.X, max.Y, max.Z), transform);
            c[7] = Vector3.Transform(new Vector3(min.X, max.Y, max.Z), transform);

            // Build billboard quads for each edge (2 tris per edge, camera-facing thickness)
            int[][] edges = { new[]{0,1}, new[]{1,2}, new[]{2,3}, new[]{3,0},
                              new[]{4,5}, new[]{5,6}, new[]{6,7}, new[]{7,4},
                              new[]{0,4}, new[]{1,5}, new[]{2,6}, new[]{3,7} };

            const float lineWidth = 0.15f;
            var camPos = Camera.Position;
            _selBoxVerts.Clear();

            foreach (var e in edges) {
                var a = c[e[0]];
                var b = c[e[1]];
                var edgeDir = Vector3.Normalize(b - a);
                var midpoint = (a + b) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;

                var normal = toCamera;
                var a0 = a - sideDir; var a1 = a + sideDir;
                var b0 = b - sideDir; var b1 = b + sideDir;

                void V(Vector3 p) { _selBoxVerts.Add(p.X); _selBoxVerts.Add(p.Y); _selBoxVerts.Add(p.Z); _selBoxVerts.Add(normal.X); _selBoxVerts.Add(normal.Y); _selBoxVerts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            int vertCount = _selBoxVerts.Count / 6;
            var data = CollectionsMarshal.AsSpan(_selBoxVerts);

            if (_lineVAO == 0) {
                gl.GenVertexArrays(1, out _lineVAO);
                gl.GenBuffers(1, out _lineVBO);
            }

            gl.BindVertexArray(_lineVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _lineVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(0.7f, 0.3f, 1.0f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);

            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderBatchedObject(GL gl, StaticObjectRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (_sceneContext == null || instanceTransforms.Count == 0 || renderData.Batches.Count == 0) return;

            int requiredFloats = instanceTransforms.Count * 16;

            if (_sceneContext.InstanceUploadBuffer.Length < requiredFloats) {
                int newSize = Math.Max(requiredFloats, 256);
                newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newSize);
                _sceneContext.InstanceUploadBuffer = new float[newSize];
            }

            for (int i = 0; i < instanceTransforms.Count; i++) {
                var t = instanceTransforms[i];
                int o = i * 16;
                _sceneContext.InstanceUploadBuffer[o+ 0]=t.M11; _sceneContext.InstanceUploadBuffer[o+ 1]=t.M12;
                _sceneContext.InstanceUploadBuffer[o+ 2]=t.M13; _sceneContext.InstanceUploadBuffer[o+ 3]=t.M14;
                _sceneContext.InstanceUploadBuffer[o+ 4]=t.M21; _sceneContext.InstanceUploadBuffer[o+ 5]=t.M22;
                _sceneContext.InstanceUploadBuffer[o+ 6]=t.M23; _sceneContext.InstanceUploadBuffer[o+ 7]=t.M24;
                _sceneContext.InstanceUploadBuffer[o+ 8]=t.M31; _sceneContext.InstanceUploadBuffer[o+ 9]=t.M32;
                _sceneContext.InstanceUploadBuffer[o+10]=t.M33; _sceneContext.InstanceUploadBuffer[o+11]=t.M34;
                _sceneContext.InstanceUploadBuffer[o+12]=t.M41; _sceneContext.InstanceUploadBuffer[o+13]=t.M42;
                _sceneContext.InstanceUploadBuffer[o+14]=t.M43; _sceneContext.InstanceUploadBuffer[o+15]=t.M44;
            }

            if (_sceneContext.InstanceVBO == 0) {
                gl.GenBuffers(1, out uint vbo);
                _sceneContext.InstanceVBO = vbo;
            }

            gl.BindBuffer(GLEnum.ArrayBuffer, _sceneContext.InstanceVBO);

            if (requiredFloats > _sceneContext.InstanceBufferCapacity) {
                int newCapacity = Math.Max(requiredFloats, 256);
                newCapacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newCapacity);
                _sceneContext.InstanceBufferCapacity = newCapacity;
                fixed (float* ptr = _sceneContext.InstanceUploadBuffer) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(newCapacity * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
            }
            else {
                fixed (float* ptr = _sceneContext.InstanceUploadBuffer) {
                    gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(requiredFloats * sizeof(float)), ptr);
                }
            }

            gl.BindVertexArray(renderData.VAO);

            gl.BindBuffer(GLEnum.ArrayBuffer, _sceneContext.InstanceVBO);
            for (int i = 0; i < 4; i++) {
                gl.EnableVertexAttribArray((uint)(3 + i));
                gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false, (uint)(16 * sizeof(float)), (void*)(i * 4 * sizeof(float)));
                gl.VertexAttribDivisor((uint)(3 + i), 1);
            }

            bool cullFaceEnabled = true;
            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;

                if (batch.IsDoubleSided && cullFaceEnabled) {
                    gl.Disable(EnableCap.CullFace);
                    cullFaceEnabled = false;
                }
                else if (!batch.IsDoubleSided && !cullFaceEnabled) {
                    gl.Enable(EnableCap.CullFace);
                    cullFaceEnabled = true;
                }

                batch.TextureArray.Bind(0);
                _sceneContext.ObjectManager._objectShader.SetUniform("uTextureArray", 0);
                _sceneContext.ObjectManager._objectShader.SetUniform("uTextureIndex", (float)batch.TextureIndex);
                gl.DisableVertexAttribArray(7);
                gl.VertexAttrib1((uint)7, (float)batch.TextureIndex);

                gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, (uint)instanceTransforms.Count);
            }

            if (!cullFaceEnabled) gl.Enable(EnableCap.CullFace);
        }

        public void Dispose() {
            ThumbnailService?.Dispose();
            if (_renderer != null) {
                var gl = _renderer.GraphicsDevice.GL;
                if (_objSelVBO != 0) gl.DeleteBuffer(_objSelVBO);
                if (_objSelVAO != 0) gl.DeleteVertexArray(_objSelVAO);
                if (_lineVBO != 0) gl.DeleteBuffer(_lineVBO);
                if (_lineVAO != 0) gl.DeleteVertexArray(_lineVAO);
                if (_connLineVBO != 0) gl.DeleteBuffer(_connLineVBO);
                if (_connLineVAO != 0) gl.DeleteVertexArray(_connLineVAO);
                if (_portalConnVBO != 0) gl.DeleteBuffer(_portalConnVBO);
                if (_portalConnVAO != 0) gl.DeleteVertexArray(_portalConnVAO);
                if (_portalOpenVBO != 0) gl.DeleteBuffer(_portalOpenVBO);
                if (_portalOpenVAO != 0) gl.DeleteVertexArray(_portalOpenVAO);
            }
            _sceneContext?.Dispose();
        }
    }

    /// <summary>
    /// Data for rendering a room placement preview (wireframe ghost).
    /// </summary>
    public struct RoomPlacementPreviewData {
        public Vector3 Origin;
        public Quaternion Orientation;
        public uint EnvFileId;
        public ushort CellStructIndex;
    }
}

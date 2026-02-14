using Chorizite.Core.Render;
using Chorizite.Core.Render.Enums;
using Chorizite.OpenGLSDLBackend;
using Chorizite.OpenGLSDLBackend.Lib;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Silk.NET.OpenGL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Chorizite.Core.Lib;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace WorldBuilder.Editors.Landscape {

    /// <summary>
    /// Manages loading, caching, and rendering of dungeon/interior EnvCell room geometry.
    /// Each EnvCell references an Environment object (0x0D000000 range) containing CellStruct
    /// geometry (vertices + polygons), and carries its own surface texture list and world transform.
    /// </summary>
    public class EnvCellManager : IDisposable {
        private const int MaxLoadedDungeonCells = 500; // Hard cap on total loaded dungeon cells to prevent memory/GPU explosion

        private readonly OpenGLRenderer _renderer;
        private readonly IDatReaderWriter _dats;
        private readonly IShader _shader;
        private readonly TextureDiskCache? _textureCache;

        // Cache Environment objects from portal.dat (many EnvCells share the same Environment)
        private readonly Dictionary<uint, DatReaderWriter.DBObjs.Environment> _environmentCache = new();

        // GPU resource cache keyed by (EnvironmentId, CellStructure, surface signature hash).
        // Different EnvCells can share the same geometry but have different surfaces,
        // so we include a surface hash to distinguish them.
        private readonly Dictionary<EnvCellGpuKey, EnvCellRenderData> _gpuCache = new();

        // Per-landblock list of loaded dungeon cells
        private readonly Dictionary<ushort, List<LoadedEnvCell>> _loadedCells = new();

        // Track which landblocks are dungeon-only (vs building interiors)
        private readonly HashSet<ushort> _dungeonOnlyLandblocks = new();

        /// <summary>
        /// When set, only render dungeon cells from this landblock. Null = show all.
        /// </summary>
        public ushort? FocusedDungeonLB { get; set; }

        /// <summary>
        /// Controls whether dungeon-only cells are rendered. Building interior cells
        /// always render regardless of this flag.
        /// </summary>
        public bool ShowDungeonCells { get; set; } = true;

        /// <summary>
        /// Returns all landblock keys that have loaded dungeon cells, sorted.
        /// </summary>
        public List<ushort> GetLoadedDungeonLandblocks() => _loadedCells.Keys.OrderBy(k => k).ToList();

        /// <summary>
        /// Cycles to the next loaded dungeon landblock. Wraps around.
        /// </summary>
        public void FocusNextDungeon() {
            var lbs = GetLoadedDungeonLandblocks();
            if (lbs.Count == 0) { FocusedDungeonLB = null; return; }
            if (!FocusedDungeonLB.HasValue) { FocusedDungeonLB = lbs[0]; return; }
            int idx = lbs.IndexOf(FocusedDungeonLB.Value);
            FocusedDungeonLB = lbs[(idx + 1) % lbs.Count];
        }

        /// <summary>
        /// Cycles to the previous loaded dungeon landblock. Wraps around.
        /// </summary>
        public void FocusPrevDungeon() {
            var lbs = GetLoadedDungeonLandblocks();
            if (lbs.Count == 0) { FocusedDungeonLB = null; return; }
            if (!FocusedDungeonLB.HasValue) { FocusedDungeonLB = lbs[^1]; return; }
            int idx = lbs.IndexOf(FocusedDungeonLB.Value);
            FocusedDungeonLB = lbs[(idx - 1 + lbs.Count) % lbs.Count];
        }

        // Track failed IDs to avoid repeated DAT reads
        private readonly HashSet<uint> _failedEnvironments = new();

        // Queue for two-phase loading: CPU-prepared data waiting for GPU upload
        private readonly ConcurrentQueue<PreparedEnvCellBatch> _uploadQueue = new();

        // Persistent instance buffer for instanced rendering
        private uint _instanceVBO;
        private int _instanceBufferCapacity;
        private float[] _instanceUploadBuffer = Array.Empty<float>();

        // Reusable cell grouping buffers (avoids per-frame dictionary allocation in Render)
        private readonly Dictionary<EnvCellGpuKey, List<Matrix4x4>> _cellGroupBuffer = new();
        private readonly Dictionary<EnvCellGpuKey, List<Matrix4x4>> _buildingCellGroupBuffer = new();

        public EnvCellManager(OpenGLRenderer renderer, IDatReaderWriter dats, IShader objectShader, TextureDiskCache? textureCache = null) {
            _renderer = renderer;
            _dats = dats;
            _shader = objectShader;
            _textureCache = textureCache;
        }

        /// <summary>
        /// Returns the number of landblocks with loaded dungeon cells.
        /// </summary>
        public int LoadedLandblockCount => _loadedCells.Count;

        /// <summary>
        /// Returns the total number of loaded dungeon cells across all landblocks.
        /// </summary>
        public int LoadedCellCount => _loadedCells.Values.Sum(list => list.Count);

        #region CPU Preparation (background thread safe)

        /// <summary>
        /// CPU-side preparation of EnvCell data for a landblock. Reads DAT files, decompresses
        /// textures, builds vertex/index arrays. Safe to call from a background thread.
        /// Returns a PreparedEnvCellBatch that must be finalized on the GL thread via FinalizeGpuUpload.
        /// </summary>
        /// <summary>
        /// ACViewer-style depth hack: push underground dungeon geometry well below the
        /// terrain/water surface to eliminate Z-fighting. Dungeon-only landblocks (cells
        /// at negative Z with no surface buildings) get bumped down by this amount.
        /// Building interiors get a small +0.05 bump instead (applied in PrepareEnvCell).
        /// </summary>
        private const float DungeonDepthOffset = -50f;

        public PreparedEnvCellBatch? PrepareLandblockEnvCells(ushort lbKey, uint lbId, List<EnvCell> envCells, bool isDungeonOnly = false) {
            if (envCells.Count == 0) return null;

            var batch = new PreparedEnvCellBatch {
                LandblockKey = lbKey,
                IsDungeonOnly = isDungeonOnly,
                Cells = new List<PreparedEnvCell>()
            };

            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);

            // Dungeon-only landblocks (no buildings on surface) get pushed well below the
            // terrain/water to eliminate Z-fighting. Building interiors are left at their
            // original Z since they need to align with the overworld surface.
            float dungeonZBump = isDungeonOnly ? DungeonDepthOffset : 0f;

            foreach (var envCell in envCells) {
                try {
                    var prepared = PrepareEnvCell(envCell, lbOffset, lbKey, dungeonZBump);
                    if (prepared != null) {
                        batch.Cells.Add(prepared);
                    }

                    // Extract static objects (furniture, torches, etc.) from inside this EnvCell.
                    // Stab Frame.Origin is in landblock-local space (confirmed by diagnostic).
                    // Route to dungeon vs building list based on landblock type.
                    if (envCell.StaticObjects != null && envCell.StaticObjects.Count > 0) {
                        var stabZOffset = new Vector3(0, 0, dungeonZBump);
                        var targetList = isDungeonOnly ? batch.DungeonStaticObjects : batch.BuildingStaticObjects;
                        foreach (var stab in envCell.StaticObjects) {
                            targetList.Add(new StaticObject {
                                Id = stab.Id,
                                IsSetup = (stab.Id & 0x02000000) != 0,
                                Origin = stab.Frame.Origin + lbOffset + stabZOffset,
                                Orientation = stab.Frame.Orientation,
                                Scale = Vector3.One
                            });
                        }
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"[EnvCellMgr] Error preparing EnvCell 0x{envCell.Id:X8}: {ex.Message}");
                }
            }

            Console.WriteLine($"[EnvCellMgr] LB 0x{lbKey:X4}: {envCells.Count} EnvCells in, {batch.Cells.Count} prepared OK, " +
                $"{batch.DungeonStaticObjects.Count} dungeon statics, {batch.BuildingStaticObjects.Count} building statics" +
                (isDungeonOnly ? " [dungeon]" : " [building]"));

            return batch.Cells.Count > 0 || batch.DungeonStaticObjects.Count > 0 || batch.BuildingStaticObjects.Count > 0 ? batch : null;
        }


        private PreparedEnvCell? PrepareEnvCell(EnvCell envCell, Vector3 lbOffset, ushort lbKey = 0, float dungeonZBump = 0f) {
            // Load Environment from portal.dat
            uint envFileId = (uint)(envCell.EnvironmentId | 0x0D000000);
            if (_failedEnvironments.Contains(envFileId)) return null;

            DatReaderWriter.DBObjs.Environment? env;
            lock (_environmentCache) {
                if (!_environmentCache.TryGetValue(envFileId, out env)) {
                    if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out env)) {
                        _failedEnvironments.Add(envFileId);
                        return null;
                    }
                    _environmentCache[envFileId] = env;
                }
            }

            // Get the CellStruct for this cell.
            if (!env.Cells.TryGetValue(envCell.CellStructure, out var cellStruct)) {
                return null;
            }

            // Build the world transform from EnvCell position + landblock offset.
            // Dungeon cells get -50 Z bump to push below terrain/water.
            // Building cells get no offset — terrain PolygonOffset(1,1) handles
            // floor vs terrain, and zero offset keeps interior geometry aligned
            // with the exterior model so it doesn't bleed through walls/dome.
            var cellOrigin = envCell.Position.Origin + lbOffset;
            cellOrigin.Z += dungeonZBump;
            var worldTransform = Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation)
                * Matrix4x4.CreateTranslation(cellOrigin);

            // Resolve surface IDs (EnvCell surfaces are not fully qualified, OR with 0x08000000)
            var surfaceIds = new List<uint>();
            foreach (var surfId in envCell.Surfaces) {
                surfaceIds.Add((uint)(surfId | 0x08000000));
            }

            // Check if we already have GPU data for this exact geometry+surface combo
            var gpuKey = new EnvCellGpuKey(envFileId, envCell.CellStructure, surfaceIds);

            // Build vertex/index data from CellStruct (same approach as StaticObjectManager.PrepareGfxObjData)
            var meshData = PrepareCellStructMesh(cellStruct, surfaceIds);
            if (meshData == null) return null;

            return new PreparedEnvCell {
                GpuKey = gpuKey,
                WorldTransform = worldTransform,
                WorldPosition = cellOrigin,
                MeshData = meshData,
                CellId = envCell.Id,
                EnvironmentId = envFileId,
                SurfaceCount = surfaceIds.Count,
                LoadedLandblockKey = lbKey
            };
        }

        private PreparedCellStructMesh? PrepareCellStructMesh(CellStruct cellStruct, List<uint> surfaceIds) {
            var vertices = new List<VertexPositionNormalTexture>();
            var UVLookup = new Dictionary<(ushort vertId, ushort uvIdx), ushort>();

            var texturesByFormat = new Dictionary<(int Width, int Height, TextureFormat Format), List<PreparedTexture>>();
            var textureIndexByKey = new Dictionary<(int Width, int Height, TextureFormat Format, TextureAtlasManager.TextureKey Key), int>();
            var batchList = new List<(TextureBatch batch, (int, int, TextureFormat) format)>();

            foreach (var kvp in cellStruct.Polygons) {
                var poly = kvp.Value;
                if (poly.VertexIds.Count < 3) continue;

                // Skip portal polygons (openings between rooms) — they have no renderable
                // surface and should not be drawn. StipplingType is a flags enum:
                //   NoPos (bit 0) = no positive surface
                //   NoNeg (bit 1) = no negative surface
                // Use HasFlag to catch both pure NoPos and compound types like NoBoth.
                if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;

                // EnvCell polygons reference surfaces from the EnvCell's Surfaces list
                int surfaceIdx = poly.PosSurface;
                bool useNegSurface = false;

                if (surfaceIdx < 0 || surfaceIdx >= surfaceIds.Count) continue;

                var surfaceId = surfaceIds[surfaceIdx];
                if (!_dats.TryGet<Surface>(surfaceId, out var surface)) continue;

                // NoPos polygons are already skipped above, so the stippling check here
                // only matters for Base1Solid surfaces.
                bool isSolid = surface.Type.HasFlag(SurfaceType.Base1Solid);
                var texResult = LoadTextureData(surfaceId, surface, isSolid, poly.Stippling);
                if (!texResult.HasValue) continue;

                var (textureData, texWidth, texHeight, textureFormat, uploadPixelFormat, uploadPixelType, paletteId) = texResult.Value;
                var format = (texWidth, texHeight, textureFormat);
                var texKey = new TextureAtlasManager.TextureKey {
                    SurfaceId = surfaceId,
                    PaletteId = paletteId,
                    Stippling = poly.Stippling,
                    IsSolid = isSolid
                };

                var texLookupKey = (texWidth, texHeight, textureFormat, texKey);
                if (!textureIndexByKey.TryGetValue(texLookupKey, out var textureIndex)) {
                    if (!texturesByFormat.TryGetValue(format, out var texList)) {
                        texList = new List<PreparedTexture>();
                        texturesByFormat[format] = texList;
                    }
                    textureIndex = texList.Count;
                    texList.Add(new PreparedTexture {
                        Key = texKey,
                        Data = textureData,
                        Width = texWidth,
                        Height = texHeight,
                        Format = textureFormat,
                        UploadPixelFormat = uploadPixelFormat,
                        UploadPixelType = uploadPixelType
                    });
                    textureIndexByKey[texLookupKey] = textureIndex;
                }

                bool isDoubleSided = !isSolid;

                var existingBatch = batchList.FirstOrDefault(b =>
                    b.format == format && b.batch.TextureIndex == textureIndex);
                TextureBatch batch;
                if (existingBatch.batch != null) {
                    batch = existingBatch.batch;
                    if (isDoubleSided) batch.IsDoubleSided = true;
                }
                else {
                    batch = new TextureBatch { TextureIndex = textureIndex, SurfaceId = surfaceId, Key = texKey, IsDoubleSided = isDoubleSided };
                    batchList.Add((batch, format));
                }

                BuildPolygonIndices(poly, cellStruct, UVLookup, vertices, batch, useNegSurface);
            }

            if (vertices.Count == 0) return null;

            var preparedBatches = batchList.Select(b => new PreparedBatch {
                Indices = b.batch.Indices.ToArray(),
                Format = b.format,
                SurfaceId = b.batch.SurfaceId,
                Key = b.batch.Key,
                TextureIndex = b.batch.TextureIndex,
                IsDoubleSided = b.batch.IsDoubleSided
            }).ToList();

            return new PreparedCellStructMesh {
                Vertices = vertices.ToArray(),
                Batches = preparedBatches,
                TexturesByFormat = texturesByFormat
            };
        }

        private void BuildPolygonIndices(Polygon poly, CellStruct cellStruct,
            Dictionary<(ushort vertId, ushort uvIdx), ushort> UVLookup,
            List<VertexPositionNormalTexture> vertices, TextureBatch batch, bool useNegSurface) {

            var polyIndices = new List<ushort>();

            for (int i = 0; i < poly.VertexIds.Count; i++) {
                ushort vertId = (ushort)poly.VertexIds[i];
                ushort uvIdx = 0;

                if (useNegSurface && poly.NegUVIndices != null && i < poly.NegUVIndices.Count) {
                    uvIdx = poly.NegUVIndices[i];
                }
                else if (!useNegSurface && poly.PosUVIndices != null && i < poly.PosUVIndices.Count) {
                    uvIdx = poly.PosUVIndices[i];
                }

                if (!cellStruct.VertexArray.Vertices.TryGetValue(vertId, out var vertex)) continue;
                if (uvIdx >= vertex.UVs.Count) {
                    uvIdx = 0;
                }

                var key = (vertId, uvIdx);
                if (!UVLookup.TryGetValue(key, out var idx)) {
                    var uv = vertex.UVs.Count > 0
                        ? new Vector2(vertex.UVs[uvIdx].U, vertex.UVs[uvIdx].V)
                        : Vector2.Zero;

                    idx = (ushort)vertices.Count;
                    vertices.Add(new VertexPositionNormalTexture(
                        vertex.Origin,
                        Vector3.Normalize(vertex.Normal),
                        uv
                    ));
                    UVLookup[key] = idx;
                }
                polyIndices.Add(idx);
            }

            // Fan triangulation (same as StaticObjectManager)
            for (int i = 2; i < polyIndices.Count; i++) {
                batch.Indices.Add(polyIndices[i]);
                batch.Indices.Add(polyIndices[i - 1]);
                batch.Indices.Add(polyIndices[0]);
            }
        }

        /// <summary>
        /// Texture loading — mirrors StaticObjectManager.LoadTextureData exactly.
        /// </summary>
        private (byte[] data, int width, int height, TextureFormat format, PixelFormat? uploadFormat, PixelType? uploadType, uint paletteId)?
            LoadTextureData(uint surfaceId, Surface surface, bool isSolid, StipplingType stippling) {

            PixelFormat? uploadPixelFormat = null;
            PixelType? uploadPixelType = null;
            uint paletteId = 0;

            if (isSolid) {
                int texWidth = 32, texHeight = 32;
                var solidData = TextureHelpers.CreateSolidColorTexture(surface.ColorValue, texWidth, texHeight);
                return (solidData, texWidth, texHeight, TextureFormat.RGBA8, PixelFormat.Rgba, null, 0);
            }

            if (!_dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfaceTexture) ||
                surfaceTexture.Textures?.Any() != true) {
                return null;
            }

            var renderSurfaceId = surfaceTexture.Textures.Last();
            if (!_dats.TryGet<RenderSurface>(renderSurfaceId, out var renderSurface)) return null;

            int w = renderSurface.Width, h = renderSurface.Height;
            paletteId = renderSurface.DefaultPaletteId;

            if (TextureHelpers.IsCompressedFormat(renderSurface.Format)) {
                var fmt = renderSurface.Format switch {
                    DatReaderWriter.Enums.PixelFormat.PFID_DXT1 => TextureFormat.DXT1,
                    DatReaderWriter.Enums.PixelFormat.PFID_DXT3 => TextureFormat.DXT3,
                    DatReaderWriter.Enums.PixelFormat.PFID_DXT5 => TextureFormat.DXT5,
                    _ => throw new NotSupportedException($"Unsupported compressed format: {renderSurface.Format}")
                };
                return (renderSurface.SourceData, w, h, fmt, null, null, paletteId);
            }

            TextureFormat textureFormat = TextureFormat.RGBA8;
            byte[] textureData;

            switch (renderSurface.Format) {
                case DatReaderWriter.Enums.PixelFormat.PFID_A8R8G8B8:
                    uploadPixelFormat = PixelFormat.Rgba;
                    textureData = renderSurface.SourceData;
                    break;
                case DatReaderWriter.Enums.PixelFormat.PFID_R8G8B8:
                    uploadPixelFormat = PixelFormat.Rgb;
                    textureFormat = TextureFormat.RGB8;
                    textureData = renderSurface.SourceData;
                    break;
                case DatReaderWriter.Enums.PixelFormat.PFID_INDEX16: {
                    var cached = _textureCache?.TryGet(surfaceId, paletteId);
                    if (cached != null) {
                        uploadPixelFormat = PixelFormat.Rgba;
                        return (cached, w, h, TextureFormat.RGBA8, uploadPixelFormat, null, paletteId);
                    }

                    if (!_dats.TryGet<Palette>(renderSurface.DefaultPaletteId, out var paletteData))
                        throw new Exception($"Unable to load Palette: 0x{renderSurface.DefaultPaletteId:X8}");
                    textureData = new byte[w * h * 4];
                    TextureHelpers.FillIndex16(renderSurface.SourceData, paletteData, textureData.AsSpan(), w, h);
                    uploadPixelFormat = PixelFormat.Rgba;

                    _textureCache?.Store(surfaceId, paletteId, textureData);
                    break;
                }
                default:
                    throw new NotSupportedException($"Unsupported surface format: {renderSurface.Format}");
            }

            return (textureData, w, h, textureFormat, uploadPixelFormat, uploadPixelType, paletteId);
        }

        #endregion

        #region GPU Upload (must be called on GL thread)

        /// <summary>
        /// Queues a prepared batch for GPU upload on the next frame.
        /// Thread-safe (uses ConcurrentQueue).
        /// </summary>
        public void QueueForUpload(PreparedEnvCellBatch batch) {
            _uploadQueue.Enqueue(batch);
        }

        /// <summary>
        /// Processes queued GPU uploads. Must be called on the GL thread (e.g. during Update).
        /// Returns the number of batches processed. Skips uploads if at the cell cap.
        /// </summary>
        public int ProcessUploads(int maxPerFrame = 2) {
            int processed = 0;
            while (processed < maxPerFrame && _uploadQueue.TryDequeue(out var batch)) {
                // Skip if we're already at the cell cap
                if (LoadedCellCount >= MaxLoadedDungeonCells) {
                    Console.WriteLine($"[EnvCellMgr] Cell cap ({MaxLoadedDungeonCells}) reached, skipping upload for LB 0x{batch.LandblockKey:X4}");
                    continue;
                }
                FinalizeGpuUpload(batch);
                processed++;
            }
            return processed;
        }

        private unsafe void FinalizeGpuUpload(PreparedEnvCellBatch batch) {
            var cells = new List<LoadedEnvCell>();

            foreach (var prepared in batch.Cells) {
                // Check if GPU data already exists for this geometry+surface combo
                if (!_gpuCache.TryGetValue(prepared.GpuKey, out var renderData)) {
                    // Upload new GPU data
                    renderData = UploadCellStructToGpu(prepared.MeshData);
                    if (renderData == null) continue;
                    _gpuCache[prepared.GpuKey] = renderData;
                }

                cells.Add(new LoadedEnvCell {
                    GpuKey = prepared.GpuKey,
                    WorldTransform = prepared.WorldTransform,
                    WorldPosition = prepared.WorldPosition,
                    CellId = prepared.CellId,
                    EnvironmentId = prepared.EnvironmentId,
                    SurfaceCount = prepared.SurfaceCount,
                    LoadedLandblockKey = prepared.LoadedLandblockKey
                });
            }

            if (cells.Count > 0) {
                _loadedCells[batch.LandblockKey] = cells;
                if (batch.IsDungeonOnly)
                    _dungeonOnlyLandblocks.Add(batch.LandblockKey);
                else
                    _dungeonOnlyLandblocks.Remove(batch.LandblockKey);
                Console.WriteLine($"[EnvCellMgr] GPU upload LB 0x{batch.LandblockKey:X4}: {cells.Count} cells, {_gpuCache.Count} unique GPU entries total");
            }
        }

        private unsafe EnvCellRenderData? UploadCellStructToGpu(PreparedCellStructMesh mesh) {
            if (mesh.Vertices.Length == 0) return null;

            var gl = _renderer.GraphicsDevice.GL;

            // Create atlas managers and upload textures
            var localAtlases = new Dictionary<(int Width, int Height, TextureFormat Format), TextureAtlasManager>();
            var atlasTextureIndices = new Dictionary<(int Width, int Height, TextureFormat Format, int PreparedIndex), int>();

            foreach (var (format, textures) in mesh.TexturesByFormat) {
                var atlasManager = new TextureAtlasManager(_renderer, format.Width, format.Height, format.Format);
                localAtlases[format] = atlasManager;

                for (int i = 0; i < textures.Count; i++) {
                    var tex = textures[i];
                    int atlasIdx = atlasManager.AddTexture(tex.Key, tex.Data, tex.UploadPixelFormat, tex.UploadPixelType);
                    atlasTextureIndices[(format.Width, format.Height, format.Format, i)] = atlasIdx;
                }
            }

            // Upload vertices
            gl.GenVertexArrays(1, out uint vao);
            gl.BindVertexArray(vao);

            gl.GenBuffers(1, out uint vbo);
            gl.BindBuffer(GLEnum.ArrayBuffer, vbo);
            fixed (VertexPositionNormalTexture* ptr = mesh.Vertices) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(mesh.Vertices.Length * VertexPositionNormalTexture.Size), ptr, GLEnum.StaticDraw);
            }

            int stride = VertexPositionNormalTexture.Size;
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, GLEnum.Float, false, (uint)stride, (void*)(6 * sizeof(float)));

            // Upload index buffers and create render batches
            var renderBatches = new List<RenderBatch>();
            foreach (var batch in mesh.Batches) {
                if (batch.Indices.Length == 0) continue;

                var atlasManager = localAtlases[batch.Format];
                var atlasKey = (batch.Format.Width, batch.Format.Height, batch.Format.Format, batch.TextureIndex);
                int atlasIdx = atlasTextureIndices.TryGetValue(atlasKey, out var idx) ? idx : batch.TextureIndex;

                gl.GenBuffers(1, out uint ibo);
                gl.BindBuffer(GLEnum.ElementArrayBuffer, ibo);
                fixed (ushort* iptr = batch.Indices) {
                    gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(batch.Indices.Length * sizeof(ushort)), iptr, GLEnum.StaticDraw);
                }

                renderBatches.Add(new RenderBatch {
                    IBO = ibo,
                    IndexCount = batch.Indices.Length,
                    TextureArray = atlasManager.TextureArray,
                    TextureIndex = atlasIdx,
                    TextureSize = (batch.Format.Width, batch.Format.Height),
                    TextureFormat = batch.Format.Format,
                    SurfaceId = batch.SurfaceId,
                    Key = batch.Key,
                    IsDoubleSided = batch.IsDoubleSided
                });
            }

            gl.BindVertexArray(0);

            return new EnvCellRenderData {
                VAO = vao,
                VBO = vbo,
                Batches = renderBatches,
                LocalAtlases = localAtlases
            };
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Renders all loaded dungeon cells. Must be called on the GL thread.
        /// The caller is expected to have already set up depth test, blend, etc.
        /// </summary>
        public unsafe void Render(Matrix4x4 viewProjection, ICamera camera, Vector3 lightDirection, float ambientIntensity, float specularPower) {
            if (_loadedCells.Count == 0) return;

            var gl = _renderer.GraphicsDevice.GL;
            var frustum = new Frustum(viewProjection);

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            // Disable face culling for EnvCell geometry — AC's CellStruct polygon winding
            // doesn't match OpenGL's default CCW front-face convention, so walls/ceilings
            // get culled if we enable it. ACViewer also uses CullMode.None for CellStructs.
            gl.Disable(EnableCap.CullFace);

            _shader.Bind();
            _shader.SetUniform("uViewProjection", viewProjection);
            _shader.SetUniform("uCameraPosition", camera.Position);
            _shader.SetUniform("uLightDirection", Vector3.Normalize(lightDirection));
            _shader.SetUniform("uAmbientIntensity", ambientIntensity);
            _shader.SetUniform("uSpecularPower", specularPower);

            // Group visible cells by GpuKey, separated into building vs dungeon batches.
            // Building interiors get polygon offset to win over terrain/exterior floors.
            // Dungeon cells already have a -50 Z bump and don't need offset.
            const float cellBoundsRadius = 50f;
            foreach (var list in _cellGroupBuffer.Values) list.Clear();
            foreach (var list in _buildingCellGroupBuffer.Values) list.Clear();

            foreach (var kvp in _loadedCells) {
                bool isDungeon = _dungeonOnlyLandblocks.Contains(kvp.Key);

                if (isDungeon) {
                    // Dungeon cells: require ShowDungeonCells + respect focus filter
                    if (!ShowDungeonCells) continue;
                    if (FocusedDungeonLB.HasValue && kvp.Key != FocusedDungeonLB.Value) continue;
                } else {
                    // Building interior cells: require ShowDungeonCells
                    if (!ShowDungeonCells) continue;
                }

                var targetBuffer = isDungeon ? _cellGroupBuffer : _buildingCellGroupBuffer;

                foreach (var cell in kvp.Value) {
                    var cellBounds = new Chorizite.Core.Lib.BoundingBox(
                        cell.WorldPosition - new Vector3(cellBoundsRadius),
                        cell.WorldPosition + new Vector3(cellBoundsRadius));
                    if (!frustum.IntersectsBoundingBox(cellBounds)) continue;

                    if (!targetBuffer.TryGetValue(cell.GpuKey, out var list)) {
                        list = new List<Matrix4x4>();
                        targetBuffer[cell.GpuKey] = list;
                    }
                    list.Add(cell.WorldTransform);
                }
            }

            // Draw building interiors pushed BACK so exterior GfxObj always wins
            // at overlapping walls/ceilings. Through door/window openings, interior
            // renders fine since there's no exterior geometry competing.
            // Terrain uses PolygonOffset(2,2) so interior floors still win over terrain.
            gl.Enable(EnableCap.PolygonOffsetFill);
            gl.PolygonOffset(1f, 1f);
            foreach (var (gpuKey, transforms) in _buildingCellGroupBuffer) {
                if (transforms.Count == 0) continue;
                if (!_gpuCache.TryGetValue(gpuKey, out var renderData)) continue;
                if (renderData.Batches.Count == 0) continue;
                RenderBatchedEnvCell(gl, renderData, transforms);
            }
            gl.Disable(EnableCap.PolygonOffsetFill);

            // Draw dungeon cells (no offset needed — already -50 Z below terrain)
            foreach (var (gpuKey, transforms) in _cellGroupBuffer) {
                if (transforms.Count == 0) continue;
                if (!_gpuCache.TryGetValue(gpuKey, out var renderData)) continue;
                if (renderData.Batches.Count == 0) continue;
                RenderBatchedEnvCell(gl, renderData, transforms);
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(EnableCap.Blend);
        }

        private unsafe void RenderBatchedEnvCell(GL gl, EnvCellRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (instanceTransforms.Count == 0 || renderData.Batches.Count == 0) return;

            int requiredFloats = instanceTransforms.Count * 16;

            // Ensure CPU-side upload buffer is large enough
            if (_instanceUploadBuffer.Length < requiredFloats) {
                int newSize = Math.Max(requiredFloats, 256);
                newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newSize);
                _instanceUploadBuffer = new float[newSize];
            }

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

            if (_instanceVBO == 0) {
                gl.GenBuffers(1, out _instanceVBO);
            }

            gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVBO);

            if (requiredFloats > _instanceBufferCapacity) {
                int newCapacity = Math.Max(requiredFloats, 256);
                newCapacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newCapacity);
                _instanceBufferCapacity = newCapacity;
                fixed (float* ptr = _instanceUploadBuffer) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(newCapacity * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
            }
            else {
                fixed (float* ptr = _instanceUploadBuffer) {
                    gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(requiredFloats * sizeof(float)), ptr);
                }
            }

            gl.BindVertexArray(renderData.VAO);

            // Set up instance attributes (mat4 in attribute slots 3-6)
            gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVBO);
            for (int i = 0; i < 4; i++) {
                gl.EnableVertexAttribArray((uint)(3 + i));
                gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false, (uint)(16 * sizeof(float)), (void*)(i * 4 * sizeof(float)));
                gl.VertexAttribDivisor((uint)(3 + i), 1);
            }

            // Culling is disabled globally for EnvCell geometry (set in Render method),
            // so no per-batch cull toggling needed.
            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;

                try {
                    batch.TextureArray.Bind(0);
                    _shader.SetUniform("uTextureArray", 0);
                    // The shader reads texture index from vertex attribute location 7 (aTextureIndex),
                    // NOT from a uniform. Use glVertexAttrib1f to set a constant value for all vertices
                    // when no VBO is bound to that attribute. This is the same approach the shader expects.
                    gl.DisableVertexAttribArray(7); // ensure no VBO is bound to location 7
                    gl.VertexAttrib1((uint)7, (float)batch.TextureIndex);
                    gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                    gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, (uint)instanceTransforms.Count);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[EnvCellMgr] Error rendering batch: {ex.Message}");
                }
            }

            gl.BindVertexArray(0);
        }

        #endregion

        #region Landblock Management

        /// <summary>
        /// Unloads all dungeon cells for a landblock, releasing GPU resources where no longer referenced.
        /// Must be called on the GL thread.
        /// </summary>
        public void UnloadLandblock(ushort lbKey) {
            if (!_loadedCells.TryGetValue(lbKey, out var cells)) return;

            // Track which GPU keys are still in use by other landblocks
            var keysToRemove = new HashSet<EnvCellGpuKey>();
            foreach (var cell in cells) {
                keysToRemove.Add(cell.GpuKey);
            }

            _loadedCells.Remove(lbKey);
            _dungeonOnlyLandblocks.Remove(lbKey);

            // Check if any remaining landblocks still reference these GPU keys
            foreach (var otherCells in _loadedCells.Values) {
                foreach (var cell in otherCells) {
                    keysToRemove.Remove(cell.GpuKey);
                }
            }

            // Release unreferenced GPU resources
            var gl = _renderer.GraphicsDevice.GL;
            foreach (var key in keysToRemove) {
                if (_gpuCache.TryGetValue(key, out var renderData)) {
                    if (renderData.VAO != 0) gl.DeleteVertexArray(renderData.VAO);
                    if (renderData.VBO != 0) gl.DeleteBuffer(renderData.VBO);
                    foreach (var batch in renderData.Batches) {
                        if (batch.IBO != 0) gl.DeleteBuffer(batch.IBO);
                    }
                    foreach (var atlas in renderData.LocalAtlases.Values) {
                        atlas.Dispose();
                    }
                    _gpuCache.Remove(key);
                }
            }
        }

        /// <summary>
        /// Returns true if the given landblock has any loaded dungeon cells.
        /// </summary>
        public bool HasLoadedCells(ushort lbKey) => _loadedCells.ContainsKey(lbKey);

        /// <summary>
        /// Returns all landblock keys that currently have loaded dungeon cells.
        /// </summary>
        public IEnumerable<ushort> GetLoadedLandblockKeys() => _loadedCells.Keys.ToList();

        #endregion

        #region Helpers

        /// <summary>
        /// Approximate bounding radius for dungeon cells (frustum culling — kept large).
        /// </summary>
        public const float CellBoundsRadius = 50f;

        /// <summary>
        /// Tighter bounding radius for ray picking (cells are typically ~10 units wide).
        /// </summary>
        public const float CellPickRadius = 4f;

        /// <summary>
        /// Hit result from an EnvCell raycast.
        /// </summary>
        public struct EnvCellRaycastHit {
            public bool Hit;
            public LoadedEnvCell Cell;
            public float Distance;
            public Vector3 HitPosition;
        }

        /// <summary>
        /// Performs a ray-AABB intersection test against all loaded dungeon cells.
        /// Returns the closest hit cell, or a miss result.
        /// </summary>
        public EnvCellRaycastHit Raycast(Vector3 rayOrigin, Vector3 rayDirection) {
            var result = new EnvCellRaycastHit { Hit = false, Distance = float.MaxValue };

            foreach (var kvp in _loadedCells) {
                // Only raycast against focused dungeon (if set), otherwise all
                if (FocusedDungeonLB.HasValue && kvp.Key != FocusedDungeonLB.Value) continue;

                foreach (var cell in kvp.Value) {
                    var aabbMin = cell.WorldPosition - new Vector3(CellPickRadius);
                    var aabbMax = cell.WorldPosition + new Vector3(CellPickRadius);

                    if (RayIntersectsAABB(rayOrigin, rayDirection, aabbMin, aabbMax, out float dist)) {
                        if (dist < result.Distance) {
                            result = new EnvCellRaycastHit {
                                Hit = true,
                                Cell = cell,
                                Distance = dist,
                                HitPosition = rayOrigin + rayDirection * dist
                            };
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Ray-AABB intersection test (slab method).
        /// </summary>
        private static bool RayIntersectsAABB(Vector3 rayOrigin, Vector3 rayDir, Vector3 aabbMin, Vector3 aabbMax, out float distance) {
            distance = 0f;
            float tMin = float.MinValue;
            float tMax = float.MaxValue;

            for (int i = 0; i < 3; i++) {
                float origin = i == 0 ? rayOrigin.X : i == 1 ? rayOrigin.Y : rayOrigin.Z;
                float dir = i == 0 ? rayDir.X : i == 1 ? rayDir.Y : rayDir.Z;
                float min = i == 0 ? aabbMin.X : i == 1 ? aabbMin.Y : aabbMin.Z;
                float max = i == 0 ? aabbMax.X : i == 1 ? aabbMax.Y : aabbMax.Z;

                if (Math.Abs(dir) < 1e-8f) {
                    if (origin < min || origin > max) return false;
                }
                else {
                    float t1 = (min - origin) / dir;
                    float t2 = (max - origin) / dir;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tMin = Math.Max(tMin, t1);
                    tMax = Math.Min(tMax, t2);
                    if (tMin > tMax) return false;
                }
            }

            distance = tMin >= 0 ? tMin : tMax;
            return distance >= 0;
        }

        #endregion

        public void Dispose() {
            var gl = _renderer.GraphicsDevice.GL;
            foreach (var renderData in _gpuCache.Values) {
                if (renderData.VAO != 0) gl.DeleteVertexArray(renderData.VAO);
                if (renderData.VBO != 0) gl.DeleteBuffer(renderData.VBO);
                foreach (var batch in renderData.Batches) {
                    if (batch.IBO != 0) gl.DeleteBuffer(batch.IBO);
                }
                foreach (var atlas in renderData.LocalAtlases.Values) {
                    atlas.Dispose();
                }
            }
            _gpuCache.Clear();
            _loadedCells.Clear();
            _dungeonOnlyLandblocks.Clear();
            _environmentCache.Clear();
            if (_instanceVBO != 0) gl.DeleteBuffer(_instanceVBO);
        }
    }

    #region Data Structures

    /// <summary>
    /// Unique key for GPU-cached EnvCell geometry. Combines the Environment ID, CellStructure index,
    /// and the full surface list (to avoid hash collisions that cause wrong textures).
    /// </summary>
    public readonly record struct EnvCellGpuKey : IEquatable<EnvCellGpuKey> {
        public readonly uint EnvironmentId;
        public readonly uint CellStructure;
        private readonly string _surfaceKey;

        public EnvCellGpuKey(uint environmentId, uint cellStructure, List<uint> surfaceIds) {
            EnvironmentId = environmentId;
            CellStructure = cellStructure;
            _surfaceKey = string.Join(",", surfaceIds);
        }

        public bool Equals(EnvCellGpuKey other) =>
            EnvironmentId == other.EnvironmentId &&
            CellStructure == other.CellStructure &&
            _surfaceKey == other._surfaceKey;

        public override int GetHashCode() => HashCode.Combine(EnvironmentId, CellStructure, _surfaceKey);
    }

    /// <summary>
    /// GPU resource data for a rendered CellStruct instance.
    /// </summary>
    public class EnvCellRenderData {
        public uint VAO { get; set; }
        public uint VBO { get; set; }
        public List<RenderBatch> Batches { get; set; } = new();
        public Dictionary<(int Width, int Height, TextureFormat Format), TextureAtlasManager> LocalAtlases { get; set; } = new();
    }

    /// <summary>
    /// A loaded dungeon cell instance with its world transform and reference to shared GPU data.
    /// </summary>
    public class LoadedEnvCell {
        public EnvCellGpuKey GpuKey { get; set; }
        public Matrix4x4 WorldTransform { get; set; }
        /// <summary>World-space position for frustum culling.</summary>
        public Vector3 WorldPosition { get; set; }
        /// <summary>Original cell ID from cell.dat (e.g. 0x01D90105).</summary>
        public uint CellId { get; set; }
        /// <summary>Qualified environment file ID (0x0D000000 range).</summary>
        public uint EnvironmentId { get; set; }
        /// <summary>Number of surfaces in this cell's surface list.</summary>
        public int SurfaceCount { get; set; }
        /// <summary>Landblock key this cell was loaded under (from GameScene, always correct).</summary>
        public ushort LoadedLandblockKey { get; set; }
    }

    /// <summary>
    /// CPU-prepared data for a batch of EnvCells from a single landblock, ready for GPU upload.
    /// </summary>
    public class PreparedEnvCellBatch {
        public ushort LandblockKey { get; set; }
        public bool IsDungeonOnly { get; set; }
        public List<PreparedEnvCell> Cells { get; set; } = new();
        /// <summary>
        /// Static objects from inside dungeon-only EnvCells (underground).
        /// Only shown when the dungeon is focused to avoid overworld clutter.
        /// </summary>
        public List<StaticObject> DungeonStaticObjects { get; set; } = new();
        /// <summary>
        /// Static objects from inside building interior EnvCells (surface level).
        /// Always shown alongside regular outdoor statics.
        /// </summary>
        public List<StaticObject> BuildingStaticObjects { get; set; } = new();
    }

    /// <summary>
    /// CPU-prepared data for a single EnvCell, ready for GPU upload.
    /// </summary>
    public class PreparedEnvCell {
        public EnvCellGpuKey GpuKey { get; set; }
        public Matrix4x4 WorldTransform { get; set; }
        /// <summary>World-space position for frustum culling.</summary>
        public Vector3 WorldPosition { get; set; }
        public PreparedCellStructMesh MeshData { get; set; } = null!;
        public uint CellId { get; set; }
        public uint EnvironmentId { get; set; }
        public int SurfaceCount { get; set; }
        public ushort LoadedLandblockKey { get; set; }
    }

    /// <summary>
    /// CPU-prepared mesh data from a CellStruct, ready for GPU upload.
    /// </summary>
    public class PreparedCellStructMesh {
        public VertexPositionNormalTexture[] Vertices { get; set; } = Array.Empty<VertexPositionNormalTexture>();
        public List<PreparedBatch> Batches { get; set; } = new();
        public Dictionary<(int Width, int Height, TextureFormat Format), List<PreparedTexture>> TexturesByFormat { get; set; } = new();
    }

    #endregion
}

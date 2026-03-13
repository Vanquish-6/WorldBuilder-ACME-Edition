using Chorizite.Core.Render;
using Chorizite.Core.Render.Enums;
using Chorizite.OpenGLSDLBackend;
using Chorizite.OpenGLSDLBackend.Lib;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Silk.NET.OpenGL;
using System;
using System.Buffers;
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
        private const int MaxLoadedDungeonCells = 10000; // Hard cap on dungeon-only cells (building interior cells are exempt)

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

        // Synchronizes access to _gpuCache, _loadedCells, _cellLookup across render and UI threads
        private readonly object _cellLock = new();

        // Track which landblocks are dungeon-only (vs building interiors)
        private readonly HashSet<ushort> _dungeonOnlyLandblocks = new();
        // Track landblocks that contain both building AND dungeon cells
        private readonly HashSet<ushort> _mixedLandblocks = new();

        // Fast lookup from full cell ID to LoadedEnvCell (for portal traversal neighbor lookups)
        private readonly Dictionary<uint, LoadedEnvCell> _cellLookup = new();

        // Portal visibility traversal state
        private LoadedEnvCell? _lastCameraCell;
        private int _cellSwitchGraceFrames;
        private int _diagThrottle;

        /// <summary>
        /// When set, only render dungeon cells from this landblock. Null = show all.
        /// </summary>
        public ushort? FocusedDungeonLB { get; set; }

        /// <summary>
        /// Landblock key used for placement/hover previews. Always rendered regardless of FocusedDungeonLB.
        /// </summary>
        public const ushort PreviewLandblockKey = 0xFFFE;

        /// <summary>
        /// Controls whether dungeon-only cells are rendered.
        /// </summary>
        public bool ShowDungeonCells { get; set; } = true;

        /// <summary>
        /// When true, building interior cells render even when the camera is outside
        /// (using frustum culling instead of portal filtering). Useful for top-down editing.
        /// </summary>
        public bool AlwaysShowBuildingInteriors { get; set; } = false;

        /// <summary>
        /// Returns all landblock keys that have loaded dungeon cells, sorted.
        /// </summary>
        public List<ushort> GetLoadedDungeonLandblocks() {
            lock (_cellLock) {
                return _loadedCells.Keys.OrderBy(k => k).ToList();
            }
        }

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
        private int _instanceUploadBufferRented; // >0 if buffer was rented from ArrayPool

        // Reusable cell grouping buffers (avoids per-frame dictionary allocation in Render)
        private readonly Dictionary<EnvCellGpuKey, List<Matrix4x4>> _cellGroupBuffer = new();
        private readonly Dictionary<EnvCellGpuKey, List<Matrix4x4>> _buildingCellGroupBuffer = new();

        // Draw plan for batched rendering (single instance buffer upload per frame)
        private readonly List<EnvCellDrawEntry> _envCellDrawPlan = new();
        private struct EnvCellDrawEntry {
            public uint VAO;
            public List<RenderBatch> Batches;
            public int InstanceCount;
            public int BufferFloatOffset;
        }

        /// <summary>
        /// Optional fallback for loading texture data when a Surface ID isn't in the DAT
        /// (e.g. custom imported textures). Returns (rgbaData, width, height) or null.
        /// </summary>
        public Func<uint, (byte[] Data, int Width, int Height)?> CustomTextureResolver { get; set; }

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
        /// Returns the total number of loaded cells across all landblocks (dungeon + building).
        /// </summary>
        public int LoadedCellCount { get { lock (_cellLock) return _loadedCells.Values.Sum(list => list.Count); } }

        /// <summary>
        /// Returns the number of loaded dungeon cells (excludes building interiors).
        /// For pure-dungeon landblocks, all cells count. For mixed landblocks, only non-building cells count.
        /// Used for cap enforcement -- building interior cells are small and always allowed.
        /// </summary>
        public int LoadedDungeonCellCount => _loadedCells
            .Where(kvp => _dungeonOnlyLandblocks.Contains(kvp.Key) || _mixedLandblocks.Contains(kvp.Key))
            .Sum(kvp => _dungeonOnlyLandblocks.Contains(kvp.Key)
                ? kvp.Value.Count
                : kvp.Value.Count(c => !c.IsBuilding));

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
        /// Building interiors get a small positive Z bump so the floor sits above terrain
        /// and avoids floor/terrain Z-fighting (flashing).
        /// </summary>
        internal const float DungeonDepthOffset = -50f;
        /// <summary>World-space Z offset so building floor sits above terrain and wins depth; 0.05 was insufficient.</summary>
        private const float BuildingFloorOffset = 0.2f;

        public PreparedEnvCellBatch? PrepareLandblockEnvCells(ushort lbKey, uint lbId, List<EnvCell> envCells, bool isDungeonOnly = false, HashSet<ushort>? buildingCellIds = null) {
            if (envCells.Count == 0) return null;

            var batch = new PreparedEnvCellBatch {
                LandblockKey = lbKey,
                IsDungeonOnly = isDungeonOnly,
                HasMixedCells = false,
                Cells = new List<PreparedEnvCell>()
            };

            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);

            int buildingCellCount = 0;
            int dungeonCellCount = 0;

            foreach (var envCell in envCells) {
                try {
                    ushort cellNum = (ushort)(envCell.Id & 0xFFFF);

                    // Per-cell classification:
                    // 1) If isDungeonOnly (no buildings at all) -> all cells are dungeon
                    // 2) If buildingCellIds provided -> check membership
                    // 3) Fallback: use SeenOutside flag (building interiors see outside; dungeons don't)
                    bool isBuildingCell;
                    if (isDungeonOnly) {
                        isBuildingCell = false;
                    } else if (buildingCellIds != null) {
                        isBuildingCell = buildingCellIds.Contains(cellNum);
                    } else {
                        isBuildingCell = envCell.Flags.HasFlag(EnvCellFlags.SeenOutside);
                    }

                    float cellZBump;
                    if (isBuildingCell) {
                        cellZBump = BuildingFloorOffset;
                        buildingCellCount++;
                    } else if (isDungeonOnly) {
                        cellZBump = DungeonDepthOffset;
                        dungeonCellCount++;
                    } else {
                        cellZBump = 0f;
                        dungeonCellCount++;
                    }

                    var prepared = PrepareEnvCell(envCell, lbOffset, lbKey, cellZBump);
                    if (prepared != null) {
                        prepared.IsBuilding = isBuildingCell;
                        batch.Cells.Add(prepared);
                    }

                    if (envCell.StaticObjects != null && envCell.StaticObjects.Count > 0) {
                        var stabZOffset = new Vector3(0, 0, cellZBump);
                        var targetList = isBuildingCell ? batch.BuildingStaticObjects : batch.DungeonStaticObjects;
                        var parentCellList = isBuildingCell ? batch.BuildingStaticParentCells : batch.DungeonStaticParentCells;
                        foreach (var stab in envCell.StaticObjects) {
                            targetList.Add(new StaticObject {
                                Id = stab.Id,
                                IsSetup = (stab.Id & 0x02000000) != 0,
                                Origin = stab.Frame.Origin + lbOffset + stabZOffset,
                                Orientation = stab.Frame.Orientation,
                                Scale = Vector3.One
                            });
                            parentCellList.Add(envCell.Id);
                        }
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"[EnvCellMgr] Error preparing EnvCell 0x{envCell.Id:X8}: {ex.Message}");
                }
            }

            batch.HasMixedCells = buildingCellCount > 0 && dungeonCellCount > 0;

            string typeLabel = isDungeonOnly ? "dungeon" : (batch.HasMixedCells ? "MIXED" : "building");
            System.Diagnostics.Debug.WriteLine($"[EnvCellMgr] LB 0x{lbKey:X4}: {envCells.Count} EnvCells, {batch.Cells.Count} OK " +
                $"[{typeLabel}: {buildingCellCount}b+{dungeonCellCount}d, {batch.DungeonStaticObjects.Count}+{batch.BuildingStaticObjects.Count} statics]");

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
            // Dungeon cells get -50 Z bump; building cells get +0.2 Z so floor wins over terrain.
            var cellOrigin = envCell.Position.Origin + lbOffset;
            cellOrigin.Z += dungeonZBump;
            // AC Frame::localtoglobal: world = R*local + origin (rotate first, then translate).
            var worldTransform = Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation)
                * Matrix4x4.CreateTranslation(cellOrigin);

            // Resolve surface IDs (EnvCell surfaces are not fully qualified, OR with 0x08000000)
            var surfaceIds = new List<uint>();
            foreach (var surfId in envCell.Surfaces) {
                surfaceIds.Add((uint)(surfId | 0x08000000));
            }

            // Build the definitive set of portal polygon IDs from multiple sources.
            var portalPolyIds = new HashSet<ushort>();

            // 1) CellStruct.Portals — structural portal list from Environment geometry.
            //    In AC source CCellStruct::UnPack, portals are stored as indices into the
            //    polygons array. DatReaderWriter may store them as indices OR as poly_ids
            //    depending on version; handle both.
            if (cellStruct.Portals != null) {
                var polyKeys = cellStruct.Polygons.Keys.ToArray();
                foreach (var portalRef in cellStruct.Portals) {
                    if (cellStruct.Polygons.ContainsKey(portalRef)) {
                        portalPolyIds.Add(portalRef);
                    }
                    else if (portalRef >= 0 && portalRef < polyKeys.Length) {
                        portalPolyIds.Add(polyKeys[portalRef]);
                    }
                }
            }

            // 2) EnvCell.CellPortals — per-instance portal connections
            if (envCell.CellPortals != null) {
                foreach (var cp in envCell.CellPortals)
                    portalPolyIds.Add((ushort)cp.PolygonId);
            }

            // 3) Polygons minus PhysicsPolygons — portal polys appear in the rendering
            //    polygon set but not in the physics/collision set
            if (cellStruct.PhysicsPolygons != null && cellStruct.PhysicsPolygons.Count > 0) {
                foreach (var polyId in cellStruct.Polygons.Keys) {
                    if (!cellStruct.PhysicsPolygons.ContainsKey(polyId))
                        portalPolyIds.Add(polyId);
                }
            }

            // Include portal polygon IDs in the GPU key so cells sharing the same
            // environment+surfaces but having different portal openings get distinct meshes.
            var gpuKey = new EnvCellGpuKey(envFileId, envCell.CellStructure, surfaceIds, portalPolyIds);

            // Build vertex/index data from CellStruct (same approach as StaticObjectManager.PrepareGfxObjData)
            var meshData = PrepareCellStructMesh(cellStruct, surfaceIds, portalPolyIds);
            if (meshData == null) return null;

            // Compute local-space AABB and centroid from CellStruct vertices.
            // Centroid is needed for portal-side determination below.
            var boundsMin = new Vector3(float.MaxValue);
            var boundsMax = new Vector3(float.MinValue);
            var cellCentroid = Vector3.Zero;
            int vtxCount = cellStruct.VertexArray.Vertices.Count;
            foreach (var vtx in cellStruct.VertexArray.Vertices.Values) {
                boundsMin = Vector3.Min(boundsMin, vtx.Origin);
                boundsMax = Vector3.Max(boundsMax, vtx.Origin);
                cellCentroid += vtx.Origin;
            }
            if (vtxCount > 0)
                cellCentroid /= vtxCount;

            // Extract portal connectivity and clip planes from CellPortals
            var portals = new List<CellPortalInfo>();
            var clipPlanes = new List<PortalClipPlane>();

            if (envCell.CellPortals != null) {
                foreach (var cp in envCell.CellPortals) {
                    int portalSide = ((int)cp.Flags & 2) == 0 ? 1 : 0;

                    portals.Add(new CellPortalInfo {
                        OtherCellId = cp.OtherCellId,
                        PolygonId = (ushort)cp.PolygonId,
                        PortalSide = portalSide
                    });

                    // Compute portal plane from the referenced polygon's vertices
                    if (cellStruct.Polygons.TryGetValue((ushort)cp.PolygonId, out var portalPoly) &&
                        portalPoly.VertexIds.Count >= 3) {
                        var v0Pos = GetVertexPosition(cellStruct, portalPoly.VertexIds[0]);
                        var v1Pos = GetVertexPosition(cellStruct, portalPoly.VertexIds[1]);
                        var v2Pos = GetVertexPosition(cellStruct, portalPoly.VertexIds[2]);

                        if (v0Pos.HasValue && v1Pos.HasValue && v2Pos.HasValue) {
                            var edge1 = v1Pos.Value - v0Pos.Value;
                            var edge2 = v2Pos.Value - v0Pos.Value;
                            var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
                            float d = -Vector3.Dot(normal, v0Pos.Value);

                            // Determine InsideSide from geometry: check which side of the
                            // portal plane the cell centroid falls on. This is always correct
                            // regardless of stored flags (which may be 0 for editor-created portals).
                            float centroidDot = Vector3.Dot(normal, cellCentroid) + d;
                            int insideSide = centroidDot >= 0 ? 0 : 1;

                            clipPlanes.Add(new PortalClipPlane {
                                Normal = normal,
                                D = d,
                                InsideSide = insideSide
                            });
                        }
                    }
                }
            }

            return new PreparedEnvCell {
                GpuKey = gpuKey,
                WorldTransform = worldTransform,
                WorldPosition = cellOrigin,
                MeshData = meshData,
                CellId = envCell.Id,
                EnvironmentId = envFileId,
                SurfaceCount = surfaceIds.Count,
                LoadedLandblockKey = lbKey,
                Portals = portals,
                ClipPlanes = clipPlanes,
                LocalBoundsMin = boundsMin,
                LocalBoundsMax = boundsMax
            };
        }

        private static Vector3? GetVertexPosition(CellStruct cellStruct, int vertexId) {
            if (cellStruct.VertexArray.Vertices.TryGetValue((ushort)vertexId, out var vertex))
                return vertex.Origin;
            return null;
        }

        private PreparedCellStructMesh? PrepareCellStructMesh(CellStruct cellStruct, List<uint> surfaceIds, HashSet<ushort>? portalPolygonIds = null) {
            var vertices = new List<VertexPositionNormalTexture>();
            var UVLookup = new Dictionary<(ushort vertId, ushort uvIdx), ushort>();
            var portalOccluderIndices = new List<ushort>();

            var texturesByFormat = new Dictionary<(int Width, int Height, TextureFormat Format), List<PreparedTexture>>();
            var textureIndexByKey = new Dictionary<(int Width, int Height, TextureFormat Format, TextureAtlasManager.TextureKey Key), int>();
            var batchList = new List<(TextureBatch batch, (int, int, TextureFormat) format)>();

            foreach (var kvp in cellStruct.Polygons) {
                var poly = kvp.Value;
                if (poly.VertexIds.Count < 3) continue;

                // Portal polygons: collect indices for depth-only occlusion pass
                // (blocks terrain from showing through openings in building cells).
                if (portalPolygonIds != null && portalPolygonIds.Contains(kvp.Key)) {
                    var portalIndices = new List<ushort>();
                    for (int i = 0; i < poly.VertexIds.Count; i++) {
                        ushort vertId = (ushort)poly.VertexIds[i];
                        if (!cellStruct.VertexArray.Vertices.TryGetValue(vertId, out var vtx)) continue;
                        var key = (vertId, (ushort)0);
                        if (!UVLookup.TryGetValue(key, out var idx)) {
                            idx = (ushort)vertices.Count;
                            vertices.Add(new VertexPositionNormalTexture(
                                vtx.Origin, Vector3.Normalize(vtx.Normal), Vector2.Zero));
                            UVLookup[key] = idx;
                        }
                        portalIndices.Add(idx);
                    }
                    for (int i = 2; i < portalIndices.Count; i++) {
                        portalOccluderIndices.Add(portalIndices[i]);
                        portalOccluderIndices.Add(portalIndices[i - 1]);
                        portalOccluderIndices.Add(portalIndices[0]);
                    }
                    continue;
                }

                // EnvCell polygons reference surfaces from the EnvCell's Surfaces list
                int surfaceIdx = poly.PosSurface;
                bool useNegSurface = false;

                if (surfaceIdx < 0 || surfaceIdx >= surfaceIds.Count) continue;

                var surfaceId = surfaceIds[surfaceIdx];

                bool isSolid;
                (byte[] Data, int Width, int Height, TextureFormat Format, PixelFormat? UploadPixelFormat, PixelType? UploadPixelType, uint PaletteId)? texResult;

                if (_dats.TryGet<Surface>(surfaceId, out var surface)) {
                    isSolid = surface.Type.HasFlag(SurfaceType.Base1Solid);
                    texResult = LoadTextureData(surfaceId, surface, isSolid, poly.Stippling);
                }
                else if (CustomTextureResolver?.Invoke(surfaceId) is { } customTex) {
                    isSolid = false;
                    texResult = (customTex.Data, customTex.Width, customTex.Height,
                        TextureFormat.RGBA8, PixelFormat.Rgba, null, 0);
                }
                else {
                    continue;
                }

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
                TexturesByFormat = texturesByFormat,
                PortalOccluderIndices = portalOccluderIndices.Count > 0 ? portalOccluderIndices.ToArray() : null
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
                    textureData = TextureHelpers.ConvertBgraToRgba(renderSurface.SourceData);
                    uploadPixelFormat = PixelFormat.Rgba;
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
        public int ProcessUploads(int maxPerFrame = 4) {
            int processed = 0;
            while (processed < maxPerFrame && _uploadQueue.TryDequeue(out var batch)) {
                // Building interior cells are small and always needed; only cap dungeon-only landblocks
                if (batch.IsDungeonOnly && LoadedDungeonCellCount >= MaxLoadedDungeonCells) {
                    Console.WriteLine($"[EnvCellMgr] Dungeon cell cap ({MaxLoadedDungeonCells}) reached ({LoadedDungeonCellCount} loaded), skipping upload for LB 0x{batch.LandblockKey:X4}");
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
                EnvCellRenderData? renderData;
                lock (_cellLock) {
                    if (!_gpuCache.TryGetValue(prepared.GpuKey, out renderData)) {
                        renderData = null;
                    }
                }

                if (renderData == null) {
                    renderData = UploadCellStructToGpu(prepared.MeshData);
                    if (renderData == null) continue;
                    lock (_cellLock) { _gpuCache[prepared.GpuKey] = renderData; }
                }

                Matrix4x4.Invert(prepared.WorldTransform, out var inverseTransform);

                var loadedCell = new LoadedEnvCell {
                    GpuKey = prepared.GpuKey,
                    WorldTransform = prepared.WorldTransform,
                    WorldPosition = prepared.WorldPosition,
                    CellId = prepared.CellId,
                    EnvironmentId = prepared.EnvironmentId,
                    SurfaceCount = prepared.SurfaceCount,
                    LoadedLandblockKey = prepared.LoadedLandblockKey,
                    IsBuilding = prepared.IsBuilding,
                    Portals = prepared.Portals,
                    ClipPlanes = prepared.ClipPlanes,
                    InverseWorldTransform = inverseTransform,
                    LocalBoundsMin = prepared.LocalBoundsMin,
                    LocalBoundsMax = prepared.LocalBoundsMax
                };
                cells.Add(loadedCell);
                lock (_cellLock) { _cellLookup[prepared.CellId] = loadedCell; }
            }

            if (cells.Count > 0) {
                lock (_cellLock) { _loadedCells[batch.LandblockKey] = cells; }
                if (batch.IsDungeonOnly)
                    _dungeonOnlyLandblocks.Add(batch.LandblockKey);
                else
                    _dungeonOnlyLandblocks.Remove(batch.LandblockKey);
                if (batch.HasMixedCells)
                    _mixedLandblocks.Add(batch.LandblockKey);
                else
                    _mixedLandblocks.Remove(batch.LandblockKey);
                System.Diagnostics.Debug.WriteLine($"[EnvCellMgr] GPU upload LB 0x{batch.LandblockKey:X4}: {cells.Count} cells, {_gpuCache.Count} GPU entries");
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

            // Upload portal occluder index buffer (depth-only pass for building cells)
            uint portalOccluderIBO = 0;
            int portalOccluderIndexCount = 0;
            if (mesh.PortalOccluderIndices != null && mesh.PortalOccluderIndices.Length > 0) {
                gl.GenBuffers(1, out portalOccluderIBO);
                gl.BindBuffer(GLEnum.ElementArrayBuffer, portalOccluderIBO);
                fixed (ushort* iptr = mesh.PortalOccluderIndices) {
                    gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(mesh.PortalOccluderIndices.Length * sizeof(ushort)), iptr, GLEnum.StaticDraw);
                }
                portalOccluderIndexCount = mesh.PortalOccluderIndices.Length;
            }

            gl.BindVertexArray(0);

            foreach (var atlas in localAtlases.Values)
                atlas.FlushMipmaps();

            return new EnvCellRenderData {
                VAO = vao,
                VBO = vbo,
                Batches = renderBatches,
                LocalAtlases = localAtlases,
                PortalOccluderIBO = portalOccluderIBO,
                PortalOccluderIndexCount = portalOccluderIndexCount
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

            // Use pre-computed visibility from ComputeVisibility() if available,
            // otherwise compute it now (backwards-compat).
            var visibility = LastVisibilityResult ?? GetVisibleCells(camera.Position, frustum);

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);

            _shader.Bind();
            _shader.SetUniform("uViewProjection", viewProjection);
            _shader.SetUniform("uCameraPosition", camera.Position);
            _shader.SetUniform("uLightDirection", Vector3.Normalize(lightDirection));
            _shader.SetUniform("uAmbientIntensity", ambientIntensity);
            _shader.SetUniform("uSpecularPower", specularPower);

            foreach (var list in _cellGroupBuffer.Values) list.Clear();
            foreach (var list in _buildingCellGroupBuffer.Values) list.Clear();

            // Determine which landblock the camera is inside (if any) for scoped portal filtering
            ushort? cameraLbKey = visibility?.CameraCell?.LoadedLandblockKey;
            bool cameraInDungeon = cameraLbKey.HasValue && _dungeonOnlyLandblocks.Contains(cameraLbKey.Value);

            foreach (var kvp in _loadedCells) {
                bool lbIsDungeon = _dungeonOnlyLandblocks.Contains(kvp.Key);
                bool lbIsMixed = _mixedLandblocks.Contains(kvp.Key);

                if (lbIsDungeon) {
                    if (!ShowDungeonCells) continue;
                    if (FocusedDungeonLB.HasValue && kvp.Key != FocusedDungeonLB.Value && kvp.Key != PreviewLandblockKey) continue;
                } else if (!lbIsMixed) {
                    bool cameraInThisBuilding = visibility != null && !cameraInDungeon && kvp.Key == cameraLbKey;
                    if (!cameraInThisBuilding && !AlwaysShowBuildingInteriors) continue;
                } else {
                    // Mixed landblocks (building + dungeon cells): the portal visibility
                    // system handles traversal between connected cells seamlessly.
                    // Show when camera is inside (portal filter) or when force-showing interiors.
                    bool cameraInsideMixed = visibility != null && kvp.Key == cameraLbKey;
                    if (!cameraInsideMixed && !AlwaysShowBuildingInteriors && !ShowDungeonCells) continue;
                }

                bool usePortalFilter = visibility != null && kvp.Key == cameraLbKey;
                bool isPreview = kvp.Key == PreviewLandblockKey;

                foreach (var cell in kvp.Value) {
                    if (isPreview) {
                        // Preview cells always render — no frustum or portal culling
                    } else if (usePortalFilter) {
                        if (!visibility!.VisibleCellIds.Contains(cell.CellId)) continue;
                    } else {
                        var cellBounds = new BoundingBox(
                            cell.WorldPosition - new Vector3(CellBoundsRadius),
                            cell.WorldPosition + new Vector3(CellBoundsRadius));
                        if (!frustum.IntersectsBoundingBox(cellBounds)) continue;
                    }

                    var targetBuffer = cell.IsBuilding ? _buildingCellGroupBuffer : _cellGroupBuffer;
                    if (!targetBuffer.TryGetValue(cell.GpuKey, out var list)) {
                        list = new List<Matrix4x4>();
                        targetBuffer[cell.GpuKey] = list;
                    }
                    list.Add(cell.WorldTransform);
                }
            }

            // Build a consolidated draw plan: collect all instance transforms into one
            // buffer to avoid per-group BufferSubData calls (major win on ANGLE/D3D11).
            _envCellDrawPlan.Clear();
            int totalFloats = 0;

            void CollectGroups(Dictionary<EnvCellGpuKey, List<Matrix4x4>> groups) {
                foreach (var (gpuKey, transforms) in groups) {
                    if (transforms.Count == 0) continue;
                    if (!_gpuCache.TryGetValue(gpuKey, out var renderData)) continue;
                    if (renderData.Batches.Count == 0) continue;

                    int offset = totalFloats;
                    int needed = totalFloats + transforms.Count * 16;
                    EnsureInstanceUploadBuffer(needed);

                    for (int i = 0; i < transforms.Count; i++) {
                        var t = transforms[i];
                        int o = totalFloats;
                        _instanceUploadBuffer[o +  0] = t.M11; _instanceUploadBuffer[o +  1] = t.M12;
                        _instanceUploadBuffer[o +  2] = t.M13; _instanceUploadBuffer[o +  3] = t.M14;
                        _instanceUploadBuffer[o +  4] = t.M21; _instanceUploadBuffer[o +  5] = t.M22;
                        _instanceUploadBuffer[o +  6] = t.M23; _instanceUploadBuffer[o +  7] = t.M24;
                        _instanceUploadBuffer[o +  8] = t.M31; _instanceUploadBuffer[o +  9] = t.M32;
                        _instanceUploadBuffer[o + 10] = t.M33; _instanceUploadBuffer[o + 11] = t.M34;
                        _instanceUploadBuffer[o + 12] = t.M41; _instanceUploadBuffer[o + 13] = t.M42;
                        _instanceUploadBuffer[o + 14] = t.M43; _instanceUploadBuffer[o + 15] = t.M44;
                        totalFloats += 16;
                    }

                    _envCellDrawPlan.Add(new EnvCellDrawEntry {
                        VAO = renderData.VAO,
                        Batches = renderData.Batches,
                        InstanceCount = transforms.Count,
                        BufferFloatOffset = offset
                    });
                }
            }

            CollectGroups(_buildingCellGroupBuffer);
            CollectGroups(_cellGroupBuffer);

            if (totalFloats == 0) {
                gl.UseProgram(0);
                gl.Disable(EnableCap.Blend);
                return;
            }

            // Single buffer upload for all instance transforms
            if (_instanceVBO == 0) {
                gl.GenBuffers(1, out _instanceVBO);
            }

            gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVBO);
            unsafe {
                fixed (float* ptr = _instanceUploadBuffer) {
                    if (totalFloats > _instanceBufferCapacity) {
                        int newCapacity = Math.Max(totalFloats, 256);
                        newCapacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newCapacity);
                        _instanceBufferCapacity = newCapacity;
                        gl.BufferData(GLEnum.ArrayBuffer, (nuint)(newCapacity * sizeof(float)), ptr, GLEnum.DynamicDraw);
                    }
                    else {
                        gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(totalFloats * sizeof(float)), ptr);
                    }
                }
            }

            // Execute draw plan
            foreach (var entry in _envCellDrawPlan) {
                gl.BindVertexArray(entry.VAO);

                int byteOffset = entry.BufferFloatOffset * sizeof(float);
                gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVBO);
                for (int i = 0; i < 4; i++) {
                    gl.EnableVertexAttribArray((uint)(3 + i));
                    unsafe {
                        gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false,
                            (uint)(16 * sizeof(float)), (void*)(byteOffset + i * 4 * sizeof(float)));
                    }
                    gl.VertexAttribDivisor((uint)(3 + i), 1);
                }

                foreach (var batch in entry.Batches) {
                    if (batch.TextureArray == null) continue;

                    try {
                        batch.TextureArray.Bind(0);
                        _shader.SetUniform("uTextureArray", 0);
                        _shader.SetUniform("uTextureIndex", (float)batch.TextureIndex);
                        gl.DisableVertexAttribArray(7);
                        gl.VertexAttrib1((uint)7, (float)batch.TextureIndex);
                        gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                        unsafe {
                            gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, (uint)entry.InstanceCount);
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[EnvCellMgr] Error rendering batch: {ex.Message}");
                    }
                }
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(EnableCap.Blend);
        }

        private void EnsureInstanceUploadBuffer(int requiredFloats) {
            if (_instanceUploadBuffer.Length >= requiredFloats) return;
            if (_instanceUploadBufferRented > 0)
                ArrayPool<float>.Shared.Return(_instanceUploadBuffer);
            int newSize = Math.Max(requiredFloats, 256);
            newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newSize);
            _instanceUploadBuffer = ArrayPool<float>.Shared.Rent(newSize);
            _instanceUploadBufferRented = newSize;
        }

        private unsafe void RenderBatchedEnvCell(GL gl, EnvCellRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (instanceTransforms.Count == 0 || renderData.Batches.Count == 0) return;

            int requiredFloats = instanceTransforms.Count * 16;

            // Ensure CPU-side upload buffer is large enough
            if (_instanceUploadBuffer.Length < requiredFloats) {
                if (_instanceUploadBufferRented > 0)
                    ArrayPool<float>.Shared.Return(_instanceUploadBuffer);
                int newSize = Math.Max(requiredFloats, 256);
                newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newSize);
                _instanceUploadBuffer = ArrayPool<float>.Shared.Rent(newSize);
                _instanceUploadBufferRented = newSize;
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
                    _shader.SetUniform("uTextureIndex", (float)batch.TextureIndex);
                    // Shader reads texture layer from vertex attribute 7 (aTextureIndex). Set constant so we don't inherit stale value from static object draw.
                    gl.DisableVertexAttribArray(7);
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

        private unsafe void RenderPortalOccluders(GL gl, EnvCellRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (instanceTransforms.Count == 0) return;

            int requiredFloats = instanceTransforms.Count * 16;
            if (_instanceUploadBuffer.Length < requiredFloats) {
                if (_instanceUploadBufferRented > 0)
                    ArrayPool<float>.Shared.Return(_instanceUploadBuffer);
                int newSize = Math.Max(requiredFloats, 256);
                newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newSize);
                _instanceUploadBuffer = ArrayPool<float>.Shared.Rent(newSize);
                _instanceUploadBufferRented = newSize;
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

            gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVBO);
            fixed (float* ptr = _instanceUploadBuffer) {
                gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(requiredFloats * sizeof(float)), ptr);
            }

            gl.BindVertexArray(renderData.VAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVBO);
            for (int i = 0; i < 4; i++) {
                gl.EnableVertexAttribArray((uint)(3 + i));
                gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false, (uint)(16 * sizeof(float)), (void*)(i * 4 * sizeof(float)));
                gl.VertexAttribDivisor((uint)(3 + i), 1);
            }

            gl.BindBuffer(GLEnum.ElementArrayBuffer, renderData.PortalOccluderIBO);
            gl.DrawElementsInstanced(GLEnum.Triangles, (uint)renderData.PortalOccluderIndexCount, GLEnum.UnsignedShort, null, (uint)instanceTransforms.Count);
            gl.BindVertexArray(0);
        }

        #endregion

        #region Landblock Management

        /// <summary>
        /// Unloads all dungeon cells for a landblock, releasing GPU resources where no longer referenced.
        /// Must be called on the GL thread.
        /// </summary>
        public void UnloadLandblock(ushort lbKey) {
            List<LoadedEnvCell>? cells;
            var keysToRemove = new HashSet<EnvCellGpuKey>();

            lock (_cellLock) {
                if (!_loadedCells.TryGetValue(lbKey, out cells)) return;

                foreach (var cell in cells) {
                    keysToRemove.Add(cell.GpuKey);
                }

                foreach (var cell in cells) {
                    _cellLookup.Remove(cell.CellId);
                    if (_lastCameraCell?.CellId == cell.CellId)
                        _lastCameraCell = null;
                }

                _loadedCells.Remove(lbKey);
                _dungeonOnlyLandblocks.Remove(lbKey);
                _mixedLandblocks.Remove(lbKey);

                foreach (var otherCells in _loadedCells.Values) {
                    foreach (var cell in otherCells) {
                        keysToRemove.Remove(cell.GpuKey);
                    }
                }
            }

            // Release unreferenced GPU resources (GL calls outside lock)
            var gl = _renderer.GraphicsDevice.GL;
            foreach (var key in keysToRemove) {
                EnvCellRenderData? renderData;
                lock (_cellLock) {
                    if (!_gpuCache.TryGetValue(key, out renderData)) continue;
                    _gpuCache.Remove(key);
                }
                if (renderData.VAO != 0) gl.DeleteVertexArray(renderData.VAO);
                if (renderData.VBO != 0) gl.DeleteBuffer(renderData.VBO);
                if (renderData.PortalOccluderIBO != 0) gl.DeleteBuffer(renderData.PortalOccluderIBO);
                foreach (var batch in renderData.Batches) {
                    if (batch.IBO != 0) gl.DeleteBuffer(batch.IBO);
                }
                foreach (var atlas in renderData.LocalAtlases.Values) {
                    atlas.Dispose();
                }
            }
        }

        /// <summary>
        /// Returns true if the given landblock has any loaded dungeon cells.
        /// </summary>
        public bool HasLoadedCells(ushort lbKey) => _loadedCells.ContainsKey(lbKey);

        /// <summary>
        /// Returns true if the given landblock is a dungeon-only landblock (vs building interiors).
        /// </summary>
        public bool IsDungeonLandblock(ushort lbKey) => _dungeonOnlyLandblocks.Contains(lbKey);

        /// <summary>
        /// Returns all landblock keys that currently have loaded dungeon cells.
        /// </summary>
        public IEnumerable<ushort> GetLoadedLandblockKeys() => _loadedCells.Keys;

        /// <summary>
        /// Returns the loaded cells for a specific landblock, or null if not loaded.
        /// </summary>
        public List<LoadedEnvCell>? GetLoadedCellsForLandblock(ushort lbKey) =>
            _loadedCells.TryGetValue(lbKey, out var cells) ? cells : null;

        /// <summary>
        /// Finds a loaded cell by its full cell ID (e.g. 0x01D90105), or null if not loaded.
        /// </summary>
        public LoadedEnvCell? FindCell(uint fullCellId) =>
            _cellLookup.TryGetValue(fullCellId, out var cell) ? cell : null;

        #endregion

        #region Portal Visibility

        private const float PointInCellEpsilon = 0.01f;
        private const int CellSwitchGraceFrameCount = 3;

        /// <summary>
        /// Tests whether a world-space point is inside a loaded cell using the cell's
        /// local-space AABB computed from CellStruct vertices. The point is transformed
        /// into cell-local space and checked against the vertex bounding box.
        /// </summary>
        public static bool PointInCell(Vector3 worldPoint, LoadedEnvCell cell) {
            if (cell.LocalBoundsMin.X >= cell.LocalBoundsMax.X) return false;

            var localPoint = Vector3.Transform(worldPoint, cell.InverseWorldTransform);

            return localPoint.X >= cell.LocalBoundsMin.X - PointInCellEpsilon &&
                   localPoint.X <= cell.LocalBoundsMax.X + PointInCellEpsilon &&
                   localPoint.Y >= cell.LocalBoundsMin.Y - PointInCellEpsilon &&
                   localPoint.Y <= cell.LocalBoundsMax.Y + PointInCellEpsilon &&
                   localPoint.Z >= cell.LocalBoundsMin.Z - PointInCellEpsilon &&
                   localPoint.Z <= cell.LocalBoundsMax.Z + PointInCellEpsilon;
        }

        /// <summary>
        /// Finds the cell the camera is currently inside, with hysteresis to avoid flicker.
        /// Returns null if the camera is not inside any loaded cell.
        /// </summary>
        public LoadedEnvCell? FindCameraCell(Vector3 cameraPos) {
            // Fast path: check cached cell first
            if (_lastCameraCell != null && PointInCell(cameraPos, _lastCameraCell))
                return _lastCameraCell;

            // Check neighbors of the last cell (one-hop through portals)
            if (_lastCameraCell != null) {
                uint lbMask = _lastCameraCell.CellId & 0xFFFF0000;
                foreach (var portal in _lastCameraCell.Portals) {
                    if (portal.OtherCellId == 0xFFFF) continue;
                    uint neighborId = lbMask | portal.OtherCellId;
                    if (_cellLookup.TryGetValue(neighborId, out var neighbor) && PointInCell(cameraPos, neighbor)) {
                        _lastCameraCell = neighbor;
                        _cellSwitchGraceFrames = CellSwitchGraceFrameCount;
                        return neighbor;
                    }
                }
            }

            // Brute force: check all loaded cells
            foreach (var kvp in _loadedCells) {
                bool isDungeon = _dungeonOnlyLandblocks.Contains(kvp.Key);
                if (isDungeon && FocusedDungeonLB.HasValue && kvp.Key != FocusedDungeonLB.Value) continue;

                foreach (var cell in kvp.Value) {
                    if (PointInCell(cameraPos, cell)) {
                        _lastCameraCell = cell;
                        _cellSwitchGraceFrames = CellSwitchGraceFrameCount;
                        return cell;
                    }
                }
            }

            // Grace period: keep previous cell for a few frames to prevent pop-in
            if (_lastCameraCell != null && _cellSwitchGraceFrames > 0) {
                _cellSwitchGraceFrames--;
                return _lastCameraCell;
            }

            // Diagnostic: log the nearest building cell when camera is close but not inside
            if (++_diagThrottle % 120 == 0) {
                LoadedEnvCell? nearest = null;
                float nearestDist = float.MaxValue;
                foreach (var kvp in _loadedCells) {
                    if (_dungeonOnlyLandblocks.Contains(kvp.Key)) continue;
                    foreach (var cell in kvp.Value) {
                        float dist = Vector3.Distance(cameraPos, cell.WorldPosition);
                        if (dist < nearestDist) {
                            nearestDist = dist;
                            nearest = cell;
                        }
                    }
                }
                if (nearest != null && nearestDist < 50f) {
                    var localCam = Vector3.Transform(cameraPos, nearest.InverseWorldTransform);
                    Console.WriteLine($"[EnvCellMgr] DIAG: cam=({cameraPos.X:F1},{cameraPos.Y:F1},{cameraPos.Z:F1}) " +
                        $"nearestBuildingCell=0x{nearest.CellId:X8} dist={nearestDist:F1} " +
                        $"cellWorldPos=({nearest.WorldPosition.X:F1},{nearest.WorldPosition.Y:F1},{nearest.WorldPosition.Z:F1}) " +
                        $"camLocal=({localCam.X:F1},{localCam.Y:F1},{localCam.Z:F1}) " +
                        $"localAABB=({nearest.LocalBoundsMin.X:F1},{nearest.LocalBoundsMin.Y:F1},{nearest.LocalBoundsMin.Z:F1})->({nearest.LocalBoundsMax.X:F1},{nearest.LocalBoundsMax.Y:F1},{nearest.LocalBoundsMax.Z:F1})");
                }
            }

            _lastCameraCell = null;
            return null;
        }

        /// <summary>
        /// Performs portal-based visibility traversal from the camera position.
        /// Returns a VisibilityResult if the camera is inside a cell, or null if the
        /// camera is outside all cells (caller should fall back to frustum culling).
        /// </summary>
        public VisibilityResult? GetVisibleCells(Vector3 cameraPos, Frustum frustum) {
            var cameraCell = FindCameraCell(cameraPos);
            if (cameraCell == null) return null;

            var result = new VisibilityResult { CameraCell = cameraCell };
            var visited = new HashSet<uint>();
            var queue = new Queue<LoadedEnvCell>();

            visited.Add(cameraCell.CellId);
            result.VisibleCellIds.Add(cameraCell.CellId);
            queue.Enqueue(cameraCell);

            uint lbMask = cameraCell.CellId & 0xFFFF0000;

            while (queue.Count > 0) {
                var cell = queue.Dequeue();

                for (int i = 0; i < cell.Portals.Count; i++) {
                    var portal = cell.Portals[i];

                    if (portal.OtherCellId == 0xFFFF) {
                        result.HasExitPortalVisible = true;
                        continue;
                    }

                    uint neighborId = lbMask | portal.OtherCellId;
                    if (visited.Contains(neighborId)) continue;

                    if (!_cellLookup.TryGetValue(neighborId, out var neighbor)) continue;

                    // Portal-side test: check if camera is on the correct side to see through
                    if (i < cell.ClipPlanes.Count) {
                        var plane = cell.ClipPlanes[i];
                        var localCam = Vector3.Transform(cameraPos, cell.InverseWorldTransform);
                        float dot = Vector3.Dot(plane.Normal, localCam) + plane.D;

                        // Camera must be on the InsideSide to look through this portal
                        if (plane.InsideSide == 0 && dot < -PointInCellEpsilon) continue;
                        if (plane.InsideSide == 1 && dot > PointInCellEpsilon) continue;
                    }

                    // Frustum test: check if the neighbor cell's bounding box is visible
                    var neighborBounds = new BoundingBox(
                        neighbor.WorldPosition - new Vector3(CellBoundsRadius),
                        neighbor.WorldPosition + new Vector3(CellBoundsRadius));
                    if (!frustum.IntersectsBoundingBox(neighborBounds)) continue;

                    visited.Add(neighborId);
                    result.VisibleCellIds.Add(neighborId);
                    queue.Enqueue(neighbor);
                }
            }

            return result;
        }

        /// <summary>
        /// Pre-compute portal visibility for the current frame. Call before Render()
        /// so that the caller can inspect <see cref="LastVisibilityResult"/> to decide
        /// whether a depth-clear is needed between terrain and EnvCell rendering.
        /// </summary>
        public void ComputeVisibility(Matrix4x4 viewProjection, ICamera camera) {
            if (_loadedCells.Count == 0) {
                LastVisibilityResult = null;
                return;
            }
            var frustum = new Frustum(viewProjection);
            LastVisibilityResult = GetVisibleCells(camera.Position, frustum);
        }

        /// <summary>
        /// Exposes the last visibility result for external consumers (e.g. GameScene static filtering).
        /// Updated each frame by <see cref="ComputeVisibility"/> or <see cref="Render"/>.
        /// </summary>
        public VisibilityResult? LastVisibilityResult { get; private set; }

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

        /// <summary>
        /// Releases all GPU resources and clears all caches without disposing shared infrastructure (instance VBO, shaders).
        /// The manager remains usable after this call -- new data will be loaded on demand.
        /// Must be called on the GL thread.
        /// </summary>
        public void ClearAll() {
            var gl = _renderer.GraphicsDevice.GL;
            foreach (var renderData in _gpuCache.Values) {
                if (renderData.VAO != 0) gl.DeleteVertexArray(renderData.VAO);
                if (renderData.VBO != 0) gl.DeleteBuffer(renderData.VBO);
                if (renderData.PortalOccluderIBO != 0) gl.DeleteBuffer(renderData.PortalOccluderIBO);
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
            _mixedLandblocks.Clear();
            _cellLookup.Clear();
            _lastCameraCell = null;
            _environmentCache.Clear();
        }

        public void Dispose() {
            ClearAll();
            var gl = _renderer.GraphicsDevice.GL;
            if (_instanceVBO != 0) gl.DeleteBuffer(_instanceVBO);
            if (_instanceUploadBufferRented > 0) {
                ArrayPool<float>.Shared.Return(_instanceUploadBuffer);
                _instanceUploadBuffer = Array.Empty<float>();
                _instanceUploadBufferRented = 0;
            }
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
        private readonly string _portalKey;

        public EnvCellGpuKey(uint environmentId, uint cellStructure, List<uint> surfaceIds, HashSet<ushort>? portalPolygonIds = null) {
            EnvironmentId = environmentId;
            CellStructure = cellStructure;
            _surfaceKey = string.Join(",", surfaceIds);
            _portalKey = portalPolygonIds != null && portalPolygonIds.Count > 0
                ? string.Join(",", portalPolygonIds.OrderBy(id => id))
                : "";
        }

        public bool Equals(EnvCellGpuKey other) =>
            EnvironmentId == other.EnvironmentId &&
            CellStructure == other.CellStructure &&
            _surfaceKey == other._surfaceKey &&
            _portalKey == other._portalKey;

        public override int GetHashCode() => HashCode.Combine(EnvironmentId, CellStructure, _surfaceKey, _portalKey);
    }

    /// <summary>
    /// GPU resource data for a rendered CellStruct instance.
    /// </summary>
    public class EnvCellRenderData {
        public uint VAO { get; set; }
        public uint VBO { get; set; }
        public List<RenderBatch> Batches { get; set; } = new();
        public Dictionary<(int Width, int Height, TextureFormat Format), TextureAtlasManager> LocalAtlases { get; set; } = new();
        /// <summary>IBO for portal polygon depth-only occluders. 0 if none.</summary>
        public uint PortalOccluderIBO { get; set; }
        /// <summary>Number of indices in the portal occluder IBO.</summary>
        public int PortalOccluderIndexCount { get; set; }
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
        /// <summary>True if this cell belongs to a building interior; false for dungeon cells.</summary>
        public bool IsBuilding { get; set; }

        /// <summary>Portal connections to neighboring cells (extracted from EnvCell.CellPortals).</summary>
        public List<CellPortalInfo> Portals { get; set; } = new();
        /// <summary>Clip planes derived from portal polygons, used for portal-side visibility checks.</summary>
        public List<PortalClipPlane> ClipPlanes { get; set; } = new();
        /// <summary>Inverse of WorldTransform, cached for world-to-local transforms.</summary>
        public Matrix4x4 InverseWorldTransform { get; set; }
        /// <summary>Local-space AABB min, computed from CellStruct vertices for point-in-cell.</summary>
        public Vector3 LocalBoundsMin { get; set; }
        /// <summary>Local-space AABB max, computed from CellStruct vertices for point-in-cell.</summary>
        public Vector3 LocalBoundsMax { get; set; }
    }

    /// <summary>
    /// Portal connection info stored per LoadedEnvCell, extracted from dat CellPortal data.
    /// </summary>
    public struct CellPortalInfo {
        /// <summary>Connected cell ID (lower 16 bits within landblock). 0xFFFF = exit to outdoors.</summary>
        public ushort OtherCellId;
        /// <summary>Polygon ID in the CellStruct that forms the portal opening.</summary>
        public ushort PolygonId;
        /// <summary>Which side of the portal plane is "outward" from this cell (0 or 1).</summary>
        public int PortalSide;
    }

    /// <summary>
    /// A clip plane derived from a portal polygon, used for point-in-cell containment testing.
    /// The plane equation is in cell-local space: Normal.X*x + Normal.Y*y + Normal.Z*z + D = 0.
    /// </summary>
    public struct PortalClipPlane {
        public Vector3 Normal;
        public float D;
        /// <summary>
        /// The side of the plane that is "inside" the cell.
        /// 0 = positive half-space (dot >= 0), 1 = negative half-space (dot &lt;= 0).
        /// </summary>
        public int InsideSide;
    }

    /// <summary>
    /// Result of portal-based visibility traversal.
    /// </summary>
    public class VisibilityResult {
        /// <summary>Set of full cell IDs (e.g. 0x01D90105) that should be rendered.</summary>
        public HashSet<uint> VisibleCellIds { get; set; } = new();
        /// <summary>True if any exit portal (OtherCellId == 0xFFFF) was visible — outdoor terrain should render.</summary>
        public bool HasExitPortalVisible { get; set; }
        /// <summary>The cell the camera is currently inside.</summary>
        public LoadedEnvCell? CameraCell { get; set; }
    }

    /// <summary>
    /// CPU-prepared data for a batch of EnvCells from a single landblock, ready for GPU upload.
    /// </summary>
    public class PreparedEnvCellBatch {
        public ushort LandblockKey { get; set; }
        public bool IsDungeonOnly { get; set; }
        /// <summary>True when the landblock contains both building AND dungeon cells.</summary>
        public bool HasMixedCells { get; set; }
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
        /// <summary>
        /// Parallel to DungeonStaticObjects: the full cell ID each static belongs to.
        /// </summary>
        public List<uint> DungeonStaticParentCells { get; set; } = new();
        /// <summary>
        /// Parallel to BuildingStaticObjects: the full cell ID each static belongs to.
        /// </summary>
        public List<uint> BuildingStaticParentCells { get; set; } = new();
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
        /// <summary>True if this cell belongs to a building interior (SeenOutside); false for dungeon cells.</summary>
        public bool IsBuilding { get; set; }
        /// <summary>Portal connections extracted during CPU preparation.</summary>
        public List<CellPortalInfo> Portals { get; set; } = new();
        /// <summary>Clip planes extracted during CPU preparation.</summary>
        public List<PortalClipPlane> ClipPlanes { get; set; } = new();
        /// <summary>Local-space AABB min, computed from CellStruct vertices.</summary>
        public Vector3 LocalBoundsMin { get; set; }
        /// <summary>Local-space AABB max, computed from CellStruct vertices.</summary>
        public Vector3 LocalBoundsMax { get; set; }
    }

    /// <summary>
    /// CPU-prepared mesh data from a CellStruct, ready for GPU upload.
    /// </summary>
    public class PreparedCellStructMesh {
        public VertexPositionNormalTexture[] Vertices { get; set; } = Array.Empty<VertexPositionNormalTexture>();
        public List<PreparedBatch> Batches { get; set; } = new();
        public Dictionary<(int Width, int Height, TextureFormat Format), List<PreparedTexture>> TexturesByFormat { get; set; } = new();
        /// <summary>
        /// Triangle indices for portal polygons, used as depth-only occluders to prevent
        /// terrain from showing through portal openings in building interior cells.
        /// Null if no portal polygons exist for this mesh.
        /// </summary>
        public ushort[]? PortalOccluderIndices { get; set; }
    }

    #endregion
}

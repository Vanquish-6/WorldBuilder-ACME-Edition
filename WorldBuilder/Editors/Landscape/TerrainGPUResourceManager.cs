using Chorizite.Core.Render.Enums;
using Chorizite.Core.Render.Vertex;
using Chorizite.OpenGLSDLBackend;
using System;
using System.Collections.Generic;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Holds CPU-generated terrain geometry ready for GPU upload.
    /// Created on a background thread, consumed on the GL thread.
    /// </summary>
    public class PreparedChunkData {
        public TerrainChunk Chunk { get; init; } = null!;
        public VertexLandscape[] Vertices { get; init; } = Array.Empty<VertexLandscape>();
        public uint[] Indices { get; init; } = Array.Empty<uint>();
        public int ActualVertexCount { get; init; }
        public int ActualIndexCount { get; init; }
        public Dictionary<uint, LandblockRenderData> LandblockOffsets { get; init; } = new();
    }

    /// <summary>
    /// Manages GPU resources for terrain chunks with landblock-level update support
    /// </summary>
    public class TerrainGPUResourceManager : IDisposable {
        private readonly OpenGLRenderer _renderer;
        private readonly Dictionary<ulong, ChunkRenderData> _renderData;

        // Reusable buffers for landblock updates (GL thread only)
        private VertexLandscape[] _landblockVertexBuffer;
        private uint[] _landblockIndexBuffer;

        public TerrainGPUResourceManager(OpenGLRenderer renderer, int estimatedChunkCount = 256) {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _renderData = new Dictionary<ulong, ChunkRenderData>(estimatedChunkCount);

            // Buffers for single landblock updates
            _landblockVertexBuffer = new VertexLandscape[TerrainGeometryGenerator.VerticesPerLandblock];
            _landblockIndexBuffer = new uint[TerrainGeometryGenerator.IndicesPerLandblock];
        }

        /// <summary>
        /// Phase 1 (CPU-only, thread-safe): Generates terrain geometry for a chunk.
        /// Allocates its own buffers so it can run on any thread without contention.
        /// </summary>
        public static PreparedChunkData PrepareChunkGeometry(TerrainChunk chunk, TerrainSystem terrainSystem) {
            var maxVertexCount = (int)(chunk.ActualLandblockCountX * chunk.ActualLandblockCountY *
                                       TerrainGeometryGenerator.VerticesPerLandblock);
            var maxIndexCount = (int)(chunk.ActualLandblockCountX * chunk.ActualLandblockCountY *
                                      TerrainGeometryGenerator.IndicesPerLandblock);

            var vertices = new VertexLandscape[maxVertexCount];
            var indices = new uint[maxIndexCount];

            TerrainGeometryGenerator.GenerateChunkGeometry(
                chunk, terrainSystem,
                vertices.AsSpan(0, maxVertexCount),
                indices.AsSpan(0, maxIndexCount),
                out int actualVertexCount, out int actualIndexCount);

            // Build landblock offsets (CPU-only)
            var offsets = BuildLandblockOffsetsStatic(chunk, terrainSystem);

            return new PreparedChunkData {
                Chunk = chunk,
                Vertices = vertices,
                Indices = indices,
                ActualVertexCount = actualVertexCount,
                ActualIndexCount = actualIndexCount,
                LandblockOffsets = offsets
            };
        }

        /// <summary>
        /// Phase 2 (GL thread only): Uploads prepared geometry to GPU buffers.
        /// </summary>
        public void UploadChunkToGPU(PreparedChunkData prepared) {
            var chunkId = prepared.Chunk.GetChunkId();

            // Dispose old resources if they exist
            if (_renderData.TryGetValue(chunkId, out var oldData)) {
                oldData.Dispose();
                _renderData.Remove(chunkId);
            }

            if (prepared.ActualVertexCount == 0 || prepared.ActualIndexCount == 0) return;

            var vb = _renderer.GraphicsDevice.CreateVertexBuffer(
                VertexLandscape.Size * prepared.ActualVertexCount,
                BufferUsage.Dynamic);
            vb.SetData(prepared.Vertices.AsSpan(0, prepared.ActualVertexCount));

            var ib = _renderer.GraphicsDevice.CreateIndexBuffer(
                sizeof(uint) * prepared.ActualIndexCount,
                BufferUsage.Dynamic);
            ib.SetData(prepared.Indices.AsSpan(0, prepared.ActualIndexCount));

            var va = _renderer.GraphicsDevice.CreateArrayBuffer(vb, VertexLandscape.Format);

            var renderData = new ChunkRenderData(vb, ib, va, prepared.ActualVertexCount, prepared.ActualIndexCount);

            // Apply pre-built landblock offsets
            foreach (var (key, value) in prepared.LandblockOffsets) {
                renderData.LandblockData[key] = value;
            }

            _renderData[chunkId] = renderData;
            prepared.Chunk.ClearDirty();
        }

        /// <summary>
        /// Creates GPU resources for an entire chunk (synchronous, for fallback/dirty updates).
        /// </summary>
        public void CreateChunkResources(TerrainChunk chunk, TerrainSystem terrainSystem) {
            var prepared = PrepareChunkGeometry(chunk, terrainSystem);
            UploadChunkToGPU(prepared);
        }

        /// <summary>
        /// Updates specific landblocks within a chunk
        /// </summary>
        public void UpdateLandblocks(TerrainChunk chunk, IEnumerable<uint> landblockIds, TerrainSystem terrainSystem, bool clearDirty = true) {

            var chunkId = chunk.GetChunkId();
            if (!_renderData.TryGetValue(chunkId, out var renderData)) {
                // Chunk doesn't exist yet, create it
                CreateChunkResources(chunk, terrainSystem);
                return;
            }

            foreach (var landblockId in landblockIds) {
                UpdateSingleLandblock(landblockId, chunk, renderData, terrainSystem);
            }

            if (clearDirty) {
                chunk.ClearDirty();
            }
        }

        /// <summary>
        /// Updates a single landblock's geometry in the GPU buffer
        /// </summary>
        private void UpdateSingleLandblock(uint landblockId, TerrainChunk chunk, ChunkRenderData renderData, TerrainSystem terrainSystem) {

            var landblockX = landblockId >> 8;
            var landblockY = landblockId & 0xFF;

            // Check if landblock is in this chunk
            if (landblockX < chunk.LandblockStartX || landblockX >= chunk.LandblockStartX + chunk.ActualLandblockCountX ||
                landblockY < chunk.LandblockStartY || landblockY >= chunk.LandblockStartY + chunk.ActualLandblockCountY) {
                return;
            }

            var landblockData = terrainSystem.GetLandblockTerrain((ushort)landblockId);
            if (landblockData == null) return;

            // Generate new geometry for this landblock
            uint vertexIndex = 0;
            uint indexIndex = 0;

            TerrainGeometryGenerator.GenerateLandblockGeometry(
                landblockX, landblockY, landblockId,
                landblockData, terrainSystem,
                ref vertexIndex, ref indexIndex,
                _landblockVertexBuffer,
                _landblockIndexBuffer
            );

            // Get the offset for this landblock in the chunk's buffer
            if (!renderData.LandblockData.TryGetValue(landblockId, out var lbData)) {
                // Landblock wasn't in the original chunk, skip update
                return;
            }

            // Adjust indices to be relative to the landblock's vertex offset
            var baseVertexIndex = (uint)lbData.VertexOffset;
            for (int i = 0; i < indexIndex; i++) {
                _landblockIndexBuffer[i] = _landblockIndexBuffer[i] - vertexIndex + baseVertexIndex;
            }

            // Update the GPU buffers at the correct offsets using SetSubData for partial updates
            renderData.VertexBuffer.SetSubData(
                _landblockVertexBuffer.AsSpan(0, (int)vertexIndex),
                lbData.VertexOffset * VertexLandscape.Size,
                0,
                (int)vertexIndex);
        }

        /// <summary>
        /// Builds the landblock offset map (CPU-only, thread-safe).
        /// </summary>
        private static Dictionary<uint, LandblockRenderData> BuildLandblockOffsetsStatic(TerrainChunk chunk, TerrainSystem terrainSystem) {
            var offsets = new Dictionary<uint, LandblockRenderData>();
            int currentVertexOffset = 0;
            int currentIndexOffset = 0;

            for (uint ly = 0; ly < chunk.ActualLandblockCountY; ly++) {
                for (uint lx = 0; lx < chunk.ActualLandblockCountX; lx++) {
                    var landblockX = chunk.LandblockStartX + lx;
                    var landblockY = chunk.LandblockStartY + ly;

                    if (landblockX >= TerrainDataManager.MapSize || landblockY >= TerrainDataManager.MapSize)
                        continue;

                    var landblockID = landblockX << 8 | landblockY;
                    var landblockData = terrainSystem.GetLandblockTerrain((ushort)landblockID);

                    if (landblockData == null) continue;

                    offsets[landblockID] = new LandblockRenderData {
                        LandblockId = landblockID,
                        VertexOffset = currentVertexOffset,
                        IndexOffset = currentIndexOffset,
                        VertexCount = TerrainGeometryGenerator.VerticesPerLandblock,
                        IndexCount = TerrainGeometryGenerator.IndicesPerLandblock
                    };

                    currentVertexOffset += TerrainGeometryGenerator.VerticesPerLandblock;
                    currentIndexOffset += TerrainGeometryGenerator.IndicesPerLandblock;
                }
            }

            return offsets;
        }

        public ChunkRenderData? GetRenderData(ulong chunkId) {
            return _renderData.TryGetValue(chunkId, out var data) ? data : null;
        }

        public bool HasRenderData(ulong chunkId) => _renderData.ContainsKey(chunkId);

        /// <summary>
        /// Disposes and removes GPU resources for a single chunk.
        /// </summary>
        public void DisposeChunkResources(ulong chunkId) {
            if (_renderData.TryGetValue(chunkId, out var data)) {
                data.Dispose();
                _renderData.Remove(chunkId);
            }
        }

        public void Dispose() {
            foreach (var data in _renderData.Values) {
                data.Dispose();
            }
            _renderData.Clear();
        }
    }
}
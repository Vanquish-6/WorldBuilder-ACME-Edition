using Chorizite.Core.Lib;
using DatReaderWriter.DBObjs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Manages terrain chunk metadata and streaming logic
    /// </summary>
    public class TerrainDataManager {
        public const uint MapSize = 254;
        public const uint LandblockLength = 192;
        public const uint LandblockEdgeCellCount = 8;
        public const float CellSize = 24.0f;

        private readonly TerrainSystem _terrainSystem;
        private readonly uint _chunkSizeInLandblocks;
        private readonly ChunkMetrics _metrics;
        private readonly Dictionary<ulong, TerrainChunk> _chunks = new();

        public TerrainDocument Terrain => _terrainSystem.TerrainDoc;
        private Region _region => _terrainSystem.Region;
        public uint ChunkSize => _chunkSizeInLandblocks;
        public ChunkMetrics Metrics => _metrics;

        public TerrainDataManager(TerrainSystem terrain, uint chunkSizeInLandblocks = 16) {
            _terrainSystem = terrain ?? throw new ArgumentNullException(nameof(terrain));
            _chunkSizeInLandblocks = Math.Max(1, chunkSizeInLandblocks);
            _metrics = ChunkMetrics.Calculate(_chunkSizeInLandblocks);
        }

        /// <summary>
        /// Determines which chunks should be loaded based on camera position
        /// </summary>
        public List<ulong> GetRequiredChunks(Vector3 cameraPosition) {
            var chunks = new List<ulong>();

            var chunkX = (uint)Math.Max(0, Math.Min(MapSize / _chunkSizeInLandblocks - 1, cameraPosition.X / _metrics.WorldSize));
            var chunkY = (uint)Math.Max(0, Math.Min(MapSize / _chunkSizeInLandblocks - 1, cameraPosition.Y / _metrics.WorldSize));

            var chunkRange = Math.Max(1u, MapSize / _chunkSizeInLandblocks);
            var maxChunksX = (MapSize + _chunkSizeInLandblocks - 1) / _chunkSizeInLandblocks;
            var maxChunksY = (MapSize + _chunkSizeInLandblocks - 1) / _chunkSizeInLandblocks;

            var minX = (uint)Math.Max(0, (int)chunkX - chunkRange);
            var maxX = Math.Min(maxChunksX - 1, chunkX + chunkRange);
            var minY = (uint)Math.Max(0, (int)chunkY - chunkRange);
            var maxY = Math.Min(maxChunksY - 1, chunkY + chunkRange);

            for (uint y = minY; y <= maxY; y++) {
                for (uint x = minX; x <= maxX; x++) {
                    chunks.Add(GetChunkId(x, y));
                }
            }

            return chunks;
        }

        /// <summary>
        /// Gets or creates chunk metadata
        /// </summary>
        public TerrainChunk GetOrCreateChunk(uint chunkX, uint chunkY) {
            var chunkId = GetChunkId(chunkX, chunkY);

            if (_chunks.TryGetValue(chunkId, out var chunk)) {
                return chunk;
            }

            chunk = CreateChunk(chunkX, chunkY);
            _chunks[chunkId] = chunk;
            return chunk;
        }

        private TerrainChunk CreateChunk(uint chunkX, uint chunkY) {
            var chunk = new TerrainChunk {
                ChunkX = chunkX,
                ChunkY = chunkY,
                LandblockStartX = chunkX * _chunkSizeInLandblocks,
                LandblockStartY = chunkY * _chunkSizeInLandblocks
            };

            // Calculate actual dimensions (handle map edges)
            chunk.ActualLandblockCountX = Math.Min(_chunkSizeInLandblocks, MapSize - chunk.LandblockStartX);
            chunk.ActualLandblockCountY = Math.Min(_chunkSizeInLandblocks, MapSize - chunk.LandblockStartY);

            // Calculate bounding box
            chunk.Bounds = CalculateChunkBounds(chunk);
            chunk.IsGenerated = true;

            return chunk;
        }

        private BoundingBox CalculateChunkBounds(TerrainChunk chunk) {
            // Use current implementation from GenerateChunk
            var minHeight = float.MaxValue;
            var maxHeight = float.MinValue;

            for (uint ly = 0; ly < chunk.ActualLandblockCountY; ly++) {
                for (uint lx = 0; lx < chunk.ActualLandblockCountX; lx++) {
                    var landblockX = chunk.LandblockStartX + lx;
                    var landblockY = chunk.LandblockStartY + ly;

                    if (landblockX >= MapSize || landblockY >= MapSize) continue;

                    var landblockID = landblockX << 8 | landblockY;
                    var landblockData = _terrainSystem.GetLandblockTerrain((ushort)landblockID);

                    if (landblockData != null) {
                        for (int i = 0; i < landblockData.Length; i++) {
                            var height = _region.LandDefs.LandHeightTable[landblockData[i].Height];
                            minHeight = Math.Min(minHeight, height);
                            maxHeight = Math.Max(maxHeight, height);
                        }
                    }
                }
            }

            return new BoundingBox(
                new Vector3(chunk.LandblockStartX * LandblockLength, chunk.LandblockStartY * LandblockLength, minHeight),
                new Vector3((chunk.LandblockStartX + chunk.ActualLandblockCountX) * LandblockLength,
                           (chunk.LandblockStartY + chunk.ActualLandblockCountY) * LandblockLength, maxHeight)
            );
        }

        public TerrainChunk? GetChunk(ulong chunkId) => _chunks.TryGetValue(chunkId, out var chunk) ? chunk : null;
        public TerrainChunk? GetChunkForLandblock(uint landblockX, uint landblockY) {
            var chunkX = landblockX / _chunkSizeInLandblocks;
            var chunkY = landblockY / _chunkSizeInLandblocks;
            return GetChunk(GetChunkId(chunkX, chunkY));
        }

        public IEnumerable<TerrainChunk> GetAllChunks() => _chunks.Values;

        /// <summary>
        /// Marks landblocks as dirty for incremental updates
        /// </summary>
        public void MarkLandblocksDirty(HashSet<ushort> landblockIds) {
            foreach (var lbId in landblockIds) {
                var chunk = GetChunkForLandblock((uint)lbId >> 8, (uint)lbId & 0xFF);
                chunk?.MarkDirty(lbId);
            }
        }

        public static ulong GetChunkId(uint chunkX, uint chunkY) => (ulong)chunkX << 32 | chunkY;

        // Height lookup utilities

        /// <summary>
        /// Gets the terrain height at a world position using AC-accurate triangle-based
        /// interpolation. Each 24x24 terrain cell is split into 2 triangles using a
        /// pseudo-random split direction matching the AC client's algorithm
        /// (see ACE LandblockStruct.ConstructPolygons).
        /// </summary>
        public float GetHeightAtPosition(float worldX, float worldY) {
            uint landblockX = (uint)Math.Floor(worldX / LandblockLength);
            uint landblockY = (uint)Math.Floor(worldY / LandblockLength);

            if (landblockX >= MapSize || landblockY >= MapSize) return 0f;

            var landblockID = landblockX << 8 | landblockY;
            var landblockData = _terrainSystem.GetLandblockTerrain((ushort)landblockID);
            if (landblockData == null) return 0f;

            float localX = worldX - landblockX * LandblockLength;
            float localY = worldY - landblockY * LandblockLength;

            return SampleHeightTriangle(landblockData, _region.LandDefs.LandHeightTable,
                localX, localY, landblockX, landblockY);
        }

        /// <summary>
        /// Triangle-based terrain height interpolation matching the AC client.
        /// localX/localY are in [0, 192] within the landblock.
        /// </summary>
        public static float SampleHeightTriangle(TerrainEntry[] data, float[] heightTable,
            float localX, float localY, uint landblockX, uint landblockY) {

            float cellX = localX / CellSize;
            float cellY = localY / CellSize;

            uint cellIndexX = Math.Min((uint)Math.Floor(cellX), LandblockEdgeCellCount - 1);
            uint cellIndexY = Math.Min((uint)Math.Floor(cellY), LandblockEdgeCellCount - 1);

            float fracX = cellX - cellIndexX;
            float fracY = cellY - cellIndexY;

            // Get the four corner heights
            float hSW = GetHeightFromData(data, heightTable, cellIndexX, cellIndexY);
            float hSE = GetHeightFromData(data, heightTable, cellIndexX + 1, cellIndexY);
            float hNW = GetHeightFromData(data, heightTable, cellIndexX, cellIndexY + 1);
            float hNE = GetHeightFromData(data, heightTable, cellIndexX + 1, cellIndexY + 1);

            // Determine triangle split direction using AC's pseudo-random algorithm
            uint globalCellX = landblockX * LandblockEdgeCellCount + cellIndexX;
            uint globalCellY = landblockY * LandblockEdgeCellCount + cellIndexY;
            bool isSWtoNE = IsSWtoNEcut(globalCellX, globalCellY);

            if (isSWtoNE) {
                // Diagonal from (0,0) to (1,1)
                // Triangle 1: (0,0)-(1,0)-(1,1) — lower-right, where fracX > fracY
                // Triangle 2: (0,0)-(1,1)-(0,1) — upper-left, where fracX <= fracY
                if (fracX > fracY) {
                    // Lower-right triangle: SW, SE, NE
                    // P = SW + fracX*(SE-SW) + fracY*(NE-SE)
                    return hSW + fracX * (hSE - hSW) + fracY * (hNE - hSE);
                }
                else {
                    // Upper-left triangle: SW, NE, NW
                    // P = SW + fracX*(NE-NW) + fracY*(hNW-hSW)
                    return hSW + fracX * (hNE - hNW) + fracY * (hNW - hSW);
                }
            }
            else {
                // Diagonal from (0,1) to (1,0) — NW-SE split
                // Triangle 1: (0,0)-(1,0)-(0,1) — lower-left, where fracX + fracY < 1
                // Triangle 2: (1,0)-(1,1)-(0,1) — upper-right, where fracX + fracY >= 1
                if (fracX + fracY <= 1.0f) {
                    // Lower-left triangle: SW, SE, NW
                    // P = SW + fracX*(SE-SW) + fracY*(NW-SW)
                    return hSW + fracX * (hSE - hSW) + fracY * (hNW - hSW);
                }
                else {
                    // Upper-right triangle: SE, NE, NW
                    // P = NE + (1-fracX)*(NW-NE) + (1-fracY)*(SE-NE)
                    return hNE + (1.0f - fracX) * (hNW - hNE) + (1.0f - fracY) * (hSE - hNE);
                }
            }
        }

        /// <summary>
        /// Determines the triangle split direction for a terrain cell, matching the
        /// AC client's pseudo-random algorithm from LandblockStruct.ConstructPolygons.
        /// Returns true if the cell is split along the SW-NE diagonal (0,0)-(1,1),
        /// false if split along the NW-SE diagonal (0,1)-(1,0).
        /// </summary>
        public static bool IsSWtoNEcut(uint globalCellX, uint globalCellY) {
            // Matches ACE: magicA = seedA + 1813693831 where seedA accumulates 214614067 per X
            // magicB = seedB which accumulates 1109124029 per X
            // With VertexPerCell=1, globalCellX = lcoord.X + x, globalCellY = lcoord.Y + y
            uint magicA = (uint)unchecked((int)globalCellX * 214614067 + 1813693831);
            uint magicB = (uint)unchecked((int)globalCellX * 1109124029);
            uint splitDir = unchecked((uint)((int)globalCellY * (int)magicA - (int)magicB - 1369149221));
            return splitDir * 2.3283064e-10 >= 0.5;
        }

        private static float GetHeightFromData(TerrainEntry[] data, float[] heightTable, uint vx, uint vy) {
            vx = Math.Min(vx, 8);
            vy = Math.Min(vy, 8);
            var idx = (int)(vx * 9 + vy);
            return idx < data.Length ? heightTable[data[idx].Height] : 0f;
        }
    }
}
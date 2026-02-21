using DatReaderWriter.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.Commands {
    public class FillCommand : TerrainVertexChangeCommand {
        private readonly TerrainRaycast.TerrainRaycastHit _hitResult;
        private readonly byte _newType;

        public FillCommand(TerrainEditingContext context, TerrainRaycast.TerrainRaycastHit hitResult, TerrainTextureType newType) : base(context) {
            _hitResult = hitResult;
            _newType = (byte)newType;
            CollectChanges();
        }

        public override string Description => $"Bucket fill with {Enum.GetName(typeof(TerrainTextureType), _newType)}";
        public override TerrainField Field => TerrainField.Type;

        protected override byte GetEntryValue(TerrainEntry entry) => entry.Type;
        protected override TerrainEntry SetEntryValue(TerrainEntry entry, byte value) => entry with { Type = value };

        private void CollectChanges() {
            var vertices = FloodFillVertices(_context.TerrainSystem, _hitResult, _newType, null);
            foreach (var (lbID, index, oldType) in vertices) {
                if (!_changes.TryGetValue(lbID, out var list)) {
                    list = new List<(int, byte, byte)>();
                    _changes[lbID] = list;
                }
                list.Add((index, oldType, _newType));
            }
        }

        /// <summary>
        /// Performs a read-only flood fill from the hit vertex, collecting all contiguous
        /// vertices with the same texture type. Returns a list of (landblockId, vertexIndex, oldType).
        /// Can be used for both preview highlighting and actual command execution.
        /// </summary>
        /// <param name="allowedLandblocks">
        /// Optional set of landblock keys to constrain the fill to (e.g. visible/loaded landblocks).
        /// When null the fill is unbounded. When provided the flood will not cross into landblocks
        /// outside this set, keeping the operation scoped to the camera view.
        /// </param>
        public static List<(ushort LbID, int VertexIndex, byte OldType)> FloodFillVertices(
            TerrainSystem terrainSystem,
            TerrainRaycast.TerrainRaycastHit hitResult,
            byte newType,
            HashSet<ushort>? allowedLandblocks = null) {

            var result = new List<(ushort, int, byte)>();

            uint startLbX = hitResult.LandblockX;
            uint startLbY = hitResult.LandblockY;
            uint startCellX = (uint)hitResult.CellX;
            uint startCellY = (uint)hitResult.CellY;
            ushort startLbID = (ushort)((startLbX << 8) | startLbY);

            // If the starting landblock is outside allowed bounds, nothing to fill
            if (allowedLandblocks != null && !allowedLandblocks.Contains(startLbID))
                return result;

            var startData = terrainSystem.GetLandblockTerrain(startLbID);
            if (startData == null) return result;

            int startIndex = (int)(startCellX * 9 + startCellY);
            if (startIndex >= startData.Length) return result;

            byte oldType = startData[startIndex].Type;
            if (oldType == newType) return result;

            var visited = new HashSet<(uint lbX, uint lbY, uint cellX, uint cellY)>();
            var queue = new Queue<(uint lbX, uint lbY, uint cellX, uint cellY)>();
            queue.Enqueue((startLbX, startLbY, startCellX, startCellY));

            var landblockDataCache = new Dictionary<ushort, TerrainEntry[]>();

            while (queue.Count > 0) {
                var (lbX, lbY, cellX, cellY) = queue.Dequeue();

                if (visited.Contains((lbX, lbY, cellX, cellY))) continue;
                visited.Add((lbX, lbY, cellX, cellY));

                var lbID = (ushort)((lbX << 8) | lbY);

                // Skip landblocks outside the allowed set
                if (allowedLandblocks != null && !allowedLandblocks.Contains(lbID)) continue;

                if (!landblockDataCache.TryGetValue(lbID, out var data)) {
                    data = terrainSystem.GetLandblockTerrain(lbID);
                    if (data == null) continue;
                    landblockDataCache[lbID] = data;
                }

                int index = (int)(cellX * 9 + cellY);
                if (index >= data.Length || data[index].Type != oldType) continue;

                result.Add((lbID, index, oldType));

                // Queue neighbors
                if (cellX > 0) queue.Enqueue((lbX, lbY, cellX - 1, cellY));
                else if (lbX > 0) queue.Enqueue((lbX - 1, lbY, 8, cellY));

                if (cellX < 8) queue.Enqueue((lbX, lbY, cellX + 1, cellY));
                else if (lbX < 255) queue.Enqueue((lbX + 1, lbY, 0, cellY));

                if (cellY > 0) queue.Enqueue((lbX, lbY, cellX, cellY - 1));
                else if (lbY > 0) queue.Enqueue((lbX, lbY - 1, cellX, 8));

                if (cellY < 8) queue.Enqueue((lbX, lbY, cellX, cellY + 1));
                else if (lbY < 255) queue.Enqueue((lbX, lbY + 1, cellX, 0));
            }

            return result;
        }
    }
}

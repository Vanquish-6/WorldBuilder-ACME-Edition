using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.Commands {
    public class HeightChangeCommand : TerrainVertexChangeCommand {
        private readonly string _description;

        public HeightChangeCommand(TerrainEditingContext context, string description, Vector3 centerPosition, float brushRadius, byte targetHeight) : base(context) {
            _description = description;
            CollectChanges(centerPosition, brushRadius, targetHeight);
        }

        public HeightChangeCommand(TerrainEditingContext context, string description, Dictionary<ushort, List<(int VertexIndex, byte OriginalValue, byte NewValue)>> changes) : base(context) {
            _description = description;
            _changesPreApplied = true;
            foreach (var kvp in changes) {
                _changes[kvp.Key] = kvp.Value;
            }
        }

        public override string Description => _description;
        public override TerrainField Field => TerrainField.Height;

        protected override byte GetEntryValue(TerrainEntry entry) => entry.Height;
        protected override TerrainEntry SetEntryValue(TerrainEntry entry, byte value) => entry with { Height = value };

        private void CollectChanges(Vector3 position, float brushRadius, byte targetHeight) {
            var affected = PaintCommand.GetAffectedVertices(position, brushRadius, _context);
            var landblockDataCache = new Dictionary<ushort, TerrainEntry[]>();

            foreach (var (lbId, vIndex, _) in affected) {
                if (!_changes.TryGetValue(lbId, out var list)) {
                    list = new List<(int, byte, byte)>();
                    _changes[lbId] = list;
                }

                if (list.Exists(c => c.VertexIndex == vIndex)) continue;

                if (!landblockDataCache.TryGetValue(lbId, out var data)) {
                    data = _context.TerrainSystem.GetLandblockTerrain(lbId);
                    if (data == null) continue;
                    landblockDataCache[lbId] = data;
                }

                byte original = data[vIndex].Height;
                if (original == targetHeight) continue;
                list.Add((vIndex, original, targetHeight));
            }
        }
    }
}

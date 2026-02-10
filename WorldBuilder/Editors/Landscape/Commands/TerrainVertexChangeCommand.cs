using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.Commands {
    public abstract class TerrainVertexChangeCommand : ICommand {
        protected readonly TerrainEditingContext _context;
        protected readonly Dictionary<ushort, List<(int VertexIndex, byte OriginalValue, byte NewValue)>> _changes = new();

        /// <summary>
        /// Stores static object Z changes per landblock for undo/redo.
        /// Null until the first execution computes them.
        /// </summary>
        private Dictionary<ushort, List<(int ObjectIndex, float OriginalZ, float NewZ)>>? _staticObjectChanges;

        public abstract string Description { get; }
        public abstract TerrainField Field { get; }
        public bool CanExecute => true;
        public bool CanUndo => true;

        public List<string> AffectedDocumentIds => new() { _context.TerrainDocument.Id };

        protected TerrainVertexChangeCommand(TerrainEditingContext context) {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            AffectedDocumentIds.Add(context.TerrainDocument.Id);
        }

        protected abstract byte GetEntryValue(TerrainEntry entry);
        protected abstract TerrainEntry SetEntryValue(TerrainEntry entry, byte value);

        protected bool Apply(bool isUndo) {
            if (_changes.Count == 0) return false;

            bool collectObjectChanges = _staticObjectChanges == null;

            // Convert changes to batch format
            var batchChanges = new Dictionary<ushort, Dictionary<byte, uint>>();
            var landblockDataCache = new Dictionary<ushort, TerrainEntry[]>();

            foreach (var (lbId, changeList) in _changes) {
                if (!landblockDataCache.TryGetValue(lbId, out var data)) {
                    data = _context.TerrainSystem.GetLandblockTerrain(lbId);
                    if (data == null) continue;
                    landblockDataCache[lbId] = data;
                }

                if (!batchChanges.TryGetValue(lbId, out var lbChanges)) {
                    lbChanges = new Dictionary<byte, uint>();
                    batchChanges[lbId] = lbChanges;
                }

                foreach (var (vIndex, original, newVal) in changeList) {
                    byte val = isUndo ? original : newVal;

                    // Skip if already at target value
                    if (GetEntryValue(data[vIndex]) == val) continue;

                    var updatedEntry = SetEntryValue(data[vIndex], val);
                    lbChanges[(byte)vIndex] = updatedEntry.ToUInt();
                }
            }

            // Single batch update with all changes
            var modifiedLandblocks = _context.TerrainSystem.UpdateLandblocksBatch(Field, batchChanges);
            _context.MarkLandblocksModified(modifiedLandblocks);

            // On first execution, compute how static objects should move with the terrain
            if (collectObjectChanges) {
                ComputeStaticObjectChanges();
            }

            // Apply static object height adjustments
            ApplyStaticObjectChanges(isUndo);

            return true;
        }

        /// <summary>
        /// Computes the Z position changes for all static objects on affected landblocks.
        /// Called once during the first execution. The terrain is already at the NEW heights
        /// (from preview painting), so we reconstruct old heights from the stored original
        /// vertex values to compute the delta.
        /// </summary>
        private void ComputeStaticObjectChanges() {
            _staticObjectChanges = new Dictionary<ushort, List<(int ObjectIndex, float OriginalZ, float NewZ)>>();

            var heightTable = _context.TerrainSystem.Region.LandDefs.LandHeightTable;
            var docManager = _context.TerrainSystem.DocumentManager;

            foreach (var lbId in _changes.Keys) {
                var docId = $"landblock_{lbId:X4}";
                var doc = docManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc == null || doc.StaticObjectCount == 0) continue;

                // Get current terrain data (already at new heights)
                var terrainData = _context.TerrainSystem.GetLandblockTerrain(lbId);
                if (terrainData == null) continue;

                // Build a copy with original heights restored for changed vertices
                var originalData = (TerrainEntry[])terrainData.Clone();
                if (_changes.TryGetValue(lbId, out var changeList)) {
                    foreach (var (vIndex, original, _) in changeList) {
                        originalData[vIndex] = SetEntryValue(originalData[vIndex], original);
                    }
                }

                // Compute landblock world origin
                uint landblockX = (uint)(lbId >> 8) & 0xFF;
                uint landblockY = (uint)(lbId & 0xFF);
                float baseLbX = landblockX * 192f;
                float baseLbY = landblockY * 192f;

                var objectChanges = new List<(int ObjectIndex, float OriginalZ, float NewZ)>();

                for (int i = 0; i < doc.StaticObjectCount; i++) {
                    var obj = doc.GetStaticObject(i);

                    // Compute local position within the landblock
                    float localX = obj.Origin.X - baseLbX;
                    float localY = obj.Origin.Y - baseLbY;

                    // Skip objects outside this landblock's bounds
                    if (localX < 0 || localX > 192f || localY < 0 || localY > 192f) continue;

                    // Sample terrain height using AC-accurate triangle interpolation
                    float oldTerrainZ = TerrainDataManager.SampleHeightTriangle(originalData, heightTable, localX, localY, landblockX, landblockY);
                    float newTerrainZ = TerrainDataManager.SampleHeightTriangle(terrainData, heightTable, localX, localY, landblockX, landblockY);

                    float delta = newTerrainZ - oldTerrainZ;
                    if (Math.Abs(delta) < 0.001f) continue;

                    // Preserve the object's offset from the terrain surface
                    float newObjZ = obj.Origin.Z + delta;
                    objectChanges.Add((i, obj.Origin.Z, newObjZ));
                }

                if (objectChanges.Count > 0) {
                    _staticObjectChanges[lbId] = objectChanges;
                }
            }
        }

        // Height sampling now uses TerrainDataManager.SampleHeightTriangle for AC-accurate
        // triangle-based interpolation matching the client's pseudo-random cell split.

        /// <summary>
        /// Applies or reverts static object height changes based on the undo direction.
        /// </summary>
        private void ApplyStaticObjectChanges(bool isUndo) {
            if (_staticObjectChanges == null || _staticObjectChanges.Count == 0) return;

            var docManager = _context.TerrainSystem.DocumentManager;

            foreach (var (lbId, changes) in _staticObjectChanges) {
                var docId = $"landblock_{lbId:X4}";
                var doc = docManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc == null) continue;

                foreach (var (index, originalZ, newZ) in changes) {
                    float targetZ = isUndo ? originalZ : newZ;
                    doc.SetStaticObjectHeight(index, targetZ);
                }
            }
        }

        public bool Execute() => Apply(false);
        public bool Undo() => Apply(true);
    }
}

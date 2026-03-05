using System;
using System.Collections.Generic;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.Commands {
    /// <summary>
    /// Applies a heightmap import using bulk import to avoid the serialization
    /// storm that occurs when firing update events after each batch.
    /// </summary>
    public class HeightImportCommand : ICommand {
        private readonly TerrainEditingContext _context;
        private readonly string _description;
        private readonly Dictionary<ushort, List<VertexChange>> _changes;

        public HeightImportCommand(
            TerrainEditingContext context,
            string description,
            Dictionary<ushort, List<VertexChange>> changes) {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _description = description;
            _changes = changes;
        }

        public string Description => _description;
        public bool CanExecute => _changes.Count > 0;
        public bool CanUndo => true;
        public List<string> AffectedDocumentIds => new() { _context.TerrainDocument.Id };

        public bool Execute() => ApplyBulk(isUndo: false);
        public bool Undo() => ApplyBulk(isUndo: true);

        private bool ApplyBulk(bool isUndo) {
            if (_changes.Count == 0) return false;

            var allChanges = new Dictionary<ushort, Dictionary<byte, uint>>();

            foreach (var (lbId, changeList) in _changes) {
                var terrainData = _context.TerrainSystem.GetLandblockTerrain(lbId);
                if (terrainData == null) continue;

                var lbChanges = new Dictionary<byte, uint>();
                foreach (var c in changeList) {
                    byte height = isUndo ? c.OrigHeight : c.NewHeight;
                    byte type = isUndo ? c.OrigType : c.NewType;

                    var current = terrainData[c.VertexIndex];
                    if (current.Height == height && current.Type == type) continue;

                    var entry = current with { Height = height, Type = type };
                    lbChanges[(byte)c.VertexIndex] = entry.ToUInt();
                }

                if (lbChanges.Count > 0) {
                    allChanges[lbId] = lbChanges;
                }
            }

            if (allChanges.Count > 0) {
                _context.TerrainSystem.TerrainDoc.ApplyBulkImport(allChanges);
            }

            return true;
        }
    }
}

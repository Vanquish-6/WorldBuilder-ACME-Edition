using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Editors.Landscape.Commands {
    public class PasteStampCommand : ICommand {
        private readonly TerrainEditingContext _context;
        private readonly TerrainStamp _stamp;
        private readonly Vector2 _pastePosition;
        private readonly bool _includeObjects;
        private readonly bool _blendEdges;
        private readonly float _zOffset;

        // Store original data for undo
        private Dictionary<ushort, Dictionary<byte, uint>>? _originalTerrainData;
        private List<(ushort LandblockKey, int ObjectIndex)>? _addedObjects;

        public string Description => $"Paste Stamp '{_stamp.Name}'";
        public bool CanExecute => true;
        public bool CanUndo => true;
        public List<string> AffectedDocumentIds { get; } = new();

        public PasteStampCommand(
            TerrainEditingContext context,
            TerrainStamp stamp,
            Vector2 pastePosition,
            bool includeObjects,
            bool blendEdges,
            float zOffset = 0f) {

            _context = context;
            _stamp = stamp;
            _pastePosition = pastePosition;
            _includeObjects = includeObjects;
            _blendEdges = blendEdges;
            _zOffset = zOffset;
        }

        public bool Execute() {
            _originalTerrainData = new();
            _addedObjects = new();

            // Apply terrain data
            var terrainChanges = new Dictionary<ushort, Dictionary<byte, uint>>();

            for (int vx = 0; vx < _stamp.WidthInVertices; vx++) {
                for (int vy = 0; vy < _stamp.HeightInVertices; vy++) {
                    float worldX = _pastePosition.X + (vx * 24f);
                    float worldY = _pastePosition.Y + (vy * 24f);

                    // Get target landblock
                    int lbX = (int)MathF.Floor(worldX / 192f);
                    int lbY = (int)MathF.Floor(worldY / 192f);
                    ushort lbKey = (ushort)((lbX << 8) | lbY);

                    float localX = worldX - (lbX * 192f);
                    float localY = worldY - (lbY * 192f);

                    int localVX = (int)MathF.Round(localX / 24f);
                    int localVY = (int)MathF.Round(localY / 24f);

                    // Skip out-of-bounds
                    if (localVX < 0 || localVX > 8 || localVY < 0 || localVY > 8)
                        continue;

                    int vertexIndex = localVX * 9 + localVY;
                    int stampIndex = vx * _stamp.HeightInVertices + vy;

                    // Store original for undo
                    var data = _context.TerrainSystem.GetLandblockTerrain(lbKey);
                    if (data == null) continue;

                    if (!_originalTerrainData.TryGetValue(lbKey, out var lbOriginals)) {
                        lbOriginals = new Dictionary<byte, uint>();
                        _originalTerrainData[lbKey] = lbOriginals;
                    }
                    if (!lbOriginals.ContainsKey((byte)vertexIndex)) {
                        lbOriginals[(byte)vertexIndex] = data[vertexIndex].ToUInt();
                    }

                    // Unpack stamp terrain data
                    ushort terrainWord = _stamp.TerrainTypes[stampIndex];
                    byte road = (byte)(terrainWord & 0x3);
                    byte type = (byte)((terrainWord >> 2) & 0x1F);
                    byte scenery = (byte)((terrainWord >> 11) & 0x1F);
                    byte height = _stamp.Heights[stampIndex];

                    // Apply Z offset
                    if (_zOffset != 0) {
                        float heightUnits = _context.TerrainSystem.Region.LandDefs.LandHeightTable[height];
                        heightUnits += _zOffset;
                        height = FindNearestHeightIndex(heightUnits);
                    }

                    // Blend edges if requested
                    if (_blendEdges && IsEdgeVertex(vx, vy)) {
                        height = BlendHeight(height, data[vertexIndex].Height);
                    }

                    // Prepare new terrain entry
                    var newEntry = new TerrainEntry(road, scenery, type, height);

                    if (!terrainChanges.TryGetValue(lbKey, out var lbChanges)) {
                        lbChanges = new Dictionary<byte, uint>();
                        terrainChanges[lbKey] = lbChanges;
                    }
                    lbChanges[(byte)vertexIndex] = newEntry.ToUInt();
                }
            }

            // Apply terrain changes in batch
            var modifiedLandblocks = _context.TerrainSystem.UpdateLandblocksBatch(
                TerrainField.Height, terrainChanges);
            _context.MarkLandblocksModified(modifiedLandblocks);

            // Place objects if requested
            if (_includeObjects) {
                PlaceObjects();
            }

            return true;
        }

        public bool Undo() {
            if (_originalTerrainData == null) return false;

            // Restore original terrain
            var modifiedLandblocks = _context.TerrainSystem.UpdateLandblocksBatch(
                TerrainField.Height, _originalTerrainData);
            _context.MarkLandblocksModified(modifiedLandblocks);

            // Remove placed objects
            if (_addedObjects != null) {
                // Group by Landblock to minimize document lookups and handle index removal properly
                var objectsByLb = new Dictionary<ushort, List<int>>();
                foreach(var item in _addedObjects) {
                    if(!objectsByLb.ContainsKey(item.LandblockKey))
                        objectsByLb[item.LandblockKey] = new List<int>();
                    objectsByLb[item.LandblockKey].Add(item.ObjectIndex);
                }

                foreach(var kvp in objectsByLb) {
                    var lbKey = kvp.Key;
                    var indices = kvp.Value;
                    // Sort descending so removing by index doesn't shift remaining indices we want to remove
                    indices.Sort();
                    indices.Reverse();

                    var docId = $"landblock_{lbKey:X4}";
                    var doc = _context.TerrainSystem.DocumentManager
                        .GetOrCreateDocumentAsync<LandblockDocument>(docId).Result;
                    if (doc != null) {
                        foreach(var idx in indices) {
                            doc.RemoveStaticObject(idx);
                        }
                    }
                }
            }

            return true;
        }

        private bool IsEdgeVertex(int vx, int vy) {
            return vx == 0 || vx == _stamp.WidthInVertices - 1 ||
                   vy == 0 || vy == _stamp.HeightInVertices - 1;
        }

        private byte BlendHeight(byte stampHeight, byte existingHeight) {
            // Simple 50/50 blend for edge smoothing
            return (byte)((stampHeight + existingHeight) / 2);
        }

        private byte FindNearestHeightIndex(float height) {
            var table = _context.TerrainSystem.Region.LandDefs.LandHeightTable;
            byte bestIndex = 0;
            float bestDiff = float.MaxValue;

            for (int i = 0; i < table.Length; i++) {
                float diff = MathF.Abs(table[i] - height);
                if (diff < bestDiff) {
                    bestDiff = diff;
                    bestIndex = (byte)i;
                }
            }
            return bestIndex;
        }

        private void PlaceObjects() {
            foreach (var obj in _stamp.Objects) {
                // Transform to world position
                float worldX = _pastePosition.X + obj.Origin.X;
                float worldY = _pastePosition.Y + obj.Origin.Y;
                float worldZ = obj.Origin.Z + _zOffset;

                int lbX = (int)MathF.Floor(worldX / 192f);
                int lbY = (int)MathF.Floor(worldY / 192f);
                ushort lbKey = (ushort)((lbX << 8) | lbY);

                var worldObj = new StaticObject {
                    Id = obj.Id,
                    IsSetup = obj.IsSetup,
                    Origin = new Vector3(worldX, worldY, worldZ),
                    Orientation = obj.Orientation,
                    Scale = obj.Scale
                };

                // Add to document
                var docId = $"landblock_{lbKey:X4}";
                var doc = _context.TerrainSystem.DocumentManager
                    .GetOrCreateDocumentAsync<LandblockDocument>(docId).Result;

                if (doc != null) {
                    int addedIndex = doc.AddStaticObject(worldObj);
                    _addedObjects?.Add((lbKey, addedIndex));
                }
            }
        }
    }
}

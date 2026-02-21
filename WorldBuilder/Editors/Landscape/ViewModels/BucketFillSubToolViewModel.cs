using CommunityToolkit.Mvvm.ComponentModel;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class BucketFillSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Bucket Fill";
        public override string IconGlyph => "🪣";

        [ObservableProperty]
        private TerrainTextureType _selectedTerrainType;

        [ObservableProperty]
        private List<TerrainTextureType> _availableTerrainTypes;
        private TerrainRaycast.TerrainRaycastHit _currentHitPosition;
        private TerrainRaycast.TerrainRaycastHit _lastHitPosition;
        private readonly CommandHistory _commandHistory;

        // Temporary preview state: original values to revert
        private Dictionary<ushort, Dictionary<byte, uint>>? _previewOriginals;
        private HashSet<ushort>? _previewLandblocks;

        public BucketFillSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _availableTerrainTypes = context.TerrainSystem.Scene.SurfaceManager.GetAvailableTerrainTextures()
                .Select(t => t.TerrainType).ToList();
            _selectedTerrainType = _availableTerrainTypes.First();
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
        }

        public override void OnActivated() {
            Context.ActiveVertices.Clear();
            _lastHitPosition = _currentHitPosition = new TerrainRaycast.TerrainRaycastHit();
            _previewOriginals = null;
            _previewLandblocks = null;
        }

        public override void OnDeactivated() {
            RevertPreview();
            Context.ActiveVertices.Clear();
        }

        public override void Update(double deltaTime) {
            if (Vector3.Distance(_currentHitPosition.NearestVertice, _lastHitPosition.NearestVertice) < 0.01f) return;
            _lastHitPosition = _currentHitPosition;

            // Revert previous preview before computing new one
            RevertPreview();

            if (_currentHitPosition.NearestVertice == Vector3.Zero) return;

            // Run flood fill to find affected vertices (unconstrained to avoid viewport race conditions)
            byte newType = (byte)SelectedTerrainType;
            var vertices = FillCommand.FloodFillVertices(
                Context.TerrainSystem, _currentHitPosition, newType, null);

            if (vertices.Count == 0) return;

            // Build batch changes and save originals for revert
            var batchChanges = new Dictionary<ushort, Dictionary<byte, uint>>();
            var originals = new Dictionary<ushort, Dictionary<byte, uint>>();

            foreach (var (lbID, vertexIndex, oldType) in vertices) {
                var data = Context.TerrainSystem.GetLandblockTerrain(lbID);
                if (data == null) continue;

                // Save original value for revert
                if (!originals.TryGetValue(lbID, out var origLb)) {
                    origLb = new Dictionary<byte, uint>();
                    originals[lbID] = origLb;
                }
                origLb[(byte)vertexIndex] = data[vertexIndex].ToUInt();

                // Build new value
                if (!batchChanges.TryGetValue(lbID, out var lbChanges)) {
                    lbChanges = new Dictionary<byte, uint>();
                    batchChanges[lbID] = lbChanges;
                }
                var newEntry = data[vertexIndex] with { Type = newType };
                lbChanges[(byte)vertexIndex] = newEntry.ToUInt();
            }

            // Apply preview changes to terrain (shows real texture on terrain)
            if (batchChanges.Count > 0) {
                _previewLandblocks = Context.TerrainSystem.UpdateLandblocksBatch(TerrainField.Type, batchChanges);
                _previewOriginals = originals;
                Context.MarkLandblocksModified(_previewLandblocks);
            }
        }

        private void RevertPreview() {
            if (_previewOriginals == null || _previewLandblocks == null) return;

            // Restore original terrain values
            var reverted = Context.TerrainSystem.UpdateLandblocksBatch(TerrainField.Type, _previewOriginals);
            Context.MarkLandblocksModified(reverted);

            _previewOriginals = null;
            _previewLandblocks = null;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (!mouseState.IsOverTerrain || !mouseState.TerrainHit.HasValue) return false;

            _currentHitPosition = mouseState.TerrainHit.Value;

            return false;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.IsOverTerrain || !mouseState.TerrainHit.HasValue || !mouseState.LeftPressed) return false;

            // Revert preview first so the command captures clean original values
            RevertPreview();

            // Use the hit from MouseState, or fallback to current tracked hit if valid
            var hit = mouseState.TerrainHit.Value;
            if (hit.NearestVertice == Vector3.Zero && _currentHitPosition.NearestVertice != Vector3.Zero) {
                hit = _currentHitPosition;
            }

            var command = new FillCommand(Context, hit, SelectedTerrainType);
            _commandHistory.ExecuteCommand(command);

            return true;
        }
    }
}
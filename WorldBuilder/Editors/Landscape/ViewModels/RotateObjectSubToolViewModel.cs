using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class RotateObjectSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Rotate";
        public override string IconGlyph => "\uD83D\uDD04";

        private bool _isDragging;
        private float _dragStartX;
        private readonly CommandHistory _commandHistory;
        private readonly SnapSettings _snapSettings;

        private List<(ushort LbKey, int Index, Vector3 OriginalPos, Quaternion OriginalOrientation)> _dragEntries = new();
        private Vector3 _groupCentroid;

        public RotateObjectSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            _snapSettings = context.TerrainSystem.Settings.Landscape.Snap;
        }

        private float SnapAngle(float angleRad) {
            if (!_snapSettings.SnapRotation || _snapSettings.RotationIncrement <= 0) return angleRad;
            float incRad = _snapSettings.RotationIncrement * MathF.PI / 180f;
            return MathF.Round(angleRad / incRad) * incRad;
        }

        public override void OnActivated() { }
        public override void OnDeactivated() {
            if (_isDragging) FinalizeDrag();
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed) return false;

            var sel = Context.ObjectSelection;

            if (mouseState.ObjectHit.HasValue && mouseState.ObjectHit.Value.Hit) {
                var hit = mouseState.ObjectHit.Value;

                // Check if clicked object is already in the current selection
                bool isInSelection = sel.SelectedEntries.Any(e =>
                    e.LandblockKey == hit.LandblockKey && e.ObjectIndex == hit.ObjectIndex);

                if (!isInSelection) {
                    // Ctrl+Click = add to selection; plain click = replace selection
                    if (mouseState.CtrlPressed) {
                        sel.ToggleSelectFromHit(hit);
                    }
                    else {
                        sel.SelectFromHit(hit);
                    }
                    return true;
                }

                // Clicking an already-selected object: start rotating all selected
                if (sel.HasSelection) {
                    var nonScenery = sel.SelectedEntries.Where(e => !e.IsScenery && e.ObjectIndex >= 0).ToList();
                    if (nonScenery.Count == 0) return false;

                    _isDragging = true;
                    _dragStartX = mouseState.Position.X;
                    _dragEntries = nonScenery.Select(e =>
                        (e.LandblockKey, e.ObjectIndex, e.Object.Origin, e.Object.Orientation)).ToList();

                    // Compute group centroid (average origin of all selected objects)
                    var sum = Vector3.Zero;
                    foreach (var entry in _dragEntries) {
                        sum += entry.OriginalPos;
                    }
                    _groupCentroid = sum / _dragEntries.Count;

                    return true;
                }
            }
            else {
                sel.Deselect();
            }

            return false;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            if (_isDragging) {
                FinalizeDrag();
                return true;
            }
            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (!_isDragging || _dragEntries.Count == 0) return false;
            if (!Context.ObjectSelection.HasSelection) return false;

            float deltaX = mouseState.Position.X - _dragStartX;
            float angleRad = deltaX * 0.5f * MathF.PI / 180f;
            angleRad = SnapAngle(angleRad);
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angleRad);

            // Apply rotation to every selected object
            foreach (var (lbKey, index, originalPos, originalOrientation) in _dragEntries) {
                // Rotate position around group centroid
                var offset = originalPos - _groupCentroid;
                var rotatedOffset = Vector3.Transform(offset, rotation);
                var newPosition = _groupCentroid + rotatedOffset;

                // Snap Z to terrain height, preserving original height offset
                // (same as Move tool — keeps objects on the ground on sloped terrain)
                float terrainZ = Context.GetHeightAtPosition(newPosition.X, newPosition.Y);
                float originalOffset = originalPos.Z - Context.GetHeightAtPosition(originalPos.X, originalPos.Y);
                newPosition.Z = terrainZ + originalOffset;

                // Rotate object orientation
                var newOrientation = Quaternion.Normalize(rotation * originalOrientation);

                var docId = $"landblock_{lbKey:X4}";
                var doc = Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc != null && index < doc.StaticObjectCount) {
                    var obj = doc.GetStaticObject(index);
                    var updated = new StaticObject {
                        Id = obj.Id,
                        IsSetup = obj.IsSetup,
                        Origin = newPosition,
                        Orientation = newOrientation,
                        Scale = obj.Scale
                    };
                    doc.UpdateStaticObject(index, updated);
                }
            }

            // Refresh selection state and invalidate rendering
            Context.ObjectSelection.RefreshAllFromDocuments(docId =>
                Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult());
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();

            return true;
        }

        private void FinalizeDrag() {
            _isDragging = false;

            if (_dragEntries.Count == 0) return;

            // Build commands for each rotated (and possibly moved) object
            var commands = new List<ICommand>();
            foreach (var (lbKey, index, originalPos, originalOrientation) in _dragEntries) {
                var docId = $"landblock_{lbKey:X4}";
                var doc = Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc == null || index >= doc.StaticObjectCount) continue;

                var currentObj = doc.GetStaticObject(index);
                bool positionChanged = Vector3.Distance(currentObj.Origin, originalPos) >= 0.01f;
                bool orientationChanged = currentObj.Orientation != originalOrientation;

                if (!positionChanged && !orientationChanged) continue;

                // Position changes first (from orbiting around centroid), then orientation
                if (positionChanged) {
                    commands.Add(new MoveObjectCommand(Context, lbKey, index, originalPos, currentObj.Origin));
                }
                if (orientationChanged) {
                    commands.Add(new RotateObjectCommand(Context, lbKey, index, originalOrientation, currentObj.Orientation));
                }
            }

            if (commands.Count > 0) {
                if (commands.Count == 1) {
                    _commandHistory.ExecuteCommand(commands[0]);
                }
                else {
                    var compound = new CompoundCommand($"Rotate {_dragEntries.Count} objects", commands);
                    _commandHistory.ExecuteCommand(compound);
                }
            }

            _dragEntries.Clear();
        }
    }
}

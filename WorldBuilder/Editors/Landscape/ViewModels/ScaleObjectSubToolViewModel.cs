using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class ScaleObjectSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Scale";
        public override string IconGlyph => "\u2922";

        private bool _isDragging;
        private float _dragStartY;
        private readonly CommandHistory _commandHistory;

        private List<(ushort LbKey, int Index, Vector3 OriginalScale)> _dragEntries = new();

        public ScaleObjectSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
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

                bool isInSelection = sel.SelectedEntries.Any(e =>
                    e.LandblockKey == hit.LandblockKey && e.ObjectIndex == hit.ObjectIndex);

                if (!isInSelection) {
                    if (mouseState.CtrlPressed)
                        sel.ToggleSelectFromHit(hit);
                    else
                        sel.SelectFromHit(hit);
                    return true;
                }

                if (sel.HasSelection) {
                    var nonScenery = sel.SelectedEntries.Where(e => !e.IsScenery && e.ObjectIndex >= 0).ToList();
                    if (nonScenery.Count == 0) return false;

                    _isDragging = true;
                    _dragStartY = mouseState.Position.Y;
                    _dragEntries = nonScenery.Select(e =>
                        (e.LandblockKey, e.ObjectIndex, e.Object.Scale)).ToList();
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

            float deltaY = _dragStartY - mouseState.Position.Y;
            float scaleFactor = 1.0f + deltaY * 0.005f;
            scaleFactor = MathF.Max(scaleFactor, 0.01f);

            foreach (var (lbKey, index, originalScale) in _dragEntries) {
                var newScale = originalScale * scaleFactor;
                newScale = Vector3.Max(newScale, new Vector3(0.01f));

                var docId = $"landblock_{lbKey:X4}";
                var doc = Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc != null && index < doc.StaticObjectCount) {
                    var obj = doc.GetStaticObject(index);
                    doc.UpdateStaticObject(index, new StaticObject {
                        Id = obj.Id,
                        IsSetup = obj.IsSetup,
                        Origin = obj.Origin,
                        Orientation = obj.Orientation,
                        Scale = newScale
                    });
                }
            }

            Context.ObjectSelection.RefreshAllFromDocuments(docId =>
                Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult());
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();

            return true;
        }

        private void FinalizeDrag() {
            _isDragging = false;

            if (_dragEntries.Count == 0) return;

            var commands = new List<ICommand>();
            foreach (var (lbKey, index, originalScale) in _dragEntries) {
                var docId = $"landblock_{lbKey:X4}";
                var doc = Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc == null || index >= doc.StaticObjectCount) continue;

                var currentObj = doc.GetStaticObject(index);
                if (Vector3.Distance(currentObj.Scale, originalScale) < 0.001f) continue;

                commands.Add(new ScaleObjectCommand(Context, lbKey, index, originalScale, currentObj.Scale));
            }

            if (commands.Count > 0) {
                if (commands.Count == 1) {
                    _commandHistory.ExecuteCommand(commands[0]);
                }
                else {
                    var compound = new CompoundCommand($"Scale {commands.Count} objects", commands);
                    _commandHistory.ExecuteCommand(compound);
                }
            }

            _dragEntries.Clear();
        }
    }
}

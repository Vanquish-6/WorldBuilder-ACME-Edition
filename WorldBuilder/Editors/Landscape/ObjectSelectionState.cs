using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Represents a single selected object entry.
    /// </summary>
    public struct SelectedEntry {
        public StaticObject Object;
        public ushort LandblockKey;
        public int ObjectIndex;
        public bool IsScenery;
    }

    /// <summary>
    /// Manages the currently selected static objects in the scene.
    /// Supports both single and multi-selection.
    /// </summary>
    public class ObjectSelectionState {
        private readonly List<SelectedEntry> _selectedObjects = new();

        /// <summary>
        /// Fires when the selection changes.
        /// </summary>
        public event EventHandler? SelectionChanged;

        // --- EnvCell (dungeon cell) selection ---

        /// <summary>The currently selected dungeon cell, or null if none.</summary>
        public LoadedEnvCell? SelectedEnvCell { get; private set; }

        /// <summary>True if a dungeon cell is selected (mutually exclusive with object selection).</summary>
        public bool HasEnvCellSelection => SelectedEnvCell != null;

        /// <summary>
        /// Selects a dungeon cell. Clears any object selection.
        /// </summary>
        public void SelectEnvCell(LoadedEnvCell cell) {
            _selectedObjects.Clear();
            SelectedEnvCell = cell;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // --- Backward-compatible single-selection properties (primary = first in list) ---

        public bool HasSelection => _selectedObjects.Count > 0 || SelectedEnvCell != null;
        public StaticObject? SelectedObject => _selectedObjects.Count > 0 ? _selectedObjects[0].Object : null;
        public ushort SelectedLandblockKey => _selectedObjects.Count > 0 ? _selectedObjects[0].LandblockKey : (ushort)0;
        public int SelectedObjectIndex => _selectedObjects.Count > 0 ? _selectedObjects[0].ObjectIndex : -1;
        public bool IsScenery => _selectedObjects.Count > 0 && _selectedObjects[0].IsScenery;

        // --- Multi-selection API ---

        public IReadOnlyList<SelectedEntry> SelectedEntries => _selectedObjects;
        public int SelectionCount => _selectedObjects.Count;
        public bool IsMultiSelection => _selectedObjects.Count > 1;

        // --- Clipboard ---

        public StaticObject? Clipboard { get; set; }
        public List<StaticObject>? ClipboardMulti { get; set; }

        public bool IsPlacementMode { get; set; }
        public StaticObject? PlacementPreview { get; set; }
        public List<StaticObject>? PlacementPreviewMulti { get; set; }

        /// <summary>
        /// Clears selection and selects a single object (standard click).
        /// </summary>
        public void Select(StaticObject obj, ushort landblockKey, int objectIndex, bool isScenery) {
            _selectedObjects.Clear();
            SelectedEnvCell = null; // clear EnvCell selection when selecting an object
            _selectedObjects.Add(new SelectedEntry {
                Object = obj,
                LandblockKey = landblockKey,
                ObjectIndex = objectIndex,
                IsScenery = isScenery
            });
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Selects from an ObjectRaycastHit (clears existing selection).
        /// </summary>
        public void SelectFromHit(ObjectRaycast.ObjectRaycastHit hit) {
            Select(hit.Object, hit.LandblockKey, hit.ObjectIndex, hit.IsScenery);
        }

        /// <summary>
        /// Toggles an object in/out of the selection (for Ctrl+Click).
        /// </summary>
        public void ToggleSelect(StaticObject obj, ushort landblockKey, int objectIndex, bool isScenery) {
            var existingIdx = _selectedObjects.FindIndex(e =>
                e.LandblockKey == landblockKey && e.ObjectIndex == objectIndex && e.IsScenery == isScenery);

            if (existingIdx >= 0) {
                _selectedObjects.RemoveAt(existingIdx);
            }
            else {
                _selectedObjects.Add(new SelectedEntry {
                    Object = obj,
                    LandblockKey = landblockKey,
                    ObjectIndex = objectIndex,
                    IsScenery = isScenery
                });
            }
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Toggles selection from a raycast hit (for Ctrl+Click).
        /// </summary>
        public void ToggleSelectFromHit(ObjectRaycast.ObjectRaycastHit hit) {
            ToggleSelect(hit.Object, hit.LandblockKey, hit.ObjectIndex, hit.IsScenery);
        }

        /// <summary>
        /// Clears the current selection.
        /// </summary>
        public void Deselect() {
            _selectedObjects.Clear();
            SelectedEnvCell = null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Refreshes the primary selected object data from the document (e.g., after a move).
        /// </summary>
        public void RefreshFromDocument(LandblockDocument doc) {
            if (!HasSelection) return;
            bool changed = false;
            for (int i = 0; i < _selectedObjects.Count; i++) {
                var entry = _selectedObjects[i];
                if (entry.ObjectIndex < 0) continue;
                var docId = $"landblock_{entry.LandblockKey:X4}";
                if (entry.ObjectIndex < doc.StaticObjectCount) {
                    entry.Object = doc.GetStaticObject(entry.ObjectIndex);
                    _selectedObjects[i] = entry;
                    changed = true;
                }
            }
            if (changed) {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Refreshes all selected objects from their respective documents.
        /// </summary>
        public void RefreshAllFromDocuments(Func<string, LandblockDocument?> getDoc) {
            bool changed = false;
            for (int i = 0; i < _selectedObjects.Count; i++) {
                var entry = _selectedObjects[i];
                if (entry.ObjectIndex < 0) continue;
                var docId = $"landblock_{entry.LandblockKey:X4}";
                var doc = getDoc(docId);
                if (doc != null && entry.ObjectIndex < doc.StaticObjectCount) {
                    entry.Object = doc.GetStaticObject(entry.ObjectIndex);
                    _selectedObjects[i] = entry;
                    changed = true;
                }
            }
            if (changed) {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

using System;
using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Services;
using WorldBuilder.Shared.Models;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class CloneSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Clone";
        public override string IconGlyph => "📋";

        private readonly StampLibraryManager _stampLibrary;
        // private readonly CommandHistory _commandHistory; // Not used yet in Clone, but needed for future Paste or if I add delete

        private Vector2 _selectionStart;
        private Vector2 _selectionEnd;
        private bool _isSelecting;

        [ObservableProperty]
        private ObservableCollection<TerrainStamp> _availableStamps;

        [ObservableProperty]
        private TerrainStamp? _selectedStamp;

        public CloneSubToolViewModel(
            TerrainEditingContext context,
            StampLibraryManager stampLibrary) : base(context) {
            _stampLibrary = stampLibrary;
            // _commandHistory = commandHistory;
            _availableStamps = _stampLibrary.Stamps;
        }

        public override void OnActivated() {
            Context.BrushActive = false;
            _isSelecting = false;
        }

        public override void OnDeactivated() {
            _isSelecting = false;
            Context.ObjectSelection.SelectionPreviewBounds = null;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.TerrainHit.HasValue || !mouseState.LeftPressed)
                return false;

            _selectionStart = new Vector2(
                mouseState.TerrainHit.Value.HitPosition.X,
                mouseState.TerrainHit.Value.HitPosition.Y);

            // Initializing end point to start point to avoid jumping
            _selectionEnd = _selectionStart;
            _isSelecting = true;
            UpdateSelectionPreview();

            return true;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (!_isSelecting || !mouseState.TerrainHit.HasValue)
                return false;

            _selectionEnd = new Vector2(
                mouseState.TerrainHit.Value.HitPosition.X,
                mouseState.TerrainHit.Value.HitPosition.Y);

            // Update visual selection box
            UpdateSelectionPreview();

            return true;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            if (!_isSelecting) return false;

            _isSelecting = false;
            CaptureStamp();
            Context.ObjectSelection.SelectionPreviewBounds = null;

            return true;
        }

        private void UpdateSelectionPreview() {
            var minX = MathF.Min(_selectionStart.X, _selectionEnd.X);
            var maxX = MathF.Max(_selectionStart.X, _selectionEnd.X);
            var minY = MathF.Min(_selectionStart.Y, _selectionEnd.Y);
            var maxY = MathF.Max(_selectionStart.Y, _selectionEnd.Y);

            // Snap to cell boundaries (24 units)
            // Same logic as CaptureStamp to ensure preview matches result
            minX = MathF.Floor(minX / 24f) * 24f;
            minY = MathF.Floor(minY / 24f) * 24f;
            maxX = MathF.Ceiling(maxX / 24f) * 24f;
            maxY = MathF.Ceiling(maxY / 24f) * 24f;

            Context.ObjectSelection.SelectionPreviewBounds = new Vector4(minX, minY, maxX, maxY);
        }

        private void CaptureStamp() {
            // Snap to 24-unit grid (AC cell boundaries)
            var minX = MathF.Min(_selectionStart.X, _selectionEnd.X);
            var maxX = MathF.Max(_selectionStart.X, _selectionEnd.X);
            var minY = MathF.Min(_selectionStart.Y, _selectionEnd.Y);
            var maxY = MathF.Max(_selectionStart.Y, _selectionEnd.Y);

            // Snap to cell boundaries
            minX = MathF.Floor(minX / 24f) * 24f;
            minY = MathF.Floor(minY / 24f) * 24f;
            maxX = MathF.Ceiling(maxX / 24f) * 24f;
            maxY = MathF.Ceiling(maxY / 24f) * 24f;

            // Calculate dimensions in vertices
            int widthInCells = (int)((maxX - minX) / 24f);
            int heightInCells = (int)((maxY - minY) / 24f);
            int widthInVertices = widthInCells + 1;
            int heightInVertices = heightInCells + 1;

            // Capture terrain data
            var stamp = CaptureTerrainData(
                minX, minY, maxX, maxY,
                widthInVertices, heightInVertices);

            // Save to library
            var filename = $"stamp_{DateTime.Now:yyyyMMdd_HHmmss}";
            _stampLibrary.SaveStamp(stamp, filename);
            SelectedStamp = stamp;

            Console.WriteLine($"[Clone] Captured {widthInVertices}x{heightInVertices} stamp");
        }

        private TerrainStamp CaptureTerrainData(
            float minX, float minY, float maxX, float maxY,
            int widthInVertices, int heightInVertices) {

            var stamp = new TerrainStamp {
                Name = $"Captured Region {DateTime.Now:HH:mm:ss}",
                WidthInVertices = widthInVertices,
                HeightInVertices = heightInVertices,
                Heights = new byte[widthInVertices * heightInVertices],
                TerrainTypes = new ushort[widthInVertices * heightInVertices],
                OriginalWorldPosition = new Vector2(minX, minY)
            };

            // Sample terrain at each vertex position
            for (int vx = 0; vx < widthInVertices; vx++) {
                for (int vy = 0; vy < heightInVertices; vy++) {
                    float worldX = minX + (vx * 24f);
                    float worldY = minY + (vy * 24f);

                    // Get landblock and local vertex index
                    int lbX = (int)MathF.Floor(worldX / 192f);
                    int lbY = (int)MathF.Floor(worldY / 192f);
                    ushort lbKey = (ushort)((lbX << 8) | lbY);

                    float localX = worldX - (lbX * 192f);
                    float localY = worldY - (lbY * 192f);

                    int localVX = (int)MathF.Round(localX / 24f);
                    int localVY = (int)MathF.Round(localY / 24f);
                    int vertexIndex = localVX * 9 + localVY;

                    // Read terrain data
                    var data = Context.TerrainSystem.GetLandblockTerrain(lbKey);
                    if (data != null && vertexIndex >= 0 && vertexIndex < 81) {
                        int stampIndex = vx * heightInVertices + vy;
                        stamp.Heights[stampIndex] = data[vertexIndex].Height;

                        // Pack terrain type WORD (road + type + scenery)
                        stamp.TerrainTypes[stampIndex] = (ushort)(
                            (data[vertexIndex].Road << 0) |
                            (data[vertexIndex].Type << 2) |
                            (data[vertexIndex].Scenery << 11));

                        // Console.WriteLine($"[Clone] Captured ({vx},{vy}): Height={data[vertexIndex].Height}, Type={data[vertexIndex].Type}");
                    } else {
                        // Console.WriteLine($"[Clone] Missed ({vx},{vy}): LB={lbKey:X4}, LocalV={vertexIndex}");
                    }
                }
            }

            // Optionally capture objects in region
            CaptureObjectsInRegion(stamp, minX, minY, maxX, maxY);

            return stamp;
        }

        private void CaptureObjectsInRegion(
            TerrainStamp stamp,
            float minX, float minY, float maxX, float maxY) {

            // Simple AABB check
            var allObjects = Context.TerrainSystem.Scene.GetAllStaticObjects();
            foreach (var obj in allObjects) {
                if (obj.Origin.X >= minX && obj.Origin.X <= maxX &&
                    obj.Origin.Y >= minY && obj.Origin.Y <= maxY) {

                    // Store relative to stamp origin
                    var relativeObj = new StaticObject {
                        Id = obj.Id,
                        IsSetup = obj.IsSetup,
                        Origin = new Vector3(
                            obj.Origin.X - minX,
                            obj.Origin.Y - minY,
                            obj.Origin.Z),
                        Orientation = obj.Orientation,
                        Scale = obj.Scale
                    };
                    stamp.Objects.Add(relativeObj);
                }
            }
        }
    }
}

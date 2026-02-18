using System;
using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Services;
using WorldBuilder.Shared.Models;
using WorldBuilder.Utilities;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class PasteSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Paste";
        public override string IconGlyph => "📌";

        private readonly StampLibraryManager _stampLibrary;
        private readonly CommandHistory _commandHistory;

        [ObservableProperty]
        private ObservableCollection<TerrainStamp> _availableStamps;

        [ObservableProperty]
        private TerrainStamp? _selectedStamp;

        [ObservableProperty]
        private int _rotationDegrees = 0; // 0, 90, 180, 270

        [ObservableProperty]
        private bool _includeObjects = true;

        [ObservableProperty]
        private bool _blendEdges = false;

        private Vector2 _previewPosition;
        private float _zOffset;
        private TerrainStamp? _rotatedStamp;
        private PlacementStage _currentStage = PlacementStage.Positioning;
        private Vector2 _dragStartMousePos;
        private float _dragStartZOffset;

        private enum PlacementStage {
            Positioning,
            Blending
        }

        public PasteSubToolViewModel(
            TerrainEditingContext context,
            StampLibraryManager stampLibrary,
            CommandHistory commandHistory) : base(context) {
            _stampLibrary = stampLibrary;
            _commandHistory = commandHistory;
            _availableStamps = _stampLibrary.Stamps;
        }

        partial void OnSelectedStampChanged(TerrainStamp? value) {
            if (value != null) {
                // Reset rotation to default when selecting a new stamp
                RotationDegrees = 0;
                _rotatedStamp = value; // Default rotation
                UpdateRotatedStamp();

                _currentStage = PlacementStage.Positioning;
                _zOffset = 0;
                // Force preview update immediately (e.g. if mouse is already in view)
                if (_rotatedStamp != null) {
                    Context.TerrainSystem.Scene.SetStampPreview(_rotatedStamp, _previewPosition, _zOffset);
                }
            }
        }

        partial void OnRotationDegreesChanged(int value) {
            UpdateRotatedStamp();
        }

        private void UpdateRotatedStamp() {
            if (SelectedStamp == null) {
                _rotatedStamp = null;
                Context.TerrainSystem.Scene.SetStampPreview(null, Vector2.Zero, 0);
                return;
            }

            _rotatedStamp = RotationDegrees switch {
                90 => StampTransforms.Rotate90Clockwise(SelectedStamp),
                180 => StampTransforms.Rotate180(SelectedStamp),
                270 => StampTransforms.Rotate270Clockwise(SelectedStamp),
                _ => SelectedStamp
            };
        }

        public override void OnActivated() {
            Context.BrushActive = false;
            _currentStage = PlacementStage.Positioning;
            _zOffset = 0;
            // Restore preview if we have a selection
            if (_rotatedStamp != null) {
                Context.TerrainSystem.Scene.SetStampPreview(_rotatedStamp, _previewPosition, _zOffset);
            }
        }

        public override void OnDeactivated() {
            Context.TerrainSystem.Scene.SetStampPreview(null, Vector2.Zero, 0);
            _currentStage = PlacementStage.Positioning;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (_rotatedStamp == null) {
                Context.TerrainSystem.Scene.SetStampPreview(null, Vector2.Zero, 0);
                return false;
            }

            if (_currentStage == PlacementStage.Positioning) {
                if (!mouseState.TerrainHit.HasValue) return false;

                var hit = mouseState.TerrainHit.Value;

                // Snap to cell grid (24 units)
                _previewPosition = new Vector2(
                    MathF.Floor(hit.HitPosition.X / 24f) * 24f,
                    MathF.Floor(hit.HitPosition.Y / 24f) * 24f);
            }
            else if (_currentStage == PlacementStage.Blending) {
                // Adjust Z offset based on vertical mouse movement
                float deltaY = _dragStartMousePos.Y - mouseState.Position.Y;
                _zOffset = _dragStartZOffset + (deltaY * 0.1f); // Sensitivity scaling
            }

            // Update preview
            Context.TerrainSystem.Scene.SetStampPreview(_rotatedStamp, _previewPosition, _zOffset);

            return true;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed || _rotatedStamp == null)
                return false;

            if (_currentStage == PlacementStage.Positioning) {
                // First click: Lock X/Y, start blending Z
                _currentStage = PlacementStage.Blending;
                _dragStartMousePos = mouseState.Position;
                _dragStartZOffset = _zOffset;
                return true;
            }
            else if (_currentStage == PlacementStage.Blending) {
                // Second click: Finalize placement
                var command = new PasteStampCommand(
                    Context, _rotatedStamp, _previewPosition,
                    IncludeObjects, BlendEdges, _zOffset);
                _commandHistory.ExecuteCommand(command);

                Console.WriteLine($"[Paste] Stamped {_rotatedStamp.WidthInVertices}x{_rotatedStamp.HeightInVertices} at ({_previewPosition.X}, {_previewPosition.Y}) Z+{_zOffset}");

                // Reset for next stamp
                _currentStage = PlacementStage.Positioning;
                _zOffset = 0;
                return true;
            }

            return false;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
             return false;
        }

        [RelayCommand]
        private void RotateClockwise() {
            RotationDegrees = (RotationDegrees + 90) % 360;
        }

        [RelayCommand]
        private void RotateCounterClockwise() {
            RotationDegrees = (RotationDegrees + 270) % 360;
        }
    }
}

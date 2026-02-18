using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
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
        private ObservableCollection<StampViewModel> _availableStamps = new();

        [ObservableProperty]
        private StampViewModel? _selectedStamp;

        [ObservableProperty]
        private int _rotationDegrees = 0; // 0, 90, 180, 270

        [ObservableProperty]
        private bool _includeObjects = true;

        [ObservableProperty]
        private bool _blendEdges = false;

        private Vector2 _previewPosition;
        private float _manualZOffset; // User-adjustable offset
        private float _autoZOffset;   // Automatically calculated alignment offset
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

            // Sync initial list
            foreach (var stamp in _stampLibrary.Stamps) {
                AvailableStamps.Add(new StampViewModel(stamp));
            }

            // Keep synced
            _stampLibrary.Stamps.CollectionChanged += OnStampsCollectionChanged;
        }

        private void OnStampsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
            if (e.Action == NotifyCollectionChangedAction.Add) {
                if (e.NewItems != null) {
                    foreach (TerrainStamp newItem in e.NewItems) {
                        AvailableStamps.Insert(0, new StampViewModel(newItem));
                    }
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove) {
                if (e.OldItems != null) {
                    foreach (TerrainStamp oldItem in e.OldItems) {
                        var vm = AvailableStamps.FirstOrDefault(x => x.Stamp == oldItem);
                        if (vm != null) AvailableStamps.Remove(vm);
                    }
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset) {
                AvailableStamps.Clear();
                foreach (var stamp in _stampLibrary.Stamps) {
                    AvailableStamps.Add(new StampViewModel(stamp));
                }
            }
        }

        partial void OnSelectedStampChanged(StampViewModel? value) {
            if (value != null) {
                // Reset rotation to default when selecting a new stamp
                RotationDegrees = 0;
                _rotatedStamp = value.Stamp; // Default rotation
                UpdateRotatedStamp();

                _currentStage = PlacementStage.Positioning;
                _manualZOffset = 0;
                // Force preview update immediately (e.g. if mouse is already in view)
                if (_rotatedStamp != null) {
                    Context.TerrainSystem.Scene.SetStampPreview(_rotatedStamp, _previewPosition, _autoZOffset + _manualZOffset);
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
                90 => StampTransforms.Rotate90Clockwise(SelectedStamp.Stamp),
                180 => StampTransforms.Rotate180(SelectedStamp.Stamp),
                270 => StampTransforms.Rotate270Clockwise(SelectedStamp.Stamp),
                _ => SelectedStamp.Stamp
            };
        }

        public override void OnActivated() {
            Context.BrushActive = false;
            _currentStage = PlacementStage.Positioning;
            _manualZOffset = 0;
            // Restore preview if we have a selection
            if (_rotatedStamp != null) {
                Context.TerrainSystem.Scene.SetStampPreview(_rotatedStamp, _previewPosition, _autoZOffset + _manualZOffset);
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

                // Calculate auto Z offset to align stamp's base to current terrain height
                if (_rotatedStamp != null && _rotatedStamp.Heights.Length > 0) {
                    float targetZ = Context.GetHeightAtPosition(_previewPosition.X, _previewPosition.Y);

                    // Get base height of stamp (first vertex / corner)
                    byte stampBaseHeightIndex = _rotatedStamp.Heights[0];
                    float stampBaseZ = Context.TerrainSystem.Region.LandDefs.LandHeightTable[stampBaseHeightIndex];

                    _autoZOffset = targetZ - stampBaseZ;
                }
            }
            else if (_currentStage == PlacementStage.Blending) {
                // Adjust manual Z offset based on vertical mouse movement
                float deltaY = _dragStartMousePos.Y - mouseState.Position.Y;
                _manualZOffset = _dragStartZOffset + (deltaY * 0.1f); // Sensitivity scaling
            }

            // Update preview
            Context.TerrainSystem.Scene.SetStampPreview(_rotatedStamp, _previewPosition, _autoZOffset + _manualZOffset);

            return true;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed || _rotatedStamp == null)
                return false;

            if (_currentStage == PlacementStage.Positioning) {
                // First click: Lock X/Y, start blending Z
                _currentStage = PlacementStage.Blending;
                _dragStartMousePos = mouseState.Position;
                _dragStartZOffset = _manualZOffset;
                return true;
            }
            else if (_currentStage == PlacementStage.Blending) {
                // Second click: Finalize placement
                var command = new PasteStampCommand(
                    Context, _rotatedStamp, _previewPosition,
                    IncludeObjects, BlendEdges, _autoZOffset + _manualZOffset);
                _commandHistory.ExecuteCommand(command);

                Console.WriteLine($"[Paste] Stamped {_rotatedStamp.WidthInVertices}x{_rotatedStamp.HeightInVertices} at ({_previewPosition.X}, {_previewPosition.Y}) Z+{_autoZOffset + _manualZOffset}");

                // Reset for next stamp
                _currentStage = PlacementStage.Positioning;
                _manualZOffset = 0;
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

        [RelayCommand]
        private void DeleteStamp(StampViewModel? stamp) {
            if (stamp == null) return;
            _stampLibrary.DeleteStamp(stamp.Stamp);
            if (SelectedStamp == stamp) {
                SelectedStamp = null;
            }
        }
    }
}

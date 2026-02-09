using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class SelectSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Select";
        public override string IconGlyph => "🔍";

        [ObservableProperty]
        private string _selectedObjectInfo = "No selection";

        [ObservableProperty]
        private string _selectedObjectId = "";

        [ObservableProperty]
        private bool _hasEditableSelection;

        // Editable position fields
        [ObservableProperty] private float _positionX;
        [ObservableProperty] private float _positionY;
        [ObservableProperty] private float _positionZ;

        // Editable rotation (Euler angles in degrees)
        [ObservableProperty] private float _rotationX;
        [ObservableProperty] private float _rotationY;
        [ObservableProperty] private float _rotationZ;

        // Landcell display
        [ObservableProperty] private string _landcellText = "";


        private bool _suppressPropertyUpdates;
        private readonly CommandHistory _commandHistory;

        public SelectSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            context.ObjectSelection.SelectionChanged += OnSelectionChanged;
        }

        private void OnSelectionChanged(object? sender, EventArgs e) {
            UpdateSelectionInfo();
        }

        private void UpdateSelectionInfo() {
            _suppressPropertyUpdates = true;
            var sel = Context.ObjectSelection;

            if (sel.IsMultiSelection) {
                // Multi-selection: show count, hide individual editing
                var nonSceneryCount = sel.SelectedEntries.Count(e => !e.IsScenery);
                SelectedObjectId = "";
                SelectedObjectInfo = $"{sel.SelectionCount} objects selected ({nonSceneryCount} editable)";
                HasEditableSelection = false;
                PositionX = 0; PositionY = 0; PositionZ = 0;
                RotationX = 0; RotationY = 0; RotationZ = 0;
                LandcellText = "";
            }
            else if (sel.HasSelection && sel.SelectedObject.HasValue) {
                // Single selection: show full details
                var obj = sel.SelectedObject.Value;
                SelectedObjectId = $"0x{obj.Id:X8} ({(obj.IsSetup ? "Setup" : "GfxObj")})";
                SelectedObjectInfo = sel.IsScenery ? "Scenery (read-only)" : $"Landblock {sel.SelectedLandblockKey:X4} [{sel.SelectedObjectIndex}]";
                HasEditableSelection = !sel.IsScenery && sel.SelectedObjectIndex >= 0;

                PositionX = obj.Origin.X;
                PositionY = obj.Origin.Y;
                PositionZ = obj.Origin.Z;

                // Extract Euler angles (X, Y, Z) from quaternion
                QuaternionToEuler(obj.Orientation, out float ex, out float ey, out float ez);
                RotationX = ex;
                RotationY = ey;
                RotationZ = ez;

                // Compute landcell from world position
                int lbX = (int)MathF.Floor(obj.Origin.X / 192f);
                int lbY = (int)MathF.Floor(obj.Origin.Y / 192f);
                int cellX = (int)MathF.Floor((obj.Origin.X - lbX * 192f) / 24f);
                int cellY = (int)MathF.Floor((obj.Origin.Y - lbY * 192f) / 24f);
                cellX = Math.Clamp(cellX, 0, 7);
                cellY = Math.Clamp(cellY, 0, 7);
                uint landcell = (uint)((lbX << 24) | (lbY << 16) | (cellX << 3 | cellY));
                LandcellText = $"0x{landcell:X8}";
            }
            else {
                SelectedObjectId = "";
                SelectedObjectInfo = "No selection";
                HasEditableSelection = false;
                PositionX = 0; PositionY = 0; PositionZ = 0;
                RotationX = 0; RotationY = 0; RotationZ = 0;
                LandcellText = "";
            }
            _suppressPropertyUpdates = false;
        }

        [RelayCommand]
        private void ApplyPosition() {
            if (_suppressPropertyUpdates || !HasEditableSelection) return;
            var sel = Context.ObjectSelection;
            if (!sel.HasSelection || sel.IsScenery || sel.SelectedObjectIndex < 0) return;

            var doc = GetDocument();
            if (doc == null) return;

            var obj = doc.GetStaticObject(sel.SelectedObjectIndex);
            var oldPos = obj.Origin;
            var newPos = new Vector3(PositionX, PositionY, PositionZ);
            if (Vector3.Distance(oldPos, newPos) < 0.001f) return;

            var cmd = new MoveObjectCommand(Context, sel.SelectedLandblockKey, sel.SelectedObjectIndex, oldPos, newPos);
            _commandHistory.ExecuteCommand(cmd);
            sel.RefreshFromDocument(doc);
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
        }

        [RelayCommand]
        private void ApplyRotation() {
            if (_suppressPropertyUpdates || !HasEditableSelection) return;
            var sel = Context.ObjectSelection;
            if (!sel.HasSelection || sel.IsScenery || sel.SelectedObjectIndex < 0) return;

            var doc = GetDocument();
            if (doc == null) return;

            var obj = doc.GetStaticObject(sel.SelectedObjectIndex);
            var oldRot = obj.Orientation;
            var newRot = EulerToQuaternion(RotationX, RotationY, RotationZ);
            if (Quaternion.Dot(oldRot, newRot) > 0.9999f) return;

            var cmd = new RotateObjectCommand(Context, sel.SelectedLandblockKey, sel.SelectedObjectIndex, oldRot, newRot);
            _commandHistory.ExecuteCommand(cmd);
            sel.RefreshFromDocument(doc);
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
        }

        [RelayCommand]
        private void SnapToTerrain() {
            if (_suppressPropertyUpdates || !HasEditableSelection) return;
            var sel = Context.ObjectSelection;
            if (!sel.HasSelection || sel.IsScenery || sel.SelectedObjectIndex < 0) return;

            var doc = GetDocument();
            if (doc == null) return;

            var obj = doc.GetStaticObject(sel.SelectedObjectIndex);
            float terrainZ = Context.GetHeightAtPosition(obj.Origin.X, obj.Origin.Y);
            if (MathF.Abs(obj.Origin.Z - terrainZ) < 0.001f) return;

            var oldPos = obj.Origin;
            var newPos = new Vector3(obj.Origin.X, obj.Origin.Y, terrainZ);

            var cmd = new MoveObjectCommand(Context, sel.SelectedLandblockKey, sel.SelectedObjectIndex, oldPos, newPos);
            _commandHistory.ExecuteCommand(cmd);
            sel.RefreshFromDocument(doc);
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
        }

        /// <summary>
        /// Converts a quaternion to Euler angles (degrees) in XYZ order.
        /// </summary>
        private static void QuaternionToEuler(Quaternion q, out float xDeg, out float yDeg, out float zDeg) {
            // Roll (X)
            float sinr_cosp = 2.0f * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
            float roll = MathF.Atan2(sinr_cosp, cosr_cosp);

            // Pitch (Y)
            float sinp = 2.0f * (q.W * q.Y - q.Z * q.X);
            float pitch = MathF.Abs(sinp) >= 1.0f
                ? MathF.CopySign(MathF.PI / 2f, sinp)
                : MathF.Asin(sinp);

            // Yaw (Z)
            float siny_cosp = 2.0f * (q.W * q.Z + q.X * q.Y);
            float cosy_cosp = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
            float yaw = MathF.Atan2(siny_cosp, cosy_cosp);

            xDeg = roll * 180f / MathF.PI;
            yDeg = pitch * 180f / MathF.PI;
            zDeg = yaw * 180f / MathF.PI;
        }

        /// <summary>
        /// Converts Euler angles (degrees) in XYZ order to a quaternion.
        /// </summary>
        private static Quaternion EulerToQuaternion(float xDeg, float yDeg, float zDeg) {
            float xRad = xDeg * MathF.PI / 180f;
            float yRad = yDeg * MathF.PI / 180f;
            float zRad = zDeg * MathF.PI / 180f;

            var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, xRad);
            var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yRad);
            var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, zRad);

            return Quaternion.Normalize(qz * qy * qx);
        }

        private LandblockDocument? GetDocument() {
            var sel = Context.ObjectSelection;
            var docId = $"landblock_{sel.SelectedLandblockKey:X4}";
            return Context.TerrainSystem.DocumentManager
                .GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
        }

        public override void OnActivated() {
            UpdateSelectionInfo();
        }

        public override void OnDeactivated() {
            // Clear placement mode when switching sub-tools
            Context.ObjectSelection.IsPlacementMode = false;
            Context.ObjectSelection.PlacementPreview = null;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed) return false;

            var sel = Context.ObjectSelection;

            // Placement mode: click terrain to place the object
            if (sel.IsPlacementMode && sel.PlacementPreview.HasValue) {
                if (mouseState.IsOverTerrain && mouseState.TerrainHit.HasValue) {
                    var terrainPos = mouseState.TerrainHit.Value.HitPosition;
                    var preview = sel.PlacementPreview.Value;

                    int lbX = (int)Math.Floor(terrainPos.X / 192f);
                    int lbY = (int)Math.Floor(terrainPos.Y / 192f);
                    ushort lbKey = (ushort)((lbX << 8) | lbY);

                    var newObj = new StaticObject {
                        Id = preview.Id,
                        IsSetup = preview.IsSetup,
                        Origin = terrainPos,
                        Orientation = preview.Orientation,
                        Scale = preview.Scale
                    };

                    var cmd = new AddObjectCommand(Context, lbKey, newObj);
                    _commandHistory.ExecuteCommand(cmd);
                    Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
                    Context.ObjectSelection.Select(newObj, lbKey, cmd.AddedIndex, false);

                    Console.WriteLine($"[Selector] Placed object 0x{newObj.Id:X8} at ({terrainPos.X:F1}, {terrainPos.Y:F1}, {terrainPos.Z:F1})");
                    return true;
                }
                return false;
            }

            // Normal selection (Ctrl+Click = toggle multi-select)
            if (mouseState.ObjectHit.HasValue && mouseState.ObjectHit.Value.Hit) {
                if (mouseState.CtrlPressed) {
                    Context.ObjectSelection.ToggleSelectFromHit(mouseState.ObjectHit.Value);
                }
                else {
                    Context.ObjectSelection.SelectFromHit(mouseState.ObjectHit.Value);
                }
                return true;
            }
            else {
                Context.ObjectSelection.Deselect();
                return false;
            }
        }

        public override bool HandleMouseUp(MouseState mouseState) => false;

        public override bool HandleMouseMove(MouseState mouseState) {
            var sel = Context.ObjectSelection;
            if (sel.IsPlacementMode && sel.PlacementPreview.HasValue && mouseState.IsOverTerrain && mouseState.TerrainHit.HasValue) {
                var terrainPos = mouseState.TerrainHit.Value.HitPosition;
                var preview = sel.PlacementPreview.Value;
                sel.PlacementPreview = new StaticObject {
                    Id = preview.Id,
                    IsSetup = preview.IsSetup,
                    Origin = terrainPos,
                    Orientation = preview.Orientation,
                    Scale = preview.Scale
                };
            }
            return false;
        }
    }
}

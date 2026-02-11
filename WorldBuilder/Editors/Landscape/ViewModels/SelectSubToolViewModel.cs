using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

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


        // Marquee (box) select state
        [ObservableProperty] private bool _isMarqueeActive;
        [ObservableProperty] private Vector2 _marqueeStart;
        [ObservableProperty] private Vector2 _marqueeEnd;
        private bool _marqueeCtrlHeld;

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

                    var placementPos = terrainPos;
                    var orientation = preview.Orientation;

                    // Building-specific placement: cell-center snapping, donor orientation, terrain flattening.
                    // Non-building objects (scenery, trees, etc.) are placed at the exact click position.
                    var dats = Context.TerrainSystem.Dats;
                    bool isBuilding = dats != null && BuildingBlueprintCache.IsBuildingModelId(preview.Id, dats);

                    if (isBuilding) {
                        // Snap to the center of the nearest outdoor cell (24x24 grid).
                        // AC only checks building collision in the player's current outdoor cell,
                        // so off-center buildings get walk-through walls on the nearest cell edge.
                        // Original AC buildings are always at cell centers (e.g. donor (36,156) = (12,12) in cell).
                        // Edge cells 0 and 7 are excluded to maintain ~36-unit landblock edge clearance.
                        float lbOriginX = lbX * 192f;
                        float lbOriginY = lbY * 192f;
                        float localX = placementPos.X - lbOriginX;
                        float localY = placementPos.Y - lbOriginY;

                        int cellX = Math.Clamp((int)(localX / 24f), 1, 6);
                        int cellY = Math.Clamp((int)(localY / 24f), 1, 6);

                        localX = cellX * 24f + 12f;
                        localY = cellY * 24f + 12f;

                        placementPos = new Vector3(lbOriginX + localX, lbOriginY + localY, placementPos.Z);

                        // Use the donor building's orientation so the interior cell geometry
                        // aligns with the physics BSP.
                        var blueprint = BuildingBlueprintCache.GetBlueprint(preview.Id, dats);
                        if (blueprint != null) {
                            orientation = blueprint.DonorOrientation;
                        }
                    }

                    var newObj = new StaticObject {
                        Id = preview.Id,
                        IsSetup = preview.IsSetup,
                        Origin = placementPos,
                        Orientation = orientation,
                        Scale = preview.Scale
                    };

                    // Flatten terrain under buildings only — scenery objects sit on existing terrain.
                    if (isBuilding) {
                        float? snappedZ = FlattenTerrainUnderBuilding(newObj);
                        if (snappedZ.HasValue && Math.Abs(snappedZ.Value - newObj.Origin.Z) > 0.01f) {
                            newObj.Origin = new Vector3(newObj.Origin.X, newObj.Origin.Y, snappedZ.Value);
                        }
                    }

                    var cmd = new AddObjectCommand(Context, lbKey, newObj);
                    _commandHistory.ExecuteCommand(cmd);
                    Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
                    Context.ObjectSelection.Select(newObj, lbKey, cmd.AddedIndex, false);

                    Console.WriteLine($"[Selector] Placed object 0x{newObj.Id:X8} at ({newObj.Origin.X:F1}, {newObj.Origin.Y:F1}, {newObj.Origin.Z:F1})");

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
                // Clicked empty space — start marquee drag
                if (!mouseState.CtrlPressed) {
                    Context.ObjectSelection.Deselect();
                }
                IsMarqueeActive = true;
                MarqueeStart = mouseState.Position;
                MarqueeEnd = mouseState.Position;
                _marqueeCtrlHeld = mouseState.CtrlPressed;
                return false;
            }
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            if (IsMarqueeActive) {
                IsMarqueeActive = false;
                MarqueeEnd = mouseState.Position;

                // Only perform box select if the rectangle is large enough (avoid accidental tiny drags)
                float width = MathF.Abs(MarqueeEnd.X - MarqueeStart.X);
                float height = MathF.Abs(MarqueeEnd.Y - MarqueeStart.Y);
                if (width > 5f || height > 5f) {
                    PerformBoxSelect(mouseState);
                }
                return true;
            }
            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            // Marquee drag update
            if (IsMarqueeActive && mouseState.LeftPressed) {
                MarqueeEnd = mouseState.Position;
                return true;
            }

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

        private void PerformBoxSelect(MouseState mouseState) {
            var scene = Context.TerrainSystem.Scene;
            var camera = scene.CameraManager.Current;
            var screenSize = camera.ScreenSize;

            var hits = ObjectRaycast.BoxSelect(
                MarqueeStart, MarqueeEnd,
                (int)screenSize.X, (int)screenSize.Y,
                camera, scene);

            if (hits.Count == 0) return;

            var sel = Context.ObjectSelection;
            if (!_marqueeCtrlHeld) {
                sel.Deselect();
            }

            foreach (var hit in hits) {
                sel.ToggleSelect(hit.Object, hit.LandblockKey, hit.ObjectIndex, hit.IsScenery);
            }

            Console.WriteLine($"[Selector] Marquee selected {hits.Count} object(s)");
        }

        /// <summary>
        /// Flattens terrain vertices under a building's footprint to the building's Z height.
        /// Only applies to known building models. Uses the model's bounding box to determine
        /// the rectangular footprint.
        /// </summary>
        /// <summary>
        /// Returns the snapped Z height (from the height table) or null if flattening didn't apply.
        /// </summary>
        private float? FlattenTerrainUnderBuilding(StaticObject obj) {
            try {
                // Start with model bounds from the object manager
                var bounds = Context.TerrainSystem.Scene._objectManager.GetBounds(obj.Id, obj.IsSetup);
                if (!bounds.HasValue) return null;

                var (localMin, localMax) = bounds.Value;

                // Compute world-space AABB from exterior model
                float worldMinX = obj.Origin.X + localMin.X;
                float worldMaxX = obj.Origin.X + localMax.X;
                float worldMinY = obj.Origin.Y + localMin.Y;
                float worldMaxY = obj.Origin.Y + localMax.Y;

                // Add a small margin around the model bounds for terrain smoothing.
                // We no longer expand by EnvCell positions with 24m margins -- that created
                // a footprint spanning many landblocks. The model bounds + a reasonable margin
                // covers the actual building footprint where terrain needs flattening.
                const float flattenMargin = 6f;
                worldMinX -= flattenMargin;
                worldMaxX += flattenMargin;
                worldMinY -= flattenMargin;
                worldMaxY += flattenMargin;

                var heightTable = Context.TerrainSystem.Region.LandDefs.LandHeightTable;

                // Get all terrain vertices in the rectangular footprint
                var vertices = PaintCommand.GetVerticesInRect(worldMinX, worldMinY, worldMaxX, worldMaxY, Context);
                if (vertices.Count == 0) return null;

                // Find the MAXIMUM height byte among all vertices in the footprint.
                // Flattening to the max ensures terrain is only raised, never lowered,
                // so the building sits ON the terrain without its base going below ground.
                var landblockDataCache = new Dictionary<ushort, TerrainEntry[]>();
                byte maxHeight = 0;
                foreach (var (lbId, vIndex, _) in vertices) {
                    if (!landblockDataCache.TryGetValue(lbId, out var data)) {
                        data = Context.TerrainSystem.GetLandblockTerrain(lbId);
                        if (data == null) continue;
                        landblockDataCache[lbId] = data;
                    }
                    byte h = data[vIndex].Height;
                    if (h > maxHeight) maxHeight = h;
                }

                // Use the higher of: the max vertex height or the placement click height.
                // This handles both slopes (use max vertex) and valleys (use click height).
                byte clickHeight = FindClosestHeightByte(heightTable, obj.Origin.Z);
                byte targetHeight = Math.Max(maxHeight, clickHeight);

                // Build change set
                var changes = new Dictionary<ushort, List<(int VertexIndex, byte OriginalValue, byte NewValue)>>();

                foreach (var (lbId, vIndex, _) in vertices) {
                    if (!landblockDataCache.TryGetValue(lbId, out var data)) continue;

                    if (!changes.TryGetValue(lbId, out var list)) {
                        list = new List<(int, byte, byte)>();
                        changes[lbId] = list;
                    }

                    if (list.Any(c => c.VertexIndex == vIndex)) continue;

                    byte original = data[vIndex].Height;
                    if (original == targetHeight) continue;
                    list.Add((vIndex, original, targetHeight));
                }

                if (changes.Count == 0) return heightTable[targetHeight];

                // Snapshot original terrain data before applying changes, so we can compute
                // height deltas for nearby static objects afterward.
                var originalDataSnapshots = new Dictionary<ushort, TerrainEntry[]>();
                foreach (var (lbId, _) in changes) {
                    if (landblockDataCache.TryGetValue(lbId, out var data)) {
                        originalDataSnapshots[lbId] = (TerrainEntry[])data.Clone();
                    }
                }

                // Apply terrain changes directly (without HeightChangeCommand's static object Z adjustment,
                // which would cause nearby existing buildings to float/sink)
                var batchChanges = new Dictionary<ushort, Dictionary<byte, uint>>();
                foreach (var (lbId, changeList) in changes) {
                    if (!landblockDataCache.TryGetValue(lbId, out var terrainData)) continue;
                    if (!batchChanges.TryGetValue(lbId, out var lbChanges)) {
                        lbChanges = new Dictionary<byte, uint>();
                        batchChanges[lbId] = lbChanges;
                    }
                    foreach (var (vIndex, _, newVal) in changeList) {
                        var newEntry = terrainData[vIndex] with { Height = newVal };
                        lbChanges[(byte)vIndex] = newEntry.ToUInt();
                    }
                }
                var modifiedLandblocks = Context.TerrainSystem.UpdateLandblocksBatch(TerrainField.Height, batchChanges);
                Context.MarkLandblocksModified(modifiedLandblocks);

                // Adjust Z of nearby non-building static objects to preserve their offset
                // from the terrain surface. Buildings are excluded to avoid shifting them.
                AdjustNearbyObjectHeights(changes, originalDataSnapshots, heightTable, obj);

                // Re-sample the interpolated terrain height at the exact building position
                // after flattening, rather than using the raw height table value.
                // This accounts for edge effects where the building straddles flattened
                // and non-flattened vertices, matching how the AC client computes height.
                float interpolatedZ = Context.GetHeightAtPosition(obj.Origin.X, obj.Origin.Y);

                Console.WriteLine($"[Selector] Flattened {vertices.Count} terrain vertices under building 0x{obj.Id:X8} (tableHeight={heightTable[targetHeight]:F1}, interpolatedZ={interpolatedZ:F2})");
                return interpolatedZ;
            }
            catch (Exception ex) {
                Console.WriteLine($"[Selector] Error flattening terrain: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the height byte (0-255) whose height table value is closest to the target Z.
        /// </summary>
        private static byte FindClosestHeightByte(float[] heightTable, float targetZ) {
            byte best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < heightTable.Length && i < 256; i++) {
                float dist = Math.Abs(heightTable[i] - targetZ);
                if (dist < bestDist) {
                    bestDist = dist;
                    best = (byte)i;
                }
            }
            return best;
        }

        /// <summary>
        /// After terrain flattening, adjusts the Z of ALL nearby static objects (including
        /// existing buildings) to preserve their offset from the terrain surface. Only the
        /// building being placed right now is skipped (its Z is set explicitly afterward).
        /// Existing buildings ARE adjusted -- at export time, SaveToDatsInternal detects the
        /// position change and MoveBuildingEnvCells correctly repositions the interior cells.
        /// </summary>
        private void AdjustNearbyObjectHeights(
            Dictionary<ushort, List<(int VertexIndex, byte OriginalValue, byte NewValue)>> changes,
            Dictionary<ushort, TerrainEntry[]> originalDataSnapshots,
            float[] heightTable,
            StaticObject placedBuilding) {

            var docManager = Context.TerrainSystem.DocumentManager;
            int totalAdjusted = 0;

            foreach (var lbId in changes.Keys) {
                if (!originalDataSnapshots.TryGetValue(lbId, out var originalData)) continue;

                var newData = Context.TerrainSystem.GetLandblockTerrain(lbId);
                if (newData == null) continue;

                var docId = $"landblock_{lbId:X4}";
                var doc = docManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc == null || doc.StaticObjectCount == 0) continue;

                uint landblockX = (uint)(lbId >> 8) & 0xFF;
                uint landblockY = (uint)(lbId & 0xFF);
                float baseLbX = landblockX * 192f;
                float baseLbY = landblockY * 192f;

                for (int i = 0; i < doc.StaticObjectCount; i++) {
                    var obj = doc.GetStaticObject(i);

                    // Skip only the building we are placing right now -- its Z will be set
                    // explicitly by the caller after this method returns.
                    if (obj.Id == placedBuilding.Id &&
                        Vector3.Distance(obj.Origin, placedBuilding.Origin) < 1.0f) {
                        continue;
                    }

                    float localX = obj.Origin.X - baseLbX;
                    float localY = obj.Origin.Y - baseLbY;

                    // Skip objects outside this landblock's bounds
                    if (localX < 0 || localX > 192f || localY < 0 || localY > 192f) continue;

                    // Sample terrain height at object position using original and new data
                    float oldTerrainZ = TerrainDataManager.SampleHeightTriangle(
                        originalData, heightTable, localX, localY, landblockX, landblockY);
                    float newTerrainZ = TerrainDataManager.SampleHeightTriangle(
                        newData, heightTable, localX, localY, landblockX, landblockY);

                    float delta = newTerrainZ - oldTerrainZ;
                    if (Math.Abs(delta) < 0.001f) continue;

                    // Preserve the object's offset from the terrain surface
                    doc.SetStaticObjectHeight(i, obj.Origin.Z + delta);
                    totalAdjusted++;
                }
            }

            if (totalAdjusted > 0) {
                Console.WriteLine($"[Selector] Adjusted Z of {totalAdjusted} nearby objects (incl. buildings) after terrain flattening");
            }
        }
    }
}

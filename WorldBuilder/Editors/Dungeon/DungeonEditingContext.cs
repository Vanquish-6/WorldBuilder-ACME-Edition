using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Shared editing state that tools use to interact with the dungeon document,
    /// scene, and selection without coupling to the full editor ViewModel.
    /// Analogous to the landscape editor's TerrainEditingContext.
    /// </summary>
    public class DungeonEditingContext {
        public DungeonDocument? Document { get; set; }
        public DungeonScene? Scene { get; set; }
        public IDatReaderWriter? Dats { get; set; }
        public RoomPaletteViewModel? RoomPalette { get; set; }
        public DungeonCommandHistory CommandHistory { get; set; } = new();
        public PortalCompatibilityIndex? PortalIndex { get; set; }
        public PortalGeometryCache? GeometryCache { get; set; }

        // Selection state — the editor VM owns the UI-bound properties, the context
        // provides the raw state that tools read/write.
        public LoadedEnvCell? SelectedCell { get; set; }
        public List<LoadedEnvCell> SelectedCells { get; } = new();
        public bool HasSelectedCell => SelectedCell != null;

        public ushort SelectedObjCellNum { get; set; }
        public int SelectedObjIndex { get; set; } = -1;
        public bool HasSelectedObject => SelectedObjIndex >= 0;

        public bool HasDungeon => Document != null && Document.Cells.Count > 0;

        // Drag state
        public Vector3 DragStartHit { get; set; }
        public Vector3 DragStartOrigin { get; set; }

        // Grid snap
        public bool GridSnapEnabled { get; set; }
        public float GridSnapSize { get; set; } = 5f;

        public event Action? SelectionChanged;
        public event Action? RenderingRefreshNeeded;
        public event Action<string>? StatusTextChanged;
        public event Action? CameraFocusRequested;

        public void NotifySelectionChanged() => SelectionChanged?.Invoke();
        public void NotifyRenderingRefresh() => RenderingRefreshNeeded?.Invoke();
        public void SetStatus(string text) => StatusTextChanged?.Invoke(text);
        public void RequestCameraFocus() => CameraFocusRequested?.Invoke();

        /// <summary>Compute ray from mouse position through the camera.</summary>
        public (Vector3 origin, Vector3 direction)? ComputeRay(Lib.MouseState mouse) {
            if (Scene?.Camera == null) return null;
            var camera = Scene.Camera;
            float w = camera.ScreenSize.X, h = camera.ScreenSize.Y;
            if (w <= 0 || h <= 0) return null;

            float ndcX = 2f * mouse.Position.X / w - 1f;
            float ndcY = 2f * mouse.Position.Y / h - 1f;

            var projection = camera.GetProjectionMatrix();
            var view = camera.GetViewMatrix();
            if (!Matrix4x4.Invert(view * projection, out var vpInverse)) return null;

            var nearW = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), vpInverse);
            var farW = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), vpInverse);
            nearW /= nearW.W;
            farW /= farW.W;

            var origin = new Vector3(nearW.X, nearW.Y, nearW.Z);
            var dir = Vector3.Normalize(new Vector3(farW.X, farW.Y, farW.Z) - origin);
            return (origin, dir);
        }

        public EnvCellManager.EnvCellRaycastHit Raycast(Vector3 origin, Vector3 dir) {
            return Scene?.EnvCellManager?.Raycast(origin, dir) ?? default;
        }

        public void RefreshRendering() {
            if (Scene == null || Document == null) return;
            if (Dats != null)
                Document.RecomputePortalFlags(Dats);
            Scene.RefreshFromDocument(Document);
            NotifyRenderingRefresh();
        }

        public List<ushort> GetSurfacesForRoom(RoomEntry room) {
            if (room.DefaultSurfaces.Count > 0)
                return new List<ushort>(room.DefaultSurfaces);

            // Try KB catalog for real surfaces from scanned dungeons
            var kb = DungeonKnowledgeBuilder.LoadCached();
            if (kb != null) {
                var cr = kb.Catalog.FirstOrDefault(c => c.EnvId == room.EnvironmentId && c.CellStruct == room.CellStructureIndex);
                if (cr != null && cr.SampleSurfaces.Count > 0) {
                    room.DefaultSurfaces = new List<ushort>(cr.SampleSurfaces);
                    return room.DefaultSurfaces;
                }
            }

            if (Dats == null) return new List<ushort>();

            // Try direct DAT lookup for an EnvCell using this room type
            try {
                var lbiIds = Dats.Dats.Cell.GetAllIdsOfType<DatReaderWriter.DBObjs.LandBlockInfo>().Take(500).ToArray();
                foreach (var lbiId in lbiIds) {
                    if (!Dats.TryGet<DatReaderWriter.DBObjs.LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;
                    uint lbId = lbiId >> 16;
                    for (uint i = 0; i < lbi.NumCells && i < 50; i++) {
                        uint cellId = (lbId << 16) | (0x0100 + i);
                        if (!Dats.TryGet<DatReaderWriter.DBObjs.EnvCell>(cellId, out var ec)) continue;
                        if (ec.EnvironmentId == room.EnvironmentId && ec.CellStructure == room.CellStructureIndex && ec.Surfaces.Count > 0) {
                            room.DefaultSurfaces = new List<ushort>(ec.Surfaces);
                            return room.DefaultSurfaces;
                        }
                    }
                }
            }
            catch { }

            // Final fallback: count surface slots from CellStruct polygons
            uint envFileId = (uint)(room.EnvironmentId | 0x0D000000);
            if (Dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env) &&
                env.Cells.TryGetValue(room.CellStructureIndex, out var cellStruct)) {
                var portalIds = cellStruct.Portals != null ? new HashSet<ushort>(cellStruct.Portals) : new HashSet<ushort>();
                int maxIdx = -1;
                foreach (var kvp in cellStruct.Polygons) {
                    if (portalIds.Contains(kvp.Key)) continue;
                    if (kvp.Value.PosSurface > maxIdx) maxIdx = kvp.Value.PosSurface;
                }
                if (maxIdx >= 0) {
                    var fallback = Enumerable.Repeat((ushort)0x032A, maxIdx + 1).ToList();
                    room.DefaultSurfaces = fallback;
                    return fallback;
                }
            }
            return new List<ushort>();
        }

        /// <summary>
        /// Build EnvCell objects for a prefab placed at a given base position.
        /// Uses stored relative transforms — no PortalSnapper needed.
        /// Used for both placement preview and actual placement.
        /// </summary>
        public List<DatReaderWriter.DBObjs.EnvCell> BuildPrefabEnvCells(
            DungeonPrefab prefab, Vector3 baseOrigin, Quaternion baseRot,
            int connectingCellIndex = 0) {

            var result = new List<DatReaderWriter.DBObjs.EnvCell>();
            if (Document == null) return result;

            if (connectingCellIndex < 0 || connectingCellIndex >= prefab.Cells.Count)
                return result;

            // Compute the world transform that maps prefab-local to world space
            var connectPC = prefab.Cells[connectingCellIndex];
            var connectOffset = new Vector3(connectPC.OffsetX, connectPC.OffsetY, connectPC.OffsetZ);
            var connectRelRot = Quaternion.Normalize(new Quaternion(connectPC.RotX, connectPC.RotY, connectPC.RotZ, connectPC.RotW));

            Quaternion invRelRot = connectRelRot.LengthSquared() > 0.01f ? Quaternion.Inverse(connectRelRot) : Quaternion.Identity;
            var worldBaseRot = Quaternion.Normalize(baseRot * invRelRot);
            var worldBaseOrigin = baseOrigin - Vector3.Transform(connectOffset, worldBaseRot);

            ushort lbKey = Document.LandblockKey;

            for (int i = 0; i < prefab.Cells.Count; i++) {
                var pc = prefab.Cells[i];
                var offset = new Vector3(pc.OffsetX, pc.OffsetY, pc.OffsetZ);
                var relRot = Quaternion.Normalize(new Quaternion(pc.RotX, pc.RotY, pc.RotZ, pc.RotW));

                var worldOrigin = worldBaseOrigin + Vector3.Transform(offset, worldBaseRot);
                var worldRot = Quaternion.Normalize(worldBaseRot * relRot);

                var envCell = new DatReaderWriter.DBObjs.EnvCell {
                    Id = (uint)((lbKey << 16) | (0xFF00 + i)),
                    EnvironmentId = pc.EnvId,
                    CellStructure = pc.CellStruct,
                    Position = new DatReaderWriter.Types.Frame {
                        Origin = worldOrigin,
                        Orientation = worldRot
                    }
                };
                envCell.Surfaces.AddRange(pc.Surfaces);
                result.Add(envCell);
            }

            return result;
        }
    }
}

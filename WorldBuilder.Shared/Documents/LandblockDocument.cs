using CommunityToolkit.Mvvm.DependencyInjection;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using MemoryPack;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace WorldBuilder.Shared.Documents {
    [MemoryPack.MemoryPackable]
    public partial struct StaticObject {
        public uint Id; // GfxObj or Setup ID
        public bool IsSetup; // True for Setup, false for GfxObj
        public Vector3 Origin; // World-space position
        public Quaternion Orientation; // World-space rotation
        public Vector3 Scale;
    }

    [MemoryPack.MemoryPackable]
    public partial class LandblockData {
        public List<StaticObject> StaticObjects = new();
    }

    public partial class LandblockDocument : BaseDocument {
        public override string Type => nameof(LandblockDocument);

        [MemoryPackInclude]
        private LandblockData _data = new();

        public LandblockDocument(ILogger logger) : base(logger) {
        }

        protected override async Task<bool> InitInternal(IDatReaderWriter datreader, DocumentManager documentManager) {
            var lbIdHex = Id.Replace("landblock_", "");
            var lbId = uint.Parse(lbIdHex, System.Globalization.NumberStyles.HexNumber);
            var infoId = lbId << 16 | 0xFFFE;

            if (datreader.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                foreach (var obj in lbi.Objects) {
                    _data.StaticObjects.Add(new StaticObject {
                        Id = obj.Id,
                        IsSetup = (obj.Id & 0x02000000) != 0,
                        Origin = Offset(obj.Frame.Origin, lbId),
                        Orientation = obj.Frame.Orientation,
                        Scale = Vector3.One
                    });
                }

                foreach (var building in lbi.Buildings) {
                    _data.StaticObjects.Add(new StaticObject {
                        Id = building.ModelId,
                        IsSetup = (building.ModelId & 0x02000000) != 0,
                        Origin = Offset(building.Frame.Origin, lbId),
                        Orientation = building.Frame.Orientation,
                        Scale = Vector3.One
                    });
                }
            }

            ClearDirty();
            return true;
        }

        private Vector3 Offset(Vector3 origin, uint lbId) {
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            return new Vector3(blockX * 192f + origin.X, blockY * 192f + origin.Y, origin.Z);
        }

        protected override byte[] SaveToProjectionInternal() {
            return MemoryPackSerializer.Serialize(_data);
        }

        protected override bool LoadFromProjectionInternal(byte[] projection) {
            _data = MemoryPackSerializer.Deserialize<LandblockData>(projection) ?? new();
            return true;
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            var lbIdHex = Id.Replace("landblock_", "");
            var lbId = uint.Parse(lbIdHex, System.Globalization.NumberStyles.HexNumber);
            var infoId = lbId << 16 | 0xFFFE;

            _logger.LogInformation("[LBDoc] Saving landblock 0x{LbId:X4} — {ObjCount} static objects", lbId, _data.StaticObjects.Count);

            // Read original LandBlockInfo to get building data (portals, cells, etc.)
            if (!datwriter.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                lbi = new LandBlockInfo();
                lbi.Id = infoId;
                _logger.LogInformation("[LBDoc]   No original LandBlockInfo found, creating new");
            }
            else {
                _logger.LogInformation("[LBDoc]   Original LandBlockInfo: {ObjCount} objects, {BldCount} buildings, NumCells={NumCells}",
                    lbi.Objects.Count, lbi.Buildings.Count, lbi.NumCells);
            }

            // Track which StaticObjects have been matched to an original building
            var consumed = new HashSet<int>();
            var originalNumCells = lbi.NumCells;

            // For each original building, try to find a matching StaticObject.
            // If found: update the building's position (preserving portal/cell data).
            // If not found: the building was deleted by the user.
            // NOTE: We do NOT use BFS exclusion sets here. In original AC data, each building's
            // cell graph is isolated (CellPortals don't cross between buildings), so the BFS
            // naturally stays within each building. Using exclusion sets caused incorrect cell
            // omission that led to missing EnvCells and ACE server crashes.
            var survivingBuildings = new List<BuildingInfo>();
            foreach (var building in lbi.Buildings) {
                var buildingWorldPos = Offset(building.Frame.Origin, lbId);

                // Find the best matching StaticObject (same ID, closest to original position)
                int bestIdx = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < _data.StaticObjects.Count; i++) {
                    if (consumed.Contains(i)) continue;
                    var obj = _data.StaticObjects[i];
                    if (obj.Id != building.ModelId) continue;

                    float dist = Vector3.Distance(obj.Origin, buildingWorldPos);
                    if (dist < bestDist) {
                        bestDist = dist;
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0) {
                    // Building still exists — update its Frame from the StaticObject (handles moves)
                    consumed.Add(bestIdx);
                    var matched = _data.StaticObjects[bestIdx];
                    var newLocal = ReverseOffset(matched.Origin, lbId);
                    var oldLocal = building.Frame.Origin;
                    var oldRotation = building.Frame.Orientation;

                    var hasMoved = Vector3.Distance(newLocal, oldLocal) > 0.01f;
                    // Quaternion dot of 1.0 means identical; allow small float precision tolerance
                    var dotProduct = Math.Abs(Quaternion.Dot(matched.Orientation, oldRotation));
                    var hasRotated = dotProduct < 0.9999f;

                    if (hasMoved || hasRotated) {
                        _logger.LogInformation("[LBDoc]   Building 0x{Id:X8} MOVED: ({OldX:F1},{OldY:F1},{OldZ:F1}) -> ({NewX:F1},{NewY:F1},{NewZ:F1})",
                            building.ModelId, oldLocal.X, oldLocal.Y, oldLocal.Z, newLocal.X, newLocal.Y, newLocal.Z);

                        // Discover and move all EnvCells belonging to this building
                        var cellIds = CollectBuildingCellIds(building, datwriter, lbId);
                        if (cellIds.Count > 0) {
                            _logger.LogInformation("[LBDoc]     Found {CellCount} EnvCells for building 0x{Id:X8}", cellIds.Count, building.ModelId);
                            MoveBuildingEnvCells(cellIds, datwriter, lbId, oldLocal, oldRotation, newLocal, matched.Orientation, iteration);
                        }
                    }

                    building.Frame = new Frame {
                        Origin = newLocal,
                        Orientation = matched.Orientation
                    };
                    survivingBuildings.Add(building);
                }
                else {
                    // Building was deleted -- count its EnvCells for NumCells adjustment
                    var deletedCellIds = CollectBuildingCellIds(building, datwriter, lbId);
                    if (deletedCellIds.Count > 0) {
                        _logger.LogInformation("[LBDoc]   Building 0x{Id:X8} DELETED — {CellCount} EnvCells orphaned (will be cleaned up in Phase 2)",
                            building.ModelId, deletedCellIds.Count);
                        lbi.NumCells -= (uint)deletedCellIds.Count;
                    }
                    else {
                        _logger.LogInformation("[LBDoc]   Building 0x{Id:X8} DELETED (no EnvCells)", building.ModelId);
                    }
                }
            }

            lbi.Buildings.Clear();
            lbi.Buildings.AddRange(survivingBuildings);

            if (lbi.NumCells != originalNumCells) {
                _logger.LogInformation("[LBDoc]   NumCells adjusted: {Old} -> {New}", originalNumCells, lbi.NumCells);
            }

            // Non-consumed StaticObjects: check for new buildings, rest become Stab entries
            lbi.Objects.Clear();
            for (int i = 0; i < _data.StaticObjects.Count; i++) {
                if (consumed.Contains(i)) continue;

                var obj = _data.StaticObjects[i];
                var localOrigin = ReverseOffset(obj.Origin, lbId);

                // Check if this is a known building model — if so, instantiate with EnvCells
                if (BuildingBlueprintCache.IsBuildingModelId(obj.Id, datwriter)) {
                    var blueprint = BuildingBlueprintCache.GetBlueprint(obj.Id, datwriter, _logger);
                    if (blueprint != null) {
                        var result = BuildingBlueprintCache.InstantiateBlueprint(
                            blueprint, localOrigin, obj.Orientation,
                            lbId, lbi.NumCells, datwriter, iteration, _logger);

                        if (result.HasValue) {
                            lbi.Buildings.Add(result.Value.building);
                            lbi.NumCells += (uint)result.Value.cellCount;
                            _logger.LogInformation("[LBDoc]   NEW building 0x{Id:X8} placed with {CellCount} EnvCells",
                                obj.Id, result.Value.cellCount);
                            continue; // Don't add as Stab
                        }
                        else {
                            _logger.LogWarning("[LBDoc]   Failed to instantiate building 0x{Id:X8}, adding as regular object", obj.Id);
                        }
                    }
                    else {
                        _logger.LogWarning("[LBDoc]   No blueprint found for building 0x{Id:X8}, adding as regular object", obj.Id);
                    }
                }

                lbi.Objects.Add(new Stab {
                    Id = obj.Id,
                    Frame = new Frame {
                        Origin = localOrigin,
                        Orientation = obj.Orientation
                    }
                });
            }

            _logger.LogInformation("[LBDoc]   Result: {ObjCount} objects, {BldCount} buildings",
                lbi.Objects.Count, lbi.Buildings.Count);

            if (!datwriter.TrySave(lbi, iteration)) {
                _logger.LogError("[LBDoc]   FAILED to save LandBlockInfo 0x{InfoId:X8}", infoId);
                return Task.FromResult(false);
            }

            _logger.LogInformation("[LBDoc]   Saved LandBlockInfo 0x{InfoId:X8} successfully", infoId);
            return Task.FromResult(true);
        }

        private Vector3 ReverseOffset(Vector3 worldPos, uint lbId) {
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            return new Vector3(worldPos.X - blockX * 192f, worldPos.Y - blockY * 192f, worldPos.Z);
        }

        /// <summary>
        /// Returns true if a cell ID is in the valid EnvCell range (0x0100-0xFFFD).
        /// Excludes outdoor LandCells (0x0001-0x0040), LandBlockInfo (0xFFFE), and LandBlock (0xFFFF).
        /// </summary>
        private static bool IsEnvCellId(ushort cellId) => cellId >= 0x0100 && cellId <= 0xFFFD;

        /// <summary>
        /// Walks a building's portal graph to collect all EnvCell IDs (the CCCC portion, 0x0100-0xFFFD)
        /// that belong to this building. Starts from BuildingPortal.OtherCellId and StabList,
        /// then follows CellPortal links in each discovered EnvCell.
        /// </summary>
        /// <summary>
        /// Walks a building's portal graph to collect all EnvCell IDs (0x0100-0xFFFD).
        /// An optional exclusion set prevents the BFS from crossing into cells belonging
        /// to other buildings in the same landblock (fixes duplicate building conflicts).
        /// </summary>
        private HashSet<ushort> CollectBuildingCellIds(BuildingInfo building, IDatReaderWriter datAccess, uint lbId,
            HashSet<ushort>? excludeCellIds = null) {
            var cellIds = new HashSet<ushort>();
            var toVisit = new Queue<ushort>();

            // Seed from building portals
            foreach (var portal in building.Portals) {
                if (IsEnvCellId(portal.OtherCellId) &&
                    (excludeCellIds == null || !excludeCellIds.Contains(portal.OtherCellId)) &&
                    cellIds.Add(portal.OtherCellId))
                    toVisit.Enqueue(portal.OtherCellId);

                foreach (var stab in portal.StabList) {
                    if (IsEnvCellId(stab) &&
                        (excludeCellIds == null || !excludeCellIds.Contains(stab)) &&
                        cellIds.Add(stab))
                        toVisit.Enqueue(stab);
                }
            }

            // BFS: follow CellPortal links to discover connected cells
            while (toVisit.Count > 0) {
                var cellNum = toVisit.Dequeue();
                uint fullCellId = (lbId << 16) | cellNum;

                if (datAccess.TryGet<EnvCell>(fullCellId, out var envCell)) {
                    foreach (var cp in envCell.CellPortals) {
                        if (IsEnvCellId(cp.OtherCellId) &&
                            (excludeCellIds == null || !excludeCellIds.Contains(cp.OtherCellId)) &&
                            cellIds.Add(cp.OtherCellId))
                            toVisit.Enqueue(cp.OtherCellId);
                    }
                }
            }

            return cellIds;
        }

        /// <summary>
        /// Moves all EnvCells belonging to a building by applying a position delta and rotation delta.
        /// Each EnvCell's Position is in landblock-local space, same as the building's Frame.
        /// </summary>
        private void MoveBuildingEnvCells(
            HashSet<ushort> cellIds,
            IDatReaderWriter datAccess,
            uint lbId,
            Vector3 oldBuildingOrigin,
            Quaternion oldBuildingRotation,
            Vector3 newBuildingOrigin,
            Quaternion newBuildingRotation,
            int iteration) {

            var positionDelta = newBuildingOrigin - oldBuildingOrigin;
            var rotationDelta = newBuildingRotation * Quaternion.Inverse(oldBuildingRotation);
            var hasRotation = Math.Abs(Quaternion.Dot(rotationDelta, Quaternion.Identity) - 1.0f) > 0.0001f;

            foreach (var cellNum in cellIds) {
                uint fullCellId = (lbId << 16) | cellNum;

                if (!datAccess.TryGet<EnvCell>(fullCellId, out var envCell)) {
                    _logger.LogWarning("[LBDoc]     EnvCell 0x{CellId:X8} not found in dat, skipping", fullCellId);
                    continue;
                }

                if (hasRotation) {
                    // Rotate the cell's position around the building's old origin, then translate
                    var relativePos = envCell.Position.Origin - oldBuildingOrigin;
                    var rotatedPos = Vector3.Transform(relativePos, rotationDelta);
                    envCell.Position.Origin = newBuildingOrigin + rotatedPos;

                    // Compose rotation deltas for the cell's own orientation
                    envCell.Position.Orientation = Quaternion.Normalize(rotationDelta * envCell.Position.Orientation);

                    // Also transform static objects inside the cell (landblock-local coords)
                    foreach (var stab in envCell.StaticObjects) {
                        var relObj = stab.Frame.Origin - oldBuildingOrigin;
                        var rotObj = Vector3.Transform(relObj, rotationDelta);
                        stab.Frame = new Frame {
                            Origin = newBuildingOrigin + rotObj,
                            Orientation = Quaternion.Normalize(rotationDelta * stab.Frame.Orientation)
                        };
                    }
                }
                else {
                    // Translation only -- just shift the origin
                    envCell.Position.Origin += positionDelta;

                    // Also shift static objects inside the cell (landblock-local coords)
                    foreach (var stab in envCell.StaticObjects) {
                        stab.Frame = new Frame {
                            Origin = stab.Frame.Origin + positionDelta,
                            Orientation = stab.Frame.Orientation
                        };
                    }
                }

                if (!datAccess.TrySave(envCell, iteration)) {
                    _logger.LogError("[LBDoc]     FAILED to save EnvCell 0x{CellId:X8}", fullCellId);
                }
                else {
                    _logger.LogInformation("[LBDoc]     Moved EnvCell 0x{CellId:X8} to ({X:F1},{Y:F1},{Z:F1})",
                        fullCellId, envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);
                }
            }
        }

        public bool Apply(BaseDocumentEvent evt) {
            if (evt is StaticObjectUpdateEvent objEvt) {
                if (objEvt.IsAdd) {
                    _data.StaticObjects.Add(objEvt.Object);
                }
                else {
                    // Remove by matching Id and Origin
                    var idx = _data.StaticObjects.FindIndex(o =>
                        o.Id == objEvt.Object.Id &&
                        Vector3.Distance(o.Origin, objEvt.Object.Origin) < 0.01f);
                    if (idx >= 0) {
                        _data.StaticObjects.RemoveAt(idx);
                    }
                }
                MarkDirty();
                return true;
            }
            return true;
        }

        public IEnumerable<(Vector3 Position, Quaternion Rotation)> GetStaticSpawns() {
            foreach (var obj in _data.StaticObjects) {
                yield return (obj.Origin, obj.Orientation);
            }
        }

        public IEnumerable<StaticObject> GetStaticObjects() => _data.StaticObjects;

        /// <summary>
        /// Gets the number of static objects in this landblock
        /// </summary>
        public int StaticObjectCount => _data.StaticObjects.Count;

        /// <summary>
        /// Gets a static object by index
        /// </summary>
        public StaticObject GetStaticObject(int index) => _data.StaticObjects[index];

        /// <summary>
        /// Updates the Z (height) coordinate of a static object at the given index
        /// </summary>
        public void SetStaticObjectHeight(int index, float newZ) {
            if (index < 0 || index >= _data.StaticObjects.Count) return;
            var obj = _data.StaticObjects[index];
            obj.Origin = new Vector3(obj.Origin.X, obj.Origin.Y, newZ);
            _data.StaticObjects[index] = obj;
            MarkDirty();
        }

        /// <summary>
        /// Replaces a static object at the given index with updated data
        /// </summary>
        public void UpdateStaticObject(int index, StaticObject updatedObj) {
            if (index < 0 || index >= _data.StaticObjects.Count) return;
            _data.StaticObjects[index] = updatedObj;
            MarkDirty();
        }

        /// <summary>
        /// Adds a new static object to the landblock
        /// </summary>
        public int AddStaticObject(StaticObject obj) {
            _data.StaticObjects.Add(obj);
            MarkDirty();
            return _data.StaticObjects.Count - 1;
        }

        /// <summary>
        /// Removes the static object at the given index
        /// </summary>
        public bool RemoveStaticObject(int index) {
            if (index < 0 || index >= _data.StaticObjects.Count) return false;
            _data.StaticObjects.RemoveAt(index);
            MarkDirty();
            return true;
        }
    }
    public class StaticObjectUpdateEvent : TerrainUpdateEvent {
        public StaticObject Object { get; }
        public bool IsAdd { get; } // True for add, false for remove

        public StaticObjectUpdateEvent(StaticObject obj, bool isAdd) {
            Object = obj;
            IsAdd = isAdd;
        }
    }
}
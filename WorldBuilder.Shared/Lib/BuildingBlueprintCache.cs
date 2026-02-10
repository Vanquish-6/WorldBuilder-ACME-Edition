using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace WorldBuilder.Shared.Lib {

    /// <summary>
    /// Snapshot of a single EnvCell's data for blueprint storage.
    /// All positions are relative to the donor building's origin.
    /// </summary>
    public class EnvCellSnapshot {
        public ushort OriginalCellId;
        public EnvCellFlags Flags;
        public List<ushort> Surfaces = new();
        public ushort EnvironmentId;
        public ushort CellStructure;
        public Vector3 RelativeOrigin;
        public Quaternion Orientation;
        public List<CellPortal> CellPortals = new();
        public List<ushort> VisibleCells = new();
        public List<StabSnapshot> StaticObjects = new();
        public uint RestrictionObj;
    }

    /// <summary>
    /// Snapshot of a static object inside an EnvCell, with position relative to building origin.
    /// </summary>
    public class StabSnapshot {
        public uint Id;
        public Vector3 RelativeOrigin;
        public Quaternion Orientation;
    }

    /// <summary>
    /// A reusable blueprint for a building's interior layout (EnvCells, portals, etc.)
    /// extracted from an existing instance in the dat.
    /// </summary>
    public class BuildingBlueprint {
        public uint ModelId;
        public uint NumLeaves;
        /// <summary>The donor building's orientation, needed to rotate relative positions for new orientations.</summary>
        public Quaternion DonorOrientation;
        /// <summary>The landblock ID the donor building was extracted from, for byte-level comparison.</summary>
        public uint DonorLandblockId;
        public List<BuildingPortal> PortalTemplates = new();
        public List<EnvCellSnapshot> Cells = new();
        /// <summary>Maps original cell IDs to indices in the Cells list, for remapping.</summary>
        public Dictionary<ushort, int> OriginalCellIdToIndex = new();
    }

    /// <summary>
    /// Caches building model IDs and extracted blueprints for creating new building instances.
    /// Thread-safe via ConcurrentDictionary.
    /// </summary>
    public static class BuildingBlueprintCache {
        private static readonly ConcurrentDictionary<uint, BuildingBlueprint?> _blueprintCache = new();
        private static HashSet<uint>? _buildingModelIds;
        private static readonly object _scanLock = new();

        /// <summary>
        /// Checks if a model ID is a known building (has BuildingInfo in any landblock).
        /// Lazily scans all LandBlockInfo entries on first call.
        /// </summary>
        public static bool IsBuildingModelId(uint modelId, IDatReaderWriter dats) {
            EnsureBuildingIdsSscanned(dats);
            return _buildingModelIds!.Contains(modelId);
        }

        /// <summary>
        /// Gets or extracts a blueprint for the given building model ID.
        /// Returns null if no existing instance can be found in the dat.
        /// </summary>
        public static BuildingBlueprint? GetBlueprint(uint modelId, IDatReaderWriter dats, ILogger? logger = null) {
            return _blueprintCache.GetOrAdd(modelId, id => ExtractBlueprint(id, dats, logger));
        }

        /// <summary>
        /// Clears all cached data. Call when dat files change.
        /// </summary>
        public static void ClearCache() {
            _blueprintCache.Clear();
            _buildingModelIds = null;
        }

        private static void EnsureBuildingIdsSscanned(IDatReaderWriter dats) {
            if (_buildingModelIds != null) return;
            lock (_scanLock) {
                if (_buildingModelIds != null) return;

                var ids = new HashSet<uint>();
                var allLbiIds = dats.Dats.Cell.GetAllIdsOfType<LandBlockInfo>().ToArray();

                if (allLbiIds.Length == 0) {
                    // Brute-force fallback
                    for (uint x = 0; x < 255; x++) {
                        for (uint y = 0; y < 255; y++) {
                            var infoId = (uint)(((x << 8) | y) << 16 | 0xFFFE);
                            if (dats.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                                foreach (var b in lbi.Buildings)
                                    ids.Add(b.ModelId);
                            }
                        }
                    }
                }
                else {
                    foreach (var infoId in allLbiIds) {
                        if (dats.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                            foreach (var b in lbi.Buildings)
                                ids.Add(b.ModelId);
                        }
                    }
                }

                _buildingModelIds = ids;
            }
        }

        /// <summary>
        /// Finds a donor instance of the given building model and extracts its EnvCell layout as a blueprint.
        /// Uses GetAllIdsOfType first, falls back to brute-force scan if needed.
        /// </summary>
        private static BuildingBlueprint? ExtractBlueprint(uint modelId, IDatReaderWriter dats, ILogger? logger) {
            // Try enumeration first
            var allLbiIds = dats.Dats.Cell.GetAllIdsOfType<LandBlockInfo>().ToArray();
            logger?.LogInformation("[Blueprint] Scanning {Count} LandBlockInfo entries for donor of 0x{ModelId:X8}", allLbiIds.Length, modelId);

            var result = FindDonorInIds(modelId, allLbiIds, dats, logger);
            if (result != null) return result;

            // Fallback: brute-force scan all possible landblock IDs
            if (allLbiIds.Length == 0) {
                logger?.LogInformation("[Blueprint] Brute-force scanning for donor of 0x{ModelId:X8}", modelId);
                for (uint x = 0; x < 255; x++) {
                    for (uint y = 0; y < 255; y++) {
                        var infoId = (uint)(((x << 8) | y) << 16 | 0xFFFE);
                        if (!dats.TryGet<LandBlockInfo>(infoId, out var lbi)) continue;
                        foreach (var building in lbi.Buildings) {
                            if (building.ModelId != modelId) continue;
                            var donorLbId = (infoId >> 16) & 0xFFFF;
                            var blueprint = ExtractFromDonor(building, (uint)donorLbId, dats, logger);
                            if (blueprint != null) {
                                logger?.LogInformation("[Blueprint] Extracted blueprint for 0x{ModelId:X8}: {CellCount} cells (brute-force)",
                                    modelId, blueprint.Cells.Count);
                                return blueprint;
                            }
                        }
                    }
                }
            }

            logger?.LogWarning("[Blueprint] No donor instance found for building model 0x{ModelId:X8}", modelId);
            return null;
        }

        private static BuildingBlueprint? FindDonorInIds(uint modelId, uint[] lbiIds, IDatReaderWriter dats, ILogger? logger) {
            foreach (var infoId in lbiIds) {
                if (!dats.TryGet<LandBlockInfo>(infoId, out var lbi)) continue;

                foreach (var building in lbi.Buildings) {
                    if (building.ModelId != modelId) continue;

                    // Found a donor! Extract the blueprint.
                    var donorLbId = (infoId >> 16) & 0xFFFF;
                    var blueprint = ExtractFromDonor(building, (uint)donorLbId, dats, logger);
                    if (blueprint != null) {
                        logger?.LogInformation("[Blueprint] Extracted blueprint for 0x{ModelId:X8}: {CellCount} cells from LB 0x{LbId:X4}",
                            modelId, blueprint.Cells.Count, donorLbId);
                        return blueprint;
                    }
                }
            }
            return null;
        }

        private static BuildingBlueprint? ExtractFromDonor(BuildingInfo donor, uint donorLbId, IDatReaderWriter dats, ILogger? logger) {
            var blueprint = new BuildingBlueprint {
                ModelId = donor.ModelId,
                NumLeaves = donor.NumLeaves,
                DonorOrientation = donor.Frame.Orientation,
                DonorLandblockId = donorLbId
            };

            // Collect all EnvCell IDs belonging to this building (may be empty for exterior-only buildings).
            // NOTE: We intentionally do NOT use an exclusion set here. In original AC data, each
            // building's cell graph is isolated (portals don't cross between buildings), so the BFS
            // naturally stays within the donor building. Adding exclusion caused incorrect cell
            // omission when other buildings' unconstrained BFS overlapped with the donor's cells.
            var cellIds = CollectBuildingCellIds(donor, dats, donorLbId);

            // Build the cell snapshots
            // Store positions in donor-local space (undo donor rotation) so they can be
            // re-applied with any new orientation during instantiation.
            var donorOrigin = donor.Frame.Origin;
            var donorInverseRot = Quaternion.Inverse(donor.Frame.Orientation);
            int index = 0;
            foreach (var cellId in cellIds.OrderBy(c => c)) {
                uint fullCellId = (donorLbId << 16) | cellId;
                if (!dats.TryGet<EnvCell>(fullCellId, out var envCell)) continue;

                logger?.LogInformation("[Blueprint] Donor EnvCell 0x{CellId:X8} env=0x{EnvId:X4} struct=0x{StructId:X4} portals={Portals} visible={Visible} statics={Statics} flags={Flags} pos=({X:F1},{Y:F1},{Z:F1})",
                    fullCellId, envCell.EnvironmentId, envCell.CellStructure,
                    envCell.CellPortals.Count, envCell.VisibleCells.Count, envCell.StaticObjects.Count,
                    envCell.Flags,
                    envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);
                foreach (var cp in envCell.CellPortals) {
                    logger?.LogInformation("[Blueprint]   Donor CellPortal: flags=0x{Flags:X4} polygon={PolyId} otherCell=0x{CellId:X4} otherPortal={PortalId}",
                        (ushort)cp.Flags, cp.PolygonId, cp.OtherCellId, cp.OtherPortalId);
                }

                // Transform world-relative offset into donor-local space
                var worldOffset = envCell.Position.Origin - donorOrigin;
                var localOffset = Vector3.Transform(worldOffset, donorInverseRot);
                // Store orientation relative to donor
                var localOrientation = Quaternion.Normalize(donorInverseRot * envCell.Position.Orientation);

                var snapshot = new EnvCellSnapshot {
                    OriginalCellId = cellId,
                    Flags = envCell.Flags,
                    EnvironmentId = envCell.EnvironmentId,
                    CellStructure = envCell.CellStructure,
                    RelativeOrigin = localOffset,
                    Orientation = localOrientation,
                    RestrictionObj = envCell.RestrictionObj
                };

                // Copy surfaces
                snapshot.Surfaces.AddRange(envCell.Surfaces);

                // Copy cell portals (will be remapped during instantiation)
                foreach (var cp in envCell.CellPortals) {
                    snapshot.CellPortals.Add(new CellPortal {
                        Flags = cp.Flags,
                        PolygonId = cp.PolygonId,
                        OtherCellId = cp.OtherCellId,
                        OtherPortalId = cp.OtherPortalId
                    });
                }

                // Copy ALL visible cells. During instantiation, building cell IDs will be
                // remapped via RemapCellId, and non-building IDs (LandCells, exterior refs)
                // pass through unchanged. Filtering here caused missing entries that broke
                // ACE's find_transit_cells portal lookups, resulting in walk-through walls.
                snapshot.VisibleCells.AddRange(envCell.VisibleCells);

                // Copy static objects in donor-local space
                foreach (var stab in envCell.StaticObjects) {
                    var stabWorldOffset = stab.Frame.Origin - donorOrigin;
                    var stabLocalOffset = Vector3.Transform(stabWorldOffset, donorInverseRot);
                    var stabLocalOrientation = Quaternion.Normalize(donorInverseRot * stab.Frame.Orientation);
                    snapshot.StaticObjects.Add(new StabSnapshot {
                        Id = stab.Id,
                        RelativeOrigin = stabLocalOffset,
                        Orientation = stabLocalOrientation
                    });
                }

                blueprint.OriginalCellIdToIndex[cellId] = index;
                blueprint.Cells.Add(snapshot);
                index++;
            }

            // Copy building portals (will be remapped during instantiation).
            // Keep ALL StabList entries -- building cells get remapped, non-building cells
            // (LandCells, exterior refs) pass through RemapCellId unchanged.
            foreach (var portal in donor.Portals) {
                logger?.LogInformation("[Blueprint] Donor BuildingPortal: flags=0x{Flags:X4} otherCell=0x{CellId:X4} otherPortal={PortalId} stabs=[{Stabs}]",
                    (ushort)portal.Flags, portal.OtherCellId, portal.OtherPortalId,
                    string.Join(",", portal.StabList.Select(s => $"0x{s:X4}")));

                blueprint.PortalTemplates.Add(new BuildingPortal {
                    Flags = portal.Flags,
                    OtherCellId = portal.OtherCellId,
                    OtherPortalId = portal.OtherPortalId,
                    StabList = new List<ushort>(portal.StabList)
                });
            }

            return blueprint;
        }

        /// <summary>
        /// Instantiates a blueprint at a new position, creating new EnvCells and a BuildingInfo.
        /// Returns the new BuildingInfo and the number of cells created.
        /// </summary>
        public static (BuildingInfo building, int cellCount)? InstantiateBlueprint(
            BuildingBlueprint blueprint,
            Vector3 newOrigin,
            Quaternion newOrientation,
            uint lbId,
            uint currentNumCells,
            IDatReaderWriter dats,
            int iteration,
            ILogger? logger) {

            // Build cell ID remap table: originalCellId -> newCellId
            var remap = new Dictionary<ushort, ushort>();
            ushort nextCellId = (ushort)(currentNumCells + 0x0100);
            foreach (var cell in blueprint.Cells) {
                remap[cell.OriginalCellId] = nextCellId;
                nextCellId++;
            }

            // Create and save each new EnvCell
            // Apply the new building's orientation to transform local-space offsets to world-space
            foreach (var cell in blueprint.Cells) {
                var newCellId = remap[cell.OriginalCellId];
                uint fullCellId = (lbId << 16) | newCellId;

                // Rotate local-space offset by new building orientation, then translate
                var worldOffset = Vector3.Transform(cell.RelativeOrigin, newOrientation);
                var worldOrientation = Quaternion.Normalize(newOrientation * cell.Orientation);

                var envCell = new EnvCell {
                    Id = fullCellId,
                    Flags = cell.Flags,
                    EnvironmentId = cell.EnvironmentId,
                    CellStructure = cell.CellStructure,
                    RestrictionObj = cell.RestrictionObj,
                    Position = new Frame {
                        Origin = newOrigin + worldOffset,
                        Orientation = worldOrientation
                    }
                };

                // Copy surfaces
                envCell.Surfaces.AddRange(cell.Surfaces);

                // Copy and remap cell portals
                foreach (var cp in cell.CellPortals) {
                    var remappedCellId = RemapCellId(cp.OtherCellId, remap);
                    var newPortal = new CellPortal {
                        Flags = cp.Flags,
                        PolygonId = cp.PolygonId,
                        OtherPortalId = cp.OtherPortalId,
                        OtherCellId = remappedCellId
                    };
                    envCell.CellPortals.Add(newPortal);

                    logger?.LogInformation("[Blueprint]     CellPortal: flags=0x{Flags:X4} poly={PolyId} otherCell=0x{NewCellId:X4} (was 0x{OldCellId:X4}) otherPortal={PortalId}",
                        (ushort)cp.Flags, cp.PolygonId, remappedCellId, cp.OtherCellId, cp.OtherPortalId);
                }

                // Copy and remap ALL visible cells (building cells remapped, others pass through)
                foreach (var vc in cell.VisibleCells) {
                    envCell.VisibleCells.Add(RemapCellId(vc, remap));
                }

                // Copy static objects with orientation-aware positions
                foreach (var stab in cell.StaticObjects) {
                    var stabWorldOffset = Vector3.Transform(stab.RelativeOrigin, newOrientation);
                    var stabWorldOrientation = Quaternion.Normalize(newOrientation * stab.Orientation);
                    envCell.StaticObjects.Add(new Stab {
                        Id = stab.Id,
                        Frame = new Frame {
                            Origin = newOrigin + stabWorldOffset,
                            Orientation = stabWorldOrientation
                        }
                    });
                }

                // Ensure flags are consistent with actual content.
                // DatReaderWriter's Pack only writes StaticObjects/RestrictionObj when
                // the corresponding flags are set.
                if (envCell.StaticObjects.Count > 0)
                    envCell.Flags |= EnvCellFlags.HasStaticObjs;
                else
                    envCell.Flags &= ~EnvCellFlags.HasStaticObjs;

                if (envCell.RestrictionObj != 0)
                    envCell.Flags |= EnvCellFlags.HasRestrictionObj;
                else
                    envCell.Flags &= ~EnvCellFlags.HasRestrictionObj;

                if (!dats.TrySave(envCell, iteration)) {
                    logger?.LogError("[Blueprint]   FAILED to save new EnvCell 0x{CellId:X8}", fullCellId);
                    return null;
                }
                logger?.LogInformation("[Blueprint]   Created EnvCell 0x{CellId:X8} env=0x{EnvId:X4} struct=0x{StructId:X4} portals={Portals} visible={Visible} statics={Statics} flags={Flags} pos=({X:F1},{Y:F1},{Z:F1})",
                    fullCellId, envCell.EnvironmentId, envCell.CellStructure,
                    envCell.CellPortals.Count, envCell.VisibleCells.Count, envCell.StaticObjects.Count,
                    envCell.Flags,
                    envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);

                // Detailed Frame serialization diagnostic
                try {
                    var donorFullCellId = (blueprint.DonorLandblockId << 16) | cell.OriginalCellId;
                    if (dats.TryGet<EnvCell>(donorFullCellId, out var donorCell)) {
                        // Serialize just the Frame from each cell
                        var donorFrameBuf = new byte[128];
                        var newFrameBuf = new byte[128];
                        var donorFW = new DatReaderWriter.Lib.IO.DatBinWriter(donorFrameBuf.AsMemory());
                        var newFW = new DatReaderWriter.Lib.IO.DatBinWriter(newFrameBuf.AsMemory());
                        donorCell.Position.Pack(donorFW);
                        envCell.Position.Pack(newFW);

                        var donorFrameHex = BitConverter.ToString(donorFrameBuf, 0, donorFW.Offset).Replace("-", " ");
                        var newFrameHex = BitConverter.ToString(newFrameBuf, 0, newFW.Offset).Replace("-", " ");
                        logger?.LogInformation("[Blueprint]   FRAME donor ({Len}b): {Hex} origin=({X},{Y},{Z}) quat=({W},{QX},{QY},{QZ})",
                            donorFW.Offset, donorFrameHex,
                            donorCell.Position.Origin.X, donorCell.Position.Origin.Y, donorCell.Position.Origin.Z,
                            donorCell.Position.Orientation.W, donorCell.Position.Orientation.X,
                            donorCell.Position.Orientation.Y, donorCell.Position.Orientation.Z);
                        logger?.LogInformation("[Blueprint]   FRAME new   ({Len}b): {Hex} origin=({X},{Y},{Z}) quat=({W},{QX},{QY},{QZ})",
                            newFW.Offset, newFrameHex,
                            envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z,
                            envCell.Position.Orientation.W, envCell.Position.Orientation.X,
                            envCell.Position.Orientation.Y, envCell.Position.Orientation.Z);

                        // Also serialize full cells and compare sizes
                        var donorBuf = new byte[65536];
                        var newBuf = new byte[65536];
                        var dw = new DatReaderWriter.Lib.IO.DatBinWriter(donorBuf.AsMemory());
                        var nw = new DatReaderWriter.Lib.IO.DatBinWriter(newBuf.AsMemory());
                        donorCell.Pack(dw);
                        envCell.Pack(nw);
                        if (dw.Offset != nw.Offset) {
                            logger?.LogWarning("[Blueprint]   FULL SIZE MISMATCH: donor={D} vs new={N}", dw.Offset, nw.Offset);
                        }
                    }
                } catch (Exception ex) {
                    logger?.LogWarning("[Blueprint]   Diag failed: {Msg}", ex.Message);
                }
            }

            // Create the new BuildingInfo
            var buildingInfo = new BuildingInfo {
                ModelId = blueprint.ModelId,
                NumLeaves = blueprint.NumLeaves,
                Frame = new Frame {
                    Origin = newOrigin,
                    Orientation = newOrientation
                }
            };

            // Copy and remap building portals
            foreach (var portalTemplate in blueprint.PortalTemplates) {
                var newPortal = new BuildingPortal {
                    Flags = portalTemplate.Flags,
                    OtherCellId = RemapCellId(portalTemplate.OtherCellId, remap),
                    OtherPortalId = portalTemplate.OtherPortalId,
                    StabList = portalTemplate.StabList.Select(s => RemapCellId(s, remap)).ToList()
                };
                buildingInfo.Portals.Add(newPortal);

                logger?.LogInformation("[Blueprint]   BuildingPortal: flags=0x{Flags:X4} otherCell=0x{CellId:X4} (was 0x{OldCellId:X4}) otherPortal={PortalId} stabs=[{Stabs}]",
                    (ushort)newPortal.Flags, newPortal.OtherCellId, portalTemplate.OtherCellId,
                    newPortal.OtherPortalId,
                    string.Join(",", newPortal.StabList.Select(s => $"0x{s:X4}")));
            }

            logger?.LogInformation("[Blueprint] Instantiated building 0x{ModelId:X8} with {CellCount} cells",
                blueprint.ModelId, blueprint.Cells.Count);

            return (buildingInfo, blueprint.Cells.Count);
        }

        /// <summary>
        /// Remaps a cell ID using the remap table. IDs not in the table (e.g. outdoor cells) pass through unchanged.
        /// </summary>
        private static ushort RemapCellId(ushort cellId, Dictionary<ushort, ushort> remap) {
            return remap.TryGetValue(cellId, out var newId) ? newId : cellId;
        }

        /// <summary>
        /// Walks a building's portal graph to collect all EnvCell IDs (0x0100-0xFFFD).
        /// Same logic as LandblockDocument.CollectBuildingCellIds but static for use here.
        /// An optional exclusion set prevents the BFS from crossing into cells belonging
        /// to other buildings in the same landblock (fixes duplicate building conflicts).
        /// </summary>
        private static HashSet<ushort> CollectBuildingCellIds(BuildingInfo building, IDatReaderWriter dats, uint lbId,
            HashSet<ushort>? excludeCellIds = null) {
            var cellIds = new HashSet<ushort>();
            var toVisit = new Queue<ushort>();

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

            while (toVisit.Count > 0) {
                var cellNum = toVisit.Dequeue();
                uint fullCellId = (lbId << 16) | cellNum;

                if (dats.TryGet<EnvCell>(fullCellId, out var envCell)) {
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

        private static bool IsEnvCellId(ushort cellId) => cellId >= 0x0100 && cellId <= 0xFFFD;
    }
}

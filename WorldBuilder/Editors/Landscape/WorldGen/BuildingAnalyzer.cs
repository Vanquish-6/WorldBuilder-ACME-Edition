using System;
using System.Collections.Generic;
using System.Linq;
using DatReaderWriter.DBObjs;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    /// <summary>
    /// Analyzes building models in the DAT to classify them as suitable for
    /// town generation. Filters out partial structures, ruins, dungeon entrances,
    /// and other non-residential/non-commercial building models.
    /// </summary>
    public static class BuildingAnalyzer {
        private static List<uint>? _cachedTownBuildings;
        private static bool _cachedTownBuildingsRetailOnly;
        private static List<uint>? _cachedEditorCompleteBuildings;
        private static Dictionary<uint, RetailTownBuildingScanner.RetailTownModelStats>? _cachedRetailTownStats;
        private static List<BuildingProfile>? _cachedProfiles;
        private static readonly object _lock = new();

        /// <summary>
        /// GfxObj / setup IDs that are known structural fragments (villa pieces, wall modules, etc.)
        /// still authored as BuildingInfo in the DAT. Extend as you find more.
        /// </summary>
        public static readonly HashSet<uint> KnownStructuralPieceModelIds = new() {
            0x01002289, // reported: villa fragment piece
            // Nantucket-style modular façade shells (GfxObj) — still BuildingInfo entries but not placeable “whole” buildings
            0x01001EE9, 0x01001EEA, 0x01001EEB, 0x01001EEC, 0x01001EED, 0x01001EEE, 0x01001EEF, 0x01001EF0,
            0x01001EF1, 0x01001EF2, 0x01001EF3, 0x01001EF4,
            0x01001F0A, 0x01001F0B, 0x01001F11, 0x01001F15, 0x01001F1A, 0x01001F1C, 0x01001F1D, 0x01001F20, 0x01001F29,
            0x01001FB3,
        };

        public record BuildingProfile {
            public uint ModelId { get; init; }
            public uint NumLeaves { get; init; }
            /// <summary>Max over all LBI placements: count of <see cref="EnvCell"/> records reachable from this building's portal graph (real interior cells in the DAT).</summary>
            public int CellCount { get; init; }
            public int PortalCount { get; init; }
            public int TotalStatics { get; init; }
            public int OccurrenceCount { get; init; }
            public int UniqueLandblocks { get; init; }
            public int MaxPerLandblock { get; init; }
            public float AvgPerLandblock => UniqueLandblocks > 0 ? (float)OccurrenceCount / UniqueLandblocks : 0;
            public bool HasInterior => CellCount > 0;
            /// <summary>Detects multi-piece structures: mirrored halves, quad-part mansions, etc.</summary>
            public bool IsPairedHalf => AvgPerLandblock >= 1.35f || MaxPerLandblock >= 3;
        }

        public static void ClearCache() {
            lock (_lock) {
                _cachedTownBuildings = null;
                _cachedTownBuildingsRetailOnly = false;
                _cachedEditorCompleteBuildings = null;
                _cachedRetailTownStats = null;
                _cachedProfiles = null;
            }
            BuildingBlueprintCache.ClearCache();
        }

        /// <summary>
        /// Building models for the object browser: must reach real <see cref="EnvCell"/> records from the
        /// building portal graph (see <see cref="BuildingProfile.CellCount"/>), not just a BuildingInfo shell.
        /// Prefers entries that also pass <see cref="BuildingBlueprintCache.GetBlueprint"/> EnvCell snapshot count.
        /// </summary>
        public static IReadOnlyList<uint> GetEditorCompleteBuildingModelIds(IDatReaderWriter dats) {
            lock (_lock) {
                if (_cachedEditorCompleteBuildings != null) return _cachedEditorCompleteBuildings;
                _cachedEditorCompleteBuildings = AnalyzeEditorComplete(dats);
                return _cachedEditorCompleteBuildings;
            }
        }

        /// <summary>
        /// Returns building model IDs that are suitable for town placement.
        /// Filters based on interior cell count, portal structure, and occurrence patterns.
        /// </summary>
        public static IReadOnlyList<uint> GetTownBuildings(IDatReaderWriter dats, bool retailTownBuildingsOnly = false) {
            if (_cachedTownBuildings != null && _cachedTownBuildingsRetailOnly == retailTownBuildingsOnly)
                return _cachedTownBuildings;
            lock (_lock) {
                if (_cachedTownBuildings != null && _cachedTownBuildingsRetailOnly == retailTownBuildingsOnly)
                    return _cachedTownBuildings;
                _cachedTownBuildings = Analyze(dats, retailTownBuildingsOnly);
                _cachedTownBuildingsRetailOnly = retailTownBuildingsOnly;
                return _cachedTownBuildings;
            }
        }

        /// <summary>
        /// Runs and logs a full analysis of all building models in the DAT.
        /// </summary>
        public static List<BuildingProfile> AnalyzeAll(IDatReaderWriter dats) {
            var profiles = GetOrBuildProfiles(dats, verbose: true);
            var retail = GetOrBuildRetailTownStats(dats);
            RetailTownBuildingScanner.LogTopRetailTownModels(retail, profiles);
            return profiles;
        }

        private static Dictionary<uint, RetailTownBuildingScanner.RetailTownModelStats> GetOrBuildRetailTownStats(IDatReaderWriter dats) {
            if (_cachedRetailTownStats != null) return _cachedRetailTownStats;
            lock (_lock) {
                if (_cachedRetailTownStats != null) return _cachedRetailTownStats;
                _cachedRetailTownStats = RetailTownBuildingScanner.Scan(dats);
                return _cachedRetailTownStats;
            }
        }

        /// <summary>
        /// Returns the raw building profiles, running the full DAT scan at most once per
        /// <see cref="ClearCache"/> call. All higher-level callers share the same result.
        /// </summary>
        private static List<BuildingProfile> GetOrBuildProfiles(IDatReaderWriter dats, bool verbose = false) {
            if (_cachedProfiles != null) return _cachedProfiles;
            lock (_lock) {
                if (_cachedProfiles != null) return _cachedProfiles;
                _cachedProfiles = RunAnalysis(dats, verbose);
                return _cachedProfiles;
            }
        }

        /// <summary>GfxObj shells (0x01xxxxxx) often have small EnvCell cliques; require stronger interior signal.</summary>
        private static bool PassesEditorGfxObjHeuristic(BuildingProfile p) {
            if ((p.ModelId & 0xFF000000) != 0x01000000)
                return true;
            // Either a larger cell graph or a furnished interior — strips bare wall/corner modules.
            return p.CellCount >= 6 || (p.CellCount >= 5 && p.TotalStatics >= 12) || p.TotalStatics >= 22;
        }

        /// <summary>
        /// Catalog for object browser: real <see cref="EnvCell"/> interiors + successful blueprint extract.
        /// Drops paired landblock modules, known shell IDs, and weak GfxObj shells. Never uses “profile-only” fallback
        /// (that re-admitted junk when blueprint extraction failed).
        /// </summary>
        private static List<uint> AnalyzeEditorComplete(IDatReaderWriter dats) {
            var profiles = GetOrBuildProfiles(dats);

            bool AllowedId(BuildingProfile p) =>
                !KnownStructuralPieceModelIds.Contains(p.ModelId);

            static List<uint> ValidateBlueprints(IEnumerable<BuildingProfile> cands, int minBlueprintCells, IDatReaderWriter dats,
                out int failed) {
                failed = 0;
                var list = new List<uint>();
                foreach (var p in cands) {
                    var blueprint = BuildingBlueprintCache.GetBlueprint(p.ModelId, dats);
                    if (blueprint != null && blueprint.Cells.Count >= minBlueprintCells)
                        list.Add(p.ModelId);
                    else
                        failed++;
                }
                return list;
            }

            const int minPortals = 2;

            // Tier 1: substantial interiors, not villa halves, GfxObj rule, blueprint must match.
            var tier1 = profiles
                .Where(AllowedId)
                .Where(p => !p.IsPairedHalf)
                .Where(PassesEditorGfxObjHeuristic)
                .Where(p => p.HasInterior && p.CellCount >= 5 && p.PortalCount >= minPortals)
                .ToList();

            Console.WriteLine($"[BuildingAnalyzer] Editor catalog tier1: {tier1.Count} candidates (>=5 EnvCells, >={minPortals} portals, !paired)");

            var validated = ValidateBlueprints(tier1, minBlueprintCells: 5, dats, out int fail1);
            Console.WriteLine($"[BuildingAnalyzer] Editor catalog tier1 blueprints: {validated.Count} ok, {fail1} failed");

            if (validated.Count == 0) {
                // Tier 2: still exclude paired halves / denylist / weak GfxObj; allow 4 cells if interior is furnished.
                var tier2 = profiles
                    .Where(AllowedId)
                    .Where(p => !p.IsPairedHalf)
                    .Where(PassesEditorGfxObjHeuristic)
                    .Where(p => p.HasInterior && p.CellCount >= 4 && p.PortalCount >= minPortals && p.TotalStatics >= 14)
                    .ToList();
                Console.WriteLine($"[BuildingAnalyzer] Editor catalog tier2: {tier2.Count} candidates (>=4 EnvCells, statics>=14)");
                validated = ValidateBlueprints(tier2, minBlueprintCells: 4, dats, out int fail2);
                Console.WriteLine($"[BuildingAnalyzer] Editor catalog tier2 blueprints: {validated.Count} ok, {fail2} failed");
            }

            return validated;
        }

        /// <summary>
        /// When <see cref="GetEditorCompleteBuildingModelIds"/> is empty: EnvCell-backed IDs only (no blueprint), still excludes paired halves and denylist.
        /// Used so the object browser does not fall back to the full raw LBI dump.
        /// </summary>
        public static HashSet<uint> GetEditorEnvCellFallbackModelIds(IDatReaderWriter dats) {
            lock (_lock) {
                var profiles = GetOrBuildProfiles(dats);
                return profiles
                    .Where(p => !KnownStructuralPieceModelIds.Contains(p.ModelId))
                    .Where(p => !p.IsPairedHalf)
                    .Where(PassesEditorGfxObjHeuristic)
                    .Where(p => p.HasInterior && p.CellCount >= 4 && p.PortalCount >= 2)
                    .Select(p => p.ModelId)
                    .ToHashSet();
            }
        }

        private static List<uint> Analyze(IDatReaderWriter dats, bool retailTownBuildingsOnly) {
            var profiles = GetOrBuildProfiles(dats);
            var retail = GetOrBuildRetailTownStats(dats);

            // Strict filter: complete, standalone buildings only.
            //  - 3+ interior cells rules out wall segments, facades, and ruins
            //  - 2+ portals means entrance + at least one room-to-room transition
            //  - appears in 2+ landblocks = standard reusable building, not a one-off
            //  - NOT a paired half (avg per-landblock ~2.0 for mirrored halves)
            //  - 4+ cells filters many single-room / facade pieces that still have portals
            bool RetailOk(BuildingProfile p) {
                retail.TryGetValue(p.ModelId, out var rs);
                return RetailTownBuildingScanner.PassesRetailTownHeuristic(p.IsPairedHalf, rs);
            }

            var candidates = profiles
                .Where(p => !KnownStructuralPieceModelIds.Contains(p.ModelId))
                .Where(p => p.HasInterior && p.CellCount >= 4 && p.PortalCount >= 2)
                .Where(p => p.OccurrenceCount >= 2 && !p.IsPairedHalf)
                .Where(RetailOk)
                .ToList();

            int pairedCount = profiles.Count(p => p.HasInterior && p.IsPairedHalf);
            Console.WriteLine($"[BuildingAnalyzer] {profiles.Count} total -> {candidates.Count} candidates " +
                $"(>=4 cells, >=2 portals, retail town heuristic); {pairedCount} paired halves excluded");

            if (candidates.Count == 0) {
                candidates = profiles
                    .Where(p => !KnownStructuralPieceModelIds.Contains(p.ModelId))
                    .Where(p => p.HasInterior && p.CellCount >= 3 && p.PortalCount >= 1)
                    .Where(p => p.OccurrenceCount >= 1 && p.AvgPerLandblock < 1.4f)
                    .Where(RetailOk)
                    .ToList();
                Console.WriteLine($"[BuildingAnalyzer] Relaxed filter: {candidates.Count} candidates (>=3 cells, avg/lb<1.4, retail town)");
            }

            if (retailTownBuildingsOnly) {
                int before = candidates.Count;
                candidates = candidates.Where(p => retail.ContainsKey(p.ModelId)).ToList();
                Console.WriteLine($"[BuildingAnalyzer] Retail town buildings only: {candidates.Count} models " +
                    $"(dropped {before - candidates.Count} not seen in town-like retail landblocks)");
            }

            Console.WriteLine($"[BuildingAnalyzer] Pre-extracting blueprints to validate...");

            var validated = new List<uint>();
            int extractFail = 0;
            foreach (var p in candidates) {
                var blueprint = BuildingBlueprintCache.GetBlueprint(p.ModelId, dats);
                if (blueprint != null && blueprint.Cells.Count >= 3) {
                    validated.Add(p.ModelId);
                } else {
                    extractFail++;
                }
            }

            Console.WriteLine($"[BuildingAnalyzer] Blueprint validation: {validated.Count} succeeded, {extractFail} failed extraction");
            Console.WriteLine($"[BuildingAnalyzer] Final town building catalog: {validated.Count} models");

            return validated;
        }

        private static List<BuildingProfile> RunAnalysis(IDatReaderWriter dats, bool verbose) {
            var buildingOccurrences = new Dictionary<uint, int>();
            var buildingNumLeaves = new Dictionary<uint, uint>();
            var buildingPortalCounts = new Dictionary<uint, int>();
            var buildingCellCounts = new Dictionary<uint, int>();
            var buildingStaticCounts = new Dictionary<uint, int>();
            var buildingLandblocks = new Dictionary<uint, HashSet<uint>>();
            var buildingMaxPerLb = new Dictionary<uint, int>();

            var allLbiIds = dats.Dats.Cell.GetAllIdsOfType<LandBlockInfo>().ToArray();

            IEnumerable<uint> lbiSource;
            if (allLbiIds.Length > 0) {
                lbiSource = allLbiIds;
            } else {
                var bruteForce = new List<uint>();
                for (uint x = 0; x < 255; x++)
                    for (uint y = 0; y < 255; y++)
                        bruteForce.Add((uint)(((x << 8) | y) << 16 | 0xFFFE));
                lbiSource = bruteForce;
            }

            int lbWithBuildings = 0;
            foreach (var infoId in lbiSource) {
                if (!dats.TryGet<LandBlockInfo>(infoId, out var lbi)) continue;
                if (lbi.Buildings == null || lbi.Buildings.Count == 0) continue;

                lbWithBuildings++;
                uint lbKey = (infoId >> 16) & 0xFFFF;

                var lbModelCounts = new Dictionary<uint, int>();
                foreach (var building in lbi.Buildings) {
                    uint mid = building.ModelId;
                    buildingOccurrences.TryGetValue(mid, out int occ);
                    buildingOccurrences[mid] = occ + 1;
                    lbModelCounts[mid] = lbModelCounts.GetValueOrDefault(mid) + 1;

                    if (!buildingLandblocks.TryGetValue(mid, out var lbSet)) {
                        lbSet = new HashSet<uint>();
                        buildingLandblocks[mid] = lbSet;
                    }
                    lbSet.Add(lbKey);

                    int portalsHere = building.Portals?.Count ?? 0;
                    buildingPortalCounts[mid] = Math.Max(buildingPortalCounts.GetValueOrDefault(mid), portalsHere);
                    buildingNumLeaves[mid] = Math.Max(buildingNumLeaves.GetValueOrDefault(mid), building.NumLeaves);

                    int cellCount = 0;
                    int staticCount = 0;
                    if (building.Portals != null) {
                        var lbId = (infoId >> 16) & 0xFFFF;
                        var visited = new HashSet<ushort>();
                        var queue = new Queue<ushort>();

                        foreach (var portal in building.Portals) {
                            if (portal.OtherCellId >= 0x0100 && portal.OtherCellId <= 0xFFFD) {
                                if (visited.Add(portal.OtherCellId))
                                    queue.Enqueue(portal.OtherCellId);
                            }
                            foreach (var stab in portal.StabList) {
                                if (stab >= 0x0100 && stab <= 0xFFFD) {
                                    if (visited.Add(stab))
                                        queue.Enqueue(stab);
                                }
                            }
                        }

                        while (queue.Count > 0) {
                            var cellNum = queue.Dequeue();
                            uint fullCellId = (uint)(lbId << 16) | cellNum;
                            if (dats.TryGet<EnvCell>(fullCellId, out var envCell)) {
                                cellCount++;
                                staticCount += envCell.StaticObjects?.Count ?? 0;
                                foreach (var cp in envCell.CellPortals) {
                                    if (cp.OtherCellId >= 0x0100 && cp.OtherCellId <= 0xFFFD) {
                                        if (visited.Add(cp.OtherCellId))
                                            queue.Enqueue(cp.OtherCellId);
                                    }
                                }
                            }
                        }
                    }

                    buildingCellCounts[mid] = Math.Max(buildingCellCounts.GetValueOrDefault(mid), cellCount);
                    buildingStaticCounts[mid] = Math.Max(buildingStaticCounts.GetValueOrDefault(mid), staticCount);
                }

                foreach (var (mid, cnt) in lbModelCounts)
                    buildingMaxPerLb[mid] = Math.Max(buildingMaxPerLb.GetValueOrDefault(mid), cnt);
            }

            var profiles = new List<BuildingProfile>();
            foreach (var (modelId, occCount) in buildingOccurrences) {
                buildingNumLeaves.TryGetValue(modelId, out uint numLeaves);
                buildingPortalCounts.TryGetValue(modelId, out int portalCount);
                buildingCellCounts.TryGetValue(modelId, out int cellCount);
                buildingStaticCounts.TryGetValue(modelId, out int staticCount);
                int uniqueLbs = buildingLandblocks.TryGetValue(modelId, out var set) ? set.Count : 0;

                profiles.Add(new BuildingProfile {
                    ModelId = modelId,
                    NumLeaves = numLeaves,
                    CellCount = cellCount,
                    PortalCount = portalCount,
                    TotalStatics = staticCount,
                    OccurrenceCount = occCount,
                    UniqueLandblocks = uniqueLbs,
                    MaxPerLandblock = buildingMaxPerLb.GetValueOrDefault(modelId)
                });
            }

            if (verbose) {
                Console.WriteLine($"[BuildingAnalyzer] === FULL ANALYSIS ===");
                Console.WriteLine($"[BuildingAnalyzer] Scanned {lbWithBuildings} landblocks with buildings");
                Console.WriteLine($"[BuildingAnalyzer] Total unique building models: {profiles.Count}");

                var noInterior = profiles.Where(p => !p.HasInterior).ToList();
                var hasInterior = profiles.Where(p => p.HasInterior).ToList();
                var smallInterior = hasInterior.Where(p => p.CellCount <= 2).ToList();
                var medInterior = hasInterior.Where(p => p.CellCount is > 2 and <= 6).ToList();
                var largeInterior = hasInterior.Where(p => p.CellCount > 6).ToList();
                var noPortals = profiles.Where(p => p.PortalCount == 0).ToList();

                Console.WriteLine($"[BuildingAnalyzer]   No interior (exterior-only): {noInterior.Count} models");
                Console.WriteLine($"[BuildingAnalyzer]   Has interior: {hasInterior.Count} models");
                Console.WriteLine($"[BuildingAnalyzer]     Small (1-2 cells): {smallInterior.Count}");
                Console.WriteLine($"[BuildingAnalyzer]     Medium (3-6 cells): {medInterior.Count}");
                Console.WriteLine($"[BuildingAnalyzer]     Large (7+ cells): {largeInterior.Count}");
                Console.WriteLine($"[BuildingAnalyzer]   No portals: {noPortals.Count} models");
                Console.WriteLine($"[BuildingAnalyzer]");

                var pairedHalves = hasInterior.Where(p => p.IsPairedHalf).ToList();
                var standalone = hasInterior.Where(p => !p.IsPairedHalf).ToList();
                Console.WriteLine($"[BuildingAnalyzer]   Paired halves (avg/lb >= 1.45): {pairedHalves.Count} models");
                Console.WriteLine($"[BuildingAnalyzer]   Standalone buildings: {standalone.Count} models");

                Console.WriteLine($"[BuildingAnalyzer] --- TOP 30 STANDALONE by occurrence (with interior) ---");
                foreach (var p in standalone.OrderByDescending(p => p.OccurrenceCount).Take(30)) {
                    Console.WriteLine($"[BuildingAnalyzer]   0x{p.ModelId:X8}: {p.OccurrenceCount}x in {p.UniqueLandblocks} LBs (avg {p.AvgPerLandblock:F1}/lb), leaves={p.NumLeaves}, {p.CellCount} cells, {p.PortalCount} portals, {p.TotalStatics} statics");
                }

                if (pairedHalves.Count > 0) {
                    Console.WriteLine($"[BuildingAnalyzer] --- PAIRED HALVES (excluded) ---");
                    foreach (var p in pairedHalves.OrderByDescending(p => p.OccurrenceCount).Take(15)) {
                        Console.WriteLine($"[BuildingAnalyzer]   0x{p.ModelId:X8}: {p.OccurrenceCount}x in {p.UniqueLandblocks} LBs (avg {p.AvgPerLandblock:F1}/lb), {p.CellCount} cells, {p.PortalCount} portals");
                    }
                }

                Console.WriteLine($"[BuildingAnalyzer]");
                Console.WriteLine($"[BuildingAnalyzer] --- EXTERIOR-ONLY (no cells, possible partial/ruins) ---");
                foreach (var p in noInterior.OrderByDescending(p => p.OccurrenceCount).Take(20)) {
                    Console.WriteLine($"[BuildingAnalyzer]   0x{p.ModelId:X8}: {p.OccurrenceCount}x, leaves={p.NumLeaves}, {p.PortalCount} portals");
                }

                Console.WriteLine($"[BuildingAnalyzer]");
                Console.WriteLine($"[BuildingAnalyzer] --- NO PORTALS (possible decoration structures) ---");
                foreach (var p in noPortals.OrderByDescending(p => p.OccurrenceCount).Take(20)) {
                    Console.WriteLine($"[BuildingAnalyzer]   0x{p.ModelId:X8}: {p.OccurrenceCount}x, leaves={p.NumLeaves}, {p.CellCount} cells");
                }

                Console.WriteLine($"[BuildingAnalyzer]");
                int totalOccurrences = profiles.Sum(p => p.OccurrenceCount);
                Console.WriteLine($"[BuildingAnalyzer] --- SUMMARY ---");
                Console.WriteLine($"[BuildingAnalyzer]   Total building placements across all landblocks: {totalOccurrences}");
                Console.WriteLine($"[BuildingAnalyzer]   Avg buildings per landblock (that has buildings): {(float)totalOccurrences / Math.Max(1, lbWithBuildings):F1}");
                Console.WriteLine($"[BuildingAnalyzer]   Avg cells per building (with interior): {(float)hasInterior.Sum(p => p.CellCount) / Math.Max(1, hasInterior.Count):F1}");

                var suitableForTown = standalone.Where(p => p.CellCount >= 4 && p.PortalCount >= 2 && p.OccurrenceCount >= 2).ToList();
                Console.WriteLine($"[BuildingAnalyzer]   Suitable for town gen (standalone, >=4 cells, >=2 portals): {suitableForTown.Count} unique models");
            }

            return profiles;
        }
    }
}

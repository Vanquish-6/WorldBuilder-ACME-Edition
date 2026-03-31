using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public record WorldGeneratorParams {
        public int Seed { get; init; } = 0;
        public bool FullWorld { get; init; } = false;
        public int StartX { get; init; } = 0;
        public int StartY { get; init; } = 0;
        public int Width { get; init; } = 20;
        public int Height { get; init; } = 20;
        public int ContinentCount { get; init; } = 1;
        public int IslandCount { get; init; } = 0;
        public float LandCoverage { get; init; } = 0.5f;
        public float Roughness { get; init; } = 0.5f;
        public int TownCount { get; init; } = 5;
        public float TownSpacing { get; init; } = 30f;
        public int SeaLevelIndex { get; init; } = 20;
        public float MountainScale { get; init; } = 1.0f;
        public bool GenerateRoads { get; init; } = true;
        public bool GenerateBuildings { get; init; } = true;
        /// <summary>
        /// If true, only building models that appear in retail-style town landblocks (from the loaded cell.dat) are used.
        /// Stricter catalog; pair with a full retail DAT for best results.
        /// </summary>
        public bool RetailTownBuildingsOnly { get; init; } = false;
    }

    public class TownSite {
        public string Name { get; init; } = "";
        public int CenterLbX { get; init; }
        public int CenterLbY { get; init; }
        public Vector3 WorldCenter { get; init; }
        public float Radius { get; init; }
        public int Size { get; init; } // 0=hamlet, 1=village, 2=town, 3=city
        public int BuildingCount { get; set; }

        public string SizeLabel => Size switch {
            0 => "Hamlet", 1 => "Village", 2 => "Town", 3 => "City", _ => "Settlement"
        };
    }

    public class PlannedBuilding {
        public uint ModelId { get; init; }
        public Vector3 WorldPosition { get; init; }
        public Quaternion Orientation { get; init; }
        /// <summary>Settlement this building belongs to (for CSV / teleloc export).</summary>
        public string TownName { get; init; } = "";
    }

    public class WorldGeneratorResult {
        public Dictionary<ushort, Dictionary<byte, uint>> TerrainChanges { get; init; } = new();
        public Dictionary<ushort, List<PlannedBuilding>> BuildingPlacements { get; init; } = new();
        public Dictionary<ushort, List<StaticObject>> DecorationPlacements { get; init; } = new();
        public List<TownSite> Towns { get; init; } = new();
        public int TotalVerticesModified { get; set; }
        public int TotalBuildingsPlaced { get; set; }
        public int TotalDecorationsPlaced { get; set; }
        public int TotalRoadVertices { get; set; }
    }
}

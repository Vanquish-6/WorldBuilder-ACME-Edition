using System;
using DatReaderWriter.Enums;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public static class BiomeMapper {
        private struct BiomeRule {
            public float MinElev, MaxElev;
            public float MinMoist, MaxMoist;
            public TerrainTextureType Type;
            public byte Scenery;
        }

        private static readonly BiomeRule[] Rules = {
            // Underwater — depth bands based on elevation so shallow water hugs coastlines
            Rule(0.00f, 0.03f, 0.0f, 1.0f, TerrainTextureType.WaterDeepSea, 0),
            Rule(0.03f, 0.06f, 0.0f, 0.5f, TerrainTextureType.WaterDeepSea, 0),
            Rule(0.03f, 0.06f, 0.5f, 1.0f, TerrainTextureType.WaterShallowSea, 0),
            Rule(0.06f, 0.10f, 0.0f, 0.7f, TerrainTextureType.WaterShallowSea, 0),
            Rule(0.06f, 0.10f, 0.7f, 1.0f, TerrainTextureType.WaterRunning, 0),

            // Beach / coastal
            Rule(0.10f, 0.18f, 0.0f, 0.4f, TerrainTextureType.SandYellow, 0),
            Rule(0.10f, 0.18f, 0.4f, 0.7f, TerrainTextureType.SandGrey, 0),
            Rule(0.10f, 0.18f, 0.7f, 1.0f, TerrainTextureType.SandRockStrewn, 0),

            // Lowland
            Rule(0.18f, 0.35f, 0.0f, 0.35f, TerrainTextureType.PatchyDirt, 1),
            Rule(0.18f, 0.35f, 0.35f, 0.65f, TerrainTextureType.PatchyGrassland, 2),
            Rule(0.18f, 0.35f, 0.65f, 1.0f, TerrainTextureType.MarshSparseSwamp, 3),

            // Midland
            Rule(0.35f, 0.55f, 0.0f, 0.35f, TerrainTextureType.PackedDirt, 1),
            Rule(0.35f, 0.55f, 0.35f, 0.65f, TerrainTextureType.Grassland, 2),
            Rule(0.35f, 0.55f, 0.65f, 1.0f, TerrainTextureType.LushGrass, 3),

            // Highland
            Rule(0.55f, 0.75f, 0.0f, 0.35f, TerrainTextureType.SedimentaryRock, 0),
            Rule(0.55f, 0.75f, 0.35f, 0.65f, TerrainTextureType.SemiBarrenRock, 0),
            Rule(0.55f, 0.75f, 0.65f, 1.0f, TerrainTextureType.BarrenRock, 0),

            // Mountain / alpine
            Rule(0.75f, 0.90f, 0.0f, 0.5f, TerrainTextureType.BarrenRock, 0),
            Rule(0.75f, 0.90f, 0.5f, 1.0f, TerrainTextureType.Snow, 0),

            // Peaks
            Rule(0.90f, 1.01f, 0.0f, 0.5f, TerrainTextureType.Snow, 0),
            Rule(0.90f, 1.01f, 0.5f, 1.0f, TerrainTextureType.Ice, 0),
        };

        private static BiomeRule Rule(float minE, float maxE, float minM, float maxM, TerrainTextureType type, byte scenery)
            => new() { MinElev = minE, MaxElev = maxE, MinMoist = minM, MaxMoist = maxM, Type = type, Scenery = scenery };

        /// <summary>
        /// Assigns terrain type and scenery for each vertex based on elevation and moisture.
        /// Sea level threshold remaps the elevation bands so underwater is below seaLevel.
        /// </summary>
        public static void Assign(
            float[,] elevation, float[,] moisture,
            float seaLevelNorm,
            out byte[,] terrainTypes, out byte[,] sceneryIndices,
            int width, int height) {

            terrainTypes = new byte[width, height];
            sceneryIndices = new byte[width, height];

            for (int x = 0; x < width; x++) {
                for (int y = 0; y < height; y++) {
                    float e = RemapElevation(elevation[x, y], seaLevelNorm);
                    float m = moisture[x, y];

                    var (type, scenery) = Classify(e, m);
                    terrainTypes[x, y] = (byte)type;
                    sceneryIndices[x, y] = scenery;
                }
            }
        }

        private static (TerrainTextureType type, byte scenery) Classify(float elev, float moisture) {
            for (int i = 0; i < Rules.Length; i++) {
                ref readonly var r = ref Rules[i];
                if (elev >= r.MinElev && elev < r.MaxElev && moisture >= r.MinMoist && moisture < r.MaxMoist)
                    return (r.Type, r.Scenery);
            }
            return (TerrainTextureType.Grassland, 1);
        }

        /// <summary>
        /// Remaps raw [0,1] elevation so that the sea level threshold maps to the
        /// underwater/land boundary (0.10 in the biome table).
        /// </summary>
        private static float RemapElevation(float raw, float seaLevelNorm) {
            const float seaBoundary = 0.10f;
            if (raw <= seaLevelNorm) {
                return raw / seaLevelNorm * seaBoundary;
            }
            return seaBoundary + (raw - seaLevelNorm) / (1f - seaLevelNorm) * (1f - seaBoundary);
        }
    }
}

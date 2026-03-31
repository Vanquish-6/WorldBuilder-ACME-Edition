using System;
using System.Collections.Generic;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public static class WorldGenerator {
        public static WorldGeneratorResult? Generate(
            WorldGeneratorParams p,
            IDatReaderWriter dats,
            Region region,
            Action<string>? progress = null) {

            var rng = p.Seed != 0 ? new Random(p.Seed) : new Random();
            var noise = new SimplexNoise(rng.Next());
            var heightTable = region.LandDefs.LandHeightTable;

            int regionW = p.FullWorld ? 254 : p.Width;
            int regionH = p.FullWorld ? 254 : p.Height;
            int startX = p.FullWorld ? 0 : p.StartX;
            int startY = p.FullWorld ? 0 : p.StartY;

            var effectiveParams = p with {
                StartX = startX,
                StartY = startY,
                Width = regionW,
                Height = regionH
            };

            int verticesX = regionW * 8 + 1;
            int verticesY = regionH * 8 + 1;

            progress?.Invoke($"Generating elevation ({verticesX}x{verticesY} vertices, {p.ContinentCount} continents, {p.IslandCount} islands)...");
            var elevation = HeightMapGenerator.Generate(effectiveParams, noise, verticesX, verticesY, rng);

            progress?.Invoke("Generating moisture map...");
            var moisture = HeightMapGenerator.GenerateMoisture(effectiveParams, noise, verticesX, verticesY);

            float seaLevelNorm = HeightMapGenerator.SeaLevelNormalized(effectiveParams.SeaLevelIndex, heightTable);

            // Rescale mountains if MountainScale is not default
            if (MathF.Abs(effectiveParams.MountainScale - 1f) > 0.01f) {
                for (int x = 0; x < verticesX; x++) {
                    for (int y = 0; y < verticesY; y++) {
                        if (elevation[x, y] > seaLevelNorm) {
                            float aboveSea = elevation[x, y] - seaLevelNorm;
                            elevation[x, y] = Math.Clamp(seaLevelNorm + aboveSea * effectiveParams.MountainScale, 0f, 1f);
                        }
                    }
                }
            }

            progress?.Invoke("Assigning biomes...");
            BiomeMapper.Assign(elevation, moisture, seaLevelNorm,
                out var terrainTypes, out var sceneryIndices,
                verticesX, verticesY);

            progress?.Invoke("Placing towns...");
            var towns = p.TownCount > 0
                ? TownPlacer.Place(elevation, terrainTypes, effectiveParams, seaLevelNorm, heightTable, rng)
                : new List<TownSite>();

            HashSet<(ushort, byte)>? roadVertices = null;
            if (p.GenerateRoads && towns.Count >= 2) {
                progress?.Invoke("Generating road network...");
                roadVertices = RoadGenerator.Generate(towns, elevation, seaLevelNorm, effectiveParams, rng);
            }

            BuildingPlacer.PlaceResult? placeResult = null;
            if (p.GenerateBuildings && towns.Count > 0) {
                progress?.Invoke(effectiveParams.RetailTownBuildingsOnly
                    ? "Analyzing building catalog (retail town buildings only)..."
                    : "Analyzing building catalog...");
                BuildingAnalyzer.ClearCache();
                BuildingAnalyzer.AnalyzeAll(dats);

                progress?.Invoke("Placing buildings and decorations...");
                placeResult = BuildingPlacer.Place(towns, elevation, heightTable, seaLevelNorm, effectiveParams, dats, rng);
            }

            progress?.Invoke("Packing terrain data...");
            var result = PackResult(
                elevation, terrainTypes, sceneryIndices,
                roadVertices, placeResult, towns,
                heightTable, effectiveParams);

            progress?.Invoke($"Done. {result.TotalVerticesModified} vertices, {result.Towns.Count} towns, " +
                $"{result.TotalBuildingsPlaced} buildings, {result.TotalDecorationsPlaced} decorations, " +
                $"{result.TotalRoadVertices} road vertices.");
            return result;
        }

        private static WorldGeneratorResult PackResult(
            float[,] elevation,
            byte[,] terrainTypes,
            byte[,] sceneryIndices,
            HashSet<(ushort lbKey, byte vertexIndex)>? roadVertices,
            BuildingPlacer.PlaceResult? placeResult,
            List<TownSite> towns,
            float[] heightTable,
            WorldGeneratorParams p) {

            var terrainChanges = new Dictionary<ushort, Dictionary<byte, uint>>();
            int verticesX = elevation.GetLength(0);
            int verticesY = elevation.GetLength(1);
            int totalVertices = 0;

            for (int lbLocalX = 0; lbLocalX < p.Width; lbLocalX++) {
                for (int lbLocalY = 0; lbLocalY < p.Height; lbLocalY++) {
                    int globalLbX = p.StartX + lbLocalX;
                    int globalLbY = p.StartY + lbLocalY;
                    if (globalLbX < 0 || globalLbX > 254 || globalLbY < 0 || globalLbY > 254) continue;

                    ushort lbKey = (ushort)((globalLbX << 8) | globalLbY);
                    var lbChanges = new Dictionary<byte, uint>();

                    for (int cx = 0; cx <= 8; cx++) {
                        for (int cy = 0; cy <= 8; cy++) {
                            int vx = lbLocalX * 8 + cx;
                            int vy = lbLocalY * 8 + cy;
                            if (vx >= verticesX || vy >= verticesY) continue;

                            byte heightIdx = HeightMapGenerator.ElevationToHeightIndex(elevation[vx, vy], heightTable);
                            byte type = terrainTypes[vx, vy];
                            byte scenery = sceneryIndices[vx, vy];

                            byte road = 0;
                            byte vertexIdx = (byte)(cx * 9 + cy);
                            if (roadVertices != null && roadVertices.Contains((lbKey, vertexIdx))) {
                                road = 1;
                            }

                            var entry = new TerrainEntry(road, scenery, type, heightIdx);
                            lbChanges[vertexIdx] = entry.ToUInt();
                            totalVertices++;
                        }
                    }

                    if (lbChanges.Count > 0) {
                        terrainChanges[lbKey] = lbChanges;
                    }
                }
            }

            return new WorldGeneratorResult {
                TerrainChanges = terrainChanges,
                BuildingPlacements = placeResult?.Buildings ?? new(),
                DecorationPlacements = placeResult?.Decorations ?? new(),
                Towns = towns,
                TotalVerticesModified = totalVertices,
                TotalBuildingsPlaced = placeResult?.TotalBuildings ?? 0,
                TotalDecorationsPlaced = placeResult?.TotalDecorations ?? 0,
                TotalRoadVertices = roadVertices?.Count ?? 0
            };
        }
    }
}

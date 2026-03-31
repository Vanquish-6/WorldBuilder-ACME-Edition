using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using static WorldBuilder.Shared.Lib.TerrainHeightSampler;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public static class BuildingPlacer {
        private static readonly float[] CardinalYaws = { 0f, MathF.PI / 2f, MathF.PI, 3f * MathF.PI / 2f };

        private enum BuildingRole { TownHall, Tavern, Shop, House, Workshop, Temple, Barracks, Warehouse, Farm }

        private record struct PlotInfo(float X, float Y, float Yaw, BuildingRole Role, float MinSpacing);

        private static readonly Dictionary<BuildingRole, (int min, int max)> CityLayout = new() {
            { BuildingRole.TownHall,  (1, 1) },
            { BuildingRole.Tavern,    (2, 3) },
            { BuildingRole.Temple,    (1, 2) },
            { BuildingRole.Shop,      (6, 10) },
            { BuildingRole.Workshop,  (4, 6) },
            { BuildingRole.Barracks,  (1, 2) },
            { BuildingRole.Warehouse, (2, 4) },
            { BuildingRole.House,     (12, 20) },
            { BuildingRole.Farm,      (3, 5) },
        };

        private static readonly Dictionary<BuildingRole, (int min, int max)> TownLayout = new() {
            { BuildingRole.TownHall,  (1, 1) },
            { BuildingRole.Tavern,    (1, 2) },
            { BuildingRole.Temple,    (1, 1) },
            { BuildingRole.Shop,      (3, 6) },
            { BuildingRole.Workshop,  (2, 4) },
            { BuildingRole.Warehouse, (1, 2) },
            { BuildingRole.House,     (8, 14) },
            { BuildingRole.Farm,      (2, 4) },
        };

        private static readonly Dictionary<BuildingRole, (int min, int max)> VillageLayout = new() {
            { BuildingRole.Tavern,    (1, 1) },
            { BuildingRole.Shop,      (1, 3) },
            { BuildingRole.Workshop,  (1, 2) },
            { BuildingRole.House,     (4, 8) },
            { BuildingRole.Farm,      (1, 3) },
        };

        private static readonly Dictionary<BuildingRole, (int min, int max)> HamletLayout = new() {
            { BuildingRole.House,     (3, 5) },
            { BuildingRole.Farm,      (1, 2) },
            { BuildingRole.Shop,      (0, 1) },
        };

        private const int FlattenRadius = 3;

        public record struct PlaceResult(
            Dictionary<ushort, List<PlannedBuilding>> Buildings,
            Dictionary<ushort, List<StaticObject>> Decorations,
            int TotalBuildings,
            int TotalDecorations
        );

        public static PlaceResult Place(
            List<TownSite> towns, float[,] elevation, float[] landHeightTable,
            float seaLevelNorm, WorldGeneratorParams p, IDatReaderWriter dats, Random rng) {

            var townBuildingIds = new List<uint>(BuildingAnalyzer.GetTownBuildings(dats, p.RetailTownBuildingsOnly));
            if (townBuildingIds.Count == 0) {
                Console.WriteLine("[BuildingPlacer] No suitable complete buildings found -- skipping building placement (decorations only)");
            }

            var decorationIds = TownDecorationCatalog.GetDecorations(dats);

            var buildings = new Dictionary<ushort, List<PlannedBuilding>>();
            var decorations = new Dictionary<ushort, List<StaticObject>>();
            int totalBuildings = 0, totalDecorations = 0;

            foreach (var town in towns) {
                var layout = town.Size switch {
                    3 => CityLayout,
                    2 => TownLayout,
                    1 => VillageLayout,
                    _ => HamletLayout
                };

                var plots = GeneratePlots(town, layout, rng, elevation, seaLevelNorm, p);
                int placedCount = 0;

                foreach (var plot in plots) {
                    if (townBuildingIds.Count > 0) {
                        float px = plot.X, py = plot.Y;
                        if (!SnapToOutdoorCellCenter(ref px, ref py))
                            continue;

                        float bz = FlattenTerrainAtPlot(px, py, elevation, landHeightTable, p);

                        uint modelId = townBuildingIds[rng.Next(townBuildingIds.Count)];
                        var blueprint = BuildingBlueprintCache.GetBlueprint(modelId, dats);
                        var orientation = blueprint != null
                            ? blueprint.DonorOrientation
                            : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, plot.Yaw);

                        AddBuilding(buildings, px, py, bz, modelId, orientation, town.Name);
                        totalBuildings++;
                        placedCount++;
                    }

                    if (decorationIds.Count > 0) {
                        int decorCount = plot.Role switch {
                            BuildingRole.TownHall => rng.Next(3, 6),
                            BuildingRole.Tavern => rng.Next(2, 4),
                            BuildingRole.Shop or BuildingRole.Workshop => rng.Next(1, 3),
                            BuildingRole.Warehouse => rng.Next(2, 4),
                            BuildingRole.Farm => rng.Next(1, 3),
                            BuildingRole.House => rng.Next(1, 2),
                            _ => rng.Next(1, 2)
                        };

                        for (int d = 0; d < decorCount; d++) {
                            float decorDist = 4f + (float)(rng.NextDouble() * 8f);
                            float decorAngle = (float)(rng.NextDouble() * Math.PI * 2.0);
                            float dx = plot.X + MathF.Cos(decorAngle) * decorDist;
                            float dy = plot.Y + MathF.Sin(decorAngle) * decorDist;
                            float dz = SampleTerrainHeight(dx, dy, elevation, landHeightTable, p);

                            uint decorId = decorationIds[rng.Next(decorationIds.Count)];
                            float decorYaw = (float)(rng.NextDouble() * Math.PI * 2.0);
                            var decorOri = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, decorYaw);

                            float scale = 0.8f + (float)(rng.NextDouble() * 0.4);
                            AddDecoration(decorations, dx, dy, dz, decorId, decorOri, new Vector3(scale, scale, scale));
                            totalDecorations++;
                        }
                    }
                }

                if (decorationIds.Count > 0 && plots.Count >= 2) {
                    int pathDecorCount = town.Size switch {
                        3 => rng.Next(10, 20),
                        2 => rng.Next(6, 12),
                        1 => rng.Next(3, 7),
                        _ => rng.Next(2, 4)
                    };

                    float cx = town.WorldCenter.X;
                    float cy = town.WorldCenter.Y;
                    float radiusWorld = town.Radius * 192f;

                    for (int i = 0; i < pathDecorCount; i++) {
                        float t = (float)rng.NextDouble();
                        float dist = t * radiusWorld * 0.5f;
                        float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                        float dx = cx + MathF.Cos(angle) * dist;
                        float dy = cy + MathF.Sin(angle) * dist;
                        float dz = SampleTerrainHeight(dx, dy, elevation, landHeightTable, p);

                        uint decorId = decorationIds[rng.Next(decorationIds.Count)];
                        float decorYaw = (float)(rng.NextDouble() * Math.PI * 2.0);
                        var decorOri = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, decorYaw);

                        float scale = 0.7f + (float)(rng.NextDouble() * 0.6);
                        AddDecoration(decorations, dx, dy, dz, decorId, decorOri, new Vector3(scale, scale, scale));
                        totalDecorations++;
                    }
                }

                town.BuildingCount = placedCount;
            }

            return new PlaceResult(buildings, decorations, totalBuildings, totalDecorations);
        }

        /// <summary>
        /// Snap to the center of an outdoor cell (1..6), matching manual building placement
        /// so BSP / collision line up with the landcell grid.
        /// </summary>
        private static bool SnapToOutdoorCellCenter(ref float worldX, ref float worldY) {
            int lbX = (int)Math.Floor(worldX / 192f);
            int lbY = (int)Math.Floor(worldY / 192f);
            if (lbX < 0 || lbX > 254 || lbY < 0 || lbY > 254) return false;

            float lbOriginX = lbX * 192f;
            float lbOriginY = lbY * 192f;
            float localX = worldX - lbOriginX;
            float localY = worldY - lbOriginY;

            int cellX = Math.Clamp((int)(localX / 24f), 1, 6);
            int cellY = Math.Clamp((int)(localY / 24f), 1, 6);

            worldX = lbOriginX + cellX * 24f + 12f;
            worldY = lbOriginY + cellY * 24f + 12f;
            return true;
        }

        /// <summary>
        /// AC-accurate triangle-interpolated height sample on the in-memory elevation
        /// array, matching <see cref="TerrainHeightSampler.SampleHeightTriangle"/>.
        /// Uses the same pseudo-random cell split and bilinear triangle interpolation
        /// that the client/renderer uses so placed objects sit flush on the terrain.
        /// </summary>
        private static float SampleTerrainHeight(float worldX, float worldY,
            float[,] elevation, float[] landHeightTable, WorldGeneratorParams p) {

            int w = elevation.GetLength(0);
            int h = elevation.GetLength(1);

            float cellXf = worldX / 24f;
            float cellYf = worldY / 24f;

            int cellX = (int)Math.Floor(cellXf);
            int cellY = (int)Math.Floor(cellYf);

            float fracX = cellXf - cellX;
            float fracY = cellYf - cellY;

            int ofsX = p.StartX * 8;
            int ofsY = p.StartY * 8;

            float HeightAt(int gx, int gy) {
                int lx = gx - ofsX, ly = gy - ofsY;
                if (lx < 0 || lx >= w || ly < 0 || ly >= h) return 0f;
                byte idx = HeightMapGenerator.ElevationToHeightIndex(elevation[lx, ly], landHeightTable);
                return landHeightTable[idx];
            }

            float hSW = HeightAt(cellX, cellY);
            float hSE = HeightAt(cellX + 1, cellY);
            float hNW = HeightAt(cellX, cellY + 1);
            float hNE = HeightAt(cellX + 1, cellY + 1);

            bool swToNE = IsSWtoNEcut((uint)cellX, (uint)cellY);

            if (swToNE) {
                if (fracX > fracY)
                    return hSW + fracX * (hSE - hSW) + fracY * (hNE - hSE);
                else
                    return hSW + fracX * (hNE - hNW) + fracY * (hNW - hSW);
            } else {
                if (fracX + fracY <= 1.0f)
                    return hSW + fracX * (hSE - hSW) + fracY * (hNW - hSW);
                else
                    return hNE + (1.0f - fracX) * (hNW - hNE) + (1.0f - fracY) * (hSE - hNE);
            }
        }

        /// <summary>
        /// Flattens terrain vertices in a radius around a building plot, then returns
        /// the resulting Z height. Modifies the elevation array in-place so terrain
        /// packing reflects the flattened ground.
        /// </summary>
        private static float FlattenTerrainAtPlot(float worldX, float worldY,
            float[,] elevation, float[] landHeightTable, WorldGeneratorParams p) {

            int w = elevation.GetLength(0);
            int h = elevation.GetLength(1);
            int cvx = (int)Math.Floor(worldX / 24f) - p.StartX * 8;
            int cvy = (int)Math.Floor(worldY / 24f) - p.StartY * 8;

            // Find max elevation in the footprint -- flatten UP so building sits on ground
            float maxElev = 0f;
            for (int dx = -FlattenRadius; dx <= FlattenRadius; dx++) {
                for (int dy = -FlattenRadius; dy <= FlattenRadius; dy++) {
                    int vx = cvx + dx, vy = cvy + dy;
                    if (vx >= 0 && vx < w && vy >= 0 && vy < h) {
                        if (elevation[vx, vy] > maxElev)
                            maxElev = elevation[vx, vy];
                    }
                }
            }

            // Flatten inner footprint and blend a transition ring for natural terrain merge
            int transitionRadius = FlattenRadius + 2;
            for (int dx = -transitionRadius; dx <= transitionRadius; dx++) {
                for (int dy = -transitionRadius; dy <= transitionRadius; dy++) {
                    int vx = cvx + dx, vy = cvy + dy;
                    if (vx < 0 || vx >= w || vy < 0 || vy >= h) continue;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist <= FlattenRadius) {
                        elevation[vx, vy] = maxElev;
                    } else if (dist <= transitionRadius) {
                        float t = (dist - FlattenRadius) / (transitionRadius - FlattenRadius);
                        t *= t;
                        elevation[vx, vy] = maxElev + t * (elevation[vx, vy] - maxElev);
                    }
                }
            }

            return SampleTerrainHeight(worldX, worldY, elevation, landHeightTable, p);
        }

        private static void AddBuilding(Dictionary<ushort, List<PlannedBuilding>> result,
            float x, float y, float z, uint modelId, Quaternion orientation, string townName) {
            int lbX = (int)Math.Floor(x / 192f);
            int lbY = (int)Math.Floor(y / 192f);
            if (lbX < 0 || lbX > 254 || lbY < 0 || lbY > 254) return;

            ushort lbKey = (ushort)((lbX << 8) | lbY);
            if (!result.TryGetValue(lbKey, out var list)) {
                list = new List<PlannedBuilding>();
                result[lbKey] = list;
            }

            list.Add(new PlannedBuilding {
                ModelId = modelId,
                WorldPosition = new Vector3(x, y, z),
                Orientation = orientation,
                TownName = townName
            });
        }

        private static void AddDecoration(Dictionary<ushort, List<StaticObject>> result,
            float x, float y, float z, uint modelId, Quaternion orientation, Vector3 scale) {
            int lbX = (int)Math.Floor(x / 192f);
            int lbY = (int)Math.Floor(y / 192f);
            if (lbX < 0 || lbX > 254 || lbY < 0 || lbY > 254) return;

            ushort lbKey = (ushort)((lbX << 8) | lbY);
            if (!result.TryGetValue(lbKey, out var list)) {
                list = new List<StaticObject>();
                result[lbKey] = list;
            }

            list.Add(new StaticObject {
                Id = modelId,
                IsSetup = (modelId & 0x02000000) != 0,
                Origin = new Vector3(x, y, z),
                Orientation = orientation,
                Scale = scale
            });
        }

        private static List<PlotInfo> GeneratePlots(TownSite town, Dictionary<BuildingRole, (int min, int max)> layout, Random rng,
            float[,] elevation, float seaLevelNorm, WorldGeneratorParams p) {
            float cx = town.WorldCenter.X;
            float cy = town.WorldCenter.Y;
            float radiusWorld = town.Radius * 192f;
            var plots = new List<PlotInfo>();
            var occupied = new List<(float x, float y, float r)>();

            foreach (var (role, range) in layout) {
                int count = rng.Next(range.min, range.max + 1);
                for (int i = 0; i < count; i++) {
                    var plot = PlaceSingleBuilding(cx, cy, radiusWorld, role, occupied, rng, elevation, seaLevelNorm, p);
                    if (plot.HasValue) {
                        plots.Add(plot.Value);
                        occupied.Add((plot.Value.X, plot.Value.Y, plot.Value.MinSpacing));
                    }
                }
            }

            return plots;
        }

        private static PlotInfo? PlaceSingleBuilding(
            float cx, float cy, float radiusWorld,
            BuildingRole role, List<(float x, float y, float r)> occupied, Random rng,
            float[,] elevation, float seaLevelNorm, WorldGeneratorParams p) {

            float ringMin, ringMax, minSpacing;
            switch (role) {
                case BuildingRole.TownHall:
                    ringMin = 0f; ringMax = 0.1f; minSpacing = 22f; break;
                case BuildingRole.Tavern:
                case BuildingRole.Temple:
                    ringMin = 0.02f; ringMax = 0.2f; minSpacing = 20f; break;
                case BuildingRole.Shop:
                case BuildingRole.Workshop:
                    ringMin = 0.05f; ringMax = 0.3f; minSpacing = 18f; break;
                case BuildingRole.Barracks:
                case BuildingRole.Warehouse:
                    ringMin = 0.15f; ringMax = 0.4f; minSpacing = 20f; break;
                case BuildingRole.House:
                    ringMin = 0.1f; ringMax = 0.5f; minSpacing = 16f; break;
                case BuildingRole.Farm:
                    ringMin = 0.4f; ringMax = 0.7f; minSpacing = 22f; break;
                default:
                    ringMin = 0.05f; ringMax = 0.4f; minSpacing = 18f; break;
            }

            for (int attempt = 0; attempt < 60; attempt++) {
                float t = ringMin + (float)rng.NextDouble() * (ringMax - ringMin);
                float dist = t * radiusWorld;
                if (dist < 8f) dist = 8f;

                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                float px = cx + MathF.Cos(angle) * dist;
                float py = cy + MathF.Sin(angle) * dist;

                bool collision = false;
                foreach (var (ox, oy, or) in occupied) {
                    float dx = px - ox, dy = py - oy;
                    float reqDist = (minSpacing + or) * 0.65f;
                    if (dx * dx + dy * dy < reqDist * reqDist) {
                        collision = true;
                        break;
                    }
                }
                if (collision) continue;

                // Reject plots on or near water
                int vxGlobal = (int)Math.Floor(px / 24f);
                int vyGlobal = (int)Math.Floor(py / 24f);
                int vxLocal = vxGlobal - p.StartX * 8;
                int vyLocal = vyGlobal - p.StartY * 8;
                if (vxLocal >= 0 && vxLocal < elevation.GetLength(0) &&
                    vyLocal >= 0 && vyLocal < elevation.GetLength(1) &&
                    elevation[vxLocal, vyLocal] < seaLevelNorm + 0.02f)
                    continue;

                float yaw = CardinalYaws[rng.Next(4)] + (float)(rng.NextDouble() - 0.5) * 0.25f;
                return new PlotInfo(px, py, yaw, role, minSpacing);
            }

            return null;
        }
    }
}

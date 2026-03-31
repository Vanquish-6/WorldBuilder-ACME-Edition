using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public static class TownPlacer {
        private static readonly string[] Prefixes = {
            "North", "South", "East", "West", "Old", "New", "Fort", "Port",
            "High", "Low", "Upper", "Lower", "Great", "Little", "Dark", "Bright",
            "Iron", "Silver", "Golden", "Storm", "Shadow", "Sun", "Moon", "Star"
        };
        private static readonly string[] Roots = {
            "haven", "stead", "ford", "gate", "bridge", "hollow", "reach", "vale",
            "crest", "fall", "keep", "hold", "watch", "rest", "well", "brook",
            "stone", "wood", "field", "moor", "ridge", "cliff", "bay", "shore",
            "marsh", "grove", "dale", "peak", "helm", "ward", "mill", "wick"
        };

        public static List<TownSite> Place(
            float[,] elevation, byte[,] terrainTypes,
            WorldGeneratorParams p, float seaLevelNorm, float[] landHeightTable, Random rng) {

            int verticesX = elevation.GetLength(0);
            int verticesY = elevation.GetLength(1);
            int lbCountX = (verticesX - 1) / 8;
            int lbCountY = (verticesY - 1) / 8;

            var candidates = ScoreCandidates(elevation, terrainTypes, seaLevelNorm, p, lbCountX, lbCountY);
            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            var placed = new List<TownSite>();
            float minDistSq = p.TownSpacing * p.TownSpacing;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (lbX, lbY, score, flatness) in candidates) {
                if (placed.Count >= p.TownCount) break;

                bool tooClose = false;
                foreach (var existing in placed) {
                    float dx = lbX - existing.CenterLbX;
                    float dy = lbY - existing.CenterLbY;
                    if (dx * dx + dy * dy < minDistSq) {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                int size;
                float radius;
                if (flatness > 0.85f && score > 0.7f) {
                    size = 3; radius = 4f;
                } else if (flatness > 0.7f) {
                    size = 2; radius = 3f;
                } else if (flatness > 0.45f) {
                    size = 1; radius = 2f;
                } else {
                    size = 0; radius = 1.5f;
                }

                int globalLbX = p.StartX + lbX;
                int globalLbY = p.StartY + lbY;
                float worldX = (globalLbX + 0.5f) * 192f;
                float worldY = (globalLbY + 0.5f) * 192f;

                int vx = Math.Clamp(lbX * 8 + 4, 0, verticesX - 1);
                int vy = Math.Clamp(lbY * 8 + 4, 0, verticesY - 1);
                byte hIdx = HeightMapGenerator.ElevationToHeightIndex(elevation[vx, vy], landHeightTable);
                float worldZ = landHeightTable[hIdx];

                string name = GenerateName(rng, usedNames);

                placed.Add(new TownSite {
                    Name = name,
                    CenterLbX = globalLbX,
                    CenterLbY = globalLbY,
                    WorldCenter = new Vector3(worldX, worldY, worldZ),
                    Radius = radius,
                    Size = size
                });
            }

            return placed;
        }

        private static string GenerateName(Random rng, HashSet<string> used) {
            for (int attempt = 0; attempt < 50; attempt++) {
                string name;
                if (rng.NextDouble() < 0.6) {
                    name = Prefixes[rng.Next(Prefixes.Length)] + Roots[rng.Next(Roots.Length)];
                } else {
                    name = Prefixes[rng.Next(Prefixes.Length)] + " " + Prefixes[rng.Next(Prefixes.Length)].ToLower() + Roots[rng.Next(Roots.Length)];
                }
                if (used.Add(name)) return name;
            }
            return "Settlement " + (used.Count + 1);
        }

        private static List<(int lbX, int lbY, float score, float flatness)> ScoreCandidates(
            float[,] elevation, byte[,] terrainTypes, float seaLevelNorm,
            WorldGeneratorParams p, int lbCountX, int lbCountY) {

            var results = new List<(int, int, float, float)>();
            int margin = 2;

            for (int lbX = margin; lbX < lbCountX - margin; lbX++) {
                for (int lbY = margin; lbY < lbCountY - margin; lbY++) {
                    float score = ScoreLandblock(elevation, terrainTypes, seaLevelNorm, lbX, lbY, out float flatness);
                    if (score > 0.1f) {
                        results.Add((lbX, lbY, score, flatness));
                    }
                }
            }

            return results;
        }

        private static float ScoreLandblock(
            float[,] elevation, byte[,] terrainTypes, float seaLevelNorm,
            int lbX, int lbY, out float flatness) {

            int verticesX = elevation.GetLength(0);
            int verticesY = elevation.GetLength(1);
            float sum = 0f, sumSq = 0f;
            int count = 0;
            int waterCount = 0;
            int landCount = 0;

            for (int dx = -1; dx <= 1; dx++) {
                for (int dy = -1; dy <= 1; dy++) {
                    int basex = (lbX + dx) * 8;
                    int basey = (lbY + dy) * 8;
                    for (int vx = 0; vx <= 8; vx++) {
                        for (int vy = 0; vy <= 8; vy++) {
                            int gx = basex + vx;
                            int gy = basey + vy;
                            if (gx < 0 || gx >= verticesX || gy < 0 || gy >= verticesY) continue;

                            float e = elevation[gx, gy];
                            sum += e;
                            sumSq += e * e;
                            count++;

                            if (e > seaLevelNorm) landCount++;
                            else waterCount++;
                        }
                    }
                }
            }

            if (count == 0) { flatness = 0; return 0; }

            float mean = sum / count;
            float variance = sumSq / count - mean * mean;
            flatness = 1f - Math.Clamp(variance * 500f, 0f, 1f);

            if (mean < seaLevelNorm + 0.08f) { flatness = 0; return 0; }
            if (mean > 0.7f) { flatness = 0; return 0; }

            float landRatio = (float)landCount / count;
            if (landRatio < 0.9f) { flatness = 0; return 0; }

            float elevScore = 1f - MathF.Abs(mean - 0.35f) * 3f;
            elevScore = Math.Clamp(elevScore, 0f, 1f);

            bool nearWater = waterCount > 0;
            float waterBonus = nearWater ? 0.2f : 0f;

            return flatness * 0.5f + elevScore * 0.3f + waterBonus;
        }
    }
}

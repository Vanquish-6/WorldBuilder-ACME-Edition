using System;
using System.Collections.Generic;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public static class HeightMapGenerator {
        private struct Landmass {
            public float CenterX, CenterY, Radius;
            public bool IsContinent;
            public float NoiseOfsX, NoiseOfsY;
            public float Angle;
            public float Elongation;
        }

        /// <summary>
        /// Generates an elevation map with distinct continents and islands.
        /// Uses domain warping and per-landmass noise for organic, non-circular shapes.
        /// </summary>
        public static float[,] Generate(WorldGeneratorParams p, SimplexNoise noise, int verticesX, int verticesY, Random rng) {
            var landmasses = PlaceLandmasses(p, verticesX, verticesY, rng);
            var map = new float[verticesX, verticesY];
            int noiseOffset = p.Seed * 1000;

            for (int x = 0; x < verticesX; x++) {
                for (int y = 0; y < verticesY; y++) {
                    float wx = (p.StartX * 8 + x) * 0.005f + noiseOffset;
                    float wy = (p.StartY * 8 + y) * 0.005f + noiseOffset;

                    float landInfluence = ComputeLandInfluence(x, y, landmasses, noise);

                    // Edge falloff to prevent continent overflow beyond world boundary
                    int edgeMargin = Math.Max(verticesX, verticesY) / 20;
                    int edgeDist = Math.Min(Math.Min(x, verticesX - 1 - x), Math.Min(y, verticesY - 1 - y));
                    if (edgeDist < edgeMargin)
                        landInfluence *= (float)edgeDist / edgeMargin;

                    float detail = noise.FBM(wx * 2f, wy * 2f, 6, 0.45f + p.Roughness * 0.1f, 2.1f);
                    float ridged = noise.RidgedNoise(wx * 1.5f + 500f, wy * 1.5f + 500f, 4, 0.5f, 2f);

                    float elevation;
                    if (landInfluence > 0.02f) {
                        float terrainVariation = 0.5f + detail * 0.25f + ridged * 0.2f;
                        elevation = 0.12f + landInfluence * 0.65f * terrainVariation;
                    } else {
                        elevation = landInfluence * 0.5f + 0.01f * (detail + 1f);
                    }

                    map[x, y] = Math.Clamp(elevation, 0f, 1f);
                }
            }

            return map;
        }

        /// <summary>
        /// Generates a moisture map for biome selection. Separate noise domain.
        /// </summary>
        public static float[,] GenerateMoisture(WorldGeneratorParams p, SimplexNoise noise, int verticesX, int verticesY) {
            var map = new float[verticesX, verticesY];
            int offset = p.Seed * 1000 + 50000;

            for (int x = 0; x < verticesX; x++) {
                for (int y = 0; y < verticesY; y++) {
                    float wx = (p.StartX * 8 + x) * 0.003f + offset;
                    float wy = (p.StartY * 8 + y) * 0.003f + offset;

                    float moisture = noise.FBM(wx, wy, 4, 0.5f, 2f);
                    moisture = (moisture + 1f) * 0.5f;
                    map[x, y] = Math.Clamp(moisture, 0f, 1f);
                }
            }

            return map;
        }

        public static byte ElevationToHeightIndex(float elevation, float[] landHeightTable) {
            float minH = landHeightTable[0];
            float maxH = landHeightTable[landHeightTable.Length - 1];
            float targetZ = minH + elevation * (maxH - minH);

            // Binary search for the nearest entry (table is sorted ascending).
            int lo = 0, hi = landHeightTable.Length - 1;
            while (lo < hi) {
                int mid = (lo + hi) >> 1;
                if (landHeightTable[mid] < targetZ) lo = mid + 1;
                else hi = mid;
            }
            // lo is the first index >= targetZ; compare with lo-1 to find the closest.
            if (lo > 0 && MathF.Abs(landHeightTable[lo - 1] - targetZ) <= MathF.Abs(landHeightTable[lo] - targetZ))
                lo--;
            return (byte)lo;
        }

        public static float SeaLevelNormalized(int seaLevelIndex, float[] landHeightTable) {
            float minH = landHeightTable[0];
            float maxH = landHeightTable[landHeightTable.Length - 1];
            float seaZ = landHeightTable[Math.Clamp(seaLevelIndex, 0, landHeightTable.Length - 1)];
            return (seaZ - minH) / (maxH - minH);
        }

        private static float ComputeLandInfluence(int x, int y, List<Landmass> landmasses, SimplexNoise noise) {
            float maxInfluence = 0f;
            foreach (var lm in landmasses) {
                float dx = x - lm.CenterX;
                float dy = y - lm.CenterY;

                // Domain warp: displace the vector itself for organic blob shapes.
                // Each landmass uses its own noise offset so every shape is unique.
                float warpScale = lm.IsContinent ? 0.007f : 0.012f;
                float warpStrength = lm.Radius * 0.55f;
                float wx = noise.FBM(x * warpScale + lm.NoiseOfsX,
                                     y * warpScale + lm.NoiseOfsY, 4, 0.55f, 2f);
                float wy = noise.FBM(x * warpScale + lm.NoiseOfsX + 300f,
                                     y * warpScale + lm.NoiseOfsY + 300f, 4, 0.55f, 2f);
                dx += wx * warpStrength;
                dy += wy * warpStrength;

                // Rotate into the landmass's local frame and apply elongation
                float cosA = MathF.Cos(lm.Angle);
                float sinA = MathF.Sin(lm.Angle);
                float localX = dx * cosA + dy * sinA;
                float localY = -dx * sinA + dy * cosA;
                localX /= lm.Elongation;

                float dist = MathF.Sqrt(localX * localX + localY * localY);

                // Fine coastline noise for bays and peninsulas
                float coastFreq = lm.IsContinent ? 0.025f : 0.04f;
                float coastAmp = lm.Radius * 0.12f;
                float coastNoise = noise.FBM(x * coastFreq + lm.NoiseOfsX + 700f,
                                             y * coastFreq + lm.NoiseOfsY + 700f, 3, 0.5f, 2.2f);
                dist += coastNoise * coastAmp;

                if (dist >= lm.Radius) continue;

                float t = 1f - dist / lm.Radius;
                t = t * t * (3f - 2f * t);
                maxInfluence = Math.Max(maxInfluence, t);
            }
            return maxInfluence;
        }

        private static List<Landmass> PlaceLandmasses(WorldGeneratorParams p, int verticesX, int verticesY, Random rng) {
            var result = new List<Landmass>();
            float area = (float)verticesX * verticesY;
            float coverageMul = 0.5f + p.LandCoverage;

            float continentRadius = 0;
            if (p.ContinentCount > 0) {
                float targetArea = area * 0.45f / p.ContinentCount;
                continentRadius = MathF.Sqrt(targetArea / MathF.PI) * coverageMul;
            }

            float islandBaseRadius;
            if (p.ContinentCount == 0 && p.IslandCount > 0) {
                float targetArea = area * 0.25f / p.IslandCount;
                islandBaseRadius = MathF.Sqrt(targetArea / MathF.PI) * coverageMul;
            } else {
                islandBaseRadius = Math.Max(continentRadius * 0.18f, 15f) * coverageMul;
            }

            for (int i = 0; i < p.ContinentCount; i++) {
                float r = continentRadius * (0.8f + (float)rng.NextDouble() * 0.4f);
                var (cx, cy) = FindCenter(result, r, verticesX, verticesY, rng, r * 0.3f);
                result.Add(new Landmass {
                    CenterX = cx, CenterY = cy, Radius = r, IsContinent = true,
                    NoiseOfsX = (float)rng.NextDouble() * 10000f,
                    NoiseOfsY = (float)rng.NextDouble() * 10000f,
                    Angle = (float)(rng.NextDouble() * Math.PI * 2),
                    Elongation = 1.3f + (float)rng.NextDouble() * 0.9f
                });
            }

            for (int i = 0; i < p.IslandCount; i++) {
                float r = islandBaseRadius * (0.5f + (float)rng.NextDouble() * 1.0f);
                var (cx, cy) = FindCenter(result, r, verticesX, verticesY, rng, r * 0.5f);
                result.Add(new Landmass {
                    CenterX = cx, CenterY = cy, Radius = r, IsContinent = false,
                    NoiseOfsX = (float)rng.NextDouble() * 10000f,
                    NoiseOfsY = (float)rng.NextDouble() * 10000f,
                    Angle = (float)(rng.NextDouble() * Math.PI * 2),
                    Elongation = 1.0f + (float)rng.NextDouble() * 0.6f
                });
            }

            return result;
        }

        private static (float x, float y) FindCenter(
            List<Landmass> existing, float radius,
            int w, int h, Random rng, float minSpacing) {

            float margin = Math.Max(radius * 0.5f, Math.Min(w, h) * 0.15f);

            for (int attempt = 0; attempt < 200; attempt++) {
                float x = margin + (float)rng.NextDouble() * (w - 2f * margin);
                float y = margin + (float)rng.NextDouble() * (h - 2f * margin);

                bool valid = true;
                foreach (var lm in existing) {
                    float dx = x - lm.CenterX;
                    float dy = y - lm.CenterY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float minDist = (radius + lm.Radius) * 0.55f + minSpacing;
                    if (dist < minDist) { valid = false; break; }
                }

                if (valid) return (x, y);
            }

            float fx = margin + (float)rng.NextDouble() * (w - 2f * margin);
            float fy = margin + (float)rng.NextDouble() * (h - 2f * margin);
            return (fx, fy);
        }
    }
}

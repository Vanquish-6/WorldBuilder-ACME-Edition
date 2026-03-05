using System;
using System.Collections.Generic;
using DatReaderWriter.Enums;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape {
    public readonly record struct RgbPixel(byte R, byte G, byte B);

    public readonly record struct VertexChange(
        int VertexIndex,
        byte OrigHeight, byte NewHeight,
        byte OrigType, byte NewType);

    public static class HeightmapImportService {
        public const int MAP_SIZE = 254;
        public const int VERTS_PER_BLOCK = 9;
        public const int CELLS_PER_BLOCK = 8;

        private static readonly HashSet<TerrainTextureType> ExcludedTypes = new() {
            TerrainTextureType.WaterRunning,
            TerrainTextureType.WaterStandingFresh,
            TerrainTextureType.WaterShallowSea,
            TerrainTextureType.WaterShallowStillSea,
            TerrainTextureType.WaterDeepSea,
            TerrainTextureType.FauxWaterRunning,
            TerrainTextureType.SeaSlime,
            TerrainTextureType.RoadType
        };

        public static (int Width, int Height) GetTargetDimensions(int lbCountX, int lbCountY) {
            return (lbCountX * CELLS_PER_BLOCK + 1, lbCountY * CELLS_PER_BLOCK + 1);
        }

        /// <summary>
        /// Loads an image in full RGB color and resamples to the target vertex grid dimensions.
        /// </summary>
        public static RgbPixel[,] LoadAndResampleRgb(string filePath, int targetWidth, int targetHeight) {
            using var image = Image.Load<Rgb24>(filePath);
            image.Mutate(ctx => ctx.Resize(targetWidth, targetHeight));

            var grid = new RgbPixel[targetWidth, targetHeight];
            image.ProcessPixelRows(accessor => {
                for (int y = 0; y < targetHeight; y++) {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < targetWidth; x++) {
                        grid[x, y] = new RgbPixel(row[x].R, row[x].G, row[x].B);
                    }
                }
            });
            return grid;
        }

        /// <summary>
        /// Computes luminance from RGB using standard BT.601 weights.
        /// </summary>
        public static byte Luminance(byte r, byte g, byte b) {
            return (byte)Math.Clamp((int)(0.299f * r + 0.587f * g + 0.114f * b + 0.5f), 0, 255);
        }

        /// <summary>
        /// Finds the terrain type whose average color is closest (Euclidean RGB distance)
        /// to the given pixel, excluding water types and RoadType.
        /// </summary>
        public static byte FindClosestTerrainType(
            byte r, byte g, byte b,
            Dictionary<TerrainTextureType, (byte R, byte G, byte B)> averageColors) {

            int bestDist = int.MaxValue;
            TerrainTextureType bestType = TerrainTextureType.Grassland;

            foreach (var (type, avg) in averageColors) {
                if (ExcludedTypes.Contains(type)) continue;

                int dr = r - avg.R;
                int dg = g - avg.G;
                int db = b - avg.B;
                int dist = dr * dr + dg * dg + db * db;

                if (dist < bestDist) {
                    bestDist = dist;
                    bestType = type;
                }
            }

            return (byte)bestType;
        }

        /// <summary>
        /// Builds per-landblock changes for both height (from luminance) and terrain type
        /// (from nearest color match). Only vertices that differ are included.
        /// </summary>
        public static Dictionary<ushort, List<VertexChange>> BuildChanges(
            RgbPixel[,] grid,
            int startLbX, int startLbY,
            int lbCountX, int lbCountY,
            TerrainSystem terrainSystem,
            Dictionary<TerrainTextureType, (byte R, byte G, byte B)> averageColors) {

            var changes = new Dictionary<ushort, List<VertexChange>>();
            int gridW = grid.GetLength(0);
            int gridH = grid.GetLength(1);

            for (int lbOffX = 0; lbOffX < lbCountX; lbOffX++) {
                for (int lbOffY = 0; lbOffY < lbCountY; lbOffY++) {
                    int lbX = startLbX + lbOffX;
                    int lbY = startLbY + lbOffY;
                    if (lbX < 0 || lbX > 0xFF || lbY < 0 || lbY > 0xFF) continue;

                    var lbKey = (ushort)((lbX << 8) | lbY);
                    var terrainData = terrainSystem.GetLandblockTerrain(lbKey);
                    if (terrainData == null) continue;

                    List<VertexChange>? lbChanges = null;

                    for (int vx = 0; vx < VERTS_PER_BLOCK; vx++) {
                        for (int vy = 0; vy < VERTS_PER_BLOCK; vy++) {
                            int px = lbOffX * CELLS_PER_BLOCK + vx;
                            int py = lbOffY * CELLS_PER_BLOCK + vy;
                            if (px >= gridW || py >= gridH) continue;

                            var pixel = grid[px, py];
                            byte newHeight = Luminance(pixel.R, pixel.G, pixel.B);
                            byte newType = FindClosestTerrainType(pixel.R, pixel.G, pixel.B, averageColors);

                            int vertexIndex = vx * VERTS_PER_BLOCK + vy;
                            var existing = terrainData[vertexIndex];

                            if (existing.Height == newHeight && existing.Type == newType) continue;

                            lbChanges ??= new List<VertexChange>();
                            lbChanges.Add(new VertexChange(
                                vertexIndex,
                                existing.Height, newHeight,
                                existing.Type, newType));
                        }
                    }

                    if (lbChanges != null) {
                        changes[lbKey] = lbChanges;
                    }
                }
            }

            return changes;
        }
    }
}

using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Services {
    public static class StampThumbnailGenerator {
        // Simple color map for AC terrain types (approximate)
        private static readonly Rgba32[] TerrainColors = new Rgba32[] {
            new Rgba32(34, 139, 34),   // 0: Barren Rock -> Forest Green (placeholder)
            new Rgba32(107, 142, 35),  // 1: Grass -> Olive Drab
            new Rgba32(240, 248, 255), // 2: Ice -> Alice Blue
            new Rgba32(139, 69, 19),   // 3: Obsidian -> Saddle Brown
            new Rgba32(210, 180, 140), // 4: Sand -> Tan
            new Rgba32(112, 128, 144), // 5: Stone -> Slate Gray
            new Rgba32(34, 139, 34),   // 6: Grass -> Forest Green
            new Rgba32(255, 250, 250), // 7: Snow -> Snow
            // Add more as needed, fallback to grey
        };

        public static void GenerateThumbnail(TerrainStamp stamp, string outputPath) {
            int width = stamp.WidthInVertices;
            int height = stamp.HeightInVertices;

            using var image = new Image<Rgba32>(width, height);

            // Find min/max height for shading
            byte minH = 255;
            byte maxH = 0;
            foreach (var h in stamp.Heights) {
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
            float heightRange = Math.Max(1, (float)maxH - minH);

            image.ProcessPixelRows(accessor => {
                for (int y = 0; y < height; y++) {
                    var pixelRow = accessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++) {
                        // Stamp data is stored column-major (x * height + y)
                        // ImageSharp is row-major (y, x).
                        int stampIndex = x * height + y;

                        if (stampIndex >= stamp.Heights.Length) continue;

                        byte h = stamp.Heights[stampIndex];
                        ushort terrainWord = stamp.TerrainTypes[stampIndex];
                        byte terrainType = (byte)((terrainWord >> 2) & 0x1F);

                        // Base color from terrain type
                        Rgba32 baseColor;
                        if (terrainType < TerrainColors.Length) {
                            baseColor = TerrainColors[terrainType];
                        } else {
                            baseColor = new Rgba32(128, 128, 128);
                        }

                        // Height shading (darker = lower)
                        float normalizedHeight = (float)(h - minH) / heightRange;
                        // Map 0..1 to 0.5..1.0 brightness
                        float brightness = 0.5f + (normalizedHeight * 0.5f);

                        pixelRow[x] = new Rgba32(
                            (byte)(baseColor.R * brightness),
                            (byte)(baseColor.G * brightness),
                            (byte)(baseColor.B * brightness),
                            255);
                    }
                }
            });

            // Scale up for better visibility if too small
            int targetSize = 128;
            if (width < targetSize || height < targetSize) {
                image.Mutate(x => x.Resize(new ResizeOptions {
                    Size = new Size(targetSize, targetSize),
                    Mode = ResizeMode.NearestNeighbor // Keep pixelated look for terrain grid
                }));
            }

            image.SaveAsPng(outputPath);
        }
    }
}

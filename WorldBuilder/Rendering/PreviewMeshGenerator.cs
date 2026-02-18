using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Rendering {
    /// <summary>
    /// Generates temporary mesh data for preview overlays.
    /// </summary>
    public class PreviewMeshGenerator {
        /// <summary>
        /// Creates preview vertices for a terrain stamp at given position.
        /// </summary>
        public static PreviewMeshData GenerateStampPreview(
            TerrainStamp stamp,
            Vector2 worldPosition,
            float zOffset,
            float[] landHeightTable) {

            int vertexCount = stamp.WidthInVertices * stamp.HeightInVertices;
            var vertices = new PreviewVertex[vertexCount];

            for (int vx = 0; vx < stamp.WidthInVertices; vx++) {
                for (int vy = 0; vy < stamp.HeightInVertices; vy++) {
                    float worldX = worldPosition.X + (vx * 24f);
                    float worldY = worldPosition.Y + (vy * 24f);

                    int stampIndex = vx * stamp.HeightInVertices + vy;
                    byte heightIndex = stamp.Heights[stampIndex];
                    float worldZ = landHeightTable[heightIndex] + zOffset;

                    // Unpack terrain type for texture lookup
                    ushort terrainWord = stamp.TerrainTypes[stampIndex];
                    byte terrainType = (byte)((terrainWord >> 2) & 0x1F);

                    vertices[stampIndex] = new PreviewVertex {
                        Position = new Vector3(worldX, worldY, worldZ),
                        TexCoords = CalculateTexCoords(vx, vy),
                        TextureIndex = terrainType
                    };
                }
            }

            // Generate triangle indices (same pattern as terrain mesh)
            var indices = GenerateTriangleIndices(
                stamp.WidthInVertices,
                stamp.HeightInVertices);

            return new PreviewMeshData {
                Vertices = vertices,
                Indices = indices
            };
        }

        private static Vector2 CalculateTexCoords(int vx, int vy) {
            // Simple tiling coordinates
            return new Vector2(vx * 0.25f, vy * 0.25f);
        }

        private static uint[] GenerateTriangleIndices(int width, int height) {
            int cellWidth = width - 1;
            int cellHeight = height - 1;
            var indices = new List<uint>();

            for (int x = 0; x < cellWidth; x++) {
                for (int y = 0; y < cellHeight; y++) {
                    uint lowerLeft = (uint)(x * height + y);
                    uint lowerRight = (uint)((x + 1) * height + y);
                    uint topLeft = (uint)(x * height + (y + 1));
                    uint topRight = (uint)((x + 1) * height + (y + 1));

                    // Two triangles per cell (SW-NE diagonal)
                    indices.Add(lowerLeft);
                    indices.Add(lowerRight);
                    indices.Add(topLeft);

                    indices.Add(topLeft);
                    indices.Add(lowerRight);
                    indices.Add(topRight);
                }
            }

            return indices.ToArray();
        }
    }

    public struct PreviewVertex {
        public Vector3 Position;
        public Vector2 TexCoords;
        public float TextureIndex; // Using float to match shader expectations
    }

    public class PreviewMeshData {
        public PreviewVertex[] Vertices { get; set; } = Array.Empty<PreviewVertex>();
        public uint[] Indices { get; set; } = Array.Empty<uint>();
    }
}

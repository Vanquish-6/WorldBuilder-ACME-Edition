using Chorizite.Core.Render;
using Chorizite.OpenGLSDLBackend;
using DatReaderWriter;
using System;
using Silk.NET.OpenGL;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Lib;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using System.IO;
using Chorizite.Core.Render.Vertex;

namespace WorldBuilder.Editors.Landscape {
    public class SceneContext : IDisposable {
        public OpenGLRenderer Renderer { get; }
        public TerrainGPUResourceManager GPUManager { get; }
        public StaticObjectManager ObjectManager { get; }
        public EnvCellManager EnvCellManager { get; }
        public IShader TerrainShader { get; }
        public IShader SphereShader { get; }

        public uint SphereVAO { get; set; }
        public uint SphereVBO { get; set; }
        public uint SphereIBO { get; set; }
        public uint SphereInstanceVBO { get; set; }
        public int SphereIndexCount { get; set; }

        // Per-context instance buffer for static objects
        public uint InstanceVBO { get; set; }
        public int InstanceBufferCapacity { get; set; }
        public float[] InstanceUploadBuffer { get; set; } = Array.Empty<float>();

        // Queues for background loading specific to this context
        public ConcurrentQueue<PreparedChunkData> ChunkUploadQueue { get; } = new();
        public ConcurrentQueue<PreparedModelData> ModelUploadQueue { get; } = new();
        public Queue<(uint Id, bool IsSetup)> ModelWarmupQueue { get; } = new();
        public HashSet<uint> ModelsPreparing { get; } = new();
        public HashSet<ulong> ChunksInFlight { get; } = new();

        public SceneContext(OpenGLRenderer renderer, IDatReaderWriter dats, TextureDiskCache textureCache) {
            Renderer = renderer;

            GPUManager = new TerrainGPUResourceManager(renderer);
            ObjectManager = new StaticObjectManager(renderer, dats, textureCache);
            EnvCellManager = new EnvCellManager(renderer, dats, ObjectManager._objectShader, textureCache);

            // Initialize shaders
            var assembly = typeof(OpenGLRenderer).Assembly;
            TerrainShader = renderer.GraphicsDevice.CreateShader("Landscape",
                GameScene.GetEmbeddedResource("Chorizite.OpenGLSDLBackend.Shaders.Landscape.vert", assembly),
                GameScene.GetEmbeddedResource("Chorizite.OpenGLSDLBackend.Shaders.Landscape.frag", assembly));

            SphereShader = renderer.GraphicsDevice.CreateShader("Sphere",
                GameScene.GetEmbeddedResource("WorldBuilder.Shaders.Sphere.vert", typeof(GameScene).Assembly),
                GameScene.GetEmbeddedResource("WorldBuilder.Shaders.Sphere.frag", typeof(GameScene).Assembly));

            InitializeSphereGeometry();
        }

        private unsafe void InitializeSphereGeometry() {
            var gl = Renderer.GraphicsDevice.GL;
            var vertices = CreateSphere(8, 6);
            var indices = CreateSphereIndices(8, 6);
            SphereIndexCount = indices.Length;

            gl.GenVertexArrays(1, out uint vao);
            SphereVAO = vao;
            gl.BindVertexArray(SphereVAO);

            gl.GenBuffers(1, out uint vbo);
            SphereVBO = vbo;
            gl.BindBuffer(GLEnum.ArrayBuffer, SphereVBO);
            fixed (VertexPositionNormal* ptr = vertices) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(vertices.Length * VertexPositionNormal.Size), ptr,
                    GLEnum.StaticDraw);
            }

            int stride = VertexPositionNormal.Size;
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, (uint)stride, (void*)(3 * sizeof(float)));

            gl.GenBuffers(1, out uint instVbo);
            SphereInstanceVBO = instVbo;
            gl.BindBuffer(GLEnum.ArrayBuffer, SphereInstanceVBO);
            gl.BufferData(GLEnum.ArrayBuffer, 0, null, GLEnum.DynamicDraw);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 4, GLEnum.Float, false, (uint)sizeof(Vector4), null);
            gl.VertexAttribDivisor(2, 1);

            gl.GenBuffers(1, out uint ibo);
            SphereIBO = ibo;
            gl.BindBuffer(GLEnum.ElementArrayBuffer, SphereIBO);
            fixed (uint* iptr = indices) {
                gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), iptr,
                    GLEnum.StaticDraw);
            }

            gl.BindVertexArray(0);
        }

        private static VertexPositionNormal[] CreateSphere(int longitudeSegments, int latitudeSegments) {
            var vertices = new List<VertexPositionNormal>();
            for (int lat = 0; lat <= latitudeSegments; lat++) {
                float theta = lat * MathF.PI / latitudeSegments;
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);
                for (int lon = 0; lon <= longitudeSegments; lon++) {
                    float phi = lon * 2 * MathF.PI / longitudeSegments;
                    float sinPhi = MathF.Sin(phi);
                    float cosPhi = MathF.Cos(phi);
                    float x = cosPhi * sinTheta;
                    float y = cosTheta;
                    float z = sinPhi * sinTheta;
                    Vector3 position = new Vector3(x, y, z);
                    Vector3 normal = Vector3.Normalize(position);
                    vertices.Add(new VertexPositionNormal(position, normal));
                }
            }

            return vertices.ToArray();
        }

        private static uint[] CreateSphereIndices(int longitudeSegments, int latitudeSegments) {
            var indices = new List<uint>();
            for (int lat = 0; lat < latitudeSegments; lat++) {
                for (int lon = 0; lon < longitudeSegments; lon++) {
                    uint current = (uint)(lat * (longitudeSegments + 1) + lon);
                    uint next = current + (uint)(longitudeSegments + 1);
                    indices.Add(current);
                    indices.Add(next);
                    indices.Add(current + 1);
                    indices.Add(current + 1);
                    indices.Add(next);
                    indices.Add(next + 1);
                }
            }

            return indices.ToArray();
        }

        public void Dispose() {
            var gl = Renderer.GraphicsDevice.GL;
            gl.DeleteBuffer(SphereVBO);
            gl.DeleteBuffer(SphereIBO);
            gl.DeleteBuffer(SphereInstanceVBO);
            gl.DeleteVertexArray(SphereVAO);
            if (InstanceVBO != 0) gl.DeleteBuffer(InstanceVBO);

            EnvCellManager.Dispose();
            ObjectManager.Dispose();
            GPUManager.Dispose();
        }
    }
}

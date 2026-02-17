using Chorizite.Core.Render;
using Chorizite.Core.Render.Enums;
using Chorizite.OpenGLSDLBackend;
using Chorizite.OpenGLSDLBackend.Lib;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Silk.NET.OpenGL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Lib;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace WorldBuilder.Editors.Landscape {
    public class StaticObjectManager : IDisposable {
        private readonly OpenGLRenderer _renderer;
        private readonly IDatReaderWriter _dats;
        private readonly Dictionary<uint, StaticObjectRenderData> _renderData = new();
        internal readonly IShader _objectShader;
        private readonly ConcurrentDictionary<uint, int> _usageCount = new();
        private readonly HashSet<uint> _failedIds = new();
        private readonly TextureDiskCache? _textureCache;
        private readonly Dictionary<(uint Id, bool IsSetup), (Vector3 Min, Vector3 Max)> _boundsCache = new();

        public StaticObjectManager(OpenGLRenderer renderer, IDatReaderWriter dats, TextureDiskCache? textureCache = null) {
            _renderer = renderer;
            _dats = dats;
            _textureCache = textureCache;

            var assembly = typeof(OpenGLRenderer).Assembly;
            _objectShader = _renderer.GraphicsDevice.CreateShader("StaticObject",
                GameScene.GetEmbeddedResource("Chorizite.OpenGLSDLBackend.Shaders.StaticObject.vert", assembly),
                GameScene.GetEmbeddedResource("Chorizite.OpenGLSDLBackend.Shaders.StaticObject.frag", assembly));
        }

        /// <summary>
        /// Returns true if the given object ID has previously failed to load.
        /// </summary>
        public bool IsKnownFailure(uint id) => _failedIds.Contains(id);

        public StaticObjectRenderData? GetRenderData(uint id, bool isSetup) {
            if (_failedIds.Contains(id)) return null;
            if (_renderData.TryGetValue(id, out var data)) {
                _usageCount.AddOrUpdate(id, 1, (_, count) => count + 1);
                return data;
            }
            data = CreateRenderData(id, isSetup);
            if (data != null) {
                _renderData[id] = data;
                _usageCount[id] = 1;
            }
            return data;
        }

        /// <summary>
        /// Returns cached render data if available, without triggering creation.
        /// Use this during rendering to avoid blocking on DAT reads/GPU uploads.
        /// </summary>
        public StaticObjectRenderData? TryGetCachedRenderData(uint id) {
            if (_renderData.TryGetValue(id, out var data)) {
                _usageCount.AddOrUpdate(id, 1, (_, count) => count + 1);
                return data;
            }
            return null;
        }

        public void ReleaseRenderData(uint id, bool isSetup) {
            if (_usageCount.TryGetValue(id, out var count) && count > 0) {
                var newCount = _usageCount.AddOrUpdate(id, 0, (_, c) => c - 1);
                if (newCount == 0) {
                    UnloadObject(id);
                    _usageCount.TryRemove(id, out _);
                }
            }
        }

        private void UnloadObject(uint key) {
            if (!_renderData.TryGetValue(key, out var data)) return;

            var gl = _renderer.GraphicsDevice.GL;
            if (data.VAO != 0) gl.DeleteVertexArray(data.VAO);
            if (data.VBO != 0) gl.DeleteBuffer(data.VBO);

            foreach (var batch in data.Batches) {
                if (batch.IBO != 0) gl.DeleteBuffer(batch.IBO);
            }

            foreach (var atlasManager in data.LocalAtlases.Values) {
                atlasManager.Dispose();
            }

            _renderData.Remove(key);

            // Invalidate cached bounds for this object
            _boundsCache.Remove((key, true));
            _boundsCache.Remove((key, false));
        }

        private StaticObjectRenderData? CreateRenderData(uint id, bool isSetup) {
            try {
                if (isSetup) {
                    if (!_dats.TryGet<Setup>(id, out var setup)) return null;
                    return CreateSetupRenderData(id, setup);
                }
                else {
                    if (!_dats.TryGet<GfxObj>(id, out var gfxObj)) return null;
                    return CreateGfxObjRenderData(id, gfxObj, Vector3.One);
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"Error creating render data for object 0x{id:X8}: {ex}");
                _failedIds.Add(id);
                return null;
            }
        }

        /// <summary>
        /// Gets the correct placement frame for a Setup, matching ACE's logic:
        /// try Resting (0x65) first, then Default (0), then first available.
        /// ACE uses SetPlacementFrame(0x65) as the standard "resting in world" pose.
        /// </summary>
        private static AnimationFrame? GetDefaultPlacementFrame(Setup setup) {
            if (setup.PlacementFrames.TryGetValue(Placement.Resting, out var resting))
                return resting;
            if (setup.PlacementFrames.TryGetValue(Placement.Default, out var def))
                return def;
            // Last resort: first available entry
            foreach (var kvp in setup.PlacementFrames)
                return kvp.Value;
            return null;
        }

        private StaticObjectRenderData CreateSetupRenderData(uint id, Setup setup) {
            var parts = new List<(uint GfxObjId, Matrix4x4 Transform)>();
            var placementFrame = GetDefaultPlacementFrame(setup);

            for (int i = 0; i < setup.Parts.Count; i++) {
                var partId = setup.Parts[i];
                var transform = Matrix4x4.Identity;

                if (placementFrame?.Frames != null && i < placementFrame.Frames.Count) {
                    transform = Matrix4x4.CreateFromQuaternion(placementFrame.Frames[i].Orientation)
                        * Matrix4x4.CreateTranslation(placementFrame.Frames[i].Origin);
                }

                parts.Add((partId, transform));
            }

            var data = new StaticObjectRenderData {
                IsSetup = true,
                SetupParts = parts,
                Batches = new List<RenderBatch>()
            };

            return data;
        }
        public (Vector3 Min, Vector3 Max)? GetBounds(uint id, bool isSetup) {
            // Check cache first
            var cacheKey = (id, isSetup);
            if (_boundsCache.TryGetValue(cacheKey, out var cached)) {
                return cached;
            }

            try {
                (Vector3 Min, Vector3 Max)? result;
                if (isSetup) {
                    if (!_dats.TryGet<Setup>(id, out var setup)) return null;
                    var parts = new List<(uint GfxObjId, Matrix4x4 Transform)>();
                    var placementFrame = GetDefaultPlacementFrame(setup);
                    for (int i = 0; i < setup.Parts.Count; i++) {
                        var partId = setup.Parts[i];
                        var transform = Matrix4x4.Identity;
                        if (placementFrame?.Frames != null && i < placementFrame.Frames.Count) {
                            transform = Matrix4x4.CreateFromQuaternion(placementFrame.Frames[i].Orientation) *
                                        Matrix4x4.CreateTranslation(placementFrame.Frames[i].Origin);
                        }
                        parts.Add((partId, transform));
                    }
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    bool hasBounds = false;
                    foreach (var (partId, transform) in parts) {
                        if (_dats.TryGet<GfxObj>(partId, out var partGfx)) {
                            var (partMin, partMax) = ComputeBounds(partGfx, Vector3.One);
                            // Approximate transformed AABB (same as original; for exact, transform 8 corners)
                            var transMin = Vector3.Transform(partMin, transform);
                            var transMax = Vector3.Transform(partMax, transform);
                            min = Vector3.Min(min, transMin);
                            max = Vector3.Max(max, transMax);
                            hasBounds = true;
                        }
                    }
                    result = hasBounds ? (min, max) : null;
                }
                else {
                    if (!_dats.TryGet<GfxObj>(id, out var gfxObj)) return null;
                    result = ComputeBounds(gfxObj, Vector3.One);
                }

                // Cache the result
                if (result.HasValue) {
                    _boundsCache[cacheKey] = result.Value;
                }
                return result;
            }
            catch (Exception ex) {
                Console.WriteLine($"Error computing bounds for 0x{id:X8}: {ex}");
                return null;
            }
        }

        private unsafe StaticObjectRenderData CreateGfxObjRenderData(uint id, GfxObj gfxObj, Vector3 scale) {
            var vertices = new List<VertexPositionNormalTexture>();
            var UVLookup = new Dictionary<(ushort vertId, ushort uvIdx), ushort>();
            var batchesByFormat = new Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatch>>();
            var localAtlases = new Dictionary<(int Width, int Height, TextureFormat Format), TextureAtlasManager>();

            foreach (var poly in gfxObj.Polygons.Values) {
                if (poly.VertexIds.Count < 3) continue;

                int surfaceIdx = poly.PosSurface;
                bool useNegSurface = false;

                // Determine which surface to use
                if (poly.Stippling == StipplingType.NoPos) {
                    if (poly.PosSurface < gfxObj.Surfaces.Count) {
                        surfaceIdx = poly.PosSurface;
                    }
                    else if (poly.NegSurface < gfxObj.Surfaces.Count) {
                        surfaceIdx = poly.NegSurface;
                        useNegSurface = true;
                    }
                    else {
                        continue;
                    }
                }
                else if (surfaceIdx >= gfxObj.Surfaces.Count) {
                    continue;
                }

                var surfaceId = gfxObj.Surfaces[surfaceIdx];
                if (!_dats.TryGet<Surface>(surfaceId, out var surface)) continue;

                bool isSolid = poly.Stippling == StipplingType.NoPos || surface.Type.HasFlag(SurfaceType.Base1Solid);
                var texResult = LoadTextureData(surfaceId, surface, isSolid, poly.Stippling);
                if (!texResult.HasValue) continue;

                var (textureData, texWidth, texHeight, textureFormat, uploadPixelFormat, uploadPixelType, paletteId) = texResult.Value;
                var format = (texWidth, texHeight, textureFormat);

                if (!localAtlases.TryGetValue(format, out var atlasManager)) {
                    atlasManager = new TextureAtlasManager(_renderer, texWidth, texHeight, textureFormat);
                    localAtlases[format] = atlasManager;
                }

                var key = new TextureAtlasManager.TextureKey {
                    SurfaceId = surfaceId,
                    PaletteId = paletteId,
                    Stippling = poly.Stippling,
                    IsSolid = isSolid
                };

                int textureIndex = atlasManager.AddTexture(key, textureData, uploadPixelFormat, uploadPixelType);

                if (!batchesByFormat.TryGetValue(format, out var batches)) {
                    batches = new List<TextureBatch>();
                    batchesByFormat[format] = batches;
                }

                // Heuristic: non-solid surfaces (textured with potential alpha) need double-sided rendering
                bool isDoubleSided = !isSolid;

                var batch = batches.FirstOrDefault(b => b.TextureIndex == textureIndex);
                if (batch == null) {
                    batch = new TextureBatch { TextureIndex = textureIndex, SurfaceId = surfaceId, Key = key, IsDoubleSided = isDoubleSided };
                    batches.Add(batch);
                }
                else if (isDoubleSided) {
                    batch.IsDoubleSided = true; // If any polygon in this batch needs double-sided, the whole batch does
                }

                BuildPolygonIndices(poly, gfxObj, scale, UVLookup, vertices, batch, useNegSurface);
            }

            var renderData = SetupGpuBuffers(vertices, batchesByFormat, id, localAtlases);
            renderData.LocalAtlases = localAtlases;
            return renderData;
        }

        private void BuildPolygonIndices(Polygon poly, GfxObj gfxObj, Vector3 scale,
            Dictionary<(ushort vertId, ushort uvIdx), ushort> UVLookup,
            List<VertexPositionNormalTexture> vertices, TextureBatch batch, bool useNegSurface) {

            var polyIndices = new List<ushort>();

            for (int i = 0; i < poly.VertexIds.Count; i++) {
                ushort vertId = (ushort)poly.VertexIds[i];
                ushort uvIdx = 0;

                if (useNegSurface && poly.NegUVIndices != null && i < poly.NegUVIndices.Count) {
                    uvIdx = poly.NegUVIndices[i];
                }
                else if (!useNegSurface && poly.PosUVIndices != null && i < poly.PosUVIndices.Count) {
                    uvIdx = poly.PosUVIndices[i];
                }

                if (vertId >= gfxObj.VertexArray.Vertices.Count) continue;
                if (uvIdx >= gfxObj.VertexArray.Vertices[vertId].UVs.Count) {
                    // This warning is common for some DAT models (empty UVs), suppress to avoid spam
                    // Console.WriteLine($"Warning: UV index {uvIdx} out of range for vertex {vertId}. Using 0.");
                    uvIdx = 0;
                }

                var vertex = gfxObj.VertexArray.Vertices[vertId];

                var key = (vertId, uvIdx);
                if (!UVLookup.TryGetValue(key, out var idx)) {
                    var uv = vertex.UVs.Count > 0
                        ? new Vector2(vertex.UVs[uvIdx].U, vertex.UVs[uvIdx].V)
                        : Vector2.Zero;

                    idx = (ushort)vertices.Count;
                    vertices.Add(new VertexPositionNormalTexture(
                        vertex.Origin * scale,
                        Vector3.Normalize(vertex.Normal),
                        uv
                    ));
                    UVLookup[key] = idx;
                }
                polyIndices.Add(idx);
            }

            for (int i = 2; i < polyIndices.Count; i++) {
                batch.Indices.Add(polyIndices[i]);
                batch.Indices.Add(polyIndices[i - 1]);
                batch.Indices.Add(polyIndices[0]);
            }
        }

        private unsafe StaticObjectRenderData SetupGpuBuffers(
            List<VertexPositionNormalTexture> vertices,
            Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatch>> batchesByFormat,
            uint id, Dictionary<(int Width, int Height, TextureFormat Format), TextureAtlasManager> localAtlases) {

            var gl = _renderer.GraphicsDevice.GL;
            gl.GenVertexArrays(1, out uint vao);
            gl.BindVertexArray(vao);

            gl.GenBuffers(1, out uint vbo);
            gl.BindBuffer(GLEnum.ArrayBuffer, vbo);
            fixed (VertexPositionNormalTexture* ptr = vertices.ToArray()) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(vertices.Count * VertexPositionNormalTexture.Size), ptr, GLEnum.StaticDraw);
            }

            int stride = VertexPositionNormalTexture.Size;
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, GLEnum.Float, false, (uint)stride, (void*)(6 * sizeof(float)));

            var renderBatches = new List<RenderBatch>();

            foreach (var (format, batches) in batchesByFormat) {
                var atlasManager = localAtlases[format];

                foreach (var batch in batches) {
                    if (batch.Indices.Count == 0) continue;

                    gl.GenBuffers(1, out uint ibo);
                    gl.BindBuffer(GLEnum.ElementArrayBuffer, ibo);
                    fixed (ushort* iptr = batch.Indices.ToArray()) {
                        gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(batch.Indices.Count * sizeof(ushort)), iptr, GLEnum.StaticDraw);
                    }

                    renderBatches.Add(new RenderBatch {
                        IBO = ibo,
                        IndexCount = batch.Indices.Count,
                        TextureArray = atlasManager.TextureArray,
                        TextureIndex = batch.TextureIndex,
                        TextureSize = (format.Width, format.Height),
                        TextureFormat = format.Format,
                        SurfaceId = batch.SurfaceId,
                        Key = batch.Key,
                        IsDoubleSided = batch.IsDoubleSided
                    });
                }
            }

            var renderData = new StaticObjectRenderData {
                VAO = vao,
                VBO = vbo,
                Batches = renderBatches
            };

            gl.BindVertexArray(0);

            return renderData;
        }

        /// <summary>
        /// CPU-only model preparation: reads DAT files, decompresses textures, builds vertex/index buffers.
        /// Safe to call from a background thread. Returns a PreparedModelData that must be finalized
        /// on the GL thread via FinalizeGpuUpload.
        /// </summary>
        public PreparedModelData? PrepareModelData(uint id, bool isSetup) {
            if (_failedIds.Contains(id)) return null;

            try {
                if (isSetup) {
                    if (!_dats.TryGet<Setup>(id, out var setup)) return null;
                    var parts = new List<(uint GfxObjId, Matrix4x4 Transform)>();
                    var placementFrame = GetDefaultPlacementFrame(setup);
                    for (int i = 0; i < setup.Parts.Count; i++) {
                        var partId = setup.Parts[i];
                        var transform = Matrix4x4.Identity;
                        if (placementFrame?.Frames != null && i < placementFrame.Frames.Count) {
                            transform = Matrix4x4.CreateFromQuaternion(placementFrame.Frames[i].Orientation)
                                * Matrix4x4.CreateTranslation(placementFrame.Frames[i].Origin);
                        }
                        parts.Add((partId, transform));
                    }
                    return new PreparedModelData { Id = id, IsSetup = true, SetupParts = parts };
                }
                else {
                    if (!_dats.TryGet<GfxObj>(id, out var gfxObj)) return null;
                    return PrepareGfxObjData(id, gfxObj, Vector3.One);
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[Statics] Error preparing model data for 0x{id:X8}: {ex.Message}");
                _failedIds.Add(id);
                return null;
            }
        }

        /// <summary>
        /// CPU-only GfxObj preparation. Reads all DAT resources, decompresses textures,
        /// and builds vertex/index arrays -- no GL calls.
        /// </summary>
        private PreparedModelData PrepareGfxObjData(uint id, GfxObj gfxObj, Vector3 scale) {
            var vertices = new List<VertexPositionNormalTexture>();
            var UVLookup = new Dictionary<(ushort vertId, ushort uvIdx), ushort>();

            // Track textures per format, indexed by key
            var texturesByFormat = new Dictionary<(int Width, int Height, TextureFormat Format), List<PreparedTexture>>();
            var textureIndexByKey = new Dictionary<(int Width, int Height, TextureFormat Format, TextureAtlasManager.TextureKey Key), int>();
            var batchList = new List<(TextureBatch batch, (int, int, TextureFormat) format)>();

            foreach (var poly in gfxObj.Polygons.Values) {
                if (poly.VertexIds.Count < 3) continue;

                int surfaceIdx = poly.PosSurface;
                bool useNegSurface = false;

                if (poly.Stippling == StipplingType.NoPos) {
                    if (poly.PosSurface < gfxObj.Surfaces.Count) {
                        surfaceIdx = poly.PosSurface;
                    }
                    else if (poly.NegSurface < gfxObj.Surfaces.Count) {
                        surfaceIdx = poly.NegSurface;
                        useNegSurface = true;
                    }
                    else continue;
                }
                else if (surfaceIdx >= gfxObj.Surfaces.Count) continue;

                var surfaceId = gfxObj.Surfaces[surfaceIdx];
                if (!_dats.TryGet<Surface>(surfaceId, out var surface)) continue;

                bool isSolid = poly.Stippling == StipplingType.NoPos || surface.Type.HasFlag(SurfaceType.Base1Solid);
                var texResult = LoadTextureData(surfaceId, surface, isSolid, poly.Stippling);
                if (!texResult.HasValue) continue;

                var (textureData, texWidth, texHeight, textureFormat, uploadPixelFormat, uploadPixelType, paletteId) = texResult.Value;
                var format = (texWidth, texHeight, textureFormat);
                var texKey = new TextureAtlasManager.TextureKey {
                    SurfaceId = surfaceId,
                    PaletteId = paletteId,
                    Stippling = poly.Stippling,
                    IsSolid = isSolid
                };

                // Track unique textures per format
                var texLookupKey = (texWidth, texHeight, textureFormat, texKey);
                if (!textureIndexByKey.TryGetValue(texLookupKey, out var textureIndex)) {
                    if (!texturesByFormat.TryGetValue(format, out var texList)) {
                        texList = new List<PreparedTexture>();
                        texturesByFormat[format] = texList;
                    }
                    textureIndex = texList.Count;
                    texList.Add(new PreparedTexture {
                        Key = texKey,
                        Data = textureData,
                        Width = texWidth,
                        Height = texHeight,
                        Format = textureFormat,
                        UploadPixelFormat = uploadPixelFormat,
                        UploadPixelType = uploadPixelType
                    });
                    textureIndexByKey[texLookupKey] = textureIndex;
                }

                // Heuristic: non-solid surfaces (textured with potential alpha) need double-sided rendering
                bool isDoubleSided = !isSolid;

                // Find or create batch
                var existingBatch = batchList.FirstOrDefault(b =>
                    b.format == format && b.batch.TextureIndex == textureIndex);
                TextureBatch batch;
                if (existingBatch.batch != null) {
                    batch = existingBatch.batch;
                    if (isDoubleSided) batch.IsDoubleSided = true;
                }
                else {
                    batch = new TextureBatch { TextureIndex = textureIndex, SurfaceId = surfaceId, Key = texKey, IsDoubleSided = isDoubleSided };
                    batchList.Add((batch, format));
                }

                BuildPolygonIndices(poly, gfxObj, scale, UVLookup, vertices, batch, useNegSurface);
            }

            // Convert to prepared batches
            var preparedBatches = batchList.Select(b => new PreparedBatch {
                Indices = b.batch.Indices.ToArray(),
                Format = b.format,
                SurfaceId = b.batch.SurfaceId,
                Key = b.batch.Key,
                TextureIndex = b.batch.TextureIndex,
                IsDoubleSided = b.batch.IsDoubleSided
            }).ToList();

            return new PreparedModelData {
                Id = id,
                IsSetup = false,
                Vertices = vertices.ToArray(),
                Batches = preparedBatches,
                TexturesByFormat = texturesByFormat
            };
        }

        /// <summary>
        /// Finalizes a prepared model by uploading vertex/index/texture data to the GPU.
        /// Must be called on the GL thread. Returns the completed StaticObjectRenderData.
        /// </summary>
        public unsafe StaticObjectRenderData? FinalizeGpuUpload(PreparedModelData prepared) {
            if (prepared.IsSetup) {
                var data = new StaticObjectRenderData {
                    IsSetup = true,
                    SetupParts = prepared.SetupParts ?? new(),
                    Batches = new List<RenderBatch>()
                };
                _renderData[prepared.Id] = data;
                _usageCount[prepared.Id] = 1;
                return data;
            }

            if (prepared.Vertices.Length == 0) return null;

            var gl = _renderer.GraphicsDevice.GL;

            // Create atlas managers and upload textures
            var localAtlases = new Dictionary<(int Width, int Height, TextureFormat Format), TextureAtlasManager>();
            var atlasTextureIndices = new Dictionary<(int Width, int Height, TextureFormat Format, int PreparedIndex), int>();

            foreach (var (format, textures) in prepared.TexturesByFormat) {
                var atlasManager = new TextureAtlasManager(_renderer, format.Width, format.Height, format.Format);
                localAtlases[format] = atlasManager;

                for (int i = 0; i < textures.Count; i++) {
                    var tex = textures[i];
                    int atlasIdx = atlasManager.AddTexture(tex.Key, tex.Data, tex.UploadPixelFormat, tex.UploadPixelType);
                    atlasTextureIndices[(format.Width, format.Height, format.Format, i)] = atlasIdx;
                }
            }

            // Upload vertices
            gl.GenVertexArrays(1, out uint vao);
            gl.BindVertexArray(vao);

            gl.GenBuffers(1, out uint vbo);
            gl.BindBuffer(GLEnum.ArrayBuffer, vbo);
            fixed (VertexPositionNormalTexture* ptr = prepared.Vertices) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(prepared.Vertices.Length * VertexPositionNormalTexture.Size), ptr, GLEnum.StaticDraw);
            }

            int stride = VertexPositionNormalTexture.Size;
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, GLEnum.Float, false, (uint)stride, (void*)(6 * sizeof(float)));

            // Upload index buffers and create render batches
            var renderBatches = new List<RenderBatch>();
            foreach (var batch in prepared.Batches) {
                if (batch.Indices.Length == 0) continue;

                var atlasManager = localAtlases[batch.Format];
                var atlasKey = (batch.Format.Width, batch.Format.Height, batch.Format.Format, batch.TextureIndex);
                int atlasIdx = atlasTextureIndices.TryGetValue(atlasKey, out var idx) ? idx : batch.TextureIndex;

                gl.GenBuffers(1, out uint ibo);
                gl.BindBuffer(GLEnum.ElementArrayBuffer, ibo);
                fixed (ushort* iptr = batch.Indices) {
                    gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(batch.Indices.Length * sizeof(ushort)), iptr, GLEnum.StaticDraw);
                }

                renderBatches.Add(new RenderBatch {
                    IBO = ibo,
                    IndexCount = batch.Indices.Length,
                    TextureArray = atlasManager.TextureArray,
                    TextureIndex = atlasIdx,
                    TextureSize = (batch.Format.Width, batch.Format.Height),
                    TextureFormat = batch.Format.Format,
                    SurfaceId = batch.SurfaceId,
                    Key = batch.Key,
                    IsDoubleSided = batch.IsDoubleSided
                });
            }

            gl.BindVertexArray(0);

            var renderData = new StaticObjectRenderData {
                VAO = vao,
                VBO = vbo,
                Batches = renderBatches,
                LocalAtlases = localAtlases
            };

            _renderData[prepared.Id] = renderData;
            _usageCount[prepared.Id] = 1;
            return renderData;
        }

        /// <summary>
        /// Loads and processes texture data for a surface, using the disk cache when available.
        /// Returns the processed RGBA/compressed texture bytes, or null if not loadable.
        /// </summary>
        private (byte[] data, int width, int height, TextureFormat format, PixelFormat? uploadFormat, PixelType? uploadType, uint paletteId)?
            LoadTextureData(uint surfaceId, Surface surface, bool isSolid, StipplingType stippling) {

            PixelFormat? uploadPixelFormat = null;
            PixelType? uploadPixelType = null;
            uint paletteId = 0;

            if (isSolid) {
                int texWidth = 32, texHeight = 32;
                var solidData = TextureHelpers.CreateSolidColorTexture(surface.ColorValue, texWidth, texHeight);
                return (solidData, texWidth, texHeight, TextureFormat.RGBA8, PixelFormat.Rgba, null, 0);
            }

            if (!_dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfaceTexture) ||
                surfaceTexture.Textures?.Any() != true) {
                return null;
            }

            var renderSurfaceId = surfaceTexture.Textures.Last();
            if (!_dats.TryGet<RenderSurface>(renderSurfaceId, out var renderSurface)) return null;

            int w = renderSurface.Width, h = renderSurface.Height;
            paletteId = renderSurface.DefaultPaletteId;

            if (TextureHelpers.IsCompressedFormat(renderSurface.Format)) {
                var fmt = renderSurface.Format switch {
                    DatReaderWriter.Enums.PixelFormat.PFID_DXT1 => TextureFormat.DXT1,
                    DatReaderWriter.Enums.PixelFormat.PFID_DXT3 => TextureFormat.DXT3,
                    DatReaderWriter.Enums.PixelFormat.PFID_DXT5 => TextureFormat.DXT5,
                    _ => throw new NotSupportedException($"Unsupported compressed format: {renderSurface.Format}")
                };
                return (renderSurface.SourceData, w, h, fmt, null, null, paletteId);
            }

            TextureFormat textureFormat = TextureFormat.RGBA8;
            byte[] textureData;

            switch (renderSurface.Format) {
                case DatReaderWriter.Enums.PixelFormat.PFID_A8R8G8B8:
                    uploadPixelFormat = PixelFormat.Rgba;
                    textureData = renderSurface.SourceData;
                    break;
                case DatReaderWriter.Enums.PixelFormat.PFID_R8G8B8:
                    uploadPixelFormat = PixelFormat.Rgb;
                    textureFormat = TextureFormat.RGB8;
                    textureData = renderSurface.SourceData;
                    break;
                case DatReaderWriter.Enums.PixelFormat.PFID_INDEX16: {
                    // Check disk cache first
                    var cached = _textureCache?.TryGet(surfaceId, paletteId);
                    if (cached != null) {
                        uploadPixelFormat = PixelFormat.Rgba;
                        return (cached, w, h, TextureFormat.RGBA8, uploadPixelFormat, null, paletteId);
                    }

                    if (!_dats.TryGet<Palette>(renderSurface.DefaultPaletteId, out var paletteData))
                        throw new Exception($"Unable to load Palette: 0x{renderSurface.DefaultPaletteId:X8}");
                    textureData = new byte[w * h * 4];
                    TextureHelpers.FillIndex16(renderSurface.SourceData, paletteData, textureData.AsSpan(), w, h);
                    uploadPixelFormat = PixelFormat.Rgba;

                    // Store in cache for future sessions
                    _textureCache?.Store(surfaceId, paletteId, textureData);
                    break;
                }
                default:
                    throw new NotSupportedException($"Unsupported surface format: {renderSurface.Format}");
            }

            return (textureData, w, h, textureFormat, uploadPixelFormat, uploadPixelType, paletteId);
        }

        private (Vector3 Min, Vector3 Max) ComputeBounds(GfxObj gfxObj, Vector3 scale) {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var vert in gfxObj.VertexArray.Vertices.Values) {
                var p = vert.Origin * scale;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (min, max);
        }

        public void Dispose() {
            var gl = _renderer.GraphicsDevice.GL;
            foreach (var data in _renderData.Values) {
                if (data.VAO != 0) gl.DeleteVertexArray(data.VAO);
                if (data.VBO != 0) gl.DeleteBuffer(data.VBO);
                foreach (var batch in data.Batches) {
                    if (batch.IBO != 0) gl.DeleteBuffer(batch.IBO);
                }
                foreach (var atlas in data.LocalAtlases.Values) {
                    atlas.Dispose();
                }
            }
            _renderData.Clear();
        }
    }

    internal class TextureBatch {
        public int TextureIndex { get; set; }
        public uint SurfaceId { get; set; }
        public TextureAtlasManager.TextureKey Key { get; set; }
        public List<ushort> Indices { get; set; } = new();
        public bool IsDoubleSided { get; set; }
    }

    public class RenderBatch {
        public uint IBO { get; set; }
        public int IndexCount { get; set; }
        public ITextureArray TextureArray { get; set; }
        public int TextureIndex { get; set; }
        public (int Width, int Height) TextureSize { get; set; }
        public TextureFormat TextureFormat { get; set; }
        public uint SurfaceId { get; set; }
        public TextureAtlasManager.TextureKey Key { get; set; }
        /// <summary>
        /// When true, backface culling is disabled for this batch (e.g. foliage, alpha-tested geometry).
        /// </summary>
        public bool IsDoubleSided { get; set; }
    }

    public class StaticObjectRenderData {
        public uint VAO { get; set; }
        public uint VBO { get; set; }
        public List<RenderBatch> Batches { get; set; } = new();
        public bool IsSetup { get; set; }
        public List<(uint GfxObjId, Matrix4x4 Transform)> SetupParts { get; set; } = new();
        public Dictionary<(int Width, int Height, TextureFormat Format), TextureAtlasManager> LocalAtlases { get; set; } = new();
    }

    /// <summary>
    /// Intermediate result of CPU-side model preparation (DAT reads, texture decompression, vertex building).
    /// Safe to create on a background thread. Must be finalized on the GL thread via FinalizeGpuUpload.
    /// </summary>
    public class PreparedModelData {
        public uint Id { get; set; }
        public bool IsSetup { get; set; }
        public List<(uint GfxObjId, Matrix4x4 Transform)>? SetupParts { get; set; }

        /// <summary>Vertex data ready for GPU upload</summary>
        public VertexPositionNormalTexture[] Vertices { get; set; } = Array.Empty<VertexPositionNormalTexture>();

        /// <summary>Per-format batches with indices and texture data ready for GPU upload</summary>
        public List<PreparedBatch> Batches { get; set; } = new();

        /// <summary>Per-format texture atlas data prepared on the CPU side</summary>
        public Dictionary<(int Width, int Height, TextureFormat Format), List<PreparedTexture>> TexturesByFormat { get; set; } = new();
    }

    /// <summary>
    /// A prepared rendering batch with CPU-side index data, ready for GPU upload.
    /// </summary>
    public class PreparedBatch {
        public ushort[] Indices { get; set; } = Array.Empty<ushort>();
        public (int Width, int Height, TextureFormat Format) Format { get; set; }
        public uint SurfaceId { get; set; }
        public TextureAtlasManager.TextureKey Key { get; set; }
        /// <summary>Index into the texture list for this format</summary>
        public int TextureIndex { get; set; }
        public bool IsDoubleSided { get; set; }
    }

    /// <summary>
    /// Pre-processed texture data ready for GPU upload.
    /// </summary>
    public class PreparedTexture {
        public TextureAtlasManager.TextureKey Key { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public TextureFormat Format { get; set; }
        public PixelFormat? UploadPixelFormat { get; set; }
        public PixelType? UploadPixelType { get; set; }
    }
}
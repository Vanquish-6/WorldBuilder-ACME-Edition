using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Renders small thumbnail images of DAT objects using an offscreen framebuffer.
    /// Uses the main scene's StaticObjectManager and shader for rendering.
    /// </summary>
    public class ThumbnailRenderService : IDisposable {
        public const int ThumbnailSize = 96;
        private const float TimeBudgetMs = 8f; // Slightly increased budget

        private readonly GL _gl;
        private readonly StaticObjectManager _objectManager;

        // Offscreen FBO resources
        private uint _fbo;
        private uint _colorTexture;
        private uint _depthRenderbuffer;
        private bool _fboInitialized;

        // Request queue: (objectId, isSetup, frameCount)
        private readonly Queue<(uint Id, bool IsSetup, int FrameCount)> _queue = new();
        private readonly HashSet<(uint Id, int FrameCount)> _queued = new();
        private readonly HashSet<uint> _failedIds = new();

        public event Action<uint, byte[], int>? ThumbnailReady;

        private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.5f, 0.3f, -0.3f));
        private const float AmbientIntensity = 0.6f;
        private const float SpecularPower = 32f;

        public ThumbnailRenderService(GL gl, StaticObjectManager objectManager) {
            _gl = gl;
            _objectManager = objectManager;
        }

        public void RequestThumbnail(uint id, bool isSetup, int frameCount = 1) {
            if (_failedIds.Contains(id)) return;
            lock (_queue) {
                if (_queued.Add((id, frameCount))) {
                    _queue.Enqueue((id, isSetup, frameCount));
                }
            }
        }

        public unsafe void ProcessQueue() {
            if (_queue.Count == 0) return;

            EnsureFBO();

            var sw = Stopwatch.StartNew();
            int rendered = 0;

            while (true) {
                (uint id, bool isSetup, int frameCount) request = default;
                lock (_queue) {
                    if (_queue.Count == 0) break;
                    request = _queue.Dequeue();
                }

                if (rendered > 0 && sw.ElapsedMilliseconds >= TimeBudgetMs) {
                    // Re-enqueue if we ran out of time
                    lock (_queue) {
                        if (_queued.Add((request.id, request.frameCount))) {
                            // Put back at front? Queue doesn't support that, so just enqueue.
                            // Ideally we'd use a Deque, but this is rare.
                            _queue.Enqueue(request);
                        }
                    }
                    break;
                }

                try {
                    // Synchronous render - simple and robust
                    byte[]? pixels = RenderThumbnail(request.id, request.isSetup);

                    if (pixels != null) {
                        ThumbnailReady?.Invoke(request.id, pixels, request.frameCount);
                        rendered++;
                    }
                    else {
                        _failedIds.Add(request.id);
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"[ThumbnailRender] Error rendering 0x{request.id:X8}: {ex}");
                    _failedIds.Add(request.id);
                }

                lock (_queue) {
                    _queued.Remove((request.id, request.frameCount));
                }
            }
        }


        private unsafe byte[]? RenderThumbnail(uint id, bool isSetup) {
            var renderData = _objectManager.GetRenderData(id, isSetup);
            if (renderData == null) return null;

            var bounds = _objectManager.GetBounds(id, isSetup);
            if (bounds == null) {
                _objectManager.ReleaseRenderData(id, isSetup);
                return null;
            }

            _gl.GetInteger(GLEnum.FramebufferBinding, out int prevFbo);
            int[] prevViewport = new int[4];
            fixed (int* vp = prevViewport) _gl.GetInteger(GLEnum.Viewport, vp);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.Viewport(0, 0, ThumbnailSize, ThumbnailSize);

            SetupCommonState();
            SetupCamera(bounds.Value);

            _gl.ClearColor(0.18f, 0.18f, 0.22f, 1.0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            RenderObjectAtAngle(renderData, bounds.Value, 0f);

            byte[] pixels = new byte[ThumbnailSize * ThumbnailSize * 4];
            fixed (byte* ptr = pixels) {
                _gl.ReadPixels(0, 0, ThumbnailSize, ThumbnailSize, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
            }

            FlipVertically(pixels, ThumbnailSize, ThumbnailSize);

            _gl.BindVertexArray(0);
            _gl.UseProgram(0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo);
            fixed (int* vp = prevViewport) _gl.Viewport(vp[0], vp[1], (uint)vp[2], (uint)vp[3]);

            _objectManager.ReleaseRenderData(id, isSetup);
            if (isSetup && renderData.IsSetup) {
                foreach (var (partId, _) in renderData.SetupParts) {
                    _objectManager.ReleaseRenderData(partId, false);
                }
            }

            return pixels;
        }

        private void SetupCommonState() {
            _gl.Disable(EnableCap.StencilTest);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);
        }

        private void SetupCamera((Vector3 Min, Vector3 Max) bounds) {
            var (boundsMin, boundsMax) = bounds;
            var center = (boundsMin + boundsMax) * 0.5f;
            var radius = (boundsMax - boundsMin).Length() * 0.5f;

            float distance = radius * 2.8f;
            float elevation = MathF.PI / 6f;
            float azimuth = MathF.PI / 4f;
            var cameraOffset = new Vector3(
                MathF.Cos(elevation) * MathF.Sin(azimuth),
                MathF.Cos(elevation) * MathF.Cos(azimuth),
                MathF.Sin(elevation)
            );
            var cameraPos = center + cameraOffset * distance;

            var view = Matrix4x4.CreateLookAt(cameraPos, center, Vector3.UnitZ);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 1.0f, distance * 0.01f, distance * 10f);
            var viewProjection = view * projection;

            _objectManager._objectShader.Bind();
            _objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            _objectManager._objectShader.SetUniform("uCameraPosition", cameraPos);
            _objectManager._objectShader.SetUniform("uLightDirection", LightDirection);
            _objectManager._objectShader.SetUniform("uAmbientIntensity", AmbientIntensity);
            _objectManager._objectShader.SetUniform("uSpecularPower", SpecularPower);
        }

        private unsafe void RenderObjectAtAngle(StaticObjectRenderData renderData, (Vector3 Min, Vector3 Max) bounds, float angle) {
            var center = (bounds.Min + bounds.Max) * 0.5f;
            var rotationMatrix = Matrix4x4.CreateRotationZ(angle);
            var objectRotation = Matrix4x4.CreateTranslation(-center) * rotationMatrix * Matrix4x4.CreateTranslation(center);

            if (renderData.IsSetup) {
                foreach (var (partId, partTransform) in renderData.SetupParts) {
                    var partRenderData = _objectManager.GetRenderData(partId, false);
                    if (partRenderData == null) continue;
                    var rotatedTransform = partTransform * objectRotation;
                    RenderSingleObject(partRenderData, rotatedTransform);
                }
            }
            else {
                RenderSingleObject(renderData, objectRotation);
            }
        }

        private unsafe void RenderSingleObject(StaticObjectRenderData renderData, Matrix4x4 transform) {
            if (renderData.Batches.Count == 0) return;

            _gl.BindVertexArray(renderData.VAO);

            for (int i = 0; i < 4; i++) _gl.DisableVertexAttribArray((uint)(3 + i));
            _gl.DisableVertexAttribArray(7);

            _gl.VertexAttrib4(3, transform.M11, transform.M12, transform.M13, transform.M14);
            _gl.VertexAttrib4(4, transform.M21, transform.M22, transform.M23, transform.M24);
            _gl.VertexAttrib4(5, transform.M31, transform.M32, transform.M33, transform.M34);
            _gl.VertexAttrib4(6, transform.M41, transform.M42, transform.M43, transform.M44);

            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;
                batch.TextureArray.Bind(0);
                _objectManager._objectShader.SetUniform("uTextureArray", 0);
                _gl.VertexAttrib1(7, (float)batch.TextureIndex);
                _gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                _gl.DrawElements(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null);
            }
            _gl.BindVertexArray(0);
        }

        private unsafe void EnsureFBO() {
            if (_fboInitialized) return;
            _colorTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, ThumbnailSize, ThumbnailSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

            _depthRenderbuffer = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRenderbuffer);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, ThumbnailSize, ThumbnailSize);

            _fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colorTexture, 0);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthRenderbuffer);
            _fboInitialized = true;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private static void FlipVertically(byte[] pixels, int width, int height) {
            int rowBytes = width * 4;
            var temp = new byte[rowBytes];
            for (int y = 0; y < height / 2; y++) {
                int topOffset = y * rowBytes;
                int bottomOffset = (height - 1 - y) * rowBytes;
                System.Buffer.BlockCopy(pixels, topOffset, temp, 0, rowBytes);
                System.Buffer.BlockCopy(pixels, bottomOffset, pixels, topOffset, rowBytes);
                System.Buffer.BlockCopy(temp, 0, pixels, bottomOffset, rowBytes);
            }
        }


        public void Dispose() {
            if (_fboInitialized) {
                _gl.DeleteFramebuffer(_fbo);
                _gl.DeleteTexture(_colorTexture);
                _gl.DeleteRenderbuffer(_depthRenderbuffer);
                _fboInitialized = false;
            }
        }
    }
}

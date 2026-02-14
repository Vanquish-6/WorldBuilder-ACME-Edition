using Silk.NET.OpenGL;
using Chorizite.OpenGLSDLBackend;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Renders small thumbnail images of DAT objects using an offscreen framebuffer.
    /// Uses the main scene's StaticObjectManager and shader for rendering,
    /// ensuring the exact same GL state and code path that works for the 3D viewport.
    /// ProcessQueue() must be called at the end of the Render() pass (not during Update)
    /// because Avalonia's UI renderer leaves GL state in an unknown configuration between frames.
    /// </summary>
    public class ThumbnailRenderService : IDisposable {
        public const int ThumbnailSize = 96;
        private const float TimeBudgetMs = 4f;

        private readonly OpenGLRenderer _renderer;
        private GL _gl => _renderer.GraphicsDevice.GL;
        private readonly StaticObjectManager _objectManager;

        // Offscreen FBO resources
        private uint _fbo;
        private uint _colorTexture;
        private uint _depthRenderbuffer;
        private bool _fboInitialized;

        // Request queue: (objectId, isSetup)
        private readonly Queue<(uint Id, bool IsSetup)> _queue = new();
        private readonly HashSet<uint> _queued = new(); // Avoid duplicate entries
        private readonly HashSet<uint> _failedIds = new(); // Never retry objects that failed

        /// <summary>
        /// Fired on the GL thread when a thumbnail has been rendered.
        /// Parameters: objectId, RGBA pixel data (ThumbnailSize x ThumbnailSize).
        /// </summary>
        public event Action<uint, byte[]>? ThumbnailReady;

        // Camera setup for thumbnail rendering
        private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.5f, 0.3f, -0.3f));
        private const float AmbientIntensity = 0.6f;
        private const float SpecularPower = 32f;

        public ThumbnailRenderService(OpenGLRenderer renderer, StaticObjectManager objectManager) {
            _renderer = renderer;
            _objectManager = objectManager;
        }

        /// <summary>
        /// Queue an object for thumbnail rendering. Thread-safe.
        /// </summary>
        public void RequestThumbnail(uint id, bool isSetup) {
            if (_failedIds.Contains(id)) return;
            lock (_queue) {
                if (_queued.Add(id)) {
                    _queue.Enqueue((id, isSetup));
                }
            }
        }

        /// <summary>
        /// Process queued thumbnail requests with a time budget.
        /// Must be called at the end of the Render() pass on the GL thread,
        /// when the GL context is in a known-good state from the main scene's rendering.
        /// </summary>
        public unsafe void ProcessQueue(OpenGLRenderer currentRenderer) {
            // Only process if we are on the correct context
            if (currentRenderer != _renderer) return;

            if (_queue.Count == 0) return;

            EnsureFBO();

            var sw = Stopwatch.StartNew();
            int rendered = 0;

            while (true) {
                (uint id, bool isSetup) request;
                lock (_queue) {
                    if (_queue.Count == 0) break;
                    request = _queue.Dequeue();
                }

                if (rendered > 0 && sw.ElapsedMilliseconds >= TimeBudgetMs) {
                    // Re-enqueue for next frame
                    lock (_queue) {
                        var temp = new Queue<(uint, bool)>();
                        temp.Enqueue(request);
                        while (_queue.Count > 0) temp.Enqueue(_queue.Dequeue());
                        _queue.Clear();
                        foreach (var item in temp) _queue.Enqueue(item);
                    }
                    break;
                }

                try {
                    var pixels = RenderThumbnail(request.id, request.isSetup);
                    if (pixels != null) {
                        ThumbnailReady?.Invoke(request.id, pixels);
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
                    _queued.Remove(request.id);
                }
            }

            if (rendered > 0) {
                Console.WriteLine($"[ThumbnailRender] Rendered {rendered} thumbnails in {sw.ElapsedMilliseconds}ms (remaining: {_queue.Count})");
            }
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

            var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete) {
                Console.WriteLine($"[ThumbnailRender] FBO incomplete: {status}");
            }

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _fboInitialized = true;
        }

        private unsafe byte[]? RenderThumbnail(uint id, bool isSetup) {
            // Load render data using the MAIN scene's object manager
            var renderData = _objectManager.GetRenderData(id, isSetup);
            if (renderData == null) return null;

            // Get bounds to compute camera framing
            var bounds = _objectManager.GetBounds(id, isSetup);
            if (bounds == null) {
                _objectManager.ReleaseRenderData(id, isSetup);
                return null;
            }

            var (boundsMin, boundsMax) = bounds.Value;
            var center = (boundsMin + boundsMax) * 0.5f;
            var extents = boundsMax - boundsMin;
            var radius = extents.Length() * 0.5f;

            if (radius < 0.001f) {
                _objectManager.ReleaseRenderData(id, isSetup);
                return null;
            }

            // Camera: orbit from top-front-right at ~30 degrees elevation
            float distance = radius * 2.8f;
            float elevation = MathF.PI / 6f; // 30 degrees
            float azimuth = MathF.PI / 4f;   // 45 degrees
            var cameraOffset = new Vector3(
                MathF.Cos(elevation) * MathF.Sin(azimuth),
                MathF.Cos(elevation) * MathF.Cos(azimuth),
                MathF.Sin(elevation)
            );
            var cameraPos = center + cameraOffset * distance;

            // View and projection matrices
            var view = Matrix4x4.CreateLookAt(cameraPos, center, Vector3.UnitZ);
            float fov = MathF.PI / 4f; // 45 degrees
            float near = distance * 0.01f;
            float far = distance * 10f;
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, 1.0f, near, far);
            var viewProjection = view * projection;

            // Save current GL state
            _gl.GetInteger(GLEnum.FramebufferBinding, out int prevFbo);
            int[] prevViewport = new int[4];
            fixed (int* vp = prevViewport) {
                _gl.GetInteger(GLEnum.Viewport, vp);
            }

            // Bind our FBO
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.Viewport(0, 0, ThumbnailSize, ThumbnailSize);

            // Clear
            _gl.ClearColor(0.18f, 0.18f, 0.22f, 1.0f);
            _gl.ClearDepth(1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Set up render state
            _gl.Disable(EnableCap.StencilTest);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);

            // Bind the MAIN scene's shader and set uniforms
            _objectManager._objectShader.Bind();
            _objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            _objectManager._objectShader.SetUniform("uCameraPosition", cameraPos);
            _objectManager._objectShader.SetUniform("uLightDirection", LightDirection);
            _objectManager._objectShader.SetUniform("uAmbientIntensity", AmbientIntensity);
            _objectManager._objectShader.SetUniform("uSpecularPower", SpecularPower);

            // Render the object
            if (isSetup && renderData.IsSetup) {
                foreach (var (partId, partTransform) in renderData.SetupParts) {
                    var partRenderData = _objectManager.GetRenderData(partId, false);
                    if (partRenderData == null) continue;
                    RenderSingleObject(partRenderData, partTransform);
                }
            }
            else {
                RenderSingleObject(renderData, Matrix4x4.Identity);
            }

            _gl.BindVertexArray(0);
            _gl.UseProgram(0);

            // Read pixels
            var pixels = new byte[ThumbnailSize * ThumbnailSize * 4];
            fixed (byte* ptr = pixels) {
                _gl.ReadPixels(0, 0, ThumbnailSize, ThumbnailSize, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
            }

            // Flip vertically (OpenGL reads bottom-up)
            FlipVertically(pixels, ThumbnailSize, ThumbnailSize);

            // Restore previous GL state
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo);
            fixed (int* vp = prevViewport) {
                _gl.Viewport(vp[0], vp[1], (uint)vp[2], (uint)vp[3]);
            }

            // Release render data to avoid GPU memory bloat for objects not in the scene
            _objectManager.ReleaseRenderData(id, isSetup);
            if (isSetup && renderData.IsSetup) {
                foreach (var (partId, _) in renderData.SetupParts) {
                    _objectManager.ReleaseRenderData(partId, false);
                }
            }

            return pixels;
        }

        private unsafe void RenderSingleObject(StaticObjectRenderData renderData, Matrix4x4 transform) {
            if (renderData.Batches.Count == 0) return;

            _gl.BindVertexArray(renderData.VAO);

            // Set the instance matrix as constant vertex attributes (no instance VBO needed
            // for single-object rendering). Disable instance arrays, use constant values.
            for (int i = 0; i < 4; i++) {
                _gl.DisableVertexAttribArray((uint)(3 + i));
            }
            _gl.DisableVertexAttribArray(7); // aTextureIndex

            // Set mat4 as 4 constant vec4 attributes
            _gl.VertexAttrib4(3, transform.M11, transform.M12, transform.M13, transform.M14);
            _gl.VertexAttrib4(4, transform.M21, transform.M22, transform.M23, transform.M24);
            _gl.VertexAttrib4(5, transform.M31, transform.M32, transform.M33, transform.M34);
            _gl.VertexAttrib4(6, transform.M41, transform.M42, transform.M43, transform.M44);

            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;
                try {
                    batch.TextureArray.Bind(0);
                    _objectManager._objectShader.SetUniform("uTextureArray", 0);

                    // Set texture index as constant attribute
                    _gl.VertexAttrib1(7, (float)batch.TextureIndex);

                    _gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                    _gl.DrawElements(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[ThumbnailRender] Batch error: {ex.Message}");
                }
            }

            _gl.BindVertexArray(0);
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

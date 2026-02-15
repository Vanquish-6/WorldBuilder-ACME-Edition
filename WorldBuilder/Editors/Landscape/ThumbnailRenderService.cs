using Silk.NET.OpenGL;
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

        private readonly GL _gl;
        private readonly StaticObjectManager _objectManager;

        // Offscreen FBO resources
        private uint _fbo;
        private uint _colorTexture;
        private uint _depthRenderbuffer;
        private bool _fboInitialized;

        // Request queue: (objectId, isSetup, frameCount)
        private readonly Queue<(uint Id, bool IsSetup, int FrameCount)> _queue = new();
        private readonly HashSet<(uint Id, int FrameCount)> _queued = new(); // Avoid duplicate entries for same config
        private readonly HashSet<uint> _failedIds = new(); // Never retry objects that failed

        // Current job state for time-sliced rendering
        private enum JobState { Pending, Loading, Uploading, Rendering, Finished }

        private class Job {
            public uint Id;
            public bool IsSetup;
            public int TargetFrameCount;
            public int CompletedFrames;
            public byte[] Buffer = Array.Empty<byte>();

            public JobState State = JobState.Pending;
            public System.Threading.Tasks.Task<PreparedModelData?>? PreparationTask;

            // Granular state for intra-frame time slicing
            public bool IsFrameStarted;
            public StaticObjectRenderData? MainRenderData;
            public int PartIndex;
        }
        private Job? _currentJob;

        /// <summary>
        /// Fired on the GL thread when a thumbnail has been rendered.
        /// Parameters: objectId, RGBA pixel data (ThumbnailSize x ThumbnailSize), frameCount.
        /// </summary>
        public event Action<uint, byte[], int>? ThumbnailReady;

        // Camera setup for thumbnail rendering
        private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.5f, 0.3f, -0.3f));
        private const float AmbientIntensity = 0.6f;
        private const float SpecularPower = 32f;

        public ThumbnailRenderService(GL gl, StaticObjectManager objectManager) {
            _gl = gl;
            _objectManager = objectManager;
        }

        /// <summary>
        /// Queue an object for thumbnail rendering. Thread-safe.
        /// </summary>
        public void RequestThumbnail(uint id, bool isSetup, int frameCount = 1) {
            if (_failedIds.Contains(id)) return;
            lock (_queue) {
                if (_queued.Add((id, frameCount))) {
                    _queue.Enqueue((id, isSetup, frameCount));
                }
            }
        }

        /// <summary>
        /// Process queued thumbnail requests with a time budget.
        /// Must be called at the end of the Render() pass on the GL thread,
        /// when the GL context is in a known-good state from the main scene's rendering.
        /// </summary>
        public unsafe void ProcessQueue() {
            if (_queue.Count == 0 && _currentJob == null) return;

            // Ensure FBO is initialized
            EnsureFBO();

            var sw = Stopwatch.StartNew();
            int framesRendered = 0;

            while (true) {
                // If no active job, get next request
                if (_currentJob == null) {
                    (uint id, bool isSetup, int frameCount) request = default;
                    bool found = false;

                    lock (_queue) {
                        if (_queue.Count > 0) {
                            // Priority handling: Scan queue for single-frame requests first
                            bool foundPriority = false;
                            int count = _queue.Count;
                            for (int i = 0; i < count; i++) {
                                var item = _queue.Dequeue();
                                if (!foundPriority && item.FrameCount == 1) {
                                    request = item;
                                    foundPriority = true;
                                }
                                else {
                                    _queue.Enqueue(item);
                                }
                            }

                            if (!foundPriority && _queue.Count > 0) {
                                request = _queue.Dequeue();
                            }

                            // If we dequeued something (either priority or normal)
                            if (foundPriority || (!foundPriority && count > 0)) { // Check count before dequeue was called above
                                found = true;
                            }
                        }
                    }

                    if (!found) break; // Queue empty

                    // Initialize new job
                    int width = ThumbnailSize * request.frameCount;
                    int height = ThumbnailSize;
                    _currentJob = new Job {
                        Id = request.id,
                        IsSetup = request.isSetup,
                        TargetFrameCount = request.frameCount,
                        CompletedFrames = 0,
                        Buffer = new byte[width * height * 4]
                    };
                }

                // Render as many frames for the current job as time allows
                if (_currentJob != null) {
                    try {
                        ProcessCurrentJob(_currentJob, sw);
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[ThumbnailRender] Error processing 0x{_currentJob.Id:X8}: {ex}");
                        _failedIds.Add(_currentJob.Id);
                        lock (_queue) {
                            _queued.Remove((_currentJob.Id, _currentJob.TargetFrameCount));
                        }
                        _currentJob = null;
                    }
                }

                // If budget exceeded, stop for this frame
                if (sw.ElapsedMilliseconds >= TimeBudgetMs) {
                    break;
                }
            }
        }

        private unsafe void ProcessCurrentJob(Job job, Stopwatch sw) {
            switch (job.State) {
                case JobState.Pending:
                    // Start background loading
                    job.PreparationTask = System.Threading.Tasks.Task.Run(() =>
                        _objectManager.PrepareModelData(job.Id, job.IsSetup));
                    job.State = JobState.Loading;
                    break;

                case JobState.Loading:
                    // Check if done
                    if (job.PreparationTask?.IsCompleted == true) {
                        job.State = JobState.Uploading;
                    }
                    break;

                case JobState.Uploading:
                    // Upload to GPU (must be on render thread)
                    var preparedData = job.PreparationTask?.Result;
                    if (preparedData != null) {
                        job.MainRenderData = _objectManager.FinalizeGpuUpload(preparedData);
                    }

                    if (job.MainRenderData == null) {
                        // Failed to load
                        FinishJob(job);
                    } else {
                        job.State = JobState.Rendering;
                    }
                    break;

                case JobState.Rendering:
                    bool finished = RenderJobStep(job, sw);
                    if (finished) {
                        ThumbnailReady?.Invoke(job.Id, job.Buffer, job.TargetFrameCount);
                        FinishJob(job);
                    }
                    break;
            }
        }

        private void FinishJob(Job job) {
            lock (_queue) {
                _queued.Remove((job.Id, job.TargetFrameCount));
            }
            _currentJob = null;
        }

        private unsafe bool RenderJobStep(Job job, Stopwatch sw) {
            // Safety check
            if (job.MainRenderData == null) return true;

            // Save current GL state
            _gl.GetInteger(GLEnum.FramebufferBinding, out int prevFbo);
            int[] prevViewport = new int[4];
            fixed (int* vp = prevViewport) {
                _gl.GetInteger(GLEnum.Viewport, vp);
            }

            // Bind FBO
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.Viewport(0, 0, ThumbnailSize, ThumbnailSize);

            // Setup common GL state
            _gl.Disable(EnableCap.StencilTest);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);

            // Calculate bounds & camera (constant for the whole job, could be cached in Job too)
            var bounds = _objectManager.GetBounds(job.Id, job.IsSetup);
            if (!bounds.HasValue) {
                ReleaseJobData(job);
                return true;
            }

            var (boundsMin, boundsMax) = bounds.Value;
            var center = (boundsMin + boundsMax) * 0.5f;
            var radius = (boundsMax - boundsMin).Length() * 0.5f;

            if (radius < 0.001f) {
                ReleaseJobData(job);
                return true;
            }

            // Camera setup
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

            // Start frame if needed
            if (!job.IsFrameStarted) {
                _gl.ClearColor(0.18f, 0.18f, 0.22f, 1.0f);
                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                job.IsFrameStarted = true;
                job.PartIndex = 0;
            }

            // Calculate frame rotation
            float angle = 0f;
            if (job.TargetFrameCount > 1) {
                angle = (job.CompletedFrames / (float)job.TargetFrameCount) * MathF.PI * 2f;
            }
            var rotationMatrix = Matrix4x4.CreateRotationZ(angle);
            var objectRotation = Matrix4x4.CreateTranslation(-center) *
                               rotationMatrix *
                               Matrix4x4.CreateTranslation(center);

            // Granular rendering loop
            bool frameFinished = false;

            if (job.IsSetup && job.MainRenderData.IsSetup) {
                // Render parts incrementally
                while (job.PartIndex < job.MainRenderData.SetupParts.Count) {
                    var (partId, partTransform) = job.MainRenderData.SetupParts[job.PartIndex];

                    var partRenderData = _objectManager.GetRenderData(partId, false);
                    if (partRenderData != null) {
                        var rotatedTransform = partTransform * objectRotation;
                        RenderSingleObject(partRenderData, rotatedTransform);

                        // We do NOT release partRenderData here; we rely on LRU or let it persist briefly.
                        // Actually, StaticObjectManager caches it, so we don't need to explicitly release it
                        // unless we want to hint it's unused.
                        // The original code called ReleaseRenderData immediately.
                        // Let's call it to be safe and match original behavior to avoid memory bloat.
                        _objectManager.ReleaseRenderData(partId, false);
                    }

                    job.PartIndex++;

                    // Check budget
                    if (sw.ElapsedMilliseconds >= TimeBudgetMs) {
                        // Pause here, return false so ProcessQueue stops but doesn't discard job
                        CleanupGLState(prevFbo, prevViewport);
                        return false;
                    }
                }
                frameFinished = true;
            }
            else {
                // Render single object (assumed fast enough to be atomic)
                RenderSingleObject(job.MainRenderData, objectRotation);
                frameFinished = true;
            }

            if (frameFinished) {
                // Read pixels
                byte[] tilePixels = new byte[ThumbnailSize * ThumbnailSize * 4];
                fixed (byte* ptr = tilePixels) {
                    _gl.ReadPixels(0, 0, ThumbnailSize, ThumbnailSize, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
                }

                if (job.TargetFrameCount > 1) {
                    CopyTileToSheet(tilePixels, job.Buffer, job.CompletedFrames, job.TargetFrameCount);
                }
                else {
                    FlipVertically(tilePixels, ThumbnailSize, ThumbnailSize);
                    Array.Copy(tilePixels, job.Buffer, tilePixels.Length);
                }

                job.CompletedFrames++;
                job.IsFrameStarted = false;
                job.PartIndex = 0;
            }

            CleanupGLState(prevFbo, prevViewport);

            if (job.CompletedFrames >= job.TargetFrameCount) {
                ReleaseJobData(job);
                return true; // Finished
            }

            return false; // Not finished with job, but maybe finished with frame or time slice
        }

        private unsafe void CleanupGLState(int prevFbo, int[] prevViewport) {
            _gl.BindVertexArray(0);
            _gl.UseProgram(0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo);
            fixed (int* vp = prevViewport) {
                _gl.Viewport(vp[0], vp[1], (uint)vp[2], (uint)vp[3]);
            }
        }

        private void ReleaseJobData(Job job) {
            if (job.MainRenderData != null) {
                _objectManager.ReleaseRenderData(job.Id, job.IsSetup);
                // Also release parts if it was a setup?
                // The GetRenderData logic increments ref count?
                // Original code:
                // _objectManager.ReleaseRenderData(id, isSetup);
                // if (isSetup && renderData.IsSetup) { foreach ... ReleaseRenderData(partId) }
                // Wait, GetRenderData(partId) was called INSIDE the loop.
                // We released part data immediately in the loop above.
                // We only need to release the MAIN render data here.

                // Correction: The original code ALSO released parts at the end of RenderThumbnail.
                // But that was because it fetched them inside the loop.
                // Here we fetch parts inside the loop and release them there.
                // So we only need to release the main render data.
                job.MainRenderData = null;
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

        private static void CopyTileToSheet(byte[] tilePixels, byte[] sheetPixels, int frameIndex, int totalFrames) {
            // Tile size: ThumbnailSize x ThumbnailSize
            // Sheet size: (ThumbnailSize * totalFrames) x ThumbnailSize
            // We need to copy row by row from tilePixels (which is flipped relative to GL, so 0 is top)
            // But wait, FlipVertically was done on the *tile* in RenderThumbnail, but here we read raw GL pixels.
            // So tilePixels is upside down (bottom-up).
            // We need to flip it AND place it in the sheet.

            int bpp = 4;
            int rowBytes = ThumbnailSize * bpp;
            int sheetWidth = ThumbnailSize * totalFrames;
            int sheetRowBytes = sheetWidth * bpp;

            for (int y = 0; y < ThumbnailSize; y++) {
                // Source: bottom-up (GL) -> y=0 is bottom
                // Dest: top-down (Bitmap) -> y=0 is top
                // So srcRow is (ThumbnailSize - 1 - y)
                int srcRow = ThumbnailSize - 1 - y;
                int srcOffset = srcRow * rowBytes;

                int destRow = y;
                int destColOffset = frameIndex * rowBytes;
                int destOffset = destRow * sheetRowBytes + destColOffset;

                Array.Copy(tilePixels, srcOffset, sheetPixels, destOffset, rowBytes);
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

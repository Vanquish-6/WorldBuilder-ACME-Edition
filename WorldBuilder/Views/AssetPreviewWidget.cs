using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Silk.NET.OpenGL;
using System;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using Chorizite.Core.Render;
using Avalonia.Data;
using System.Collections.Generic;

namespace WorldBuilder.Views {
    public partial class AssetPreviewWidget : Base3DView {
        private uint _objectId;
        private bool _isSetup;
        private float _rotationAngle = 0f;
        private bool _autoRotate = true;
        private float _autoRotateSpeed = 0.5f; // radians per second
        private Vector2 _lastMousePos;

        // Camera controls
        private float _distance = 10f;
        private float _elevation = MathF.PI / 6f; // 30 degrees
        private float _azimuth = MathF.PI / 4f;   // 45 degrees

        public static readonly StyledProperty<StaticObjectManager?> ObjectManagerProperty =
            AvaloniaProperty.Register<AssetPreviewWidget, StaticObjectManager?>(nameof(ObjectManager));

        public StaticObjectManager? ObjectManager {
            get => GetValue(ObjectManagerProperty);
            set => SetValue(ObjectManagerProperty, value);
        }

        public static readonly StyledProperty<bool> AutoRotateProperty =
            AvaloniaProperty.Register<AssetPreviewWidget, bool>(nameof(AutoRotate), defaultValue: true);

        public bool AutoRotate {
            get => GetValue(AutoRotateProperty);
            set => SetValue(AutoRotateProperty, value);
        }

        public AssetPreviewWidget() {
            var viewport = new Panel { Name = "Viewport", Background = Brushes.Transparent };
            Content = viewport;
            InitializeBase3DView();
            AutoRotate = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == AutoRotateProperty) {
                _autoRotate = change.GetNewValue<bool>();
            }
        }

        public void SetObject(uint objectId, bool isSetup) {
            _objectId = objectId;
            _isSetup = isSetup;
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }

        protected override void OnGlInit(GL gl, PixelSize canvasSize) {
            // No special init needed
        }

        protected override void OnGlResize(PixelSize canvasSize) {
            // Handled by Base3DView / Renderer
        }

        protected override void OnGlDestroy() {
            // No resources to destroy
        }

        protected override void OnGlRender(double deltaTime) {
            if (ObjectManager == null) return;
            var gl = Renderer?.GraphicsDevice.GL;
            if (gl == null) return;

            if (_autoRotate) {
                _rotationAngle += _autoRotateSpeed * (float)deltaTime;
            }

            RenderPreview(gl, new PixelSize((int)Bounds.Width, (int)Bounds.Height));
        }

        private void RenderPreview(GL gl, PixelSize size) {
            if (ObjectManager == null) return;

            var renderData = ObjectManager.GetRenderData(_objectId, _isSetup);
            if (renderData == null) return;

            var bounds = ObjectManager.GetBounds(_objectId, _isSetup);
            if (!bounds.HasValue) {
                ObjectManager.ReleaseRenderData(_objectId, _isSetup);
                return;
            }

            var (boundsMin, boundsMax) = bounds.Value;
            var center = (boundsMin + boundsMax) * 0.5f;
            var radius = (boundsMax - boundsMin).Length() * 0.5f;

            if (radius < 0.001f) {
                ObjectManager.ReleaseRenderData(_objectId, _isSetup);
                return;
            }

            var rotation = Matrix4x4.CreateRotationZ(_rotationAngle);

            // Camera setup
            var cameraOffset = new Vector3(
                MathF.Cos(_elevation) * MathF.Sin(_azimuth),
                MathF.Cos(_elevation) * MathF.Cos(_azimuth),
                MathF.Sin(_elevation)
            );

            // Apply auto-rotation to the camera position
            var finalCameraOffset = Vector3.Transform(cameraOffset, rotation);

            float zoomFactor = _distance / 10f;
            float dist = radius * 2.8f * zoomFactor;

            var viewPos = center + finalCameraOffset * dist;

            var view = Matrix4x4.CreateLookAt(viewPos, center, Vector3.UnitZ);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f,
                size.Width / (float)size.Height,
                dist * 0.01f,
                dist * 10f
            );

            // GL State
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);
            gl.ClearColor(0f, 0f, 0f, 0f);
            gl.Clear(ClearBufferMask.DepthBufferBit | ClearBufferMask.ColorBufferBit);

            // Setup Shader
            var shader = ObjectManager._objectShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", view * projection);
            shader.SetUniform("uCameraPosition", viewPos);
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.5f, 0.3f, -0.3f)));
            shader.SetUniform("uAmbientIntensity", 0.6f);
            shader.SetUniform("uSpecularPower", 32f);

            // Render
            if (_isSetup && renderData.IsSetup) {
                foreach (var (partId, partTransform) in renderData.SetupParts) {
                    var partRenderData = ObjectManager.GetRenderData(partId, false);
                    if (partRenderData == null) continue;
                    RenderSingleObject(gl, partRenderData, partTransform, shader);
                    ObjectManager.ReleaseRenderData(partId, false);
                }
            }
            else {
                RenderSingleObject(gl, renderData, Matrix4x4.Identity, shader);
            }

            ObjectManager.ReleaseRenderData(_objectId, _isSetup);
        }

        private unsafe void RenderSingleObject(GL gl, StaticObjectRenderData renderData, Matrix4x4 transform, IShader shader) {
            if (renderData.Batches.Count == 0) return;

            gl.BindVertexArray(renderData.VAO);

            // Set the instance matrix as constant vertex attributes
            for (int i = 0; i < 4; i++) {
                gl.DisableVertexAttribArray((uint)(3 + i));
            }
            gl.DisableVertexAttribArray(7); // aTextureIndex

            gl.VertexAttrib4(3, transform.M11, transform.M12, transform.M13, transform.M14);
            gl.VertexAttrib4(4, transform.M21, transform.M22, transform.M23, transform.M24);
            gl.VertexAttrib4(5, transform.M31, transform.M32, transform.M33, transform.M34);
            gl.VertexAttrib4(6, transform.M41, transform.M42, transform.M43, transform.M44);

            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;
                batch.TextureArray.Bind(0);
                shader.SetUniform("uTextureArray", 0);
                gl.VertexAttrib1(7, (float)batch.TextureIndex);

                gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                gl.DrawElements(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null);
            }

            gl.BindVertexArray(0);
        }

        protected override void OnGlPointerPressed(PointerPressedEventArgs e) {
            SetCurrentValue(AutoRotateProperty, false);
            var p = e.GetPosition(this);
            _lastMousePos = new Vector2((float)p.X, (float)p.Y);
        }

        protected override void OnGlPointerMoved(PointerEventArgs e, Vector2 mousePositionScaled) {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
                var p = e.GetPosition(this);
                var pos = new Vector2((float)p.X, (float)p.Y);
                var delta = pos - _lastMousePos;

                _azimuth -= delta.X * 0.01f;
                _elevation = Math.Clamp(_elevation + delta.Y * 0.01f,
                    -MathF.PI / 2f + 0.1f,
                    MathF.PI / 2f - 0.1f);

                _lastMousePos = pos;
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            } else {
                var p = e.GetPosition(this);
                _lastMousePos = new Vector2((float)p.X, (float)p.Y);
            }
        }

        protected override void OnGlPointerWheelChanged(PointerWheelEventArgs e) {
            _distance *= MathF.Exp(-(float)e.Delta.Y * 0.1f);
            _distance = Math.Clamp(_distance, 2f, 50f);
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }

        protected override void OnGlKeyDown(KeyEventArgs e) { }
        protected override void OnGlKeyUp(KeyEventArgs e) { }
        protected override void OnGlPointerReleased(PointerReleasedEventArgs e) { }
    }
}

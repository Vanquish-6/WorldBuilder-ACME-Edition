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
        private StaticObjectManager? _cachedObjectManager;
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

        public static readonly StyledProperty<uint> ObjectIdProperty =
            AvaloniaProperty.Register<AssetPreviewWidget, uint>(nameof(ObjectId));

        public uint ObjectId {
            get => GetValue(ObjectIdProperty);
            set => SetValue(ObjectIdProperty, value);
        }

        public static readonly StyledProperty<bool> IsSetupProperty =
            AvaloniaProperty.Register<AssetPreviewWidget, bool>(nameof(IsSetup));

        public bool IsSetup {
            get => GetValue(IsSetupProperty);
            set => SetValue(IsSetupProperty, value);
        }

        public static readonly StyledProperty<bool> AutoRotateProperty =
            AvaloniaProperty.Register<AssetPreviewWidget, bool>(nameof(AutoRotate), defaultValue: true);

        public bool AutoRotate {
            get => GetValue(AutoRotateProperty);
            set => SetValue(AutoRotateProperty, value);
        }

        public AssetPreviewWidget() {
            var viewport = new Panel { Name = "Viewport", Background = Brushes.Transparent };

            // Register name scope so Base3DView can find "Viewport"
            if (NameScope.GetNameScope(this) == null) {
                NameScope.SetNameScope(this, new NameScope());
            }
            NameScope.GetNameScope(this)?.Register("Viewport", viewport);

            Content = viewport;
            InitializeBase3DView();
            AutoRotate = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == AutoRotateProperty) {
                _autoRotate = change.GetNewValue<bool>();
            }
            else if (change.Property == ObjectIdProperty) {
                _objectId = change.GetNewValue<uint>();
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
            }
            else if (change.Property == IsSetupProperty) {
                _isSetup = change.GetNewValue<bool>();
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
            }
            else if (change.Property == ObjectManagerProperty) {
                _cachedObjectManager = change.GetNewValue<StaticObjectManager?>();
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
            }
        }

        public void SetObject(uint objectId, bool isSetup) {
            SetCurrentValue(ObjectIdProperty, objectId);
            SetCurrentValue(IsSetupProperty, isSetup);
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
            if (_cachedObjectManager == null) return;
            var gl = Renderer?.GraphicsDevice.GL;
            if (gl == null) return;

            if (_autoRotate) {
                _rotationAngle += _autoRotateSpeed * (float)deltaTime;
            }

            RenderPreview(gl, new PixelSize((int)Bounds.Width, (int)Bounds.Height));
        }

        private void RenderPreview(GL gl, PixelSize size) {
            var objectManager = _cachedObjectManager;
            if (objectManager == null) return;

            var renderData = objectManager.GetRenderData(_objectId, _isSetup);
            if (renderData == null) return;

            var bounds = objectManager.GetBounds(_objectId, _isSetup);
            if (!bounds.HasValue) {
                objectManager.ReleaseRenderData(_objectId, _isSetup);
                return;
            }

            var (boundsMin, boundsMax) = bounds.Value;
            var center = (boundsMin + boundsMax) * 0.5f;
            var radius = (boundsMax - boundsMin).Length() * 0.5f;

            if (radius < 0.001f) {
                objectManager.ReleaseRenderData(_objectId, _isSetup);
                return;
            }

            // Fixed camera position (no rotation applied to camera)
            var cameraOffset = new Vector3(
                MathF.Cos(_elevation) * MathF.Sin(_azimuth),
                MathF.Cos(_elevation) * MathF.Cos(_azimuth),
                MathF.Sin(_elevation)
            );

            float zoomFactor = _distance / 10f;
            float dist = radius * 2.8f * zoomFactor;

            var viewPos = center + cameraOffset * dist;

            var view = Matrix4x4.CreateLookAt(viewPos, center, Vector3.UnitZ);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f,
                size.Width / (float)size.Height,
                dist * 0.01f,
                dist * 10f
            );

            // Flip Y axis to correct for OpenGL/Window coordinate mismatch (Upside Down fix)
            projection.M22 *= -1;

            // GL State
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);
            gl.FrontFace(FrontFaceDirection.CW); // Flip winding order because of negative Y scaling
            gl.ClearColor(0.1f, 0.1f, 0.12f, 1.0f); // Dark gray background instead of transparent
            gl.Clear(ClearBufferMask.DepthBufferBit | ClearBufferMask.ColorBufferBit);

            // Setup Shader
            var shader = objectManager._objectShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", view * projection);
            shader.SetUniform("uCameraPosition", viewPos);
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.5f, 0.3f, -0.3f)));
            shader.SetUniform("uAmbientIntensity", 0.6f);
            shader.SetUniform("uSpecularPower", 32f);

            // Create rotation matrix for the object around Z axis (vertical spin)
            // We rotate around the object center.
            var objectRotation = Matrix4x4.CreateTranslation(-center) *
                           Matrix4x4.CreateRotationZ(_rotationAngle) *
                               Matrix4x4.CreateTranslation(center);

            // Render with rotation applied to the object
            if (_isSetup && renderData.IsSetup) {
                foreach (var (partId, partTransform) in renderData.SetupParts) {
                    var partRenderData = objectManager.GetRenderData(partId, false);
                    if (partRenderData == null) continue;

                    // Apply rotation to the part transform
                    var rotatedTransform = partTransform * objectRotation;
                    RenderSingleObject(gl, partRenderData, rotatedTransform, shader);
                    objectManager.ReleaseRenderData(partId, false);
                }
            }
            else {
                // Apply rotation to the object
                RenderSingleObject(gl, renderData, objectRotation, shader);
            }

            objectManager.ReleaseRenderData(_objectId, _isSetup);
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

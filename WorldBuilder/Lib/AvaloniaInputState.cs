using Avalonia;
using Avalonia.Input;
using System;
using System.Collections;
using System.Numerics;
using WorldBuilder.Editors.Landscape;

namespace WorldBuilder.Lib {
    public class AvaloniaInputState {
        private readonly BitArray _keys = new((int)Key.DeadCharProcessed + 1);
        private readonly BitArray _keysPrevious = new((int)Key.DeadCharProcessed + 1);
        private MouseState _currentMouseState;

        public KeyModifiers Modifiers { get; internal set; }
        public MouseState MouseState => _currentMouseState;
        private Vector2 _lastMousePos = new();

        /// <summary>
        /// Called internally at the start of each frame to copy keyboard state to the previous frame's buffer.
        /// You probably don't want to call this, but it's public just-in-case.
        /// </summary>
        public void OnFrame() {
            _keysPrevious.SetAll(false);
            _keysPrevious.Or(_keys);
            _lastMousePos = MouseState.Position;
        }

        /// <summary>
        /// Called to set the state of a key when an input event is received.
        /// </summary>
        /// <param name="key">The key to set</param>
        /// <param name="pressed">True if the key is down, false if it is up.</param>
        public void SetKey(Key key, bool pressed) => _keys.Set((int)key, pressed);

        /// <summary>
        /// Checks if the specified key was down at the start of the current frame.
        /// </summary>
        /// <param name="key">The key to check the state of</param>
        /// <returns>True if the key was down, false if it was up.</returns>
        public bool IsKeyDown(Key key) => _keys.Get((int)key);

        /// <summary>
        /// Checks if the specified key was down at the start of the <em>previous</em> frame.
        /// </summary>
        /// <param name="key">The key to check the state of</param>
        /// <returns>True if the key was down, false if it was up.</returns>
        public bool WasKeyDownLastFrame(Key key) => _keysPrevious.Get((int)key);

        internal void UpdateMouseState(Point p, PointerPointProperties properties, int Width, int Height, Vector2 inputScale, ICamera camera, TerrainSystem provider) {
            Vector2 relativePos = new Vector2((float)p.X, (float)p.Y) * inputScale;
            var hitResult = TerrainRaycast.Raycast(
                relativePos.X, relativePos.Y,
                Width, Height,
                camera,
                provider
            );

            // Object raycast (only if scene is available)
            ObjectRaycast.ObjectRaycastHit? objectHit = null;
            EnvCellManager.EnvCellRaycastHit? envCellHit = null;
            if (provider.Scene != null) {
                var objHitResult = ObjectRaycast.Raycast(
                    relativePos.X, relativePos.Y,
                    Width, Height,
                    camera,
                    provider.Scene
                );
                if (objHitResult.Hit) {
                    objectHit = objHitResult;
                }

                // EnvCell (dungeon cell) raycast — build ray the same way as ObjectRaycast
                if (provider.Scene._envCellManager.LoadedCellCount > 0) {
                    float ndcX = 2.0f * relativePos.X / Width - 1.0f;
                    float ndcY = 2.0f * relativePos.Y / Height - 1.0f;
                    Matrix4x4 projection = camera.GetProjectionMatrix();
                    Matrix4x4 view = camera.GetViewMatrix();
                    if (Matrix4x4.Invert(view * projection, out Matrix4x4 vpInverse)) {
                        Vector4 nearW = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), vpInverse);
                        Vector4 farW = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), vpInverse);
                        nearW /= nearW.W;
                        farW /= farW.W;
                        var rayOrigin = new Vector3(nearW.X, nearW.Y, nearW.Z);
                        var rayDir = Vector3.Normalize(new Vector3(farW.X, farW.Y, farW.Z) - rayOrigin);
                        var cellHitResult = provider.Scene._envCellManager.Raycast(rayOrigin, rayDir);
                        if (cellHitResult.Hit) {
                            // Only use EnvCell hit if no object was closer
                            if (!objectHit.HasValue || cellHitResult.Distance < objectHit.Value.Distance) {
                                envCellHit = cellHitResult;
                            }
                        }
                    }
                }
            }

            _currentMouseState = new MouseState {
                Position = relativePos,
                LeftPressed = properties.IsLeftButtonPressed,
                RightPressed = properties.IsRightButtonPressed,
                MiddlePressed = properties.IsMiddleButtonPressed,
                ShiftPressed = Modifiers.HasFlag(KeyModifiers.Shift),
                CtrlPressed = Modifiers.HasFlag(KeyModifiers.Control),
                Delta = relativePos - _lastMousePos,
                IsOverTerrain = hitResult.Hit,
                TerrainHit = hitResult.Hit ? hitResult : null,
                ObjectHit = objectHit,
                EnvCellHit = envCellHit
            };
        }
    }
}
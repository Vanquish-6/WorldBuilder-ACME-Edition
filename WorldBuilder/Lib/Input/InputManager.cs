using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;

namespace WorldBuilder.Lib.Input {
    public class InputManager {
        public static InputManager? Instance { get; private set; }

        private readonly WorldBuilderSettings _rootSettings;
        private InputSettings _settings => _rootSettings.Input;
        private readonly Dictionary<string, List<InputBinding>> _activeBindings = new();

        public InputManager(WorldBuilderSettings settings) {
            if (Instance == null) Instance = this;
            _rootSettings = settings;

            // If settings are empty, load defaults
            if (_settings.Bindings == null || _settings.Bindings.Count == 0) {
                _settings.Bindings = GetDefaultBindings();
            }

            ReloadBindings();
        }

        public void ReloadBindings() {
            _activeBindings.Clear();
            foreach (var binding in _settings.Bindings) {
                if (!_activeBindings.ContainsKey(binding.ActionName)) {
                    _activeBindings[binding.ActionName] = new List<InputBinding>();
                }
                _activeBindings[binding.ActionName].Add(binding);
            }
        }

        public void ResetToDefaults() {
            _settings.Bindings = GetDefaultBindings();
            ReloadBindings();
        }

        public void RevertToSaved() {
            // Load settings from disk
            if (System.IO.File.Exists(_rootSettings.SettingsFilePath)) {
                try {
                    var json = System.IO.File.ReadAllText(_rootSettings.SettingsFilePath);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<WorldBuilderSettings>(json, SourceGenerationContext.Default.WorldBuilderSettings);
                    if (settings?.Input != null) {
                        _rootSettings.Input = settings.Input;
                        // _settings property accesses _rootSettings.Input so it's updated automatically
                        ReloadBindings();
                    }
                }
                catch {
                    // Ignore load errors
                }
            }
        }

        public bool IsActionActive(string actionName, AvaloniaInputState inputState) {
            if (_activeBindings.TryGetValue(actionName, out var bindings)) {
                foreach (var binding in bindings) {
                    if (IsBindingActive(binding, inputState)) {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool IsActionTriggered(string actionName, AvaloniaInputState inputState) {
            if (_activeBindings.TryGetValue(actionName, out var bindings)) {
                foreach (var binding in bindings) {
                    if (IsBindingTriggered(binding, inputState)) {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool MatchesAction(string actionName, Key key, KeyModifiers modifiers) {
            if (_activeBindings.TryGetValue(actionName, out var bindings)) {
                foreach (var binding in bindings) {
                    if (binding.Key == key && (binding.IgnoreModifiers || binding.Modifiers == modifiers)) {
                        return true;
                    }
                }
            }
            return false;
        }

        public KeyGesture? GetKeyGesture(string actionName) {
            if (_activeBindings.TryGetValue(actionName, out var bindings) && bindings.Count > 0) {
                // Return the first binding as the gesture hint
                var binding = bindings[0];
                return new KeyGesture(binding.Key, binding.Modifiers);
            }
            return null;
        }

        private bool IsBindingActive(InputBinding binding, AvaloniaInputState inputState) {
            if (!binding.IgnoreModifiers && binding.Modifiers != inputState.Modifiers) return false;
            return inputState.IsKeyDown(binding.Key);
        }

        private bool IsBindingTriggered(InputBinding binding, AvaloniaInputState inputState) {
            if (!binding.IgnoreModifiers && binding.Modifiers != inputState.Modifiers) return false;

            // Key is down NOW, and was NOT down LAST FRAME
            return inputState.IsKeyDown(binding.Key) && !inputState.WasKeyDownLastFrame(binding.Key);
        }

        private List<InputBinding> GetDefaultBindings() {
            var list = new List<InputBinding>();

            // Camera Movement (WASD) - Ignore modifiers so you can move while holding Shift/Ctrl etc.
            list.Add(new InputBinding(InputActions.CameraMoveForward, Key.W, KeyModifiers.None, "Move Forward", "Camera", ignoreModifiers: true));
            list.Add(new InputBinding(InputActions.CameraMoveBackward, Key.S, KeyModifiers.None, "Move Backward", "Camera", ignoreModifiers: true));
            list.Add(new InputBinding(InputActions.CameraMoveLeft, Key.A, KeyModifiers.None, "Move Left", "Camera", ignoreModifiers: true));
            list.Add(new InputBinding(InputActions.CameraMoveRight, Key.D, KeyModifiers.None, "Move Right", "Camera", ignoreModifiers: true));

            // Camera Movement (Arrows) - Only works if Shift is NOT held. Strict Modifiers = None.
            list.Add(new InputBinding(InputActions.CameraMoveForward, Key.Up, KeyModifiers.None, "Move Forward (Alt)", "Camera"));
            list.Add(new InputBinding(InputActions.CameraMoveBackward, Key.Down, KeyModifiers.None, "Move Backward (Alt)", "Camera"));
            list.Add(new InputBinding(InputActions.CameraMoveLeft, Key.Left, KeyModifiers.None, "Move Left (Alt)", "Camera"));
            list.Add(new InputBinding(InputActions.CameraMoveRight, Key.Right, KeyModifiers.None, "Move Right (Alt)", "Camera"));

            // Camera Rotation (Shift + Arrows)
            list.Add(new InputBinding(InputActions.CameraRotateUp, Key.Up, KeyModifiers.Shift, "Rotate Up", "Camera"));
            list.Add(new InputBinding(InputActions.CameraRotateDown, Key.Down, KeyModifiers.Shift, "Rotate Down", "Camera"));
            list.Add(new InputBinding(InputActions.CameraRotateLeft, Key.Left, KeyModifiers.Shift, "Rotate Left", "Camera"));
            list.Add(new InputBinding(InputActions.CameraRotateRight, Key.Right, KeyModifiers.Shift, "Rotate Right", "Camera"));

            // Camera Other
            list.Add(new InputBinding(InputActions.CameraToggleMode, Key.Q, KeyModifiers.None, "Toggle Camera Mode", "Camera"));
            list.Add(new InputBinding(InputActions.CameraZoomIn, Key.OemPlus, KeyModifiers.None, "Zoom In", "Camera", ignoreModifiers: true));
            list.Add(new InputBinding(InputActions.CameraZoomIn, Key.Add, KeyModifiers.None, "Zoom In (Alt)", "Camera", ignoreModifiers: true));
            list.Add(new InputBinding(InputActions.CameraZoomOut, Key.OemMinus, KeyModifiers.None, "Zoom Out", "Camera", ignoreModifiers: true));
            list.Add(new InputBinding(InputActions.CameraZoomOut, Key.Subtract, KeyModifiers.None, "Zoom Out (Alt)", "Camera", ignoreModifiers: true));

            // Navigation
            list.Add(new InputBinding(InputActions.NavigationGoToLandblock, Key.G, KeyModifiers.Control, "Go To Landblock", "Navigation"));

            // Edit
            list.Add(new InputBinding(InputActions.EditUndo, Key.Z, KeyModifiers.Control, "Undo", "Edit"));
            list.Add(new InputBinding(InputActions.EditRedo, Key.Z, KeyModifiers.Control | KeyModifiers.Shift, "Redo", "Edit"));
            list.Add(new InputBinding(InputActions.EditRedoAlternate, Key.Y, KeyModifiers.Control, "Redo (Alt)", "Edit"));
            list.Add(new InputBinding(InputActions.EditCopy, Key.C, KeyModifiers.Control, "Copy", "Edit"));
            list.Add(new InputBinding(InputActions.EditPaste, Key.V, KeyModifiers.Control, "Paste", "Edit"));
            list.Add(new InputBinding(InputActions.EditDelete, Key.Delete, KeyModifiers.None, "Delete", "Edit"));
            list.Add(new InputBinding(InputActions.EditCancel, Key.Escape, KeyModifiers.None, "Cancel/Deselect", "Edit", ignoreModifiers: true));

            // App
            list.Add(new InputBinding(InputActions.AppExit, Key.F4, KeyModifiers.Alt, "Exit Application", "Application"));

            return list;
        }

        public List<InputBinding> GetAllBindings() {
            // Return a flat list of all active bindings (from settings)
            return _settings.Bindings;
        }

        public void SaveBinding(InputBinding binding) {
             ReloadBindings();
        }
    }
}

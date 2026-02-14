using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorldBuilder.Lib.Input;
using WorldBuilder.Lib.Settings;
using System.Linq;
using Avalonia.Input;
using System.Collections.Generic;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class KeyboardMappingViewModel : ViewModelBase {
        private readonly InputManager _inputManager;
        private readonly WorldBuilderSettings _settings;

        [ObservableProperty]
        private ObservableCollection<InputBindingViewModel> _bindings = new();

        [ObservableProperty]
        private InputBindingViewModel? _selectedBinding;

        [ObservableProperty]
        private bool _isListening;

        public KeyboardMappingViewModel(InputManager inputManager, WorldBuilderSettings settings) {
            _inputManager = inputManager;
            _settings = settings;
            LoadBindings();
        }

        private void LoadBindings() {
            Bindings.Clear();
            var allBindings = _inputManager.GetAllBindings();

            // Group by category if desired, but for now flat list sorted by category/action
            var sorted = allBindings.OrderBy(b => b.Category).ThenBy(b => b.ActionName);

            foreach (var b in sorted) {
                Bindings.Add(new InputBindingViewModel(b));
            }
        }

        [RelayCommand]
        private void StartRebind(InputBindingViewModel binding) {
            SelectedBinding = binding;
            IsListening = true;
            binding.IsListening = true;
        }

        public void HandleKeyPress(Key key, KeyModifiers modifiers) {
            if (IsListening && SelectedBinding != null) {
                // Ignore modifier-only presses if we want?
                // Usually we wait for a non-modifier key, unless we want to bind "Ctrl" (rare).
                // But Avalonia passes modifiers separately.
                // If key is Key.LeftCtrl, modifiers might be None or Control.
                // We typically bind "Action" to "Key + Modifiers".
                // If the user presses "Ctrl+C", Key is C, Modifiers is Control.

                // If Key is a modifier key, we might want to wait?
                if (IsModifierKey(key)) return;

                SelectedBinding.Key = key;
                SelectedBinding.Modifiers = modifiers;

                // Commit to the actual binding object
                SelectedBinding.Commit();

                // Update manager
                _inputManager.SaveBinding(SelectedBinding.Source);

                StopRebind();
            }
        }

        private bool IsModifierKey(Key key) {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LWin || key == Key.RWin;
        }

        [RelayCommand]
        private void StopRebind() {
            if (SelectedBinding != null) {
                SelectedBinding.IsListening = false;
            }
            IsListening = false;
            SelectedBinding = null;
        }

        [RelayCommand]
        private void Save() {
            _settings.Save();
            // Revert button logic depends on state?
            // The user requirement says "leaving the revert button unless they press save... which will solidify".
            // So "Save" solidifies. Revert reverts to *last saved*.
            // If we just saved, Revert would revert to now.
        }

        [RelayCommand]
        private void Revert() {
            _inputManager.RevertToSaved();
            LoadBindings();
        }

        [RelayCommand]
        private void ResetToDefaults() {
            _inputManager.ResetToDefaults();
            LoadBindings();
        }
    }

    public partial class InputBindingViewModel : ObservableObject {
        public InputBinding Source { get; }

        public string ActionName => Source.ActionName;
        public string Description => Source.Description;
        public string Category => Source.Category;

        [ObservableProperty]
        private Key _key;

        [ObservableProperty]
        private KeyModifiers _modifiers;

        [ObservableProperty]
        private bool _isListening;

        public InputBindingViewModel(InputBinding source) {
            Source = source;
            Key = source.Key;
            Modifiers = source.Modifiers;
        }

        public void Commit() {
            Source.Key = Key;
            Source.Modifiers = Modifiers;
        }

        public string KeyDisplay => $"{Modifiers} + {Key}".Replace("None + ", "").Replace("None", "");

        // When Key/Modifiers change, update Display
        partial void OnKeyChanged(Key value) => OnPropertyChanged(nameof(KeyDisplay));
        partial void OnModifiersChanged(KeyModifiers value) => OnPropertyChanged(nameof(KeyDisplay));
    }
}

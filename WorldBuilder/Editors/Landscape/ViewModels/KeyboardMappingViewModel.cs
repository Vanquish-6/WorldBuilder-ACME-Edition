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
        private ObservableCollection<ActionInputViewModel> _actions = new();

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
            Actions.Clear();
            var allBindings = _inputManager.GetAllBindings();

            // Group bindings by action
            var groups = allBindings.GroupBy(b => b.ActionName)
                                    .OrderBy(g => g.First().Category)
                                    .ThenBy(g => g.Key);

            foreach (var group in groups) {
                var first = group.First();
                var actionVM = new ActionInputViewModel(first.Category, first.Description);

                foreach (var binding in group) {
                    actionVM.Bindings.Add(new InputBindingViewModel(binding));
                }

                // Ensure at least 2 slots? Or just display what we have?
                // Request was "put the alts on the same line (so it would just be like 2 input boxes)"
                // If we have only 1 binding, maybe add a placeholder?
                // For now, let's just list existing bindings. If user wants to ADD a binding, that's a different feature.
                // But typically "Primary" and "Alternate" implies 2 slots.
                // The current data model supports N bindings.

                Actions.Add(actionVM);
            }

            CheckConflicts();
        }

        [RelayCommand]
        private void StartRebind(InputBindingViewModel binding) {
            SelectedBinding = binding;
            IsListening = true;
            binding.IsListening = true;
        }

        public void HandleKeyPress(Key key, KeyModifiers modifiers) {
            if (IsListening && SelectedBinding != null) {
                if (IsModifierKey(key)) return;

                SelectedBinding.Key = key;
                SelectedBinding.Modifiers = modifiers;

                SelectedBinding.Commit();
                _inputManager.SaveBinding(SelectedBinding.Source);

                CheckConflicts();
                StopRebind();
            }
        }

        private void CheckConflicts() {
            var allBindings = Actions.SelectMany(a => a.Bindings).ToList();

            foreach (var binding in allBindings) {
                binding.IsConflicting = false;
            }

            for (int i = 0; i < allBindings.Count; i++) {
                var b1 = allBindings[i];
                if (b1.Key == Key.None) continue; // Ignore unbound?

                for (int j = i + 1; j < allBindings.Count; j++) {
                    var b2 = allBindings[j];
                    if (b2.Key == Key.None) continue;

                    // Conflict if keys match AND modifiers match
                    // What about "IgnoreModifiers"?
                    // If b1 ignores modifiers, it conflicts with b2 if b2.Key == b1.Key
                    // If b2 ignores modifiers, it conflicts with b1 if b1.Key == b2.Key
                    // If neither ignores, they must match exactly.

                    bool conflict = false;
                    if (b1.Source.IgnoreModifiers || b2.Source.IgnoreModifiers) {
                        if (b1.Key == b2.Key) conflict = true;
                    }
                    else {
                        if (b1.Key == b2.Key && b1.Modifiers == b2.Modifiers) conflict = true;
                    }

                    if (conflict) {
                        b1.IsConflicting = true;
                        b2.IsConflicting = true;
                    }
                }
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

            // Mark all bindings as saved (unmodified)
            foreach (var action in Actions) {
                foreach (var binding in action.Bindings) {
                    binding.MarkAsSaved();
                }
            }
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

    public partial class ActionInputViewModel : ObservableObject {
        public string Category { get; }
        public string Description { get; }

        [ObservableProperty]
        private ObservableCollection<InputBindingViewModel> _bindings = new();

        public ActionInputViewModel(string category, string description) {
            Category = category;
            Description = description;
        }
    }

    public partial class InputBindingViewModel : ObservableObject {
        public InputBinding Source { get; }

        public string ActionName => Source.ActionName;
        public string Description => Source.Description;
        public string Category => Source.Category;

        private Key _originalKey;
        private KeyModifiers _originalModifiers;

        [ObservableProperty]
        private Key _key;

        [ObservableProperty]
        private KeyModifiers _modifiers;

        [ObservableProperty]
        private bool _isListening;

        [ObservableProperty]
        private bool _isModified;

        [ObservableProperty]
        private bool _isConflicting;

        public InputBindingViewModel(InputBinding source) {
            Source = source;
            Key = source.Key;
            Modifiers = source.Modifiers;

            // Store original state for modification tracking
            _originalKey = source.Key;
            _originalModifiers = source.Modifiers;
        }

        public void Commit() {
            Source.Key = Key;
            Source.Modifiers = Modifiers;
            CheckModified();
        }

        public void MarkAsSaved() {
            _originalKey = Key;
            _originalModifiers = Modifiers;
            CheckModified();
        }

        [RelayCommand]
        public void RevertBinding() {
            Key = _originalKey;
            Modifiers = _originalModifiers;
            Commit(); // Updates Source and IsModified
        }

        private void CheckModified() {
            IsModified = Key != _originalKey || Modifiers != _originalModifiers;
        }

        public string KeyDisplay => $"{Modifiers} + {Key}".Replace("None + ", "").Replace("None", "");

        // When Key/Modifiers change, update Display
        partial void OnKeyChanged(Key value) => OnPropertyChanged(nameof(KeyDisplay));
        partial void OnModifiersChanged(KeyModifiers value) => OnPropertyChanged(nameof(KeyDisplay));
    }
}

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using WorldBuilder.Lib.Input;

namespace WorldBuilder.Lib.Settings {
    public class InputSettings : ObservableObject {
        private List<InputBinding> _bindings = new();
        public List<InputBinding> Bindings {
            get => _bindings;
            set => SetProperty(ref _bindings, value);
        }
    }
}

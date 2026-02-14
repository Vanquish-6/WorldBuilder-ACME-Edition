using Avalonia.Input;
using System.Text.Json.Serialization;

namespace WorldBuilder.Lib.Input {
    public class InputBinding {
        public string ActionName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public Key Key { get; set; }
        public KeyModifiers Modifiers { get; set; }
        public bool IgnoreModifiers { get; set; }

        public InputBinding() { }

        public InputBinding(string actionName, Key key, KeyModifiers modifiers, string description, string category = "General", bool ignoreModifiers = false) {
            ActionName = actionName;
            Key = key;
            Modifiers = modifiers;
            Description = description;
            Category = category;
            IgnoreModifiers = ignoreModifiers;
        }

        public InputBinding Clone() {
            return new InputBinding(ActionName, Key, Modifiers, Description, Category, IgnoreModifiers);
        }

        public override string ToString() {
            var modStr = Modifiers == KeyModifiers.None ? "" : $"{Modifiers} + ";
            return $"{modStr}{Key}";
        }
    }
}

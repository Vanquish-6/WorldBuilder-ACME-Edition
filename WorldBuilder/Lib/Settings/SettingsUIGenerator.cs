using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using WorldBuilder.Lib.Converters;

namespace WorldBuilder.Lib.Settings {
    /// <summary>
    /// Generates Avalonia UI controls dynamically from settings metadata
    /// </summary>
    public class SettingsUIGenerator {
        private readonly object _settingsRoot;
        private readonly SettingsMetadataProvider _metadata;
        private readonly SettingsUIHandlers? _handlers;

        public SettingsUIGenerator(object settingsRoot, Window? window = null) {
            _settingsRoot = settingsRoot;
            _metadata = new SettingsMetadataProvider(settingsRoot.GetType());
            _handlers = window != null ? new SettingsUIHandlers(window) : null;
        }

        /// <summary>
        /// Generate navigation items for the settings categories
        /// </summary>
        public ListBox GenerateNavigation() {
            var listBox = new ListBox {
                Margin = new Thickness(0, 16, 0, 0)
            };

            foreach (var category in _metadata.RootCategories.OrderBy(c => c.Order)) {
                AddNavigationItems(listBox, category, isRoot: true);
            }

            return listBox;
        }

        private void AddNavigationItems(ListBox listBox, SettingCategoryMetadata category, bool isRoot, string? parentTag = null) {
            var tag = string.IsNullOrEmpty(parentTag)
                ? category.Name.ToLower().Replace(" ", "-")
                : $"{parentTag}-{category.Name.ToLower().Replace(" ", "-")}";

            var item = new ListBoxItem {
                Content = category.Name,
                Tag = tag,
                Classes = { isRoot ? "NavSection" : "NavSubSection" }
            };

            listBox.Items.Add(item);

            // Add subcategories
            foreach (var subCategory in category.SubCategories.OrderBy(c => c.Order)) {
                AddNavigationItems(listBox, subCategory, isRoot: false, tag);
            }
        }

        /// <summary>
        /// Generate content panels for all categories
        /// </summary>
        public Panel GenerateContentPanels() {
            var panel = new Panel();

            foreach (var category in _metadata.RootCategories) {
                GenerateCategoryPanels(panel, category);
            }

            return panel;
        }

        private void GenerateCategoryPanels(Panel parent, SettingCategoryMetadata category, string? parentTag = null) {
            var tag = string.IsNullOrEmpty(parentTag)
                ? category.Name.ToLower().Replace(" ", "-")
                : $"{parentTag}-{category.Name.ToLower().Replace(" ", "-")}";

            var scrollViewer = new ScrollViewer {
                Name = $"{tag.Replace("-", "")}Panel",
                IsVisible = false
            };

            var stackPanel = new StackPanel {
                Margin = new Thickness(16)
            };

            // Add title
            stackPanel.Children.Add(new TextBlock {
                Text = category.Name,
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Add property controls
            var categoryInstance = GetCategoryInstance(category.Type);
            if (categoryInstance != null) {
                foreach (var property in category.Properties) {
                    var control = GeneratePropertyControl(property, categoryInstance);
                    if (control != null) {
                        stackPanel.Children.Add(control);
                    }
                }
            }

            scrollViewer.Content = stackPanel;
            parent.Children.Add(scrollViewer);

            // Generate panels for subcategories
            foreach (var subCategory in category.SubCategories) {
                GenerateCategoryPanels(parent, subCategory, tag);
            }
        }

        private object? GetCategoryInstance(Type categoryType) {
            return FindInstance(categoryType, _settingsRoot);
        }

        private object? FindInstance(Type targetType, object current) {
            if (current.GetType() == targetType) return current;

            var properties = current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string)
                    && p.GetMethod != null && p.GetIndexParameters().Length == 0);

            foreach (var prop in properties) {
                var child = prop.GetValue(current);
                if (child != null) {
                    var found = FindInstance(targetType, child);
                    if (found != null) return found;
                }
            }

            return null;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
        private Control? GeneratePropertyControl(SettingPropertyMetadata metadata, object instance) {
            var border = new Border {
                Classes = { "SettingGroup" },
                Margin = new Thickness(0, 0, 0, 16)
            };

            var stackPanel = new StackPanel();

            // Create binding path
            var bindingPath = metadata.Property.Name;

            // Label with value display if applicable (skip value display for numeric+range; the NumericUpDown shows the value)
            if (metadata.Range != null || !string.IsNullOrEmpty(metadata.Format)) {
                var dockPanel = new DockPanel();

                if (!string.IsNullOrEmpty(metadata.Format) && metadata.Range == null) {
                    var valueDisplay = new TextBlock {
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    valueDisplay.Bind(TextBlock.TextProperty, new Binding {
                        Source = instance,
                        Path = bindingPath,
                        StringFormat = metadata.Format
                    });
                    DockPanel.SetDock(valueDisplay, Dock.Right);
                    dockPanel.Children.Add(valueDisplay);
                }

                dockPanel.Children.Add(new TextBlock {
                    Classes = { "SettingLabel" },
                    Text = metadata.DisplayName
                });

                stackPanel.Children.Add(dockPanel);
            }
            else {
                stackPanel.Children.Add(new TextBlock {
                    Classes = { "SettingLabel" },
                    Text = metadata.DisplayName
                });
            }

            // Description
            if (!string.IsNullOrEmpty(metadata.Description)) {
                stackPanel.Children.Add(new TextBlock {
                    Classes = { "SettingDescription" },
                    Text = metadata.Description
                });
            }

            // Input control
            var inputControl = CreateInputControl(metadata, instance, bindingPath);
            if (inputControl != null) {
                stackPanel.Children.Add(inputControl);
            }

            border.Child = stackPanel;
            return border;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
        private Control? CreateInputControl(SettingPropertyMetadata metadata, object instance, string bindingPath) {
            var propType = metadata.Property.PropertyType;

            // Boolean -> CheckBox
            if (propType == typeof(bool)) {
                var checkBox = new CheckBox {
                    Content = $"Enable {metadata.DisplayName.ToLower()}"
                };
                checkBox.Bind(ToggleButton.IsCheckedProperty, new Binding {
                    Source = instance,
                    Path = bindingPath,
                    Mode = BindingMode.TwoWay
                });
                return checkBox;
            }

            // Numeric with Range -> Slider + NumericUpDown (slider for quick adjust, numeric box for typing exact value)
            if (metadata.Range != null && IsNumericType(propType)) {
                var r = metadata.Range;
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(80, GridUnitType.Pixel));

                var slider = new Slider {
                    Minimum = r.Minimum,
                    Maximum = r.Maximum,
                    SmallChange = r.SmallChange,
                    LargeChange = r.LargeChange,
                    [Grid.ColumnProperty] = 0
                };
                slider.Bind(Slider.ValueProperty, new Binding {
                    Source = instance,
                    Path = bindingPath,
                    Mode = BindingMode.TwoWay
                });

                var numericUpDown = new NumericUpDown {
                    Minimum = (decimal)r.Minimum,
                    Maximum = (decimal)r.Maximum,
                    Increment = (decimal)r.SmallChange,
                    Margin = new Thickness(8, 0, 0, 0),
                    ShowButtonSpinner = false,
                    FontSize = 12,
                    [Grid.ColumnProperty] = 1
                };
                if (propType == typeof(int) || propType == typeof(long) || propType == typeof(short) || propType == typeof(byte)) {
                    numericUpDown.FormatString = "0";
                }
                else {
                    numericUpDown.FormatString = "G";
                }
                numericUpDown.Bind(NumericUpDown.ValueProperty, new Binding {
                    Source = instance,
                    Path = bindingPath,
                    Mode = BindingMode.TwoWay
                });

                grid.Children.Add(slider);
                grid.Children.Add(numericUpDown);
                return grid;
            }

            // Path -> TextBox with Browse button
            if (metadata.Path != null) {
                var dockPanel = new DockPanel();

                var button = new Button {
                    Width = 80,
                    Content = "Browse...",
                    Margin = new Thickness(8, 0, 0, 0)
                };
                // Note: Button click handler would need to be wired up separately
                DockPanel.SetDock(button, Dock.Right);
                dockPanel.Children.Add(button);

                var textBox = new TextBox {
                    Watermark = metadata.Path.DialogTitle ?? "Select path..."
                };
                textBox.Bind(TextBox.TextProperty, new Binding {
                    Source = instance,
                    Path = bindingPath,
                    Mode = BindingMode.TwoWay
                });
                dockPanel.Children.Add(textBox);

                return dockPanel;
            }

            // LogLevel -> ComboBox
            if (propType == typeof(LogLevel)) {
                var comboBox = new ComboBox {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = Enum.GetValues<LogLevel>()
                };
                comboBox.Bind(SelectingItemsControl.SelectedItemProperty, new Binding {
                    Source = instance,
                    Path = bindingPath,
                    Mode = BindingMode.TwoWay
                });
                return comboBox;
            }

            // Vector3 -> ColorPicker
            if (propType == typeof(Vector3)) {
                var colorPicker = new Avalonia.Controls.ColorPicker {
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                var converter = new Vector3ToColorConverter();
                colorPicker.Bind(Avalonia.Controls.ColorPicker.ColorProperty, new Binding {
                    Source = instance,
                    Path = bindingPath,
                    Mode = BindingMode.TwoWay,
                    Converter = converter
                });
                return colorPicker;
            }

            // String -> TextBox
            if (propType == typeof(string)) {
                var textBox = new TextBox();
                textBox.Bind(TextBox.TextProperty, new Binding {
                    Source = instance,
                    Path = bindingPath,
                    Mode = BindingMode.TwoWay
                });
                return textBox;
            }

            // Default: TextBox for other types
            var defaultTextBox = new TextBox();
            defaultTextBox.Bind(TextBox.TextProperty, new Binding {
                Source = instance,
                Path = bindingPath,
                Mode = BindingMode.TwoWay
            });
            return defaultTextBox;
        }

        private bool IsNumericType(Type type) {
            return type == typeof(int) || type == typeof(long) ||
                   type == typeof(float) || type == typeof(double) ||
                   type == typeof(decimal) || type == typeof(short) ||
                   type == typeof(byte);
        }
    }
}
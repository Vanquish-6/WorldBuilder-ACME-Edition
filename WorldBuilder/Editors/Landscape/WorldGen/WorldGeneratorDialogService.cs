using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public static class WorldGeneratorDialogService {
        private static readonly (string label, int w, int h)[] SizePresets = {
            ("Small Island (10\u00d710)", 10, 10),
            ("Region (20\u00d720)", 20, 20),
            ("Continent (50\u00d750)", 50, 50),
            ("Large Continent (100\u00d7100)", 100, 100),
            ("Full World (254\u00d7254)", 254, 254),
            ("Custom", 0, 0)
        };

        private const int MapDisplaySize = 300;
        private const int WorldSize = 254;
        private const double MapScale = (double)MapDisplaySize / WorldSize;

        public static async Task<WorldGeneratorParams?> ShowDialog(byte[]? minimapHeights = null) {
            WorldGeneratorParams? result = null;

            int selX = 100, selY = 100, selW = 20, selH = 20;
            bool isFullWorld = false;

            var mapBitmap = BuildMinimapBitmap(minimapHeights);
            var mapImage = new Image {
                Source = mapBitmap,
                Width = MapDisplaySize,
                Height = MapDisplaySize,
                Stretch = Stretch.Fill
            };
            Canvas.SetLeft(mapImage, 0);
            Canvas.SetTop(mapImage, 0);

            var selectionRect = new Border {
                Background = new SolidColorBrush(Color.FromArgb(45, 80, 160, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 100, 170, 255)),
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(1)
            };

            var coordLabel = new TextBlock {
                FontSize = 11, Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var mapCanvas = new Canvas {
                Width = MapDisplaySize,
                Height = MapDisplaySize,
                Background = Brushes.Transparent,
                ClipToBounds = true,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            mapCanvas.Children.Add(mapImage);
            mapCanvas.Children.Add(selectionRect);

            void UpdateSelectionVisual() {
                if (isFullWorld) {
                    Canvas.SetLeft(selectionRect, 0);
                    Canvas.SetTop(selectionRect, 0);
                    selectionRect.Width = MapDisplaySize;
                    selectionRect.Height = MapDisplaySize;
                    coordLabel.Text = "Full World (254\u00d7254)";
                    return;
                }
                selX = Math.Clamp(selX, 0, Math.Max(WorldSize - selW, 0));
                selY = Math.Clamp(selY, 0, Math.Max(WorldSize - selH, 0));
                Canvas.SetLeft(selectionRect, selX * MapScale);
                Canvas.SetTop(selectionRect, (WorldSize - selY - selH) * MapScale);
                selectionRect.Width = Math.Max(selW * MapScale, 2);
                selectionRect.Height = Math.Max(selH * MapScale, 2);
                coordLabel.Text = $"Landblocks ({selX}, {selY}) to ({selX + selW - 1}, {selY + selH - 1})  \u2014  {selW}\u00d7{selH}";
            }

            bool dragging = false;
            double dragOfsX = 0, dragOfsY = 0;

            mapCanvas.PointerPressed += (s, e) => {
                if (isFullWorld) return;
                var pos = e.GetPosition(mapCanvas);
                double rectL = selX * MapScale;
                double rectT = (WorldSize - selY - selH) * MapScale;
                double rectR = rectL + selW * MapScale;
                double rectB = rectT + selH * MapScale;

                if (pos.X >= rectL && pos.X <= rectR && pos.Y >= rectT && pos.Y <= rectB) {
                    dragOfsX = pos.X - rectL;
                    dragOfsY = pos.Y - rectT;
                } else {
                    int lbX = Math.Clamp((int)(pos.X / MapScale), 0, WorldSize - 1);
                    int lbY = Math.Clamp(WorldSize - 1 - (int)(pos.Y / MapScale), 0, WorldSize - 1);
                    selX = lbX - selW / 2;
                    selY = lbY - selH / 2;
                    UpdateSelectionVisual();
                    dragOfsX = selW * MapScale / 2;
                    dragOfsY = selH * MapScale / 2;
                }
                dragging = true;
                e.Pointer.Capture(mapCanvas);
                e.Handled = true;
            };

            mapCanvas.PointerMoved += (s, e) => {
                if (!dragging || isFullWorld) return;
                var pos = e.GetPosition(mapCanvas);
                selX = (int)((pos.X - dragOfsX) / MapScale);
                int bitmapTopRow = (int)((pos.Y - dragOfsY) / MapScale);
                selY = WorldSize - bitmapTopRow - selH;
                UpdateSelectionVisual();
                e.Handled = true;
            };

            mapCanvas.PointerReleased += (s, e) => {
                if (dragging) {
                    dragging = false;
                    e.Pointer.Capture(null);
                }
            };

            var seedBox = new TextBox { Text = "0", Width = 120, FontSize = 12 };
            var randomizeBtn = new Button {
                Content = "\u21bb", FontSize = 14, Padding = new Thickness(6, 2),
                Command = new RelayCommand(() => seedBox.Text = new Random().Next(1, 999999).ToString())
            };

            var sizePresetCombo = new ComboBox {
                FontSize = 12, Width = 200,
                ItemsSource = Array.ConvertAll(SizePresets, p => p.label),
                SelectedIndex = 1
            };

            var widthBox = new NumericUpDown { Value = 20, Minimum = 2, Maximum = 254, Width = 90, FontSize = 12 };
            var heightBox = new NumericUpDown { Value = 20, Minimum = 2, Maximum = 254, Width = 90, FontSize = 12 };
            var customSizePanel = new StackPanel {
                Spacing = 6, IsVisible = false,
                Children = { MakeRow("Width:", widthBox), MakeRow("Height:", heightBox) }
            };

            widthBox.ValueChanged += (_, _) => {
                if (!isFullWorld) { selW = (int)(widthBox.Value ?? 20); UpdateSelectionVisual(); }
            };
            heightBox.ValueChanged += (_, _) => {
                if (!isFullWorld) { selH = (int)(heightBox.Value ?? 20); UpdateSelectionVisual(); }
            };

            sizePresetCombo.SelectionChanged += (_, _) => {
                int idx = sizePresetCombo.SelectedIndex;
                if (idx < 0 || idx >= SizePresets.Length) return;
                var preset = SizePresets[idx];
                isFullWorld = preset.w == 254;
                customSizePanel.IsVisible = preset.w == 0;
                mapCanvas.Cursor = isFullWorld ? new Cursor(StandardCursorType.Arrow) : new Cursor(StandardCursorType.Hand);

                if (isFullWorld) {
                    selX = 0; selY = 0; selW = 254; selH = 254;
                } else if (preset.w != 0) {
                    int cx = selX + selW / 2;
                    int cy = selY + selH / 2;
                    selW = preset.w;
                    selH = preset.h;
                    selX = cx - selW / 2;
                    selY = cy - selH / 2;
                } else {
                    selW = (int)(widthBox.Value ?? 20);
                    selH = (int)(heightBox.Value ?? 20);
                }
                UpdateSelectionVisual();
            };

            var continentCountBox = new NumericUpDown { Value = 1, Minimum = 0, Maximum = 12, Width = 90, FontSize = 12 };
            var islandCountBox = new NumericUpDown { Value = 0, Minimum = 0, Maximum = 20, Width = 90, FontSize = 12 };
            var landCoverageSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, Width = 180 };
            var roughnessSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, Width = 180 };
            var seaLevelBox = new NumericUpDown { Value = 20, Minimum = 5, Maximum = 50, Width = 90, FontSize = 12 };
            var mountainScaleSlider = new Slider { Minimum = 0.3, Maximum = 2.0, Value = 1.0, Width = 180 };
            var townCountBox = new NumericUpDown { Value = 5, Minimum = 0, Maximum = 50, Width = 90, FontSize = 12 };
            var roadsCheck = new CheckBox { Content = "Generate roads", IsChecked = true, FontSize = 12 };
            var buildingsCheck = new CheckBox { Content = "Generate buildings", IsChecked = true, FontSize = 12 };
            var retailTownBuildingsCheck = new CheckBox {
                Content = "Retail town buildings only (from loaded cell.dat)",
                IsChecked = false,
                FontSize = 12,
                Opacity = 0.95
            };
            void SyncRetailCheckEnabled() {
                retailTownBuildingsCheck.IsEnabled = buildingsCheck.IsChecked == true;
                if (!retailTownBuildingsCheck.IsEnabled)
                    retailTownBuildingsCheck.IsChecked = false;
            }
            buildingsCheck.Click += (_, _) => SyncRetailCheckEnabled();
            SyncRetailCheckEnabled();

            UpdateSelectionVisual();

            var mapContainer = new StackPanel {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = {
                    new TextBlock {
                        Text = "Click or drag to position the region",
                        FontSize = 11, Opacity = 0.5,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new Border {
                        BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                        BorderThickness = new Thickness(1),
                        Child = mapCanvas
                    },
                    coordLabel
                }
            };

            var panel = new StackPanel {
                Margin = new Thickness(24),
                Spacing = 10,
                Width = 460,
                Children = {
                    new TextBlock { Text = "Generate World", FontSize = 18, FontWeight = FontWeight.Bold },
                    new TextBlock {
                        Text = "Procedurally generate terrain with biomes, towns, and roads.\nThis will overwrite all existing terrain, objects, and dungeons in the region.",
                        FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap
                    },
                    new Separator { Margin = new Thickness(0, 4) },

                    MakeRow("Seed:", new StackPanel {
                        Orientation = Orientation.Horizontal, Spacing = 4,
                        Children = { seedBox, randomizeBtn }
                    }),
                    MakeRow("Size:", sizePresetCombo),
                    customSizePanel,
                    mapContainer,

                    new Separator { Margin = new Thickness(0, 4) },
                    new TextBlock { Text = "Landmasses", FontSize = 14, FontWeight = FontWeight.SemiBold },
                    MakeRow("Continents:", continentCountBox),
                    MakeRow("Islands:", islandCountBox),
                    MakeRow("Land Coverage:", landCoverageSlider),
                    MakeRow("Roughness:", roughnessSlider),
                    MakeRow("Sea Level:", seaLevelBox),
                    MakeRow("Mountain Scale:", mountainScaleSlider),

                    new Separator { Margin = new Thickness(0, 4) },
                    new TextBlock { Text = "Civilization", FontSize = 14, FontWeight = FontWeight.SemiBold },
                    MakeRow("Town Count:", townCountBox),
                    roadsCheck,
                    buildingsCheck,
                    new TextBlock {
                        Text = "Uses landblocks with several buildings + outdoor clutter, like retail towns. Requires retail (or full) cell.dat.",
                        FontSize = 10, Opacity = 0.55, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 0)
                    },
                    retailTownBuildingsCheck,

                    new Separator { Margin = new Thickness(0, 4) },
                    new StackPanel {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("MainDialogHost"))
                            },
                            new Button {
                                Content = "Generate",
                                Command = new RelayCommand(() => {
                                    int.TryParse(seedBox.Text, out int seed);
                                    result = new WorldGeneratorParams {
                                        Seed = seed,
                                        FullWorld = isFullWorld,
                                        StartX = selX,
                                        StartY = selY,
                                        Width = selW,
                                        Height = selH,
                                        ContinentCount = (int)(continentCountBox.Value ?? 1),
                                        IslandCount = (int)(islandCountBox.Value ?? 0),
                                        LandCoverage = (float)landCoverageSlider.Value,
                                        Roughness = (float)roughnessSlider.Value,
                                        SeaLevelIndex = (int)(seaLevelBox.Value ?? 20),
                                        MountainScale = (float)mountainScaleSlider.Value,
                                        TownCount = (int)(townCountBox.Value ?? 5),
                                        GenerateRoads = roadsCheck.IsChecked == true,
                                        GenerateBuildings = buildingsCheck.IsChecked == true,
                                        RetailTownBuildingsOnly = retailTownBuildingsCheck.IsChecked == true
                                    };
                                    DialogHost.Close("MainDialogHost");
                                })
                            }
                        }
                    }
                }
            };

            var scrollWrapper = new ScrollViewer {
                Content = panel,
                MaxHeight = 780,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };

            await DialogHost.Show(scrollWrapper, "MainDialogHost");
            return result;
        }

        private static WriteableBitmap BuildMinimapBitmap(byte[]? heights) {
            var bitmap = new WriteableBitmap(
                new PixelSize(WorldSize, WorldSize),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var fb = bitmap.Lock();
            unsafe {
                var ptr = (byte*)fb.Address;
                int stride = fb.RowBytes;
                for (int row = 0; row < WorldSize; row++) {
                    for (int col = 0; col < WorldSize; col++) {
                        int lbX = col;
                        int lbY = WorldSize - 1 - row;
                        byte h = heights != null ? heights[lbY * WorldSize + lbX] : (byte)0;
                        var (r, g, b) = HeightToColor(h);
                        int ofs = row * stride + col * 4;
                        ptr[ofs + 0] = b;
                        ptr[ofs + 1] = g;
                        ptr[ofs + 2] = r;
                        ptr[ofs + 3] = 255;
                    }
                }
            }
            return bitmap;
        }

        private static (byte r, byte g, byte b) HeightToColor(byte h) {
            if (h < 12) return (25, 50, 100);
            if (h < 20) return (40, 80, 140);
            if (h < 26) return (170, 160, 110);
            if (h < 45) return (75, 135, 55);
            if (h < 75) return (55, 115, 40);
            if (h < 110) return (95, 85, 55);
            if (h < 160) return (125, 120, 110);
            if (h < 210) return (155, 150, 145);
            return (205, 205, 210);
        }

        private static StackPanel MakeRow(string label, Control control) {
            return new StackPanel {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = {
                    new TextBlock {
                        Text = label, FontSize = 12, Width = 120,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    control
                }
            };
        }
    }
}

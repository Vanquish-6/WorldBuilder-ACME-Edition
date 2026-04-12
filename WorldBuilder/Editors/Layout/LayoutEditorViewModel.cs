using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Services;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Layout {
    public partial class LayoutEditorViewModel : ViewModelBase {
        private IDatReaderWriter? _dats;
        private Project? _project;
        private LayoutDatDocument? _layoutDoc;
        private PortalDatDocument? _portalDoc;
        private uint[] _allLayoutIds = Array.Empty<uint>();

        [ObservableProperty] private string _statusText = "No layouts loaded";
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private ObservableCollection<LayoutListItem> _filteredLayouts = new();
        [ObservableProperty] private LayoutListItem? _selectedLayout;
        [ObservableProperty] private LayoutDetailViewModel? _selectedDetail;

        public WorldBuilderSettings Settings { get; }

        /// <summary>DAT access for layout preview textures (same client files as the open project).</summary>
        public IDatReaderWriter? Dats => _dats;

        private readonly TextureImportService? _textureImport;

        public LayoutEditorViewModel(WorldBuilderSettings settings, TextureImportService? textureImport = null) {
            Settings = settings;
            _textureImport = textureImport;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _layoutDoc = project.DocumentManager.GetOrCreateDocumentAsync<LayoutDatDocument>(LayoutDatDocument.DocumentId).GetAwaiter().GetResult();
            _portalDoc = project.DocumentManager.GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId).GetAwaiter().GetResult();
            LoadLayoutIds();
            SaveLayoutToProjectCommand.NotifyCanExecuteChanged();
        }

        private void LoadLayoutIds() {
            if (_dats == null) return;

            try {
                _allLayoutIds = _dats.Dats.GetAllIdsOfType<LayoutDesc>().OrderBy(id => id).ToArray();
                StatusText = $"Found {_allLayoutIds.Length} UI layouts";
            }
            catch (Exception ex) {
                StatusText = $"Failed to load layout IDs: {ex.Message}";
                Console.WriteLine($"[Layout] Error: {ex}");
            }

            ApplyFilter();
        }

        partial void OnSearchTextChanged(string value) {
            ApplyFilter();
        }

        private void ApplyFilter() {
            var query = SearchText?.Trim().ToUpperInvariant() ?? "";
            IEnumerable<uint> results = _allLayoutIds;

            if (!string.IsNullOrEmpty(query)) {
                var hex = query.TrimStart('0', 'X');
                results = results.Where(id => id.ToString("X8").Contains(hex));
            }

            var items = results.Take(500)
                .Select(id => new LayoutListItem(id, _layoutDoc != null && _layoutDoc.HasStoredLayout(id)))
                .ToList();

            FilteredLayouts = new ObservableCollection<LayoutListItem>(items);
        }

        partial void OnSelectedLayoutChanged(LayoutListItem? value) {
            if (value == null || _dats == null) {
                SelectedDetail = null;
                return;
            }

            try {
                LayoutDesc? source = null;
                bool fromProject = false;
                if (_layoutDoc != null && _layoutDoc.TryGetLayout(value.Id, out var overlay) && overlay != null) {
                    source = overlay;
                    fromProject = true;
                }
                else if (_dats.TryGet<LayoutDesc>(value.Id, out var fromDat) && fromDat != null) {
                    source = fromDat;
                }

                if (source != null) {
                    var localDb = _dats.Dats.Local ?? throw new InvalidOperationException("DAT collection has no local database (client_local).");
                    var working = LayoutDescBinary.Clone(source, value.Id, localDb);
                    SelectedDetail = new LayoutDetailViewModel(value.Id, working, _dats, _textureImport, _portalDoc);
                    _ = SelectedDetail.LoadPreviewTexturesAsync();
                    var src = fromProject ? "project override" : "client DAT";
                    var stringTableCount = 0;
                    try {
                        stringTableCount = _dats.Dats.GetAllIdsOfType<StringTable>().Count();
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[Layout] StringTable index: {ex.Message}");
                    }
                    StatusText = $"Layout 0x{value.Id:X8} ({src}): {working.Width}x{working.Height}, {working.Elements.Count} elements — {stringTableCount} StringTable(s) in local";
                }
                else {
                    SelectedDetail = null;
                    StatusText = $"Failed to read layout 0x{value.Id:X8}";
                }
            }
            catch (Exception ex) {
                SelectedDetail = null;
                StatusText = $"Error reading layout: {ex.Message}";
                Console.WriteLine($"[Layout] Error reading 0x{value.Id:X8}: {ex}");
            }
        }

        partial void OnSelectedDetailChanged(LayoutDetailViewModel? value) {
            SaveLayoutToProjectCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanSaveLayoutToProject))]
        private void SaveLayoutToProject() {
            if (SelectedDetail == null || _layoutDoc == null) {
                StatusText = "Nothing to save";
                return;
            }

            _layoutDoc.SetLayout(SelectedDetail.LayoutId, SelectedDetail.WorkingLayout);
            StatusText = $"Saved layout {SelectedDetail.LayoutIdHex} to project — export DATs to write client_local";
            ApplyFilter();
        }

        private bool CanSaveLayoutToProject() => SelectedDetail != null && _layoutDoc != null;
    }

    public class LayoutListItem {
        public uint Id { get; }
        public string DisplayId { get; }
        public bool HasProjectOverride { get; }

        public LayoutListItem(uint id, bool hasProjectOverride = false) {
            Id = id;
            HasProjectOverride = hasProjectOverride;
            DisplayId = hasProjectOverride ? $"0x{id:X8}  *" : $"0x{id:X8}";
        }

        public override string ToString() => DisplayId;
    }

    public partial class LayoutDetailViewModel : ObservableObject {
        private readonly IDatReaderWriter? _dats;
        private readonly DatCollection? _stringDats;
        private readonly TextureImportService? _textureImport;
        private readonly PortalDatDocument? _portalDoc;

        public uint LayoutId { get; }
        public string LayoutIdHex { get; }

        public uint Width => WorkingLayout.Width;
        public uint Height => WorkingLayout.Height;
        public int ElementCount => WorkingLayout.Elements.Count;

        /// <summary>Mutable working copy; persist with <see cref="LayoutDatDocument.SetLayout"/> / export.</summary>
        public LayoutDesc WorkingLayout { get; }

        public ObservableCollection<ElementTreeNode> RootElements { get; } = new();

        [ObservableProperty] private ElementTreeNode? _selectedElement;

        /// <summary>Per <see cref="ElementTreeNode.ElementId"/>, decoded surface preview (best effort).</summary>
        [ObservableProperty] private IReadOnlyDictionary<uint, WriteableBitmap?>? _elementTextures;

        [ObservableProperty] private string _elementGeomXText = "0";
        [ObservableProperty] private string _elementGeomYText = "0";
        [ObservableProperty] private string _elementGeomWidthText = "0";
        [ObservableProperty] private string _elementGeomHeightText = "0";
        [ObservableProperty] private string _elementGeomZText = "0";
        [ObservableProperty] private string _elementGeomLeftText = "0";
        [ObservableProperty] private string _elementGeomTopText = "0";
        [ObservableProperty] private string _elementGeomRightText = "0";
        [ObservableProperty] private string _elementGeomBottomText = "0";
        [ObservableProperty] private string _elementReadOrderText = "0";
        [ObservableProperty] private string _primarySurfaceHexText = "";
        [ObservableProperty] private string _newTemplateImageHexText = "";
        [ObservableProperty] private string _layoutWidthText = "";
        [ObservableProperty] private string _layoutHeightText = "";

        public LayoutDetailViewModel(uint id, LayoutDesc workingLayout, IDatReaderWriter? dats, TextureImportService? textureImport = null, PortalDatDocument? portalDoc = null) {
            _dats = dats;
            _stringDats = dats?.Dats;
            var stringTableIds = _stringDats?.GetAllIdsOfType<StringTable>().ToArray();
            _textureImport = textureImport;
            _portalDoc = portalDoc;
            WorkingLayout = workingLayout;
            workingLayout.Id = id;
            LayoutId = id;
            LayoutIdHex = $"0x{id:X8}";
            _layoutWidthText = workingLayout.Width.ToString();
            _layoutHeightText = workingLayout.Height.ToString();

            foreach (var kvp in workingLayout.Elements.OrderBy(e => e.Value.ReadOrder)) {
                RootElements.Add(new ElementTreeNode(kvp.Value, _stringDats, stringTableIds, id));
            }
        }

        static TopLevel? GetTopLevel() {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        partial void OnSelectedElementChanged(ElementTreeNode? value) {
            ReplaceUiRenderSurfaceCommand.NotifyCanExecuteChanged();

            if (value == null) {
                PrimarySurfaceHexText = "";
                return;
            }

            ElementGeomXText = value.Source.X.ToString();
            ElementGeomYText = value.Source.Y.ToString();
            ElementGeomWidthText = value.Source.Width.ToString();
            ElementGeomHeightText = value.Source.Height.ToString();
            ElementGeomZText = value.Source.ZLevel.ToString();
            ElementGeomLeftText = value.Source.LeftEdge.ToString();
            ElementGeomTopText = value.Source.TopEdge.ToString();
            ElementGeomRightText = value.Source.RightEdge.ToString();
            ElementGeomBottomText = value.Source.BottomEdge.ToString();
            ElementReadOrderText = value.Source.ReadOrder.ToString();

            var sid = LayoutMediaHelper.TryPrimarySurfaceForElement(value.Source);
            PrimarySurfaceHexText = sid.HasValue ? $"0x{sid.Value:X8}" : "";
        }

        bool CanReplaceUiRenderSurface() {
            if (SelectedElement == null || _textureImport == null || _dats == null || _portalDoc == null) return false;
            var sid = LayoutMediaHelper.TryPrimarySurfaceForElement(SelectedElement.Source);
            return sid.HasValue && TryUnpackRenderSurface(_dats, sid.Value, out _);
        }

        /// <summary>DatReaderWriter can throw from <see cref="RenderSurface.Unpack"/> on corrupt entries — never call raw TryGet from RelayCommand CanExecute.</summary>
        static bool TryUnpackRenderSurface(IDatReaderWriter dats, uint id, out RenderSurface? rs) {
            rs = null;
            try {
                return dats.TryGet<RenderSurface>(id, out rs) && rs != null;
            }
            catch (Exception ex) {
                Console.WriteLine($"[Layout] RenderSurface 0x{id:X8} read failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanReplaceUiRenderSurface))]
        private async Task ReplaceUiRenderSurfaceAsync() {
            if (SelectedElement == null || _textureImport == null || _dats == null || _portalDoc == null) return;
            var sid = LayoutMediaHelper.TryPrimarySurfaceForElement(SelectedElement.Source);
            if (!sid.HasValue || !TryUnpackRenderSurface(_dats, sid.Value, out _)) {
                Console.WriteLine("[Layout] Replace texture: primary image id is not a portal RenderSurface (use hex + Apply with a 0x06…… id, or a raw id that resolves to RenderSurface).");
                return;
            }

            var top = GetTopLevel();
            if (top == null) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Replace RenderSurface pixels (image resized to existing DAT width/height)",
                AllowMultiple = false,
                FileTypeFilter = new[] {
                    new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } }
                }
            });

            if (files.Count == 0) return;
            var localPath = files[0].TryGetLocalPath();
            if (localPath == null) return;

            try {
                if (!_textureImport.TryOverwriteUiRenderSurface(localPath, sid.Value, _portalDoc)) {
                    return;
                }

                await LoadPreviewTexturesAsync();

                var filePreview = await Dispatcher.UIThread.InvokeAsync(() =>
                    TextureImportService.TryCreateWriteableBitmapPreview(localPath, 256));
                if (filePreview != null && SelectedElement != null) {
                    var merged = new Dictionary<uint, WriteableBitmap?>();
                    if (ElementTextures != null) {
                        foreach (var kv in ElementTextures)
                            merged[kv.Key] = kv.Value;
                    }

                    merged[SelectedElement.ElementId] = filePreview;
                    ElementTextures = merged;
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[Layout] Replace RenderSurface failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ApplyLayoutSize() {
            if (!uint.TryParse(LayoutWidthText, out var w)) return;
            if (!uint.TryParse(LayoutHeightText, out var h)) return;
            if (w == 0 || h == 0) return;
            WorkingLayout.Width = w;
            WorkingLayout.Height = h;
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(Height));
        }

        [RelayCommand]
        private void ApplyElementGeometry() {
            if (SelectedElement == null) return;
            var el = SelectedElement;

            if (!uint.TryParse(ElementGeomXText, out var x)) return;
            if (!uint.TryParse(ElementGeomYText, out var y)) return;
            if (!uint.TryParse(ElementGeomWidthText, out var w)) return;
            if (!uint.TryParse(ElementGeomHeightText, out var h)) return;
            if (!uint.TryParse(ElementGeomZText, out var z)) return;
            if (!uint.TryParse(ElementGeomLeftText, out var le)) return;
            if (!uint.TryParse(ElementGeomTopText, out var te)) return;
            if (!uint.TryParse(ElementGeomRightText, out var re)) return;
            if (!uint.TryParse(ElementGeomBottomText, out var be)) return;
            if (!uint.TryParse(ElementReadOrderText, out var ro)) return;

            LayoutElementEditHelper.EnsureGeometryIncorporation(el.Source);
            el.Source.X = x;
            el.Source.Y = y;
            el.Source.Width = w;
            el.Source.Height = h;
            el.Source.ZLevel = z;
            el.Source.LeftEdge = le;
            el.Source.TopEdge = te;
            el.Source.RightEdge = re;
            el.Source.BottomEdge = be;
            el.Source.ReadOrder = ro;
            el.NotifySourceMutated();
        }

        [RelayCommand]
        private void ApplyPrimarySurface() {
            if (SelectedElement == null) return;
            if (!LayoutElementEditHelper.TryParseDatHex(PrimarySurfaceHexText, out var id)) return;
            LayoutElementEditHelper.TrySetPrimaryImageSurface(SelectedElement.Source, id);
            SelectedElement.RefreshStateRows();
            ReplaceUiRenderSurfaceCommand.NotifyCanExecuteChanged();
            _ = LoadPreviewTexturesAsync();
        }

        [RelayCommand]
        private void AddTemplateImage() {
            if (SelectedElement == null) return;
            if (!LayoutElementEditHelper.TryParseDatHex(NewTemplateImageHexText, out var id)) return;
            LayoutElementEditHelper.TryAddTemplateImage(SelectedElement.Source, id);
            SelectedElement.RefreshStateRows();
            NewTemplateImageHexText = "";
            _ = LoadPreviewTexturesAsync();
        }

        public async Task LoadPreviewTexturesAsync() {
            if (_dats == null) return;

            var dats = _dats;
            var nodes = new List<ElementTreeNode>();
            foreach (var r in RootElements)
                CollectNodes(r, nodes);

            var map = await Task.Run(() => {
                var d = new Dictionary<uint, WriteableBitmap?>();
                const int maxEdge = 256;
                foreach (var node in nodes) {
                    var sid = LayoutMediaHelper.TryPrimarySurfaceForElement(node.Source);
                    if (!sid.HasValue) continue;
                    var bmp = SurfaceBrowserViewModel.DecodeSurfaceToBitmap(sid.Value, dats, maxEdge);
                    if (bmp != null)
                        d[node.ElementId] = bmp;
                }
                return (IReadOnlyDictionary<uint, WriteableBitmap?>)d;
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => ElementTextures = map);
        }

        static void CollectNodes(ElementTreeNode n, List<ElementTreeNode> acc) {
            acc.Add(n);
            foreach (var c in n.Children)
                CollectNodes(c, acc);
        }
    }

    public partial class ElementTreeNode : ObservableObject {
        private readonly DatCollection? _stringDats;
        private readonly uint[]? _stringTableIds;
        /// <summary>DID of the layout file open in the editor (for string resolution through <see cref="ElementDesc.BaseLayoutId"/>).</summary>
        private readonly uint _containingLayoutFileId;

        public ElementDesc Source { get; }

        public uint ElementId => Source.ElementId;
        public string DisplayId => $"0x{Source.ElementId:X}";
        public uint Type => Source.Type;
        public string TypeHex => Source.Type != 0 ? $"0x{Source.Type:X8}" : "inherit";

        /// <summary>Concrete type if <see cref="ElementDesc.Type"/> is non-zero; otherwise first non-zero type along base-layout chain, or <c>inherit</c>.</summary>
        public string TypeLabel {
            get {
                if (Source.Type != 0)
                    return TypeHex;
                var eff = LayoutUiStringResolver.TryResolveEffectiveType(_stringDats, Source, _containingLayoutFileId);
                return eff is uint t ? $"inherit→0x{t:X8}" : TypeHex;
            }
        }

        /// <summary>Identity panel: explains raw vs resolved type for thin overrides.</summary>
        public string EffectiveTypeHint {
            get {
                if (Source.Type != 0)
                    return $"0x{Source.Type:X8} (defined on this element)";
                var eff = LayoutUiStringResolver.TryResolveEffectiveType(_stringDats, Source, _containingLayoutFileId);
                if (eff is uint t)
                    return $"0x{t:X8} (resolved via BaseLayoutId → BaseElement chain)";
                return "Not resolved — Type stays 0 with no usable base chain, or a cycle in bases.";
            }
        }

        public uint X => Source.X;
        public uint Y => Source.Y;
        public uint Width => Source.Width;
        public uint Height => Source.Height;
        public uint ZLevel => Source.ZLevel;
        public uint BaseElement => Source.BaseElement;
        public uint BaseLayoutId => Source.BaseLayoutId;
        public string BaseLayoutHex => Source.BaseLayoutId != 0 ? $"0x{Source.BaseLayoutId:X8}" : "none";
        public uint LeftEdge => Source.LeftEdge;
        public uint TopEdge => Source.TopEdge;
        public uint RightEdge => Source.RightEdge;
        public uint BottomEdge => Source.BottomEdge;
        public uint ReadOrder => Source.ReadOrder;
        public int StatesCount => Source.States?.Count ?? 0;
        public int ChildrenCount => Source.Children?.Count ?? 0;

        public string DefaultStateHex => $"0x{(uint)Source.DefaultState:X8}";

        public string Summary => $"#{DisplayId} {TypeHex} ({Source.Width}x{Source.Height})";

        /// <summary>Resolved from the first <see cref="MediaDescMessage"/> on the element’s representative UI state (English string tables).</summary>
        public string Caption { get; private set; }

        /// <summary>Caption for the panel, or an explanation when nothing resolved (bound in Identity so empty is never a blank gap).</summary>
        public string CaptionDisplay => string.IsNullOrWhiteSpace(Caption)
            ? "No English UI string resolved. We try this element, then <BaseLayoutId>/<BaseElement> ancestors, using MediaDescMessage and StringInfo on each. If still empty, the control may be a plain container or ids are missing from StringTables."
            : Caption;

        /// <summary>Tree row: hex id, optional string-table caption, effective type, Z/read order, size.</summary>
        public string TreeLine {
            get {
                var cap = string.IsNullOrWhiteSpace(Caption) ? "" : $" — {Caption}";
                return $"{DisplayId}{cap}  {TypeLabel}  Z {ZLevel}  RO {ReadOrder}  ({Source.Width}×{Source.Height})";
            }
        }

        /// <summary>Hover on hierarchy row: ids, stacking, bases, primary image surface, resolved caption.</summary>
        public string HierarchyTooltip {
            get {
                var surf = LayoutMediaHelper.TryPrimarySurfaceForElement(Source);
                var surfLine = surf is uint sid ? $"0x{sid:X8}" : "(none — no MediaDescImage in default/template/states)";
                return
                    $"Element ID: {DisplayId}\n" +
                    $"Raw type: {TypeHex}\n" +
                    $"Effective type: {TypeLabel}\n" +
                    $"Primary surface: {surfLine}\n" +
                    $"Size: {Source.Width}×{Source.Height}  Position: X={X} Y={Y}\n" +
                    $"Z-order: {ZLevel}  Read order: {ReadOrder}\n" +
                    $"Base: layout {BaseLayoutHex}  element {(BaseElement != 0 ? $"0x{BaseElement:X}" : "none")}\n" +
                    $"UI text: {(string.IsNullOrWhiteSpace(Caption) ? "(none resolved)" : Caption)}";
            }
        }

        public ObservableCollection<LayoutStateRow> StateRows { get; } = new();

        public ObservableCollection<ElementTreeNode> Children { get; } = new();

        public ElementTreeNode(ElementDesc element, DatCollection? stringDats, uint[]? stringTableIds, uint containingLayoutFileId) {
            Source = element;
            _stringDats = stringDats;
            _stringTableIds = stringTableIds;
            _containingLayoutFileId = containingLayoutFileId;
            Caption = LayoutUiStringResolver.TryGetElementCaption(stringDats, element, stringTableIds, containingLayoutFileId) ?? "";
            LayoutMediaHelper.PopulateStateRows(element, StateRows, stringDats, stringTableIds);

            if (element.Children != null) {
                foreach (var kvp in element.Children.OrderBy(c => c.Value.ReadOrder)) {
                    Children.Add(new ElementTreeNode(kvp.Value, stringDats, stringTableIds, containingLayoutFileId));
                }
            }
        }

        public void SetPosition(uint x, uint y) {
            LayoutElementEditHelper.EnsureGeometryIncorporation(Source);
            if (Source.X == x && Source.Y == y) return;
            Source.X = x;
            Source.Y = y;
            NotifySourceMutated();
        }

        public void SetBounds(uint x, uint y, uint width, uint height) {
            LayoutElementEditHelper.EnsureGeometryIncorporation(Source);
            width = System.Math.Max(1u, width);
            height = System.Math.Max(1u, height);
            if (Source.X == x && Source.Y == y && Source.Width == width && Source.Height == height) return;
            Source.X = x;
            Source.Y = y;
            Source.Width = width;
            Source.Height = height;
            NotifySourceMutated();
        }

        public void NotifySourceMutated() {
            OnPropertyChanged(nameof(X));
            OnPropertyChanged(nameof(Y));
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(Height));
            OnPropertyChanged(nameof(ZLevel));
            OnPropertyChanged(nameof(LeftEdge));
            OnPropertyChanged(nameof(TopEdge));
            OnPropertyChanged(nameof(RightEdge));
            OnPropertyChanged(nameof(BottomEdge));
            OnPropertyChanged(nameof(ReadOrder));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(TreeLine));
            OnPropertyChanged(nameof(TypeLabel));
            OnPropertyChanged(nameof(EffectiveTypeHint));
            OnPropertyChanged(nameof(HierarchyTooltip));
            OnPropertyChanged(nameof(StatesCount));
            OnPropertyChanged(nameof(ChildrenCount));
        }

        public void RefreshStateRows() {
            StateRows.Clear();
            LayoutMediaHelper.PopulateStateRows(Source, StateRows, _stringDats, _stringTableIds);
            var newCap = LayoutUiStringResolver.TryGetElementCaption(_stringDats, Source, _stringTableIds, _containingLayoutFileId) ?? "";
            if (newCap != Caption) {
                Caption = newCap;
                OnPropertyChanged(nameof(Caption));
                OnPropertyChanged(nameof(CaptionDisplay));
                OnPropertyChanged(nameof(TreeLine));
            }
            OnPropertyChanged(nameof(HierarchyTooltip));
            OnPropertyChanged(nameof(StatesCount));
        }
    }
}

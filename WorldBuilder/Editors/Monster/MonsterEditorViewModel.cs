using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Services;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Monster {

    /// <summary>One texture slot on a specific body part — maps to one weenie_properties_texture_map row.</summary>
    public partial class SurfaceOverrideVm : ObservableObject {
        public byte PartIndex { get; init; }
        /// <summary>SurfaceTexture DID (0x05...) — goes in old_Id column.</summary>
        public uint OriginalTextureId { get; init; }
        public string OriginalLabel => "0x" + OriginalTextureId.ToString("X8", CultureInfo.InvariantCulture);

        [ObservableProperty] private WriteableBitmap? _originalThumbnail;
        [ObservableProperty] private string _replacementHex = "";
        [ObservableProperty] private WriteableBitmap? _replacementThumbnail;
        [ObservableProperty] private bool _isActive;
        public string Comment { get; set; } = "";

        public bool HasReplacement =>
            !string.IsNullOrWhiteSpace(ReplacementHex) && TryParseUInt(ReplacementHex, out _);

        static bool TryParseUInt(string s, out uint v) {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }
    }

    /// <summary>One part card — groups all surface texture slots for a single part index.</summary>
    public partial class PartGroupVm : ObservableObject {
        public byte PartIndex { get; init; }
        public uint GfxObjId { get; init; }
        public string PartLabel => $"Part {PartIndex}";
        public string GfxObjLabel => "0x" + GfxObjId.ToString("X8", CultureInfo.InvariantCulture);

        [ObservableProperty] private bool _isRemoved;
        /// <summary>
        /// When set, replaces this part's GfxObj with the given DID (mix-and-match donor part).
        /// Generates a weenie_properties_anim_part row.
        /// </summary>
        [ObservableProperty] private string _donorGfxObjHex = "";
        [ObservableProperty] private string _donorLabel = "";

        public ObservableCollection<SurfaceOverrideVm> Surfaces { get; } = new();

        public bool HasAnyOverride => IsRemoved || !string.IsNullOrWhiteSpace(DonorGfxObjHex) || Surfaces.Any(s => s.HasReplacement);
    }

    /// <summary>One item in the SurfaceTexture browser panel (0x05... IDs).</summary>
    public partial class SurfaceTextureItem : ObservableObject {
        public uint FullId { get; }
        public string DisplayId => "0x" + FullId.ToString("X8", CultureInfo.InvariantCulture);
        [ObservableProperty] private WriteableBitmap? _thumbnail;
        public SurfaceTextureItem(uint id) => FullId = id;
    }

    /// <summary>Creature list entry for the Monster Creator picker.</summary>
    public partial class MonsterListEntryVm : ObservableObject {
        public uint ClassId { get; }
        public string Name { get; }
        public uint SetupId { get; }
        public string Subtitle => $"WCID {ClassId}  •  Setup 0x{SetupId:X8}";
        public MonsterListEntryVm(uint classId, string name, uint setupId) {
            ClassId = classId; Name = name; SetupId = setupId;
        }
    }

    /// <summary>
    /// Monster Creator editor: browse creatures, see their body parts as visual cards with
    /// texture thumbnails, pick replacement textures from the inline browser, and apply/export SQL.
    /// The 3D preview re-renders live with overrides applied.
    /// </summary>
    public partial class MonsterEditorViewModel : ViewModelBase {
        private Project? _project;
        private IDatReaderWriter? _dats;
        private readonly TextureImportService? _textureImport;

        private uint[] _allTextureIds = Array.Empty<uint>();
        private int _browserDisplayCount = 300;

        public WorldBuilderSettings Settings { get; }

        /// <summary>True when a TextureImportService is available (project is open with write support).</summary>
        public bool CanImportTexture => _textureImport != null;

        public MonsterEditorViewModel(WorldBuilderSettings settings, TextureImportService? textureImport = null) {
            Settings = settings;
            _textureImport = textureImport;
        }

        // Creature picker
        [ObservableProperty] private string _statusText = "Configure ACE database in Settings, then search for a base creature.";
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private ObservableCollection<MonsterListEntryVm> _weenies = new();
        [ObservableProperty] private MonsterListEntryVm? _selectedWeenie;
        [ObservableProperty] private string _targetObjectId = "";

        // Part cards
        [ObservableProperty] private ObservableCollection<PartGroupVm> _partGroups = new();
        [ObservableProperty] private string _datPartsStatus = "Select a creature to load its body parts.";
        [ObservableProperty] private bool _isDatLoading;

        // Live preview
        [ObservableProperty] private uint _previewSetupDid;
        [ObservableProperty] private Dictionary<uint, uint>? _previewTextureOverrides;
        [ObservableProperty] private HashSet<int>? _previewHiddenParts;
        [ObservableProperty] private Dictionary<int, uint>? _previewGfxObjRemapping;
        [ObservableProperty] private DatReaderWriter.Types.ColorARGB[]? _previewCreaturePalette;

        // Donor creature (mix & match parts)
        [ObservableProperty] private string _donorSearchText = "";
        [ObservableProperty] private bool _isDonorBusy;
        [ObservableProperty] private ObservableCollection<MonsterListEntryVm> _donorWeenies = new();
        [ObservableProperty] private MonsterListEntryVm? _selectedDonorWeenie;
        [ObservableProperty] private ObservableCollection<PartGroupVm> _donorPartGroups = new();
        [ObservableProperty] private string _donorPartsStatus = "Search a creature above to browse its parts.";
        [ObservableProperty] private bool _isDonorDatLoading;
        [ObservableProperty] private PartGroupVm? _selectedDonorPart;
        [ObservableProperty] private string _selectedDonorLabel = "Select a donor part, then click 'Apply' on a target part above";

        // SQL
        [ObservableProperty] private string _sqlOutput = "";

        // Active slot (target for browser picks)
        [ObservableProperty] private SurfaceOverrideVm? _activeSlot;
        [ObservableProperty] private string _activeSlotLabel = "Select a part slot to target texture browser";

        // Texture browser
        [ObservableProperty] private string _browserSearchText = "";
        [ObservableProperty] private ObservableCollection<SurfaceTextureItem> _browserItems = new();
        [ObservableProperty] private string _browserStatus = "Loading textures…";
        [ObservableProperty] private bool _canLoadMoreBrowser;

        internal void Init(Project project) {
            _project = project;
            _dats = project.DatReaderWriter;
            _ = LoadBrowserItemsAsync();
        }

        partial void OnBrowserSearchTextChanged(string _) => ApplyBrowserFilter();

        partial void OnActiveSlotChanged(SurfaceOverrideVm? value) {
            ActiveSlotLabel = value == null
                ? "Select a part slot to target texture browser"
                : $"Targeting: Part {value.PartIndex}  ·  {value.OriginalLabel}";
        }

        // ─── Creature Search ────────────────────────────────────────────────────

        [RelayCommand]
        private async Task SearchWeeniesAsync() {
            if (Settings?.AceDbConnection == null) { StatusText = "Configure ACE Database in Settings first."; return; }

            IsBusy = true;
            StatusText = "Loading weenie list…";
            Weenies.Clear();
            SelectedWeenie = null;
            ClearDetail();

            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
                var list = await connector.GetWeenieNamesAsync(search, limit: 2500);
                foreach (var e in list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    Weenies.Add(new MonsterListEntryVm(e.ClassId, e.Name, e.SetupId));
                StatusText = list.Count == 0 ? "No weenies matched." : $"{list.Count} weenies. Select one to load its parts.";
            }
            catch (Exception ex) { StatusText = "Error: " + ex.Message; }
            finally { IsBusy = false; }
        }

        partial void OnSelectedWeenieChanged(MonsterListEntryVm? value) {
            if (value == null) return;
            TargetObjectId = value.ClassId.ToString(CultureInfo.InvariantCulture);
            PreviewSetupDid = value.SetupId;
            _ = LoadCreatureAsync(value);
        }

        async Task LoadCreatureAsync(MonsterListEntryVm entry) {
            ClearDetail();
            PreviewSetupDid = entry.SetupId;

            // Load DB overrides first so we can match them when DAT parts arrive
            List<AceTextureMapRow>? dbTexRows = null;
            List<AceAnimPartRow>? dbAnimRows = null;
            AceCreatureOverrides? overrides = null;

            if (Settings?.AceDbConnection != null) {
                StatusText = $"Loading overrides for WCID {entry.ClassId}…";
                try {
                    var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                    using var connector = new AceDbConnector(aceSettings);
                    overrides = await connector.LoadCreatureOverridesAsync(entry.ClassId);
                    dbTexRows = overrides.TextureMap;
                    dbAnimRows = overrides.AnimParts;
                    StatusText = $"{entry.Name} — WCID {entry.ClassId}";
                }
                catch (Exception ex) { StatusText = "DB load failed: " + ex.Message; }
            }

            await LoadDatPartsAsync(entry.SetupId, dbTexRows, dbAnimRows);

            // Build creature palette from ClothingBase + PaletteTemplate + Shade in background
            if (overrides != null)
                _ = BuildAndApplyCreaturePaletteAsync(overrides);

            RegenerateSql();
        }

        // ─── DAT Parts Loading ────────────────────────────────────────────────

        async Task LoadDatPartsAsync(uint setupDid,
            List<AceTextureMapRow>? dbTexRows,
            List<AceAnimPartRow>? dbAnimRows) {

            if (setupDid == 0) { DatPartsStatus = "No Setup DID — this weenie has no 3D model."; return; }
            if (_dats == null) { DatPartsStatus = "DAT files not loaded. Open a project first."; return; }

            IsDatLoading = true;
            DatPartsStatus = "Reading Setup from DAT…";
            var dats = _dats;

            var result = await Task.Run(() => {
                var groups = new List<PartGroupVm>();
                if (!dats.TryGet<Setup>(setupDid, out var setup) || setup?.Parts == null)
                    return (null, $"Setup 0x{setupDid:X8} not found in DAT.");

                for (int i = 0; i < setup.Parts.Count; i++) {
                    uint originalGfxObjId = setup.Parts[i];

                    // If the DB already has a non-hide anim_part for this index, load surfaces
                    // from that GfxObj so texture slot old_Ids match weenie_properties_texture_map.
                    var animOverride = dbAnimRows?.FirstOrDefault(
                        r => r.Index == i && r.AnimationId != 0x010001EC);
                    uint effectiveGfxObjId = animOverride?.AnimationId ?? originalGfxObjId;

                    if (!dats.TryGet<GfxObj>(effectiveGfxObjId, out var gfxObj) || gfxObj?.Polygons == null) {
                        if (effectiveGfxObjId != originalGfxObjId &&
                            dats.TryGet<GfxObj>(originalGfxObjId, out gfxObj) && gfxObj?.Polygons != null) {
                            effectiveGfxObjId = originalGfxObjId;
                        }
                        else continue;
                    }

                    var group = new PartGroupVm { PartIndex = (byte)i, GfxObjId = originalGfxObjId };

                    // Pre-populate the donor field so the UI and SQL reflect the existing override.
                    if (animOverride != null) {
                        group.DonorGfxObjHex = "0x" + animOverride.AnimationId.ToString("X8", CultureInfo.InvariantCulture);
                        group.DonorLabel = animOverride.Comment.Length > 0
                            ? animOverride.Comment
                            : $"0x{animOverride.AnimationId:X8}";
                    }

                    var seenSurfaces = new HashSet<uint>();  // Surface DIDs (0x08...)
                    var seenTextures = new HashSet<uint>();  // SurfaceTexture IDs — one slot per unique old_Id

                    foreach (var poly in gfxObj.Polygons.Values) {
                        // Walk both polygon sides so NegSurface (e.g. "Torso back") is not missed.
                        for (int side = 0; side < 2; side++) {
                            bool isNeg = side == 1;
                            if (isNeg  && poly.Stippling == StipplingType.NoNeg) continue;
                            if (!isNeg && poly.Stippling == StipplingType.NoPos) continue;

                            int si = isNeg ? poly.NegSurface : poly.PosSurface;
                            if (si < 0 || si >= gfxObj.Surfaces.Count) continue;

                            uint surfaceId = gfxObj.Surfaces[si]; // 0x08...
                            if (!seenSurfaces.Add(surfaceId)) continue;

                            if (!dats.TryGet<Surface>(surfaceId, out var surface) || surface == null) continue;
                            if (surface.Type.HasFlag(SurfaceType.Base1Solid)) continue;

                            uint texId = surface.OrigTextureId; // 0x05... — this is old_Id in texture_map
                            if (texId == 0 || !seenTextures.Add(texId)) continue;

                            group.Surfaces.Add(new SurfaceOverrideVm {
                                PartIndex = (byte)i,
                                OriginalTextureId = texId,
                            });
                        }
                    }

                    if (group.Surfaces.Count > 0)
                        groups.Add(group);
                }

                var status = $"{setup.Parts.Count} part(s), {groups.Sum(g => g.Surfaces.Count)} surface slot(s) loaded.";
                return (groups, status);
            });

            IsDatLoading = false;

            if (result.Item1 == null) {
                DatPartsStatus = result.Item2;
                return;
            }

            foreach (var group in result.Item1) {
                // Hide flag: anim_part row with the sentinel "invisible" GfxObj
                if (dbAnimRows?.Any(r => r.Index == group.PartIndex && r.AnimationId == 0x010001EC) == true)
                    group.IsRemoved = true;

                // Apply matching DB texture overrides.
                // Surfaces are already keyed to the effective (possibly donor-overridden) GfxObj,
                // so old_Ids here will correctly match weenie_properties_texture_map entries.
                foreach (var surf in group.Surfaces) {
                    var match = dbTexRows?.FirstOrDefault(r => r.Index == surf.PartIndex && r.OldId == surf.OriginalTextureId);
                    if (match != null) {
                        surf.ReplacementHex = "0x" + match.NewId.ToString("X8", CultureInfo.InvariantCulture);
                        surf.Comment = match.Comment;
                    }
                    SubscribeSurface(surf);
                }

                PartGroups.Add(group);
            }

            DatPartsStatus = result.Item2;
            _ = LoadOriginalThumbnailsAsync();
            _ = LoadReplacementThumbnailsForExistingAsync();
            RebuildPreviewOverrides();
        }

        void SubscribeSurface(SurfaceOverrideVm surf) {
            surf.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(SurfaceOverrideVm.ReplacementHex)) {
                    _ = LoadReplacementThumbnailAsync(surf);
                    RebuildPreviewOverrides();
                    RegenerateSql();
                }
            };
        }

        async Task LoadOriginalThumbnailsAsync() {
            var dats = _dats;
            if (dats == null) return;
            foreach (var group in PartGroups.ToArray()) {
                foreach (var surf in group.Surfaces.ToArray()) {
                    if (surf.OriginalThumbnail != null) continue;
                    var id = surf.OriginalTextureId;
                    var bmp = await Task.Run(() => DatIconLoader.LoadSurfaceTextureIcon(dats, id, 64));
                    surf.OriginalThumbnail = bmp;
                }
            }
        }

        async Task LoadReplacementThumbnailsForExistingAsync() {
            foreach (var group in PartGroups.ToArray())
                foreach (var surf in group.Surfaces.ToArray())
                    if (surf.HasReplacement)
                        await LoadReplacementThumbnailAsync(surf);
        }

        async Task LoadReplacementThumbnailAsync(SurfaceOverrideVm surf) {
            if (!TryParseUInt(surf.ReplacementHex, out var id)) {
                surf.ReplacementThumbnail = null;
                return;
            }
            var dats = _dats;
            if (dats == null) return;
            var customEntry = _textureImport?.Store.GetDungeonSurfaces()
                .FirstOrDefault(e => e.SurfaceTextureGid == id);
            WriteableBitmap? bmp;
            if (customEntry != null && _textureImport != null)
                bmp = await Task.Run(() => _textureImport.GenerateThumbnail(customEntry, 64));
            else
                bmp = await Task.Run(() => DatIconLoader.LoadSurfaceTextureIcon(dats, id, 64));
            surf.ReplacementThumbnail = bmp;
        }

        // ─── Part/Slot Commands ───────────────────────────────────────────────

        [RelayCommand]
        private void ActivatePart(PartGroupVm? group) {
            if (group == null) return;
            var first = group.Surfaces.FirstOrDefault();
            if (first != null) ActivateSlot(first);
        }

        [RelayCommand]
        private void ActivateSlot(SurfaceOverrideVm? slot) {
            if (ActiveSlot != null) ActiveSlot.IsActive = false;
            ActiveSlot = slot;
            if (slot != null) slot.IsActive = true;
        }

        [RelayCommand]
        private void PickTexture(SurfaceTextureItem? item) {
            if (item == null || ActiveSlot == null) return;
            ActiveSlot.ReplacementHex = item.DisplayId;
            if (item.Thumbnail != null) ActiveSlot.ReplacementThumbnail = item.Thumbnail;
            else _ = LoadReplacementThumbnailAsync(ActiveSlot);
        }

        [RelayCommand]
        private void ClearSlot(SurfaceOverrideVm? slot) {
            if (slot == null) return;
            slot.ReplacementHex = "";
            slot.ReplacementThumbnail = null;
        }

        [RelayCommand]
        private void ToggleRemovePart(PartGroupVm? group) {
            if (group == null) return;
            group.IsRemoved = !group.IsRemoved;
            if (group.IsRemoved) group.DonorGfxObjHex = ""; // clear donor when hiding
            RebuildPreviewOverrides();
            RegenerateSql();
        }

        // ─── Donor / Mix & Match ─────────────────────────────────────────

        [RelayCommand]
        private async Task SearchDonorWeeniesAsync() {
            if (Settings?.AceDbConnection == null) { DonorPartsStatus = "Configure ACE Database in Settings first."; return; }
            IsDonorBusy = true;
            DonorWeenies.Clear();
            DonorPartGroups.Clear();
            SelectedDonorWeenie = null;
            DonorPartsStatus = "Searching…";
            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var search = string.IsNullOrWhiteSpace(DonorSearchText) ? null : DonorSearchText.Trim();
                var list = await connector.GetWeenieNamesAsync(search, limit: 1000);
                foreach (var e in list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    DonorWeenies.Add(new MonsterListEntryVm(e.ClassId, e.Name, e.SetupId));
                DonorPartsStatus = list.Count == 0 ? "No matches." : $"{list.Count} creatures found.";
            }
            catch (Exception ex) { DonorPartsStatus = "Error: " + ex.Message; }
            finally { IsDonorBusy = false; }
        }

        partial void OnSelectedDonorWeenieChanged(MonsterListEntryVm? value) {
            SelectedDonorPart = null;
            if (value == null) return;
            _ = LoadDonorPartsAsync(value);
        }

        async Task LoadDonorPartsAsync(MonsterListEntryVm donor) {
            DonorPartGroups.Clear();
            if (donor.SetupId == 0) { DonorPartsStatus = $"{donor.Name} has no 3D model."; return; }
            if (_dats == null) return;

            IsDonorDatLoading = true;
            DonorPartsStatus = $"Loading parts for {donor.Name}…";
            var dats = _dats;

            var result = await Task.Run(() => {
                var groups = new List<PartGroupVm>();
                if (!dats.TryGet<Setup>(donor.SetupId, out var setup) || setup?.Parts == null)
                    return (null, $"Setup 0x{donor.SetupId:X8} not found.");
                for (int i = 0; i < setup.Parts.Count; i++) {
                    uint gfxObjId = setup.Parts[i];
                    if (!dats.TryGet<GfxObj>(gfxObjId, out var gfxObj) || gfxObj?.Polygons == null) continue;
                    var group = new PartGroupVm { PartIndex = (byte)i, GfxObjId = gfxObjId };
                    var seenSurfaces = new HashSet<uint>();
                    var seenTextures = new HashSet<uint>();
                    foreach (var poly in gfxObj.Polygons.Values) {
                        for (int side = 0; side < 2; side++) {
                            bool isNeg = side == 1;
                            if (isNeg  && poly.Stippling == StipplingType.NoNeg) continue;
                            if (!isNeg && poly.Stippling == StipplingType.NoPos) continue;
                            int si = isNeg ? poly.NegSurface : poly.PosSurface;
                            if (si < 0 || si >= gfxObj.Surfaces.Count) continue;
                            uint surfaceId = gfxObj.Surfaces[si];
                            if (!seenSurfaces.Add(surfaceId)) continue;
                            if (!dats.TryGet<Surface>(surfaceId, out var surface) || surface == null) continue;
                            if (surface.Type.HasFlag(SurfaceType.Base1Solid)) continue;
                            uint texId = surface.OrigTextureId;
                            if (texId == 0 || !seenTextures.Add(texId)) continue;
                            group.Surfaces.Add(new SurfaceOverrideVm { PartIndex = (byte)i, OriginalTextureId = texId });
                        }
                    }
                    if (group.Surfaces.Count > 0) groups.Add(group);
                }
                return (groups, $"{setup.Parts.Count} part(s) from {donor.Name}. Click a part to swap it onto the current creature.");
            });

            IsDonorDatLoading = false;
            if (result.Item1 == null) { DonorPartsStatus = result.Item2; return; }
            foreach (var g in result.Item1) DonorPartGroups.Add(g);
            DonorPartsStatus = result.Item2;

            // Load donor thumbnails in background
            _ = LoadDonorThumbnailsAsync(result.Item1);
        }

        async Task LoadDonorThumbnailsAsync(List<PartGroupVm> groups) {
            var dats = _dats;
            if (dats == null) return;
            foreach (var group in groups) {
                foreach (var surf in group.Surfaces.ToArray()) {
                    if (surf.OriginalThumbnail != null) continue;
                    var id = surf.OriginalTextureId;
                    var bmp = await Task.Run(() => DatIconLoader.LoadSurfaceTextureIcon(dats, id, 56));
                    surf.OriginalThumbnail = bmp;
                }
            }
        }

        /// <summary>
        /// Marks a donor part as the active selection for cross-index swapping.
        /// After calling this, the user can click "Apply" on any target part card.
        /// </summary>
        [RelayCommand]
        private void SelectDonorPart(PartGroupVm? donorPart) {
            SelectedDonorPart = donorPart;
            SelectedDonorLabel = donorPart == null
                ? "Select a donor part, then click 'Apply' on a target part above"
                : $"Selected: Part {donorPart.PartIndex} · {donorPart.GfxObjLabel}  from {SelectedDonorWeenie?.Name ?? "?"} — click 'Apply' on any part card";
        }

        /// <summary>
        /// Applies the currently selected donor part's GfxObj to the given target part
        /// — regardless of part index. This is what enables cross-index mixing.
        /// Rebuilds the part's surface slots from the donor GfxObj so texture overrides work.
        /// </summary>
        [RelayCommand]
        private void ApplySelectedDonorToPart(PartGroupVm? targetGroup) {
            if (targetGroup == null || SelectedDonorPart == null) return;
            targetGroup.IsRemoved = false;
            targetGroup.DonorGfxObjHex = SelectedDonorPart.GfxObjLabel;
            targetGroup.DonorLabel = $"Part {SelectedDonorPart.PartIndex} from {SelectedDonorWeenie?.Name ?? "donor"}";
            RebuildPreviewOverrides();
            RegenerateSql();
            StatusText = $"Applied {SelectedDonorWeenie?.Name} Part {SelectedDonorPart.PartIndex} ({SelectedDonorPart.GfxObjLabel}) → Part {targetGroup.PartIndex}";
            _ = ReloadGroupSurfacesAsync(targetGroup, SelectedDonorPart.GfxObjId);
        }

        [RelayCommand]
        private void ClearDonorPart(PartGroupVm? group) {
            if (group == null) return;
            group.DonorGfxObjHex = "";
            group.DonorLabel = "";
            RebuildPreviewOverrides();
            RegenerateSql();
            // Restore surfaces from the original GfxObj
            _ = ReloadGroupSurfacesAsync(group, group.GfxObjId);
        }

        /// <summary>
        /// Rebuilds the surface slots for <paramref name="group"/> from the given GfxObj.
        /// Called after a donor part is applied or cleared so texture overrides always
        /// reference the texture IDs that are actually rendered.
        /// </summary>
        async Task ReloadGroupSurfacesAsync(PartGroupVm group, uint gfxObjId) {
            if (_dats == null || gfxObjId == 0) return;
            var dats = _dats;

            var surfaces = await Task.Run(() => {
                var result = new List<SurfaceOverrideVm>();
                if (!dats.TryGet<GfxObj>(gfxObjId, out var gfxObj) || gfxObj?.Polygons == null)
                    return result;

                var seenSurfaces = new HashSet<uint>();
                var seenTextures = new HashSet<uint>();

                foreach (var poly in gfxObj.Polygons.Values) {
                    for (int side = 0; side < 2; side++) {
                        bool isNeg = side == 1;
                        if (isNeg  && poly.Stippling == StipplingType.NoNeg) continue;
                        if (!isNeg && poly.Stippling == StipplingType.NoPos) continue;
                        int si = isNeg ? poly.NegSurface : poly.PosSurface;
                        if (si < 0 || si >= gfxObj.Surfaces.Count) continue;
                        uint surfaceId = gfxObj.Surfaces[si];
                        if (!seenSurfaces.Add(surfaceId)) continue;
                        if (!dats.TryGet<Surface>(surfaceId, out var surface) || surface == null) continue;
                        if (surface.Type.HasFlag(SurfaceType.Base1Solid)) continue;
                        uint texId = surface.OrigTextureId;
                        if (texId == 0 || !seenTextures.Add(texId)) continue;
                        result.Add(new SurfaceOverrideVm { PartIndex = group.PartIndex, OriginalTextureId = texId });
                    }
                }
                return result;
            });

            group.Surfaces.Clear();
            foreach (var surf in surfaces) {
                SubscribeSurface(surf);
                group.Surfaces.Add(surf);
            }

            RebuildPreviewOverrides();
            RegenerateSql();
            _ = LoadOriginalThumbnailsAsync();
        }

        // ─── Preview / SQL ────────────────────────────────────────────────────

        void RebuildPreviewOverrides() {
            var texMap = new Dictionary<uint, uint>();
            var hidden = new HashSet<int>();
            var gfxMap = new Dictionary<int, uint>();

            foreach (var group in PartGroups) {
                if (group.IsRemoved) {
                    hidden.Add(group.PartIndex);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(group.DonorGfxObjHex) && TryParseUInt(group.DonorGfxObjHex, out var gfxId))
                    gfxMap[group.PartIndex] = gfxId;

                foreach (var surf in group.Surfaces) {
                    if (!surf.HasReplacement) continue;
                    if (!TryParseUInt(surf.ReplacementHex, out var newId)) continue;
                    if (newId != surf.OriginalTextureId)
                        texMap[surf.OriginalTextureId] = newId;
                }
            }

            PreviewTextureOverrides = texMap.Count > 0 ? texMap : null;
            PreviewHiddenParts = hidden.Count > 0 ? hidden : null;
            PreviewGfxObjRemapping = gfxMap.Count > 0 ? gfxMap : null;
        }

        [RelayCommand]
        private void RegenerateSqlManual() => RegenerateSql();

        void RegenerateSql() {
            if (!TryBuildOverrides(out var overrides, out var err)) {
                SqlOutput = $"-- Validation error: {err}";
                return;
            }
            SqlOutput = AceDbConnector.GenerateCreatureOverridesSql(overrides);
        }

        [RelayCommand]
        private async Task SaveToDbAsync() {
            if (Settings?.AceDbConnection == null) { StatusText = "Configure ACE Database in Settings first."; return; }
            if (!TryBuildOverrides(out var overrides, out var err)) { StatusText = err; return; }
            if (overrides.ObjectId == 0) { StatusText = "Enter a Target WCID before saving."; return; }

            IsBusy = true;
            StatusText = "Saving overrides…";
            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var ok = await connector.SaveCreatureOverridesAsync(overrides);
                StatusText = ok
                    ? $"Saved WCID {overrides.ObjectId}: {overrides.TextureMap.Count} texture override(s), {overrides.AnimParts.Count} anim part override(s)."
                    : "Save failed — check DB permissions and that the weenie exists.";
            }
            catch (Exception ex) { StatusText = "Save error: " + ex.Message; }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task CopyToClipboardAsync() {
            try {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow != null) {
                    var clipboard = TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard;
                    if (clipboard != null) {
                        RegenerateSql();
                        await clipboard.SetTextAsync(SqlOutput);
                        StatusText = "SQL copied to clipboard.";
                        return;
                    }
                }
                StatusText = "Could not access clipboard.";
            }
            catch (Exception ex) { StatusText = "Copy failed: " + ex.Message; }
        }

        [RelayCommand]
        private async Task ReloadOverridesAsync() {
            if (SelectedWeenie != null) await LoadCreatureAsync(SelectedWeenie);
        }

        [RelayCommand]
        private void ClearAllOverrides() {
            foreach (var group in PartGroups) {
                group.IsRemoved = false;
                foreach (var surf in group.Surfaces) {
                    surf.ReplacementHex = "";
                    surf.ReplacementThumbnail = null;
                }
            }
            RebuildPreviewOverrides();
            RegenerateSql();
            StatusText = "All overrides cleared.";
        }

        bool TryBuildOverrides(out AceCreatureOverrides overrides, out string error) {
            error = "";
            overrides = new AceCreatureOverrides();

            uint objectId = 0;
            if (!string.IsNullOrWhiteSpace(TargetObjectId)) {
                if (!TryParseUInt(TargetObjectId, out objectId)) {
                    error = "Target WCID must be a valid unsigned integer.";
                    return false;
                }
            }
            else if (SelectedWeenie != null) {
                objectId = SelectedWeenie.ClassId;
            }
            overrides.ObjectId = objectId;

            foreach (var group in PartGroups) {
                if (group.IsRemoved)
                    overrides.AnimParts.Add(new AceAnimPartRow { Index = group.PartIndex, AnimationId = 0x010001EC, Comment = $"Hide part {group.PartIndex}" });
                else if (!string.IsNullOrWhiteSpace(group.DonorGfxObjHex) && TryParseUInt(group.DonorGfxObjHex, out var gfxId))
                    overrides.AnimParts.Add(new AceAnimPartRow { Index = group.PartIndex, AnimationId = gfxId, Comment = group.DonorLabel });

                foreach (var surf in group.Surfaces.Where(s => s.HasReplacement)) {
                    if (!TryParseUInt(surf.ReplacementHex, out var newId)) continue;
                    overrides.TextureMap.Add(new AceTextureMapRow {
                        Index = surf.PartIndex,
                        OldId = surf.OriginalTextureId,
                        NewId = newId,
                        Comment = $"Part {surf.PartIndex}",
                    });
                }
            }
            return true;
        }

        void ClearDetail() {
            ActiveSlot = null;
            PreviewSetupDid = 0;
            PreviewTextureOverrides = null;
            PreviewHiddenParts = null;
            PreviewGfxObjRemapping = null;
            PreviewCreaturePalette = null;
            PartGroups.Clear();
            DatPartsStatus = "Select a creature to load its body parts.";
            SqlOutput = "";
        }

        // ─── Palette Building ─────────────────────────────────────────────────

        async Task BuildAndApplyCreaturePaletteAsync(AceCreatureOverrides overrides) {
            var dats = _dats;
            if (dats == null) return;
            var palette = await Task.Run(() => BuildCreaturePalette(dats, overrides));
            PreviewCreaturePalette = palette;
        }

        static ColorARGB[]? BuildCreaturePalette(IDatReaderWriter dats, AceCreatureOverrides overrides) {
            if (overrides.PaletteBase == 0) return null;

            if (!dats.TryGet<Palette>(overrides.PaletteBase, out var basePalette) || basePalette?.Colors == null)
                return null;

            // Copy base palette into a working array
            var palette = new ColorARGB[basePalette.Colors.Count];
            for (int i = 0; i < basePalette.Colors.Count; i++)
                palette[i] = basePalette.Colors[i];

            // Apply ClothingBase sub-palette effects
            if (overrides.ClothingBase != 0 &&
                dats.TryGet<ClothingTable>(overrides.ClothingBase, out var clothingTable) &&
                clothingTable?.ClothingSubPalEffects != null) {

                float shade = overrides.Shade > 0f ? overrides.Shade : 1f;

                foreach (var kvp in clothingTable.ClothingSubPalEffects) {
                    foreach (var subPalette in kvp.Value.CloSubPalettes) {
                        uint palSetId = subPalette.PaletteSet.DataId;
                        if (!dats.TryGet<PalSet>(palSetId, out var palSet) || palSet?.Palettes == null)
                            continue;

                        int templateIdx = overrides.PaletteTemplate;
                        if (templateIdx < 0 || templateIdx >= palSet.Palettes.Count) continue;

                        uint subPalDid = palSet.Palettes[templateIdx].DataId;
                        if (!dats.TryGet<Palette>(subPalDid, out var subPal) || subPal?.Colors == null)
                            continue;

                        foreach (var range in subPalette.Ranges) {
                            int destOffset = (int)range.Offset;
                            int numColors = (int)range.NumColors;
                            for (int i = 0; i < numColors; i++) {
                                if (i >= subPal.Colors.Count) break;
                                if (destOffset + i >= palette.Length) break;
                                var src = subPal.Colors[i];
                                palette[destOffset + i] = new ColorARGB {
                                    Red = (byte)Math.Min(255, (int)(src.Red * shade)),
                                    Green = (byte)Math.Min(255, (int)(src.Green * shade)),
                                    Blue = (byte)Math.Min(255, (int)(src.Blue * shade)),
                                    Alpha = src.Alpha,
                                };
                            }
                        }
                    }
                }
            }

            // Apply explicit weenie_properties_palette overrides
            foreach (var row in overrides.PaletteOverrides) {
                if (!dats.TryGet<Palette>(row.SubPaletteId, out var subPal) || subPal?.Colors == null)
                    continue;
                for (int i = 0; i < (int)row.Length; i++) {
                    if (i >= subPal.Colors.Count) break;
                    if (row.Offset + i >= (uint)palette.Length) break;
                    palette[row.Offset + i] = subPal.Colors[i];
                }
            }

            return palette;
        }

        // ─── Texture Browser ─────────────────────────────────────────────────

        async Task LoadBrowserItemsAsync() {
            if (_dats == null) return;
            BrowserStatus = "Loading SurfaceTextures from DAT…";
            var dats = _dats;

            var ids = await Task.Run(() => {
                try {
                    return dats.Dats.Portal.GetAllIdsOfType<SurfaceTexture>().OrderBy(id => id).ToArray();
                }
                catch { return Array.Empty<uint>(); }
            });

            // Merge any already-imported custom SurfaceTexture GIDs
            if (_textureImport != null) {
                var customIds = _textureImport.Store.GetDungeonSurfaces()
                    .Select(e => e.SurfaceTextureGid)
                    .Where(id => id != 0);
                ids = ids.Concat(customIds).Distinct().OrderBy(id => id).ToArray();
            }

            _allTextureIds = ids;
            BrowserStatus = $"{ids.Length} textures loaded.";
            ApplyBrowserFilter();
        }

        void ApplyBrowserFilter() {
            IEnumerable<uint> source = _allTextureIds;

            if (!string.IsNullOrWhiteSpace(BrowserSearchText)) {
                var hex = BrowserSearchText.TrimStart('0', 'x', 'X').ToUpperInvariant();
                source = source.Where(id => id.ToString("X8").Contains(hex));
            }

            var all = source.ToArray();
            var shown = all.Take(_browserDisplayCount).ToArray();
            CanLoadMoreBrowser = shown.Length < all.Length;

            var items = shown.Select(id => new SurfaceTextureItem(id)).ToList();
            BrowserItems = new ObservableCollection<SurfaceTextureItem>(items);
            BrowserStatus = shown.Length < all.Length
                ? $"Showing {shown.Length} of {all.Length} textures"
                : $"{shown.Length} textures";

            _ = GenerateBrowserThumbnailsAsync(items);
        }

        async Task GenerateBrowserThumbnailsAsync(IEnumerable<SurfaceTextureItem> items) {
            var dats = _dats;
            if (dats == null) return;
            foreach (var item in items.ToArray()) {
                if (item.Thumbnail != null) continue;
                var id = item.FullId;
                var customEntry = _textureImport?.Store.GetDungeonSurfaces()
                    .FirstOrDefault(e => e.SurfaceTextureGid == id);
                WriteableBitmap? bmp;
                if (customEntry != null && _textureImport != null)
                    bmp = await Task.Run(() => _textureImport.GenerateThumbnail(customEntry, 56));
                else
                    bmp = await Task.Run(() => DatIconLoader.LoadSurfaceTextureIcon(dats, id, 56));
                item.Thumbnail = bmp;
            }
        }

        [RelayCommand]
        private void LoadMoreBrowser() {
            _browserDisplayCount += 300;
            ApplyBrowserFilter();
        }

        [RelayCommand]
        private async Task ImportTextureAsync() {
            if (_textureImport == null) return;
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow == null) return;
            var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Import Texture",
                AllowMultiple = false,
                FileTypeFilter = new[] {
                    new FilePickerFileType("Image Files") {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" }
                    }
                }
            });

            if (files.Count == 0) return;
            var localPath = files[0].TryGetLocalPath();
            if (localPath == null) return;

            try {
                var name = Path.GetFileNameWithoutExtension(localPath);
                var entry = _textureImport.ImportDungeonSurface(localPath, name);
                var stGid = entry.SurfaceTextureGid;

                // Merge the new SurfaceTexture GID into the browser list
                if (stGid != 0 && !_allTextureIds.Contains(stGid))
                    _allTextureIds = _allTextureIds.Append(stGid).OrderBy(id => id).ToArray();

                _browserDisplayCount = Math.Max(_browserDisplayCount, _allTextureIds.Length);
                ApplyBrowserFilter();

                // Auto-apply to the active slot
                if (ActiveSlot != null && stGid != 0)
                    ActiveSlot.ReplacementHex = "0x" + stGid.ToString("X8", CultureInfo.InvariantCulture);

                StatusText = $"Imported '{name}' as 0x{stGid:X8} — will be written to DAT on export.";
                Console.WriteLine($"[MonsterEditor] Imported texture '{name}' ST=0x{stGid:X8}");
            }
            catch (Exception ex) {
                StatusText = $"Import failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Replaces an existing RenderSurface by ID (for monster/creature texture replacements).
        /// Validates format compatibility before importing.
        /// </summary>
        [RelayCommand]
        private async Task ReplaceRenderSurfaceAsync() {
            if (_textureImport == null) return;
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow == null) return;
            var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel == null) return;

            // Get RenderSurface ID from user
            var inputDialog = new Window {
                Title = "Replace RenderSurface by ID",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = "Enter RenderSurface ID to replace (e.g., 0x05003305):" });
            var textBox = new TextBox { Margin = new Thickness(0, 10, 0, 10) };
            stack.Children.Add(textBox);

            var buttonPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 10, 0) };
            var cancelButton = new Button { Content = "Cancel", Width = 80 };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stack.Children.Add(buttonPanel);

            inputDialog.Content = stack;

            uint? rsId = null;
            okButton.Click += (_, _) => {
                if (TryParseUInt(textBox.Text ?? "", out var id)) {
                    rsId = id;
                    inputDialog.Close();
                }
            };
            cancelButton.Click += (_, _) => inputDialog.Close();

            await inputDialog.ShowDialog(desktop.MainWindow);

            if (rsId == null) return;

            // Pick image file
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Select Replacement Texture",
                AllowMultiple = false,
                FileTypeFilter = new[] {
                    new FilePickerFileType("Image Files") {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" }
                    }
                }
            });

            if (files.Count == 0) return;
            var localPath = files[0].TryGetLocalPath();
            if (localPath == null) return;

            try {
                var name = Path.GetFileNameWithoutExtension(localPath);
                if (_textureImport.TryImportRenderSurfaceReplacement(localPath, rsId.Value, name, out var error)) {
                    StatusText = $"Imported replacement for RenderSurface 0x{rsId.Value:X8} — will overwrite on export.";
                    Console.WriteLine($"[MonsterEditor] Imported RenderSurface replacement '{name}' for 0x{rsId.Value:X8}");
                }
                else {
                    StatusText = $"Cannot replace 0x{rsId.Value:X8}: {error}";
                    Console.WriteLine($"[MonsterEditor] Failed to import RenderSurface replacement: {error}");
                }
            }
            catch (Exception ex) {
                StatusText = $"Import failed: {ex.Message}";
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        static bool TryParseUInt(string s, out uint v) {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }
    }
}

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Spell {
    public partial class SpellEditorViewModel : ViewModelBase {
        private IDatReaderWriter? _dats;
        private Project? _project;
        private PortalDatDocument? _portalDoc;
        private SpellDbDocument? _spellDbDoc;
        private SpellTable? _spellTable;
        private Dictionary<uint, SpellBase>? _allSpells;
        private Dictionary<uint, SpellRecord> _dbSpellCache = new();
        private SpellComponentTable? _componentTable;
        private const uint SpellTableId = 0x0E00000E;

        [ObservableProperty] private string _statusText = "No spells loaded";
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private MagicSchool? _filterSchool;
        [ObservableProperty] private SpellType? _filterSpellType;
        [ObservableProperty] private ObservableCollection<SpellListItem> _spells = new();
        [ObservableProperty] private SpellListItem? _selectedSpell;
        [ObservableProperty] private SpellDetailViewModel? _selectedDetail;
        [ObservableProperty] private int _totalSpellCount;
        [ObservableProperty] private int _filteredSpellCount;
        [ObservableProperty] private bool _saveToDb;

        public IReadOnlyList<MagicSchool?> SchoolOptions { get; } = new List<MagicSchool?> {
            null, MagicSchool.WarMagic, MagicSchool.LifeMagic,
            MagicSchool.ItemEnchantment, MagicSchool.CreatureEnchantment, MagicSchool.VoidMagic,
        };

        public IReadOnlyList<SpellType?> SpellTypeOptions { get; } = new List<SpellType?> {
            null, SpellType.Enchantment, SpellType.Projectile, SpellType.Boost,
            SpellType.Transfer, SpellType.PortalLink, SpellType.PortalRecall,
            SpellType.PortalSummon, SpellType.PortalSending, SpellType.Dispel,
            SpellType.LifeProjectile, SpellType.FellowBoost, SpellType.FellowEnchantment,
            SpellType.FellowPortalSending, SpellType.FellowDispel, SpellType.EnchantmentProjectile,
        };

        public WorldBuilderSettings Settings { get; }

        public SpellEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _portalDoc = project.DocumentManager.GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId).Result;
            _spellDbDoc = project.DocumentManager.GetOrCreateDocumentAsync<SpellDbDocument>(SpellDbDocument.DocumentId).Result;
            LoadSpells();
        }

        private void LoadSpells() {
            if (_dats == null) return;

            if (_portalDoc != null && _portalDoc.TryGetEntry<SpellTable>(SpellTableId, out var docTable) && docTable != null) {
                _spellTable = docTable;
            }
            else if (!_dats.TryGet<SpellTable>(SpellTableId, out var datTable)) {
                StatusText = "Failed to load SpellTable from DAT";
                return;
            }
            else {
                _spellTable = datTable;
            }

            _allSpells = _spellTable.Spells;
            TotalSpellCount = _allSpells.Count;

            _dats.TryGet<SpellComponentTable>(0x0E00000F, out var compTable);
            _componentTable = compTable;

            ApplyFilter();
            StatusText = $"Loaded {TotalSpellCount} spells, {_componentTable?.Components.Count ?? 0} components";
        }

        partial void OnSearchTextChanged(string value) => ApplyFilter();
        partial void OnFilterSchoolChanged(MagicSchool? value) => ApplyFilter();
        partial void OnFilterSpellTypeChanged(SpellType? value) => ApplyFilter();
        partial void OnSelectedSpellChanged(SpellListItem? value) {
            if (value != null && _allSpells != null && _allSpells.TryGetValue(value.Id, out var spell) && _dats != null) {
                var detail = new SpellDetailViewModel(value.Id, spell, _componentTable, _allSpells, _dats);
                SelectedDetail = detail;

                if (_dbSpellCache.TryGetValue(value.Id, out var spellCache)) {
                    detail.LoadFromDb(spellCache);
                } else if (_spellDbDoc != null && _spellDbDoc.TryGet(value.Id, out var localDb)) {
                    detail.LoadFromDb(localDb);
                }
                else {
                    _ = LoadDbSpellAsync(detail, value.Id);
                }
            }
            else {
                SelectedDetail = null;
            }
        }

        private async Task LoadDbSpellAsync(SpellDetailViewModel detail, uint spellId) {
            if (Settings?.AceDbConnection == null)
                return;

            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var currentId = detail.SpellId;

                var dbSpell = await connector.GetSpellAsync(spellId);

                if (dbSpell != null && currentId == detail.SpellId) {
                    _dbSpellCache[spellId] = dbSpell;
                    detail.LoadFromDb(dbSpell);
                }
            }
            catch {
            }
        }

        private void ApplyFilter() {
            if (_allSpells == null) return;

            var query = SearchText?.Trim() ?? "";
            uint searchId = 0;
            bool hasIdSearch = query.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(query.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out searchId);

            var filtered = _allSpells
                .Where(kvp => {
                    if (hasIdSearch) return kvp.Key == searchId;
                    if (!string.IsNullOrEmpty(query) &&
                        !(kvp.Value.Name?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (FilterSchool.HasValue && kvp.Value.School != FilterSchool.Value) return false;
                    if (FilterSpellType.HasValue && kvp.Value.MetaSpellType != FilterSpellType.Value) return false;
                    return true;
                })
                .OrderBy(kvp => kvp.Value.Name?.ToString() ?? "")
                .Take(500)
                .Select(kvp => new SpellListItem(kvp.Key, kvp.Value))
                .ToList();

            Spells = new ObservableCollection<SpellListItem>(filtered);
            FilteredSpellCount = filtered.Count;
        }

        [RelayCommand]
        private void ClearFilters() {
            SearchText = "";
            FilterSchool = null;
            FilterSpellType = null;
        }

        [RelayCommand]
        private void AddSpell() {
            if (_spellTable == null || _allSpells == null) return;

            uint nextId = 1;
            if (_allSpells.Count > 0)
                nextId = _allSpells.Keys.Max() + 1;

            var newSpell = new SpellBase { Name = $"New Spell {nextId}", Description = "" };
            _allSpells[nextId] = newSpell;
            TotalSpellCount = _allSpells.Count;
            ApplyFilter();

            SelectedSpell = Spells.FirstOrDefault(s => s.Id == nextId);
            StatusText = $"Added new spell #{nextId}. Remember to Save.";
        }

        [RelayCommand]
        private void DeleteSpell() {
            if (SelectedDetail == null || _spellTable == null || _portalDoc == null || _allSpells == null) return;

            var id = SelectedDetail.SpellId;
            if (!_allSpells.Remove(id)) return;

            _portalDoc.SetEntry(SpellTableId, _spellTable);

            SelectedDetail = null;
            TotalSpellCount = _allSpells.Count;
            ApplyFilter();
            StatusText = $"Deleted spell #{id}. Use File > Export to write DATs.";
        }

        [RelayCommand]
        private void CopySpell() {
            if (SelectedSpell == null || SelectedDetail == null || _spellTable == null || _allSpells == null)
                return;

            if (_allSpells.Count <= 0)
                return;

            uint nextId = 1;
            nextId = _allSpells.Keys.Max() + 1;

            var newSpell = new SpellBase();

            SelectedDetail.ApplyTo(newSpell);

            if (SelectedDetail.DbSpell != null && _spellDbDoc != null) {
                var original = SelectedDetail.DbSpell;

                var clone = new SpellRecord {
                    Id = nextId,
                    Name = original.Name,

                    StatModType = original.StatModType,
                    StatModKey = original.StatModKey,
                    StatModVal = original.StatModVal,

                    EType = original.EType,
                    BaseIntensity = original.BaseIntensity,
                    Variance = original.Variance,

                    Wcid = original.Wcid,

                    NumProjectiles = original.NumProjectiles,
                    NumProjectilesVariance = original.NumProjectilesVariance,
                    SpreadAngle = original.SpreadAngle,
                    VerticalAngle = original.VerticalAngle,
                    DefaultLaunchAngle = original.DefaultLaunchAngle,

                    NonTracking = original.NonTracking,

                    CreateOffsetOriginX = original.CreateOffsetOriginX,
                    CreateOffsetOriginY = original.CreateOffsetOriginY,
                    CreateOffsetOriginZ = original.CreateOffsetOriginZ,

                    PaddingOriginX = original.PaddingOriginX,
                    PaddingOriginY = original.PaddingOriginY,
                    PaddingOriginZ = original.PaddingOriginZ,

                    DimsOriginX = original.DimsOriginX,
                    DimsOriginY = original.DimsOriginY,
                    DimsOriginZ = original.DimsOriginZ,

                    PeturbationOriginX = original.PeturbationOriginX,
                    PeturbationOriginY = original.PeturbationOriginY,
                    PeturbationOriginZ = original.PeturbationOriginZ,

                    ImbuedEffect = original.ImbuedEffect,

                    SlayerCreatureType = original.SlayerCreatureType,
                    SlayerDamageBonus = original.SlayerDamageBonus,

                    CritFreq = original.CritFreq,
                    CritMultiplier = original.CritMultiplier,

                    IgnoreMagicResist = original.IgnoreMagicResist,
                    ElementalModifier = original.ElementalModifier,

                    DrainPercentage = original.DrainPercentage,
                    DamageRatio = original.DamageRatio,

                    DamageType = original.DamageType,

                    Boost = original.Boost,
                    BoostVariance = original.BoostVariance,

                    Source = original.Source,
                    Destination = original.Destination,

                    Proportion = original.Proportion,
                    LossPercent = original.LossPercent,

                    SourceLoss = original.SourceLoss,
                    TransferCap = original.TransferCap,
                    MaxBoostAllowed = original.MaxBoostAllowed,

                    TransferBitfield = original.TransferBitfield,

                    Index = original.Index,
                    Link = original.Link,

                    PositionObjCellId = original.PositionObjCellId,

                    PositionOriginX = original.PositionOriginX,
                    PositionOriginY = original.PositionOriginY,
                    PositionOriginZ = original.PositionOriginZ,

                    PositionAnglesW = original.PositionAnglesW,
                    PositionAnglesX = original.PositionAnglesX,
                    PositionAnglesY = original.PositionAnglesY,
                    PositionAnglesZ = original.PositionAnglesZ,

                    MinPower = original.MinPower,
                    MaxPower = original.MaxPower,
                    PowerVariance = original.PowerVariance,

                    DispelSchool = original.DispelSchool,

                    Align = original.Align,
                    Number = original.Number,
                    NumberVariance = original.NumberVariance,

                    DotDuration = original.DotDuration
                };

                _dbSpellCache[nextId] = clone;
                _spellDbDoc?.Set(nextId, clone);
            }

            newSpell.Name = newSpell.Name + " - Copy";
            _allSpells[nextId] = newSpell;

            ApplyFilter();

            SelectedSpell = Spells.FirstOrDefault(s => s.Id == nextId);
            StatusText = $"Copied spell to new ID: 0x{nextId:X4}. Remember to Save.";
        }

        [RelayCommand]
        private async Task SaveSpell() {
            if (SelectedDetail == null || _spellTable == null || _portalDoc == null || _allSpells == null) return;

            var detail = SelectedDetail;
            var id = detail.SpellId;

            if (!_allSpells.TryGetValue(id, out var spell)) return;

            detail.ApplyTo(spell);

            _portalDoc.SetEntry(SpellTableId, _spellTable);

            var existing = Spells.FirstOrDefault(s => s.Id == id);
            if (existing != null) {
                var index = Spells.IndexOf(existing);
                Spells[index] = new SpellListItem(id, spell);
            }

            if (_spellDbDoc != null) {
                var db = detail.DbSpell ?? new SpellRecord { Id = id };

                detail.ApplyDbTo(db);

                _spellDbDoc.Set(id, db);

                detail.DbSpell = db;
                _dbSpellCache.Remove(id);

                if (SaveToDb && Settings?.AceDbConnection != null) {
                    try {
                        var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                        using var connector = new AceDbConnector(aceSettings);
                        var success = await connector.SaveSpellAsync(db);

                        if (!success) {
                            StatusText = $"Saved locally, but DB save failed for spell #{id}";
                            return;
                        }
                    }
                    catch {
                        StatusText = $"Saved locally, but failed to save to DB for spell #{id}";
                        return;
                    }
                }
            }

            StatusText = $"Saved spell #{id}: {spell.Name} to project. Use File > Export to write DATs.";
        }
    }

    public class SpellListItem {
        public uint Id { get; }
        public string Name { get; }
        public string IdHex { get; }
        public MagicSchool School { get; }
        public SpellType MetaSpellType { get; }
        public uint Power { get; }
        public uint BaseMana { get; }

        public SpellListItem(uint id, SpellBase spell) {
            Id = id;
            Name = spell.Name?.ToString() ?? "";
            IdHex = $"0x{id:X4}";
            School = spell.School;
            MetaSpellType = spell.MetaSpellType;
            Power = spell.Power;
            BaseMana = spell.BaseMana;
        }

        public override string ToString() => $"{IdHex} - {Name}";
    }

    /// <summary>
    /// Selectable component entry for the component picker dropdown.
    /// </summary>
    public partial class ComponentPickerItem : ObservableObject {
        public uint Id { get; }
        public string Name { get; }
        public string TypeName { get; }
        public string DisplayLabel { get; }

        [ObservableProperty] private WriteableBitmap? _icon;

        public ComponentPickerItem(uint id, SpellComponentBase comp) {
            Id = id;
            Name = comp.Name?.ToString() ?? $"#{id}";
            TypeName = comp.Type.ToString();
            DisplayLabel = $"{Name} ({TypeName})";
        }

        public override string ToString() => DisplayLabel;
    }

    /// <summary>
    /// A component slot (1-8) that holds a selected component from the picker.
    /// </summary>
    public partial class SpellComponentSlot : ObservableObject {
        public int SlotIndex { get; }
        public string SlotLabel { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasComponent))]
        private ComponentPickerItem? _selectedComponent;

        public bool HasComponent => SelectedComponent != null;

        public SpellComponentSlot(int slotIndex) {
            SlotIndex = slotIndex;
            SlotLabel = $"#{slotIndex + 1}";
        }
    }

    /// <summary>
    /// Selectable icon for the icon picker grid.
    /// </summary>
    public partial class IconPickerItem : ObservableObject {
        public uint Id { get; }
        public string IdHex { get; }

        [ObservableProperty] private WriteableBitmap? _bitmap;

        public IconPickerItem(uint id) {
            Id = id;
            IdHex = $"0x{id:X8}";
        }
    }

    public partial class SpellDetailViewModel : ObservableObject {
        public uint SpellId { get; }

        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _description = "";
        [ObservableProperty] private MagicSchool _school;
        [ObservableProperty] private SpellType _metaSpellType;
        [ObservableProperty] private SpellCategory _category;
        [ObservableProperty] private uint _icon;
        [ObservableProperty] private WriteableBitmap? _iconBitmap;
        [ObservableProperty] private uint _baseMana;
        [ObservableProperty] private uint _power;
        [ObservableProperty] private float _baseRangeConstant;
        [ObservableProperty] private float _baseRangeMod;
        [ObservableProperty] private float _spellEconomyMod;
        [ObservableProperty] private uint _formulaVersion;
        [ObservableProperty] private float _componentLoss;
        [ObservableProperty] private uint _bitfield;
        [ObservableProperty] private uint _metaSpellId;
        [ObservableProperty] private double _duration;
        [ObservableProperty] private float _degradeModifier;
        [ObservableProperty] private float _degradeLimit;
        [ObservableProperty] private double _portalLifetime;
        [ObservableProperty] private PlayScript _casterEffect;
        [ObservableProperty] private PlayScript _targetEffect;
        [ObservableProperty] private PlayScript _fizzleEffect;
        [ObservableProperty] private double _recoveryInterval;
        [ObservableProperty] private float _recoveryAmount;
        [ObservableProperty] private uint _displayOrder;
        [ObservableProperty] private uint _nonComponentTargetType;
        [ObservableProperty] private uint _manaMod;
        [ObservableProperty] private SpellRecord? _dbSpell;

        [ObservableProperty] private ObservableCollection<SpellComponentSlot> _componentSlots = new();
        [ObservableProperty] private ObservableCollection<IconPickerItem> _availableIcons = new();
        [ObservableProperty] private bool _isIconPickerOpen;


        // ACE Spell table props
        [ObservableProperty] private string? _dbName;

        [ObservableProperty] private uint? _dbStatModType;
        [ObservableProperty] private uint? _dbStatModKey;
        [ObservableProperty] private float? _dbStatModVal;

        [ObservableProperty] private uint? _dbEType;
        [ObservableProperty] private int? _dbBaseIntensity;
        [ObservableProperty] private int? _dbVariance;

        [ObservableProperty] private uint? _dbWcid;

        [ObservableProperty] private int? _dbNumProjectiles;
        [ObservableProperty] private int? _dbNumProjectilesVariance;
        [ObservableProperty] private float? _dbSpreadAngle;
        [ObservableProperty] private float? _dbVerticalAngle;
        [ObservableProperty] private float? _dbDefaultLaunchAngle;

        [ObservableProperty] private bool? _dbNonTracking;

        [ObservableProperty] private float? _dbCreateOffsetOriginX;
        [ObservableProperty] private float? _dbCreateOffsetOriginY;
        [ObservableProperty] private float? _dbCreateOffsetOriginZ;

        [ObservableProperty] private float? _dbPaddingOriginX;
        [ObservableProperty] private float? _dbPaddingOriginY;
        [ObservableProperty] private float? _dbPaddingOriginZ;

        [ObservableProperty] private float? _dbDimsOriginX;
        [ObservableProperty] private float? _dbDimsOriginY;
        [ObservableProperty] private float? _dbDimsOriginZ;

        [ObservableProperty] private float? _dbPeturbationOriginX;
        [ObservableProperty] private float? _dbPeturbationOriginY;
        [ObservableProperty] private float? _dbPeturbationOriginZ;

        [ObservableProperty] private uint? _dbImbuedEffect;

        [ObservableProperty] private int? _dbSlayerCreatureType;
        [ObservableProperty] private float? _dbSlayerDamageBonus;

        [ObservableProperty] private double? _dbCritFreq;
        [ObservableProperty] private double? _dbCritMultiplier;

        [ObservableProperty] private int? _dbIgnoreMagicResist;
        [ObservableProperty] private double? _dbElementalModifier;

        [ObservableProperty] private float? _dbDrainPercentage;
        [ObservableProperty] private float? _dbDamageRatio;

        [ObservableProperty] private int? _dbDamageType;

        [ObservableProperty] private int? _dbBoost;
        [ObservableProperty] private int? _dbBoostVariance;

        [ObservableProperty] private int? _dbSource;
        [ObservableProperty] private int? _dbDestination;

        [ObservableProperty] private float? _dbProportion;
        [ObservableProperty] private float? _dbLossPercent;

        [ObservableProperty] private int? _dbSourceLoss;
        [ObservableProperty] private int? _dbTransferCap;
        [ObservableProperty] private int? _dbMaxBoostAllowed;

        [ObservableProperty] private uint? _dbTransferBitfield;

        [ObservableProperty] private int? _dbIndex;
        [ObservableProperty] private int? _dbLink;

        [ObservableProperty] private uint? _dbPositionObjCellId;

        [ObservableProperty] private float? _dbPositionOriginX;
        [ObservableProperty] private float? _dbPositionOriginY;
        [ObservableProperty] private float? _dbPositionOriginZ;

        [ObservableProperty] private float? _dbPositionAnglesW;
        [ObservableProperty] private float? _dbPositionAnglesX;
        [ObservableProperty] private float? _dbPositionAnglesY;
        [ObservableProperty] private float? _dbPositionAnglesZ;

        [ObservableProperty] private int? _dbMinPower;
        [ObservableProperty] private int? _dbMaxPower;
        [ObservableProperty] private float? _dbPowerVariance;

        [ObservableProperty] private int? _dbDispelSchool;

        [ObservableProperty] private int? _dbAlign;
        [ObservableProperty] private int? _dbNumber;
        [ObservableProperty] private float? _dbNumberVariance;

        [ObservableProperty] private double? _dbDotDuration;

        public void LoadFromDb(SpellRecord db) {
            if (db == null)
                return;

            DbSpell = db;

            DbName = db.Name;

            DbStatModType = db.StatModType;
            DbStatModKey = db.StatModKey;
            DbStatModVal = db.StatModVal;

            DbEType = db.EType;
            DbBaseIntensity = db.BaseIntensity;
            DbVariance = db.Variance;

            DbWcid = db.Wcid;

            DbNumProjectiles = db.NumProjectiles;
            DbNumProjectilesVariance = db.NumProjectilesVariance;
            DbSpreadAngle = db.SpreadAngle;
            DbVerticalAngle = db.VerticalAngle;
            DbDefaultLaunchAngle = db.DefaultLaunchAngle;

            DbNonTracking = db.NonTracking;

            DbCreateOffsetOriginX = db.CreateOffsetOriginX;
            DbCreateOffsetOriginY = db.CreateOffsetOriginY;
            DbCreateOffsetOriginZ = db.CreateOffsetOriginZ;

            DbPaddingOriginX = db.PaddingOriginX;
            DbPaddingOriginY = db.PaddingOriginY;
            DbPaddingOriginZ = db.PaddingOriginZ;

            DbDimsOriginX = db.DimsOriginX;
            DbDimsOriginY = db.DimsOriginY;
            DbDimsOriginZ = db.DimsOriginZ;

            DbPeturbationOriginX = db.PeturbationOriginX;
            DbPeturbationOriginY = db.PeturbationOriginY;
            DbPeturbationOriginZ = db.PeturbationOriginZ;

            DbImbuedEffect = db.ImbuedEffect;

            DbSlayerCreatureType = db.SlayerCreatureType;
            DbSlayerDamageBonus = db.SlayerDamageBonus;

            DbCritFreq = db.CritFreq;
            DbCritMultiplier = db.CritMultiplier;

            DbIgnoreMagicResist = db.IgnoreMagicResist;
            DbElementalModifier = db.ElementalModifier;

            DbDrainPercentage = db.DrainPercentage;
            DbDamageRatio = db.DamageRatio;

            DbDamageType = db.DamageType;

            DbBoost = db.Boost;
            DbBoostVariance = db.BoostVariance;

            DbSource = db.Source;
            DbDestination = db.Destination;

            DbProportion = db.Proportion;
            DbLossPercent = db.LossPercent;

            DbSourceLoss = db.SourceLoss;
            DbTransferCap = db.TransferCap;
            DbMaxBoostAllowed = db.MaxBoostAllowed;

            DbTransferBitfield = db.TransferBitfield;

            DbIndex = db.Index;
            DbLink = db.Link;

            DbPositionObjCellId = db.PositionObjCellId;

            DbPositionOriginX = db.PositionOriginX;
            DbPositionOriginY = db.PositionOriginY;
            DbPositionOriginZ = db.PositionOriginZ;

            DbPositionAnglesW = db.PositionAnglesW;
            DbPositionAnglesX = db.PositionAnglesX;
            DbPositionAnglesY = db.PositionAnglesY;
            DbPositionAnglesZ = db.PositionAnglesZ;

            DbMinPower = db.MinPower;
            DbMaxPower = db.MaxPower;
            DbPowerVariance = db.PowerVariance;

            DbDispelSchool = db.DispelSchool;

            DbAlign = db.Align;
            DbNumber = db.Number;
            DbNumberVariance = db.NumberVariance;

            DbDotDuration = db.DotDuration;
        }

        public void ApplyDbTo(SpellRecord db) {
            if (db == null)
                return;

            db.Name = DbName;

            db.StatModType = DbStatModType;
            db.StatModKey = DbStatModKey;
            db.StatModVal = DbStatModVal;

            db.EType = DbEType;
            db.BaseIntensity = DbBaseIntensity;
            db.Variance = DbVariance;

            db.Wcid = DbWcid;

            db.NumProjectiles = DbNumProjectiles;
            db.NumProjectilesVariance = DbNumProjectilesVariance;
            db.SpreadAngle = DbSpreadAngle;
            db.VerticalAngle = DbVerticalAngle;
            db.DefaultLaunchAngle = DbDefaultLaunchAngle;

            db.NonTracking = DbNonTracking;

            db.CreateOffsetOriginX = DbCreateOffsetOriginX;
            db.CreateOffsetOriginY = DbCreateOffsetOriginY;
            db.CreateOffsetOriginZ = DbCreateOffsetOriginZ;

            db.PaddingOriginX = DbPaddingOriginX;
            db.PaddingOriginY = DbPaddingOriginY;
            db.PaddingOriginZ = DbPaddingOriginZ;

            db.DimsOriginX = DbDimsOriginX;
            db.DimsOriginY = DbDimsOriginY;
            db.DimsOriginZ = DbDimsOriginZ;

            db.PeturbationOriginX = DbPeturbationOriginX;
            db.PeturbationOriginY = DbPeturbationOriginY;
            db.PeturbationOriginZ = DbPeturbationOriginZ;

            db.ImbuedEffect = DbImbuedEffect;

            db.SlayerCreatureType = DbSlayerCreatureType;
            db.SlayerDamageBonus = DbSlayerDamageBonus;

            db.CritFreq = DbCritFreq;
            db.CritMultiplier = DbCritMultiplier;

            db.IgnoreMagicResist = DbIgnoreMagicResist;
            db.ElementalModifier = DbElementalModifier;

            db.DrainPercentage = DbDrainPercentage;
            db.DamageRatio = DbDamageRatio;

            db.DamageType = DbDamageType;

            db.Boost = DbBoost;
            db.BoostVariance = DbBoostVariance;

            db.Source = DbSource;
            db.Destination = DbDestination;

            db.Proportion = DbProportion;
            db.LossPercent = DbLossPercent;

            db.SourceLoss = DbSourceLoss;
            db.TransferCap = DbTransferCap;
            db.MaxBoostAllowed = DbMaxBoostAllowed;

            db.TransferBitfield = DbTransferBitfield;

            db.Index = DbIndex;
            db.Link = DbLink;

            db.PositionObjCellId = DbPositionObjCellId;

            db.PositionOriginX = DbPositionOriginX;
            db.PositionOriginY = DbPositionOriginY;
            db.PositionOriginZ = DbPositionOriginZ;

            db.PositionAnglesW = DbPositionAnglesW;
            db.PositionAnglesX = DbPositionAnglesX;
            db.PositionAnglesY = DbPositionAnglesY;
            db.PositionAnglesZ = DbPositionAnglesZ;

            db.MinPower = DbMinPower;
            db.MaxPower = DbMaxPower;
            db.PowerVariance = DbPowerVariance;

            db.DispelSchool = DbDispelSchool;

            db.Align = DbAlign;
            db.Number = DbNumber;
            db.NumberVariance = DbNumberVariance;

            db.DotDuration = DbDotDuration;
        }

        public List<ComponentPickerItem> AllComponents { get; private set; } = new();

        public bool IsEnchantment => MetaSpellType == SpellType.Enchantment || MetaSpellType == SpellType.FellowEnchantment;
        public bool IsPortalSummon => MetaSpellType == SpellType.PortalSummon;
        public bool CanAddComponent => ComponentSlots.Count < 8;

        partial void OnMetaSpellTypeChanged(SpellType value) {
            OnPropertyChanged(nameof(IsEnchantment));
            OnPropertyChanged(nameof(IsPortalSummon));
        }

        partial void OnIconChanged(uint value) {
            if (_dats != null) {
                var localDats = _dats;
                Task.Run(() => {
                    var bmp = DatIconLoader.LoadIcon(localDats, value, 48);
                    Dispatcher.UIThread.Post(() => IconBitmap = bmp);
                });
            }
        }

        public IReadOnlyList<MagicSchool> AllSchools { get; } = Enum.GetValues<MagicSchool>();
        public IReadOnlyList<SpellType> AllSpellTypes { get; } = Enum.GetValues<SpellType>();
        public IReadOnlyList<PlayScript> AllPlayScripts { get; } = Enum.GetValues<PlayScript>();

        public ObservableCollection<FlagItem> BitfieldFlags { get; } = new();
        public ObservableCollection<FlagItem> TargetTypeFlags { get; } = new();

        public string BitfieldDisplay => BitfieldFlags.Any(f => f.IsChecked)
            ? string.Join(", ", BitfieldFlags.Where(f => f.IsChecked).Select(f => f.Name))
            : "(none)";

        public string TargetTypeDisplay => TargetTypeFlags.Any(f => f.IsChecked)
            ? string.Join(", ", TargetTypeFlags.Where(f => f.IsChecked).Select(f => f.Name))
            : "(none)";

        private readonly SpellComponentTable? _componentTable;
        private readonly IDatReaderWriter? _dats;
        private uint _extraBitfieldBits;
        private uint _extraTargetTypeBits;

        public SpellDetailViewModel(uint id, SpellBase spell, SpellComponentTable? componentTable,
            Dictionary<uint, SpellBase> allSpells, IDatReaderWriter dats) {
            _componentTable = componentTable;
            _dats = dats;

            SpellId = id;
            Name = spell.Name?.ToString() ?? "";
            Description = spell.Description?.ToString() ?? "";
            School = spell.School;
            MetaSpellType = spell.MetaSpellType;
            Category = spell.Category;
            Icon = spell.Icon;
            BaseMana = spell.BaseMana;
            Power = spell.Power;
            BaseRangeConstant = spell.BaseRangeConstant;
            BaseRangeMod = spell.BaseRangeMod;
            SpellEconomyMod = spell.SpellEconomyMod;
            FormulaVersion = spell.FormulaVersion;
            ComponentLoss = spell.ComponentLoss;
            Bitfield = (uint)spell.Bitfield;
            MetaSpellId = spell.MetaSpellId;
            Duration = spell.Duration;
            DegradeModifier = spell.DegradeModifier;
            DegradeLimit = spell.DegradeLimit;
            PortalLifetime = spell.PortalLifetime;
            CasterEffect = spell.CasterEffect;
            TargetEffect = spell.TargetEffect;
            FizzleEffect = spell.FizzleEffect;
            RecoveryInterval = spell.RecoveryInterval;
            RecoveryAmount = spell.RecoveryAmount;
            DisplayOrder = spell.DisplayOrder;
            NonComponentTargetType = (uint)spell.NonComponentTargetType;
            ManaMod = spell.ManaMod;

            InitBitfieldFlags(Bitfield);
            InitTargetTypeFlags(NonComponentTargetType);

            BuildAllComponents();
            BuildComponentSlots(spell.Components);
            BuildAvailableIcons(allSpells);
            LoadIconAsync(spell.Icon);
        }

        private void BuildAllComponents() {
            if (_componentTable == null) return;
            AllComponents = _componentTable.Components
                .OrderBy(kvp => kvp.Value.Name?.ToString() ?? "")
                .Select(kvp => new ComponentPickerItem(kvp.Key, kvp.Value))
                .ToList();

            if (_dats != null) {
                var localDats = _dats;
                foreach (var item in AllComponents) {
                    var comp = _componentTable.Components[item.Id];
                    if (comp.Icon == 0) continue;
                    var localItem = item;
                    var localIconId = comp.Icon;
                    Task.Run(() => {
                        var bmp = DatIconLoader.LoadIcon(localDats, localIconId, 20);
                        Dispatcher.UIThread.Post(() => localItem.Icon = bmp);
                    });
                }
            }
        }

        private void BuildComponentSlots(List<uint> componentIds) {
            ComponentSlots.Clear();
            for (int i = 0; i < componentIds.Count; i++) {
                var slot = new SpellComponentSlot(i);
                var match = AllComponents.FirstOrDefault(c => c.Id == componentIds[i]);
                slot.SelectedComponent = match;
                ComponentSlots.Add(slot);
            }
            OnPropertyChanged(nameof(CanAddComponent));
        }

        private void BuildAvailableIcons(Dictionary<uint, SpellBase> allSpells) {
            var snapshot = allSpells.Values.ToArray();
            var uniqueIconIds = snapshot
                .Select(s => s.Icon)
                .Where(id => id != 0)
                .Distinct()
                .ToList();
            uniqueIconIds.Sort();

            if (_dats != null) {
                var localDats = _dats;
                Task.Run(() => {
                    var items = new List<IconPickerItem>();
                    foreach (var iconId in uniqueIconIds) {
                        var item = new IconPickerItem(iconId);
                        item.Bitmap = DatIconLoader.LoadIcon(localDats, iconId, 32);
                        if (item.Bitmap != null)
                            items.Add(item);
                    }
                    Dispatcher.UIThread.Post(() => {
                        AvailableIcons = new ObservableCollection<IconPickerItem>(items);
                    });
                });
            }
        }

        private void LoadIconAsync(uint iconId) {
            if (iconId == 0 || _dats == null) return;
            var localDats = _dats;
            Task.Run(() => {
                var bmp = DatIconLoader.LoadIcon(localDats, iconId, 48);
                Dispatcher.UIThread.Post(() => IconBitmap = bmp);
            });
        }

        [RelayCommand]
        private void PickIcon(IconPickerItem? item) {
            if (item == null) return;
            Icon = item.Id;
            IsIconPickerOpen = false;
        }

        [RelayCommand]
        private void ToggleIconPicker() {
            IsIconPickerOpen = !IsIconPickerOpen;
        }

        [RelayCommand]
        private void AddComponent() {
            if (ComponentSlots.Count >= 8) return;
            var slot = new SpellComponentSlot(ComponentSlots.Count);
            ComponentSlots.Add(slot);
            OnPropertyChanged(nameof(CanAddComponent));
        }

        [RelayCommand]
        private void RemoveComponent(SpellComponentSlot? slot) {
            if (slot == null) return;
            ComponentSlots.Remove(slot);
            for (int i = 0; i < ComponentSlots.Count; i++) {
                // Re-index isn't needed since SlotIndex is readonly, but labels update via the collection
            }
            OnPropertyChanged(nameof(CanAddComponent));
        }

        [RelayCommand]
        private void MoveComponentUp(SpellComponentSlot? slot) {
            if (slot == null) return;
            int idx = ComponentSlots.IndexOf(slot);
            if (idx <= 0) return;
            ComponentSlots.Move(idx, idx - 1);
        }

        [RelayCommand]
        private void MoveComponentDown(SpellComponentSlot? slot) {
            if (slot == null) return;
            int idx = ComponentSlots.IndexOf(slot);
            if (idx < 0 || idx >= ComponentSlots.Count - 1) return;
            ComponentSlots.Move(idx, idx + 1);
        }

        private void InitBitfieldFlags(uint bitfield) {
            uint knownBits = 0;
            foreach (var flag in Enum.GetValues<SpellIndex>()) {
                var val = (uint)flag;
                if (val == 0 || (val & (val - 1)) != 0) continue;
                knownBits |= val;
                var item = new FlagItem(flag.ToString(), val, (bitfield & val) != 0);
                item.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(FlagItem.IsChecked)) UpdateBitfieldFromFlags();
                };
                BitfieldFlags.Add(item);
            }
            _extraBitfieldBits = bitfield & ~knownBits;
        }

        private void UpdateBitfieldFromFlags() {
            uint val = _extraBitfieldBits;
            foreach (var f in BitfieldFlags)
                if (f.IsChecked) val |= f.Value;
            Bitfield = val;
            OnPropertyChanged(nameof(BitfieldDisplay));
        }

        private void InitTargetTypeFlags(uint targetType) {
            uint knownBits = 0;
            foreach (var flag in Enum.GetValues<ItemType>()) {
                var val = (uint)flag;
                if (val == 0 || (val & (val - 1)) != 0) continue;
                knownBits |= val;
                var item = new FlagItem(flag.ToString(), val, (targetType & val) != 0);
                item.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(FlagItem.IsChecked)) UpdateTargetTypeFromFlags();
                };
                TargetTypeFlags.Add(item);
            }
            _extraTargetTypeBits = targetType & ~knownBits;
        }

        private void UpdateTargetTypeFromFlags() {
            uint val = _extraTargetTypeBits;
            foreach (var f in TargetTypeFlags)
                if (f.IsChecked) val |= f.Value;
            NonComponentTargetType = val;
            OnPropertyChanged(nameof(TargetTypeDisplay));
        }

        public void ApplyTo(SpellBase spell) {
            spell.Name = Name;
            spell.Description = Description;
            spell.School = School;
            spell.MetaSpellType = MetaSpellType;
            spell.Category = Category;
            spell.Icon = Icon;
            spell.BaseMana = BaseMana;
            spell.Power = Power;
            spell.BaseRangeConstant = BaseRangeConstant;
            spell.BaseRangeMod = BaseRangeMod;
            spell.SpellEconomyMod = SpellEconomyMod;
            spell.FormulaVersion = FormulaVersion;
            spell.ComponentLoss = ComponentLoss;
            spell.Bitfield = (SpellIndex)Bitfield;
            spell.MetaSpellId = MetaSpellId;
            spell.Duration = Duration;
            spell.DegradeModifier = DegradeModifier;
            spell.DegradeLimit = DegradeLimit;
            spell.PortalLifetime = PortalLifetime;
            spell.CasterEffect = CasterEffect;
            spell.TargetEffect = TargetEffect;
            spell.FizzleEffect = FizzleEffect;
            spell.RecoveryInterval = RecoveryInterval;
            spell.RecoveryAmount = RecoveryAmount;
            spell.DisplayOrder = DisplayOrder;
            spell.NonComponentTargetType = (ItemType)NonComponentTargetType;
            spell.ManaMod = ManaMod;

            spell.Components = ComponentSlots
                .Where(s => s.SelectedComponent != null)
                .Select(s => s.SelectedComponent!.Id)
                .ToList();
        }
    }

    public partial class FlagItem : ObservableObject {
        public string Name { get; }
        public uint Value { get; }

        [ObservableProperty] private bool _isChecked;

        public FlagItem(string name, uint value, bool isChecked) {
            Name = name;
            Value = value;
            _isChecked = isChecked;
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.DBObjs;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Experience {
    public partial class XpRow : ObservableObject {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _value = "0";

        public XpRow(int index, string value) {
            _index = index;
            _value = value;
        }
    }

    public partial class LevelRow : ObservableObject {
        [ObservableProperty] private int _level;
        [ObservableProperty] private string _xpRequired = "0";
        [ObservableProperty] private string _skillCredits = "0";

        public LevelRow(int level, string xp, string credits) {
            _level = level;
            _xpRequired = xp;
            _skillCredits = credits;
        }
    }

    public partial class ExperienceEditorViewModel : ViewModelBase {
        private IDatReaderWriter? _dats;
        private Project? _project;
        private PortalDatDocument? _portalDoc;
        private ExperienceTable? _table;
        private const uint ExperienceTableId = 0x0E000018;

        [ObservableProperty] private string _statusText = "No experience table loaded";
        [ObservableProperty] private int _selectedTabIndex;
        [ObservableProperty] private bool _isAutoScaleOpen;

        [ObservableProperty] private ObservableCollection<LevelRow> _levels = new();
        [ObservableProperty] private ObservableCollection<XpRow> _attributes = new();
        [ObservableProperty] private ObservableCollection<XpRow> _vitals = new();
        [ObservableProperty] private ObservableCollection<XpRow> _trainedSkills = new();
        [ObservableProperty] private ObservableCollection<XpRow> _specializedSkills = new();

        [ObservableProperty] private string _autoScaleTotalLevels = "275";
        [ObservableProperty] private string _autoScaleBaseXp = "1000";
        [ObservableProperty] private string _autoScaleGrowthRate = "2.5";
        [ObservableProperty] private string _autoScaleCreditsEveryN = "5";
        [ObservableProperty] private string _autoScaleAttributeRanks = "190";
        [ObservableProperty] private string _autoScaleVitalRanks = "196";
        [ObservableProperty] private string _autoScaleSkillRanks = "226";

        partial void OnAutoScaleTotalLevelsChanged(string value) {
            if (!int.TryParse(value, out int levels) || levels < 1) return;
            double scale = (double)levels / 275.0;
            AutoScaleAttributeRanks = Math.Max(10, (int)(190 * scale)).ToString();
            AutoScaleVitalRanks = Math.Max(10, (int)(196 * scale)).ToString();
            AutoScaleSkillRanks = Math.Max(10, (int)(226 * scale)).ToString();
        }

        public WorldBuilderSettings Settings { get; }

        public ExperienceEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _portalDoc = project.DocumentManager.GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId).Result;
            LoadTable();
        }

        private void LoadTable() {
            if (_dats == null) return;

            if (_portalDoc != null && _portalDoc.TryGetEntry<ExperienceTable>(ExperienceTableId, out var docTable) && docTable != null) {
                _table = docTable;
            }
            else if (!_dats.TryGet<ExperienceTable>(ExperienceTableId, out var datTable)) {
                StatusText = "Failed to load ExperienceTable (0x0E000018) from DAT";
                return;
            }
            else {
                _table = datTable;
            }

            PopulateCollections();
            StatusText = $"Loaded: {_table.Levels.Length} levels, {_table.Attributes.Length} attribute ranks, " +
                         $"{_table.Vitals.Length} vital ranks, {_table.TrainedSkills.Length} trained, " +
                         $"{_table.SpecializedSkills.Length} specialized";
        }

        private void PopulateCollections() {
            if (_table == null) return;

            Levels.Clear();
            int levelCount = _table.Levels.Length;
            int creditCount = _table.SkillCredits.Length;
            for (int i = 0; i < levelCount; i++) {
                var credits = i < creditCount ? _table.SkillCredits[i].ToString() : "0";
                Levels.Add(new LevelRow(i, _table.Levels[i].ToString(), credits));
            }

            Attributes.Clear();
            for (int i = 0; i < _table.Attributes.Length; i++)
                Attributes.Add(new XpRow(i, _table.Attributes[i].ToString()));

            Vitals.Clear();
            for (int i = 0; i < _table.Vitals.Length; i++)
                Vitals.Add(new XpRow(i, _table.Vitals[i].ToString()));

            TrainedSkills.Clear();
            for (int i = 0; i < _table.TrainedSkills.Length; i++)
                TrainedSkills.Add(new XpRow(i, _table.TrainedSkills[i].ToString()));

            SpecializedSkills.Clear();
            for (int i = 0; i < _table.SpecializedSkills.Length; i++)
                SpecializedSkills.Add(new XpRow(i, _table.SpecializedSkills[i].ToString()));
        }

        [RelayCommand]
        private void AddLevel() {
            int nextLevel = Levels.Count;
            Levels.Add(new LevelRow(nextLevel, "0", "0"));
            StatusText = $"Added level {nextLevel} (total: {Levels.Count})";
        }

        [RelayCommand]
        private void RemoveLevel() {
            if (Levels.Count <= 1) {
                StatusText = "Cannot remove the last level";
                return;
            }
            int removed = Levels.Count - 1;
            Levels.RemoveAt(removed);
            StatusText = $"Removed level {removed} (total: {Levels.Count})";
        }

        [RelayCommand]
        private void AddRank() {
            ObservableCollection<XpRow>? collection = GetActiveRankCollection();
            if (collection == null) {
                StatusText = "Select a rank tab (Attributes, Vitals, Trained, or Specialized)";
                return;
            }
            int nextIndex = collection.Count;
            collection.Add(new XpRow(nextIndex, "0"));
            StatusText = $"Added rank {nextIndex} to {GetActiveTabName()}";
        }

        [RelayCommand]
        private void RemoveRank() {
            ObservableCollection<XpRow>? collection = GetActiveRankCollection();
            if (collection == null) {
                StatusText = "Select a rank tab (Attributes, Vitals, Trained, or Specialized)";
                return;
            }
            if (collection.Count <= 1) {
                StatusText = $"Cannot remove the last rank from {GetActiveTabName()}";
                return;
            }
            int removed = collection.Count - 1;
            collection.RemoveAt(removed);
            StatusText = $"Removed rank {removed} from {GetActiveTabName()}";
        }

        [RelayCommand]
        private void ToggleAutoScale() {
            IsAutoScaleOpen = !IsAutoScaleOpen;
        }

        private static ulong SafePow(double baseVal, double exp, int i) {
            if (i == 0) return 0;
            double val = baseVal * Math.Pow(i, exp);
            return val > (double)ulong.MaxValue ? ulong.MaxValue : (ulong)val;
        }

        private static uint SafePowUint(double baseVal, double exp, int i) {
            if (i == 0) return 0;
            double val = baseVal * Math.Pow(i, exp);
            return val > uint.MaxValue ? uint.MaxValue : (uint)val;
        }

        [RelayCommand]
        private void GenerateAutoScale() {
            if (!int.TryParse(AutoScaleTotalLevels, out int totalLevels) || totalLevels < 1) {
                StatusText = "Invalid total levels"; return;
            }
            if (!double.TryParse(AutoScaleBaseXp, out double baseXp) || baseXp < 1) {
                StatusText = "Invalid base XP"; return;
            }
            if (!double.TryParse(AutoScaleGrowthRate, out double exponent) || exponent < 1.0) {
                StatusText = "Exponent must be >= 1.0"; return;
            }
            if (!int.TryParse(AutoScaleCreditsEveryN, out int creditsEveryN) || creditsEveryN < 1) {
                StatusText = "Credits-every-N must be >= 1"; return;
            }

            int attrRanks = 190, vitalRanks = 196, skillRanks = 226;
            int.TryParse(AutoScaleAttributeRanks, out attrRanks);
            int.TryParse(AutoScaleVitalRanks, out vitalRanks);
            int.TryParse(AutoScaleSkillRanks, out skillRanks);
            if (attrRanks < 1) attrRanks = 190;
            if (vitalRanks < 1) vitalRanks = 196;
            if (skillRanks < 1) skillRanks = 226;

            // Base counts that are preserved verbatim from the loaded DAT table
            const int baseDefaultLevels = 275;
            const int baseDefaultAttrRanks = 190;
            const int baseDefaultVitalRanks = 196;
            const int baseDefaultSkillRanks = 226;

            // Levels + Skill Credits:
            // Copy the base 1-275 rows from the loaded table unchanged; only generate beyond that.
            Levels.Clear();
            int baseLevelCopy = Math.Min(baseDefaultLevels, totalLevels);
            for (int i = 0; i < baseLevelCopy; i++) {
                if (_table != null && i < _table.Levels.Length) {
                    var credits = i < _table.SkillCredits.Length ? _table.SkillCredits[i].ToString() : "0";
                    Levels.Add(new LevelRow(i, _table.Levels[i].ToString(), credits));
                }
                else {
                    ulong xp = SafePow(baseXp, exponent, i);
                    uint credits = (i > 0 && i % creditsEveryN == 0) ? 1u : 0u;
                    Levels.Add(new LevelRow(i, xp.ToString(), credits.ToString()));
                }
            }
            // Fit the effective base from the last preserved retail level so the power curve
            // continues at the scale the retail data already defines, not from baseXp.
            // effectiveBase = retail[274] / 274^exp  →  level[i] = effectiveBase * i^exp
            double effectiveLevelBase = baseXp;
            if (_table != null && baseLevelCopy > 1) {
                int lastLevelIdx = baseLevelCopy - 1;
                if (lastLevelIdx > 0 && lastLevelIdx < _table.Levels.Length && _table.Levels[lastLevelIdx] > 0)
                    effectiveLevelBase = (double)_table.Levels[lastLevelIdx] / Math.Pow(lastLevelIdx, exponent);
            }
            for (int i = baseLevelCopy; i < totalLevels; i++) {
                double val = effectiveLevelBase * Math.Pow(i, exponent);
                ulong xp = val > (double)ulong.MaxValue ? ulong.MaxValue : (ulong)val;
                uint credits = (i % creditsEveryN == 0) ? 1u : 0u;
                Levels.Add(new LevelRow(i, xp.ToString(), credits.ToString()));
            }

            // Attribute ranks: copy base 190 ranks, extend with curve fitted to last retail rank
            double attrBase = baseXp * 0.25;
            var newAttrs = new ObservableCollection<XpRow>();
            int baseAttrCopy = Math.Min(baseDefaultAttrRanks, attrRanks);
            for (int i = 0; i < baseAttrCopy; i++) {
                if (_table != null && i < _table.Attributes.Length)
                    newAttrs.Add(new XpRow(i, _table.Attributes[i].ToString()));
                else
                    newAttrs.Add(new XpRow(i, SafePowUint(attrBase, exponent, i).ToString()));
            }
            double effectiveAttrBase = attrBase;
            if (_table != null && baseAttrCopy > 1) {
                int lastIdx = baseAttrCopy - 1;
                if (lastIdx > 0 && lastIdx < _table.Attributes.Length && _table.Attributes[lastIdx] > 0)
                    effectiveAttrBase = (double)_table.Attributes[lastIdx] / Math.Pow(lastIdx, exponent);
            }
            for (int i = baseAttrCopy; i < attrRanks; i++) {
                double val = effectiveAttrBase * Math.Pow(i, exponent);
                newAttrs.Add(new XpRow(i, (val > uint.MaxValue ? uint.MaxValue : (uint)val).ToString()));
            }
            Attributes = newAttrs;

            // Vital ranks: copy base 196 ranks, extend with curve fitted to last retail rank
            double vitalBase = baseXp * 0.2;
            var newVitals = new ObservableCollection<XpRow>();
            int baseVitalCopy = Math.Min(baseDefaultVitalRanks, vitalRanks);
            for (int i = 0; i < baseVitalCopy; i++) {
                if (_table != null && i < _table.Vitals.Length)
                    newVitals.Add(new XpRow(i, _table.Vitals[i].ToString()));
                else
                    newVitals.Add(new XpRow(i, SafePowUint(vitalBase, exponent, i).ToString()));
            }
            double effectiveVitalBase = vitalBase;
            if (_table != null && baseVitalCopy > 1) {
                int lastIdx = baseVitalCopy - 1;
                if (lastIdx > 0 && lastIdx < _table.Vitals.Length && _table.Vitals[lastIdx] > 0)
                    effectiveVitalBase = (double)_table.Vitals[lastIdx] / Math.Pow(lastIdx, exponent);
            }
            for (int i = baseVitalCopy; i < vitalRanks; i++) {
                double val = effectiveVitalBase * Math.Pow(i, exponent);
                newVitals.Add(new XpRow(i, (val > uint.MaxValue ? uint.MaxValue : (uint)val).ToString()));
            }
            Vitals = newVitals;

            // Trained skill ranks: copy base 226 ranks, extend with curve fitted to last retail rank
            double trainedBase = baseXp * 0.33;
            var newTrained = new ObservableCollection<XpRow>();
            int baseSkillCopy = Math.Min(baseDefaultSkillRanks, skillRanks);
            for (int i = 0; i < baseSkillCopy; i++) {
                if (_table != null && i < _table.TrainedSkills.Length)
                    newTrained.Add(new XpRow(i, _table.TrainedSkills[i].ToString()));
                else
                    newTrained.Add(new XpRow(i, SafePowUint(trainedBase, exponent, i).ToString()));
            }
            double effectiveTrainedBase = trainedBase;
            if (_table != null && baseSkillCopy > 1) {
                int lastIdx = baseSkillCopy - 1;
                if (lastIdx > 0 && lastIdx < _table.TrainedSkills.Length && _table.TrainedSkills[lastIdx] > 0)
                    effectiveTrainedBase = (double)_table.TrainedSkills[lastIdx] / Math.Pow(lastIdx, exponent);
            }
            for (int i = baseSkillCopy; i < skillRanks; i++) {
                double val = effectiveTrainedBase * Math.Pow(i, exponent);
                newTrained.Add(new XpRow(i, (val > uint.MaxValue ? uint.MaxValue : (uint)val).ToString()));
            }
            TrainedSkills = newTrained;

            // Specialized skill ranks: copy base 226 ranks, extend with curve fitted to last retail rank
            double specBase = baseXp * 0.2;
            var newSpec = new ObservableCollection<XpRow>();
            for (int i = 0; i < baseSkillCopy; i++) {
                if (_table != null && i < _table.SpecializedSkills.Length)
                    newSpec.Add(new XpRow(i, _table.SpecializedSkills[i].ToString()));
                else
                    newSpec.Add(new XpRow(i, SafePowUint(specBase, exponent, i).ToString()));
            }
            double effectiveSpecBase = specBase;
            if (_table != null && baseSkillCopy > 1) {
                int lastIdx = baseSkillCopy - 1;
                if (lastIdx > 0 && lastIdx < _table.SpecializedSkills.Length && _table.SpecializedSkills[lastIdx] > 0)
                    effectiveSpecBase = (double)_table.SpecializedSkills[lastIdx] / Math.Pow(lastIdx, exponent);
            }
            for (int i = baseSkillCopy; i < skillRanks; i++) {
                double val = effectiveSpecBase * Math.Pow(i, exponent);
                newSpec.Add(new XpRow(i, (val > uint.MaxValue ? uint.MaxValue : (uint)val).ToString()));
            }
            SpecializedSkills = newSpec;

            int extendedLevels = Math.Max(0, totalLevels - baseDefaultLevels);
            StatusText = $"Generated: {totalLevels} levels ({baseLevelCopy} preserved + {extendedLevels} new), " +
                         $"{attrRanks} attr, {vitalRanks} vital, {skillRanks} skill ranks";
        }

        private ObservableCollection<XpRow>? GetActiveRankCollection() {
            return SelectedTabIndex switch {
                1 => Attributes,
                2 => Vitals,
                3 => TrainedSkills,
                4 => SpecializedSkills,
                _ => null
            };
        }

        private string GetActiveTabName() {
            return SelectedTabIndex switch {
                1 => "Attributes",
                2 => "Vitals",
                3 => "Trained Skills",
                4 => "Specialized Skills",
                _ => "Unknown"
            };
        }

        [RelayCommand]
        private void Save() {
            if (_table == null || _portalDoc == null) {
                StatusText = "Nothing to save";
                return;
            }

            try {
                _table.Levels = new ulong[Levels.Count];
                _table.SkillCredits = new uint[Levels.Count];
                for (int i = 0; i < Levels.Count; i++) {
                    _table.Levels[i] = ulong.TryParse(Levels[i].XpRequired, out var xp) ? xp : 0;
                    _table.SkillCredits[i] = uint.TryParse(Levels[i].SkillCredits, out var sc) ? sc : 0;
                }

                _table.Attributes = new uint[Attributes.Count];
                for (int i = 0; i < Attributes.Count; i++)
                    _table.Attributes[i] = uint.TryParse(Attributes[i].Value, out var v) ? v : 0;

                _table.Vitals = new uint[Vitals.Count];
                for (int i = 0; i < Vitals.Count; i++)
                    _table.Vitals[i] = uint.TryParse(Vitals[i].Value, out var v) ? v : 0;

                _table.TrainedSkills = new uint[TrainedSkills.Count];
                for (int i = 0; i < TrainedSkills.Count; i++)
                    _table.TrainedSkills[i] = uint.TryParse(TrainedSkills[i].Value, out var v) ? v : 0;

                _table.SpecializedSkills = new uint[SpecializedSkills.Count];
                for (int i = 0; i < SpecializedSkills.Count; i++)
                    _table.SpecializedSkills[i] = uint.TryParse(SpecializedSkills[i].Value, out var v) ? v : 0;

                _portalDoc.SetEntry(ExperienceTableId, _table);
                StatusText = $"Saved: {Levels.Count} levels, {Attributes.Count} attr, " +
                             $"{Vitals.Count} vital, {TrainedSkills.Count} trained, " +
                             $"{SpecializedSkills.Count} specialized. Use File > Export to write DATs.";
            }
            catch (Exception ex) {
                StatusText = $"Save error: {ex.Message}";
            }
        }
    }
}

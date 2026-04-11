using MemoryPack;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;

namespace WorldBuilder.Shared.Documents {
    [MemoryPackable]
    public partial class SpellDbData {
        public Dictionary<uint, SpellDbEntry> Spells = new();
    }

    [MemoryPackable]
    public partial class SpellDbEntry {
        public required SpellRecord Record { get; set; }

        public bool IsModified { get; set; }
        public bool IsNew { get; set; }
    }

    /// <summary>
    /// Stores editable spell DB properties (SpellRecord) in the project.
    /// Persists changes locally (via MemoryPack) without writing to the live database.
    /// Acts as an override layer, falling back to DB when no local entry exists.
    /// Used to stage DB edits before export.
    /// </summary>
    public partial class SpellDbDocument : BaseDocument {
        public override string Type => nameof(SpellDbDocument);

        public const string DocumentId = "spell_db";

        private SpellDbData _data = new();

        public SpellDbDocument(ILogger logger) : base(logger) { }


        public bool TryGet(uint id, out SpellRecord? spell) {
            if (_data.Spells.TryGetValue(id, out var entry)) {
                spell = entry.Record;
                return true;
            }

            spell = null;
            return false;
        }

        public void Set(uint id, SpellRecord spell) {
            _data.Spells[id] = new SpellDbEntry {
                Record = spell
            };

            MarkDirty();
            OnUpdate(new BaseDocumentEvent());
        }

        public void Remove(uint id) {
            if (_data.Spells.Remove(id)) {
                MarkDirty();
                OnUpdate(new BaseDocumentEvent());
            }
        }

        public IEnumerable<uint> GetIds() => _data.Spells.Keys;


        protected override Task<bool> InitInternal(IDatReaderWriter datreader, DocumentManager documentManager) {
            ClearDirty();
            return Task.FromResult(true);
        }

        protected override byte[] SaveToProjectionInternal() {
            return MemoryPackSerializer.Serialize(_data);
        }

        protected override bool LoadFromProjectionInternal(byte[] projection) {
            try {
                _data = MemoryPackSerializer.Deserialize<SpellDbData>(projection) ?? new();
                return true;
            }
            catch {
                _data = new();
                return true;
            }
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            // DB overrides are NOT written to DATs
            return Task.FromResult(true);
        }
    }
}

using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using MemoryPack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Shared.Documents {

    [MemoryPackable]
    public partial class PortalDatData {
        public Dictionary<uint, PortalDatEntry> Entries = new();
    }

    [MemoryPackable]
    public partial class PortalDatEntry {
        public string TypeName = "";
        public byte[] Data = Array.Empty<byte>();
    }

    /// <summary>
    /// Stores modified portal DAT table entries (SpellTable, SkillTable, VitalTable, etc.)
    /// in the project document system, following the same pattern as DungeonDocument / TerrainDocument.
    /// Editors save changes here; the export pipeline writes them to DAT files.
    /// </summary>
    public partial class PortalDatDocument : BaseDocument {
        public override string Type => nameof(PortalDatDocument);

        public const string DocumentId = "portal_tables";
        private const int PackBufferSize = 16 * 1024 * 1024;

        private PortalDatData _data = new();
        private readonly Dictionary<uint, object> _objectCache = new();

        public PortalDatDocument(ILogger logger) : base(logger) { }

        public bool HasEntry(uint fileId) =>
            _objectCache.ContainsKey(fileId) || _data.Entries.ContainsKey(fileId);

        public int EntryCount => _data.Entries.Count;

        /// <summary>
        /// Store a modified IDBObj in the document. Packs the object to bytes for
        /// project persistence and caches the live object for in-session access.
        /// </summary>
        public void SetEntry<T>(uint fileId, T obj) where T : IDBObj, new() {
            _objectCache[fileId] = obj;

            try {
                var buffer = new byte[PackBufferSize];
                var writer = new DatBinWriter(buffer.AsMemory());
                ((IPackable)obj).Pack(writer);
                _data.Entries[fileId] = new PortalDatEntry {
                    TypeName = typeof(T).Name,
                    Data = buffer[..writer.Offset]
                };
            }
            catch (Exception ex) {
                _logger.LogError(ex, "[PortalDatDoc] Failed to pack entry 0x{FileId:X8}", fileId);
                _data.Entries[fileId] = new PortalDatEntry {
                    TypeName = typeof(T).Name,
                    Data = Array.Empty<byte>()
                };
            }

            MarkDirty();
            OnUpdate(new BaseDocumentEvent());
        }

        /// <summary>
        /// Retrieve a previously stored entry. Returns from the in-memory cache if available,
        /// otherwise unpacks from the persisted bytes.
        /// </summary>
        public bool TryGetEntry<T>(uint fileId, out T? obj) where T : IDBObj, new() {
            if (_objectCache.TryGetValue(fileId, out var cached) && cached is T typed) {
                obj = typed;
                return true;
            }

            if (_data.Entries.TryGetValue(fileId, out var entry) && entry.Data.Length > 0) {
                try {
                    obj = new T();
                    var reader = new DatBinReader(entry.Data);
                    ((IUnpackable)obj).Unpack(reader);
                    _objectCache[fileId] = obj;
                    return true;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "[PortalDatDoc] Failed to unpack entry 0x{FileId:X8}", fileId);
                }
            }

            obj = default;
            return false;
        }

        public void RemoveEntry(uint fileId) {
            _data.Entries.Remove(fileId);
            _objectCache.Remove(fileId);
            MarkDirty();
            OnUpdate(new BaseDocumentEvent());
        }

        protected override Task<bool> InitInternal(IDatReaderWriter datreader, DocumentManager documentManager) {
            ClearDirty();
            return Task.FromResult(true);
        }

        protected override byte[] SaveToProjectionInternal() {
            SyncCacheToData();
            return MemoryPackSerializer.Serialize(_data);
        }

        protected override bool LoadFromProjectionInternal(byte[] projection) {
            try {
                _data = MemoryPackSerializer.Deserialize<PortalDatData>(projection) ?? new();
                _objectCache.Clear();
                return true;
            }
            catch (MemoryPackSerializationException) {
                _logger.LogWarning("[PortalDatDoc] Project cache has incompatible format (schema changed), will reset to empty");
                _data = new();
                _objectCache.Clear();
                return true;
            }
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            SyncCacheToData();

            foreach (var (fileId, entry) in _data.Entries) {
                bool saved = false;

                if (_objectCache.TryGetValue(fileId, out var cachedObj)) {
                    saved = TrySaveTyped(datwriter, cachedObj, iteration);
                }

                if (!saved && entry.Data.Length > 0) {
                    saved = TrySaveFromBytes(datwriter, entry, iteration);
                }

                if (saved) {
                    _logger.LogInformation("[PortalDatDoc] Exported 0x{FileId:X8} ({Type})", fileId, entry.TypeName);
                }
                else {
                    _logger.LogError("[PortalDatDoc] Failed to export 0x{FileId:X8} ({Type})", fileId, entry.TypeName);
                }
            }

            return Task.FromResult(true);
        }

        /// <summary>
        /// Re-pack any cached objects that may have been mutated in-place by editors.
        /// </summary>
        private void SyncCacheToData() {
            foreach (var (fileId, obj) in _objectCache) {
                if (!_data.Entries.TryGetValue(fileId, out var entry)) continue;
                try {
                    var buffer = new byte[PackBufferSize];
                    var writer = new DatBinWriter(buffer.AsMemory());
                    ((IPackable)obj).Pack(writer);
                    entry.Data = buffer[..writer.Offset];
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "[PortalDatDoc] Failed to re-pack entry 0x{FileId:X8} during sync", fileId);
                }
            }
        }

        private static bool TrySaveTyped(IDatReaderWriter writer, object obj, int iteration) {
            return obj switch {
                SpellTable t => writer.TrySave(t, iteration),
                VitalTable t => writer.TrySave(t, iteration),
                SkillTable t => writer.TrySave(t, iteration),
                ExperienceTable t => writer.TrySave(t, iteration),
                CharGen t => writer.TrySave(t, iteration),
                _ => false
            };
        }

        private bool TrySaveFromBytes(IDatReaderWriter writer, PortalDatEntry entry, int iteration) {
            try {
                return entry.TypeName switch {
                    nameof(SpellTable) => UnpackAndSave<SpellTable>(writer, entry.Data, iteration),
                    nameof(VitalTable) => UnpackAndSave<VitalTable>(writer, entry.Data, iteration),
                    nameof(SkillTable) => UnpackAndSave<SkillTable>(writer, entry.Data, iteration),
                    nameof(ExperienceTable) => UnpackAndSave<ExperienceTable>(writer, entry.Data, iteration),
                    nameof(CharGen) => UnpackAndSave<CharGen>(writer, entry.Data, iteration),
                    _ => false
                };
            }
            catch (Exception ex) {
                _logger.LogError(ex, "[PortalDatDoc] Failed to unpack-and-save {Type}", entry.TypeName);
                return false;
            }
        }

        private static bool UnpackAndSave<T>(IDatReaderWriter writer, byte[] data, int iteration)
            where T : IDBObj, new() {
            var obj = new T();
            var reader = new DatBinReader(data);
            ((IUnpackable)obj).Unpack(reader);
            return writer.TrySave(obj, iteration);
        }
    }
}

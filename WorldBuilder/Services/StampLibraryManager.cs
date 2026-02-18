using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Services {
    public class StampLibraryManager {
        private const string StampDirectory = "Stamps";
        private readonly WorldBuilderSettings _settings;
        private readonly ObservableCollection<TerrainStamp> _stamps = new();

        public ObservableCollection<TerrainStamp> Stamps => _stamps;

        public StampLibraryManager(WorldBuilderSettings settings) {
            _settings = settings;
            LoadStampsFromDisk();
        }

        public void SaveStamp(TerrainStamp stamp, string filename) {
            Directory.CreateDirectory(StampDirectory);
            var path = Path.Combine(StampDirectory, $"{filename}.stamp");
            var thumbPath = Path.Combine(StampDirectory, $"{filename}.png");

            // Set filename on the stamp object so we can delete it later
            stamp.Filename = path;

            // Generate thumbnail
            try {
                StampThumbnailGenerator.GenerateThumbnail(stamp, thumbPath);
            }
            catch (Exception ex) {
                Console.WriteLine($"[StampLibrary] Failed to generate thumbnail: {ex.Message}");
            }

            using (var writer = new BinaryWriter(File.OpenWrite(path))) {
                // Header
                writer.Write("ACSTAMP"); // Magic bytes
                writer.Write((byte)1);   // Version

                // Metadata
                writer.Write(stamp.Name);
                writer.Write(stamp.Description);
                writer.Write(stamp.Created.ToBinary());
                writer.Write((ushort)stamp.WidthInVertices);
                writer.Write((ushort)stamp.HeightInVertices);
                writer.Write(stamp.OriginalWorldPosition.X);
                writer.Write(stamp.OriginalWorldPosition.Y);
                writer.Write(stamp.SourceLandblockId);

                // Height data
                writer.Write(stamp.Heights.Length);
                writer.Write(stamp.Heights);

                // Terrain type data
                writer.Write(stamp.TerrainTypes.Length);
                foreach (var terrain in stamp.TerrainTypes) {
                    writer.Write(terrain);
                }

                // Objects (optional)
                writer.Write(stamp.Objects.Count);
                foreach (var obj in stamp.Objects) {
                    WriteStaticObject(writer, obj);
                }
            }

            // Add to memory at the top
            _stamps.Insert(0, stamp);

            // Enforce limit
            while (_stamps.Count > _settings.Landscape.Stamps.MaxStamps) {
                var oldest = _stamps[_stamps.Count - 1];
                _stamps.RemoveAt(_stamps.Count - 1);

                if (!string.IsNullOrEmpty(oldest.Filename)) {
                    DeleteStampFiles(oldest.Filename);
                }
            }
        }

        public void DeleteStamp(TerrainStamp stamp) {
            if (_stamps.Contains(stamp)) {
                _stamps.Remove(stamp);
            }

            if (!string.IsNullOrEmpty(stamp.Filename)) {
                DeleteStampFiles(stamp.Filename);
            }
        }

        private void DeleteStampFiles(string stampPath) {
            if (File.Exists(stampPath)) {
                try {
                    File.Delete(stampPath);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[StampLibrary] Failed to delete stamp file {stampPath}: {ex.Message}");
                }
            }

            var thumbPath = Path.ChangeExtension(stampPath, ".png");
            if (File.Exists(thumbPath)) {
                try {
                    File.Delete(thumbPath);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[StampLibrary] Failed to delete thumbnail {thumbPath}: {ex.Message}");
                }
            }
        }

        public TerrainStamp? LoadStamp(string filename) {
            var path = Path.Combine(StampDirectory, $"{filename}.stamp");
            if (!File.Exists(path)) return null;

            using var reader = new BinaryReader(File.OpenRead(path));

            // Validate header
            var magic = reader.ReadString();
            if (magic != "ACSTAMP") return null;

            var version = reader.ReadByte();
            if (version != 1) return null;

            // Read metadata
            var stamp = new TerrainStamp {
                Name = reader.ReadString(),
                Description = reader.ReadString(),
                Created = DateTime.FromBinary(reader.ReadInt64()),
                WidthInVertices = reader.ReadUInt16(),
                HeightInVertices = reader.ReadUInt16(),
                OriginalWorldPosition = new Vector2(
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                SourceLandblockId = reader.ReadUInt16()
            };

            // Set filename so we can delete it later if needed
            stamp.Filename = path;

            // Read height data
            int heightCount = reader.ReadInt32();
            stamp.Heights = reader.ReadBytes(heightCount);

            // Read terrain types
            int terrainCount = reader.ReadInt32();
            stamp.TerrainTypes = new ushort[terrainCount];
            for (int i = 0; i < terrainCount; i++) {
                stamp.TerrainTypes[i] = reader.ReadUInt16();
            }

            // Read objects
            int objectCount = reader.ReadInt32();
            for (int i = 0; i < objectCount; i++) {
                stamp.Objects.Add(ReadStaticObject(reader));
            }

            return stamp.IsValid() ? stamp : null;
        }

        private void LoadStampsFromDisk() {
            if (!Directory.Exists(StampDirectory)) return;

            var loadedStamps = new List<TerrainStamp>();
            foreach (var file in Directory.GetFiles(StampDirectory, "*.stamp")) {
                var filename = Path.GetFileNameWithoutExtension(file);
                var stamp = LoadStamp(filename);
                if (stamp != null) {
                    loadedStamps.Add(stamp);
                }
            }

            // Sort by creation date descending (newest first)
            loadedStamps.Sort((a, b) => b.Created.CompareTo(a.Created));

            foreach (var stamp in loadedStamps) {
                _stamps.Add(stamp);
            }
        }

        private void WriteStaticObject(BinaryWriter writer, StaticObject obj) {
            writer.Write(obj.Id);
            writer.Write(obj.IsSetup);
            writer.Write(obj.Origin.X);
            writer.Write(obj.Origin.Y);
            writer.Write(obj.Origin.Z);
            writer.Write(obj.Orientation.X);
            writer.Write(obj.Orientation.Y);
            writer.Write(obj.Orientation.Z);
            writer.Write(obj.Orientation.W);
            writer.Write(obj.Scale.X);
            writer.Write(obj.Scale.Y);
            writer.Write(obj.Scale.Z);
        }

        private StaticObject ReadStaticObject(BinaryReader reader) {
            return new StaticObject {
                Id = reader.ReadUInt32(),
                IsSetup = reader.ReadBoolean(),
                Origin = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                Orientation = new Quaternion(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                Scale = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle())
            };
        }
    }
}

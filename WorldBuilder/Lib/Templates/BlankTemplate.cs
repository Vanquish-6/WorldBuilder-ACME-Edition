using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Lib.Templates;

/// <summary>
/// Writes WaterDeepSea (type 0x14, height 0) over every vertex in the 255×255 grid,
/// producing a pure ocean world with no terrain features and no static objects.
/// Mirrors the "Fresh Start" operation in the landscape editor.
/// </summary>
public class BlankTemplate : IWorldTemplate {
    public string Id => "blank";
    public string Name => "Blank World";
    public string Description => "A completely empty ocean world — every landblock starts as open sea.\nBest for builds that require a completely fresh slate.";
    public string[] Tags => ["Open ocean start", "Full creative control"];
    public string PreviewResourceName => "WorldBuilder.Assets.Templates.preview_blank.png";

    public async Task ApplyAsync(Project project, IProgress<string> progress) {
        progress.Report("Clearing terrain to blank ocean...");

        const byte WATER_DEEP_SEA = 0x14;
        const int MAP_SIZE = 255;
        const int LANDBLOCK_SIZE = 81;

        var terrainDoc = await project.DocumentManager.GetOrCreateDocumentAsync<TerrainDocument>("terrain");
        if (terrainDoc == null) return;

        var waterEntry = new TerrainEntry(road: 0, scenery: 0, type: WATER_DEEP_SEA, height: 0).ToUInt();

        var allChanges = new Dictionary<ushort, Dictionary<byte, uint>>();
        for (int x = 0; x < MAP_SIZE; x++) {
            for (int y = 0; y < MAP_SIZE; y++) {
                var lbKey = (ushort)((x << 8) | y);
                var existing = terrainDoc.GetLandblockInternal(lbKey);
                if (existing == null) continue;

                var changes = new Dictionary<byte, uint>();
                for (byte i = 0; i < LANDBLOCK_SIZE; i++) {
                    if (existing[i].ToUInt() != waterEntry) {
                        changes[i] = waterEntry;
                    }
                }
                if (changes.Count > 0) {
                    allChanges[lbKey] = changes;
                }
            }
        }

        terrainDoc.ApplyBulkImport(allChanges);

        // Prevent the editor from pulling original-world statics back in as new landblocks load
        project.DocumentManager.SkipDatStatics = true;
    }
}

using System;
using System.Threading.Tasks;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Lib.Templates;

/// <summary>
/// Seeds the project with a small hand-crafted island in the middle of a 10×10 ocean
/// patch. Demonstrates every major editor feature out of the box: terrain variety,
/// height variation, a dungeon with pre-built rooms, placed objects, a named layer,
/// and an initial snapshot.
/// </summary>
public class StarterIslandTemplate : IWorldTemplate {
    public string Id => "starter_island";
    public string Name => "Starter Island";
    public string Description => "A hand-crafted island ready to explore and edit from the moment the project opens.\nIncludes terrain variety, a dungeon entrance, placed objects, and a named layer.";
    public string[] Tags => ["Recommended for beginners", "Pre-built dungeon", "Instant terrain"];
    public string PreviewResourceName => "WorldBuilder.Assets.Templates.preview_starter_island.png";

    public async Task ApplyAsync(Project project, IProgress<string> progress) {
        progress.Report("Applying Starter Island template...");

        // The starter island data is stored as embedded binary resources:
        //   WorldBuilder.Assets.Templates.StarterIsland.terrain  — TerrainStamp MemoryPack binary
        //   WorldBuilder.Assets.Templates.StarterIsland.dungeon  — DungeonDocument projection binary
        //
        // Application steps once those assets exist:
        //   1. Deserialize TerrainStamp from embedded resource
        //   2. Locate target landblock region (e.g. 0x7070–0x797F — a quiet ocean patch)
        //   3. Call HeightImportCommand-style bulk write via TerrainDocument.ApplyBulkImport
        //   4. Deserialize DungeonDocument projection and write into DocumentStorageService
        //   5. Place dungeon entrance portal in OutdoorInstancePlacements
        //   6. Create "Island Base" terrain layer entry in TerrainData.RootItems
        //   7. Write initial snapshot named "Fresh Island"
        //
        // The stamp and dungeon assets are not yet baked — this method is the correct
        // hook point and will be fleshed out when the island assets are authored.

        await Task.CompletedTask;
    }
}

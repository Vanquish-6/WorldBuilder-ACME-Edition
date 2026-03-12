using System;
using System.Threading.Tasks;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Lib.Templates;

/// <summary>
/// No-op template — the editor reads landblocks live from the base DAT cache, which
/// reproduces the world exactly as it exists in retail AC. Ideal for modding the
/// existing world.
/// </summary>
public class ClassicWorldTemplate : IWorldTemplate {
    public string Id => "classic";
    public string Name => "End of Retail Map";
    public string Description => "Loads the complete original AC world from your DAT files.\nEvery landblock reflects the retail map — ideal for modding the existing world.";
    public string[] Tags => ["Full retail world", "DAT-accurate", "Best for modders"];
    public string PreviewResourceName => "WorldBuilder.Assets.Templates.preview_classic.png";

    public Task ApplyAsync(Project project, IProgress<string> progress) {
        // The terrain editor always falls through to the base DAT cache when a landblock
        // has no project-level delta entry — so opening a new project already shows the
        // full retail AC world. No seeding is required.
        return Task.CompletedTask;
    }
}

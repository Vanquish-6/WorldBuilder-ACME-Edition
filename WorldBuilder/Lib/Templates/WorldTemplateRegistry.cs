using System.Collections.Generic;
using System.Linq;

namespace WorldBuilder.Lib.Templates;

public static class WorldTemplateRegistry {
    private static readonly List<IWorldTemplate> _templates =
    [
        new ClassicWorldTemplate(),
        new StarterIslandTemplate(),
        new BlankTemplate(),
    ];

    public static IReadOnlyList<IWorldTemplate> All => _templates;

    public static IWorldTemplate Default => _templates[0];

    public static IWorldTemplate? GetById(string id) =>
        _templates.FirstOrDefault(t => t.Id == id);
}

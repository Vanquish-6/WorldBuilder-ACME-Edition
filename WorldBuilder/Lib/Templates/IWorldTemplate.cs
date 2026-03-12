using System;
using System.Threading.Tasks;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Lib.Templates;

public interface IWorldTemplate {
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string[] Tags { get; }
    string PreviewResourceName { get; }

    Task ApplyAsync(Project project, IProgress<string> progress);
}

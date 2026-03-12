using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Editors.Landscape.Views;
using WorldBuilder.Editors.CharGen;
using WorldBuilder.Editors.Experience;
using WorldBuilder.Editors.Layout;
using WorldBuilder.Editors.Skill;
using WorldBuilder.Editors.Spell;
using WorldBuilder.Editors.SpellSet;
using WorldBuilder.Editors.Vital;
using WorldBuilder.Lib.Factories;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Services;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Models;
using WorldBuilder.Shared.Services;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Lib.Extensions {
    public static class ServiceCollectionExtensions {
        public static void AddCommonServices(this IServiceCollection collection) {
            collection.AddLogging((c) => c.AddProvider(new ColorConsoleLoggerProvider()));

            collection.AddSingleton<ProjectManager>();
            collection.AddSingleton<WorldBuilderSettings>();
            collection.AddSingleton<SplashPageFactory>();

            // splash page
            collection.AddTransient<RecentProject>();
            collection.AddTransient<CreateProjectViewModel>();
            collection.AddTransient<TemplateSelectionViewModel>();
            collection.AddTransient<ProjectLoadingViewModel>();
            collection.AddTransient<SplashPageViewModel>();
            collection.AddTransient<ProjectSelectionViewModel>();

            // app
            collection.AddTransient<MainViewModel>();
        }

        public static void AddProjectServices(this IServiceCollection collection, Project project, IServiceProvider rootProvider) {
            collection.AddDbContext<DocumentDbContext>(
                o => {
                    o.UseSqlite($"DataSource={project.DatabasePath}");
                },
                ServiceLifetime.Scoped);

            collection.AddLogging((c) => c.AddProvider(new ColorConsoleLoggerProvider()));

            collection.AddSingleton(rootProvider.GetRequiredService<WorldBuilderSettings>());
            collection.AddSingleton(rootProvider.GetRequiredService<ProjectManager>());

            collection.AddSingleton<DocumentManager>();
            collection.AddSingleton<IDocumentStorageService, DocumentStorageService>();
            collection.AddSingleton(project);
            collection.AddSingleton(project.CustomTextures);
            collection.AddSingleton<TextureImportService>(sp => {
                var svc = new TextureImportService(project.CustomTextures, project);
                project.OnExportCustomTextures = (writer, iteration) => {
                    svc.WriteToDats(writer, iteration);
                    svc.UpdateRegionForTerrainReplacements(writer, iteration);
                };
                return svc;
            });
            collection.AddSingleton<InstanceRepositionService>();
            collection.AddSingleton<LandscapeEditorViewModel>();
            collection.AddSingleton<DungeonEditorViewModel>();
            collection.AddSingleton<SpellEditorViewModel>();
            collection.AddSingleton<SpellSetEditorViewModel>();
            collection.AddSingleton<SkillEditorViewModel>();
            collection.AddSingleton<ExperienceEditorViewModel>();
            collection.AddSingleton<VitalEditorViewModel>();
            collection.AddSingleton<CharGenEditorViewModel>();
            collection.AddSingleton<LayoutEditorViewModel>();
            collection.AddTransient<ObjectDebugViewModel>();
            collection.AddTransient<HistorySnapshotPanelViewModel>();
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Editors.ObjectDebug;
using WorldBuilder.Editors.CharGen;
using WorldBuilder.Editors.Experience;
using WorldBuilder.Editors.Layout;
using WorldBuilder.Editors.Skill;
using WorldBuilder.Editors.Spell;
using WorldBuilder.Editors.SpellSet;
using WorldBuilder.Editors.Vital;
using WorldBuilder.Editors.Monster;
using WorldBuilder.Editors.Weenie;
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
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ACME WorldBuilder");
            var logDir = Path.Combine(appDataDir, "Logs");

            long maxLogBytes = 5L * 1024 * 1024;
            try {
                var settingsPath = Path.Combine(appDataDir, "settings.json");
                if (File.Exists(settingsPath)) {
                    using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                    if (doc.RootElement.TryGetProperty("App", out var app)
                        && app.TryGetProperty("MaxLogFileSizeMb", out var mb)
                        && mb.TryGetInt32(out var sizeMb) && sizeMb >= 1) {
                        maxLogBytes = sizeMb * 1024L * 1024;
                    }
                }
            }
            catch { }

            var fileLoggerProvider = new FileLoggerProvider(logDir, maxLogBytes);

            collection.AddLogging((c) => {
                c.AddProvider(new ColorConsoleLoggerProvider());
                c.AddProvider(fileLoggerProvider);
            });

            collection.AddSingleton(fileLoggerProvider);

            collection.AddSingleton<ProjectManager>();
            collection.AddSingleton<WorldBuilderSettings>();
            collection.AddSingleton<SplashPageFactory>();

            // splash page
            collection.AddTransient<RecentProject>();
            collection.AddTransient<CreateProjectViewModel>();
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

            var fileLoggerProvider = rootProvider.GetRequiredService<FileLoggerProvider>();
            collection.AddLogging((c) => {
                c.AddProvider(new ColorConsoleLoggerProvider());
                c.AddProvider(fileLoggerProvider);
            });

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
            collection.AddSingleton<ObjectDebugEditorViewModel>();
            collection.AddSingleton<WeenieEditorViewModel>();
            collection.AddSingleton<MonsterEditorViewModel>();
            collection.AddTransient<HistorySnapshotPanelViewModel>();
        }
    }
}

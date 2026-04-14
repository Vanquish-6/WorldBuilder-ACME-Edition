using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace WorldBuilder.Lib.Settings {
    [SettingCategory("Application", Order = 0)]
    public partial class AppSettings : ObservableObject {
        [SettingDescription("Directory where all WorldBuilder projects are stored")]
        [SettingPath(PathType.Folder, DialogTitle = "Select Projects Directory")]
        [SettingOrder(0)]
        private string _projectsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            "ACME WorldBuilder",
            "Projects"
        );
        public string ProjectsDirectory { get => _projectsDirectory; set => SetProperty(ref _projectsDirectory, value); }

        [SettingDescription("Minimum log level for application logging")]
        [SettingOrder(1)]
        private LogLevel _logLevel = LogLevel.Information;
        public LogLevel LogLevel { get => _logLevel; set => SetProperty(ref _logLevel, value); }

        [SettingDescription("Enable verbose logging for database queries (may impact performance)")]
        [SettingOrder(2)]
        private bool _logDatabaseQueries = false;
        public bool LogDatabaseQueries { get => _logDatabaseQueries; set => SetProperty(ref _logDatabaseQueries, value); }

        [SettingDescription("Write application logs to a file (useful for sharing with developers when reporting issues)")]
        [SettingOrder(3)]
        private bool _enableFileLogging = true;
        public bool EnableFileLogging { get => _enableFileLogging; set => SetProperty(ref _enableFileLogging, value); }

        [SettingDescription("Maximum log file size in megabytes before rotating (one backup is kept)")]
        [SettingRange(1, 50, 1, 5)]
        [SettingFormat("{0:F0} MB")]
        [SettingOrder(4)]
        private int _maxLogFileSizeMb = 5;
        public int MaxLogFileSizeMb { get => _maxLogFileSizeMb; set => SetProperty(ref _maxLogFileSizeMb, value); }

        [SettingDescription("Maximum number of history items to keep")]
        [SettingRange(5, 10000, 1, 100)]
        [SettingFormat("{0:F0}")]
        [SettingOrder(5)]
        private int _historyLimit = 50;
        public int HistoryLimit { get => _historyLimit; set => SetProperty(ref _historyLimit, value); }
    }
}
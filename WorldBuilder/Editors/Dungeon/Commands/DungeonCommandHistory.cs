using System;
using System.Collections.Generic;
using WorldBuilder.Shared.Documents;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {
    public class DungeonCommandHistory {
        private readonly List<(IDungeonCommand Command, DateTime Timestamp)> _history = new();
        private int _currentIndex = -1;
        private int _trimmedCount;

        public bool CanUndo => _currentIndex >= 0;
        public bool CanRedo => _currentIndex < _history.Count - 1;
        public int CurrentIndex => _currentIndex;

        public string? LastCommandDescription =>
            _currentIndex >= 0 ? _history[_currentIndex].Command.Description : null;

        public int HistoryLimit { get; set; } = 50;

        public event EventHandler? Changed;

        public void Execute(IDungeonCommand command, DungeonDocument document) {
            command.Execute(document);

            if (_currentIndex < _history.Count - 1) {
                _history.RemoveRange(_currentIndex + 1, _history.Count - _currentIndex - 1);
            }

            _history.Add((command, DateTime.UtcNow));
            _currentIndex++;
            TrimHistory(document);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Record an already-executed command for undo/redo without calling Execute again.
        /// </summary>
        public void Record(IDungeonCommand command) {
            if (_currentIndex < _history.Count - 1) {
                _history.RemoveRange(_currentIndex + 1, _history.Count - _currentIndex - 1);
            }

            _history.Add((command, DateTime.UtcNow));
            _currentIndex++;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Undo(DungeonDocument document) {
            if (_currentIndex < 0) return;
            _history[_currentIndex].Command.Undo(document);
            _currentIndex--;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Redo(DungeonDocument document) {
            if (_currentIndex >= _history.Count - 1) return;
            _currentIndex++;
            _history[_currentIndex].Command.Execute(document);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void JumpToHistory(int targetIndex, DungeonDocument document) {
            if (targetIndex < -1 || targetIndex >= _history.Count) return;
            if (targetIndex == _currentIndex) return;

            while (_currentIndex > targetIndex) {
                _history[_currentIndex].Command.Undo(document);
                _currentIndex--;
            }

            while (_currentIndex < targetIndex) {
                _currentIndex++;
                _history[_currentIndex].Command.Execute(document);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public List<HistoryListItem> GetHistoryList() {
            var items = new List<HistoryListItem> {
                new HistoryListItem {
                    Index = -1,
                    Description = _trimmedCount > 0
                        ? $"Earliest State ({_trimmedCount} trimmed)"
                        : "Original Document",
                    Timestamp = DateTime.MinValue,
                    IsCurrent = _currentIndex == -1,
                    IsSnapshot = false
                }
            };

            for (int i = 0; i < _history.Count; i++) {
                items.Add(new HistoryListItem {
                    Index = i,
                    Description = _history[i].Command.Description,
                    Timestamp = _history[i].Timestamp,
                    IsCurrent = _currentIndex == i,
                    IsSnapshot = false,
                    IsDimmed = i > _currentIndex
                });
            }

            return items;
        }

        public void Clear() {
            _history.Clear();
            _currentIndex = -1;
            _trimmedCount = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void TrimHistory(DungeonDocument document) {
            while (HistoryLimit > 0 && _history.Count > HistoryLimit) {
                _history.RemoveAt(0);
                _currentIndex--;
                _trimmedCount++;
            }
            if (_currentIndex < -1) _currentIndex = -1;
        }
    }
}

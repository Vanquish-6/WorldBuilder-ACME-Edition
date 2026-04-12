using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace WorldBuilder.Editors.Layout {

    /// <summary>
    /// Resolves <see cref="MediaDescImage"/> surface IDs from UI <see cref="ElementDesc"/> / <see cref="StateDesc"/> data.
    /// </summary>
    public static class LayoutMediaHelper {

        public static uint NormalizeSurfaceId(uint id) {
            if (id == 0) return 0;
            if ((id & 0xFFFF0000u) == 0 && id <= 0xFFFFu)
                return id | 0x08000000u;
            return id;
        }

        public static uint? TryFirstImageSurfaceId(StateDesc? state) {
            if (state?.Media == null) return null;
            foreach (var m in state.Media) {
                if (m is MediaDescImage img) {
                    var n = NormalizeSurfaceId(img.File);
                    return n == 0 ? null : n;
                }
            }
            return null;
        }

        /// <summary>
        /// Picks a representative surface for layout preview: default UI state, then template <see cref="ElementDesc.StateDesc"/>, then any state.
        /// </summary>
        public static uint? TryPrimarySurfaceForElement(ElementDesc el) {
            if (el.States != null && el.States.TryGetValue(el.DefaultState, out var defState)) {
                var s = TryFirstImageSurfaceId(defState);
                if (s.HasValue) return s;
            }

            var fromTemplate = TryFirstImageSurfaceId(el.StateDesc);
            if (fromTemplate.HasValue) return fromTemplate;

            if (el.States != null) {
                foreach (var kv in el.States.OrderBy(k => (uint)k.Key)) {
                    var s = TryFirstImageSurfaceId(kv.Value);
                    if (s.HasValue) return s;
                }
            }

            return null;
        }

        public static void PopulateStateRows(ElementDesc el, ObservableCollection<LayoutStateRow> rows, DatCollection? stringDats = null,
            IReadOnlyList<uint>? stringTableIdsInLocal = null) {
            rows.Clear();
            rows.Add(MakeRow("Template", el.StateDesc, el, stringDats, stringTableIdsInLocal));
            if (el.States == null) return;
            foreach (var kv in el.States.OrderBy(k => (uint)k.Key)) {
                rows.Add(MakeRow($"UIState 0x{(uint)kv.Key:X8}", kv.Value, el, stringDats, stringTableIdsInLocal));
            }
        }

        static LayoutStateRow MakeRow(string label, StateDesc sd, ElementDesc owner, DatCollection? stringDats,
            IReadOnlyList<uint>? stringTableIdsInLocal) {
            var surf = TryFirstImageSurfaceId(sd);
            string mediaSummary = stringDats != null
                ? LayoutUiStringResolver.BuildMediaSummary(sd, stringDats, owner, stringTableIdsInLocal)
                : sd.Media is { Count: > 0 }
                    ? string.Join(", ",
                        sd.Media.Select(m => m.GetType().Name.Replace("MediaDesc", "", StringComparison.Ordinal)))
                    : "—";

            return new LayoutStateRow {
                RowLabel = label,
                StateRecordIdHex = $"0x{sd.StateId:X8}",
                FirstImageSurfaceHex = surf.HasValue ? $"0x{surf.Value:X8}" : null,
                MediaSummary = mediaSummary
            };
        }
    }

    public class LayoutStateRow {
        public string RowLabel { get; init; } = "";
        public string StateRecordIdHex { get; init; } = "";
        public string? FirstImageSurfaceHex { get; init; }
        public string FirstImageSurfaceDisplay => FirstImageSurfaceHex ?? "—";
        public string MediaSummary { get; init; } = "";
    }
}

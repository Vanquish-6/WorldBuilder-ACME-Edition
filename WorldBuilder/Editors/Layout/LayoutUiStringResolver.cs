using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WorldBuilder.Editors.Layout {

    /// <summary>
    /// Resolves UI copy from <see cref="MediaDescMessage"/> and/or <see cref="StringInfoBaseProperty"/> via
    /// <see cref="StringTable"/> in <c>client_local_English.dat</c>: message id or <see cref="StringInfo.StringId"/>
    /// as the key in <see cref="StringTable.Strings"/>.
    /// Walks <see cref="ElementDesc.BaseLayoutId"/> / <see cref="ElementDesc.BaseElement"/> when the open layout’s
    /// element is a thin override (strings defined on the referenced layout).
    /// </summary>
    public static class LayoutUiStringResolver {

        /// <param name="containingLayoutId">DID of the <see cref="LayoutDesc"/> file that owns this element occurrence (the layout open in the editor). Used for cycle detection and inheritance; pass 0 to skip base-layout walk.</param>
        public static string? TryGetElementCaption(DatCollection? dats, ElementDesc element,
            IReadOnlyList<uint>? stringTableIdsInLocal = null, uint containingLayoutId = 0) {
            if (dats == null) return null;

            var visited = new HashSet<(uint layout, uint element)>();
            ElementDesc? cur = element;
            uint layoutFileId = containingLayoutId;

            while (cur != null) {
                if (layoutFileId != 0) {
                    if (!visited.Add((layoutFileId, cur.ElementId)))
                        break;
                }

                var caption = TryResolveCaptionOnElementOnly(dats, cur, stringTableIdsInLocal);
                if (!string.IsNullOrWhiteSpace(caption))
                    return caption;

                if (cur.BaseLayoutId == 0 || cur.BaseElement == 0)
                    break;

                if (!dats.TryGet<LayoutDesc>(cur.BaseLayoutId, out var baseLayout) || baseLayout?.Elements == null)
                    break;

                ElementDesc? next = null;
                if (baseLayout.Elements.TryGetValue(cur.BaseElement, out var byKey))
                    next = byKey;
                else {
                    foreach (var cand in baseLayout.Elements.Values) {
                        if (cand != null && cand.ElementId == cur.BaseElement) {
                            next = cand;
                            break;
                        }
                    }
                }

                if (next == null)
                    break;

                layoutFileId = cur.BaseLayoutId;
                cur = next;
            }

            return null;
        }

        /// <summary>First non-zero <see cref="ElementDesc.Type"/> along this element and its base-layout chain.</summary>
        public static uint? TryResolveEffectiveType(DatCollection? dats, ElementDesc element, uint containingLayoutId = 0) {
            if (dats == null) return null;

            var visited = new HashSet<(uint layout, uint element)>();
            ElementDesc? cur = element;
            uint layoutFileId = containingLayoutId;

            while (cur != null) {
                if (cur.Type != 0)
                    return cur.Type;

                if (layoutFileId != 0) {
                    if (!visited.Add((layoutFileId, cur.ElementId)))
                        break;
                }

                if (cur.BaseLayoutId == 0 || cur.BaseElement == 0)
                    break;

                if (!dats.TryGet<LayoutDesc>(cur.BaseLayoutId, out var baseLayout) || baseLayout?.Elements == null)
                    break;

                ElementDesc? next = null;
                if (baseLayout.Elements.TryGetValue(cur.BaseElement, out var byKey))
                    next = byKey;
                else {
                    foreach (var cand in baseLayout.Elements.Values) {
                        if (cand != null && cand.ElementId == cur.BaseElement) {
                            next = cand;
                            break;
                        }
                    }
                }

                if (next == null)
                    break;

                layoutFileId = cur.BaseLayoutId;
                cur = next;
            }

            return null;
        }

        /// <summary>MediaDescMessage then StringInfo on this <see cref="ElementDesc"/> only (no inheritance).</summary>
        static string? TryResolveCaptionOnElementOnly(DatCollection dats, ElementDesc element,
            IReadOnlyList<uint>? stringTableIdsInLocal) {
            foreach (var st in EnumerateLayoutStates(element)) {
                if (st.Media == null) continue;
                foreach (var m in st.Media) {
                    if (m is MediaDescMessage msg && TryResolveMessageText(dats, element, st, msg, out var text, stringTableIdsInLocal) &&
                        !string.IsNullOrWhiteSpace(text))
                        return SanitizeSingleLine(text, maxLen: 96);
                }
            }
            return TryGetCaptionFromStringInfoProperties(dats, element, stringTableIdsInLocal);
        }

        /// <summary>First resolvable <see cref="StringInfo"/> on any enumerated state (template → default → others).</summary>
        public static string? TryGetCaptionFromStringInfoProperties(DatCollection dats, ElementDesc element,
            IReadOnlyList<uint>? stringTableIdsInLocal = null) {
            foreach (var st in EnumerateLayoutStates(element)) {
                if (st.Properties == null) continue;
                foreach (var p in st.Properties.Values) {
                    if (p is not StringInfoBaseProperty sip)
                        continue;
                    if (TryResolveStringInfoText(dats, sip.Value, out var text, stringTableIdsInLocal) &&
                        !string.IsNullOrWhiteSpace(text))
                        return SanitizeSingleLine(text, maxLen: 96);
                }
            }
            return null;
        }

        /// <summary>Resolve <see cref="StringInfo.StringId"/> using its <see cref="StringInfo.TableId"/>, then scan all string tables.</summary>
        public static bool TryResolveStringInfoText(DatCollection dats, StringInfo info,
            out string? text, IReadOnlyList<uint>? stringTableIdsInLocal = null) {
            text = null;
            if (info.StringId == 0)
                return false;

            var tried = new HashSet<uint>();
            var primary = info.TableId.DataId;
            if (primary != 0) {
                tried.Add(primary);
                if (TryLookupRow(dats, primary, info.StringId, out text))
                    return true;
            }

            foreach (var tableId in EffectiveStringTableScanOrder(dats, stringTableIdsInLocal)) {
                if (!tried.Add(tableId))
                    continue;
                if (TryLookupRow(dats, tableId, info.StringId, out text))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Template <see cref="ElementDesc.StateDesc"/> first, then common compact <see cref="UIStateId"/> keys
        /// (<c>0x0</c> Undef through <c>0x10</c> Heavily_encumbered, etc.), then a slice of <c>0x1000…</c> states,
        /// then <see cref="ElementDesc.DefaultState"/>, then every other state. Retail often hangs Media/StringInfo
        /// on <see cref="UIStateId.Normal"/> while <see cref="ElementDesc.DefaultState"/> stays Undef or another token.
        /// </summary>
        public static IEnumerable<StateDesc> EnumerateLayoutStates(ElementDesc element) {
            var list = new List<StateDesc>();
            AddDistinct(list, element.StateDesc);
            if (element.States == null)
                return list;

            for (uint probe = 0; probe <= 0x40; probe++) {
                if (element.States.TryGetValue((UIStateId)probe, out var s))
                    AddDistinct(list, s);
            }

            for (uint probe = 0x10000000; probe <= 0x10000030; probe++) {
                if (element.States.TryGetValue((UIStateId)probe, out var s))
                    AddDistinct(list, s);
            }

            if (element.States.TryGetValue(element.DefaultState, out var def))
                AddDistinct(list, def);

            foreach (var kv in element.States.OrderBy(k => (uint)k.Key))
                AddDistinct(list, kv.Value);

            return list;
        }

        static void AddDistinct(List<StateDesc> list, StateDesc? state) {
            if (state == null) return;
            foreach (var x in list) {
                if (ReferenceEquals(x, state)) return;
            }
            list.Add(state);
        }

        /// <summary>First UI state matching <see cref="ElementDesc.DefaultState"/>, else template <see cref="ElementDesc.StateDesc"/>.</summary>
        public static StateDesc? TryRepresentativeState(ElementDesc element) {
            if (element.States != null && element.States.TryGetValue(element.DefaultState, out var def) && def != null)
                return def;
            return element.StateDesc;
        }

        public static bool TryResolveMessageText(DatCollection dats, ElementDesc element, StateDesc state,
            MediaDescMessage message, out string? text, IReadOnlyList<uint>? stringTableIdsInLocal = null) {
            text = null;
            if (message.Id == 0) return false;

            var tried = new HashSet<uint>();

            // Prefer StringInfo rows whose StringId matches this message id (exact only — avoids a wrong “first StringInfo” table).
            foreach (var hintState in EnumerateLayoutStates(element)) {
                var hintedTable = TryStringTableDidWhenStringIdMatches(hintState, message.Id);
                if (hintedTable is not uint ht) continue;
                if (!tried.Add(ht)) continue;
                if (TryLookupRow(dats, ht, message.Id, out text))
                    return true;
            }

            // Then try every distinct StringTable DID referenced by any StringInfo on the element.
            foreach (var hintState in EnumerateLayoutStates(element)) {
                if (hintState.Properties == null) continue;
                foreach (var p in hintState.Properties.Values) {
                    if (p is not StringInfoBaseProperty sip)
                        continue;
                    var tid = sip.Value.TableId.DataId;
                    if (tid == 0 || !tried.Add(tid))
                        continue;
                    if (TryLookupRow(dats, tid, message.Id, out text))
                        return true;
                }
            }

            foreach (var tableId in EffectiveStringTableScanOrder(dats, stringTableIdsInLocal)) {
                if (!tried.Add(tableId))
                    continue;
                if (TryLookupRow(dats, tableId, message.Id, out text))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// When <paramref name="cachedIds"/> is an empty array, <see cref="DatCollection.GetAllIdsOfType{T}"/> was
        /// already evaluated once and must not be treated as “scan nothing” — fall back to a live query.
        /// </summary>
        static IEnumerable<uint> EffectiveStringTableScanOrder(DatCollection dats, IReadOnlyList<uint>? cachedIds) {
            if (cachedIds == null || cachedIds.Count == 0)
                return dats.GetAllIdsOfType<StringTable>();
            return cachedIds;
        }

        static uint? TryStringTableDidWhenStringIdMatches(StateDesc? state, uint messageId) {
            if (state?.Properties == null) return null;
            foreach (var p in state.Properties.Values) {
                if (p is not StringInfoBaseProperty sip)
                    continue;
                var tableDid = sip.Value.TableId.DataId;
                if (tableDid == 0 || sip.Value.StringId != messageId)
                    continue;
                return tableDid;
            }
            return null;
        }

        static bool TryLookupRow(DatCollection dats, uint stringTableId, uint stringKey, out string? text) {
            text = null;
            if (!dats.TryGet<StringTable>(stringTableId, out var table) || table == null)
                return false;
            if (table.Strings == null)
                return false;
            foreach (var key in EnumerateStringRowKeys(stringKey)) {
                if (!table.Strings.TryGetValue(key, out var row) || row == null)
                    continue;
                text = FormatStringTableRow(row);
                if (!string.IsNullOrWhiteSpace(text))
                    return true;
            }

            text = null;
            return false;
        }

        /// <summary>Try the id as stored, then a low-16-bit variant (some rows use compact keys).</summary>
        static IEnumerable<uint> EnumerateStringRowKeys(uint stringKey) {
            yield return stringKey;
            if (stringKey == 0)
                yield break;
            var low16 = stringKey & 0xFFFFu;
            if (low16 != stringKey)
                yield return low16;
        }

        public static string? FormatStringTableRow(StringTableString row) {
            if (row.Strings == null || row.Strings.Count == 0)
                return null;
            var sb = new StringBuilder();
            foreach (var line in row.Strings) {
                var v = line?.Value;
                if (string.IsNullOrEmpty(v)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(v);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        public static string SanitizeSingleLine(string text, int maxLen) {
            var one = text.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            while (one.Contains("  ", StringComparison.Ordinal))
                one = one.Replace("  ", " ", StringComparison.Ordinal);
            if (one.Length <= maxLen) return one;
            return one[..(maxLen - 1)].TrimEnd() + "…";
        }

        /// <summary>Human-readable media list including resolved <see cref="MediaDescMessage"/> text.</summary>
        public static string BuildMediaSummary(StateDesc state, DatCollection? dats, ElementDesc owner,
            IReadOnlyList<uint>? stringTableIdsInLocal = null) {
            if (state.Media == null || state.Media.Count == 0)
                return "—";

            static string Kind(MediaDesc m) => m.GetType().Name.Replace("MediaDesc", "", StringComparison.Ordinal);

            var parts = new List<string>();
            foreach (var m in state.Media) {
                if (m is MediaDescMessage mm) {
                    if (dats != null && TryResolveMessageText(dats, owner, state, mm, out var txt, stringTableIdsInLocal) &&
                        !string.IsNullOrWhiteSpace(txt)) {
                        parts.Add($"Message: {SanitizeSingleLine(txt, 72)}");
                    }
                    else {
                        parts.Add($"Message(0x{mm.Id:X8})");
                    }
                }
                else if (m is MediaDescImage) {
                    parts.Add(Kind(m));
                }
                else {
                    parts.Add(Kind(m));
                }
            }
            return string.Join(", ", parts);
        }
    }
}

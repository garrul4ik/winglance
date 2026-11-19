using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

public sealed record UndoRedoResult(
    bool Applied,
    string Toast,
    string? FocusItemId,
    IReadOnlyList<string> InvalidatedItemIds,
    bool PlaylistChanged
);

public sealed class CullingService
{
    private readonly List<SlideItem> _allItems = new();
    private readonly List<SlideItem> _filteredItems = new();
    private Dictionary<string, CullingMetadata> _cullingMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly CullingUndoManager _undoManager = new();
    private readonly Action<CullingMetadata, string, Action<string>?> _saveXmp;
    private CullingFilter? _filterBeforePicksToggle;

    public CullingService(Action<CullingMetadata, string, Action<string>?>? saveXmp = null)
    {
        _saveXmp = saveXmp ?? ((meta, path, onBeforeWrite) =>
            Task.Run(() => XmpSidecarManager.Save(meta, path, onBeforeWrite)));
    }

    public IReadOnlyList<SlideItem> AllItems => _allItems;
    public IReadOnlyList<SlideItem> FilteredItems => _filteredItems;
    public IReadOnlyDictionary<string, CullingMetadata> CullingMap => _cullingMap;
    public CullingFilter ActiveFilter { get; set; } = CullingFilter.All;
    public bool AutoAdvanceEnabled { get; set; } = true;
    public CullingUndoManager UndoManager => _undoManager;

    public void SetFolderData(
        IEnumerable<SlideItem> items,
        Dictionary<string, CullingMetadata> cullingMap,
        bool resetUndo = true)
    {
        _allItems.Clear();
        _allItems.AddRange(items);
        _cullingMap = new Dictionary<string, CullingMetadata>(cullingMap, StringComparer.OrdinalIgnoreCase);
        ActiveFilter = CullingFilter.All;
        _filterBeforePicksToggle = null;
        if (resetUndo)
        {
            _undoManager.Clear();
        }
        RebuildFilteredItems();
    }

    public void MergeDirectoryRescan(
        IReadOnlyList<SlideItem> scannedItems,
        IReadOnlyDictionary<string, CullingMetadata> newSidecars)
    {
        _allItems.Clear();
        _allItems.AddRange(scannedItems);

        var activeIds = new HashSet<string>(scannedItems.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var id in _cullingMap.Keys.Where(k => !activeIds.Contains(k)).ToList())
        {
            _cullingMap.Remove(id);
        }
        foreach (var kv in newSidecars)
        {
            _cullingMap[kv.Key] = kv.Value;
        }
        RebuildFilteredItems();
    }

    public CullingMetadata GetMetadata(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return new CullingMetadata();
        return _cullingMap.TryGetValue(itemId, out var meta) ? meta : new CullingMetadata();
    }

    public IReadOnlyList<SlideItem> RebuildFilteredItems()
    {
        _filteredItems.Clear();
        foreach (var item in _allItems)
        {
            if (ActiveFilter.Matches(GetMetadata(item.Id)))
            {
                _filteredItems.Add(item);
            }
        }
        return _filteredItems;
    }

    public IReadOnlyList<CullingFilter> GetAvailableFilters()
    {
        var filters = new List<CullingFilter> { CullingFilter.All };

        bool hasPicks = false;
        bool hasRejects = false;
        bool hasUnrated = false;
        bool hasRatedOrFlagged = false;
        var populatedRatings = new SortedSet<int>();
        var populatedColors = new HashSet<ColorLabel>();

        foreach (var item in _allItems)
        {
            var meta = GetMetadata(item.Id);
            if (meta.Pick == PickStatus.Pick) hasPicks = true;
            if (meta.Pick == PickStatus.Reject) hasRejects = true;
            if (meta.Rating > 0) populatedRatings.Add(meta.Rating);
            if (meta.Color != ColorLabel.None) populatedColors.Add(meta.Color);

            if (meta.Rating == 0 && meta.Pick == PickStatus.Unflagged) hasUnrated = true;
            else hasRatedOrFlagged = true;
        }

        if (hasRejects) filters.Add(CullingFilter.HideRejects);
        if (hasPicks) filters.Add(CullingFilter.PicksOnly);

        foreach (int r in populatedRatings)
        {
            filters.Add(new CullingFilter(CullingFilterType.MinRating, r));
        }

        ColorLabel[] standardColors = { ColorLabel.Red, ColorLabel.Yellow, ColorLabel.Green, ColorLabel.Blue };
        foreach (var c in standardColors)
        {
            if (populatedColors.Contains(c))
            {
                filters.Add(CullingFilter.ForColor(c));
            }
        }

        if (hasUnrated && hasRatedOrFlagged) filters.Add(CullingFilter.UnratedOnly);
        if (hasRejects) filters.Add(CullingFilter.RejectsOnly);

        return filters;
    }

    public void SetFilter(CullingFilter filter)
    {
        ActiveFilter = filter;
        _filterBeforePicksToggle = null;
        RebuildFilteredItems();
    }

    public bool CycleFilter(out string toast)
    {
        _filterBeforePicksToggle = null;
        var filters = GetAvailableFilters();

        if (filters.Count <= 1)
        {
            bool changed = ActiveFilter != CullingFilter.All;
            ActiveFilter = CullingFilter.All;
            if (changed) RebuildFilteredItems();
            toast = "Filter: All Files (No ratings/flags)";
            return changed;
        }

        int currentIdx = -1;
        for (int i = 0; i < filters.Count; i++)
        {
            if (filters[i] == ActiveFilter)
            {
                currentIdx = i;
                break;
            }
        }

        ActiveFilter = currentIdx >= 0
            ? filters[(currentIdx + 1) % filters.Count]
            : filters[1];

        RebuildFilteredItems();
        toast = $"Filter: {ActiveFilter.Title}";
        return true;
    }

    public bool TogglePicksOnlyFilter(out string toast)
    {
        if (ActiveFilter == CullingFilter.PicksOnly)
        {
            ActiveFilter = _filterBeforePicksToggle ?? CullingFilter.All;
            _filterBeforePicksToggle = null;
        }
        else
        {
            bool hasAnyPicks = _allItems.Any(it => GetMetadata(it.Id).Pick == PickStatus.Pick);
            if (!hasAnyPicks)
            {
                toast = "No Picks (P) flagged yet";
                return false;
            }
            _filterBeforePicksToggle = ActiveFilter;
            ActiveFilter = CullingFilter.PicksOnly;
        }

        RebuildFilteredItems();
        toast = $"Filter: {ActiveFilter.Title}";
        return true;
    }

    public bool ApplyRating(
        SlideItem item,
        int stars,
        Action<string>? onBeforeWrite,
        out string toast,
        out bool shouldAutoAdvance,
        out bool itemRemovedFromFilter)
    {
        int clamped = Math.Clamp(stars, 0, 5);
        var prev = GetMetadata(item.Id);
        int nextStars = clamped == 0 ? 0 : (clamped == prev.Rating ? 0 : clamped);

        var updated = prev with { Rating = nextStars };
        _cullingMap[item.Id] = updated;
        _undoManager.Record(new RatingAction(item.Id, prev.Rating, nextStars));

        var saveTarget = item.RawPath ?? item.Path;
        _saveXmp(updated, saveTarget, onBeforeWrite);

        toast = nextStars > 0 ? new string('★', nextStars) : "Rating Cleared";
        itemRemovedFromFilter = !ActiveFilter.Matches(updated);
        shouldAutoAdvance = AutoAdvanceEnabled && nextStars > 0 && !itemRemovedFromFilter;
        return true;
    }

    public bool ApplyPick(
        SlideItem item,
        PickStatus target,
        bool toggle,
        Action<string>? onBeforeWrite,
        out string toast,
        out bool shouldAutoAdvance,
        out bool itemRemovedFromFilter)
    {
        var prev = GetMetadata(item.Id);
        var nextPick = toggle && prev.Pick == target ? PickStatus.Unflagged : target;

        var updated = prev with { Pick = nextPick };
        _cullingMap[item.Id] = updated;
        _undoManager.Record(new PickAction(item.Id, prev.Pick, nextPick));

        var saveTarget = item.RawPath ?? item.Path;
        _saveXmp(updated, saveTarget, onBeforeWrite);

        toast = nextPick switch
        {
            PickStatus.Pick => "✓ Pick",
            PickStatus.Reject => "✕ Reject",
            _ => "Unflagged"
        };

        itemRemovedFromFilter = !ActiveFilter.Matches(updated);
        shouldAutoAdvance = AutoAdvanceEnabled && nextPick != PickStatus.Unflagged && !itemRemovedFromFilter;
        return true;
    }

    /// <summary>
    /// Marks the burst Hero frame as Pick and all remaining frames in the burst cluster as Reject
    /// in a single undoable CompoundAction.
    /// </summary>
    public bool ApplyBurstKnockout(
        IReadOnlyList<SlideItem> burstItems,
        SlideItem winnerItem,
        Action<string>? onBeforeWrite,
        out string toast,
        out bool anyRemovedFromFilter)
    {
        var actions = new List<CullingAction>(burstItems.Count);
        int rejectedCount = 0;
        bool removed = false;

        foreach (var item in burstItems)
        {
            bool isWinner = string.Equals(item.Id, winnerItem.Id, StringComparison.OrdinalIgnoreCase);
            var prev = GetMetadata(item.Id);
            var targetPick = isWinner ? PickStatus.Pick : PickStatus.Reject;
            if (!isWinner) rejectedCount++;

            if (prev.Pick != targetPick)
            {
                var updated = prev with { Pick = targetPick };
                _cullingMap[item.Id] = updated;
                actions.Add(new PickAction(item.Id, prev.Pick, targetPick));

                var saveTarget = item.RawPath ?? item.Path;
                _saveXmp(updated, saveTarget, onBeforeWrite);
                if (!ActiveFilter.Matches(updated)) removed = true;
            }
            else if (!ActiveFilter.Matches(prev))
            {
                removed = true;
            }
        }

        if (actions.Count > 0)
        {
            _undoManager.Record(new CompoundAction(actions, $"Burst Knockout ({winnerItem.DisplayName})"));
        }

        anyRemovedFromFilter = removed;
        toast = actions.Count > 0
            ? $"👑 Picked Winner & Rejected {rejectedCount}"
            : $"Burst Winner: {winnerItem.DisplayName} (already Picked)";
        return actions.Count > 0;
    }

    /// <summary>
    /// King of the Hill (2-UP keys 1 / 2): Picks the winning contender and Rejects the losing contender
    /// in a single undoable CompoundAction.
    /// </summary>
    public bool ApplyKingOfTheHill(
        SlideItem winnerItem,
        SlideItem loserItem,
        Action<string>? onBeforeWrite,
        out string toast)
    {
        var actions = new List<CullingAction>(2);

        var prevWin = GetMetadata(winnerItem.Id);
        if (prevWin.Pick != PickStatus.Pick)
        {
            var updWin = prevWin with { Pick = PickStatus.Pick };
            _cullingMap[winnerItem.Id] = updWin;
            actions.Add(new PickAction(winnerItem.Id, prevWin.Pick, PickStatus.Pick));
            _saveXmp(updWin, winnerItem.RawPath ?? winnerItem.Path, onBeforeWrite);
        }

        if (!string.Equals(winnerItem.Id, loserItem.Id, StringComparison.OrdinalIgnoreCase))
        {
            var prevLose = GetMetadata(loserItem.Id);
            if (prevLose.Pick != PickStatus.Reject)
            {
                var updLose = prevLose with { Pick = PickStatus.Reject };
                _cullingMap[loserItem.Id] = updLose;
                actions.Add(new PickAction(loserItem.Id, prevLose.Pick, PickStatus.Reject));
                _saveXmp(updLose, loserItem.RawPath ?? loserItem.Path, onBeforeWrite);
            }
        }

        if (actions.Count > 0)
        {
            _undoManager.Record(new CompoundAction(actions, $"King of the Hill ({winnerItem.DisplayName})"));
        }

        toast = $"👑 Winner: {winnerItem.DisplayName}";
        return actions.Count > 0;
    }

    /// <summary>
    /// Applies a non-destructive Camera Profile simulation and persists crs:CameraProfile into the XMP sidecar.
    /// </summary>
    public bool ApplyCameraProfile(
        SlideItem item,
        CameraProfile nextProfile,
        CameraBrand brand,
        Action<string>? onBeforeWrite,
        out string toast)
    {
        var prev = GetMetadata(item.Id);
        CameraProfile? prevProf = prev.CameraProfile;
        CameraProfile? storedNew = nextProfile == CameraProfile.AsShot ? null : nextProfile;

        var updated = prev with { CameraProfile = storedNew };
        _cullingMap[item.Id] = updated;
        _undoManager.Record(new CameraProfileAction(item.Id, prevProf, storedNew));

        _saveXmp(updated, item.RawPath ?? item.Path, onBeforeWrite);
        toast = $"🎨 Profile: {nextProfile.GetDisplayName(brand)}";
        return true;
    }

    public bool ApplyColor(
        SlideItem item,
        ColorLabel label,
        Action<string>? onBeforeWrite,
        out string toast,
        out bool shouldAutoAdvance,
        out bool itemRemovedFromFilter)
    {
        var prev = GetMetadata(item.Id);
        var nextColor = prev.Color == label ? ColorLabel.None : label;

        var updated = prev with { Color = nextColor };
        _cullingMap[item.Id] = updated;
        _undoManager.Record(new ColorAction(item.Id, prev.Color, nextColor));

        var saveTarget = item.RawPath ?? item.Path;
        _saveXmp(updated, saveTarget, onBeforeWrite);

        toast = nextColor == ColorLabel.None ? "Label Cleared" : $"Label: {nextColor.GetTitle()}";
        itemRemovedFromFilter = !ActiveFilter.Matches(updated);
        shouldAutoAdvance = AutoAdvanceEnabled && nextColor != ColorLabel.None && !itemRemovedFromFilter;
        return true;
    }

    public int ApplyRotation(SlideItem item, RotationDirection dir, Action<string>? onBeforeWrite)
    {
        var prevMeta = GetMetadata(item.Id);
        int prevOrient = prevMeta.Orientation
            ?? FastImageDecoder.ReadExifMetadata(item.Path)?.Orientation
            ?? 1;

        int newOrient = LosslessRotationEngine.Rotate(item, dir, onBeforeWrite);
        _cullingMap[item.Id] = prevMeta with { Orientation = newOrient };
        _undoManager.Record(new RotateAction(item, dir, prevOrient, newOrient));
        return newOrient;
    }

    public async Task<bool> TrashItemAsync(SlideItem item, Action<string>? onBeforeWrite = null)
    {
        int idx = _allItems.FindIndex(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;

        CullingMetadata? prevMeta = _cullingMap.TryGetValue(item.Id, out var m) ? m : null;
        var records = await TrashManager.MoveItemAndSidecarsToTrashAsync(item, onBeforeWrite);
        if (records.Count == 0 && item.AllPaths.Any(File.Exists))
        {
            return false;
        }

        _allItems.RemoveAt(idx);
        _cullingMap.Remove(item.Id);
        _undoManager.Record(new DeleteAction(item, idx, prevMeta, records));
        RebuildFilteredItems();
        return true;
    }

    public async Task<int> PurgeRejectsAsync(Action<string>? onBeforeWrite = null)
    {
        var rejects = _allItems.Where(it => GetMetadata(it.Id).Pick == PickStatus.Reject).ToList();
        if (rejects.Count == 0) return 0;

        var deleteActions = new List<CullingAction>(rejects.Count);
        foreach (var item in rejects)
        {
            int idx = _allItems.IndexOf(item);
            if (idx < 0) continue;

            CullingMetadata? prevMeta = _cullingMap.TryGetValue(item.Id, out var m) ? m : null;
            var records = await TrashManager.MoveItemAndSidecarsToTrashAsync(item, onBeforeWrite);
            _allItems.RemoveAt(idx);
            _cullingMap.Remove(item.Id);
            deleteActions.Add(new DeleteAction(item, idx, prevMeta, records));
        }

        if (deleteActions.Count > 0)
        {
            _undoManager.Record(new CompoundAction(deleteActions, $"Purge {deleteActions.Count} Rejects"));
            RebuildFilteredItems();
        }

        return deleteActions.Count;
    }

    public async Task<UndoRedoResult> UndoAsync(Action<string>? onBeforeWrite = null)
    {
        var action = _undoManager.PopUndo();
        if (action == null)
        {
            return new UndoRedoResult(false, "Nothing to Undo", null, Array.Empty<string>(), false);
        }

        var invalidated = new List<string>();
        bool playlistChanged = false;
        string? focusId = null;

        string toast = await ApplySingleAsync(action, isUndo: true, onBeforeWrite, invalidated,
            v => playlistChanged |= v, id => focusId ??= id);

        if (playlistChanged || !ActiveFilterMatchesAll())
        {
            RebuildFilteredItems();
            playlistChanged = true;
        }

        return new UndoRedoResult(true, toast, focusId, invalidated, playlistChanged);
    }

    public async Task<UndoRedoResult> RedoAsync(Action<string>? onBeforeWrite = null)
    {
        var action = _undoManager.PopRedo();
        if (action == null)
        {
            return new UndoRedoResult(false, "Nothing to Redo", null, Array.Empty<string>(), false);
        }

        var invalidated = new List<string>();
        bool playlistChanged = false;
        string? focusId = null;

        string toast = await ApplySingleAsync(action, isUndo: false, onBeforeWrite, invalidated,
            v => playlistChanged |= v, id => focusId ??= id);

        if (playlistChanged || !ActiveFilterMatchesAll())
        {
            RebuildFilteredItems();
            playlistChanged = true;
        }

        return new UndoRedoResult(true, toast, focusId, invalidated, playlistChanged);
    }

    private bool ActiveFilterMatchesAll() => ActiveFilter.Type == CullingFilterType.All;

    private async Task<string> ApplySingleAsync(
        CullingAction action,
        bool isUndo,
        Action<string>? onBeforeWrite,
        List<string> invalidated,
        Action<bool> markPlaylistChanged,
        Action<string> setFocusId)
    {
        switch (action)
        {
            case RatingAction r:
                {
                    int target = isUndo ? r.Previous : r.New;
                    var updated = GetMetadata(r.ItemId) with { Rating = target };
                    _cullingMap[r.ItemId] = updated;
                    SaveSidecarByItemId(r.ItemId, updated, onBeforeWrite);
                    setFocusId(r.ItemId);
                    string desc = target > 0 ? new string('★', target) : "Clear Rating";
                    return isUndo ? $"↩ Undo: {desc}" : $"↪ Redo: {desc}";
                }

            case PickAction p:
                {
                    var target = isUndo ? p.Previous : p.New;
                    var updated = GetMetadata(p.ItemId) with { Pick = target };
                    _cullingMap[p.ItemId] = updated;
                    SaveSidecarByItemId(p.ItemId, updated, onBeforeWrite);
                    setFocusId(p.ItemId);
                    string desc = target switch
                    {
                        PickStatus.Pick => "✓ Pick",
                        PickStatus.Reject => "✕ Reject",
                        _ => "Unflagged"
                    };
                    return isUndo ? $"↩ Undo: {desc}" : $"↪ Redo: {desc}";
                }

            case ColorAction c:
                {
                    var target = isUndo ? c.Previous : c.New;
                    var updated = GetMetadata(c.ItemId) with { Color = target };
                    _cullingMap[c.ItemId] = updated;
                    SaveSidecarByItemId(c.ItemId, updated, onBeforeWrite);
                    setFocusId(c.ItemId);
                    string desc = target == ColorLabel.None ? "Clear Label" : target.GetTitle();
                    return isUndo ? $"↩ Undo: {desc}" : $"↪ Redo: {desc}";
                }

            case RotateAction rot:
                {
                    var dir = isUndo
                        ? (rot.Direction == RotationDirection.Clockwise
                            ? RotationDirection.CounterClockwise
                            : RotationDirection.Clockwise)
                        : rot.Direction;

                    int newOrient = LosslessRotationEngine.Rotate(rot.Item, dir, onBeforeWrite);
                    _cullingMap[rot.Item.Id] = GetMetadata(rot.Item.Id) with { Orientation = newOrient };
                    invalidated.Add(rot.Item.Id);
                    setFocusId(rot.Item.Id);
                    return isUndo ? "↩ Undo: Rotation" : "↪ Redo: Rotation";
                }

            case CameraProfileAction cp:
                {
                    var target = isUndo ? cp.Previous : cp.New;
                    var updated = GetMetadata(cp.ItemId) with { CameraProfile = target };
                    _cullingMap[cp.ItemId] = updated;
                    SaveSidecarByItemId(cp.ItemId, updated, onBeforeWrite);
                    setFocusId(cp.ItemId);
                    return isUndo ? "↩ Undo: Camera Profile" : "↪ Redo: Camera Profile";
                }

            case DeleteAction del:
                {
                    if (isUndo)
                    {
                        int restoredCount = 0;
                        foreach (var rec in del.TrashedRecords)
                        {
                            onBeforeWrite?.Invoke(rec.OriginalPath);
                            if (await TrashManager.RestoreAsync(rec))
                            {
                                restoredCount++;
                            }
                        }

                        if (restoredCount > 0 || del.TrashedRecords.Count == 0)
                        {
                            int insertIdx = Math.Clamp(del.OriginalAllItemsIndex, 0, _allItems.Count);
                            if (!_allItems.Any(x => string.Equals(x.Id, del.Item.Id, StringComparison.OrdinalIgnoreCase)))
                            {
                                _allItems.Insert(insertIdx, del.Item);
                            }
                            if (del.PreviousMeta.HasValue)
                            {
                                _cullingMap[del.Item.Id] = del.PreviousMeta.Value;
                            }
                            markPlaylistChanged(true);
                            setFocusId(del.Item.Id);
                            return $"↩ Restored {del.Item.DisplayName}";
                        }

                        return $"⚠ Could not restore {del.Item.DisplayName} from Recycle Bin";
                    }
                    else
                    {
                        var records = await TrashManager.MoveItemAndSidecarsToTrashAsync(del.Item, onBeforeWrite);
                        del.TrashedRecords = records;
                        _allItems.RemoveAll(x => string.Equals(x.Id, del.Item.Id, StringComparison.OrdinalIgnoreCase));
                        _cullingMap.Remove(del.Item.Id);
                        markPlaylistChanged(true);
                        return $"↪ Redo Trash: {del.Item.DisplayName}";
                    }
                }

            case CompoundAction comp:
                {
                    if (isUndo)
                    {
                        for (int i = comp.Actions.Count - 1; i >= 0; i--)
                        {
                            await ApplySingleAsync(comp.Actions[i], true, onBeforeWrite, invalidated, markPlaylistChanged, setFocusId);
                        }
                        return $"↩ Undo: {comp.Description}";
                    }
                    else
                    {
                        for (int i = 0; i < comp.Actions.Count; i++)
                        {
                            await ApplySingleAsync(comp.Actions[i], false, onBeforeWrite, invalidated, markPlaylistChanged, setFocusId);
                        }
                        return $"↪ Redo: {comp.Description}";
                    }
                }
        }

        return "";
    }

    private void SaveSidecarByItemId(string itemId, CullingMetadata meta, Action<string>? onBeforeWrite)
    {
        var item = _allItems.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.OrdinalIgnoreCase));
        var saveTarget = item?.RawPath ?? item?.Path ?? itemId;
        _saveXmp(meta, saveTarget, onBeforeWrite);
    }
}

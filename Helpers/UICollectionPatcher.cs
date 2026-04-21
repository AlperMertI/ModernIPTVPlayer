using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Provides pinnacle-level collection synchronization for WinUI 3.
    /// Uses background diffing and atomic UI patching to ensure 120fps smooth transitions.
    /// Eliminates the need for .Clear() and .Add() cycles that cause UI flickering.
    /// </summary>
    public static class UICollectionPatcher
    {
        public enum PatchType { Add, Remove, Move, Replace }

        public record PatchOp<T>(PatchType Type, T Item, int Index, int OldIndex = -1);

        /// <summary>
        /// Patches an ObservableCollection with a new set of items using a background diffing algorithm.
        /// Only triggers specific Move, Remove, and Add operations on the UI thread.
        /// </summary>
        public static async Task PatchAsync<T>(
            ObservableCollection<T> collection,
            IEnumerable<T>? newItems,
            DispatcherQueue dispatcher,
            Func<T, object> identityFunc) where T : class
        {
            if (collection == null) return;
            if (newItems == null)
            {
                dispatcher.TryEnqueue(() => collection.Clear());
                return;
            }

            // 1. Snapshot current state on UI thread
            var currentList = collection.ToImmutableArray();
            var targetList = newItems.ToImmutableArray();

            // 2. Perform diffing on background thread
            var patches = await Task.Run(() => CalculatePatches(currentList, targetList, identityFunc));

            if (patches.Count == 0) return;

            // 3. Apply patches on UI thread
            dispatcher.TryEnqueue(() =>
            {
                ApplyPatches(collection, patches);
            });
        }

        /// <summary>
        /// A pinnacle-quality diffing algorithm that minimizes collection operations.
        /// Handles Add, Remove, Move, and Replace (for updated metadata).
        /// </summary>
        private static List<PatchOp<T>> CalculatePatches<T>(
            ImmutableArray<T> current,
            ImmutableArray<T> target,
            Func<T, object> identityFunc) where T : class
        {
            var patches = new List<PatchOp<T>>();
            
            // 1. Identify Removals (Reverse to maintain indices)
            var workingList = current.ToList();
            var targetKeys = new HashSet<object>(target.Select(identityFunc));

            for (int i = current.Length - 1; i >= 0; i--)
            {
                var id = identityFunc(current[i]);
                if (id == null || !targetKeys.Contains(id))
                {
                    patches.Add(new PatchOp<T>(PatchType.Remove, current[i], i));
                    workingList.RemoveAt(i);
                }
            }

            // 2. Identify Additions, Moves, and Replacements
            for (int i = 0; i < target.Length; i++)
            {
                var targetItem = target[i];
                var targetId = identityFunc(targetItem);
                
                if (i < workingList.Count)
                {
                    var currentItem = workingList[i];
                    var currentId = identityFunc(currentItem);

                    if (Equals(currentId, targetId))
                    {
                        // Same ID. Check if reference changed (Metadata Update)
                        if (!ReferenceEquals(currentItem, targetItem))
                        {
                            patches.Add(new PatchOp<T>(PatchType.Replace, targetItem, i));
                            workingList[i] = targetItem;
                        }
                        continue;
                    }

                    // Not a match at this position. Search ahead.
                    int existingIndex = -1;
                    for (int j = i + 1; j < workingList.Count; j++)
                    {
                        if (Equals(identityFunc(workingList[j]), targetId))
                        {
                            existingIndex = j;
                            break;
                        }
                    }

                    if (existingIndex != -1)
                    {
                        // Match found later -> Move it here
                        var itemToMove = workingList[existingIndex];
                        workingList.RemoveAt(existingIndex);
                        workingList.Insert(i, itemToMove);
                        
                        // IMPORTANT: We also check if the moved item needs a replacement
                        if (!ReferenceEquals(itemToMove, targetItem))
                        {
                            workingList[i] = targetItem;
                            // We can't easily express Move+Replace in one OC op, 
                            // so we do Move then Replace.
                            patches.Add(new PatchOp<T>(PatchType.Move, itemToMove, i, existingIndex));
                            patches.Add(new PatchOp<T>(PatchType.Replace, targetItem, i));
                        }
                        else
                        {
                            patches.Add(new PatchOp<T>(PatchType.Move, itemToMove, i, existingIndex));
                        }
                    }
                    else
                    {
                        // No match later -> Insert as new
                        workingList.Insert(i, targetItem);
                        patches.Add(new PatchOp<T>(PatchType.Add, targetItem, i));
                    }
                }
                else
                {
                    // Past end of working list -> Simple Add
                    workingList.Add(targetItem);
                    patches.Add(new PatchOp<T>(PatchType.Add, targetItem, i));
                }
            }

            return patches;
        }

        private static void ApplyPatches<T>(ObservableCollection<T> collection, List<PatchOp<T>> patches) where T : class
        {
            foreach (var patch in patches)
            {
                try
                {
                    switch (patch.Type)
                    {
                        case PatchType.Remove:
                            if (patch.Index >= 0 && patch.Index < collection.Count)
                                collection.RemoveAt(patch.Index);
                            break;
                        case PatchType.Add:
                            if (patch.Index >= 0 && patch.Index <= collection.Count)
                                collection.Insert(patch.Index, patch.Item);
                            break;
                        case PatchType.Move:
                            if (patch.OldIndex != -1 && patch.OldIndex < collection.Count && patch.Index < collection.Count)
                                collection.Move(patch.OldIndex, patch.Index);
                            break;
                        case PatchType.Replace:
                             if (patch.Index >= 0 && patch.Index < collection.Count)
                                collection[patch.Index] = patch.Item;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UICollectionPatcher] Patch failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Atomic replacement of entire collection. Efficiently updates current items 
        /// instead of clearing/re-adding to preserve virtualization and minimize layout cycles.
        /// </summary>
        public static void ReplaceAll<T>(this ObservableCollection<T> collection, IEnumerable<T>? newItems)
        {
            if (collection == null) return;
            if (newItems == null)
            {
                if (collection.Count > 0) collection.Clear();
                return;
            }

            var newList = newItems.ToList();
            int count = newList.Count;

            // 1. Update existing items in place
            int commonCount = Math.Min(collection.Count, count);
            for (int i = 0; i < commonCount; i++)
            {
                if (!ReferenceEquals(collection[i], newList[i]))
                    collection[i] = newList[i];
            }

            // 2. Remove excess items
            while (collection.Count > count)
            {
                collection.RemoveAt(collection.Count - 1);
            }

            // 3. Add new items
            for (int i = collection.Count; i < count; i++)
            {
                collection.Add(newList[i]);
            }
        }
    }
}

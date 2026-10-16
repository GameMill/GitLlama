using System.Collections.ObjectModel;

namespace GitLlama.Desktop.ViewModels;

internal static class CollectionUpdates
{
    internal static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> values, Func<T, string> key)
    {
        var desired = values.ToArray();
        if (target.SequenceEqual(desired))
            return;

        // Small edits retain individual notifications and selection. Large snapshots must not make
        // the staged/unstaged projections and bound controls rebuild thousands of times.
        if (target is BatchObservableCollection<T> batch)
        {
            var changed = Math.Abs(target.Count - desired.Length);
            for (var i = 0; i < Math.Min(target.Count, desired.Length) && changed <= 128; i++)
                if (!EqualityComparer<T>.Default.Equals(target[i], desired[i]))
                    changed++;
            if (changed > 128)
            {
                var existing = new Dictionary<string, T>(StringComparer.Ordinal);
                foreach (var item in target)
                    existing.TryAdd(key(item), item);
                for (var i = 0; i < desired.Length; i++)
                    if (existing.TryGetValue(key(desired[i]), out var retained)
                        && EqualityComparer<T>.Default.Equals(retained, desired[i]))
                        desired[i] = retained;
                batch.ReplaceAll(desired);
                return;
            }
        }
        for (var index = 0; index < desired.Length; index++)
        {
            var found = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
            {
                if (key(target[candidate]) == key(desired[index]))
                {
                    found = candidate;
                    break;
                }
            }

            if (found < 0)
            {
                target.Insert(index, desired[index]);
            }
            else
            {
                if (found != index)
                {
                    target.Move(found, index);
                }

                if (!EqualityComparer<T>.Default.Equals(target[index], desired[index]))
                {
                    target[index] = desired[index];
                }
            }
        }

        while (target.Count > desired.Length)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Centralized UI thread execution helper. Replaces the scattered pattern of
    /// manual DispatcherQueue.HasThreadAccess checks across the codebase.
    /// </summary>
    public static class UiThread
    {
        /// <summary>
        /// Executes an action on the UI thread. If already on the UI thread, executes synchronously.
        /// </summary>
        public static void Execute(DispatcherQueue dispatcher, Action action)
        {
            if (dispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                dispatcher.TryEnqueue(() => action());
            }
        }

        /// <summary>
        /// Executes an action on the UI thread with a specified priority.
        /// </summary>
        public static void Execute(DispatcherQueue dispatcher, Action action, DispatcherQueuePriority priority)
        {
            if (dispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                dispatcher.TryEnqueue(priority, () => action());
            }
        }

        /// <summary>
        /// Executes a function on the UI thread and returns the result via Task.
        /// If already on the UI thread, executes synchronously and returns a completed task.
        /// </summary>
        public static Task<T> ExecuteAsync<T>(DispatcherQueue dispatcher, Func<T> func)
        {
            if (dispatcher.HasThreadAccess)
            {
                return Task.FromResult(func());
            }

            var tcs = new TaskCompletionSource<T>();
            dispatcher.TryEnqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        /// <summary>
        /// Executes an async function on the UI thread.
        /// If already on the UI thread, executes directly.
        /// </summary>
        public static async Task ExecuteAsync(DispatcherQueue dispatcher, Func<Task> func)
        {
            if (dispatcher.HasThreadAccess)
            {
                await func();
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            dispatcher.TryEnqueue(async () =>
            {
                try { await func(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            await tcs.Task;
        }

        /// <summary>
        /// Ensures the current code is running on the UI thread.
        /// Returns true if execution continued (was already on UI thread or successfully enqueued).
        /// Returns false if enqueue failed (dispatcher unavailable).
        /// </summary>
        public static bool Ensure(DispatcherQueue dispatcher, Action action)
        {
            if (dispatcher.HasThreadAccess)
            {
                action();
                return true;
            }
            return dispatcher.TryEnqueue(() => action());
        }
    }
}

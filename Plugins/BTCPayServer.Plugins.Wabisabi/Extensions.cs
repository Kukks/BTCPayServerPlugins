using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WalletWasabi.Blockchain.TransactionOutputs;

namespace BTCPayServer.Plugins.Wabisabi;

public static class Extensions
{
    
    public static string ToSentenceCase(this string str)
    {
        return Regex.Replace(str, "[a-z][A-Z]", m => m.Value[0] + " " + char.ToLower(m.Value[1]));
    }
    
    /// <summary>
    /// Returns an existing task from the concurrent dictionary, or adds a new task
    /// using the specified asynchronous factory method. Concurrent invocations for
    /// the same key are prevented, unless the task is removed before the completion
    /// of the delegate. Failed tasks are evicted from the concurrent dictionary.
    /// </summary>
    public static Task<TValue> GetOrAddAsync<TKey, TValue>(
        this ConcurrentDictionary<TKey, Task<TValue>> source, TKey key,
        Func<TKey, Task<TValue>> valueFactory)
    {
        if (!source.TryGetValue(key, out var currentTask))
        {
            Task<TValue> newTask = null;
            var newTaskTask = new Task<Task<TValue>>(async () =>
            {
                try { return await valueFactory(key).ConfigureAwait(false); }
                catch
                {
                    source.TryRemove(KeyValuePair.Create(key, newTask));
                    throw;
                }
            });
            newTask = newTaskTask.Unwrap();
            currentTask = source.GetOrAdd(key, newTask);
            if (currentTask == newTask) newTaskTask.Start(TaskScheduler.Default);
        }
        return currentTask;
    }
}

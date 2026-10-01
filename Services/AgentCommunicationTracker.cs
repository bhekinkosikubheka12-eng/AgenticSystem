using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public static class AgentCommunicationTracker
{
    private static readonly ConcurrentDictionary<string, List<AgentCommunication>> _taskCommunications = new();
    private static readonly AsyncLocal<string> _asyncLocalTaskId = new();

    public static string ActiveTaskId
    {
        get => _asyncLocalTaskId.Value ?? "";
        set => _asyncLocalTaskId.Value = value;
    }

    public static void RecordCommunication(string sender, string receiver, string taskGiven, string response)
    {
        var taskId = ActiveTaskId;
        if (string.IsNullOrEmpty(taskId)) return;

        var list = _taskCommunications.GetOrAdd(taskId, _ => new List<AgentCommunication>());
        lock (list)
        {
            list.Add(new AgentCommunication
            {
                Sender = sender,
                Receiver = receiver,
                TaskGiven = taskGiven,
                Response = response,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    public static List<AgentCommunication> GetCommunications(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return new List<AgentCommunication>();
        _taskCommunications.TryGetValue(taskId, out var list);
        return list ?? new List<AgentCommunication>();
    }

    public static void Clear(string taskId)
    {
        if (!string.IsNullOrEmpty(taskId))
        {
            _taskCommunications.TryRemove(taskId, out _);
        }
    }
}

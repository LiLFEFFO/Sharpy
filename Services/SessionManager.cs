using System.Collections.Concurrent;
using System.Reflection;

namespace Sharpy.Services;

public class SessionManager
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    public string GetOrCreateSessionId(string? sessionId)
    {
        if (sessionId != null && _sessions.ContainsKey(sessionId))
            return sessionId;

        var newId = Guid.NewGuid().ToString("N");
        _sessions[newId] = new SessionState();
        return newId;
    }

    public bool TryGetEntry(string sessionId, string assemblyToken, string className, out Assembly? assembly, out object? instance)
    {
        assembly = null;
        instance = null;
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            var key = GetKey(assemblyToken, className);
            if (session.Entries.TryGetValue(key, out var entry))
            {
                assembly = entry.Assembly;
                instance = entry.Instance;
                return true;
            }
        }
        return false;
    }

    public void SetEntry(string sessionId, string assemblyToken, string className, Assembly assembly, object? instance)
    {
        var session = _sessions.GetOrAdd(sessionId, _ => new SessionState());
        var key = GetKey(assemblyToken, className);
        session.Entries[key] = new SessionEntry { Assembly = assembly, Instance = instance };
    }

    public void ResetInstance(string sessionId, string assemblyToken, string className)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            var key = GetKey(assemblyToken, className);
            session.Entries.TryRemove(key, out _);
        }
    }

    public void ResetSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    private static string GetKey(string assemblyToken, string className) => $"{assemblyToken}:{className}";

    private class SessionState
    {
        public ConcurrentDictionary<string, SessionEntry> Entries { get; } = new();
    }

    private class SessionEntry
    {
        public Assembly Assembly { get; set; } = null!;
        public object? Instance { get; set; }
    }
}

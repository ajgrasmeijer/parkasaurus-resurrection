using UnityEngine;

namespace ParkasaurusResurrection
{
    // Temporary: finds out which code triggers the "Failed to create agent because it is not close
    // enough to the NavMesh" warnings (~9 per second with the mod on). Logs the first few with the
    // script call stack, then switches the extra stack traces off again.
    internal static class NavMeshWarningProbe
    {
        private const int MaxCaptures = 5;
        private const string Message = "Failed to create agent";

        private static int s_captured;

        internal static void Start()
        {
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.ScriptOnly);
            Application.logMessageReceived += OnLog;
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Warning || s_captured >= MaxCaptures || message == null || !message.StartsWith(Message))
                return;

            s_captured++;
            string stack = string.IsNullOrEmpty(stackTrace) ? "(no script stack: raised by the engine itself)" : stackTrace.Trim();
            Plugin.Log.LogWarning($"[NAVMESH {s_captured}/{MaxCaptures}] {message}\n{stack}");

            if (s_captured >= MaxCaptures)
            {
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
                Application.logMessageReceived -= OnLog;
            }
        }
    }
}

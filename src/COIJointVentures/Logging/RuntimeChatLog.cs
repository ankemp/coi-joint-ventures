using System;
using System.IO;
using BepInEx.Logging;
using COIJointVentures.Chat;

namespace COIJointVentures.Logging;

internal sealed class RuntimeChatLog
{
    private readonly ManualLogSource _log;
    private readonly string _outputPath;
    private readonly object _gate = new object();

    public RuntimeChatLog(ManualLogSource log, string outputPath)
    {
        _log = log;
        _outputPath = outputPath;

        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Create or touch the file now so we can verify the path up front.
        File.AppendAllText(_outputPath, string.Empty);
        _log.LogInfo($"Initialized runtime chat log at '{_outputPath}'.");
    }

    public void RecordChatEntry(string formattedEntry)
    {
        lock (_gate)
        {
            File.AppendAllText(_outputPath, formattedEntry + Environment.NewLine);
        }

        _log.LogInfo($"Chat logged: {formattedEntry}");
    }

    public void RecordChatEntry(ChatEntry entry)
    {
        RecordChatEntry($"{DateTime.UtcNow:O} [{entry.Kind}] {entry.FormatForDisplay()}");
    }
}

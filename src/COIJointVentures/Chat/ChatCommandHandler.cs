using System;
using BepInEx.Logging;
using COIJointVentures.Runtime;
using COIJointVentures.Session;

namespace COIJointVentures.Chat;

internal sealed class ChatCommandHandler
{
    private readonly MultiplayerSession _session;
    private readonly ManualLogSource _log;

    public ChatCommandHandler(MultiplayerSession session, ManualLogSource log)
    {
        _session = session;
        _log = log;
    }

    public bool TryHandle(string text)
    {
        if (!ChatCommandRegistry.TryParse(text, out var command))
        {
            return false;
        }

        switch (command.Name)
        {
            case "/help":
                HandleHelp(text);
                return true;

            case "/hiccup":
                HandleHiccup();
                return true;

            case "/ping":
                HandlePing(text);
                return true;

            default:
                return false;
        }
    }

    private void HandleHelp(string text)
    {
        var helpTarget = ExtractHelpTarget(text);
        var helpText = ChatCommandRegistry.BuildHelpText(helpTarget);
        PluginRuntime.Chat.AddSystem(helpText);
        _log.LogInfo("Executed /help command.");
    }

    private static string? ExtractHelpTarget(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0)
        {
            return null;
        }

        var target = trimmed.Substring(firstSpace + 1).Trim();
        return string.IsNullOrWhiteSpace(target) ? null : target;
    }

    private void HandleHiccup()
    {
        _log.LogInfo("Executed /hiccup command.");
        _session.ActivateDebugPacketDrop();
    }

    private void HandlePing(string text)
    {
        _session.HandlePingCommand(text);
    }
}

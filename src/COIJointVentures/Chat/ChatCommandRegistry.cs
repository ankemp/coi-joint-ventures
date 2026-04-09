using System;
using System.Collections.Generic;
using System.Text;

namespace COIJointVentures.Chat;

internal static class ChatCommandRegistry
{
    public static readonly IReadOnlyDictionary<string, ChatCommandInfo> Commands =
        new Dictionary<string, ChatCommandInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["/help"] = new("/help", "Show available chat commands", "/help"),
            ["/ping"] = new("/ping", "Ping the host and receive a response", "/ping"),
            ["/hiccup"] = new("/hiccup", "Drop the next host command to simulate a desync", "/hiccup")
        };

    public static bool TryParse(string text, out ChatCommandInfo command)
    {
        command = default!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        foreach (var kvp in Commands)
        {
            var key = kvp.Key;
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Length == key.Length || char.IsWhiteSpace(trimmed[key.Length]))
            {
                command = kvp.Value;
                return true;
            }
        }

        return false;
    }

    public static bool TryGet(string commandName, out ChatCommandInfo command)
    {
        command = default!;
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return false;
        }

        var normalized = commandName.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        return Commands.TryGetValue(normalized, out command);
    }

    public static string BuildCommandList()
    {
        var builder = new StringBuilder();
        var first = true;

        foreach (var info in Commands.Values)
        {
            if (info.IsHidden)
            {
                continue;
            }

            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(info.Name);
            first = false;
        }

        return builder.Length > 0 ? builder.ToString() : "No commands registered.";
    }

    public static string BuildHelpText(string? commandName = null)
    {
        if (!string.IsNullOrWhiteSpace(commandName))
        {
            if (TryGet(commandName!, out var command))
            {
                return BuildCommandHelpText(command);
            }

            return $"Unknown command: {commandName}. Known commands: {BuildCommandList()}";
        }

        var builder = new StringBuilder();
        var first = true;

        foreach (var info in Commands.Values)
        {
            if (info.IsHidden)
            {
                continue;
            }

            if (!first)
            {
                builder.Append("\n");
            }

            builder.Append(BuildCommandHelpText(info));
            first = false;
        }

        return builder.Length > 0 ? builder.ToString() : "No commands registered.";
    }

    private static string BuildCommandHelpText(ChatCommandInfo info)
    {
        var builder = new StringBuilder();
        builder.Append(info.Name);
        builder.Append(" - ");
        builder.Append(info.Description);

        if (!string.IsNullOrWhiteSpace(info.Usage))
        {
            builder.Append(" (Usage: ");
            builder.Append(info.Usage);
            builder.Append(")");
        }

        return builder.ToString();
    }
}

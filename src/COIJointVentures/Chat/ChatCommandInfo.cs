namespace COIJointVentures.Chat;

internal sealed class ChatCommandInfo
{
    public ChatCommandInfo(string name, string description, string? usage = null, bool isHidden = false)
    {
        Name = name;
        Description = description;
        Usage = usage;
        IsHidden = isHidden;
    }

    public string Name { get; }

    public string Description { get; }

    public string? Usage { get; }

    public bool IsHidden { get; }
}

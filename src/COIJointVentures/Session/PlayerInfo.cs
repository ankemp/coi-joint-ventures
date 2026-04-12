namespace COIJointVentures.Session;

internal sealed class PlayerInfo
{
    public string Name { get; set; } = "";
    public int ColorIndex { get; set; }
    public bool IsPending { get; set; }
    public int LatencyMs { get; set; } = -1;
}

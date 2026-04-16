namespace ServerWatch.Models;

public class NetworkInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Scope { get; set; } = "";
    public bool Internal { get; set; }
    public string Subnet { get; set; } = "";
    public string Gateway { get; set; } = "";
    public List<NetworkContainer> Containers { get; set; } = new();
    public string ServerId { get; set; } = "local";
    public string ServerName { get; set; } = "Local";
}

public class NetworkContainer
{
    public string ContainerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string IPv4Address { get; set; } = "";
}

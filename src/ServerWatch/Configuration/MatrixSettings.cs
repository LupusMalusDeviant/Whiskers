namespace ServerWatch.Configuration;

public class MatrixSettings
{
    public const string SectionName = "Matrix";

    public string HomeserverUrl { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RoomId { get; set; } = "";
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}

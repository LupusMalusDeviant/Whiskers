namespace ServerWatch.Models.Cve;

/// <summary>Where a CVE finding originated: the host OS package set, or a container image.</summary>
public enum CveSource
{
    Os,
    Container
}

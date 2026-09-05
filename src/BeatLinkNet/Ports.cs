namespace BeatLink;

/// <summary>
/// The UDP and TCP port numbers used by the Pro DJ Link protocol. Kept in a dedicated class so that
/// packet-type registries and services can share them without creating initialization cycles.
/// </summary>
public static class Ports
{
    /// <summary>The port on which device announcement (keep-alive) and device-number claim packets are broadcast.</summary>
    public const int AnnouncementPort = 50000;

    /// <summary>The port on which beat, precise position, and mixer command packets are broadcast.</summary>
    public const int BeatPort = 50001;

    /// <summary>The port on which device status updates are received, and from which we send packets.</summary>
    public const int UpdatePort = 50002;

    /// <summary>The TCP port on which players answer queries about the port of their database server.</summary>
    public const int DbServerQueryPort = 12523;
}

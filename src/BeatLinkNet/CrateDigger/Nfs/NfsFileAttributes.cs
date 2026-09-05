namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// A timestamp as reported by an NFS version 2 server (RFC 1094 <c>timeval</c>).
/// </summary>
/// <param name="Seconds">the seconds portion, counted from the Unix epoch</param>
/// <param name="Microseconds">the microseconds portion</param>
public readonly record struct NfsTimestamp(uint Seconds, uint Microseconds)
{
    /// <summary>
    /// Read a timestamp from an XDR stream.
    /// </summary>
    /// <param name="reader">the stream being decoded</param>
    internal static NfsTimestamp ReadFrom(XdrReader reader) => new(reader.ReadUInt32(), reader.ReadUInt32());
}

/// <summary>
/// The attributes of a file or directory as reported by an NFS version 2 server (RFC 1094
/// <c>fattr</c>). Corresponds to the generated <c>FAttr</c> class in the Java crate-digger.
/// </summary>
public sealed class NfsFileAttributes
{
    /// <summary>The kind of file this is.</summary>
    public NfsFileType Type { get; }

    /// <summary>The access mode and file-type bits, encoded as in Unix <c>stat</c>.</summary>
    public uint Mode { get; }

    /// <summary>The number of hard links to the file.</summary>
    public uint LinkCount { get; }

    /// <summary>The user identifier of the owner of the file.</summary>
    public uint UserId { get; }

    /// <summary>The group identifier of the group of the file.</summary>
    public uint GroupId { get; }

    /// <summary>The size of the file in bytes.</summary>
    public uint Size { get; }

    /// <summary>The size in bytes of a block of the file.</summary>
    public uint BlockSize { get; }

    /// <summary>The device number of the file if it is a device-special file.</summary>
    public uint DeviceNumber { get; }

    /// <summary>The number of blocks the file takes up on disk.</summary>
    public uint Blocks { get; }

    /// <summary>The identifier of the file system containing the file.</summary>
    public uint FileSystemId { get; }

    /// <summary>A number which uniquely identifies the file within its file system.</summary>
    public uint FileId { get; }

    /// <summary>The time when the file was last accessed.</summary>
    public NfsTimestamp AccessTime { get; }

    /// <summary>The time when the file was last modified.</summary>
    public NfsTimestamp ModificationTime { get; }

    /// <summary>The time when the status of the file was last changed.</summary>
    public NfsTimestamp StatusChangeTime { get; }

    private NfsFileAttributes(XdrReader reader)
    {
        Type = (NfsFileType)reader.ReadUInt32();
        Mode = reader.ReadUInt32();
        LinkCount = reader.ReadUInt32();
        UserId = reader.ReadUInt32();
        GroupId = reader.ReadUInt32();
        Size = reader.ReadUInt32();
        BlockSize = reader.ReadUInt32();
        DeviceNumber = reader.ReadUInt32();
        Blocks = reader.ReadUInt32();
        FileSystemId = reader.ReadUInt32();
        FileId = reader.ReadUInt32();
        AccessTime = NfsTimestamp.ReadFrom(reader);
        ModificationTime = NfsTimestamp.ReadFrom(reader);
        StatusChangeTime = NfsTimestamp.ReadFrom(reader);
    }

    /// <summary>
    /// Read a set of file attributes from an XDR stream.
    /// </summary>
    /// <param name="reader">the stream being decoded</param>
    internal static NfsFileAttributes ReadFrom(XdrReader reader) => new(reader);
}

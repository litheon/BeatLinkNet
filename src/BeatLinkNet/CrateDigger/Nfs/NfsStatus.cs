namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// The status values which NFS version 2 procedures can return (RFC 1094 <c>stat</c>).
/// Corresponds to the generated <c>Stat</c> class in the Java crate-digger.
/// </summary>
public enum NfsStatus : uint
{
    /// <summary>The call completed successfully.</summary>
    Ok = 0,

    /// <summary>Not owner: the caller does not have the right to perform the requested operation.</summary>
    NotOwner = 1,

    /// <summary>No such file or directory.</summary>
    NoSuchEntity = 2,

    /// <summary>A hard error occurred when the operation was in progress, such as a disk error.</summary>
    IoError = 5,

    /// <summary>No such device or address.</summary>
    NoSuchDevice = 6,

    /// <summary>Permission denied.</summary>
    AccessDenied = 13,

    /// <summary>The file already exists.</summary>
    AlreadyExists = 17,

    /// <summary>No such device.</summary>
    NoDevice = 19,

    /// <summary>Not a directory: the caller specified a non-directory in a directory operation.</summary>
    NotDirectory = 20,

    /// <summary>Is a directory: the caller specified a directory in a non-directory operation.</summary>
    IsDirectory = 21,

    /// <summary>File too large: the operation would have caused a file to grow beyond the server's limit.</summary>
    FileTooBig = 27,

    /// <summary>No space left on device.</summary>
    NoSpace = 28,

    /// <summary>Write attempted on a read-only file system.</summary>
    ReadOnlyFileSystem = 30,

    /// <summary>File name too long.</summary>
    NameTooLong = 63,

    /// <summary>An attempt was made to remove a directory that was not empty.</summary>
    DirectoryNotEmpty = 66,

    /// <summary>The caller's disk quota on the server has been exceeded.</summary>
    QuotaExceeded = 69,

    /// <summary>The file handle given in the arguments is no longer valid.</summary>
    StaleFileHandle = 70,

    /// <summary>The server's write cache used in the <c>WRITECACHE</c> call got flushed to disk.</summary>
    WriteCacheFlushed = 99,
}

/// <summary>
/// The kinds of file which NFS version 2 attributes can describe (RFC 1094 <c>ftype</c>).
/// Corresponds to the generated <c>FType</c> class in the Java crate-digger.
/// </summary>
public enum NfsFileType : uint
{
    /// <summary>A non-file, used as a distinguished value.</summary>
    NonFile = 0,

    /// <summary>A regular file.</summary>
    Regular = 1,

    /// <summary>A directory.</summary>
    Directory = 2,

    /// <summary>A block-special device.</summary>
    BlockDevice = 3,

    /// <summary>A character-special device.</summary>
    CharacterDevice = 4,

    /// <summary>A symbolic link.</summary>
    SymbolicLink = 5,
}

namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// Reports a failure at the ONC RPC layer (RFC 5531, originally RFC 1057) while talking to a player:
/// a timeout with no reply, a rejected or unparseable reply, or a reply whose accepted status was
/// not success. Corresponds to the Remote Tea <c>OncRpcException</c> in the Java crate-digger,
/// but derives from <see cref="IOException"/> so callers see all communication problems uniformly.
/// </summary>
public class RpcException : IOException
{
    /// <summary>
    /// Create an exception explaining a remote procedure call failure.
    /// </summary>
    /// <param name="message">the description of the problem</param>
    public RpcException(string message) : base(message)
    {
    }

    /// <summary>
    /// Create an exception explaining a remote procedure call failure caused by another exception.
    /// </summary>
    /// <param name="message">the description of the problem</param>
    /// <param name="innerException">the underlying cause</param>
    public RpcException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

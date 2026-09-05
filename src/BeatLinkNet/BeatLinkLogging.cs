using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeatLink;

/// <summary>
/// Provides the <see cref="ILoggerFactory"/> used by all Beat Link Net classes. Because the library's
/// services are singletons, loggers cannot be constructor-injected; assign a factory here before
/// starting any of the services if you want to see log output. Defaults to a factory that discards
/// all log messages.
/// </summary>
public static class BeatLinkLogging
{
    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    /// <summary>
    /// The logger factory from which all Beat Link Net classes obtain their loggers.
    /// Set this before starting any services; classes cache loggers they have already created.
    /// </summary>
    public static ILoggerFactory LoggerFactory
    {
        get => _loggerFactory;
        set => _loggerFactory = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Creates a logger whose category is the full name of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">the type requesting a logger</typeparam>
    /// <returns>a logger backed by the current <see cref="LoggerFactory"/></returns>
    public static ILogger CreateLogger<T>() => _loggerFactory.CreateLogger(typeof(T).FullName ?? typeof(T).Name);
}

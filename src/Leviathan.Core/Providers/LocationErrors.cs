using Leviathan.Core.Paths;

namespace Leviathan.Core.Providers;

/// <summary>Maps raw exceptions and Win32 error codes to <see cref="LocationError"/>.</summary>
public static class LocationErrors
{
    /// <summary>ERROR_FILE_NOT_FOUND.</summary>
    public const int FileNotFound = 2;

    /// <summary>ERROR_PATH_NOT_FOUND.</summary>
    public const int PathNotFound = 3;

    /// <summary>ERROR_ACCESS_DENIED.</summary>
    public const int AccessDenied = 5;

    /// <summary>ERROR_BAD_NETPATH.</summary>
    public const int BadNetPath = 53;

    /// <summary>ERROR_UNEXP_NET_ERR.</summary>
    public const int UnexpectedNetworkError = 59;

    /// <summary>ERROR_NETNAME_DELETED.</summary>
    public const int NetNameDeleted = 64;

    /// <summary>ERROR_BAD_NET_NAME.</summary>
    public const int BadNetName = 67;

    /// <summary>ERROR_SEM_TIMEOUT.</summary>
    public const int SemaphoreTimeout = 121;

    /// <summary>ERROR_LOGON_FAILURE.</summary>
    public const int LogonFailure = 1326;

    /// <summary>ERROR_NETWORK_UNREACHABLE.</summary>
    public const int NetworkUnreachable = 1231;

    /// <summary>ERROR_NO_NETWORK / ERROR_CONNECTION_UNAVAIL style failures.</summary>
    public const int ConnectionUnavailable = 1201;

    /// <summary>Classifies a Win32 error code.</summary>
    public static LocationError FromWin32(int code) => code switch
    {
        FileNotFound or PathNotFound => LocationError.NotFound,
        AccessDenied => LocationError.AccessDenied,
        BadNetPath or BadNetName or UnexpectedNetworkError or NetNameDeleted or NetworkUnreachable or ConnectionUnavailable => LocationError.Unreachable,
        SemaphoreTimeout => LocationError.Timeout,
        LogonFailure => LocationError.AuthenticationRequired,
        _ => LocationError.Unknown,
    };

    /// <summary>The Win32 error code carried in an HRESULT of the form 0x8007xxxx, or the value itself otherwise.</summary>
    public static int Win32CodeFromHResult(int hresult) => (hresult & 0xFFFF0000) == unchecked((int)0x80070000) ? hresult & 0xFFFF : hresult;

    /// <summary>Human-readable name of a Win32 error code, for crash reports and logs.</summary>
    public static string Win32Name(int code) => code switch
    {
        FileNotFound => "ERROR_FILE_NOT_FOUND",
        PathNotFound => "ERROR_PATH_NOT_FOUND",
        AccessDenied => "ERROR_ACCESS_DENIED",
        BadNetPath => "ERROR_BAD_NETPATH",
        UnexpectedNetworkError => "ERROR_UNEXP_NET_ERR",
        NetNameDeleted => "ERROR_NETNAME_DELETED",
        BadNetName => "ERROR_BAD_NET_NAME",
        SemaphoreTimeout => "ERROR_SEM_TIMEOUT",
        LogonFailure => "ERROR_LOGON_FAILURE",
        NetworkUnreachable => "ERROR_NETWORK_UNREACHABLE",
        ConnectionUnavailable => "ERROR_CONNECTION_UNAVAIL",
        _ => $"WIN32_{code}",
    };

    /// <summary>Wraps any exception from a provider in a classified <see cref="LocationException"/>.</summary>
    public static LocationException Wrap(Exception exception, LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            LocationException le => le,
            UnauthorizedAccessException => new LocationException(LocationError.AccessDenied, location, $"Access to {location} was denied.", exception),
            DirectoryNotFoundException or FileNotFoundException => new LocationException(LocationError.NotFound, location, $"{location} was not found.", exception),
            TimeoutException => new LocationException(LocationError.Timeout, location, $"{location} did not respond in time.", exception),
            IOException io => FromIo(io, location),
            _ => new LocationException(LocationError.Unknown, location, exception.Message, exception),
        };
    }

    private static LocationException FromIo(IOException io, LocationPath location)
    {
        var code = Win32CodeFromHResult(io.HResult);
        var error = FromWin32(code);
        var message = error switch
        {
            LocationError.Unreachable => $"{location.Server ?? location.Root} is not responding.",
            LocationError.Timeout => $"{location} did not respond in time.",
            LocationError.AuthenticationRequired => $"{location.Server ?? location.Root} needs you to sign in.",
            LocationError.NotFound => $"{location} was not found.",
            LocationError.AccessDenied => $"Access to {location} was denied.",
            _ => io.Message,
        };
        return new LocationException(error, location, message, io);
    }
}

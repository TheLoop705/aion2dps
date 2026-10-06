using System.Net;

namespace Aion2Dps.Armory;

public enum ArmoryErrorKind
{
    /// <summary>No connection (DNS failure, connection refused, no network).</summary>
    Offline,
    /// <summary>The request timed out.</summary>
    Timeout,
    /// <summary>The character / item / board does not exist (HTTP 404 on a lookup).</summary>
    NotFound,
    /// <summary>The site answered with something we do not understand (HTML instead of JSON, missing fields, 404 on a list
    /// endpoint, 400 with valid input). NCSOFT probably changed the site.</summary>
    EndpointChanged,
    /// <summary>The site refused because of too many requests (HTTP 429).</summary>
    RateLimited,
    /// <summary>The site is down or failing (HTTP 5xx).</summary>
    ServerError,
    /// <summary>The site requires a login or blocks us (HTTP 401/403).</summary>
    Blocked,
    /// <summary>The operation is not available for this region.</summary>
    NotSupported,
    /// <summary>The request itself is invalid (empty name, ...).</summary>
    InvalidInput,
}

/// <summary>Error of an official character-info lookup. <see cref="Exception.Message"/> is a short, user-friendly sentence.</summary>
public sealed class ArmoryException : Exception
{
    public ArmoryErrorKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
    /// <summary>The URL that failed (no secrets, these are public GETs), for logs.</summary>
    public string? Url { get; }

    public ArmoryException(ArmoryErrorKind kind, string message, Exception? inner = null, HttpStatusCode? statusCode = null, string? url = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        Url = url;
    }

    public static string DefaultMessage(ArmoryErrorKind kind) => kind switch
    {
        ArmoryErrorKind.Offline => "Can't reach the official AION 2 site. Check your internet connection.",
        ArmoryErrorKind.Timeout => "The official AION 2 site took too long to answer. Try again in a moment.",
        ArmoryErrorKind.NotFound => "Not found on the official AION 2 site.",
        ArmoryErrorKind.EndpointChanged => "The official AION 2 character site changed and this lookup no longer understands it.",
        ArmoryErrorKind.RateLimited => "Too many lookups in a short time. Wait a minute and try again.",
        ArmoryErrorKind.ServerError => "The official AION 2 site is having problems right now. Try again later.",
        ArmoryErrorKind.Blocked => "The official AION 2 site refused the request (login required or blocked).",
        ArmoryErrorKind.NotSupported => "This lookup is not available for the selected region.",
        ArmoryErrorKind.InvalidInput => "Invalid input.",
        _ => "Lookup failed.",
    };
}

using System.Net;

namespace Cyberklar.AdLogin;

public sealed record LoginResult(bool Success, string Message, HttpStatusCode StatusCode, string? FinalUrl);

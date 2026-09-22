using System.Net;
using System.Text.Json;

namespace Cyberklar.AdLogin;

public sealed record StoredCookie(string Name, string Value, string Domain, string Path, DateTime? Expires, bool Secure, bool HttpOnly);

public static class SessionStore
{
    private static readonly Uri SiteUri = new("https://cyberklar.ventures.dk");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(CookieContainer cookieContainer, string filePath)
    {
        var cookies = cookieContainer.GetCookies(SiteUri)
            .Cast<Cookie>()
            .Select(c => new StoredCookie(
                c.Name,
                c.Value,
                c.Domain,
                c.Path,
                c.Expires == DateTime.MinValue ? null : c.Expires,
                c.Secure,
                c.HttpOnly))
            .ToList();

        File.WriteAllText(filePath, JsonSerializer.Serialize(cookies, JsonOptions));
    }

    public static CookieContainer? Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<List<StoredCookie>>(File.ReadAllText(filePath));
        if (stored is null)
        {
            return null;
        }

        var container = new CookieContainer();
        foreach (var c in stored)
        {
            var cookie = new Cookie(c.Name, c.Value, c.Path, c.Domain)
            {
                Secure = c.Secure,
                HttpOnly = c.HttpOnly,
            };
            if (c.Expires.HasValue)
            {
                cookie.Expires = c.Expires.Value;
            }

            container.Add(cookie);
        }

        return container;
    }
}

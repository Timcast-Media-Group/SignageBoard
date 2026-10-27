using System.Security.Claims;

namespace SignageBoard.Web.Services
{
    public static class UserNames
    {
        // given_name only arrives when the app registration exposes it as an optional claim,
        // so fall back to the first word of the display name that Entra always sends.
        public static string? FirstName(ClaimsPrincipal user)
        {
            var given = user.FindFirst(ClaimTypes.GivenName)?.Value ?? user.FindFirst("given_name")?.Value;
            if (!string.IsNullOrWhiteSpace(given))
                return given.Trim();

            var name = user.FindFirst("name")?.Value ?? user.Identity?.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('@'))
                return null;

            // Handle "Last, First" as well as "First Last".
            var comma = name.IndexOf(',');
            var first = comma >= 0 ? name[(comma + 1)..] : name;
            return first.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
    }
}

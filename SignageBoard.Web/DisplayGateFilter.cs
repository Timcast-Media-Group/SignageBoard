using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SignageBoard.Web
{
    public sealed class DisplayGateFilter(IOptionsMonitor<BoardOptions> options, ILogger<DisplayGateFilter> log) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
        {
            var http = ctx.HttpContext;
            var display = options.CurrentValue.Display;
            var ip = http.Connection.RemoteIpAddress;

            if (display.AllowedIps.Length > 0 && !IsAllowed(ip, display.AllowedIps))
            {
                log.LogWarning("Display request rejected: IP {Ip}", ip);
                return Results.NotFound();
            }

            var key = http.Request.Headers["X-Display-Key"].ToString();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(display.KeyHash))
                return Results.NotFound();

            var presented = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            byte[] expected;
            try { expected = Convert.FromHexString(display.KeyHash); }
            catch (FormatException) { return Results.NotFound(); }

            if (!CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                log.LogWarning("Display request rejected: bad key from {Ip}", ip);
                return Results.NotFound();
            }
            return await next(ctx);
        }

        private static bool IsAllowed(IPAddress? ip, string[] allowed)
        {
            if (ip is null)
                return false;

            var normalized = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

            return allowed.Any(entry => IPAddress.TryParse(entry, out var candidate) && candidate.Equals(normalized));
        }
    }
}

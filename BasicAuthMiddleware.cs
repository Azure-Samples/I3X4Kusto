using Microsoft.AspNetCore.Http;
using System;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace I3X4Kusto
{
    /// <summary>
    /// Mandatory HTTP Basic authentication for the i3X API. Credentials are supplied via the
    /// <c>I3X_BASIC_AUTH_USERNAME</c> and <c>I3X_BASIC_AUTH_PASSWORD</c> environment variables. Every request
    /// must carry a matching <c>Authorization: Basic</c> header, except:
    ///   - CORS preflight requests (<c>OPTIONS</c>), which must not require auth or the browser blocks them,
    ///   - the unauthenticated health/capabilities endpoint (<c>GET /v1/info</c>, per the i3X spec),
    ///   - the Swagger UI / OpenAPI documents.
    /// Authentication cannot be turned off. If the credential environment variables are not configured the
    /// API fails closed (HTTP 503) rather than serving requests without authentication.
    /// </summary>
    public sealed class BasicAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string _username;
        private readonly string _password;
        private readonly bool _configured;

        public BasicAuthMiddleware(RequestDelegate next)
        {
            _next = next;
            _username = Environment.GetEnvironmentVariable("I3X_BASIC_AUTH_USERNAME");
            _password = Environment.GetEnvironmentVariable("I3X_BASIC_AUTH_PASSWORD");
            _configured = !string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (IsExempt(context))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Fail closed: authentication is mandatory, so refuse to serve if no credentials are configured.
            if (!_configured)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync(
                    "Authentication is not configured. Set I3X_BASIC_AUTH_USERNAME and I3X_BASIC_AUTH_PASSWORD.")
                    .ConfigureAwait(false);
                return;
            }

            if (TryGetCredentials(context, out var user, out var pass) &&
                FixedTimeEquals(user, _username) &&
                FixedTimeEquals(pass, _password))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"i3X4Kusto\", charset=\"UTF-8\"";
            await context.Response.WriteAsync("Unauthorized").ConfigureAwait(false);
        }

        // Endpoints that must remain reachable without credentials.
        private static bool IsExempt(HttpContext context)
        {
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                return true;
            }

            PathString path = context.Request.Path;
            return path.StartsWithSegments("/v1/info", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetCredentials(HttpContext context, out string username, out string password)
        {
            username = null;
            password = null;

            string header = context.Request.Headers.Authorization;
            if (string.IsNullOrEmpty(header) ||
                !AuthenticationHeaderValue.TryParse(header, out var parsed) ||
                !string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(parsed.Parameter))
            {
                return false;
            }

            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
            }
            catch (FormatException)
            {
                return false;
            }

            int separator = decoded.IndexOf(':');
            if (separator < 0)
            {
                return false;
            }

            username = decoded.Substring(0, separator);
            password = decoded.Substring(separator + 1);
            return true;
        }

        // Constant-time comparison to avoid leaking credential length/content via timing.
        private static bool FixedTimeEquals(string a, string b)
        {
            byte[] left = Encoding.UTF8.GetBytes(a ?? string.Empty);
            byte[] right = Encoding.UTF8.GetBytes(b ?? string.Empty);
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
    }
}

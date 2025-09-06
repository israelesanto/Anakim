using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace AnakimOrchestrator.Controllers
{
    [ApiController]
    [Route("auth")]
    [Produces("application/json")]
    public class AuthBffController : ControllerBase
    {
        private readonly IConfiguration _cfg;
        private readonly IHttpClientFactory _http;
        private readonly ILogger<AuthBffController> _log;

        public AuthBffController(IConfiguration cfg, IHttpClientFactory http, ILogger<AuthBffController> log)
        {
            _cfg = cfg;
            _http = http;
            _log = log;  
        }

        // ====== Models ======
        public record LoginReq(string Email, string Password);

        // ====== Endpoints ======

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] object payload)
        {
            var baseUrl = GetAuthBaseUrl();
            var client = CreateAuthClient();

            try
            {
                var json = payload is string s ? s : JsonSerializer.Serialize(payload);
                var res = await client.PostAsync($"{baseUrl}/auth/register",
                    new StringContent(json, Encoding.UTF8, "application/json"));

                var txt = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    return StatusCode((int)res.StatusCode, TryAsJson(txt) ?? new { error = "register_failed" });

                return TryAsJson(txt) is { } ok ? Ok(ok) : Ok(new { ok = true });
            }
            catch (HttpRequestException hre)
            {
                return StatusCode(502, new { error = "authservice_unreachable", detail = hre.Message });
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginReq body)
        {
            try
            {
                if (body is null) return BadRequest(new { error = "invalid_payload" });

                var baseUrl = GetAuthBaseUrl();
                var client = CreateAuthClient();

                // logs úteis para diferenciar cURL vs navegador/proxy
                _log.LogInformation("[BFF] /auth/login start host={Host} origin={Origin} baseUrl={BaseUrl}",
                    Request.Headers.Host.ToString(),
                    Request.Headers.Origin.ToString(),
                    baseUrl);

                //var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var json = JsonSerializer.Serialize(
                    new { Email = body.Email, Password = body.Password },
                    new JsonSerializerOptions { PropertyNamingPolicy = null } // preserva "Email"/"Password"
                );
                _log.LogInformation("[BFF] payload={Payload}", json);

                HttpResponseMessage res;
                try
                {
                    res = await client.PostAsync($"{baseUrl}/auth/login",
                        new StringContent(json, Encoding.UTF8, "application/json"));
                }
                catch (HttpRequestException hre)
                {
                    _log.LogError(hre, "[BFF] AuthService unreachable");
                    return StatusCode(502, new { error = "authservice_unreachable", detail = hre.Message });
                }

                var txt = await res.Content.ReadAsStringAsync();
                _log.LogInformation("[BFF] AuthService responded status={Status} body={Body}", (int)res.StatusCode, txt);

                if (!res.IsSuccessStatusCode)
                    return StatusCode((int)res.StatusCode, TryAsJson(txt) ?? new { error = "login_failed" });

                var (access, refresh, expMinutes) = ParseTokenResponse(txt);
                if (string.IsNullOrEmpty(access))
                    return StatusCode(502, new { error = "invalid_token_payload" });

                SetAuthCookies(access!, refresh, expMinutes);
                return Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[BFF] Unexpected error in /auth/login");
                return StatusCode(500, new { error = "bff_login_exception", detail = ex.Message });
            }
        }

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            var baseUrl = GetAuthBaseUrl();
            var client = CreateAuthClient();

            var refresh = Request.Cookies[CookieName("rt")];
            if (string.IsNullOrEmpty(refresh))
                return Unauthorized(new { error = "refresh_cookie_missing" });

            try
            {
                var payload = JsonSerializer.Serialize(new { refresh_token = refresh });
                var res = await client.PostAsync($"{baseUrl}/auth/refresh",
                    new StringContent(payload, Encoding.UTF8, "application/json"));

                var txt = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                {
                    ClearAuthCookies();
                    return StatusCode((int)res.StatusCode, TryAsJson(txt) ?? new { error = "refresh_failed" });
                }

                var (access, newRefresh, expMinutes) = ParseTokenResponse(txt);
                if (string.IsNullOrEmpty(access))
                {
                    ClearAuthCookies();
                    return StatusCode(502, new { error = "invalid_token_payload" });
                }

                SetAuthCookies(access!, newRefresh ?? refresh, expMinutes);
                return Ok(new { ok = true });
            }
            catch (HttpRequestException hre)
            {
                ClearAuthCookies();
                return StatusCode(502, new { error = "authservice_unreachable", detail = hre.Message });
            }
        }

        [HttpGet("me")]
        public IActionResult Me()
        {
            var access = Request.Cookies[CookieName("at")];
            if (string.IsNullOrEmpty(access))
                return Unauthorized(new { error = "access_cookie_missing" });

            var (ok, claims, error) = TryValidateToken(access);
            if (!ok)
            {
                if (error == "token_expired") return Unauthorized(new { error = "token_expired" });
                return Unauthorized(new { error = error ?? "invalid_token" });
            }

            return Ok(claims);
        }

        [HttpPost("logout")]
        public IActionResult Logout()
        {
            ClearAuthCookies();
            return Ok(new { ok = true });
        }

        // ====== Helpers ======

        private HttpClient CreateAuthClient()
        {
            var internalKey = _cfg["AuthService:InternalKey"];
            var client = _http.CreateClient("AuthClient"); // já configurado no Program.cs

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(internalKey))
            {
                if (client.DefaultRequestHeaders.Contains("X-Internal-Key"))
                    client.DefaultRequestHeaders.Remove("X-Internal-Key");
                client.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);
            }
            return client;
        }

        private string GetAuthBaseUrl()
        {
            var baseUrl = _cfg["AuthService:BaseUrl"] ?? throw new InvalidOperationException("AuthService:BaseUrl ausente");
            return baseUrl.TrimEnd('/');
        }

        private static object? TryAsJson(string text)
        {
            try { return JsonSerializer.Deserialize<object>(text); }
            catch { return null; }
        }

        private (string? access, string? refresh, int? expMinutes) ParseTokenResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? access =
                    GetString(root, "access_token") ??
                    GetString(root, "token");

                string? refresh =
                    GetString(root, "refresh_token") ??
                    GetString(root, "refreshToken");

                int? expiresIn =
                    GetInt(root, "expires_in") ??
                    GetInt(root, "expiresIn");

                int? expMinutes = expiresIn.HasValue ? Math.Max(1, expiresIn.Value / 60) : GetInt(root, "expMinutes");
                return (access, refresh, expMinutes);
            }
            catch
            {
                return (null, null, null);
            }
        }

        private static string? GetString(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static int? GetInt(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

        // ---- Cookies (usa seção "Cookies" do appsettings) ----
        private string CookieBaseName() => _cfg["Cookies:Name"] ?? "anakim";
        private string CookieName(string suffix) => $"{CookieBaseName()}_{suffix}";

        private void SetAuthCookies(string access, string? refresh, int? expiresInMinutes)
        {
            var secure = bool.TryParse(_cfg["Cookies:Secure"], out var s) ? s : true;
            var sameSiteCfg = (_cfg["Cookies:SameSite"] ?? "Lax").Trim();
            var sameSite = sameSiteCfg.Equals("None", StringComparison.OrdinalIgnoreCase) ? SameSiteMode.None
                          : sameSiteCfg.Equals("Strict", StringComparison.OrdinalIgnoreCase) ? SameSiteMode.Strict
                          : SameSiteMode.Lax;
            var minutes = expiresInMinutes ?? (int.TryParse(_cfg["Cookies:ExpireMinutes"], out var m) ? m : 60);
            var domain = _cfg["Cookies:Domain"];
            if (sameSite == SameSiteMode.None) secure = true; // exigência do browser

            var accessOpts = new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = sameSite,
                Path = "/",
                Domain = string.IsNullOrWhiteSpace(domain) ? null : domain,
                MaxAge = TimeSpan.FromMinutes(minutes)
            };
            Response.Cookies.Append(CookieName("at"), access, accessOpts);

            if (!string.IsNullOrEmpty(refresh))
            {
                var refreshOpts = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = secure,
                    SameSite = sameSite,
                    Path = "/auth", // escopo apenas para endpoints de auth
                    Domain = string.IsNullOrWhiteSpace(domain) ? null : domain,
                    MaxAge = TimeSpan.FromDays(7)
                };
                Response.Cookies.Append(CookieName("rt"), refresh!, refreshOpts);
            }
        }

        private void ClearAuthCookies()
        {
            var baseName = CookieBaseName();
            void Expire(string suffix, string path)
            {
                Response.Cookies.Append($"{baseName}_{suffix}", "",
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Lax,
                        Path = path,
                        Domain = string.IsNullOrWhiteSpace(_cfg["Cookies:Domain"]) ? null : _cfg["Cookies:Domain"],
                        Expires = DateTimeOffset.UnixEpoch
                    });
            }
            Expire("at", "/");
            Expire("rt", "/auth");
        }

        // ====== JWT local validation ======
        // ====== JWT local validation ======
        private (bool ok, Dictionary<string, object>? claims, string? error) TryValidateToken(string token)
        {
            var keyStr = _cfg["Jwt:Key"];
            var issuerCfg = _cfg["Jwt:Issuer"];

            if (string.IsNullOrWhiteSpace(keyStr))
                return (false, null, "jwt_config_missing"); // precisa da MESMA chave do AuthService

            // Constrói a chave a partir de Base64 (se possível) ou texto
            var signingKey = new SymmetricSecurityKey(GetSigningKeyBytes(keyStr));

            var handler = new JwtSecurityTokenHandler();

            // Lê sem validar só para inspecionar 'iss'
            JwtSecurityToken? readJwt = null;
            try { readJwt = handler.ReadJwtToken(token); } catch { /* segue para validação */ }

            var tokenHasIssuer = !string.IsNullOrWhiteSpace(readJwt?.Issuer);
            var shouldValidateIssuer = tokenHasIssuer && !string.IsNullOrWhiteSpace(issuerCfg);

            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = shouldValidateIssuer,
                ValidIssuer = shouldValidateIssuer ? issuerCfg : null,

                ValidateAudience = false, // sem audience por ora
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,

                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RequireSignedTokens = true
            };

            try
            {
                var principal = handler.ValidateToken(token, parameters, out var validatedToken);
                var jwt = (JwtSecurityToken)validatedToken;

                var dict = principal.Claims
                    .GroupBy(c => c.Type)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Count() > 1 ? (object)g.Select(c => c.Value).ToArray() : g.First().Value
                    );

                dict["jti"] = jwt.Id;
                if (jwt.Payload.Exp.HasValue) dict["exp"] = jwt.Payload.Exp;
                if (!string.IsNullOrWhiteSpace(jwt.Issuer)) dict["iss"] = jwt.Issuer;

                return (true, dict, null);
            }
            catch (SecurityTokenExpiredException)
            {
                return (false, null, "token_expired");
            }
            catch (SecurityTokenInvalidIssuerException)
            {
                return (false, null, "invalid_issuer");
            }
            catch (SecurityTokenInvalidSignatureException)
            {
                return (false, null, "invalid_signature");
            }
            catch
            {
                return (false, null, "token_invalid");
            }
        }

        // Tenta decodificar Base64; se falhar, usa UTF8
        private static byte[] GetSigningKeyBytes(string key)
        {
            try
            {
                // remove espaços e padding irregular antes de tentar base64
                var compact = key.Trim();
                return Convert.FromBase64String(compact);
            }
            catch
            {
                return Encoding.UTF8.GetBytes(key);
            }
        }

    }
}

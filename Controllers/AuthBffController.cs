using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;


namespace AnakimOrchestrator.Controllers
{
    [ApiController]
    [Route("auth")]
    [Produces("application/json")]
    public class AuthBffController : ControllerBase
    {
        private readonly IConfiguration _cfg;
        private readonly IHttpClientFactory _http;

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] object payload)
        {
            var baseUrl = _cfg["AuthService:BaseUrl"] ?? throw new InvalidOperationException("AuthService:BaseUrl ausente");
            var internalKey = _cfg["AuthService:InternalKey"] ?? "";

            var client = _http.CreateClient("AuthClient");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(internalKey))
                client.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);

            // Encaminha para o AuthService — mantenha o path padrão para simplificar config
            HttpResponseMessage res;
            try
            {
                var json = payload is string s ? s : JsonSerializer.Serialize(payload);
                res = await client.PostAsync($"{baseUrl}/auth/register",
                    new StringContent(json, Encoding.UTF8, "application/json"));
            }
            catch (HttpRequestException hre)
            {
                return StatusCode(502, new { error = "authservice_unreachable", detail = hre.Message });
            }

            var txt = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                var err = TryAsJson(txt) ?? new { error = "register_failed" };
                return StatusCode((int)res.StatusCode, err);
            }

            // Não cria cookies aqui: normalmente exige verificação de e-mail antes de login
            // Apenas propaga o OK do AuthService
            return TryAsJson(txt) is { } ok ? Ok(ok) : Ok(new { ok = true });
        }


        public AuthBffController(IConfiguration cfg, IHttpClientFactory http)
        {
            _cfg = cfg;
            _http = http;
        }

        public record LoginReq(string username, string password);

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginReq body)
        {
            if (body is null)
                return BadRequest(new { error = "invalid_payload" });

            var baseUrl = _cfg["AuthService:BaseUrl"] ?? throw new InvalidOperationException("AuthService:BaseUrl ausente");
            var internalKey = _cfg["AuthService:InternalKey"] ?? string.Empty;

            var client = _http.CreateClient("AuthClient");

            // Headers
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Envia a chave interna exigida pelo AuthService
            if (!string.IsNullOrEmpty(internalKey))
            {
                if (client.DefaultRequestHeaders.Contains("X-Internal-Key"))
                    client.DefaultRequestHeaders.Remove("X-Internal-Key");
                client.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);
            }

            // Serializa como camelCase para compatibilidade (username/password)
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            HttpResponseMessage res;
            try
            {
                var url = $"{baseUrl.TrimEnd('/')}/auth/login";
                res = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            }
            catch (HttpRequestException hre)
            {
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "authservice_unreachable", detail = hre.Message });
            }

            var txt = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                // Propaga o JSON de erro do AuthService se houver; senão, mensagem padrão
                var err = TryAsJson(txt) ?? new { error = "login_failed" };
                return StatusCode((int)res.StatusCode, err);
            }

            var (access, refresh, expMinutes) = ParseTokenResponse(txt);
            if (string.IsNullOrEmpty(access))
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "invalid_token_payload" });

            SetAuthCookies(access, refresh, expMinutes);
            return Ok(new { ok = true });
        }


        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            var baseUrl = _cfg["AuthService:BaseUrl"] ?? throw new InvalidOperationException("AuthService:BaseUrl ausente");
            var internalKey = _cfg["AuthService:InternalKey"] ?? "";
            var refresh = Request.Cookies[CookieName("rt")];

            if (string.IsNullOrEmpty(refresh))
                return Unauthorized(new { error = "refresh_cookie_missing" });

            var client = _http.CreateClient("AuthClient");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(internalKey))
                client.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);

            var payload = JsonSerializer.Serialize(new { refresh_token = refresh });
            HttpResponseMessage res;
            try
            {
                res = await client.PostAsync($"{baseUrl}/auth/refresh",
                    new StringContent(payload, Encoding.UTF8, "application/json"));
            }
            catch (HttpRequestException hre)
            {
                ClearAuthCookies();
                return StatusCode(502, new { error = "authservice_unreachable", detail = hre.Message });
            }

            var txt = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                ClearAuthCookies();
                var err = TryAsJson(txt) ?? new { error = "refresh_failed" };
                return StatusCode((int)res.StatusCode, err);
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

        [HttpPost("logout")]
        public IActionResult Logout()
        {
            ClearAuthCookies();
            return Ok(new { ok = true });
        }

        // -------- helpers --------

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

                // aceita "access_token" OU "token"
                string? access =
                    GetString(root, "access_token") ??
                    GetString(root, "token");

                // aceita "refresh_token" OU "refreshToken" (opcional)
                string? refresh =
                    GetString(root, "refresh_token") ??
                    GetString(root, "refreshToken");

                // aceita "expires_in" OU "expiresIn"
                int? expiresIn =
                    GetInt(root, "expires_in") ??
                    GetInt(root, "expiresIn");

                int? expMinutes = expiresIn.HasValue ? expiresIn.Value / 60 : null;

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

        private string CookieBaseName() => _cfg["Cookie:Name"] ?? "anakim";
        private string CookieName(string suffix) => $"{CookieBaseName()}_{suffix}";

        private void SetAuthCookies(string access, string? refresh, int? expiresInMinutes)
        {
            var secure = bool.TryParse(_cfg["Cookie:Secure"], out var s) ? s : true;
            var sameSite = (_cfg["Cookie:SameSite"] ?? "Lax") switch
            {
                "Strict" => SameSiteMode.Strict,
                "None" => SameSiteMode.None,
                _ => SameSiteMode.Lax
            };
            var minutes = expiresInMinutes ?? (int.TryParse(_cfg["Cookie:Minutes"], out var m) ? m : 60);

            var accessOpts = new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = sameSite,
                Path = "/",
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
                    Path = "/auth/refresh",
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
                        Expires = DateTimeOffset.UnixEpoch
                    });
            }
            Expire("at", "/");
            Expire("rt", "/auth/refresh");
        }
    }
}

using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;
using AnakimOrchestrator.Infrastructure;
using AnakimSuite.AnakimAccessProvider;
using Microsoft.AspNetCore.Http; // <-- novo

namespace AnakimOrchestrator.Services
{
    public enum ScriptLanguage
    {
        CSharp = 1,
        Python = 2,
        JavaScript = 3
    }

    public class ScriptExecutorService
    {
        private readonly IConfiguration _configuration;
        private readonly IAnakimAccessProvider _dbProvider;
        private readonly ILogger<ScriptExecutorService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor; // <-- novo

        private static readonly string BaseScriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts");

        // Cache de scripts compilados
        private static readonly ConcurrentDictionary<string, (Script<object> Script, DateTime LastWrite)> _scriptCache = new();

        public ScriptExecutorService(
            IConfiguration configuration,
            IAnakimAccessProvider dbProvider,
            ILogger<ScriptExecutorService> logger,
            IHttpContextAccessor httpContextAccessor // <-- novo
        )
        {
            _configuration = configuration;
            _dbProvider = dbProvider;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor; // <-- novo
        }

        // 🔧 aceitamos args = null e normalizamos internamente
        public async Task<object?> RunScriptAsync(string scriptName, IDictionary<string, object>? args, int? languageOverride = null) // <-- ❗ args agora é nullable
        {
            // Se vier null (corpo vazio), viramos um dicionário vazio
            args ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); // <-- normalize

            // Injeta variáveis padrão
            InjectBuiltInVariables(args);

            // Injeta contexto do request: headers, current_user_id (via token)
            var headers = InjectRequestContext(args); // <-- novo

            var languageCode = languageOverride ?? _configuration.GetValue<int>("ScriptSettings:DefaultLanguage");

            return (ScriptLanguage)languageCode switch
            {
                ScriptLanguage.CSharp => await RunCSharpScriptAsync(scriptName, args, headers), // <-- passa headers
                _ => throw new NotSupportedException($"Código de linguagem '{languageCode}' não é suportado.")
            };
        }

        private async Task<object?> RunCSharpScriptAsync(string scriptName, IDictionary<string, object> args, IDictionary<string, object> headers)
        {
            var scriptPath = Path.Combine(BaseScriptPath, "CSharp", $"{scriptName}.csx");

            if (!File.Exists(scriptPath))
            {
                _logger.LogError("Script '{ScriptName}' não encontrado no caminho {ScriptPath}", scriptName, scriptPath);
                throw new FileNotFoundException($"Script '{scriptName}' não encontrado em CSharp.");
            }

            _logger.LogInformation("Iniciando execução do script '{ScriptName}'", scriptName);

            var lastWriteTime = File.GetLastWriteTimeUtc(scriptPath);

            if (!_scriptCache.TryGetValue(scriptPath, out var cached) || cached.LastWrite < lastWriteTime)
            {
                var code = await File.ReadAllTextAsync(scriptPath);

                var options = ScriptOptions.Default
                    .AddImports("System", "System.Collections.Generic")
                    .AddReferences(AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)))
                    .AddReferences("Microsoft.CSharp");

                var compiledScript = CSharpScript.Create(code, options, typeof(Globals));

                var diagnostics = compiledScript.GetCompilation().GetDiagnostics();
                if (diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                {
                    var errors = string.Join(", ", diagnostics);
                    _logger.LogError("Erro de compilação no script '{ScriptName}': {Errors}", scriptName, errors);
                    throw new Exception("Erro ao compilar script: " + errors);
                }

                _scriptCache[scriptPath] = (compiledScript, lastWriteTime);
                _logger.LogInformation("Script '{ScriptName}' compilado e armazenado em cache", scriptName);
            }

            var (script, _) = _scriptCache[scriptPath];
            var globals = new Globals
            {
                Args = args,
                Db = _dbProvider,
                Headers = headers // <-- novo
            };

            var scriptState = await script.RunAsync(globals);
            var scriptObject = scriptState?.ReturnValue;

            if (scriptObject == null)
            {
                _logger.LogWarning("Script '{ScriptName}' executado, mas retornou null", scriptName);
                throw new Exception("Script executado, mas não retornou uma instância.");
            }

            var runMethod = scriptObject.GetType().GetMethod(
                "Run",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            if (runMethod == null)
            {
                _logger.LogError("Script '{ScriptName}' não possui método 'Run'", scriptName);
                throw new Exception("Método 'Run' não encontrado na instância retornada.");
            }

            try
            {
                _logger.LogInformation("Executando método Run do script '{ScriptName}'", scriptName);

                var returnVal = runMethod.Invoke(scriptObject, new object[] { globals });

                // Robusto: aceita Task<object>, Task, ou retorno direto
                if (returnVal is Task<object> tobj) return await tobj;
                if (returnVal is Task t) { await t; return null; }
                return returnVal;
            }
            catch (TargetInvocationException ex)
            {
                _logger.LogError(ex.InnerException ?? ex, "Erro ao executar o script '{ScriptName}'", scriptName);
                throw new Exception($"Erro ao executar método Run: {ex.InnerException?.Message}", ex);
            }
        }

        private void InjectBuiltInVariables(IDictionary<string, object> args)
        {
            if (!args.ContainsKey("__instance"))
            {
                var instanceName = _configuration.GetSection("ProxySettings")?.GetValue<string>("InstanceName") ?? "Unknown";
                args["__instance"] = instanceName;
            }

            args["__timestamp"] = DateTime.UtcNow;
            args["__uid"] = Guid.NewGuid().ToString();
        }

        // ---- NOVO: injeta headers e current_user_id (se houver token) ----
        private IDictionary<string, object> InjectRequestContext(IDictionary<string, object> args)
        {
            var headers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx == null) return headers;

            var authHdr = ctx.Request.Headers["Authorization"].ToString();
            var cookieHdr = ctx.Request.Headers["Cookie"].ToString();
            var xAccess = ctx.Request.Headers["X-Access-Token"].ToString();
            var qAccess = ctx.Request.Query["access_token"].ToString(); // fallback via query

            if (!string.IsNullOrWhiteSpace(authHdr)) headers["Authorization"] = authHdr;
            if (!string.IsNullOrWhiteSpace(cookieHdr)) headers["Cookie"] = cookieHdr;
            if (!string.IsNullOrWhiteSpace(xAccess)) headers["X-Access-Token"] = xAccess;

            // LOG de debug (não imprime token)
            try
            {
                var cookieKeys = string.Join(", ", ctx.Request.Cookies.Keys);
                _logger.LogInformation("[Scripts] Auth hdr? {HasAuth} | Cookie hdr len: {CookieLen} | CookieKeys: {CookieKeys}",
                    !string.IsNullOrWhiteSpace(authHdr), cookieHdr?.Length ?? 0, cookieKeys);
            }
            catch { }

            string? token = null;

            // 1) Authorization: Bearer <token> (regex tolerante)
            if (!string.IsNullOrWhiteSpace(authHdr))
            {
                var m = System.Text.RegularExpressions.Regex.Match(authHdr, @"^\s*Bearer\s+(.+?)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                    token = m.Groups[1].Value.Trim();

                // 1.1) Se não for "Bearer ...", mas parecer um JWT (xxx.yyy.zzz), aceita assim mesmo
                if (string.IsNullOrWhiteSpace(token) && authHdr.Count(c => c == '.') == 2)
                    token = authHdr.Trim(); // Authorization contém o próprio JWT
            }

            // 2) Cookie: <Cookies:Name>_at
            if (string.IsNullOrWhiteSpace(token))
            {
                var cookieBase = _configuration["Cookies:Name"] ?? "anakim";
                var atName = $"{cookieBase}_at";
                if (ctx.Request.Cookies.TryGetValue(atName, out var atVal) && !string.IsNullOrWhiteSpace(atVal))
                    token = atVal;
            }

            // 3) Fallbacks: X-Access-Token (header) e access_token (query)
            if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(xAccess))
                token = xAccess;
            if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(qAccess))
                token = qAccess;

            if (!string.IsNullOrWhiteSpace(token))
            {
                var userId = TryGetSubAsLong(token);
                _logger.LogInformation("[Scripts] Token detectado. sub={UserId}", userId?.ToString() ?? "null");
                if (userId.HasValue)
                    args["current_user_id"] = userId.Value;
            }
            else
            {
                _logger.LogWarning("[Scripts] Nenhum token encontrado em Authorization, Cookie *_at, X-Access-Token ou ?access_token=.");
            }

            return headers;
        }

        // --- helpers de JWT (decodifica payload e lê 'sub') ---
        private static long? TryGetSubAsLong(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                var payload = Base64UrlDecode(parts[1]);

                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (root.TryGetProperty("sub", out var subProp))
                {
                    var subStr = subProp.GetString();
                    if (long.TryParse(subStr, out var idAsLong))
                        return idAsLong;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static string Base64UrlDecode(string input)
        {
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            var bytes = Convert.FromBase64String(s);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public class Globals
        {
            public IDictionary<string, object> Args { get; set; } = new Dictionary<string, object>();
            public IAnakimAccessProvider Db { get; set; }
            public IDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>(); // <-- novo
        }
    }
}

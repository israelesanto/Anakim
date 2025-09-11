using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;
using AnakimOrchestrator.Infrastructure;
using AnakimSuite.AnakimAccessProvider;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

// JS via Jint
using Jint;

namespace AnakimOrchestrator.Services
{
    public enum ScriptLanguage { CSharp = 1, Python = 2, JavaScript = 3 }

    public class ScriptExecutorService
    {
        private readonly IConfiguration _configuration;
        private readonly IAnakimAccessProvider _dbProvider;
        private readonly ILogger<ScriptExecutorService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        private static readonly string BaseScriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts");

        // Cache de scripts C# compilados
        private static readonly ConcurrentDictionary<string, (Script<object> Script, DateTime LastWrite)> _scriptCache = new();

        // Cache de resolução (nome -> extensão + lastWrite)
        private static readonly ConcurrentDictionary<string, (string ext, DateTime lastWrite)> _routeCache = new();

        public ScriptExecutorService(
            IConfiguration configuration,
            IAnakimAccessProvider dbProvider,
            ILogger<ScriptExecutorService> logger,
            IHttpContextAccessor httpContextAccessor
        )
        {
            _configuration = configuration;
            _dbProvider = dbProvider;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }

        /// <summary>
        /// Descobre automaticamente: JS se existir .js, senão C# se existir .csx, senão 404.
        /// Opcionalmente aceita languageOverride interno (não exposto na URL).
        /// </summary>
        public async Task<object?> RunScriptAsync(string scriptName, IDictionary<string, object> args, int? languageOverride = null)
        {
            InjectBuiltInVariables(args);
            var headers = InjectRequestContext(args);

            // Override interno (se você quiser usar a partir de outro ponto da app)
            if (languageOverride.HasValue)
            {
                return ((ScriptLanguage)languageOverride.Value) switch
                {
                    ScriptLanguage.JavaScript => await RunJavaScriptAsync(scriptName, args, headers),
                    ScriptLanguage.CSharp => await RunCSharpScriptAsync(scriptName, args, headers),
                    _ => throw new NotSupportedException($"Linguagem override '{languageOverride}' não suportada.")
                };
            }

            // Resolução com cache (prioridade JS -> C#)
            if (!TryResolveWithCache(scriptName, out var ext))
            {
                _logger.LogError("Script '{ScriptName}' não encontrado em JavaScript nem em CSharp.", scriptName);
                throw new FileNotFoundException($"Script '{scriptName}' não encontrado.");
            }

            return ext == ".js"
                ? await RunJavaScriptAsync(scriptName, args, headers)
                : await RunCSharpScriptAsync(scriptName, args, headers);
        }

        // ========================= Resolução com cache =========================
        private bool TryResolveWithCache(string name, out string ext)
        {
            // 1) Tenta cache (só aceita se o arquivo ainda existe)
            if (_routeCache.TryGetValue(name, out var hit))
            {
                var path = ResolvePathByExt(name, hit.ext);
                if (File.Exists(path))
                {
                    var nowWrite = File.GetLastWriteTimeUtc(path);
                    if (nowWrite == hit.lastWrite)
                    {
                        ext = hit.ext;
                        return true;
                    }
                }

                // arquivo mudou/sumiu -> invalida cache para revalidar
                _routeCache.TryRemove(name, out _);
            }

            // 2) Revalidação: prioridade JS → C#
            var js = Path.Combine(BaseScriptPath, "JavaScript", $"{name}.js");
            if (File.Exists(js))
            {
                ext = ".js";
                _routeCache[name] = (ext, File.GetLastWriteTimeUtc(js));
                return true;
            }

            var csx = Path.Combine(BaseScriptPath, "CSharp", $"{name}.csx");
            if (File.Exists(csx))
            {
                ext = ".csx";
                _routeCache[name] = (ext, File.GetLastWriteTimeUtc(csx));
                return true;
            }

            ext = "";
            return false;
        }


        private static string ResolvePathByExt(string name, string ext)
        {
            return ext switch
            {
                ".js" => Path.Combine(BaseScriptPath, "JavaScript", $"{name}.js"),
                ".csx" => Path.Combine(BaseScriptPath, "CSharp", $"{name}.csx"),
                _ => Path.Combine(BaseScriptPath, $"{name}{ext}")
            };
        }

        // ========================= Execução C# (.csx) =========================
        private async Task<object?> RunCSharpScriptAsync(string scriptName, IDictionary<string, object> args, IDictionary<string, object> headers)
        {
            var scriptPath = Path.Combine(BaseScriptPath, "CSharp", $"{scriptName}.csx");
            if (!File.Exists(scriptPath))
            {
                _logger.LogError("Script '{ScriptName}' não encontrado no caminho {ScriptPath}", scriptName, scriptPath);
                throw new FileNotFoundException($"Script '{scriptName}' não encontrado em CSharp.");
            }

            _logger.LogInformation("Iniciando execução do script C# '{ScriptName}'", scriptName);
            var lastWriteTime = File.GetLastWriteTimeUtc(scriptPath);

            if (!_scriptCache.TryGetValue(scriptPath, out var cached) || cached.LastWrite < lastWriteTime)
            {
                var code = await File.ReadAllTextAsync(scriptPath);

                var options = ScriptOptions.Default
                    .AddImports(
                        "System", "System.Collections.Generic", "System.Linq",
                        "System.Threading.Tasks", "System.Text", "System.Text.Json"
                    )
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
                _logger.LogInformation("Script C# '{ScriptName}' compilado e armazenado em cache", scriptName);
            }

            var (script, _) = _scriptCache[scriptPath];
            var globals = BuildGlobals(scriptName, args, headers);

            var state = await script.RunAsync(globals);
            var ret = state?.ReturnValue;

            var runTask = TryGetRunTask(ret, globals);
            if (runTask is not null)
                return await runTask;

            var resolved = await ResolveReturnAsync(ret, globals);
            if (resolved is not null)
                return resolved;

            _logger.LogWarning("Script '{ScriptName}' não retornou conteúdo (null).", scriptName);
            return new { success = true };
        }

        // ========================= Execução JavaScript (.js) via Jint =========================
        private async Task<object?> RunJavaScriptAsync(string scriptName, IDictionary<string, object> args, IDictionary<string, object> headers)
        {
            var scriptPath = Path.Combine(BaseScriptPath, "JavaScript", $"{scriptName}.js");

            // Se não existir JS, tenta C# antes de errar
            if (!File.Exists(scriptPath))
            {
                var csxPath = Path.Combine(BaseScriptPath, "CSharp", $"{scriptName}.csx");
                if (File.Exists(csxPath))
                {
                    _logger.LogWarning("JS '{ScriptName}' não encontrado; fazendo fallback para C#.", scriptName);
                    // Atualiza o cache de resolução para refletir a realidade
                    _routeCache[scriptName] = (".csx", File.GetLastWriteTimeUtc(csxPath));
                    return await RunCSharpScriptAsync(scriptName, args, headers);
                }

                _logger.LogError("Script '{ScriptName}' não encontrado no caminho {ScriptPath}", scriptName, scriptPath);
                throw new FileNotFoundException($"Script '{scriptName}' não encontrado em JavaScript.");
            }

            _logger.LogInformation("Iniciando execução do script JS '{ScriptName}'", scriptName);

            var code = await File.ReadAllTextAsync(scriptPath);
            var g = BuildGlobals(scriptName, args, headers);

            // Limites fixos (simples). Se quiser, leia de config.
            var timeoutSeconds = 2;
            var maxMemoryBytes = 16_000_000;

            var engine = new Engine(o => o
                .Strict()
                .TimeoutInterval(TimeSpan.FromSeconds(timeoutSeconds))
                .LimitMemory(maxMemoryBytes));

            // expor só o necessário
            engine.SetValue("g", g);
            if (g.LogInfo != null) engine.SetValue("logInfo", new Action<string>(g.LogInfo));
            if (g.LogWarn != null) engine.SetValue("logWarn", new Action<string>(g.LogWarn));
            if (g.LogError != null) engine.SetValue("logError", new Action<string>(m => g.LogError!(new Exception(m), m)));

            engine.Execute(code);
            var result = engine.Invoke("handle", g).ToObject();

            var resolved = await ResolveReturnAsync(result, g);
            return resolved ?? new { success = true };
        }


        // ========================= Helpers comuns =========================
        private Globals BuildGlobals(string scriptName, IDictionary<string, object> args, IDictionary<string, object> headers)
        {
            long? currentUserId = null;
            if (args.TryGetValue("current_user_id", out var uidObj) && uidObj is not null
                && long.TryParse(uidObj.ToString(), out var uidParsed))
            {
                currentUserId = uidParsed;
            }

            var ctx = _httpContextAccessor.HttpContext;

            var globals = new Globals
            {
                Args = args,
                Headers = headers,
                Db = _dbProvider,

                CurrentUserId = currentUserId,
                Config = _configuration,
                NowUtc = DateTime.UtcNow,
                Cancellation = ctx?.RequestAborted ?? CancellationToken.None,

                LogInfo = msg => _logger.LogInformation("[Script:{Script}] {Msg}", scriptName, msg),
                LogWarn = msg => _logger.LogWarning("[Script:{Script}] {Msg}", scriptName, msg),
                LogError = (ex, msg) => _logger.LogError(ex, "[Script:{Script}] {Msg}", scriptName, msg),
            };

            if (ctx?.Request != null)
            {
                globals.Set("BaseUrl", $"{ctx.Request.Scheme}://{ctx.Request.Host}");
                globals.Set("Path", ctx.Request.Path.ToString());
                globals.Set("TraceId", ctx.TraceIdentifier);
            }

            return globals;
        }

        private static Task<object>? TryGetRunTask(object? obj, Globals g)
        {
            if (obj is null) return null;
            var m = obj.GetType().GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m is null) return null;

            var result = m.Invoke(obj, new object[] { g });
            return result switch
            {
                Task<object> t => t,
                Task t => WrapTask(t),
                _ => Task.FromResult((object)result!)
            };

            static async Task<object> WrapTask(Task t)
            {
                await t.ConfigureAwait(false);
                return new { success = true };
            }
        }

        private static async Task<object?> ResolveReturnAsync(object? ret, Globals g)
        {
            if (ret is null) return null;

            if (ret is Task<object> tobj) return await tobj.ConfigureAwait(false);
            if (ret is Task t) { await t.ConfigureAwait(false); return new { success = true }; }

            if (ret is Func<Globals, Task<object>> f1) return await f1(g).ConfigureAwait(false);
            if (ret is Func<dynamic, Task<object>> f2) return await f2(g).ConfigureAwait(false);
            if (ret is Func<Globals, object> f3) return f3(g);
            if (ret is Func<dynamic, object> f4) return f4(g);

            return ret;
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

        private IDictionary<string, object> InjectRequestContext(IDictionary<string, object> args)
        {
            var headers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx == null) return headers;

            var authHdr = ctx.Request.Headers["Authorization"].ToString();
            var cookieHdr = ctx.Request.Headers["Cookie"].ToString();
            var xAccess = ctx.Request.Headers["X-Access-Token"].ToString();
            var qAccess = ctx.Request.Query["access_token"].ToString();

            if (!string.IsNullOrWhiteSpace(authHdr)) headers["Authorization"] = authHdr;
            if (!string.IsNullOrWhiteSpace(cookieHdr)) headers["Cookie"] = cookieHdr;
            if (!string.IsNullOrWhiteSpace(xAccess)) headers["X-Access-Token"] = xAccess;

            try
            {
                var cookieKeys = string.Join(", ", ctx.Request.Cookies.Keys);
                _logger.LogInformation("[Scripts] Auth hdr? {HasAuth} | Cookie hdr len: {CookieLen} | CookieKeys: {CookieKeys}",
                    !string.IsNullOrWhiteSpace(authHdr), cookieHdr?.Length ?? 0, cookieKeys);
            }
            catch { }

            string? token = null;

            if (!string.IsNullOrWhiteSpace(authHdr))
            {
                var m = System.Text.RegularExpressions.Regex.Match(authHdr, @"^\s*Bearer\s+(.+?)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) token = m.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(token) && authHdr.Count(c => c == '.') == 2)
                    token = authHdr.Trim();
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                var cookieBase = _configuration["Cookies:Name"] ?? "anakim";
                var atName = $"{cookieBase}_at";
                if (ctx.Request.Cookies.TryGetValue(atName, out var atVal) && !string.IsNullOrWhiteSpace(atVal))
                    token = atVal;
            }

            if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(xAccess)) token = xAccess;
            if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(qAccess)) token = qAccess;

            if (!string.IsNullOrWhiteSpace(token))
            {
                var userId = TryGetSubAsLong(token);
                _logger.LogInformation("[Scripts] Token detectado. sub={UserId}", userId?.ToString() ?? "null");
                if (userId.HasValue) args["current_user_id"] = userId.Value;
            }
            else
            {
                _logger.LogWarning("[Scripts] Nenhum token encontrado em Authorization, Cookie *_at, X-Access-Token ou ?access_token=.");
            }

            return headers;
        }

        private static long? TryGetSubAsLong(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                var payload = Base64UrlDecode(parts[1]);

                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("sub", out var subProp))
                {
                    var subStr = subProp.GetString();
                    if (long.TryParse(subStr, out var idAsLong))
                        return idAsLong;
                }
            }
            catch { }
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
            public IDictionary<string, object> Args { get; init; } = new Dictionary<string, object>();
            public IDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>();
            public IAnakimAccessProvider Db { get; init; } = default!;

            public long? CurrentUserId { get; init; }

            public IConfiguration Config { get; init; } = default!;
            public DateTime NowUtc { get; init; } = DateTime.UtcNow;
            public CancellationToken Cancellation { get; init; }

            public Action<string>? LogInfo { get; init; }
            public Action<string>? LogWarn { get; init; }
            public Action<Exception, string>? LogError { get; init; }

            public IDictionary<string, object> Bag { get; } =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public T? Get<T>(string key) =>
                Bag.TryGetValue(key, out var v) ? (v is T t ? t : default) : default;

            public void Set(string key, object value) => Bag[key] = value;

            public object Ok(object data) => new { ok = true, data };
            public object Error(string code, string? detail = null) => new { ok = false, error = code, detail };

            public T? Arg<T>(string key, T? @default = default)
            {
                if (Args == null || !Args.TryGetValue(key, out var v) || v is null)
                    return @default;

                if (v is JsonElement je)
                    v = JsonElementToObject(je);

                try
                {
                    if (v is T tt) return tt;

                    var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

                    if (t == typeof(string)) return (T)(object?)v.ToString();
                    if (t == typeof(bool)) return (T)(object)ToBool(v);
                    if (t == typeof(int)) return (T)(object)Convert.ToInt32(v);
                    if (t == typeof(long)) return (T)(object)Convert.ToInt64(v);
                    if (t == typeof(double)) return (T)(object)Convert.ToDouble(v);
                    if (t == typeof(decimal)) return (T)(object)Convert.ToDecimal(v);
                    if (t == typeof(DateTime)) return (T)(object)Convert.ToDateTime(v);

                    var json = JsonSerializer.Serialize(v);
                    return JsonSerializer.Deserialize<T>(json);
                }
                catch
                {
                    return @default;
                }
            }

            public object? Arg(string key) => Arg<object?>(key, null);

            public string? Header(string name, string? @default = null)
            {
                if (Headers != null && Headers.TryGetValue(name, out var v) && v is not null)
                    return v.ToString();
                return @default;
            }

            private static object? JsonElementToObject(JsonElement el)
            {
                return el.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.TryGetInt64(out var l) ? l : (el.TryGetDouble(out var d) ? d : el.GetRawText()),
                    JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Deserialize<object>(el.GetRawText()),
                    _ => el.GetRawText()
                };
            }

            private static bool ToBool(object v)
            {
                if (v is bool b) return b;
                var s = (v?.ToString() ?? "").Trim().ToLowerInvariant();
                return s is "1" or "true" or "t" or "yes" or "y" or "on";
            }
        }
    }
}

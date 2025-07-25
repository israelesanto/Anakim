using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;
using AnakimOrchestrator.Infrastructure;
using AnakimSuite.AnakimAccessProvider;

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
        private static readonly string BaseScriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts");

        // Cache de scripts compilados
        private static readonly ConcurrentDictionary<string, (Script<object> Script, DateTime LastWrite)> _scriptCache = new();

        public ScriptExecutorService(
            IConfiguration configuration,
            IAnakimAccessProvider dbProvider,
            ILogger<ScriptExecutorService> logger)
        {
            _configuration = configuration;
            _dbProvider = dbProvider;
            _logger = logger;
        }

        public async Task<object?> RunScriptAsync(string scriptName, IDictionary<string, object> args, int? languageOverride = null)
        {
            InjectBuiltInVariables(args);

            var languageCode = languageOverride ?? _configuration.GetValue<int>("ScriptSettings:DefaultLanguage");

            return (ScriptLanguage)languageCode switch
            {
                ScriptLanguage.CSharp => await RunCSharpScriptAsync(scriptName, args),
                _ => throw new NotSupportedException($"Código de linguagem '{languageCode}' não é suportado.")
            };
        }

        private async Task<object?> RunCSharpScriptAsync(string scriptName, IDictionary<string, object> args)
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
                Db = _dbProvider
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
                var resultTask = (Task<object>)runMethod.Invoke(scriptObject, new object[] { globals });
                return await resultTask;
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

        public class Globals
        {
            public IDictionary<string, object> Args { get; set; } = new Dictionary<string, object>();
            public IAnakimAccessProvider Db { get; set; }
        }
    }
}

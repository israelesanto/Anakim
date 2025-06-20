using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Reflection;
using Anakim.Infrastructure;

namespace Anakim.Services
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
        private static readonly string BaseScriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts");

        // Cache de scripts compilados
        private static readonly ConcurrentDictionary<string, (Script<object> Script, DateTime LastWrite)> _scriptCache = new();

        public ScriptExecutorService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<object?> RunScriptAsync(string scriptName, IDictionary<string, object> args, int? languageOverride = null)
        {
            InjectBuiltInVariables(args);

            var languageCode = languageOverride ?? _configuration.GetValue<int>("ScriptSettings:DefaultLanguage");

            return (ScriptLanguage)languageCode switch
            {
                ScriptLanguage.CSharp => await RunCSharpScriptAsync(scriptName, args),
                // ScriptLanguage.Python => await RunPythonScriptAsync(scriptName, args),
                // ScriptLanguage.JavaScript => await RunJavaScriptScriptAsync(scriptName, args),
                _ => throw new NotSupportedException($"Código de linguagem '{languageCode}' não é suportado.")
            };
        }

        private async Task<object?> RunCSharpScriptAsync(string scriptName, IDictionary<string, object> args)
        {
            var scriptPath = Path.Combine(BaseScriptPath, "CSharp", $"{scriptName}.csx");

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Script '{scriptName}' não encontrado em CSharp.");

            var lastWriteTime = File.GetLastWriteTimeUtc(scriptPath);

            if (!_scriptCache.TryGetValue(scriptPath, out var cached) || cached.LastWrite < lastWriteTime)
            {
                var code = await File.ReadAllTextAsync(scriptPath);

                var options = ScriptOptions.Default
                    .AddImports("System", "System.Collections.Generic")
                    .AddReferences(AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)));

                var compiledScript = CSharpScript.Create(code, options, typeof(Globals));

                var diagnostics = compiledScript.GetCompilation().GetDiagnostics();
                if (diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                    throw new Exception("Erro ao compilar script: " + string.Join(", ", diagnostics));

                _scriptCache[scriptPath] = (compiledScript, lastWriteTime);
            }

            var (script, _) = _scriptCache[scriptPath];
            var globals = new Globals { Args = args };

            var scriptState = await script.RunAsync(globals);

            var scriptObject = scriptState?.ReturnValue;

            if (scriptObject == null)
                throw new Exception("Script executado, mas não retornou uma instância.");

            var runMethod = scriptObject.GetType().GetMethod(
                "Run",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            if (runMethod == null)
                throw new Exception("Método 'Run' não encontrado na instância retornada.");

            try
            {
                return runMethod.Invoke(scriptObject, new object[] { args });
            }
            catch (TargetInvocationException ex)
            {
                Logger.LogError($"Erro no script: {ex.InnerException?.Message}");
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
        }
    }
}

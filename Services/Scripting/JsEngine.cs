using System;
using Jint;
using static AnakimOrchestrator.Services.ScriptExecutorService;

namespace AnakimOrchestrator.Services.Scripting
{
    public sealed class JsEngine : IScriptEngine
    {
        public string Name => "js";
        public string[] FileExtensions => new[] { ".js" };

        public Task<object?> ExecuteAsync(string code, Globals g, CancellationToken ct)
        {
            var engine = new Engine(o => o
                .Strict()
                .LimitMemory(GetMaxMemory(g))
                .TimeoutInterval(GetTimeout(g))
            );

            // Exponha somente o necessário
            engine.SetValue("g", g);

            if (g.LogInfo != null)
                engine.SetValue("logInfo", new Action<string>(g.LogInfo));

            if (g.LogWarn != null)
                engine.SetValue("logWarn", new Action<string>(g.LogWarn));

            if (g.LogError != null)
                engine.SetValue("logError", new Action<string>(m => g.LogError!(new Exception(m), m)));

            // Executa o código e invoca a função handle(g)
            engine.Execute(code);

            // ✅ Correção: em Jint 3.x, use engine.Invoke("handle", args...)
            var result = engine.Invoke("handle", g).ToObject();

            return Task.FromResult(result);
        }

        static int GetMaxMemory(Globals g)
        {
            var cfg = g.Config?["Scripts:MaxMemoryBytes"];
            return int.TryParse(cfg, out var v) ? v : 16_000_000;
        }

        static TimeSpan GetTimeout(Globals g)
        {
            var cfg = g.Config?["Scripts:TimeoutSeconds"];
            return int.TryParse(cfg, out var v) ? TimeSpan.FromSeconds(v) : TimeSpan.FromSeconds(2);
        }
    }
}

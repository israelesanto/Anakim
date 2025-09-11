using static AnakimOrchestrator.Services.ScriptExecutorService;

namespace AnakimOrchestrator.Services.Scripting;

public interface IScriptEngine
{
    string Name { get; }
    string[] FileExtensions { get; }
    Task<object?> ExecuteAsync(string code, Globals g, CancellationToken ct);
}

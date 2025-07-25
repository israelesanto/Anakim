using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AnakimOrchestrator.Services;
using System.Text.Json;

namespace AnakimOrchestrator.Controllers
{
    [ApiController]
    [Route("scripts")]
    public class ScriptsController : ControllerBase
    {
        private readonly ILogger<ScriptsController> _logger;
        private readonly ScriptExecutorService _scriptExecutor;

        public ScriptsController(ILogger<ScriptsController> logger, ScriptExecutorService scriptExecutor)
        {
            _logger = logger;
            _scriptExecutor = scriptExecutor;
        }

        [HttpPost("{scriptName}")]
        public async Task<IActionResult> RunScript(string scriptName, [FromBody] JsonElement args)
        {
            _logger.LogInformation("Recebida requisição para script: {Script}", scriptName);

            var argsDict = JsonElementToDictionary(args);
            var result = await _scriptExecutor.RunScriptAsync(scriptName, argsDict);

            return Ok(result);
        }

        private static IDictionary<string, object?> JsonElementToDictionary(JsonElement element)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var property in element.EnumerateObject())
            {
                dict[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Object => JsonElementToDictionary(property.Value),
                    JsonValueKind.Null => null,
                    _ => property.Value.ToString()
                };
            }
            return dict;
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AnakimOrchestrator.Services;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

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
        public async Task<IActionResult> RunScript(string scriptName)
        {
            _logger.LogInformation("Recebida requisição para script: {Script}", scriptName);

            // Lê o corpo como texto para tolerar corpo vazio
            string body;
            using (var reader = new StreamReader(Request.Body))
                body = await reader.ReadToEndAsync();

            // Se vier vazio ou só espaços, tratamos como {} (sem args)
            IDictionary<string, object> argsDict;
            if (string.IsNullOrWhiteSpace(body))
            {
                argsDict = new Dictionary<string, object>();
            }
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return BadRequest(new { error = "O corpo JSON deve ser um objeto (ex.: { \"x\": 1 })." });

                    argsDict = JsonElementToDictionary(doc.RootElement);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "JSON inválido para script {Script}", scriptName);
                    return BadRequest(new { error = "JSON inválido no corpo da requisição." });
                }
            }

            var result = await _scriptExecutor.RunScriptAsync(scriptName, argsDict);
            return Ok(result);
        }

        private static IDictionary<string, object> JsonElementToDictionary(JsonElement element)
        {
            var dict = new Dictionary<string, object>();
            foreach (var property in element.EnumerateObject())
            {
                dict[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Object => JsonElementToDictionary(property.Value),
                    JsonValueKind.Array => JsonArrayToList(property.Value),
                    JsonValueKind.Null => null!,
                    _ => property.Value.ToString()
                };
            }
            return dict;
        }

        private static List<object> JsonArrayToList(JsonElement arrayEl)
        {
            var list = new List<object>();
            foreach (var item in arrayEl.EnumerateArray())
            {
                list.Add(item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString()!,
                    JsonValueKind.Number => item.TryGetInt64(out var l) ? l : item.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Object => JsonElementToDictionary(item),
                    JsonValueKind.Array => JsonArrayToList(item),
                    JsonValueKind.Null => null!,
                    _ => item.ToString()
                });
            }
            return list;
        }
    }
}

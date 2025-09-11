using System;
using System.Collections.Generic;
using System.Text.Json;

public class ScriptHandler
{
    public object Run(IDictionary<string, object> args)
    {
        string cep = args.ContainsKey("cep") ? args["cep"]?.ToString() ?? "00000-000" : "00000-000";
        //double peso = ((JsonElement)args["peso"]).GetDouble();
		double peso = 15;

        string instanceName = args.ContainsKey("__instance") ? args["__instance"]?.ToString() ?? "AH-Unknown" : "AH-Unknown";
        Guid uid = Guid.NewGuid();

        double freteBase = 12.50;
        double adicionalPeso = peso * 1.8;

        return new
        {
            codigo = "0000123",
            descricao = "GRAN PLUS CHOICE CARNE CAES ADULTOS",
            peso,
            chave = uid.ToString(),
        };
    }
}

return new ScriptHandler();

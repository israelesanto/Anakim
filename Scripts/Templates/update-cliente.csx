// update-cliente.csx
// ----------------------
// Atualiza dados de um cliente existente.
// Padrões:
// - Verifica se os parâmetros obrigatórios foram passados.
// - Usa parâmetros nomeados para evitar SQL Injection.
// - Retorna status e quantidade de registros afetados.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ERPUSASolutions.DataAccessProvider;

public class ScriptHandler
{
    public async Task<object> Run(dynamic globals)
    {
        var db = (IDataAccessProvider)globals.Db;
        var args = (IDictionary<string, object>)globals.Args;

        var parameters = new Dictionary<string, object>
        {
            ["@id"] = Convert.ToInt32(args["id"]),
            ["@nome"] = args["nome"]?.ToString()?.Trim('"')
        };

        string sql = "UPDATE cliente SET nome = @nome WHERE id = @id";
        int result = await db.ExecuteNonQueryAsync(sql, parameters);

        return new { success = result > 0, updated = result };
    }
}

return new ScriptHandler();

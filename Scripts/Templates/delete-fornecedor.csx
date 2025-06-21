// delete-fornecedor.csx
// --------------------------
// Remove fornecedor com base no CNPJ informado.
// Convenções:
// - Nome do script usa hífen (-) para URLs legíveis.
// - Executa DELETE com parâmetro nomeado para segurança.

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
            ["@cnpj"] = args["cnpj"]?.ToString()?.Trim('"')
        };

        string sql = "DELETE FROM fornecedor WHERE cnpj = @cnpj";
        int result = await db.ExecuteNonQueryAsync(sql, parameters);

        return new { success = result > 0, deleted = result };
    }
}

return new ScriptHandler();

// insert-cliente.csx
// ----------------------
// Este script insere um novo cliente na base de dados.
// Padrões adotados:
// - Nome do script e URL utilizam hífen (-) para seguir convenções REST modernas.
// - A entrada é validada via IDictionary<string, object> com tratamento de tipos.
// - Usa IDataAccessProvider para comunicação com o banco (injeção via DI).
// - Retorna JSON padrão com campo "success" e número de registros inseridos.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using ERPUSASolutions.DataAccessProvider;

public class ScriptHandler
{
    public async Task<object> Run(dynamic globals)
    {
        var db = (IDataAccessProvider)globals.Db;
        var args = (IDictionary<string, object>)globals.Args;

        var parameters = new Dictionary<string, object>
        {
            ["@nome"] = args["nome"]?.ToString()?.Trim('"'),
            ["@cpf"] = args["cpf"]?.ToString()?.Trim('"')
        };

        string sql = "INSERT INTO cliente (nome, cpf) VALUES (@nome, @cpf)";
        int result = await db.ExecuteNonQueryAsync(sql, parameters);

        return new { success = result > 0, inserted = result };
    }
}

return new ScriptHandler();

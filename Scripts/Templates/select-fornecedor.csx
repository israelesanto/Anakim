// select-fornecedor.csx
// --------------------------
// Este script lista todos os fornecedores.
// Padrões adotados:
// - Nome com hífen para URL amigável e padrão REST.
// - Uso de IDataAccessProvider para leitura.
// - Mapeamento manual do DataReader para objetos anônimos.
// - Retorno de array com os dados.

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using ERPUSASolutions.DataAccessProvider;

public class ScriptHandler
{
    public async Task<object> Run(dynamic globals)
    {
        var db = (IDataAccessProvider)globals.Db;
        var result = new List<object>();

        var reader = await db.ExecuteReaderAsync("SELECT * FROM fornecedor", null);
        while (reader.Read())
        {
            result.Add(new
            {
                id = reader["id_fornecedor"],
                nome = reader["razao_social"],
                cnpj = reader["cnpj"]
            });
        }

        return new { success = true, data = result };
    }
}

return new ScriptHandler();

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnakimSuite.AnakimAccessProvider;

public class ScriptHandler
{
    public async Task<object> Run(dynamic globals)
    {
        var db = (IAnakimAccessProvider)globals.Db;
        var args = (IDictionary<string, object>)globals.Args;

		var parameters = new Dictionary<string, object>
		{
			["razao_social"] = args["razao_social"].ToString().Trim('"'),
			["nome_fantasia"] = args["nome_fantasia"].ToString().Trim('"'),
			["cnpj"] = args["cnpj"].ToString().Trim('"'),
			["email"] = args["email"].ToString().Trim('"'),
			["telefone"] = args["telefone"].ToString().Trim('"'),
			["data_cadastro"] = DateTime.UtcNow,
			["inscricao_estadual"] = args["inscricao_estadual"]?.ToString().Trim('"')
		};

        var sql = @"
            INSERT INTO fornecedor (
                razao_social, nome_fantasia, cnpj, email, telefone, data_cadastro
            ) VALUES (
                @razao_social, @nome_fantasia, @cnpj, @email, @telefone, @data_cadastro
            )
            RETURNING id_fornecedor
        ";

        var result = await db.ExecuteScalarAsync(sql, parameters);
        return new { id_inserido = result };
    }
}

// ⚠️ ATENÇÃO: NÃO EXECUTA o método Run aqui!
return new ScriptHandler();

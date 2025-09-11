using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using ERPUSASolutions.DataAccessProvider;
using Anakim.Services;

public class ScriptHandler
{
    public async Task<object> Run(ScriptExecutorService.Globals globals)
    {
        var db = globals.Db;
        var result = new List<object>();

        var reader = await db.ExecuteReaderAsync("SELECT * FROM fornecedor", null);

        while (reader.Read())
        {
            result.Add(new
            {
                id_fornecedor = reader["id_fornecedor"],
                razao_social = reader["razao_social"],
                nome_fantasia = reader["nome_fantasia"],
                cnpj = reader["cnpj"],
                inscricao_estadual = reader["inscricao_estadual"],
                inscricao_municipal = reader["inscricao_municipal"],
                logradouro = reader["logradouro"],
                numero = reader["numero"],
                complemento = reader["complemento"],
                bairro = reader["bairro"],
                cidade = reader["cidade"],
                uf = reader["uf"],
                cep = reader["cep"],
                telefone = reader["telefone"],
                email = reader["email"],
                contato_principal = reader["contato_principal"],
                status = reader["status"],
                data_cadastro = reader["data_cadastro"],
                observacoes = reader["observacoes"],
                tipo_fornecedor = reader["tipo_fornecedor"],
                site = reader["site"],
                redes_sociais = reader["redes_sociais"]
            });
        }

        reader.Close();
        return result;
    }
}

return new ScriptHandler();

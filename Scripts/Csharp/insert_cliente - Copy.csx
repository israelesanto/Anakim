using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using AnakimSuite.AnakimAccessProvider;


public class ScriptHandler
{
    public async Task<object> Run(dynamic globals)
    {
        var db = (IAnakimAccessProvider)globals.Db;
        var args = (IDictionary<string, object>)globals.Args;
		string instanceName = args.ContainsKey("__instance") ? args["__instance"]?.ToString() ?? "AH-Unknown" : "AH-Unknown";

		var parameters = new Dictionary<string, object>
		{
			["tipo_cliente"]       = args["tipo_cliente"] is JsonElement el1 ? el1.GetInt16() : Convert.ToInt16(args["tipo_cliente"]),
			["razao_social"]       = args["razao_social"]?.ToString().Trim('"'),
			["nome_fantasia"]      = args["nome_fantasia"]?.ToString().Trim('"'),
			["nome"]               = args["nome"]?.ToString().Trim('"'),
			["cnpj"]               = args["cnpj"]?.ToString().Trim('"'),
			["cpf"]                = args["cpf"]?.ToString().Trim('"'),
			["ie"]                 = args["ie"]?.ToString().Trim('"'),
			["isuf"]               = args["isuf"]?.ToString().Trim('"'),
			["im"]                 = args["im"]?.ToString().Trim('"'),
			["logradouro"]         = args["logradouro"]?.ToString().Trim('"'),
			["numero"]             = args["numero"]?.ToString().Trim('"'),
			["complemento"]        = args["complemento"]?.ToString().Trim('"'),
			["bairro"]             = args["bairro"]?.ToString().Trim('"'),
			["municipio"]          = args["municipio"]?.ToString().Trim('"'),
			["codigo_municipio"]   = args["codigo_municipio"] is JsonElement el2 ? el2.GetInt32() : Convert.ToInt32(args["codigo_municipio"]),
			["uf"]                 = args["uf"]?.ToString().Trim('"'),
			["cep"]                = args["cep"]?.ToString().Trim('"'),
			["pais"]               = args.ContainsKey("pais") ? args["pais"]?.ToString().Trim('"') : "Brasil",
			["codigo_pais"]        = args.ContainsKey("codigo_pais") 
									  ? (args["codigo_pais"] is JsonElement el3 ? el3.GetInt32() : Convert.ToInt32(args["codigo_pais"])) 
									  : 1058,
			["telefone"]           = args["telefone"]?.ToString().Trim('"'),
			["celular"]            = args["celular"]?.ToString().Trim('"'),
			["email"]              = args["email"]?.ToString().Trim('"'),
			["contato"]            = args["contato"]?.ToString().Trim('"'),
			["status"]             = args.ContainsKey("status") 
									  ? (args["status"] is JsonElement el4 ? el4.GetInt16() : Convert.ToInt16(args["status"])) 
									  : 1,
			["data_cadastro"]      = DateTime.UtcNow,
			["data_nascimento"]    = args.ContainsKey("data_nascimento") 
									  ? (args["data_nascimento"] is JsonElement el5 ? el5.GetDateTime() : Convert.ToDateTime(args["data_nascimento"])) 
									  : (object)DBNull.Value,
			["observacoes"]        = args["observacoes"]?.ToString().Trim('"'),
			["site"]               = args["site"]?.ToString().Trim('"'),
			["redes_sociais"]      = args["redes_sociais"]?.ToString().Trim('"')
		};


        var sql = @"
				INSERT INTO cliente (
					tipo_cliente,
					razao_social,
					nome_fantasia,
					nome,
					cnpj,
					cpf,
					ie,
					isuf,
					im,
					logradouro,
					numero,
					complemento,
					bairro,
					municipio,
					codigo_municipio,
					uf,
					cep,
					pais,
					codigo_pais,
					telefone,
					celular,
					email,
					contato,
					status,
					data_cadastro,
					data_nascimento,
					observacoes,
					site,
					redes_sociais
				) VALUES (
					@tipo_cliente,
					@razao_social,
					@nome_fantasia,
					@nome,
					@cnpj,
					@cpf,
					@ie,
					@isuf,
					@im,
					@logradouro,
					@numero,
					@complemento,
					@bairro,
					@municipio,
					@codigo_municipio,
					@uf,
					@cep,
					@pais,
					@codigo_pais,
					@telefone,
					@celular,
					@email,
					@contato,
					@status,
					@data_cadastro,
					@data_nascimento,
					@observacoes,
					@site,
					@redes_sociais
				)
				RETURNING id_cliente;

        ";

        var result = await db.ExecuteScalarAsync(sql, parameters);
        return new { 
			id_inserido = result,
			ah = instanceName
		};
    }
}

// ⚠️ ATENÇÃO: NÃO EXECUTA o método Run aqui!
return new ScriptHandler();

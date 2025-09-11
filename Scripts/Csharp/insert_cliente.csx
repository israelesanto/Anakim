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
            ["tipo_cliente"] = args.ContainsKey("tipo_cliente") && short.TryParse(args["tipo_cliente"]?.ToString(), out var tipoCliente) ? tipoCliente : (object)DBNull.Value,
            ["razao_social"] = args.ContainsKey("razao_social") ? args["razao_social"]?.ToString().Trim('"') : null,
            ["nome_fantasia"] = args.ContainsKey("nome_fantasia") ? args["nome_fantasia"]?.ToString().Trim('"') : null,
            ["nome"] = args.ContainsKey("nome") ? args["nome"]?.ToString().Trim('"') : null,
            ["cnpj"] = args.ContainsKey("cnpj") ? args["cnpj"]?.ToString().Trim('"') : null,
            ["cpf"] = args.ContainsKey("cpf") ? args["cpf"]?.ToString().Trim('"') : null,
            ["ie"] = args.ContainsKey("ie") ? args["ie"]?.ToString().Trim('"') : null,
            ["isuf"] = args.ContainsKey("isuf") ? args["isuf"]?.ToString().Trim('"') : null,
            ["im"] = args.ContainsKey("im") ? args["im"]?.ToString().Trim('"') : null,
            ["logradouro"] = args.ContainsKey("logradouro") ? args["logradouro"]?.ToString().Trim('"') : null,
            ["numero"] = args.ContainsKey("numero") ? args["numero"]?.ToString().Trim('"') : null,
            ["complemento"] = args.ContainsKey("complemento") ? args["complemento"]?.ToString().Trim('"') : null,
            ["bairro"] = args.ContainsKey("bairro") ? args["bairro"]?.ToString().Trim('"') : null,
            ["municipio"] = args.ContainsKey("municipio") ? args["municipio"]?.ToString().Trim('"') : null,
            ["codigo_municipio"] = args.ContainsKey("codigo_municipio") && int.TryParse(args["codigo_municipio"]?.ToString(), out var codMunicipio) ? codMunicipio : (object)DBNull.Value,			
            ["uf"] = args.ContainsKey("uf") ? args["uf"]?.ToString().Trim('"') : null,
            ["cep"] = args.ContainsKey("cep") ? args["cep"]?.ToString().Trim('"') : null,
            ["pais"] = args.ContainsKey("pais") ? args["pais"]?.ToString().Trim('"') : null,
            ["codigo_pais"] = args.ContainsKey("codigo_pais") && int.TryParse(args["codigo_pais"]?.ToString(), out var codPais) ? codPais : (object)DBNull.Value,
            ["telefone"] = args.ContainsKey("telefone") ? args["telefone"]?.ToString().Trim('"') : null,
            ["celular"] = args.ContainsKey("celular") ? args["celular"]?.ToString().Trim('"') : null,
            ["email"] = args.ContainsKey("email") ? args["email"]?.ToString().Trim('"') : null,
            ["contato"] = args.ContainsKey("contato") ? args["contato"]?.ToString().Trim('"') : null,
            ["status"] = args.ContainsKey("status") && short.TryParse(args["status"]?.ToString(), out var status) ? status  : (object)1, // Valor padrão: 1 (Ativo)
            ["data_cadastro"] = DateTime.Now,
            ["data_nascimento"] = args.ContainsKey("data_nascimento")
                ? (args["data_nascimento"] is JsonElement el5 ? el5.GetDateTime() : Convert.ToDateTime(args["data_nascimento"]))
                : (object)DBNull.Value,
            ["observacoes"] = args.ContainsKey("observacoes") ? args["observacoes"]?.ToString().Trim('"') : null,
            ["site"] = args.ContainsKey("site") ? args["site"]?.ToString().Trim('"') : null,
            ["redes_sociais"] = args.ContainsKey("redes_sociais") ? args["redes_sociais"]?.ToString().Trim('"') : null,
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
        return new
        {
            id_inserido = result,
            ah = instanceName
        };
    }
}

// ⚠️ ATENÇÃO: NÃO EXECUTA o método Run aqui!
return new ScriptHandler();

using System.Collections.Generic;

public object Run(IDictionary<string, object> args)
{
    string cep = args["cep"].ToString();
    double peso = Convert.ToDouble(args["peso"]);

    double freteBase = 12.50;
    double adicionalPeso = peso * 1.8;

    return new
    {
        cep,
        peso,
        valorTotal = freteBase + adicionalPeso
    };
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FiscalBr.Common.Sped;
using FiscalBr.EFDContribuicoes;
using FiscalBr.EFDFiscal;

namespace FiscalBr.ReadSped;

internal static class Program
{
    private enum SpedFileKind
    {
        EfdContribuicoes,
        EfdFiscal
    }

    private sealed record BlockSummary(string BlockId, int LineCount, int DistinctRegisters);

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (args.Any(IsHelpOption))
        {
            PrintUsage();
            return 0;
        }

        string? filePath = null;
        SpedFileKind? forcedKind = null;

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];

            if (current.StartsWith("--tipo", StringComparison.OrdinalIgnoreCase))
            {
                var value = ExtractOptionValue(args, ref index, "--tipo");
                forcedKind = ParseKind(value);
                continue;
            }

            if (current.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Opção desconhecida: {current}");
                return 1;
            }

            if (filePath != null)
            {
                Console.Error.WriteLine("Informe apenas um caminho de arquivo por vez.");
                return 1;
            }

            filePath = current;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            Console.Error.WriteLine("Informe o caminho do arquivo SPED a ser lido.");
            PrintUsage();
            return 1;
        }

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"Arquivo não encontrado: {filePath}");
            return 1;
        }

        try
        {
            var detectedKind = forcedKind ?? DetectKind(filePath);
            if (detectedKind is null)
            {
                Console.Error.WriteLine("Não foi possível identificar o tipo do arquivo. Informe explicitamente usando --tipo=efd-contrib ou --tipo=efd-fiscal.");
                return 1;
            }

            switch (detectedKind.Value)
            {
                case SpedFileKind.EfdContribuicoes:
                    ProcessEfdContribuicoes(filePath);
                    break;
                case SpedFileKind.EfdFiscal:
                    ProcessEfdFiscal(filePath);
                    break;
                default:
                    throw new InvalidOperationException($"Tipo de arquivo SPED não suportado: {detectedKind.Value}.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Erro ao ler o arquivo: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static void ProcessEfdContribuicoes(string filePath)
    {
        var arquivo = new ArquivoEFDContribuicoes();
        arquivo.Ler(filePath);

        var header = arquivo.Bloco0?.Reg0000;
        WriteSummary(
            arquivo,
            "EFD Contribuições",
            header?.Nome,
            header?.Cnpj,
            header?.DtIni,
            header?.DtFin,
            header?.Uf);
    }

    private static void ProcessEfdFiscal(string filePath)
    {
        var arquivo = new ArquivoEFDFiscal();
        arquivo.Ler(filePath);

        var header = arquivo.Bloco0?.Reg0000;
        WriteSummary(
            arquivo,
            "EFD Fiscal",
            header?.Nome,
            header?.Cnpj,
            header?.DtIni,
            header?.DtFin,
            header?.Uf);
    }

    private static void WriteSummary(
        ArquivoSped arquivo,
        string tipoDescricao,
        string? nome,
        string? cnpj,
        DateTime? dataInicial,
        DateTime? dataFinal,
        string? uf)
    {
        Console.WriteLine($"Tipo de arquivo: {tipoDescricao}");

        if (!string.IsNullOrWhiteSpace(nome))
        {
            Console.WriteLine($"Empresa: {nome}");
        }

        if (!string.IsNullOrWhiteSpace(cnpj))
        {
            Console.WriteLine($"CNPJ: {cnpj}");
        }

        if (!string.IsNullOrWhiteSpace(uf))
        {
            Console.WriteLine($"UF: {uf}");
        }

        var periodoFormatado = FormatPeriod(dataInicial, dataFinal);
        if (!string.IsNullOrWhiteSpace(periodoFormatado))
        {
            Console.WriteLine($"Período: {periodoFormatado}");
        }

        Console.WriteLine($"Versão do leiaute (informada no arquivo): {arquivo.ObterVersaoLayout():D3}");
        Console.WriteLine($"Total de linhas lidas: {arquivo.Linhas.Count}");

        if (arquivo.Erros.Any())
        {
            Console.WriteLine("Erros encontrados durante a leitura:");
            foreach (var erro in arquivo.Erros)
            {
                Console.WriteLine($"  - {erro}");
            }
        }
        else
        {
            Console.WriteLine("Nenhum erro foi identificado durante a leitura.");
        }

        Console.WriteLine("Resumo por bloco (considerando os registros lidos):");
        foreach (var resumo in BuildBlockSummaries(arquivo.Linhas))
        {
            Console.WriteLine($"  Bloco {resumo.BlockId,-2} -> {resumo.LineCount,4} linhas ({resumo.DistinctRegisters,2} registros distintos)");
        }
    }

    private static IEnumerable<BlockSummary> BuildBlockSummaries(IEnumerable<string> linhas)
    {
        return linhas
            .Select(ExtractRegistro)
            .Where(registro => !string.IsNullOrEmpty(registro))
            .GroupBy(registro => registro![0])
            .Select(grupo => new BlockSummary(
                grupo.Key.ToString(CultureInfo.InvariantCulture),
                grupo.Count(),
                grupo.Select(registro => registro!).Distinct().Count()))
            .OrderBy(summary => summary.BlockId, StringComparer.Ordinal);
    }

    private static string? ExtractRegistro(string linha)
    {
        if (string.IsNullOrWhiteSpace(linha))
        {
            return null;
        }

        var trimmed = linha.Trim();
        if (!trimmed.StartsWith('|'))
        {
            return null;
        }

        var secondPipeIndex = trimmed.IndexOf('|', 1);
        if (secondPipeIndex < 0)
        {
            return null;
        }

        return trimmed.Substring(1, secondPipeIndex - 1);
    }

    private static string? FormatPeriod(DateTime? dataInicial, DateTime? dataFinal)
    {
        var inicio = FormatDate(dataInicial);
        var fim = FormatDate(dataFinal);

        if (inicio == null && fim == null)
        {
            return null;
        }

        return inicio == null
            ? $"até {fim}"
            : fim == null
                ? $"a partir de {inicio}"
                : $"{inicio} - {fim}";
    }

    private static string? FormatDate(DateTime? data)
    {
        if (!data.HasValue || data.Value == default)
        {
            return null;
        }

        return data.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private static SpedFileKind? DetectKind(string filePath)
    {
        var hasContribBlocks = false;
        var hasFiscalBlocks = false;

        foreach (var line in File.ReadLines(filePath))
        {
            var registro = ExtractRegistro(line);
            if (string.IsNullOrEmpty(registro))
            {
                continue;
            }

            switch (registro![0])
            {
                case 'F':
                case 'I':
                case 'M':
                case 'P':
                    hasContribBlocks = true;
                    break;
                case 'B':
                case 'E':
                case 'G':
                case 'H':
                case 'K':
                    hasFiscalBlocks = true;
                    break;
            }

            if (hasContribBlocks && hasFiscalBlocks)
            {
                break;
            }
        }

        if (hasContribBlocks && !hasFiscalBlocks)
        {
            return SpedFileKind.EfdContribuicoes;
        }

        if (hasFiscalBlocks && !hasContribBlocks)
        {
            return SpedFileKind.EfdFiscal;
        }

        return null;
    }

    private static string ExtractOptionValue(string[] args, ref int index, string optionName)
    {
        var current = args[index];
        var separatorIndex = current.IndexOf('=');
        if (separatorIndex > 0)
        {
            return current.Substring(separatorIndex + 1);
        }

        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"A opção {optionName} requer um valor.");
        }

        index++;
        return args[index];
    }

    private static SpedFileKind ParseKind(string rawValue)
    {
        var normalized = rawValue.Trim().ToLowerInvariant();
        return normalized switch
        {
            "efd-contrib" or "efdcontrib" or "contrib" or "contribuicoes" or "efd-contribuicoes" => SpedFileKind.EfdContribuicoes,
            "efd-fiscal" or "efdfiscal" or "fiscal" => SpedFileKind.EfdFiscal,
            _ => throw new ArgumentException($"Valor inválido para --tipo: {rawValue}")
        };
    }

    private static bool IsHelpOption(string arg)
    {
        return string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-?", StringComparison.Ordinal);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Uso: FiscalBr.ReadSped <caminho-do-arquivo> [--tipo=efd-contrib|efd-fiscal]");
        Console.WriteLine();
        Console.WriteLine("O parâmetro --tipo é opcional quando o programa consegue identificar o layout pelos blocos presentes.");
        Console.WriteLine("Use --help para exibir esta mensagem novamente.");
    }
}

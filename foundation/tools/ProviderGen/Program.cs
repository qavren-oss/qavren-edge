using System.Text.Json.Nodes;

namespace Qavren.Edge.ProviderGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: providergen manifest --out <path> [--check]");
            Console.Error.WriteLine("       providergen generate --manifest <path> --template <path> --out <path> [--check]");
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "manifest" => Manifest(args),
                "generate" => Generate(args),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static string Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length)
        {
            throw new InvalidOperationException($"missing required argument {name}");
        }

        return args[i + 1];
    }

    private static int Manifest(string[] args)
    {
        var outPath = Arg(args, "--out");
        var text = ManifestBuilder.Serialize(ManifestBuilder.Build());
        return Emit(args, outPath, text);
    }

    private static int Generate(string[] args)
    {
        var manifestPath = Arg(args, "--manifest");
        var templatePath = Arg(args, "--template");
        var outPath = Arg(args, "--out");

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var template = File.ReadAllText(templatePath);
        var text = ProviderRenderer.Render(template, manifest);
        return Emit(args, outPath, text);
    }

    private static int Emit(string[] args, string outPath, string text)
    {
        var check = args.Contains("--check");
        if (check)
        {
            if (!File.Exists(outPath))
            {
                Console.Error.WriteLine($"DRIFT: {outPath} does not exist.");
                return 1;
            }

            var existing = File.ReadAllText(outPath);
            if (!string.Equals(existing.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"), StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"DRIFT: {outPath} differs from freshly generated output.");
                return 1;
            }

            Console.WriteLine($"OK: {outPath} is up to date.");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, text);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }
}

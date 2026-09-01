namespace GitSrv.Api.Git;

public sealed record LanguageSlice(string Language, long Bytes, double Percent);

/// <summary>Crude extension → language mapping for the repo language bar. Not linguist-accurate.</summary>
public static class Languages
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#", [".fs"] = "F#", [".vb"] = "Visual Basic",
        [".js"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript", [".jsx"] = "JavaScript",
        [".ts"] = "TypeScript", [".tsx"] = "TypeScript",
        [".py"] = "Python", [".rb"] = "Ruby", [".go"] = "Go", [".rs"] = "Rust",
        [".java"] = "Java", [".kt"] = "Kotlin", [".kts"] = "Kotlin", [".scala"] = "Scala",
        [".c"] = "C", [".h"] = "C", [".cpp"] = "C++", [".cc"] = "C++", [".hpp"] = "C++",
        [".php"] = "PHP", [".swift"] = "Swift", [".m"] = "Objective-C",
        [".html"] = "HTML", [".htm"] = "HTML", [".css"] = "CSS", [".scss"] = "SCSS", [".sass"] = "Sass",
        [".sh"] = "Shell", [".bash"] = "Shell", [".zsh"] = "Shell", [".ps1"] = "PowerShell",
        [".sql"] = "SQL", [".r"] = "R", [".lua"] = "Lua", [".dart"] = "Dart", [".ex"] = "Elixir", [".exs"] = "Elixir",
        [".json"] = "JSON", [".yaml"] = "YAML", [".yml"] = "YAML", [".toml"] = "TOML", [".xml"] = "XML",
        [".md"] = "Markdown", [".markdown"] = "Markdown", [".tex"] = "TeX",
        [".dockerfile"] = "Dockerfile", [".vue"] = "Vue", [".svelte"] = "Svelte",
    };

    public static string? Detect(string fileName)
    {
        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "Dockerfile";
        if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase)) return "Makefile";
        var ext = Path.GetExtension(fileName);
        return ByExtension.GetValueOrDefault(ext);
    }

    public static IReadOnlyList<LanguageSlice> Summarise(IEnumerable<(string Name, long Bytes)> files)
    {
        var totals = new Dictionary<string, long>();
        foreach (var (name, bytes) in files)
        {
            var lang = Detect(name);
            if (lang is null) continue;
            totals[lang] = totals.GetValueOrDefault(lang) + bytes;
        }
        var grand = totals.Values.Sum();
        if (grand == 0) return [];
        return totals.OrderByDescending(kv => kv.Value)
            .Select(kv => new LanguageSlice(kv.Key, kv.Value, Math.Round(kv.Value * 100.0 / grand, 1)))
            .Take(10)
            .ToList();
    }
}

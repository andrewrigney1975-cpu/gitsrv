using YamlDotNet.Serialization;

namespace GitSrv.Api.Actions;

/// <summary>
/// Parses a GitHub-Actions-flavoured workflow YAML into <see cref="Workflow"/>. Deliberately a
/// subset: <c>name</c>, <c>on</c> (push / pull_request with optional <c>branches</c>),
/// <c>jobs.&lt;id&gt;</c> with <c>runs-on</c>, <c>container</c>, <c>needs</c>, <c>env</c>,
/// <c>strategy.matrix</c> (scalar axes), and <c>steps</c> (<c>name</c>, <c>run</c>, <c>uses</c>,
/// <c>with</c>, <c>env</c>, <c>if</c>, <c>shell</c>, <c>working-directory</c>,
/// <c>continue-on-error</c>). Unknown keys are ignored.
/// </summary>
public static class WorkflowParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static Workflow Parse(string yamlText, string path)
    {
        var root = Yaml.Deserialize<Dictionary<object, object>>(yamlText)
                   ?? throw new FormatException("Empty workflow.");

        var name = Str(root, "name") ?? Path.GetFileNameWithoutExtension(path);
        var on = ParseTriggers(root.GetValueOrDefault("on"));

        var jobs = new List<WorkflowJob>();
        if (root.GetValueOrDefault("jobs") is Dictionary<object, object> jobsMap)
        {
            foreach (var (k, v) in jobsMap)
            {
                if (v is not Dictionary<object, object> jm) continue;
                var id = k.ToString()!;
                jobs.Add(new WorkflowJob(
                    id,
                    Str(jm, "name") ?? id,
                    NormaliseRunsOn(Str(jm, "runs-on") ?? "ubuntu-latest"),
                    ParseContainer(jm.GetValueOrDefault("container")),
                    ParseMatrix(jm.GetValueOrDefault("strategy")),
                    ParseStringList(jm.GetValueOrDefault("needs")),
                    ParseSteps(jm.GetValueOrDefault("steps")),
                    ParseStringMap(jm.GetValueOrDefault("env")),
                    Str(jm, "if")));
            }
        }
        if (jobs.Count == 0) throw new FormatException("Workflow has no jobs.");

        return new Workflow(name, path, on, jobs);
    }

    private static WorkflowTriggers ParseTriggers(object? on)
    {
        switch (on)
        {
            case string s:
                return new WorkflowTriggers(s == "push", null, s == "pull_request", null);
            case List<object> list:
                var names = list.Select(x => x.ToString()).ToHashSet();
                return new WorkflowTriggers(names.Contains("push"), null, names.Contains("pull_request"), null);
            case Dictionary<object, object> map:
                var push = map.ContainsKey("push");
                var pr = map.ContainsKey("pull_request");
                string[]? pb = push && map["push"] is Dictionary<object, object> pm ? ParseStringList(pm.GetValueOrDefault("branches")).ToArray() : null;
                string[]? rb = pr && map["pull_request"] is Dictionary<object, object> rm ? ParseStringList(rm.GetValueOrDefault("branches")).ToArray() : null;
                return new WorkflowTriggers(push, pb is { Length: > 0 } ? pb : null, pr, rb is { Length: > 0 } ? rb : null);
            default:
                return new WorkflowTriggers(true, null, false, null);
        }
    }

    private static string? ParseContainer(object? c) => c switch
    {
        string s => s,
        Dictionary<object, object> m => Str(m, "image"),
        _ => null,
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseMatrix(object? strategy)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        if (strategy is Dictionary<object, object> s && s.GetValueOrDefault("matrix") is Dictionary<object, object> m)
        {
            foreach (var (k, v) in m)
            {
                var key = k.ToString()!;
                if (key is "include" or "exclude") continue; // not supported yet
                if (v is List<object> vals)
                    result[key] = vals.Select(x => x?.ToString() ?? "").ToList();
            }
        }
        return result;
    }

    private static IReadOnlyList<WorkflowStep> ParseSteps(object? steps)
    {
        var result = new List<WorkflowStep>();
        if (steps is not List<object> list) return result;
        foreach (var item in list)
        {
            if (item is not Dictionary<object, object> sm) continue;
            result.Add(new WorkflowStep(
                Str(sm, "name"),
                Str(sm, "run"),
                Str(sm, "uses"),
                ParseStringMap(sm.GetValueOrDefault("with")),
                ParseStringMap(sm.GetValueOrDefault("env")),
                Str(sm, "if"),
                Str(sm, "shell"),
                Str(sm, "working-directory"),
                bool.TryParse(Str(sm, "continue-on-error"), out var c) && c));
        }
        return result;
    }

    private static IReadOnlyList<string> ParseStringList(object? v) => v switch
    {
        string s => [s],
        List<object> l => l.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList(),
        _ => [],
    };

    private static IReadOnlyDictionary<string, string> ParseStringMap(object? v)
    {
        var d = new Dictionary<string, string>();
        if (v is Dictionary<object, object> m)
            foreach (var (k, val) in m) d[k.ToString()!] = val?.ToString() ?? "";
        return d;
    }

    private static string? Str(Dictionary<object, object> map, string key)
        => map.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static string NormaliseRunsOn(string r) => r.Trim();
}

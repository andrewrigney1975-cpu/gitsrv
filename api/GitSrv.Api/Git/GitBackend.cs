using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace GitSrv.Api.Git;

/// <summary>
/// Runs <c>git upload-pack</c> / <c>git receive-pack</c> in Smart-HTTP stateless-RPC mode and
/// streams the result straight to the response. No buffering of pack data in memory.
/// </summary>
public sealed class GitBackend(ILogger<GitBackend> logger)
{
    public static bool IsValidService(string? service) =>
        service is "git-upload-pack" or "git-receive-pack";

    /// <summary>Writes the ref advertisement for <c>GET /info/refs?service=…</c>.</summary>
    public async Task AdvertiseAsync(HttpContext ctx, string service, string repoDir, string? gitProtocol, CancellationToken ct)
    {
        ctx.Response.ContentType = $"application/x-{service}-advertisement";
        ctx.Response.Headers.CacheControl = "no-cache";

        // Smart-HTTP requires the service header (in pkt-line framing) before git's own advertisement.
        var header = $"# service={service}\n";
        await ctx.Response.WriteAsync($"{header.Length + 4:x4}{header}", ct);
        await ctx.Response.WriteAsync("0000", ct);

        var verb = service["git-".Length..];
        await RunAsync(verb, ["--stateless-rpc", "--advertise-refs", repoDir], gitProtocol,
            input: null, ctx.Response.Body, ct);
    }

    /// <summary>Handles <c>POST /git-upload-pack</c> or <c>POST /git-receive-pack</c>.</summary>
    public async Task RpcAsync(HttpContext ctx, string service, string repoDir, string? gitProtocol, CancellationToken ct,
        IReadOnlyDictionary<string, string>? hookEnv = null)
    {
        ctx.Response.ContentType = $"application/x-{service}-result";
        ctx.Response.Headers.CacheControl = "no-cache";

        Stream input = ctx.Request.Body;
        if (string.Equals(ctx.Request.Headers.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
            input = new GZipStream(input, CompressionMode.Decompress);

        var verb = service["git-".Length..];
        await RunAsync(verb, ["--stateless-rpc", repoDir], gitProtocol, input, ctx.Response.Body, ct, hookEnv);
    }

    private async Task RunAsync(string verb, string[] args, string? gitProtocol, Stream? input, Stream output, CancellationToken ct,
        IReadOnlyDictionary<string, string>? hookEnv = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(verb);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Protocol v2 when the client asked for it.
        if (!string.IsNullOrEmpty(gitProtocol))
            psi.Environment["GIT_PROTOCOL"] = gitProtocol;
        // Passed through to the pre-receive / post-receive hooks.
        if (hookEnv is not null)
            foreach (var (k, v) in hookEnv) psi.Environment[k] = v;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");

        var pumpErr = Task.Run(async () =>
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            if (!string.IsNullOrWhiteSpace(err))
                logger.LogWarning("git {Verb}: {Stderr}", verb, err.Trim());
        }, ct);

        if (input is not null)
        {
            await input.CopyToAsync(proc.StandardInput.BaseStream, ct);
            proc.StandardInput.Close();
        }

        await proc.StandardOutput.BaseStream.CopyToAsync(output, ct);
        await proc.WaitForExitAsync(ct);
        await pumpErr;

        if (proc.ExitCode != 0)
            logger.LogWarning("git {Verb} exited {Code}", verb, proc.ExitCode);
    }
}

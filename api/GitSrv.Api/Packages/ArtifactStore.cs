namespace GitSrv.Api.Packages;

/// <summary>
/// Content storage for package files. One implementation today (<see cref="LocalArtifactStore"/>);
/// an S3-compatible driver slots in behind the same interface without touching the registry code.
/// </summary>
public interface IArtifactStore
{
    Task<string> PutAsync(Stream content, CancellationToken ct);            // returns a storage key
    Task<string> PutTempAsync(Stream content, CancellationToken ct);        // upload session buffer
    Task AppendTempAsync(string key, Stream content, CancellationToken ct);
    Task<long> TempSizeAsync(string key);
    Task<string> PromoteTempAsync(string tempKey, CancellationToken ct);    // temp -> permanent
    Task DeleteTempAsync(string key);
    Task<Stream?> OpenAsync(string key);
    Task<long> SizeAsync(string key);
    Task DeleteAsync(string key);
}

public sealed class LocalArtifactStore : IArtifactStore
{
    private readonly string _root;
    private readonly string _temp;

    public LocalArtifactStore(IConfiguration config)
    {
        _root = config["GitSrv:ArtifactRoot"]
            ?? Path.Combine(config["GitSrv:RepositoryRoot"] ?? "/var/lib/gitsrv/repositories", "_packages");
        _temp = Path.Combine(_root, "_uploads");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    private string Path2(string key) => Path.Combine(_root, key);

    public async Task<string> PutAsync(Stream content, CancellationToken ct)
    {
        var key = $"{Guid.NewGuid():N}";
        var sub = Path.Combine(key[..2], key);
        Directory.CreateDirectory(Path.Combine(_root, key[..2]));
        await using var fs = File.Create(Path2(sub));
        await content.CopyToAsync(fs, ct);
        return sub;
    }

    public async Task<string> PutTempAsync(Stream content, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var fs = File.Create(Path.Combine(_temp, key));
        await content.CopyToAsync(fs, ct);
        return key;
    }

    public async Task AppendTempAsync(string key, Stream content, CancellationToken ct)
    {
        await using var fs = new FileStream(Path.Combine(_temp, key), FileMode.Append, FileAccess.Write);
        await content.CopyToAsync(fs, ct);
    }

    public Task<long> TempSizeAsync(string key)
    {
        var f = new FileInfo(Path.Combine(_temp, key));
        return Task.FromResult(f.Exists ? f.Length : 0);
    }

    public Task<string> PromoteTempAsync(string tempKey, CancellationToken ct)
    {
        var src = Path.Combine(_temp, tempKey);
        var key = Guid.NewGuid().ToString("N");
        var sub = Path.Combine(key[..2], key);
        Directory.CreateDirectory(Path.Combine(_root, key[..2]));
        File.Move(src, Path2(sub), overwrite: true);
        return Task.FromResult(sub);
    }

    public Task DeleteTempAsync(string key)
    {
        var f = Path.Combine(_temp, key);
        if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenAsync(string key)
    {
        var f = Path2(key);
        return Task.FromResult<Stream?>(File.Exists(f) ? File.OpenRead(f) : null);
    }

    public Task<long> SizeAsync(string key)
    {
        var f = new FileInfo(Path2(key));
        return Task.FromResult(f.Exists ? f.Length : 0);
    }

    public Task DeleteAsync(string key)
    {
        var f = Path2(key);
        if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }
}

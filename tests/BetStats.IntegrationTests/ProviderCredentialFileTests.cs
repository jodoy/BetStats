using BetStats.Infrastructure.Pipeline;

namespace BetStats.IntegrationTests;

[Trait("Category", "Security")]
public sealed class ProviderCredentialFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "betstats-secret-test-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(root, "api-football.json");
    private void Write(string content) { Directory.CreateDirectory(root); File.WriteAllText(PathName, content); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact] public void Local_key_is_development_only_and_environment_takes_precedence()
    {
        Write("{\"ApiKey\":\"synthetic-local-key\"}");
        Assert.Equal("synthetic-local-key", ProviderCredentialFile.Resolve(true, null, PathName));
        Assert.Null(ProviderCredentialFile.Resolve(false, null, PathName));
        Assert.Equal("synthetic-env-key", ProviderCredentialFile.Resolve(true, "synthetic-env-key", PathName));
    }
    [Theory] [InlineData("{")] [InlineData("{\"ApiKey\":\"\"}")]
    [InlineData("{\"ApiKey\":\"first\",\"ApiKey\":\"second\"}")]
    [InlineData("{\"ApiKey\":\"synthetic-key\",\"Enabled\":true}")]
    [InlineData("{\"ApiKey\":\"invalid key\"}")] [InlineData("[]")]
    public void Invalid_files_fail_closed_without_exposing_content(string content)
    { Write(content); Assert.Null(ProviderCredentialFile.Resolve(true, null, PathName)); }
    [Fact] public void Missing_large_and_repository_files_are_rejected()
    {
        Assert.Null(ProviderCredentialFile.Resolve(true, null, PathName));
        Write(new string('x', 4097)); Assert.Null(ProviderCredentialFile.Resolve(true, null, PathName));
        Write("{\"ApiKey\":\"synthetic-key\"}"); File.WriteAllText(Path.Combine(root, "BetStats.slnx"), "");
        Assert.Null(ProviderCredentialFile.Resolve(true, null, PathName));
    }
    [Fact] public void Rotation_is_read_without_restarting_or_changing_evidence()
    {
        Write("{\"ApiKey\":\"synthetic-first\"}"); Assert.Equal("synthetic-first", ProviderCredentialFile.Resolve(true, null, PathName));
        Write("{\"ApiKey\":\"synthetic-second\"}"); Assert.Equal("synthetic-second", ProviderCredentialFile.Resolve(true, null, PathName));
    }
}

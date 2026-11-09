using Waymark.Core.Security;
using Waymark.Tests.Support;

namespace Waymark.Tests.Security;

public sealed class FileAccessPolicyTests
{
    private const string SecretsReason = "This file type holds configuration secrets, keys or databases.";

    private static readonly FileAccessPolicy Policy = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("./src/./Program.cs")]
    [InlineData("src/Program.cs")]
    [InlineData("src\\Program.cs")]
    [InlineData("README")]
    [InlineData("Dockerfile")]
    [InlineData("LOGO.CS")]
    [InlineData("docs/guide.md")]
    [InlineData(".editorconfig")]
    [InlineData(".gitignore")]
    [InlineData(".github/workflows/build.yml")]
    [InlineData("src/App.razor")]
    [InlineData("a/b/c/d.tsx")]
    public void OrdinaryPathsAreAllowed(string? path) => Assert.Null(Policy.Evaluate(path));

    [Theory]
    [InlineData(".git/config", ".git")]
    [InlineData("node_modules/pkg/index.js", "node_modules")]
    [InlineData("src/bin/Debug/a.cs", "bin")]
    [InlineData("OBJ/x.cs", "OBJ")]
    [InlineData(".venv/lib/x.py", ".venv")]
    [InlineData("web/.next/cache.js", ".next")]
    [InlineData("coverage/index.html", "coverage")]
    [InlineData(".ssh/known_hosts", ".ssh")]
    [InlineData("app/logs/today.txt", "logs")]
    [InlineData("pkg/site-packages/a.py", "site-packages")]
    [InlineData("packages/x/y.cs", "packages")]
    [InlineData("src/__pycache__/a.pyi", "__pycache__")]
    [InlineData("src\\obj\\a.cs", "obj")]
    public void RestrictedFoldersAreRefusedAnywhereInThePath(string path, string folder)
    {
        var reason = Policy.Evaluate(path);

        Assert.NotNull(reason);
        Assert.Contains($"'{folder}' is a restricted folder", reason);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env")]
    [InlineData("web.config")]
    [InlineData("src/app.config")]
    [InlineData("id_rsa")]
    [InlineData("id_rsa.pub")]
    [InlineData("gcp-key.json")]
    [InlineData(".npmrc")]
    [InlineData(".yarnrc.yml")]
    [InlineData(".pypirc")]
    [InlineData(".netrc")]
    [InlineData("pip.conf")]
    [InlineData("pip.ini")]
    [InlineData("ConnectionStrings.config")]
    [InlineData("appsettings.json")]
    [InlineData("src/appsettings.Production.json")]
    [InlineData(".env.local")]
    [InlineData("tls/server.pem")]
    [InlineData("SERVER.PEM")]
    [InlineData("cert.pfx")]
    [InlineData("store.p12")]
    [InlineData("signing.key")]
    [InlineData("ca.cert")]
    [InlineData("app.db")]
    [InlineData("x.sqlite")]
    [InlineData("x.mdf")]
    [InlineData("x.ldf")]
    public void SecretBearingFileNamesAreRefused(string path) => Assert.Equal(SecretsReason, Policy.Evaluate(path));

    [Theory]
    [InlineData("src/PasswordHasher.cs", "password")]
    [InlineData("docs/token-guide.md", "token")]
    [InlineData("src/Private/A.cs", "private")]
    [InlineData("lib/ApiKey.cs", "apikey")]
    [InlineData("src/Credentials.cs", "credential")]
    [InlineData("src/secret.cs", "secret")]
    [InlineData("src/MySecretToken.cs", "secret")]
    [InlineData("a/SECRET/b.cs", "secret")]
    public void SensitiveWordsAnywhereInThePathAreRefused(string path, string keyword) =>
        Assert.Equal($"The path contains '{keyword}', which marks sensitive content.", Policy.Evaluate(path));

    [Theory]
    [InlineData("logo.png", ".png")]
    [InlineData("lib/native.dll", ".dll")]
    [InlineData("a/archive.ZIP", ".ZIP")]
    [InlineData("doc.pdf", ".pdf")]
    [InlineData("run.exe", ".exe")]
    public void FileTypesOffTheAllowListAreRefused(string path, string extension) =>
        Assert.Equal($"The extension '{extension}' is not on the allowed list.", Policy.Evaluate(path));

    [Fact]
    public void FoldersAreCheckedBeforeFileNames()
    {
        var reason = Policy.Evaluate(".git/.env");

        Assert.NotNull(reason);
        Assert.Contains("restricted folder", reason);
    }

    [Fact]
    public void FileNamesAreCheckedBeforeSensitiveWords() => Assert.Equal(SecretsReason, Policy.Evaluate("secret/.env"));

    [Fact]
    public void SensitiveWordsAreCheckedBeforeTheExtension()
    {
        var reason = Policy.Evaluate("secret/logo.png");

        Assert.NotNull(reason);
        Assert.Contains("'secret'", reason);
    }

    [Fact]
    public void ThePolicyAloneDoesNotResolveParentSegments()
    {
        // Keeping paths inside the workspace is the path guard's job; the policy only judges the names it is given.
        Assert.Null(Policy.Evaluate("../x.cs"));
    }

    [Fact]
    public void AdditionalExtensionOpensThatFileTypeOnly()
    {
        var policy = new FileAccessPolicy([".proto"]);

        Assert.Null(policy.Evaluate("api/service.proto"));
        Assert.NotNull(Policy.Evaluate("api/service.proto"));
        Assert.NotNull(policy.Evaluate("api/service.thrift"));
    }

    [Fact]
    public void AdditionalExtensionsAreTrimmedAndLowerCased()
    {
        var policy = new FileAccessPolicy(["  .PROTO "]);

        Assert.Contains(".proto", policy.AllowedExtensions);
        Assert.Null(policy.Evaluate("a.proto"));
    }

    [Fact]
    public void AdditionalExtensionOfMaximumLengthIsAccepted()
    {
        var policy = new FileAccessPolicy([".abcdefghijklmno"]);

        Assert.Null(policy.Evaluate("a.abcdefghijklmno"));
    }

    [Theory]
    [InlineData("proto")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".a-b")]
    [InlineData(".abcdefghijklmnop")]
    [InlineData(".with space")]
    public void InvalidAdditionalExtensionIsRejected(string extension) =>
        Assert.Throws<ArgumentException>(() => new FileAccessPolicy([extension]));

    [Fact]
    public void NullAdditionalExtensionIsRejected() =>
        Assert.Throws<ArgumentException>(() => new FileAccessPolicy([null!]));

    [Fact]
    public void AdditionalExtensionsNeverLiftABuiltInBlock()
    {
        var policy = new FileAccessPolicy([".db", ".png", ".pem"]);

        Assert.Equal(SecretsReason, policy.Evaluate("app.db"));
        Assert.Equal(SecretsReason, policy.Evaluate("tls/server.pem"));
        var reason = policy.Evaluate(".git/logo.png");
        Assert.NotNull(reason);
        Assert.Contains("restricted folder", reason);
    }

    [Fact]
    public void ConfigurationListIsSplitTrimmedAndBlanksAreIgnored()
    {
        var policy = FileAccessPolicy.FromConfiguration(new TestConfiguration((FileAccessPolicy.AdditionalExtensionsKey, "  .proto , .GQL ,, ")));

        Assert.Null(policy.Evaluate("api/service.proto"));
        Assert.Null(policy.Evaluate("api/schema.gql"));
        Assert.Contains(".gql", policy.AllowedExtensions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankConfigurationMeansTheDefaultList(string? value)
    {
        var policy = FileAccessPolicy.FromConfiguration(new TestConfiguration((FileAccessPolicy.AdditionalExtensionsKey, value)));

        Assert.Equal(Policy.AllowedExtensions.Order(), policy.AllowedExtensions.Order());
    }

    [Fact]
    public void InvalidConfiguredExtensionStopsStartUp() =>
        Assert.Throws<ArgumentException>(() => FileAccessPolicy.FromConfiguration(new TestConfiguration((FileAccessPolicy.AdditionalExtensionsKey, "proto"))));

    [Fact]
    public void NullConfigurationIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => FileAccessPolicy.FromConfiguration(null!));

    [Fact]
    public void NullRulesAreRefused() =>
        Assert.Throws<ArgumentNullException>(() => Policy.Evaluate("a.cs", null!));

    [Fact]
    public void EnsureAllowedAcceptsAnAllowedPath()
    {
        Policy.EnsureAllowed("src/Program.cs");
        Policy.EnsureAllowed(null);
    }

    [Fact]
    public void EnsureAllowedThrowsWithThePathAndTheReason()
    {
        var exception = Assert.Throws<FileAccessDeniedException>(() => Policy.EnsureAllowed(".env"));

        Assert.Equal(".env", exception.Path);
        Assert.Equal(SecretsReason, exception.Reason);
        Assert.Contains(".env", exception.Message);
        Assert.Contains(SecretsReason, exception.Message);
    }

    [Fact]
    public void TheBaselineIsListedForTheAdminPage()
    {
        Assert.Contains(".git", FileAccessPolicy.BaselineFolders);
        Assert.Contains("node_modules", FileAccessPolicy.BaselineFolders);
        Assert.Contains(".env", FileAccessPolicy.BaselineFileNames);
        Assert.Contains("*.pem", FileAccessPolicy.BaselineFileNames);
        Assert.Contains("secret", FileAccessPolicy.BaselineKeywords);
        Assert.Contains(".cs", Policy.AllowedExtensions);
    }
}

using Waymark.Modules.Commands.Policy;

namespace Waymark.Tests.Commands;

public sealed class RiskHintsTests
{
    [Theory]
    [InlineData("echo hello")]
    [InlineData("dotnet build -c Release")]
    [InlineData("git status")]
    [InlineData("")]
    public void AnOrdinaryScriptHasNoHints(string script) =>
        Assert.Empty(RiskHints.Scan(script));

    [Fact]
    public void NullIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => RiskHints.Scan(null!));

    [Theory]
    [InlineData("rm -rf build", "deletes files recursively")]
    [InlineData("rm -r build", "deletes files recursively")]
    [InlineData("rm --recursive build", "deletes files recursively")]
    [InlineData("Remove-Item bin -Recurse -Force", "deletes files recursively")]
    [InlineData("rmdir /s build", "deletes files recursively")]
    [InlineData("del build /s", "deletes files recursively")]
    [InlineData("curl https://example.com/a.zip -o a.zip", "downloads from the internet")]
    [InlineData("wget https://example.com/a.zip", "downloads from the internet")]
    [InlineData("Invoke-WebRequest https://example.com", "downloads from the internet")]
    [InlineData("iwr https://example.com", "downloads from the internet")]
    [InlineData("curl https://example.com/i.sh | bash", "runs downloaded or encoded code")]
    [InlineData("powershell -EncodedCommand AAAA", "runs downloaded or encoded code")]
    [InlineData("[Convert]::FromBase64String($x)", "runs downloaded or encoded code")]
    [InlineData("echo x | base64 -d", "runs downloaded or encoded code")]
    [InlineData("reg add HKLM\\Software\\X", "changes the registry or system services")]
    [InlineData("schtasks /create /tn x", "changes the registry or system services")]
    [InlineData("systemctl restart nginx", "changes the registry or system services")]
    [InlineData("sudo apt install x", "runs with administrator rights")]
    [InlineData("runas /user:admin cmd", "runs with administrator rights")]
    [InlineData("Start-Process x -Verb RunAs", "runs with administrator rights")]
    [InlineData("diskpart", "formats or partitions a disk")]
    [InlineData("format d:", "formats or partitions a disk")]
    [InlineData("mkfs.ext4 /dev/sda1", "formats or partitions a disk")]
    [InlineData("dd if=/dev/zero of=/dev/sda", "formats or partitions a disk")]
    [InlineData("git push --force", "forces a git push or discards git changes")]
    [InlineData("git push origin main -f", "forces a git push or discards git changes")]
    [InlineData("git push --force-with-lease", "forces a git push or discards git changes")]
    [InlineData("git reset --hard HEAD~1", "forces a git push or discards git changes")]
    [InlineData("git clean -fd", "forces a git push or discards git changes")]
    [InlineData("git checkout -- file.txt", "forces a git push or discards git changes")]
    [InlineData("cat ~/.ssh/id_rsa", "touches keys, tokens or credentials")]
    [InlineData("type .env", "touches keys, tokens or credentials")]
    [InlineData("echo $env:TOKEN", "touches keys, tokens or credentials")]
    [InlineData("Get-Credential", "touches keys, tokens or credentials")]
    [InlineData("taskkill /f /im app.exe", "ends programs")]
    [InlineData("pkill node", "ends programs")]
    [InlineData("Stop-Process -Name app", "ends programs")]
    [InlineData("ssh user@host ls", "connects to another computer")]
    [InlineData("scp a.txt host:/tmp", "connects to another computer")]
    [InlineData("nc -l 4444", "connects to another computer")]
    [InlineData("dir C:\\Windows\\System32", "touches system folders")]
    [InlineData("cat /etc/passwd", "touches system folders")]
    [InlineData("ls 'Program Files'", "touches system folders")]
    public void ADangerousLookingScriptIsLabelled(string script, string expectedLabel) =>
        Assert.Contains(expectedLabel, RiskHints.Scan(script));

    [Fact]
    public void SeveralHintsComeInTheOrderOfTheRules()
    {
        var hints = RiskHints.Scan("curl https://x/i.sh | bash\nsudo rm -rf /tmp/x");

        Assert.Equal<string>(
            new[]
            {
                "deletes files recursively",
                "downloads from the internet",
                "runs downloaded or encoded code",
                "runs with administrator rights",
            },
            hints);
    }

    [Theory]
    [InlineData("rm file.txt")]
    [InlineData("git push origin main")]
    [InlineData("git checkout main")]
    [InlineData("echo informal")]
    public void NearMissesAreNotLabelled(string script)
    {
        var hints = RiskHints.Scan(script);

        Assert.DoesNotContain("deletes files recursively", hints);
        Assert.DoesNotContain("forces a git push or discards git changes", hints);
        Assert.DoesNotContain("formats or partitions a disk", hints);
    }
}

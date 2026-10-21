using System.Text.RegularExpressions;

namespace Waymark.Modules.Commands.Policy;

/// <summary>
/// Plain-language warnings about what a shell script seems to do, shown to the owner above the script. They are hints for reading faster,
/// not a filter: the owner always decides, and a script that matches nothing is still shown in full.
/// </summary>
public static partial class RiskHints
{
    private static readonly (string Label, Regex Pattern)[] Rules =
    [
        ("deletes files recursively", DeletePattern()),
        ("downloads from the internet", DownloadPattern()),
        ("runs downloaded or encoded code", RemoteCodePattern()),
        ("changes the registry or system services", SystemPattern()),
        ("runs with administrator rights", ElevatePattern()),
        ("formats or partitions a disk", DiskPattern()),
        ("forces a git push or discards git changes", GitDestructivePattern()),
        ("touches keys, tokens or credentials", SecretPattern()),
        ("ends programs", KillPattern()),
        ("connects to another computer", RemotePattern()),
        ("touches system folders", SystemFolderPattern()),
    ];

    [GeneratedRegex(@"\brm\s+(-[a-z]*r[a-z]*|--recursive)\b|Remove-Item[^\n]*-Recurse|\b(rd|rmdir)\s+/s\b|\bdel\s+[^\n]*/s\b", RegexOptions.IgnoreCase, 200)]
    private static partial Regex DeletePattern();

    [GeneratedRegex(@"\b(curl|wget|Invoke-WebRequest|iwr|Invoke-RestMethod|irm|bitsadmin|certutil\s+-urlcache)\b", RegexOptions.IgnoreCase, 200)]
    private static partial Regex DownloadPattern();

    [GeneratedRegex(@"\|\s*(iex|Invoke-Expression|bash|sh|zsh|python3?|node)\b|-EncodedCommand|\bFromBase64String\b|base64\s+(-d|--decode)|\biex\b", RegexOptions.IgnoreCase, 200)]
    private static partial Regex RemoteCodePattern();

    [GeneratedRegex(@"\breg\s+(add|delete|import)\b|\bHKLM\b|\bHKCU\b|Set-ItemProperty|\bsc(\.exe)?\s+(config|create|delete)\b|\bschtasks\b|New-Service|\bnetsh\b|\bsystemctl\b|\bcrontab\b", RegexOptions.IgnoreCase, 200)]
    private static partial Regex SystemPattern();

    [GeneratedRegex(@"\bsudo\b|\brunas\b|-Verb\s+RunAs|\bsu\s+-", RegexOptions.IgnoreCase, 200)]
    private static partial Regex ElevatePattern();

    [GeneratedRegex(@"\bdiskpart\b|\bmkfs\b|\bformat\s+[a-z]:|Format-Volume|\bdd\s+[^\n]*of=", RegexOptions.IgnoreCase, 200)]
    private static partial Regex DiskPattern();

    [GeneratedRegex(@"git\s+push\s+[^\n]*(--force|-f\b|--force-with-lease)|git\s+reset\s+--hard|git\s+clean\s+[^\n]*-[a-z]*f|git\s+checkout\s+--\s", RegexOptions.IgnoreCase, 200)]
    private static partial Regex GitDestructivePattern();

    [GeneratedRegex(@"\.ssh\b|id_rsa|id_ed25519|\.aws\b|\.kube\b|\.npmrc|\.git-credentials|\.env\b|Get-Credential|ConvertTo-SecureString|\bpassword\b|\btoken\b|\bsecret\b|credential", RegexOptions.IgnoreCase, 200)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b(kill|pkill|killall|taskkill|Stop-Process)\b", RegexOptions.IgnoreCase, 200)]
    private static partial Regex KillPattern();

    [GeneratedRegex(@"\b(ssh|scp|sftp|nc|ncat|telnet|ftp|Enter-PSSession|Invoke-Command)\b", RegexOptions.IgnoreCase, 200)]
    private static partial Regex RemotePattern();

    [GeneratedRegex(@"[a-z]:\\Windows\b|System32|(^|[\s'""=])/(etc|usr|bin|sbin|boot|var|opt|root)(/|\b)|Program Files", RegexOptions.IgnoreCase | RegexOptions.Multiline, 200)]
    private static partial Regex SystemFolderPattern();

    public static IReadOnlyList<string> Scan(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var hints = new List<string>();
        foreach (var (label, pattern) in Rules)
        {
            try
            {
                if (pattern.IsMatch(script))
                {
                    hints.Add(label);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                hints.Add(label + " (could not be checked in time)");
            }
        }

        return hints;
    }
}

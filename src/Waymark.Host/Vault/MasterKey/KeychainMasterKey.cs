using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Waymark.Host.Vault.MasterKey;

/// <summary>
/// The master key, kept as a generic password in the user's login Keychain, which macOS encrypts with the login password: a copied data
/// folder or backup cannot open the vault without this Mac account.
/// <para>
/// The item is created and read through Apple's own <c>/usr/bin/security</c> tool, and that tool is the item's one trusted application
/// (<c>-T</c>). Keychain access is tied to the code signature of the program that asks; Waymark is rebuilt on every update and would be
/// asked for permission again each time, at sign-in, with nobody there to answer. The signature of <c>security</c> never changes.
/// </para>
/// <para>
/// The new key goes in through standard input (<c>security -i</c>), never the command line, which other programs can read. The output
/// that holds the key is never logged. A failure is remembered for a minute so a locked Keychain does not stall every request.
/// </para>
/// </summary>
public sealed class KeychainMasterKey(ILogger<KeychainMasterKey> logger) : IMasterKeySource
{
    internal const string SecurityTool = "/usr/bin/security";
    internal const string Service = "waymark-data-protection";
    internal const string Account = "waymark";

    // security(1): errSecItemNotFound is reported as exit code 44.
    private const int ItemNotFound = 44;

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private byte[]? _key;
    private (DateTimeOffset At, string Message)? _lastFailure;

    public ReadOnlyMemory<byte> Get()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                return _key;
            }

            if (_lastFailure is { } failure && DateTimeOffset.UtcNow - failure.At < FailureMemory)
            {
                throw new CryptographicException(failure.Message);
            }

            try
            {
                _key = ReadOrCreate();
                _lastFailure = null;
                return _key;
            }
            catch (CryptographicException ex)
            {
                _lastFailure = (DateTimeOffset.UtcNow, ex.Message);
                logger.LogError("{Message}", ex.Message);
                throw;
            }
        }
    }

    private byte[] ReadOrCreate()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new CryptographicException("The Keychain master key exists on macOS only.");
        }

        var read = Run(["find-generic-password", "-s", Service, "-a", Account, "-w"], input: null);
        if (read.ExitCode == 0)
        {
            return Parse(read.Output);
        }

        if (read.ExitCode != ItemNotFound)
        {
            throw Unavailable($"reading it failed (exit {read.ExitCode}: {read.Error})");
        }

        var key = RandomNumberGenerator.GetBytes(MasterKeyCipher.KeyBytes);
        var command = $"add-generic-password -s {Service} -a {Account} -T {SecurityTool} -w {Convert.ToHexString(key)}\n";
        CryptographicOperations.ZeroMemory(key);
        var added = Run(["-i"], command);

        // The read-back is the proof: the item exists, and reading it needs no permission prompt. It also settles a race with a second
        // copy of Waymark, whose key then wins for both.
        var check = Run(["find-generic-password", "-s", Service, "-a", Account, "-w"], input: null);
        if (check.ExitCode != 0)
        {
            throw Unavailable($"creating it failed (exit {added.ExitCode}: {added.Error}; read-back exit {check.ExitCode}: {check.Error})");
        }

        logger.LogInformation("Created the Waymark master key in the login Keychain (service {Service}).", Service);
        return Parse(check.Output);
    }

    private static byte[] Parse(string output)
    {
        byte[] key;
        try
        {
            key = Convert.FromHexString(output.Trim());
        }
        catch (FormatException)
        {
            throw Unavailable("the Keychain item does not hold a Waymark key");
        }

        return key.Length == MasterKeyCipher.KeyBytes ? key : throw Unavailable("the Keychain item does not hold a Waymark key");
    }

    private static CryptographicException Unavailable(string detail) =>
        new($"The Waymark master key in the macOS login Keychain is not available: {detail}. Secrets and the ngrok authtoken cannot be read " +
            $"until it is. Sign in at the Mac so the login Keychain is unlocked, then restart Waymark. If the item '{Service}' was deleted, " +
            "saved secrets cannot be recovered and must be entered again.");

    private static (int ExitCode, string Output, string Error) Run(IReadOnlyList<string> arguments, string? input)
    {
        var info = new ProcessStartInfo(SecurityTool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw Unavailable($"{SecurityTool} could not be started");
        }
        catch (Win32Exception ex)
        {
            throw Unavailable($"{SecurityTool} could not be started ({ex.Message})");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                if (input is not null)
                {
                    process.StandardInput.Write(input);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The tool ended before it read its input; its exit code and error say why.
            }

            if (!process.WaitForExit(CallTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Ended on its own in the meantime.
                }

                throw Unavailable($"{SecurityTool} did not answer within {CallTimeout.TotalSeconds:0} seconds (a locked Keychain, or a prompt nobody can answer)");
            }

            process.WaitForExit();
            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult().Trim());
        }
    }
}

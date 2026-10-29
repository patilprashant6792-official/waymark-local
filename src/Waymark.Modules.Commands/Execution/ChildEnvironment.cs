using System.Collections;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// The environment of a command. Waymark's own variables are not passed on: only a short list that programs need to work is kept,
/// so keys and settings in Waymark's environment never reach a build, a test or a script. Prompts are switched off because nobody can answer them.
/// </summary>
public static class ChildEnvironment
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "CommonProgramW6432", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL",
        "PROCESSOR_REVISION", "NUMBER_OF_PROCESSORS", "LANG", "LC_ALL", "USER", "LOGNAME", "SHELL", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "SSH_AUTH_SOCK", "JAVA_HOME", "ANDROID_HOME",
    };

    private static readonly string[] AllowedPrefixes = ["NUGET_", "DOTNET_", "UV_", "PIP_"];

    // These describe how Waymark itself was started and would change the behaviour of the program being run.
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOTNET_ENVIRONMENT", "DOTNET_URLS", "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "DOTNET_WATCH", "DOTNET_USE_POLLING_FILE_WATCHER",
    };

    private static readonly Dictionary<string, string> Forced = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1",
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_EDITOR"] = "true",
        ["GIT_PAGER"] = "cat",
        ["PAGER"] = "cat",
        ["FORCE_COLOR"] = "1",
        ["CLICOLOR_FORCE"] = "1",
        ["PYTHONUNBUFFERED"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONDONTWRITEBYTECODE"] = "1",
        ["PIP_NO_INPUT"] = "1",
        ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
        ["PIP_REQUIRE_VIRTUALENV"] = "1",
        ["UV_NO_PROGRESS"] = "1",
        ["CI"] = "1",
        ["NO_UPDATE_NOTIFIER"] = "1",
        ["npm_config_update_notifier"] = "false",
        ["npm_config_fund"] = "false",
    };

    public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string>? extra = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value)
            {
                continue;
            }

            if (Denied.Contains(name))
            {
                continue;
            }

            if (Allowed.Contains(name) || AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                result[name] = value;
            }
        }

        foreach (var (key, value) in Forced)
        {
            result[key] = value;
        }

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                result[key] = value;
            }
        }

        return result;
    }
}

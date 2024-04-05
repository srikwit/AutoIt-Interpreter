using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;

using Unknown6656.AutoIt3.Runtime.Native;
using Unknown6656.AutoIt3.Localization;

namespace Unknown6656.AutoIt3.CLI;


public enum VerbosityLevel
{
    Tidy,
    Normal,
    Telemetry,
    FullDebug,
}

/// <summary>
/// An enumeration of different program execution modes.
/// </summary>
public enum ExecutionMode
{
    Normal,
    View,
    Line,
    Interactive,
}

public enum UpdaterMode
{
    Release,
    Beta,
    None,
}


public abstract record CommandLineOptions
{
    public required string LanguageCode { get; set; }
    public required UpdaterMode UpdaterMode { get; set; }
    public virtual VerbosityLevel VerbosityLevel { get; set; } = VerbosityLevel.Normal;
    public virtual bool StrictAU3Mode { get; set; } = true;

    public bool VerboseOutput => VerbosityLevel >= VerbosityLevel.FullDebug;

    public override string ToString() => $"--{CommandLineParser.OPTION_LANG} {LanguageCode} --{CommandLineParser.OPTION_CHECK_FOR_UPDATE} {UpdaterMode}";


    public sealed record ShowHelp
        : CommandLineOptions
    {
        public override string ToString() => $"{base.ToString()} --{CommandLineParser.OPTION_HELP}";
    }

    public sealed record ShowVersion
        : CommandLineOptions
    {
        public override string ToString() => $"{base.ToString()} --{CommandLineParser.OPTION_VERSION}";
    }

    public sealed record ViewMode
        : CommandLineOptions
    {
        public required string FilePath { get; set; }

        public override string ToString() => $"{base.ToString()} --{CommandLineParser.OPTION_MODE} {ExecutionMode.View} \"{FilePath}\"";
    }

    public abstract record RunMode
        : CommandLineOptions
    {
        public required bool RedirectStdErrToStdOut { get; set; }
        public override required bool StrictAU3Mode { get; set; }
        public required string[] ScriptArguments { get; set; }
        public required bool DontLoadPlugins { get; set; }
        public required bool IgnoreErrors { get; set; }

        public override string ToString()
        {
            StringBuilder sb = new(base.ToString());

            if (DontLoadPlugins)
                sb.Append($" --{CommandLineParser.OPTION_NO_PLUGINS}");

            if (StrictAU3Mode)
                sb.Append($" --{CommandLineParser.OPTION_STRICT}");

            if (IgnoreErrors)
                sb.Append($" --{CommandLineParser.OPTION_IGNORE_ERRORS}");

            if (RedirectStdErrToStdOut)
                sb.Append($" --{CommandLineParser.OPTION_REDIRECT_STDOUT}");

            return sb.ToString();
        }


        public sealed record InteractiveMode
            : RunMode
        {
            public override string ToString() => $"{base.ToString()} --{CommandLineParser.OPTION_MODE} {ExecutionMode.Interactive}";
        }

        public abstract record NonInteractiveMode
            : RunMode
        {
            public override required VerbosityLevel VerbosityLevel { get; set; }
            public required bool DisableCOMConnector { get; set; } // windows only
            public required bool DisableGUIConnector { get; set; }

            public override string ToString()
            {
                StringBuilder sb = new(base.ToString());

                if (DisableCOMConnector)
                    sb.Append($" --{CommandLineParser.OPTION_NO_COM}");

                if (DisableGUIConnector)
                    sb.Append($" --{CommandLineParser.OPTION_NO_GUI}");

                sb.Append($" --{CommandLineParser.OPTION_VERBOSITY} {VerbosityLevel}");

                return sb.ToString();
            }


            public sealed record RunLine
                : NonInteractiveMode
            {
                public required string Code { get; set; }

                public override string ToString() => $"{base.ToString()} --{CommandLineParser.OPTION_MODE} {ExecutionMode.Line} \"{Code}\"";
            }

            public sealed record RunScript
                : NonInteractiveMode
            {
                public required string FilePath { get; set; }

                public override string ToString()
                {
                    StringBuilder sb = new(base.ToString());

                    sb.Append($" --{CommandLineParser.OPTION_MODE} {ExecutionMode.Normal} \"{FilePath}\"");

                    foreach (string arg in ScriptArguments)
                        sb.Append($" \"{arg}\"");

                    return sb.ToString();
                }
            }
        }
    }
}

public sealed record CommandLineParsingError(int ArgumentIndex, string Message, bool Fatal);

/*
SUPPORTED AUTOIT3 COMMAND LINE OPTIONS:

    -m<m>, --mode <mode>        The program's execution mode. Possible values are 'normal' (n), 'interactive' (i), and 'view' (v). The default value is 'normal'. This will run the specified script. The value 'view' indicates that the interpreter shall only display a syntax highlighted version of the script. The value 'interactive' starts the interactive AutoIt shell. The value 'tidy' formats the speicified script file.
    -N, --no-plugins            Prevents the loading of interpreter plugins/extensions.
    -s, --strict                Indicates that only strict Au3-features and -syntaxes should be be supported (Extensions to the AutoIt language will be interpreted as errors).
    -e, --ignore-errors         Ignores syntax and evaluation errors during parsing (unsafe!). This can lead to undefined and non-deterministic behaviour.
    -t, --telemetry             Prints the interpreter telemetry. A verbosity level of 'v' will automatically set this flag.  NOTE: All telemetry data \e[4mstays\e[24m on this machine contrary to what this option might suggest. \e[4mNo part\e[24m of the telemetry will be uploaded to an external (web)server.
    -v, --verbosity <level>     Indicates that the interpreter should also print debug messages.

    -v0                             tidy
    -v1 / -vn                       normal
    -v2 / -vt / -t / --telemetry    telemetry
    -v3 / -vv / -v / --verbose      full debug

    -u, --check-for-update <mode> Specifies how the interpreter should check for software updates. Possible values are 'release' (default), 'beta', and 'none'. 'none' indicates that no updates shall be downloaded; 'beta' indicates that beta-releases should be included in the search for the newest update. Updates will be downloaded from the GitHub repository (\e[4mhttps://github.com/unknown6656/AutoIt3/releases\e[24m).
    -l, --lang <lang>           The CLI language code to be used by the compiler shell. The default value is 'en' for the English language.
    -?, --help                  Shows this help message.
    -V, --version               Shows the interpreter version.

                            //    [Value(0, HelpText = "The AutoIt-3 script path. This can be a local file or a web resource (HTTP/HTTPS/SMB/FTP/...).")]
    -ErrorStdOut
    -AutoIt3ExecuteScript
    -AutoIt3ExecuteLine
 */

public class CommandLineParser(LanguagePack language)
{
    internal const string OPTION_MODE = "mode";
    internal const string OPTION_NO_PLUGINS = "no-plugins";
    internal const string OPTION_NO_COM = "no-com";
    internal const string OPTION_NO_GUI = "no-gui";
    internal const string OPTION_STRICT = "strict";
    internal const string OPTION_IGNORE_ERRORS = "ignore-errors";
    internal const string OPTION_CHECK_FOR_UPDATE = "check-for-update";
    internal const string OPTION_LANG = "lang";
    internal const string OPTION_HELP = "help";
    internal const string OPTION_VERBOSE = "verbose"; // TODO: phase out this option in the future
    internal const string OPTION_TELEMETRY = "telemetry"; // TODO: phase out this option in the future
    internal const string OPTION_VERBOSITY = "verbosity";
    internal const string OPTION_VERSION = "version";
    internal const string OPTION_REDIRECT_STDOUT = "ErrorStdOut";
    internal const string OPTION_EXECUTE_SCRIPT = "AutoIt3ExecuteScript";
    internal const string OPTION_EXECUTE_LINE = "AutoIt3ExecuteLine";


    public LanguagePack Language { get; private set; } = language;

    private static string? TryMapShortOption(char short_option) => short_option switch
    {
        'm' or 'M' => OPTION_MODE,
        'N' or 'n' => OPTION_NO_PLUGINS,
        'C' or 'c' => OPTION_NO_COM,
        'G' or 'g' => OPTION_NO_GUI,
        's' or 'S' => OPTION_STRICT,
        'e' or 'E' => OPTION_IGNORE_ERRORS,
        't' or 'T' => OPTION_TELEMETRY,
        'u' or 'U' => OPTION_CHECK_FOR_UPDATE,
        'l' or 'L' => OPTION_LANG,
        '?' => OPTION_HELP,
        'v' => OPTION_VERBOSE,
        'V' => OPTION_VERSION,
        _ => null
    };

    private RawCommandLineOptions ParseRaw(string[] argv, List<CommandLineParsingError> errors)
    {
        RawCommandLineOptions raw = new();
        string? current_option = null;
        bool ignore_dash = false;
        int index = 0;


        void unknown_option(string option, bool fatal) => errors.Add(new(index, Language["command_line.error.unknown_option", option], fatal));

        void process_option(string option, string? value)
        {
            string normalized_option = option.Replace("-", "").TrimStart('/').ToLower();

            void set_option<T>(ref T? option, T value)
            {
                if (option is not null and T prev)
                    if (Equals(prev, value))
                        errors.Add(new(index, Language["command_line.error.duplicate_option", normalized_option, option], false));
                    else
                        errors.Add(new(index, Language["command_line.error.conflicting_option", normalized_option, value, option], false));
                else
                    option = value;
            }

            void set_enum_option<T>(ref T? option, string? input) where T : struct, Enum
            {
                if (Enum.TryParse<T>(input, true, out T value))
                    set_option(ref option, value);
                else
                    try
                    {
                        int intval = int.Parse(input ?? "0");
                        Type underlying = Enum.GetUnderlyingType(typeof(T));
                        object parsed = Convert.ChangeType(intval, underlying);

                        set_option(ref option, (T)Enum.ToObject(typeof(T), parsed));
                    }
                    catch
                    {
                        input ??= "";

                        T[] candidates = [..from val in Enum.GetValues<T>()
                                            let name = Enum.GetName(val)
                                            where name.StartsWith(input, StringComparison.OrdinalIgnoreCase)
                                            select val];

                        if (candidates.Length == 1)
                            set_option(ref option, candidates[0]);
                        else if (candidates.Length == 0)
                            errors.Add(new(index, Language["command_line.error.invalid_enum_value", input, normalized_option], true));
                        else
                            errors.Add(new(index, Language["command_line.error.ambiguous_enum_value", input, normalized_option, string.Join("', '", candidates)], true));
                    }
            }


            if (normalized_option is OPTION_MODE)
                set_enum_option(ref raw.execmode, value);
            else if (normalized_option is OPTION_CHECK_FOR_UPDATE)
                set_enum_option(ref raw.updatemode, value);
            else if (normalized_option is OPTION_VERBOSITY)
            {

                set_enum_option(ref raw.verbosity, value);
            }
            else if (normalized_option is OPTION_LANG)
                set_option(ref raw.langcode, value ?? raw.langcode);
            else if (normalized_option is OPTION_NO_PLUGINS or OPTION_NO_GUI or OPTION_NO_COM or OPTION_STRICT or OPTION_IGNORE_ERRORS or OPTION_HELP or OPTION_VERSION
                                       or OPTION_TELEMETRY or OPTION_VERBOSE or OPTION_EXECUTE_LINE or OPTION_EXECUTE_SCRIPT or OPTION_REDIRECT_STDOUT)
            {
                if (normalized_option is OPTION_NO_PLUGINS)
                    set_option(ref raw.no_plugins, true);
                else if (normalized_option is OPTION_NO_COM)
                    if (NativeInterop.OperatingSystem is OS.Windows)
                        set_option(ref raw.no_com, true);
                    else
                        errors.Add(new(index, Language["command_line.error.unsupported_os", NativeInterop.OperatingSystem, normalized_option], true));
                else if (normalized_option is OPTION_NO_GUI)
                    set_option(ref raw.no_gui, true);
                else if (normalized_option is OPTION_STRICT)
                    set_option(ref raw.strict_au3, true);
                else if (normalized_option is OPTION_IGNORE_ERRORS)
                    set_option(ref raw.ignore_errors, true);
                else if (normalized_option is OPTION_HELP)
                    set_option(ref raw.show_help, true);
                else if (normalized_option is OPTION_VERSION)
                    set_option(ref raw.show_version, true);
                else if (normalized_option is OPTION_REDIRECT_STDOUT)
                    set_option(ref raw.redirect_stderr, true);
                else if (normalized_option is OPTION_VERBOSE)
                    set_option(ref raw.verbosity, VerbosityLevel.FullDebug);
                else if (normalized_option is OPTION_TELEMETRY)
                    set_option(ref raw.verbosity, VerbosityLevel.Telemetry);
                else if (normalized_option is OPTION_EXECUTE_LINE)
                    set_option(ref raw.execmode, ExecutionMode.Line);
                else if (normalized_option is OPTION_EXECUTE_SCRIPT)
                    set_option(ref raw.execmode, ExecutionMode.Normal);
                else
                    unknown_option(option, true);

                index += string.IsNullOrEmpty(value) ? 0 : 1;
            }
            else
                unknown_option(option, true);
        }


        while (index < argv.Length)
        {
            string argument = argv[index];

            if (argument == "--")
                ignore_dash = true;
            else if (current_option is { })
            {
                process_option(current_option, argument);

                current_option = null;
            }
            else if (!ignore_dash && argument is ['-', '-', .. string opt1])
                current_option = opt1;
            else if (!ignore_dash && argument is ['/', .. string opt2])
                current_option = opt2;
            else if (!ignore_dash && argument is ['-', char short_option, .. string value])
                if (TryMapShortOption(short_option) is string option)
                    process_option(option, value);
                else
                    unknown_option("-" + short_option, true);
            else if (raw.script_path is null)
                raw.script_path = argument;
            else
                raw.script_options.Add(argument);

            ++index;
        }

        if (current_option is { })
            process_option(current_option, null);

        return raw;
    }

    public CommandLineOptions? Parse(string[] argv, out List<CommandLineParsingError> errors)
    {
        errors = [];

        RawCommandLineOptions raw = ParseRaw(argv, errors);
        UpdaterMode updatemode = raw.updatemode ?? UpdaterMode.Release;
        ExecutionMode execmode = raw.execmode ?? ExecutionMode.Normal;
        string langcode = raw.langcode ?? Language.LanguageCode;

        if (raw.show_help ?? false)
            return new CommandLineOptions.ShowHelp { LanguageCode = langcode, UpdaterMode = updatemode };
        else if (raw.show_version ?? false)
            return new CommandLineOptions.ShowVersion { LanguageCode = langcode, UpdaterMode = updatemode };
        else if (execmode is ExecutionMode.View)
            if (raw.script_path is null)
                errors.Add(new(-1, Language["command_line.error.missing_file_path"], true));
            else
                return new CommandLineOptions.ViewMode { LanguageCode = langcode, UpdaterMode = updatemode, FilePath = raw.script_path };
        else
        {
            bool strict_au3 = raw.strict_au3 ?? false;
            bool no_plugins = raw.no_plugins ?? false;
            bool ignore_errors = raw.ignore_errors ?? false;
            bool redirect_stderr = raw.redirect_stderr ?? false;

            if (execmode is ExecutionMode.Interactive)
                return new CommandLineOptions.RunMode.InteractiveMode
                {
                    LanguageCode = langcode,
                    UpdaterMode = updatemode,
                    DontLoadPlugins = no_plugins,
                    IgnoreErrors = ignore_errors,
                    RedirectStdErrToStdOut = redirect_stderr,
                    StrictAU3Mode = strict_au3,
                    ScriptArguments = [.. raw.script_options],
                };
            else if (string.IsNullOrWhiteSpace(raw.script_path))
                errors.Add(new(-1, Language[execmode is ExecutionMode.Line ? "command_line.error.missing_au3_code_line" : "command_line.error.missing_file_path"], true));
            else
            {
                VerbosityLevel verbosity = raw.verbosity ?? VerbosityLevel.Normal;
                bool no_com = raw.no_com ?? false;
                bool no_gui = raw.no_gui ?? false;

                if (execmode is ExecutionMode.Line)
                    return new CommandLineOptions.RunMode.NonInteractiveMode.RunLine
                    {
                        LanguageCode = langcode,
                        UpdaterMode = updatemode,
                        DontLoadPlugins = no_plugins,
                        IgnoreErrors = ignore_errors,
                        RedirectStdErrToStdOut = redirect_stderr,
                        StrictAU3Mode = strict_au3,
                        VerbosityLevel = verbosity,
                        DisableCOMConnector = no_com,
                        DisableGUIConnector = no_gui,
                        Code = raw.script_path,
                        ScriptArguments = [.. raw.script_options],
                    };
                else
                    return new CommandLineOptions.RunMode.NonInteractiveMode.RunScript
                    {
                        LanguageCode = langcode,
                        UpdaterMode = updatemode,
                        DontLoadPlugins = no_plugins,
                        IgnoreErrors = ignore_errors,
                        RedirectStdErrToStdOut = redirect_stderr,
                        StrictAU3Mode = strict_au3,
                        VerbosityLevel = verbosity,
                        DisableCOMConnector = no_com,
                        DisableGUIConnector = no_gui,
                        FilePath = raw.script_path,
                        ScriptArguments = [.. raw.script_options],
                    };
            }
        }

        return null;
    }

    public void PrintHelp()
    {

    }


    private sealed class RawCommandLineOptions
    {
        public VerbosityLevel? verbosity = null;
        public ExecutionMode? execmode = null;
        public UpdaterMode? updatemode = null;
        public string? langcode = null;
        public bool? strict_au3 = null;
        public bool? ignore_errors = null;
        public bool? no_plugins = null;
        public bool? no_com = null;
        public bool? no_gui = null;
        public bool? show_help = null;
        public bool? show_version = null;
        public bool? redirect_stderr = null;
        public List<string> script_options = [];
        public string? script_path = null;
    }
}

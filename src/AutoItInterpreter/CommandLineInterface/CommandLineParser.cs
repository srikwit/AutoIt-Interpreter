using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System;

using Unknown6656.AutoIt3.Runtime.Native;
using Unknown6656.AutoIt3.Localization;
using Unknown6656.Controls.Console;
using Unknown6656.Generics;
using Unknown6656.Imaging;
using Unknown6656.Common;

namespace Unknown6656.AutoIt3.CLI;


public enum VerbosityLevel
{
    Quiet,
    Normal,
    Telemetry,
    Verbose,
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
    public required VerbosityLevel VerbosityLevel { get; set; }
    public virtual bool StrictAU3Mode { get; set; } = true;

    public bool VerboseOutput => VerbosityLevel >= VerbosityLevel.Verbose;


    private protected virtual IEnumerable<(string option, object? value)> Options
    { 
        get
        {
            List<(string, object?)> opts = [
                (CommandLineParser.OPTION_LANG, LanguageCode),
                (CommandLineParser.OPTION_CHECK_FOR_UPDATE, UpdaterMode),
                (CommandLineParser.OPTION_VERBOSITY, VerbosityLevel),
            ];

            if (StrictAU3Mode)
                opts.Add((CommandLineParser.OPTION_STRICT, null));

            return opts;
        }
    }

    public virtual string Serialize() => (from opt in Options
                                          let vstr = opt.value as string ?? opt.value?.ToString() ?? ""
                                          select $"--{opt.option}{(opt.value is null ? "" : vstr.Contains(' ') || vstr.Contains('"') ? $" \"{vstr.Replace("\"", "\\\"")}\"" : ' ' + vstr)}"
                                          ).StringJoin(" ");


    public sealed record ShowHelp
        : CommandLineOptions
    {
        private protected override IEnumerable<(string option, object? value)> Options => base.Options.Append((CommandLineParser.OPTION_HELP, null));
    }

    public sealed record ShowVersion
        : CommandLineOptions
    {
        private protected override IEnumerable<(string option, object? value)> Options => base.Options.Append((CommandLineParser.OPTION_VERSION, null));
    }

    public sealed record ViewMode
        : CommandLineOptions
    {
        public required string FilePath { get; set; }

        private protected override IEnumerable<(string option, object? value)> Options => base.Options.Append((CommandLineParser.OPTION_MODE, ExecutionMode.View));

        public override string Serialize() => $"{base.Serialize()} \"{FilePath}\"";
    }

    public abstract record RunMode
        : CommandLineOptions
    {
        public required bool RedirectStdErrToStdOut { get; set; }
        public override required bool StrictAU3Mode { get; set; }
        public required string[] ScriptArguments { get; set; }
        public required bool DontLoadPlugins { get; set; }
        public required bool IgnoreErrors { get; set; }

        private protected override IEnumerable<(string option, object? value)> Options
        {
            get
            {
                IEnumerable<(string, object?)> opt = base.Options;

                if (DontLoadPlugins)
                    opt = opt.Append((CommandLineParser.OPTION_NO_PLUGINS, null));

                if (StrictAU3Mode)
                    opt = opt.Append((CommandLineParser.OPTION_STRICT, null));

                if (IgnoreErrors)
                    opt = opt.Append((CommandLineParser.OPTION_IGNORE_ERRORS, null));

                if (RedirectStdErrToStdOut)
                    opt = opt.Append((CommandLineParser.OPTION_REDIRECT_STDOUT, null));

                return opt;
            }
        }


        public sealed record InteractiveMode
            : RunMode
        {
            private protected override IEnumerable<(string option, object? value)> Options => base.Options.Append((CommandLineParser.OPTION_MODE, ExecutionMode.Interactive));
        }

        public abstract record NonInteractiveMode
            : RunMode
        {
            public required bool DisableCOMConnector { get; set; } // windows only
            public required bool DisableGUIConnector { get; set; }

            private protected override IEnumerable<(string option, object? value)> Options
            {
                get
                {
                    IEnumerable<(string, object?)> opt = base.Options;

                    if (DisableCOMConnector)
                        opt = opt.Append((CommandLineParser.OPTION_NO_COM, null));

                    if (DisableGUIConnector)
                        opt = opt.Append((CommandLineParser.OPTION_NO_GUI, null));

                    return opt;
                }
            }


            public sealed record RunLine
                : NonInteractiveMode
            {
                public required string Code { get; set; }

                private protected override IEnumerable<(string option, object? value)> Options => base.Options.Append((CommandLineParser.OPTION_MODE, ExecutionMode.Line));

                public override string Serialize() => $"{base.Serialize()} \"{Code}\"";
            }

            public sealed record RunScript
                : NonInteractiveMode
            {
                public required string FilePath { get; set; }

                private protected override IEnumerable<(string option, object? value)> Options => base.Options.Append((CommandLineParser.OPTION_MODE, ExecutionMode.Normal));

                public override string Serialize()
                {
                    StringBuilder sb = new();

                    sb.Append(base.Serialize());
                    sb.Append($" \"{FilePath}\"");

                    foreach (string arg in ScriptArguments)
                        sb.Append($" \"{arg.Replace("\"", "\\\"")}\"");

                    return sb.ToString();
                }
            }
        }
    }
}

public sealed record CommandLineParsingError(int ArgumentIndex, string Message, bool Fatal);

public partial class CommandLineParser(LanguagePack language)
{
    internal static readonly Regex _regex_fmtstring = new(@"\{(?<content>[^\}:,]+)(?<format>(,[^\}:]+)?(:[^\}]+)?)\}", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    internal const string OPTION_MODE = "mode";
    internal const string OPTION_NO_PLUGINS = "no-plugins";
    internal const string OPTION_NO_COM = "no-com";
    internal const string OPTION_NO_GUI = "no-gui";
    internal const string OPTION_STRICT = "strict";
    internal const string OPTION_IGNORE_ERRORS = "ignore-errors";
    internal const string OPTION_CHECK_FOR_UPDATE = "update";
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
        string? current_prefix = null;
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
#warning TODO : add parsing for -vv
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
                    set_option(ref raw.verbosity, VerbosityLevel.Verbose);
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
            else if (File.Exists(option))
            {
                raw.script_path = option;
                current_prefix = null;
                current_option = null;
            }
            else
            {
                string path = current_prefix + option;

                if (File.Exists(path))
                {
                    raw.script_path = path;
                    current_prefix = null;
                    current_option = null;
                }
                else
                    unknown_option(option, true);
            }
        }


        while (index < argv.Length)
        {
            string argument = argv[index];

            if (!ignore_dash && argument == "--")
                ignore_dash = true;
            else if (current_option is { })
            {
                process_option(current_option, argument);

                current_prefix = null;
                current_option = null;
            }
            else if (!ignore_dash && argument is ['-', '-', .. string opt1])
            {
                current_prefix = "--";

                if (opt1.IndexOf('=') is int i and > 0)
                {
                    current_option = opt1[..i];

                    process_option(current_option, opt1[(i + 1)..]);
                }
                else
                    current_option = opt1;
            }
            else if (!ignore_dash && argument is ['/', .. string opt2])
            {
                current_prefix = "/";
                current_option = opt2;
            }
            else if (!ignore_dash && argument is ['-', char short_option, .. string value])
            {
                if (value.StartsWith(':'))
                    value = value[1..];

                if (TryMapShortOption(short_option) is string option)
                    process_option(option, value);
                else
                    unknown_option("-" + short_option, true);
            }
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
        VerbosityLevel verbosity = raw.verbosity ?? VerbosityLevel.Normal;
        UpdaterMode updatemode = raw.updatemode ?? UpdaterMode.Release;
        ExecutionMode execmode = raw.execmode ?? ExecutionMode.Normal;
        string langcode = raw.langcode ?? Language.LanguageCode;

        if (raw.show_help ?? false)
            return new CommandLineOptions.ShowHelp { LanguageCode = langcode, UpdaterMode = updatemode, VerbosityLevel = verbosity };
        else if (raw.show_version ?? false)
            return new CommandLineOptions.ShowVersion { LanguageCode = langcode, UpdaterMode = updatemode, VerbosityLevel = verbosity };
        else if (execmode is ExecutionMode.View)
            if (raw.script_path is null)
                errors.Add(new(-1, Language["command_line.error.missing_file_path"], true));
            else
                return new CommandLineOptions.ViewMode { LanguageCode = langcode, UpdaterMode = updatemode, FilePath = raw.script_path, VerbosityLevel = verbosity };
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
                    VerbosityLevel = verbosity,
                    ScriptArguments = [.. raw.script_options],
                };
            else if (string.IsNullOrWhiteSpace(raw.script_path))
                errors.Add(new(-1, Language[execmode is ExecutionMode.Line ? "command_line.error.missing_au3_code_line" : "command_line.error.missing_file_path"], true));
            else
            {
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

    private static string FormatOption(int indentation, string option, int option_width, string description, int total_width)
    {
        int description_width = total_width - indentation - option_width;
        List<string> outlines = new();
        string curr_string = "";

        foreach (string line in description.SplitIntoLines())
        {
            Match[] esc_seq = line.MatchVT100EscapeSequences().ToArray();
            int src_offs = 0;
            int dst_offs = 0;

            for (int i = 0; i < esc_seq.Length; ++i)
            {
                string prepend = line[src_offs..esc_seq[i].Index];

                while (prepend.Length > description_width - dst_offs)
                {
                    outlines.Add(curr_string + prepend[..(description_width - dst_offs)]);
                    prepend = prepend[(description_width - dst_offs)..];
                    curr_string = "";
                    dst_offs = 0;
                }

                curr_string += prepend + esc_seq[i].Value;
                dst_offs = prepend.Length;
                src_offs = esc_seq[i].Index + esc_seq[i].Length;
            }

            outlines.Add(curr_string + line[src_offs..]);
            curr_string = "";
        }

        int opt_len = option.Length - option.MatchVT100EscapeSequences().Sum(m => m.Length);
        string opt_padding = opt_len < option_width ? new(' ', option_width - opt_len) : "";

        return (from t in outlines.WithIndex()
                let padding = t.Index == 0 ? new string(' ', indentation) + option + opt_padding : new(' ', indentation + option_width)
                select padding + t.Item + '\n'
                ).StringConcat();
    }

    private static string FormatHelpString(FormattableString fstring, VT100Stylesheet stylesheet) => FormatHelpString(fstring.Format, stylesheet, fstring.GetArguments());

    private static string FormatHelpString(string fstring, VT100Stylesheet stylesheet, object?[] args) => stylesheet.DefaultVT100Style + _regex_fmtstring.Replace(fstring, match =>
    {
        string content = match.Groups["content"].Value;
        string format = match.Groups["format"].Value;
        bool sindx = int.TryParse(content, out int index) && index < args.Length;

        if (format.Length > 0 && stylesheet.VT100Styles.TryGetValue(format[1..], out string? vt100))
            content = vt100 + (sindx ? args[index] : content) + stylesheet.DefaultVT100Style;
        else if (sindx)
            content = string.Format($"{{0{format}}}", args[index]);
        else
            content = string.Format($"{{0{format}}}", content);

        return content + stylesheet.DefaultVT100Style;
    });

    private HelpPage BuildHelpPage()
    {
        string executable = NativeInterop.OperatingSystem is OS.Linux or OS.UnixLike or OS.MacOS ? "./autoit3" : "autoit3";

        return new HelpPage(Language["command_line.help.title"], [
            new HelpSection(null, Language["command_line.help.intro", executable]),
            new HelpExamples(
                Language["command_line.help.usage.header"],
                null,
                [
                    ($"{executable:executable} {"--" + OPTION_HELP:option}", null),
                    ($"{executable:executable} {"--" + OPTION_VERSION:option}", null),
                    ($"{executable:executable} {"-m":option}{'i':value}", null),
                    ($"{executable:executable} {$"[{Language["command_line.help.placeholder.options"]}]":optional} {$"<{Language["command_line.help.placeholder.script_path"]}>":placeholder} {$"[{Language["command_line.help.placeholder.script_args"]}]":optional}", null),
                ]
            ),
            new HelpReference(
                null,
                Language["command_line.help.options.syntax.text"],
                [
                    new(["{-o:option}"], Language["command_line.help.options.syntax.short_no_value"]),
                    new(["{-o:option}{v:value}"], Language["command_line.help.options.syntax.short_value"]),
                    new(["{-o:option}{value:value}"], Language["command_line.help.options.syntax.short_full_value"]),
                    new(["{-o:option}:{v:value}"], Language["command_line.help.options.syntax.short_value"]),
                    new(["{-o:option}:{value:value}"], Language["command_line.help.options.syntax.short_full_value"]),
                    new(["{--option:option}"], Language["command_line.help.options.syntax.long_no_value"]),
                    new(["{--option:option} {value:value}"], Language["command_line.help.options.syntax.long_value"]),
                    new(["{--option:option}={value:value}"], Language["command_line.help.options.syntax.long_value"]),
                    new(["{/option:option}"], Language["command_line.help.options.syntax.long_no_value"]),
                    new(["{/option:option} {value:value}"], Language["command_line.help.options.syntax.long_value"]),
                ]
            ),
            new HelpReference(
                Language["command_line.help.options.header"],
                null,
                [
                    new(["-h", "-?", "--help"], Language["command_line.help.options.help"]),
                    new(["-V", "--version"], Language["command_line.help.options.version"]),
                    new(["{-m:option}{mode:placeholder}", "{--mode:option} {mode:placeholder}"], Language["command_line.help.options.mode.header"], default, [
                        new(["n", "normal"], Language["command_line.help.options.mode.normal"], ValueProperties.DefaultValue),
                        new(["v", "view"], Language["command_line.help.options.mode.view"]),
                        new(["l", "line"], Language["command_line.help.options.mode.line"]),
                        new(["i", "interactive"], Language["command_line.help.options.mode.interactive"]),
                    ]),
                    new(["{-v:option}{level:placeholder}", "{--verbosity:option} {level:placeholder}"], Language["command_line.help.options.verbosity.header"], default, [
                        new(["0", "q", "quiet"], Language["command_line.help.options.verbosity.quiet"], ValueProperties.DefaultValue),
                        new(["1", "n", "normal"], Language["command_line.help.options.verbosity.normal"]),
                        new(["2", "t", "telemetry"], Language["command_line.help.options.verbosity.telemetry"]),
                        new(["3", "v", "verbose"], Language["command_line.help.options.verbosity.verbose"]),
                    ]),
                    new(["-N", "--no-plugins"], Language["command_line.help.options.no_plugins"]),
                    new(["-C", "--no-com"], Language["command_line.help.options.no_com"]),
                    new(["-G", "--no-gui"], Language["command_line.help.options.no_gui"]),
                    new(["-s", "--strict"], Language["command_line.help.options.strict"]),
                    new(["-e", "--ignore-errors"], Language["command_line.help.options.ignore_errors"], OptionProperties.Unsafe),
                    new(["{-u:option}{mode:placeholder}", "{--update:option} {mode:placeholder}"], Language["command_line.help.options.update.header"], default, [
                        new(["r", "release"], Language["command_line.help.options.update.release"], ValueProperties.DefaultValue),
                        new(["b", "beta"], Language["command_line.help.options.update.beta"]),
                        new(["n", "none"], Language["command_line.help.options.update.none"]),
                    ]),
                    new(["{-l:option}{lang_code:placeholder}", "{--lang:option} {lang_code:placeholder}"], Language["command_line.help.options.language", MainProgram.LANG_DIR]),
                    new(["--ErrorStdOut"], Language["command_line.help.options.redirect_stderr"]),
                    new(["--"], Language["command_line.help.options.ignore_subsequent"]),
                    new(["-t", "--telemetry"], Language["command_line.help.same_as", "{--verbosity:option} {telemetry:value}"], OptionProperties.Obsolete),
                    new(["-v", "--verbose"], Language["command_line.help.same_as", "{--verbosity:option} {verbose:value}"], OptionProperties.Obsolete),
                    new(["--AutoIt3ExecuteScript"], Language["command_line.help.same_as", "{--mode:option} {normal:value}"], OptionProperties.Obsolete),
                    new(["--AutoIt3ExecuteLine"], Language["command_line.help.same_as", "{--mode:option} {line:value}"], OptionProperties.Obsolete),
                ]
            ),
            new HelpSection(Language["command_line.help.script_path.header"], Language["command_line.help.script_path.text"]),
            new HelpSection(Language["command_line.help.script_args.header"], Language["command_line.help.script_args.text"]),
            new HelpExamples(
                Language["command_line.help.examples.header"],
                null,
                [
                    ($"{executable:executable} {"--" + OPTION_VERSION:option}", Language["command_line.help.examples.version"]),
                    ($"{executable:executable} {"-m":option}{'i':value}", Language["command_line.help.examples.interactive"]),
                    ($"{executable:executable} {"~/scripts/hello_world.au3":placeholder}", Language["command_line.help.examples.run_script"]),
                    ($"{executable:executable} {"-m":option}{'v':value} {"http://example.com/script.au3":placeholder}", Language["command_line.help.examples.view_script"]),
                    ($"{executable:executable} {"-m":option}{'l':value} \"ConsoleWrite(@AUTOIT_EXE)\"", Language["command_line.help.examples.run_line", ScriptVisualizer.VisualizeScriptAsVT100("ConsoleWrite(@AUTOIT_EXE)", false)]),

#warning TODO : add more examples
                    //($"{executable:executable} {"-m":option}{'n':value} <script_path> -- -arg1 -arg2", Language["command_line.help.examples.script_args"]),
                    //($"{executable:executable} {"-m":option}{'n':value} <script_path> -arg1 -arg2", Language["command_line.help.examples.script_args"]),
                ]
            ),
        ]);
    }

    public string RenderHelpMenu(int console_width)
    {
        if (console_width < 110)
            console_width = short.MaxValue; // disable block wrapping, use single line wrapping instead

        HelpPage help_page = BuildHelpPage();
        VT100Stylesheet stylesheet = new(
            new()
            {
                ["header"] = "\e[1m\e[4m" + RGBAColor.NavajoWhite.ToVT100ForegroundString(),
                ["executable"] = RGBAColor.LightSteelBlue.ToVT100ForegroundString(),
                ["optional"] = RGBAColor.Gray.ToVT100ForegroundString(),
                ["option"] = RGBAColor.Coral.ToVT100ForegroundString(),
                ["value"] = RGBAColor.LightGreen.ToVT100ForegroundString(),
                ["error"] = RGBAColor.Red.ToVT100ForegroundString(),
                ["warning"] = RGBAColor.Orange.ToVT100ForegroundString(),
                ["placeholder"] = RGBAColor.Plum.ToVT100ForegroundString(),
                ["description"] = RGBAColor.White.ToVT100ForegroundString(),
            },
            "\e[0m"
        );
        StringBuilder sb = new();

        sb.AppendLine(FormatHelpString($"\e[1m{help_page.Title:header}\n", stylesheet));

        foreach (HelpSection section in help_page.Sections)
        {
            if (section.Header is string header)
                sb.AppendLine(FormatHelpString($"{header:header}{(string.IsNullOrEmpty(section.Text) ? "" : '\n' + section.Text)}", stylesheet));
            else if (!string.IsNullOrEmpty(section.Text))
                sb.AppendLine(FormatHelpString(section.Text, stylesheet, []));

            if (section is HelpExamples examples)
                foreach ((FormattableString example, string? descr) in examples.Examples)
                    if (descr is { } d)
                        sb.Append(FormatOption(
                            4,
                            FormatHelpString(example, stylesheet),
                            53,
                            FormatHelpString(d, stylesheet, []),
                            console_width
                        ));
                    else
                        sb.Append("    ")
                          .AppendLine(FormatHelpString(example, stylesheet));
            else if (section is HelpReference reference)
                foreach (OptionReference option in reference.OptionReferences)
                {
                    string descr = option.Description;

                    if (option.Properties.HasFlag(OptionProperties.Unsafe))
                        descr = $"{{[{Language["command_line.help.options.unsafe"]}]:warning}} {descr}";
                    else if (option.Properties.HasFlag(OptionProperties.Obsolete))
                        descr = $"{{[{Language["command_line.help.options.obsolete"]}]:warning}} {descr}";

                    sb.Append(FormatOption(
                        4,
                        FormatHelpString(option.Options.Select(opt => opt.Contains('{') ? opt : $"{{{opt}:option}}").StringJoin(", "), stylesheet, []),
                        53,
                        FormatHelpString(descr, stylesheet, []),
                        console_width
                    ));

                    foreach (ValueReference value in option.ValueReferences ?? [])
                    {
                        descr = value.Description;

                        if (value.Properties.HasFlag(ValueProperties.DefaultValue))
                            descr = $"{{[{Language["command_line.help.options.default"]}]:optional}} {descr}";
                        else if (value.Properties.HasFlag(ValueProperties.Obsolete))
                            descr = $"{{[{Language["command_line.help.options.obsolete"]}]:warning}} {descr}";

                        sb.Append(FormatOption(
                            8,
                            FormatHelpString(value.Values.Select(val => val.Contains('{') ? val : $"{{{val}:value}}").StringJoin(", "), stylesheet, []),
                            53,
                            FormatHelpString(descr, stylesheet, []),
                            console_width
                        ));
                    }
                }

            sb.AppendLine()
              .AppendLine();
        }

        return sb.ToString();
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

    private record VT100Stylesheet(Dictionary<string, string> VT100Styles, string DefaultVT100Style = "\e[0m");

    private record HelpPage(string Title, HelpSection[] Sections);

    private record HelpSection(string? Header, string? Text);

    private sealed record HelpExamples(string Header, string? Text, (FormattableString Example, string? Description)[] Examples) : HelpSection(Header, Text);

    private sealed record HelpReference(string? Header, string? Text, OptionReference[] OptionReferences) : HelpSection(Header, Text);

    private sealed record OptionReference(string[] Options, string Description, OptionProperties Properties = OptionProperties.None, ValueReference[]? ValueReferences = null);

    private sealed record ValueReference(string[] Values, string Description, ValueProperties Properties = ValueProperties.None);

    [Flags]
    private enum OptionProperties
    {
        None = 0,
        Unsafe = 1,
        Obsolete = 2,
    }

    [Flags]
    private enum ValueProperties
    {
        None = 0,
        DefaultValue = 1,
        Obsolete = 2,
    }
}

using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Reflection;
using System.Linq;
using System.Text;
using System.IO;
using System;

using Octokit;

using Unknown6656.AutoIt3.Localization;
using Unknown6656.AutoIt3.Runtime.Native;
using Unknown6656.AutoIt3.Runtime;

using Unknown6656.Mathematics.Cryptography;
using Unknown6656.Controls.Console;
using Unknown6656.Generics;
using Unknown6656.Imaging;
using Unknown6656.Common;
using Unknown6656.IO;

using OS = Unknown6656.AutoIt3.Runtime.Native.OS;

namespace Unknown6656.AutoIt3.CLI;


/// <summary>
/// The module containing the AutoIt Interpreter's main entry point.
/// <para/>
/// <b>NOTE:</b> The .NET runtime does not actually call this class directly. The actual entry point resides in the file "../EntryPoint.cs"
/// </summary>
public static class MainProgram
{
    public static readonly Assembly ASM = typeof(MainProgram).Assembly;
    public static readonly FileInfo ASM_FILE = new(ASM.Location);
    public static readonly DirectoryInfo ASM_DIR = ASM_FILE.Directory!;
    public static readonly DirectoryInfo PLUGIN_DIR = ASM_DIR.CreateSubdirectory("plugins/");
    public static readonly DirectoryInfo LANG_DIR = ASM_DIR.CreateSubdirectory("lang/");
    public static readonly DirectoryInfo INCLUDE_DIR = ASM_DIR.CreateSubdirectory("include/");
    public static readonly FileInfo WINAPI_CONNECTOR = new(Path.Combine(ASM_DIR.FullName, "autoit3.win32apiserver.exe"));
    public static readonly FileInfo COM_CONNECTOR = new(Path.Combine(ASM_DIR.FullName, "autoit3.comserver.exe"));
    public static readonly FileInfo GUI_CONNECTOR = new(Path.Combine(ASM_DIR.FullName, "autoit3.guiserver.dll"));
    public static readonly FileInfo UPDATER = new(Path.Combine(ASM_DIR.FullName, "autoit3.updater.dll"));

    internal static readonly RGBAColor COLOR_TIMESTAMP = RGBAColor.Gray;
    internal static readonly RGBAColor COLOR_PREFIX_SCRIPT = RGBAColor.Cyan;
    internal static readonly RGBAColor COLOR_PREFIX_DEBUG = RGBAColor.PaleTurquoise;
    internal static readonly RGBAColor COLOR_SCRIPT = RGBAColor.White;
    internal static readonly RGBAColor COLOR_DEBUG = RGBAColor.LightSteelBlue;
    internal static readonly RGBAColor COLOR_ERROR = RGBAColor.Salmon;
    internal static readonly RGBAColor COLOR_WARNING = RGBAColor.Orange;

    private static readonly ConcurrentQueue<Action> _print_queue = new();
    private static volatile bool _isrunning = true;
    private static volatile bool _finished;

#nullable disable
    public static string[] RawCMDLineArguments { get; private set; }

    public static CommandLineOptions CommandLineOptions { get; private set; }
#nullable enable
    public static InteractiveShell? InteractiveShell { get; private set; }

    public static LanguageLoader LanguageLoader { get; } = new LanguageLoader();

    public static Telemetry Telemetry { get; } = new Telemetry();

    public static bool PausePrinter { get; set; }


    // TODO : clean up 'Start'-method


    /// <summary>
    /// The main entry point for this application.
    /// </summary>
    /// <param name="argv">Command line arguments.</param>
    /// <returns>Return/exit code.</returns>
    public static int Start(string[] argv)
    {
        ConsoleState state = SetUpTerminal();
        Stopwatch sw = Stopwatch.StartNew();

        RawCMDLineArguments = argv;

        Console.CancelKeyPress += OnCancelKeyPress;

        using Task printer_task = Task.Run(PrinterTask);
        using Task telemetry_task = Task.Run(Telemetry.StartPerformanceMonitorAsync);
        int exitcode = 0;

        Telemetry.Measure(TelemetryCategory.ProgramRuntime, delegate
        {
            try
            {
                LanguagePack? lang = TryLoadDefaultLanguage();

                if (lang == null)
                {
                    exitcode = -1;

                    return;
                }

                CommandLineParser parser = new(lang);
                CommandLineOptions? cli_options = Telemetry.Measure(TelemetryCategory.ParseCommandLine, delegate
                {
                    CommandLineOptions? result = parser.Parse(argv, out List<CommandLineParsingError> errors);

                    if (result is null)
                        HandleParserErrors(lang, result, [.. errors.OrderBy(err => err.ArgumentIndex)], ref exitcode);
                    else
                        CommandLineOptions = result;

                    return result;
                });

                if (cli_options == null || exitcode != 0)
                {
                    exitcode = -1;

                    return;
                }
                else if (!LanguageLoader.TrySetCurrentLanguagePack(cli_options.LanguageCode))
                {
                    PrintError(lang["error.unknown_language_pack", cli_options.LanguageCode, LanguageLoader.LoadedLanguageCodes.StringJoin("', '")]);

                    exitcode = -1;

                    return;
                }

                if (cli_options is CommandLineOptions.ShowVersion)
                    PrintVersion();
                else if (cli_options is CommandLineOptions.ShowHelp)
                    PrintHelp(parser);
                else
                    exitcode = Run();
            }
            catch (Exception ex)
            when (!Debugger.IsAttached)
            {
                Telemetry.Measure(TelemetryCategory.Exceptions, delegate
                {
                    exitcode = ex.HResult;

                    PrintException(ex);
                });
            }
        });

        while (_print_queue.Count > 0)
            Thread.Sleep(100);

        sw.Stop();
        Telemetry.SubmitTimings(TelemetryCategory.ProgramRuntimeAndPrinting, sw.Elapsed);
        Telemetry.StopPerformanceMonitor();
        telemetry_task.Wait();

        if (CommandLineOptions is CommandLineOptions.RunMode { VerbosityLevel: > VerbosityLevel.Quiet })
            PrintReturnCodeAndTelemetry(exitcode, Telemetry);

        _isrunning = false;

        while (!_finished)
            printer_task.Wait();

        CleanUpTerminal(state);

        return exitcode;
    }

    private static ConsoleState SetUpTerminal()
    {
        ConsoleExtensions.ThrowOnInvalidConsoleMode = false;

        if (!ConsoleExtensions.SupportsVT100EscapeSequences)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("""
            .--------------------------------------------------.
            |                     WARNING!                     |
            |                                                  |
            | Your terminal does NOT support VT100/ANSI escape |
            | sequences. This WILL lead to a severely degraded |
            |      user experience. You have been warned.      |
            '--------------------------------------------------'

            """);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("(Press any key to continue)");
            Console.ReadKey(true);
        }

        ConsoleState state = ConsoleExtensions.SaveConsoleState();

        NativeInterop.DoPlatformDependent(delegate
        {
#pragma warning disable CA1416 // Validate platform compatibility
            Console.BufferHeight = short.MaxValue - 1;
            Console.WindowWidth = Math.Max(Console.WindowWidth, 100);
            Console.BufferWidth = Math.Max(Console.BufferWidth, Console.WindowWidth);
#pragma warning restore CA1416
        }, OS.Windows);

        // Console.OutputEncoding = Encoding.Unicode;
        // Console.InputEncoding = Encoding.Unicode;
        Console.ResetColor();
        ConsoleExtensions.RGBForegroundColor = RGBAColor.White;

        return state;
    }

    private static void CleanUpTerminal(ConsoleState state)
    {
        ConsoleExtensions.RGBForegroundColor = RGBAColor.White;
        ConsoleExtensions.RestoreConsoleState(state);
        Console.Write("\e[0m");
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (InteractiveShell is { } shell)
        {
            e.Cancel = true;
            shell.OnCancelKeyPressed();
        }
        else
        {
            Interpreter[] instances = Interpreter.ActiveInstances;
            List<FunctionReturnValue> return_values = [];

            e.Cancel = instances.Length > 0;

            foreach (Interpreter interpreter in instances)
            {
                interpreter.ExitMethod = InterpreterExitMethod.ByClick;
                return_values.Add(interpreter.Stop(-1));
            }

            // TODO : exit?
            // TODO : print fatal error
        }
    }

    private static LanguagePack? TryLoadDefaultLanguage() => Telemetry.Measure(TelemetryCategory.LoadLanguage, delegate
    {
        LanguageLoader.LoadLanguagePacksFromDirectory(LANG_DIR);

        if (LanguageLoader.LoadedLanguageCodes.Length == 0)
            PrintError($"Unable to load any language packs. Verify whether the directory '{LANG_DIR}' is accessible and contains at least one valid language pack.");
        else if (LanguageLoader.LoadedLanguageCodes.Contains("en", StringComparer.OrdinalIgnoreCase))
            LanguageLoader.TrySetCurrentLanguagePack("en");
        else
            LanguageLoader.TrySetCurrentLanguagePack(LanguageLoader.LoadedLanguageCodes[0]);

        return LanguageLoader.CurrentLanguage;
    });

    private static void HandleParserErrors(LanguagePack lang, CommandLineOptions? result, CommandLineParsingError[] errors, ref int code)
    {
        if (errors.Length == 0)
            return;

        bool fatal = errors.Any(e => e.Fatal);

        code = fatal ? -1 : 0;

        if (fatal || result?.VerbosityLevel > VerbosityLevel.Normal)
#warning TODO : change to quiet?
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Command line arguments ({RawCMDLineArguments.Length}):");
            Console.WriteLine($"    {RawCMDLineArguments.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg).StringJoin(" ")}");

            foreach (CommandLineParsingError err in errors)
            {
                Console.ForegroundColor = err.Fatal ? ConsoleColor.Red : ConsoleColor.Yellow;
                Console.WriteLine($"    {(err.ArgumentIndex < 0 ? "         " : $"Arg. #{err.ArgumentIndex,2}:")} {err.Message}");
            }
        }
    }

    private static int Run()
    {
        LanguagePack lang = LanguageLoader.CurrentLanguage!;
        Task<bool> update_task = UpdateSoftwareTask();

        PrintBanner();
        PrintDebugMessage($"{CommandLineOptions.Serialize()}\n{CommandLineOptions}");
        PrintfDebugMessage("debug.langpack_found", LanguageLoader.LoadedLanguageCodes.Length);
        PrintfDebugMessage("debug.loaded_langpack", lang);
        PrintfDebugMessage("debug.interpreter_loading");

        using Interpreter interpreter = Telemetry.Measure(TelemetryCategory.InterpreterInitialization, () => new Interpreter(CommandLineOptions, Telemetry, LanguageLoader));

        PrintfDebugMessage("debug.interpreter_loaded");

        if (update_task.GetAwaiter().GetResult())
        {
#warning TODO : print some kind of message???
            return 0; // update has been performed
        }
        if (CommandLineOptions is CommandLineOptions.ViewMode view)
            return interpreter.ScriptScanner
                              .ScanScriptFile(SourceLocation.Unknown, view.FilePath, true)
                              .Match(
                error =>
                {
                    PrintError($"{lang["error.error_in", error.Location ?? SourceLocation.Unknown]}:\n    {error.Message}");

                    return -1;
                },
                script =>
                {
                    ScriptToken[] tokens = ScriptVisualizer.TokenizeScript(script);

                    Console.WriteLine(tokens.ConvertToVT100(true));

                    return 0;
                }
            );
        else if (CommandLineOptions is CommandLineOptions.RunMode.InteractiveMode interactive)
            using (InteractiveShell shell = new(interpreter))
            {
                if (shell.Initialize())
                    (InteractiveShell = shell).Run();

                InteractiveShell = null;
            }
        else if (CommandLineOptions is CommandLineOptions.RunMode.NonInteractiveMode non_interactive)
        {
            Union<InterpreterError, ScannedScript> resolved;

            if (non_interactive is CommandLineOptions.RunMode.NonInteractiveMode.RunLine run_line)
            {
                FileInfo tmp_path = new($"0:/temp~{interpreter.Random.NextInt():x8}");

                resolved = interpreter.ScriptScanner.ProcessScriptFile(tmp_path, run_line.Code);
            }
            else if (non_interactive is CommandLineOptions.RunMode.NonInteractiveMode.RunScript run_script)
                resolved = interpreter.ScriptScanner.ScanScriptFile(SourceLocation.Unknown, run_script.FilePath, true);
            else
            {
                // TODO : unknown execution mode

                throw new NotImplementedException();
                return -1;
            }

            InterpreterError? error = null;

            if (resolved.Is(out ScannedScript? script))
            {
                FunctionReturnValue result = Telemetry.Measure(TelemetryCategory.InterpreterRuntime, () => interpreter.Run(script, InterpreterRunContext.Regular));

                result.IsFatal(out error);

                return result.IsError(out int exitcode) ? exitcode : 0;
            }
            else
                error = resolved.As<InterpreterError>();

            if (error is InterpreterError err)
                PrintError($"{lang["error.error_in", err.Location ?? SourceLocation.Unknown]}:\n    {err.Message}");
        }

        return -1;
    }

    private static async Task<bool> UpdateSoftwareTask()
    {
        if (CommandLineOptions.UpdaterMode is UpdaterMode.None)
            return false;

        GithubUpdater updater = new(Telemetry)
        {
            UpdaterMode = CommandLineOptions.UpdaterMode is UpdaterMode.Beta ? GithubUpdaterMode.IncludeBetaVersions : GithubUpdaterMode.ReleaseOnly
        };

        bool success = await updater.FetchReleaseInformationAsync().ConfigureAwait(true);
        LanguagePack lang = LanguageLoader.CurrentLanguage!;

        if (!success && CommandLineOptions.VerboseOutput)
        {
            PrintWarning(null, lang["warning.unable_to_update", __module__.RepositoryURL + "/releases"]);

            return false;
        }
        else
            success = false;

        if (updater.LatestReleaseAvailable is Release latest)
        {
            bool handled = false;
            bool confirmation = false;

            _print_queue.Enqueue(() => Task.Run(delegate
            {
                ConsoleExtensions.RGBForegroundColor = COLOR_PREFIX_DEBUG;
                Console.WriteLine("\n-------------------------------------------------------------------------------------------------------------\t");
                ConsoleExtensions.WriteUnderlined(lang["general.update.header"]);
                Console.WriteLine("\n-------------------------------------------------------------------------------------------------------------");
                ConsoleExtensions.RGBForegroundColor = COLOR_DEBUG;
                Console.WriteLine(lang["general.update.message", __module__.InterpreterVersion, latest.TagName, latest.Body.SplitIntoLines().Select(line => '\t' + line).StringJoin("\n")]);

                confirmation = Console.ReadKey(true).Key == ConsoleKey.Y;

                Console.WriteLine();

                handled = true;
            }).GetAwaiter().GetResult());

            while (!handled)
                await Task.Delay(20).ConfigureAwait(true);

            if (confirmation)
            {
                success = await updater.TryUpdateTo(latest).ConfigureAwait(true);

                if (!success)
                    PrintError(lang["error.update_failed", latest.TagName, latest.PublishedAt, __module__.RepositoryURL + "/releases"]);
            }
            else
                Console.WriteLine(lang["general.update.update_cancelled"]);
        }

        return success;
    }

    private static async Task PrinterTask()
    {
        while (_isrunning)
            if (!PausePrinter && _print_queue.TryDequeue(out Action? func))
                try
                {
                    Telemetry.Measure(TelemetryCategory.Printing, func);
                }
                catch (Exception ex)
                {
                    PrintException(ex);
                }
            else
                await Task.Delay(50);

        while (_print_queue.TryDequeue(out Action? func))
            try
            {
                Telemetry.Measure(TelemetryCategory.Printing, func);
            }
            catch (Exception ex)
            {
                PrintException(ex);
            }

        _finished = true;
    }

    private static void SubmitPrint(bool requires_verbose, string prefix, string? msg, bool from_script)
    {
        if (!CommandLineOptions.VerboseOutput && requires_verbose)
            return;
        else if (msg is null or "")
            return; // TODO : handle this in a better way??

        DateTime now = DateTime.Now;

        _print_queue.Enqueue(delegate
        {
            ConsoleExtensions.RGBForegroundColor = RGBAColor.DarkGray;
            Console.Write('[');
            ConsoleExtensions.RGBForegroundColor = COLOR_TIMESTAMP;
            Console.Write(now.ToString("HH:mm:ss.fff"));
            ConsoleExtensions.RGBForegroundColor = RGBAColor.DarkGray;
            Console.Write("][");
            ConsoleExtensions.RGBForegroundColor = from_script ? COLOR_PREFIX_SCRIPT : COLOR_PREFIX_DEBUG;
            Console.Write(prefix);
            ConsoleExtensions.RGBForegroundColor = RGBAColor.DarkGray;
            Console.Write("] ");
            ConsoleExtensions.RGBForegroundColor = from_script ? COLOR_SCRIPT : COLOR_DEBUG;
            Console.WriteLine(msg);
            ConsoleExtensions.RGBForegroundColor = RGBAColor.White;
        });
    }

    /// <summary>
    /// Prints the given debug message asynchronously to STDOUT.
    /// </summary>
    /// <param name="message">The debug message to be printed.</param>
    public static void PrintDebugMessage(string message) => PrintChannelMessage("Debug", message);

    /// <summary>
    /// Prints the given localized debug message asynchronously to STDOUT.
    /// </summary>
    /// <param name="key">The language key of the message to be printed.</param>
    /// <param name="args">The arguments used to format the message to be printed.</param>
    public static void PrintfDebugMessage(string key, params object?[] args) => PrintDebugMessage(LanguageLoader.CurrentLanguage?[key, args] ?? key);

    internal static void PrintChannelMessage(string channel, string? message) => SubmitPrint(true, channel, message, false);

    /// <summary>
    /// Prints the given message asynchronously to STDOUT.
    /// </summary>
    /// <param name="file">The (script) file which emitted the message.</param>
    /// <param name="message">The message to be printed.</param>
    public static void PrintScriptMessage(string? file, string message) => Telemetry.Measure(TelemetryCategory.ScriptConsoleOut, delegate
    {
        if (CommandLineOptions is CommandLineOptions.ViewMode)
            return;
        else if (InteractiveShell is InteractiveShell shell)
            shell.SubmitPrint(message);
        else if (!CommandLineOptions.VerboseOutput)
            Console.Write(message);
        else
            SubmitPrint(true, file ?? '<' + LanguageLoader.CurrentLanguage?["general.unknown"] + '>', message.Trim(), true);
    });

    /// <summary>
    /// Prints the given exception asynchronously to STDOUT.
    /// </summary>
    /// <param name="exception">The exception to be printed.</param>
    public static void PrintException(this Exception? exception)
    {
        if (exception is { })
            if (!CommandLineOptions.VerboseOutput)
                PrintError(exception.Message);
            else
            {
                StringBuilder sb = new();

                while (exception is { })
                {
                    sb.Insert(0, $"[{exception.GetType()}] \"{exception.Message}\":\n{exception.StackTrace}\n");
                    exception = exception.InnerException;
                }

                PrintError(sb.ToString());
            }
    }

    /// <summary>
    /// Prints the given error message asynchronously to STDOUT.
    /// </summary>
    /// <param name="message">The error message to be printed.</param>
    public static void PrintError(this string message) => _print_queue.Enqueue(() => Telemetry.Measure(TelemetryCategory.Exceptions, delegate
    {
        if (!CommandLineOptions.VerboseOutput && Console.CursorLeft > 0)
            Console.WriteLine();

        if (CommandLineOptions.VerboseOutput)
        {
            ConsoleExtensions.RGBForegroundColor = RGBAColor.Orange;
            Console.WriteLine(@"
                               ____
                       __,-~~/~    `---.
                     _/_,---(      ,    )
                 __ /        <    /   )  \___
  - ------===;;;'====------------------===;;;===----- -  -
                    \/  ~:~'~^~'~ ~\~'~)~^/
                    (_ (   \  (     >    \)
                     \_( _ <         >_>'
                        ~ `-i' ::>|--`'
                            I;|.|.|
                            | |: :|`
                         .-=||  | |=-.       ___  ____  ____  __  ___  __
                         `-=#$%&%$#=-'      / _ )/ __ \/ __ \/  |/  / / /
                           .| ;  :|        / _  / /_/ / /_/ / /|_/ / /_/
                          (`^':`-'.)      /____/\____/\____/_/  /_/ (_)
______________________.,-#%&$@#&@%#&#~,.___________________________________");
            ConsoleExtensions.RGBForegroundColor = RGBAColor.Yellow;
            Console.WriteLine("            AW SHIT -- THE INTERPRETER JUST BLEW UP!\n");
        }
        else
            Console.WriteLine();

        ConsoleExtensions.RGBForegroundColor = COLOR_ERROR;

        const string report_url = $"\e[4m{__module__.RepositoryURL}/issues/new?template=bug_report.md\e[24m";
        string please_report = LanguageLoader.CurrentLanguage?["error.please_report_bug", report_url] ?? $"If you believe that this is a bug, please report it to {report_url}.";

        Console.WriteLine(message.TrimEnd());
        Console.WriteLine(please_report);

        if (CommandLineOptions.VerboseOutput)
        {
            ConsoleExtensions.RGBForegroundColor = RGBAColor.White;
            Console.WriteLine(new string('_', Console.WindowWidth - 1));
        }

        Console.Write("\e[0m");
    }));

    /// <summary>
    /// Prints the given warning message asynchronously to STDOUT.
    /// </summary>
    /// <param name="location">The source location at which the warning occurred.</param>
    /// <param name="message">The warning message to be printed.</param>
    public static void PrintWarning(SourceLocation? location, string message) => _print_queue.Enqueue(() => Telemetry.Measure(TelemetryCategory.Warnings, delegate
    {
        if (!CommandLineOptions.VerboseOutput)
        {
            if (Console.CursorLeft > 0)
                Console.WriteLine();

            ConsoleExtensions.RGBForegroundColor = COLOR_WARNING;
            Console.WriteLine(LanguageLoader.CurrentLanguage?[location is null ? "warning.warning" : "warning.warning_in", location] + ":\n    " + message.Trim());
        }
        else
        {
            ConsoleExtensions.RGBForegroundColor = RGBAColor.DarkGray;
            Console.Write('[');
            ConsoleExtensions.RGBForegroundColor = COLOR_TIMESTAMP;
            Console.Write(DateTime.Now.ToString("HH:mm:ss.fff", null));
            ConsoleExtensions.RGBForegroundColor = RGBAColor.DarkGray;
            Console.Write("][");
            ConsoleExtensions.RGBForegroundColor = COLOR_WARNING;
            Console.Write("warning");
            ConsoleExtensions.RGBForegroundColor = RGBAColor.DarkGray;
            Console.Write("] ");
            ConsoleExtensions.RGBForegroundColor = COLOR_WARNING;
            Console.WriteLine(message.Trim());
            ConsoleExtensions.RGBForegroundColor = RGBAColor.White;
        }

        Console.Write("\e[0m");
    }));

    /// <summary>
    /// Prints the given return code and telemetry data synchronously to STDOUT.
    /// </summary>
    /// <param name="retcode">Return code, e.g. from the interpreter execution.</param>
    /// <param name="telemetry">Telemetry data to be printed.</param>
    public static void PrintReturnCodeAndTelemetry(int retcode, Telemetry telemetry) => _print_queue.Enqueue(delegate
    {
        LanguagePack? lang = LanguageLoader.CurrentLanguage;

        if (lang is null)
            return;
        else if (Console.CursorLeft > 0)
            Console.WriteLine();

        bool print_telemetry = CommandLineOptions.VerbosityLevel >= VerbosityLevel.Telemetry;
        int width = Math.Min(Console.WindowWidth, Console.BufferWidth);

        if (print_telemetry)
        {
            const int MIN_WIDTH = 180;

            NativeInterop.DoPlatformDependent(delegate
            {
                Console.WindowWidth = Math.Max(Console.WindowWidth, MIN_WIDTH);
                Console.BufferWidth = Math.Max(Console.BufferWidth, Console.WindowWidth);
                Console.BufferHeight = short.MaxValue - 1;
            }, OS.Windows);

            width = Math.Min(Console.WindowWidth, Console.BufferWidth);

            if (NativeInterop.OperatingSystem == OS.Windows && width < MIN_WIDTH)
            {
                PrintError(lang["debug.telemetry.print_error", MIN_WIDTH]);

                return;
            }
        }

        TelemetryTimingsNode root = TelemetryTimingsNode.FromTelemetry(telemetry);

        ConsoleExtensions.RGBForegroundColor = RGBAColor.White;
        Console.WriteLine(new string('_', width - 1));
        ConsoleExtensions.RGBForegroundColor = retcode == 0 ? RGBAColor.SpringGreen : RGBAColor.Salmon;
        Console.WriteLine(lang["debug.telemetry.exit_code", retcode, root.Total, telemetry.TotalTime[TelemetryCategory.InterpreterRuntime]]);
        ConsoleExtensions.RGBForegroundColor = RGBAColor.White;

        if (!print_telemetry)
            return;

        ConsoleExtensions.RGBForegroundColor = RGBAColor.Yellow;
        Console.WriteLine("\n\t\t" + lang["debug.telemetry.header"]);

        #region TIMTINGS : FETCH DATA, INIT

        RGBAColor col_table = RGBAColor.LightGray;
        RGBAColor col_text = RGBAColor.White;
        RGBAColor col_backg = RGBAColor.DarkSlateGray;
        RGBAColor col_hotpath = RGBAColor.Salmon;

        Regex regex_trimstart = new(@"^(?<space>\s*)0(?<rest>\d[:\.].+)$", RegexOptions.Compiled);
        string[] headers = [
            lang["debug.telemetry.columns.category"],
            lang["debug.telemetry.columns.count"],
            lang["debug.telemetry.columns.total"],
            lang["debug.telemetry.columns.avg"],
            lang["debug.telemetry.columns.min"],
            lang["debug.telemetry.columns.max"],
            lang["debug.telemetry.columns.parent"],
            lang["debug.telemetry.columns.relative"],
        ];
        List<(string[] cells, TelemetryTimingsNode node)> rows = [];
        static string ReplaceStart(string input, params (string search, string replace)[] substitutions)
        {
            int idx = 0;
            bool match;

            do
            {
                match = false;

                foreach ((string search, string replace) in substitutions)
                    if (input[idx..].StartsWith(search))
                    {
                        input = input[..idx] + replace + input[(idx + search.Length)..];
                        idx += replace.Length;
                        match = true;

                        break;
                    }
            }
            while (match);

            return input;
        }
        string PrintTime(TimeSpan time)
        {
            string s = ReplaceStart(time.ToString("h\\:mm\\:ss\\.ffffff"),
                ("00:", "   "),
                ("0:", "  "),
                ("00.", " 0.")
            ).TrimEnd('0');

            if (s.Match(regex_trimstart, out ReadOnlyIndexer<string, string>? groups))
                s = groups["space"] + ' ' + groups["rest"];

            if (s.EndsWith("0."))
                s = s[..^1];
            else if (s.EndsWith('.'))
                s += '0';

            return s;
        }
        void traverse(TelemetryTimingsNode node, string prefix = "", bool last = true)
        {
            rows.Add((new[]
            {
                prefix.Length switch
                {
                    0 => " ·─ " + node.Name,
                    _ => string.Concat(prefix.Select(c => c is 'x' ? " │  " : "    ").Append(last ? " └─ " : " ├─ ").Append(node.Name))
                },
                node.Timings.Length.ToString().PadLeft(5),
                PrintTime(node.Total),
                PrintTime(node.Average),
                PrintTime(node.Min),
                PrintTime(node.Max),
                $"{node.PercentageOfParent * 100,9:F5} %",
                $"{node.PercentageOfTotal * 100,9:F5} %",
            }, node));

            TelemetryTimingsNode[] children = [.. node.Children.OrderByDescending(c => c.PercentageOfTotal)];

            for (int i = 0; i < children.Length; i++)
            {
                TelemetryTimingsNode child = children[i];

                traverse(child, prefix + (last ? ' ' : 'x'), i == children.Length - 1);
            }
        }

        traverse(root);

        int[] widths = headers.ToArray(h => h.Length);

        foreach (string[] cells in rows.Select(r => r.cells))
            for (int i = 0; i < widths.Length; i++)
                widths[i] = Math.Max(widths[i], cells[i].Length);

        #endregion
        #region TIMINGS : PRINT HEADER

        //Console.CursorTop -= 2;
        ConsoleExtensions.RGBForegroundColor = col_table;

        for (int i = 0, l = widths.Length; i < l; i++)
        {
            if (i == 0)
            {
                ConsoleExtensions.WriteVertical("┌│├");
                Console.CursorTop -= 2;
            }

            int yoffs = Console.CursorTop;
            int xoffs = Console.CursorLeft;

            Console.Write(new string('─', widths[i]));
            ConsoleExtensions.RGBForegroundColor = col_text;
            ConsoleExtensions.Write(headers[i].PadRight(widths[i]), (xoffs, yoffs + 1));
            ConsoleExtensions.RGBForegroundColor = col_table;
            ConsoleExtensions.Write(new string('─', widths[i]), (xoffs, yoffs + 2));
            ConsoleExtensions.WriteVertical(i == l - 1 ? "┐│┤" : "┬│┼", (xoffs + widths[i], yoffs));
            Console.CursorTop = yoffs;
            
            if (i == l - 1)
            {
                Console.CursorTop += 2;
                Console.WriteLine();
            }
        }

        #endregion
        #region TIMINGS : PRINT DATA

        foreach ((string[] cells, TelemetryTimingsNode node) in rows)
        {
            for (int i = 0, l = cells.Length; i < l; i++)
            {
                ConsoleExtensions.RGBForegroundColor = col_table;

                if (i == 0)
                    Console.Write('│');
                
                ConsoleExtensions.RGBForegroundColor = node.IsHot ? col_hotpath : col_text;

                string cell = cells[i];

                if (i == 0)
                {
                    Console.Write(cell);
                    ConsoleExtensions.RGBForegroundColor = col_backg;
                    Console.Write(new string('─', widths[i] - cell.Length));
                }
                else
                {
                    int xoffs = Console.CursorLeft;

                    ConsoleExtensions.RGBForegroundColor = col_backg;
                    Console.Write(new string('─', widths[i]));
                    ConsoleExtensions.RGBForegroundColor = node.IsHot ? col_hotpath : col_text;
                    Console.CursorLeft = xoffs;

                    for (int j = 0, k = Math.Min(widths[i], cell.Length); j < k; ++j)
                        if (char.IsWhiteSpace(cell[j]))
                            ++Console.CursorLeft;
                        else
                            Console.Write(cell[j]);

                   Console.CursorLeft = xoffs + widths[i];
                }

                ConsoleExtensions.RGBForegroundColor = col_table;
                Console.Write('│');
            }

            Console.WriteLine();
        }

        #endregion
        #region TIMINGS : PRINT FOOTER

        ConsoleExtensions.RGBForegroundColor = col_table;

        for (int i = 0, l = widths.Length; i < l; i++)
        {
            if (i == 0)
                Console.Write('└');

            Console.Write(new string('─', widths[i]));
            Console.Write(i == l - 1 ? '┘' : '┴');
        }

        Console.WriteLine();
        Console.WriteLine(lang["debug.telemetry.explanation"]);

        #endregion

        if (NativeInterop.OperatingSystem == OS.Windows)
        {
            #region PERFORMANCE : FETCH DATA

            const int PADDING = 22;
            List<(DateTime time, double total, double user, double kernel, long ram)> performance_data = [];
            int width_perf = width - 3 - PADDING;
            const int height_perf_cpu = 14;

            performance_data.AddRange(telemetry.PerformanceMeasurements);

            if (performance_data.Count > width_perf)
            {
                int step = performance_data.Count / (performance_data.Count - width_perf);
                int index = performance_data.Count - 1;

                while (index > 0 && performance_data.Count > width_perf)
                {
                    performance_data.RemoveAt(index);
                    index -= step;
                }
            }

            width_perf = performance_data.Count + PADDING;

            #endregion
            #region PERFORMANCE : PRINT FRAME

            RGBAColor col_cpu_user = RGBAColor.Chartreuse;
            RGBAColor col_cpu_kernel = RGBAColor.LimeGreen;
            RGBAColor col_ram = RGBAColor.CornflowerBlue;

            ConsoleExtensions.RGBForegroundColor = col_table;
            Console.WriteLine('┌' + new string('─', width_perf) + '┐');

            int ypos = Console.CursorTop;

            for (int i = 0; i < height_perf_cpu + 2; ++i)
            {
                Console.CursorLeft = 0;
                Console.Write('│');
                Console.CursorLeft = width_perf + 1;
                Console.Write('│');
                Console.CursorTop++;
            }

            Console.CursorLeft = 0;
            Console.WriteLine('└' + new string('─', width_perf) + '┘');

            Console.SetCursorPosition(2, ypos);
            ConsoleExtensions.RGBForegroundColor = col_text;
            ConsoleExtensions.WriteUnderlined("CPU Load");
            Console.SetCursorPosition(2, ypos + 2);
            ConsoleExtensions.RGBForegroundColor = col_cpu_user;
            Console.Write("███ ");
            ConsoleExtensions.RGBForegroundColor = col_text;
            Console.Write("User");
            Console.SetCursorPosition(2, ypos + 3);
            ConsoleExtensions.RGBForegroundColor = col_cpu_kernel;
            Console.Write("███ ");
            ConsoleExtensions.RGBForegroundColor = col_text;
            Console.Write("Kernel");

            string grid = "─┼";

            for (int j = 0; j < height_perf_cpu; ++j)
            {
                ConsoleExtensions.RGBForegroundColor = col_text;
                Console.SetCursorPosition(PADDING - 7, ypos + height_perf_cpu - j - 1);
                Console.Write($"{100 * j / (height_perf_cpu - 1d),3:F0} %");
                ConsoleExtensions.RGBForegroundColor = col_backg;
                Console.Write('─' + Enumerable.Repeat(grid, (performance_data.Count + 1) / 2).StringConcat());

                if (performance_data.Count % 2 == 0)
                    Console.Write('─');
            }

            ConsoleExtensions.RGBForegroundColor = col_text;
            Console.SetCursorPosition(2, ypos + height_perf_cpu + 1);
            Console.Write("time since start:");

            for (int j = 1; j < performance_data.Count - 8; j += 12)
            {
                ConsoleExtensions.RGBForegroundColor = col_backg;
                Console.SetCursorPosition(PADDING + j + 1, ypos + height_perf_cpu);
                Console.Write('│');
                ConsoleExtensions.RGBForegroundColor = col_text;
                Console.SetCursorPosition(PADDING + j - 3, ypos + height_perf_cpu + 1);

                TimeSpan diff = performance_data[j].time - performance_data[0].time;

                Console.Write(diff switch
                {
                    _ when diff.TotalSeconds < 1 => $"{diff.TotalMilliseconds,7:F3}ms",
                    _ when diff.TotalSeconds < 60 => $"{diff.Seconds:D2}:{diff.TotalMilliseconds % 1000,6:F2}ms",
                    _ when diff.TotalMinutes < 60 => $"{diff.Minutes:D2}:{diff.Seconds:D2}:{diff.Milliseconds:D3}ms",
                    _ => diff.ToString("HH:mm:ss:f")
                });
            }

            // TODO : smthing with Environment.ProcessorCount?

            #endregion
            #region PERFORMANCE : PRINT DATA

            string bars = "_‗▄░▒▓█";

            for (int i = 0; i < performance_data.Count; i++)
            {
                (_, double cpu, _, double kernel, _) = performance_data[i];

                for (int j = 0; j < height_perf_cpu; ++j)
                {
                    Console.SetCursorPosition(PADDING + i, ypos + height_perf_cpu - j - 1);

                    double lo = j / (height_perf_cpu - 1d);
                    double hi = (j + 1) / (height_perf_cpu - 1d);

                    if (cpu < lo)
                        break;
                    else if (cpu < hi)
                    {
                        ConsoleExtensions.RGBForegroundColor = col_cpu_user;
                        ConsoleExtensions.WriteUnderlined(bars[(int)(Math.Min(.99, (hi - cpu) / (hi - lo)) * bars.Length)].ToString());
                    }
                    else if (kernel < lo)
                    {
                        ConsoleExtensions.RGBForegroundColor = col_cpu_user;
                        Console.Write(bars[^1]);
                    }
                    else if (kernel < hi)
                    {
                        ConsoleExtensions.RGBForegroundColor = col_cpu_kernel;
                        ConsoleExtensions.RGBBackgroundColor = col_cpu_user;
                        ConsoleExtensions.WriteUnderlined(bars[(int)(Math.Min(.99, (hi - kernel) / (hi - lo)) * bars.Length)].ToString());
                        Console.Write("\e[0m");
                    }
                    else
                    {
                        ConsoleExtensions.RGBForegroundColor = col_cpu_kernel;
                        Console.Write(bars[^1]);
                    }
                }
            }

            IEnumerable<double> c_total = performance_data.Select(p => p.total * 100);
            IEnumerable<double> c_user = performance_data.Select(p => p.user * 100);
            IEnumerable<double> c_kernel = performance_data.Select(p => p.kernel * 100);
            IEnumerable<double> c_ram = performance_data.Select(p => p.ram / 1024d / 1024d);

            Console.SetCursorPosition(0, ypos + height_perf_cpu + 1);
            ConsoleExtensions.RGBForegroundColor = col_table;
            Console.WriteLine($@"
├────────────┬──────────────┬──────────────┬
│ Category   │ Maximum Load │ Average Load │
├────────────┼──────────────┼──────────────┤
│ Total CPU  │ {c_total.Max(),10:F5} % │ {c_total.Average(),10:F5} % │
│ User CPU   │ {c_user.Max(),10:F5} % │ {c_user.Average(),10:F5} % │
│ Kernel CPU │ {c_kernel.Max(),10:F5} % │ {c_kernel.Average(),10:F5} % │
│ RAM        │ {c_ram.Max(),9:F3} MB │ {c_ram.Average(),9:F3} MB │
└────────────┴──────────────┴──────────────┘
");

            #endregion
        }

        ConsoleExtensions.RGBForegroundColor = RGBAColor.White;
        Console.WriteLine(new string('_', width - 1));
    });

    public static void PrintVersion()
    {
        DataStream hash = DataStream.FromFile(ASM_FILE).Hash(HashFunction.SHA256);
        string[] version = hash.ToDrunkBishop().SplitIntoLines();
        LanguagePack? lang = LanguageLoader.CurrentLanguage;

        version[1] +=  "   AUTOIT3 INTERPRETER";
        version[2] += $"     {lang?["banner.written_by", __module__.Author, __module__.Year]}";
        version[4] += $"   \e[4m{__module__.RepositoryURL}/\e[24m";
        version[6] += $"   {lang?["banner.version"]} {__module__.InterpreterVersion}, {__module__.GitHash}";
        version[7] += $"   {hash.ToHexString()}";
        version[9] += $"   {lang?["banner.drunk_bishop"]}";


        _print_queue.Enqueue(() => Telemetry.Measure(TelemetryCategory.Printing, delegate
        {
            ConsoleExtensions.RGBForegroundColor = RGBAColor.White;

            foreach (string line in version)
                Console.WriteLine(line);
        }));
    }

    private static void PrintHelp(CommandLineParser parser) => _print_queue.Enqueue(() => Telemetry.Measure(TelemetryCategory.Printing, delegate
    {
        string help = parser.RenderHelpMenu(Console.BufferWidth);

        Console.WriteLine(help);
    }));

    /// <summary>
    /// Prints the banner synchronously to STDOUT.
    /// </summary>
    public static void PrintBanner()
    {
        if (CommandLineOptions.VerboseOutput)
            _print_queue.Enqueue(delegate
            {
                LanguagePack? lang = LanguageLoader.CurrentLanguage;

                ConsoleExtensions.RGBForegroundColor = RGBAColor.White;
                Console.WriteLine($@"
                        _       _____ _   ____
             /\        | |     |_   _| | |___ \
            /  \  _   _| |_ ___  | | | |_  __) |
           / /\ \| | | | __/ _ \ | | | __||__ <
          / ____ \ |_| | || (_) || |_| |_ ___) |
         /_/    \_\__,_|\__\___/_____|\__|____/
  _____       _                           _
 |_   _|     | |                         | |
   | |  _ __ | |_ ___ _ __ _ __  _ __ ___| |_ ___ _ __
   | | | '_ \| __/ _ \ '__| '_ \| '__/ _ \ __/ _ \ '__|
  _| |_| | | | ||  __/ |  | |_) | | |  __/ ||  __/ |
 |_____|_| |_|\__\___|_|  | .__/|_|  \___|\__\___|_|
                          | |
                          |_|  {lang?["banner.written_by", __module__.Author, __module__.Year]}
{lang?["banner.version"]} v.{__module__.InterpreterVersion} ({__module__.GitHash})
   {'\e'}[4m{__module__.RepositoryURL}/{'\e'}[24m 
");
                ConsoleExtensions.RGBForegroundColor = RGBAColor.Crimson;
                Console.Write("    ");
                ConsoleExtensions.WriteUnderlined("WARNING!");
                ConsoleExtensions.RGBForegroundColor = RGBAColor.Salmon;
                Console.WriteLine(" This may panic your CPU.\n\n");
            });
    }
}

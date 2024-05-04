using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using System.IO;
using System;

using Unknown6656.AutoIt3.Parser.ExpressionParser;
using Unknown6656.AutoIt3.Localization;
using Unknown6656.AutoIt3.Runtime.Native;
using Unknown6656.AutoIt3.Runtime;

using Unknown6656.Controls.Console;
using Unknown6656.Generics;
using Unknown6656.Imaging;
using Unknown6656.Common;

namespace Unknown6656.AutoIt3.CLI;


public sealed class InteractiveShell
    : IDisposable
{
    public const int MIN_WIDTH = 128;

    internal static readonly string[] KNOWN_OPERATORS = ["+", "-", "*", "/", "+=", "-=", "*=", "/=", "&", "&=", "^", "<=", "<", ">", ">=", "<>", "=", "=="];
    private static readonly string[] DO_NOT_SUGGEST = ["_", "$_", "$GLOBAL"];
    private static readonly Regex REGEX_END_OF_MULTILINE = new(@"^(.*\s+)?(?<sep>_)$", RegexOptions.Compiled);
    private static readonly RGBAColor COLOR_HELP_FG = 0xffff;
    private static readonly RGBAColor COLOR_SEPARATOR = 0xfaaa;
    private static readonly RGBAColor COLOR_PROMPT = 0xffff;
    private static readonly int MAX_SUGGESTIONS = 8;
    private static readonly int MARGIN_RIGHT = 50;
    private static readonly int MARGIN_TOP = 8;
    private int MARGIN_BOTTOM => MAX_SUGGESTIONS + 5; // Suggestions.Count == 0 ? 2 : 5 + Math.Min(MAX_SUGGESTIONS, Suggestions.Count);
    private static int WIDTH => Console.WindowWidth;
    private static int HEIGHT => Console.WindowHeight;

    private readonly string[] _helptext;
    private readonly FileInfo _interactive_tmp_path;

    private Index _current_cursor_pos = ^0;
    private bool _forceredraw;
    private bool _isdisposed;


    /// <summary>
    /// Returns an enumeration of all currently active <see cref="InteractiveShell"/> instances.
    /// </summary>
    public static ConcurrentHashSet<InteractiveShell> Instances { get; } = [];

    /// <summary>
    /// Returns the current history of <see cref="InteractiveShell"/> content (represented by <see cref="ScriptToken"/>s), as well as the corresponding <see cref="InteractiveShellStreamDirection"/>s.
    /// </summary>
    public List<(ScriptToken[] Content, InteractiveShellStreamDirection Stream)> History { get; } = [];

    /// <summary>
    /// Returns a list of formatted textual suggestions for the auto-complete functionality of the interactive AutoIt shell.
    /// </summary>
    public List<(ScriptToken[] Display, string Content)> Suggestions { get; } = [];

    /// <summary>
    /// Contains the current user-provided input.
    /// </summary>
    public string CurrentInput { get; private set; } = "";

    /// <summary>
    /// The current cursor position in characters from the left-hand bound.
    /// </summary>
    public Index CurrentCursorPosition
    {
        get => _current_cursor_pos;
        private set
        {
            _current_cursor_pos = value;

            UpdateSuggestions();
        }
    }

    /// <summary>
    /// The currently selected zero-based index of auto-completion suggestions.
    /// </summary>
    public int CurrentSuggestionIndex { get; private set; }

    /// <summary>
    /// The current zero-based history scroll index. A larger history scroll index suggests a farther scrolling into the past.
    /// </summary>
    public int HistoryScrollIndex { get; private set; }

    /// <summary>
    /// The current AutoIt interpreter instance.
    /// </summary>
    public Interpreter Interpreter { get; }

    /// <summary>
    /// Indicates whether the current interactive shell is running.
    /// </summary>
    public bool IsRunning { get; private set; } = true;

    public bool IsCurrentlyDrawing { get; private set; } = false;

    /// <summary>
    /// Returns the main thread of the current instance of <see cref="InteractiveShell"/>.
    /// </summary>
    public AU3Thread Thread { get; }

    /// <summary>
    /// Returns the global <see cref="VariableScope"/> associated with this interactive shell instance.
    /// </summary>
    public VariableScope Variables { get; }

    /// <summary>
    /// Returns the global stack call frame.
    /// </summary>
    public AU3CallFrame CallFrame { get; }

    /// <summary>
    /// Returns the currently typed <see cref="ScriptToken"/> (or <see langword="null"/>).
    /// </summary>
    public ScriptToken? CurrentlyTypedToken
    {
        get
        {
            ScriptToken[] tokens = ScriptVisualizer.TokenizeScript(CurrentInput);
            int cursor = CurrentCursorPosition.GetOffset(CurrentInput.Length);

            return cursor == 0 ? tokens[0] : tokens.FirstOrDefault(t => t.CharIndex < cursor && cursor <= t.CharIndex + t.TokenLength);
        }
    }


    /// <summary>
    /// Creates a new interactive shell instance using the given AutoIt interpreter.
    /// </summary>
    /// <param name="interpreter">The AutoIt interpreter instance which shall be used by the interactive shell.</param>
    public InteractiveShell(Interpreter interpreter)
    {
        Instances.Add(this);
        Interpreter = interpreter;
        _interactive_tmp_path = new($"0:/temp~{interpreter.Random.NextInt():x8}");
        Thread = interpreter.CreateNewThread();
        CallFrame = Thread.PushAnonymousCallFrame();
        Variables = Thread.CurrentVariableResolver;
        _helptext = CreateHelpText();
    }

    ~InteractiveShell() => Dispose(disposing: false);

    public void Dispose()
    {
        Dispose(disposing: true);

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc cref="Dispose()"/>
    private void Dispose(bool disposing)
    {
        if (!_isdisposed)
        {
            if (disposing)
            {
                Thread.Dispose();

                MainProgram.PausePrinter = false;
            }

            Instances.Remove(this);
            _isdisposed = true;
        }
    }

    /// <summary>
    /// Initializes the interactive shell and returns whether the console is large enough for interactive usage (see <see cref="MIN_WIDTH"/>).
    /// </summary>
    /// <returns>Indication of successfull initialization.</returns>
    public bool Initialize()
    {
        if (WIDTH < MIN_WIDTH)
            try
            {
#pragma warning disable CA1416 // Validate platform compatibility
                Console.WindowWidth = MIN_WIDTH + 1;
#pragma warning restore CA1416
            }
            catch
            {
                MainProgram.PrintError(Interpreter.CurrentUILanguage["error.min_width_interactive", MIN_WIDTH, WIDTH]);

                return false;
            }

        return true;
    }

    /// <summary>
    /// Starts and runs the current interactive shell in a thread-blocking fashion.
    /// <para/>
    /// Consider using <see cref="TaskFactory.StartNew(Action)"/> in order to run the interactive shell on an <see langword="async"/> thread model.
    /// </summary>
    public void Run()
    {
        if (Console.CursorTop > 0)
            Console.Clear();

        MainLoop();
    }

    private void MainLoop()
    {
        try
        {
            MainProgram.PausePrinter = true;

            UpdateSuggestions();

            int hist_count = 0;
            int hist_index = 0;
            int width = 0;
            int height = 0;
            int input_y = -1;

            while (IsRunning)
            {
                IsCurrentlyDrawing = true;

                if (NativeInterop.OperatingSystem.HasFlag(OS.Windows))
                    Console.CursorVisible = false;

                bool candraw = WIDTH >= MIN_WIDTH && HEIGHT >= 32;

                if (width != WIDTH || height != HEIGHT || _forceredraw)
                {
                    Console.Clear();

//#pragma warning disable CA1416 // Validate platform compatibility
//                    if (NativeInterop.OperatingSystem == OS.Windows)
//                        Console.BufferHeight = Math.Max(Console.BufferHeight, Console.WindowHeight + 1);
//#pragma warning restore CA1416

                    if (candraw)
                        RedrawHelp();

                    width = WIDTH;
                    height = HEIGHT;
                    hist_count = -1;
                    hist_index = 0;
                }

                if (!candraw)
                {
                    DrawResizeWarning();

                    continue;
                }

                (int Left, int Top, int InputAreaYOffset) cursor = RedrawInputArea(false);

                if (hist_count != History.Count || hist_index != HistoryScrollIndex || cursor.InputAreaYOffset != input_y || _forceredraw)
                {
                    RedrawHistoryArea(cursor.InputAreaYOffset);
                    RedrawThreadAndVariableWatchers();

                    hist_count = History.Count;
                    hist_index = HistoryScrollIndex;
                    input_y = cursor.InputAreaYOffset;
                }
                else if (Interpreter.Threads.Length > 1 || _forceredraw)
                    RedrawThreadAndVariableWatchers();

                Console.CursorLeft = cursor.Left;
                Console.CursorTop = cursor.Top;

                if (NativeInterop.OperatingSystem.HasFlag(OS.Windows))
                    Console.CursorVisible = true;

                _forceredraw = false;
                IsCurrentlyDrawing = false;

                HandleKeyPress();
            }
        }
        finally
        {
            if (NativeInterop.OperatingSystem.HasFlag(OS.Windows))
                Console.CursorVisible = true;

            Console.Clear();

            MainProgram.PausePrinter = false;
        }
    }

    private void DrawResizeWarning()
    {
        string[] message =
        [
            "  CONSOLE WINDOW TOO SMALL!  ",
            "-----------------------------",
            "  Please resize this window  ",
            $" to a minimum size of {MIN_WIDTH}x32 ",
            "to use the interactive shell.",
            $" Current window size: {WIDTH}x{HEIGHT}"
        ];
        int w = message.Max(l => l.Length);

        ConsoleExtensions.RGBForegroundColor = RGBAColor.Red;

        if (WIDTH <= w || HEIGHT <= message.Length)
        {
            Console.Clear();
            message.Do(Console.WriteLine);
        }
        else
            ConsoleExtensions.WriteBlock(message, (WIDTH - w) / 2, (HEIGHT - message.Length) / 2);
    }

    private void HandleKeyPress()
    {
        int w = WIDTH, h = HEIGHT;

        while (!Console.KeyAvailable)
            if (w != WIDTH || h != HEIGHT)
                return;
            else
                System.Threading.Thread.Sleep(10);

        ConsoleKeyInfo k = Console.ReadKey(true);
        int cursor_pos = CurrentCursorPosition.GetOffset(CurrentInput.Length);

        switch (k.Key)
        {
            // TODO : CTRL+C
            // TODO : CTRL+Z

            case ConsoleKey.RightArrow when k.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (cursor_pos < CurrentInput.Length)
                {
                    ScriptToken? token = CurrentlyTypedToken;

                    if (token is null)
                        CurrentCursorPosition = cursor_pos + 1;
                    else if (token.CharIndex + token.TokenLength == cursor_pos)
                    {
                        CurrentCursorPosition = cursor_pos + 1;
                        token = CurrentlyTypedToken;

                        if (token is { })
                            CurrentCursorPosition = token.CharIndex + token.TokenLength;
                    }
                    else
                        CurrentCursorPosition = token.CharIndex + token.TokenLength;
                }
                break;
            case ConsoleKey.LeftArrow when k.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (cursor_pos > 0)
                    CurrentCursorPosition = CurrentlyTypedToken?.CharIndex ?? cursor_pos - 1;

                break;
            case ConsoleKey.RightArrow:
                if (cursor_pos < CurrentInput.Length)
                    CurrentCursorPosition = cursor_pos + 1;

                break;
            case ConsoleKey.LeftArrow:
                if (cursor_pos > 0)
                    CurrentCursorPosition = cursor_pos - 1;

                break;
            case ConsoleKey.UpArrow when k.Modifiers.HasFlag(ConsoleModifiers.Control):
                CurrentSuggestionIndex = 0;

                break;
            case ConsoleKey.DownArrow when k.Modifiers.HasFlag(ConsoleModifiers.Control):
                CurrentSuggestionIndex = Suggestions.Count - 1;

                break;
            case ConsoleKey.UpArrow:
                if (CurrentSuggestionIndex > 0)
                    --CurrentSuggestionIndex;

                break;
            case ConsoleKey.DownArrow:
                if (CurrentSuggestionIndex < Suggestions.Count - 1)
                    ++CurrentSuggestionIndex;

                break;
            case ConsoleKey.PageDown when k.Modifiers.HasFlag(ConsoleModifiers.Control):
                HistoryScrollIndex = 0;

                break;
            case ConsoleKey.PageUp when k.Modifiers.HasFlag(ConsoleModifiers.Control):
                HistoryScrollIndex = History.Count - 1;

                break;
            case ConsoleKey.PageUp:
                if (HistoryScrollIndex < History.Count - 1)
                    ++HistoryScrollIndex;

                break;
            case ConsoleKey.PageDown:
                if (HistoryScrollIndex > 0)
                    --HistoryScrollIndex;

                break;
            case ConsoleKey.Delete:
                if (cursor_pos < CurrentInput.Length)
                    if (k.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        // TODO : handle ctrl+del
                    }
                    else
                    {
                        CurrentInput = CurrentInput.Remove(cursor_pos, 1);
                        UpdateSuggestions();
                    }

                break;
            case ConsoleKey.Backspace:
                if (cursor_pos > 0)
                    if (k.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        // TODO : handle ctrl+bsp
                    }
                    else
                    {
                        CurrentInput = CurrentInput.Remove(cursor_pos - 1, 1);
                        CurrentCursorPosition = cursor_pos - 1;
                    }

                break;
            case ConsoleKey.Home:
                CurrentCursorPosition = 0;

                break;
            case ConsoleKey.End:
                CurrentCursorPosition = ^0;

                break;
            case ConsoleKey.F5:
                CurrentInput = History.Where(t => t.Stream is InteractiveShellStreamDirection.Input)
                                      .LastOrDefault()
                                      .Content
                                      ?.Select(t => t.Content)
                                      .StringJoin("")
                                      ?? CurrentInput;
                CurrentCursorPosition = CurrentInput.Length;

                break;
            case ConsoleKey.Enter when k.Modifiers.HasFlag(ConsoleModifiers.Shift):
                CurrentInput = CurrentInput[..cursor_pos] + '\n' + CurrentInput[cursor_pos..];
                CurrentCursorPosition = cursor_pos + 1;

                break;
            case ConsoleKey.Enter:
                string trimmed = ScriptScanner.TrimComment(CurrentInput);

                if (trimmed.Match(REGEX_END_OF_MULTILINE, out Match match) && match.Groups["sep"].Index < CurrentCursorPosition.GetOffset(CurrentInput.Length))
                {
                    CurrentInput = CurrentInput[..cursor_pos] + '\n' + CurrentInput[cursor_pos..];
                    CurrentCursorPosition = cursor_pos + 1;
                }
                else
                {
                    ProcessInput();

                    if (IsRunning)
                        UpdateSuggestions();
                }

                break;
            case ConsoleKey.Tab:
                if (CurrentSuggestionIndex >= 0 && CurrentSuggestionIndex < Suggestions.Count)
                {
                    string insertion = Suggestions[CurrentSuggestionIndex].Content;
                    ScriptToken? curr_token = CurrentlyTypedToken;
                    int insertion_index = curr_token?.CharIndex ?? cursor_pos;
                    int deletion_index = curr_token is null ? cursor_pos : curr_token.CharIndex + curr_token.TokenLength;

                    while (deletion_index < CurrentInput.Length && CurrentInput[deletion_index] == ' ')
                        ++deletion_index;

                    if (deletion_index >= CurrentInput.Length)
                        insertion = insertion.TrimEnd();

                    CurrentInput = CurrentInput[..insertion_index] + insertion + CurrentInput[deletion_index..];
                    CurrentCursorPosition = insertion_index + insertion.Length;
                }

                break;
            default:
                if (k.KeyChar is char c and >= ' ' and <= 'ÿ')
                {
                    CurrentInput = CurrentInput[..cursor_pos] + c + CurrentInput[cursor_pos..];
                    CurrentCursorPosition = cursor_pos + 1;
                }

                break;
        }
    }

    private string[] CreateHelpText()
    {
        const int width = 18;

        static string create_key_string(string[] keys)
        {
            List<ScriptToken> tokens = new();
            int offs = 0;

            foreach (string key in keys)
            {
                if (offs > 0)
                    tokens.Add(new(0, offs++, 1, "+", TokenType.Operator));

                tokens.Add(new(0, offs, 1, "[", TokenType.Symbol));
                tokens.Add(new(0, offs + 1, key.Length, key, TokenType.Number));
                tokens.Add(new(0, offs + key.Length + 1, 1, "]", TokenType.Symbol));

                offs += key.Length + 2;
            }

            string padding = new(' ', width - keys.Select(k => $"[{k}]").StringJoin("+").Length);

            return ScriptVisualizer.ConvertToVT100(tokens, false) + padding;
        }
        static string create_command_string(string command)
        {
            string padding = new(' ', width - command.Length);

            return ScriptVisualizer.ConvertToVT100([
                new(0, 0, command.Length, command, TokenType.Keyword)
            ], false) + padding;
        }

        (Union<string[], string> keys, string helptext) key(string[] keys, string helptext) => (keys, helptext);
        (Union<string[], string> keys, string helptext) command(string command, string helptext) => (command, helptext);
        (Union<string[], string> keys, string helptext)[] entries = [
            key(["F5"], "repeat_previous"),
            key(["PAGE UP/DOWN"], "history_scroll"),

            key(["F6"], "repeat_next"),
            key(["ARROW LEFT/RIGHT"], "text_navigation"),

            key(["ENTER"], "execute_current"),
            key(["CTRL", "ARROW L/R"], "text_word_navigation"),

            key(["SHIFT", "ENTER"], "enter_line_break"),
            command("CLEAR", "clear"),

            key(["ARROW UP/DOWN"], "select_suggestion"),
            command("RESET", "reset_all"),

            key(["TAB"], "insert_suggestion"),
            command("EXIT", "exit")
        ];
        StringBuilder help_text = new();
        bool newline = false;

        help_text.Append(Interpreter.CurrentUILanguage["interactive.keyboard_help.header"])
                 .AppendLine(":\n");

        foreach ((Union<string[], string> keys, string helptext) in entries)
        {
            help_text.Append(' ')
                     .Append(keys.Match(create_key_string, create_command_string))
                     .Append("\e[0m ")
                     .Append(Interpreter.CurrentUILanguage["interactive.keyboard_help." + helptext].PadRight(45));

            if (newline)
                help_text.AppendLine();

            newline ^= true;
        }

        return help_text.ToString().SplitIntoLines();
    }

    private void RedrawHelp()
    {
        ConsoleExtensions.RGBForegroundColor = COLOR_HELP_FG;
        Console.CursorTop = 0;
        Console.CursorLeft = 0;

        int width = WIDTH;
        int height = HEIGHT;

        foreach (string line in _helptext)
        {
            Console.Write(line.TrimEnd());
            Console.WriteLine(new string(' ', width - Console.CursorLeft - 1));
        }

        ConsoleExtensions.RGBForegroundColor = COLOR_SEPARATOR;
        ConsoleExtensions.WriteVertical(new string('│', height - MARGIN_TOP), (width - MARGIN_RIGHT - 1, MARGIN_TOP));
        Console.CursorTop = MARGIN_TOP;
        Console.CursorLeft = 0;
        Console.WriteLine(new string('─', width - MARGIN_RIGHT - 1) + '┬' + new string('─', MARGIN_RIGHT));
    }

    private (int Left, int Top, int InputAreaYOffset) RedrawInputArea(bool blink)
    {
        int width = WIDTH;
        int height = HEIGHT;
        int input_area_width = width - MARGIN_RIGHT - 1;
        int line_max_len = input_area_width - 4;
        List<(string content, int orig_index)> input_lines = [];
        int cursor_pos = CurrentCursorPosition.GetOffset(CurrentInput.Length);
        int cursor_pos_x = -1;
        int cursor_pos_y = 0;
        int _tmp_index = 0;

        foreach ((string line, int index) in CurrentInput.SplitIntoLines().WithIndex())
        {
            foreach ((char[] sub_line, int sub_index) in line.PartitionByArraySize(line_max_len).WithIndex())
            {
                input_lines.Add((new string(sub_line), _tmp_index));
                _tmp_index += sub_line.Length;

                if (_tmp_index < cursor_pos)
                    ++cursor_pos_y;
                if (cursor_pos >= _tmp_index - sub_line.Length && cursor_pos <= _tmp_index)
                    cursor_pos_x = cursor_pos - _tmp_index + sub_line.Length;
            }

            ++_tmp_index;
        }

        if (input_lines.Count == 0)
            input_lines.Add(("", 0));

        if (cursor_pos_x < 0)
            cursor_pos_x = 0;

        int input_area_height = height - MARGIN_BOTTOM + 2 - input_lines.Count;

        ConsoleExtensions.WriteBlock(new string(' ', input_area_width * (MARGIN_BOTTOM + input_lines.Count - 1)), 0, input_area_height - 1, input_area_width, MARGIN_BOTTOM + input_lines.Count - 1, true);
        ConsoleExtensions.RGBForegroundColor = COLOR_SEPARATOR;
        Console.CursorLeft = 0;
        Console.CursorTop = input_area_height - 1;
        Console.WriteLine(new string('─', width - MARGIN_RIGHT - 3) + "┴─┤");

        int line_no = 0;

        foreach ((string line, _) in input_lines)
        {
            string txt = (line_no == 0 ? " > " : " ¦ ") + ScriptVisualizer.TokenizeScript(line).ConvertToVT100(false);

            Console.CursorLeft = 0;
            Console.CursorTop = input_area_height + line_no;
            ConsoleExtensions.RGBForegroundColor = COLOR_PROMPT;
            Console.Write(txt);

            if (Console.CursorLeft < input_area_width)
                Console.Write(new string(' ', input_area_width - Console.CursorLeft));

            ConsoleExtensions.RGBForegroundColor = COLOR_SEPARATOR;
            Console.Write('│');

            ++line_no;
        }

        (int l, int t) cursor = (3 + cursor_pos_x, input_area_height + cursor_pos_y);
#pragma warning disable CA1416 // Validate platform compatibility
        bool cursor_visible = NativeInterop.DoPlatformDependent(() => Console.CursorVisible, () => false);
#pragma warning restore CA1416

        if (blink && !cursor_visible)
        {
            Console.CursorTop = cursor.t;
            Console.CursorLeft = cursor.l;

            int idx = CurrentCursorPosition.GetOffset(CurrentInput.Length);

            Console.Write($"\e[7m{(idx < CurrentInput.Length ? CurrentInput[idx] : ' ')}\e[27m");
        }

        if (Suggestions.Count > 0)
        {
            if (CurrentSuggestionIndex > Suggestions.Count)
                CurrentSuggestionIndex = Suggestions.Count - 1;
            else if (CurrentSuggestionIndex < 0)
                CurrentSuggestionIndex = 0;

            int start_index = CurrentSuggestionIndex < MAX_SUGGESTIONS / 2 ? 0
                            : CurrentSuggestionIndex > Suggestions.Count - MAX_SUGGESTIONS / 2 ? Suggestions.Count - MAX_SUGGESTIONS
                            : CurrentSuggestionIndex - MAX_SUGGESTIONS / 2;

            start_index = Math.Max(0, Math.Min(start_index, Suggestions.Count - MAX_SUGGESTIONS));

            ScriptToken[][] suggestions = Suggestions.Skip(start_index)
                                                     .Take(MAX_SUGGESTIONS)
                                                     .ToArray(s => s.Display.ToArray());

            int sugg_width = suggestions.Select(s => s.Sum(t => t.TokenLength)).Append(0).Max() + 4;
            int sugg_left = Math.Min(input_area_width - 4 - sugg_width, cursor_pos_x + 3);
            int i = 0;

            ConsoleExtensions.RGBForegroundColor = COLOR_SEPARATOR;
            Console.CursorTop = cursor.t + 2 + i;
            Console.CursorLeft = sugg_left;
            Console.Write('┌' + new string('─', sugg_width) + '┐');
            Console.CursorTop = cursor.t + 1;
            Console.CursorLeft = cursor_pos_x + 3;

            string indicator = Suggestions.Count > MAX_SUGGESTIONS ? $" {CurrentSuggestionIndex + 1} of {Suggestions.Count} " : "";

            if (sugg_left + 2 + indicator.Length < input_area_width)
                indicator = '│' + indicator;
            else
            {
                Console.CursorLeft -= indicator.Length;
                indicator += '│';
            }

            Console.Write(indicator);
            Console.CursorTop++;
            Console.CursorLeft = 3 + cursor_pos_x;
            Console.Write(cursor_pos_x + 3 == sugg_left ? '├' : cursor_pos_x + 2 == sugg_left + sugg_width ? '┤' : '┴');

            foreach (ScriptToken[] suggestion in suggestions)
            {
                Console.CursorTop = cursor.t + 3 + i;
                Console.CursorLeft = sugg_left;

                if (i == CurrentSuggestionIndex - start_index)
                    Console.Write("{> ");
                else
                    Console.Write("│  ");

                Console.Write(suggestion.ConvertToVT100(false) + COLOR_SEPARATOR.ToVT100ForegroundString());
                Console.Write(new string(' ', sugg_width + sugg_left - Console.CursorLeft - 1));

                if (i == CurrentSuggestionIndex - start_index)
                    Console.Write(" <}");
                else
                    Console.Write("  │");

                ++i;
            }

            Console.CursorLeft = sugg_left;
            Console.CursorTop = cursor.t + 3 + i;
            Console.Write('└' + new string('─', sugg_width) + '┘');
        }

        return (cursor.l, cursor.t, input_area_height);
    }

    private void RedrawHistoryArea(int input_area_y)
    {
        int width = WIDTH;
        Dictionary<int, int> index_map = new() { [0] = 0 };
        string[] history = History.SelectMany((entry, index) =>
        {
            List<string> lines = [];
            string line = COLOR_PROMPT.ToVT100ForegroundString() + (entry.Stream is InteractiveShellStreamDirection.Input ? " > " : "");
            int line_width = width - MARGIN_RIGHT - (entry.Stream is InteractiveShellStreamDirection.Input ? 3 : 0) - 3;
            int len = 0;

            foreach (ScriptToken token in entry.Content.SelectMany(c => c.SplitByLineBreaks()))
                if (token.Type is TokenType.NewLine)
                {
                    lines.Add(line);
                    line = "";
                    len = 0;
                }
                else if (len + token.TokenLength > line_width)
                    foreach (string partial in token.Content.PartitionByArraySize(line_width).ToArray(cs => new string(cs)))
                    {
                        if (line.Length > (entry.Stream is InteractiveShellStreamDirection.Input ? 3 : 0))
                            lines.Add(line);

                        line = (entry.Stream is InteractiveShellStreamDirection.Input ? " ¦ " : "")
                             + ScriptToken.FromString(partial, token.Type).ConvertToVT100(false);
                        len = token.TokenLength;
                    }
                else
                {
                    line += token.ConvertToVT100(false);
                    len += token.TokenLength;
                }

            lines.Add(line);
            index_map[index] = index > 0 ? index_map[index - 1] + lines.Count : 0;

            return lines;
        }).Where(line => !string.IsNullOrEmpty(line)).ToArray();

        int height = input_area_y - MARGIN_TOP - 2;

        //foreach ((int from, int to) in index_map.ToArray())
        //    index_map[History.Count - 1 - from] = to;

        for (int i = index_map.Count - 1; i >= 0; --i)
            if (index_map[i] + height <= history.Length)
            {
                HistoryScrollIndex = Math.Max(0, Math.Min(i, HistoryScrollIndex));

                break;
            }

        bool display_scroll_up = false;
        bool display_scroll_down = false;
        int scroll_height = height - 2;
        int scroll_offset = 0;

        if (history.Length > height)
        {
            display_scroll_up = history.Length - index_map[HistoryScrollIndex] >= height;
            display_scroll_down = index_map[HistoryScrollIndex] > 0;

            double scale = Math.Min(1, (height - 2d) / history.Length);
            double progress = 1 - (double)index_map[HistoryScrollIndex] / (history.Length - height);

            scroll_height = (int)Math.Ceiling(scale * (height - 2));
            scroll_offset = (int)(progress * (height - 2 - scroll_height));
        }
        else
            display_scroll_up = display_scroll_down = false;

        ConsoleExtensions.RGBForegroundColor = COLOR_SEPARATOR;
        ConsoleExtensions.WriteVertical('┬' + new string('│', height) + '┴', width - MARGIN_RIGHT - 3, MARGIN_TOP);
        ConsoleExtensions.WriteVertical(
            (display_scroll_up ? '^' : '-') +
            new string(' ', scroll_offset) +
            new string('█', scroll_height) +
            new string(' ', height - 2 - scroll_height - scroll_offset) +
            (display_scroll_down ? 'v' : '-'),
            width - MARGIN_RIGHT - 2, MARGIN_TOP + 1
        );

        if (history.Length > height)
            history = history[^(index_map[HistoryScrollIndex] + height)..^index_map[HistoryScrollIndex]];

        for (int y = 0; y < height; ++y)
        {
            Console.CursorTop = MARGIN_TOP + 1 + y;
            Console.CursorLeft = 0;

            if (height - history.Length <= y)
                Console.Write(history[history.Length - height + y]);

            Console.Write(new string(' ', width - MARGIN_RIGHT - 3 - Console.CursorLeft));
        }
    }

    private void RedrawThreadAndVariableWatchers()
    {
        AU3Thread[] threads = Interpreter.Threads;
        int left = WIDTH - MARGIN_RIGHT;
        int top = MARGIN_TOP + 1;
        string fg = COLOR_PROMPT.ToVT100ForegroundString();
        List<string> lines = [
            $"{fg}Threads ({threads.Length}):"
        ];

        foreach (AU3Thread thread in threads)
        {
            (RGBAColor color, string status) = thread.IsRunning ? (RGBAColor.Green, "Active") : (RGBAColor.LightSalmon, "Paused/Stopped/Interactive");

            lines.Add($"> Thread {(thread.IsMainThread ? "[Main] " : "")}{ScriptVisualizer.VisualizeScriptAsVT100($"{thread.ThreadID} (0x{thread.ThreadID:x8})", false)}{fg}");
            lines.Add($"  Status: {color.ToVT100ForegroundString()}{status}{fg}");
            lines.Add($"  Call stack ({thread.CallStack.Length}):");

            foreach (AU3CallFrame frame in thread.CallStack)
            {
                string path = frame.CurrentLocation.FullFileName;

                LINQ.TryDo(() => path = Path.GetFileName(path));

                lines.Add($"   - {path}, L.{frame.CurrentLocation.StartLineNumber + 1}");
                lines.Add($"     {frame.CurrentFunction.Name}");
            }
        }

        foreach (string line in lines.Take(MARGIN_TOP))
        {
            Console.CursorLeft = left;
            Console.CursorTop = top++;
            Console.Write(line.PadRight(MARGIN_RIGHT));
        }

        top = Console.CursorTop + 1;

        ConsoleExtensions.RGBForegroundColor = COLOR_SEPARATOR;
        ConsoleExtensions.Write('├' + new string('─', MARGIN_RIGHT), left - 1, top);
        ++top;

        string[] variables = Variables.LocalVariables
                            .Concat(Interpreter.VariableResolver.GlobalVariables)
                            .Select(v =>
                            {
                                string str = $"${v.Name} = {v.Value.ToDebugString(Interpreter)}";

                                if (v.IsConst)
                                    str = "const " + str;
                                if (!v.IsGlobal)
                                    str = "local " + str;

                                return ScriptVisualizer.ConvertToVT100(ScriptVisualizer.TokenizeScript(str), false);
                            })
                            .Take(HEIGHT - top - 2)
                            .ToArray();

        ConsoleExtensions.RGBForegroundColor = COLOR_PROMPT;
        ConsoleExtensions.Write($"Variables ({variables.Length}):", left, top);

        foreach (string line in variables)
        {
            ++top;

            Console.SetCursorPosition(left, top);

            foreach (char c in line)
                if (Console.CursorLeft < WIDTH - 4)
                    Console.Write(c);
                else
                {
                    ConsoleExtensions.RGBForegroundColor = COLOR_PROMPT;
                    Console.Write(" ...");

                    break;
                }

            if (Console.CursorLeft < WIDTH)
                Console.Write(new string(' ', WIDTH - Console.CursorLeft));
        }

        //while (top < HEIGHT)

    }

    /// <summary>
    /// Clears the history of the current interactive shell.
    /// </summary>
    public void Clear()
    {
        History.Clear();
        HistoryScrollIndex = 0;
    }

    public void Reset()
    {
        History.Clear();
        HistoryScrollIndex = -1;
        CurrentCursorPosition = 0;
        CurrentInput = "";

        Variables.DestroyAllVariables(true);
        Interpreter.ResetGlobalVariableScope();

        Clear();

        _forceredraw = true;
    }

    /// <summary>
    /// Requests the interactive shell to be exited.
    /// </summary>
    public void Exit() => IsRunning = false;

    private void ProcessInput()
    {
        if (!string.IsNullOrWhiteSpace(CurrentInput))
        {
            string input = CurrentInput.Trim().SplitIntoLines().Select(ScriptScanner.TrimComment).Aggregate(new List<string>(), (list, elem) =>
            {
                if (list.Count > 0 && list[^1].Match(REGEX_END_OF_MULTILINE, out Match match))
                    list[^1] = list[^1][..match.Groups["sep"].Index] + elem;
                else
                    list.Add(elem);

                return list;
            }).StringJoinLines();

            CurrentInput = "";
            CurrentCursorPosition = 0;
            History.Add((ScriptVisualizer.TokenizeScript(input), InteractiveShellStreamDirection.Input));

            Union<InterpreterError, ScannedScript> scanned = Interpreter.ScriptScanner.ProcessScriptFile(_interactive_tmp_path, input);
            FunctionReturnValue? result = Variant.Zero;

            if (scanned.Is(out ScannedScript? script))
                result = Interpreter.Run(script, InterpreterRunContext.Interactive);
            else if (scanned.Is(out InterpreterError? error))
                result = error;

            if (result.IsFatal(out InterpreterError? e))
                History.Add((new[] { ScriptToken.FromString(e.Message, TokenType.UNKNOWN) }, InteractiveShellStreamDirection.Error));
            else
            {
                List<ScriptToken> tokens = [];

                result.IsSuccess(out Variant value, out Variant? extended);
                tokens.AddRange(ScriptVisualizer.TokenizeScript(value.ToDebugString(Interpreter)));

                if (result.IsError(out int error))
                    tokens.AddRange(ScriptVisualizer.TokenizeScript($"\n@error: {error} (0x{error:x8})"));

                if (extended is Variant ext)
                    tokens.AddRange(ScriptVisualizer.TokenizeScript($"\n@extended: {ext.ToDebugString(Interpreter)}"));

                History.Add((tokens.ToArray(), InteractiveShellStreamDirection.Output));
            }
        }
    }

    internal void OnCancelKeyPressed() => Task.Factory.StartNew(async delegate
    {
        while (IsCurrentlyDrawing)
            await Task.Delay(0);

        int ctop = Console.CursorTop;
        int cleft = Console.CursorLeft;

        string vt100_prefix = "\e[1;91m";
        string token_exit = new ScriptToken(0, 0, 4, "EXIT", TokenType.Keyword).ConvertToVT100(false) + vt100_prefix;
        string token_ctrlc = ScriptVisualizer.ConvertToVT100([
            new(0, 0, 1, "[", TokenType.Symbol),
            new(0, 1, 4, "CTRL", TokenType.Number),
            new(0, 5, 1, "]", TokenType.Symbol),
            new(0, 6, 1, "+", TokenType.Operator),
            new(0, 7, 1, "[", TokenType.Symbol),
            new(0, 8, 1, "C", TokenType.Number),
            new(0, 9, 1, "]", TokenType.Symbol),
        ], false) + vt100_prefix;
        string text = Interpreter.CurrentUILanguage["interactive.ctrl_c_detected", token_ctrlc, token_exit];

        Console.Write($"{vt100_prefix}{text}\e[0m");
        Console.CursorTop = ctop;
        Console.CursorLeft = cleft;
    });

    /// <summary>
    /// Updates the list of auto-complete suggestions.
    /// </summary>
    public void UpdateSuggestions()
    {
        OS os = NativeInterop.OperatingSystem;
        ScriptToken? curr_token = CurrentlyTypedToken;
        string? filter = curr_token?.Content[..(CurrentCursorPosition.GetOffset(CurrentInput.Length) - curr_token.CharIndex)];

        if (string.IsNullOrWhiteSpace(filter))
            filter = null;

        List<(ScriptToken[] tokens, string content)> suggestions =
        [
            //(new[] { ScriptToken.FromString("CLEAR", TokenType.Keyword) }, "clear"),
            //(new[] { ScriptToken.FromString("RESET", TokenType.Keyword) }, "reset"),
        ];

        void add_suggestions(IEnumerable<string> suggs, TokenType type) => suggs.Select(s => (new[] { ScriptToken.FromString(s, type) }, s)).AppendToList(suggestions);
        bool suggest_all = string.IsNullOrEmpty(CurrentInput) || curr_token?.Type is null or TokenType.UNKNOWN or TokenType.Whitespace or TokenType.NewLine;
        string to_debug_string(Variant value)
        {
            string str = value.ToDebugString(Interpreter);

            return str.Length > WIDTH - MARGIN_RIGHT - 30 ? str[..(WIDTH - MARGIN_RIGHT - 33)] + " ..." : str;
        }

        if (suggest_all || curr_token?.Type is TokenType.Directive)
             add_suggestions(["#include"], TokenType.Directive);

        // if (suggest_all || curr_token?.Type is TokenType.DirectiveOption)
        //     ; // TODO

        if (suggest_all || curr_token?.Type is TokenType.Operator)
            add_suggestions(KNOWN_OPERATORS, TokenType.Operator);

        if (suggest_all || curr_token?.Type is TokenType.Macro)
        {
            KnownMacro[] macros = [.. Interpreter.MacroResolver.KnownMacros];
            int name_length = macros.Max(m => m.Name.Length);

            macros.Select(macro =>
            {
                bool supported = macro.Metadata.SupportsPlatfrom(os);
                bool deprecated = macro.Metadata.IsDeprecated;
                string name = macro.ToString();

                if (deprecated)
                    return (new[] { ScriptToken.FromString($"{Interpreter.CurrentUILanguage["interactive.deprecated", os]} {name}", TokenType.UNKNOWN) }, name);
                else if (!supported)
                    return (new[] { ScriptToken.FromString($"{Interpreter.CurrentUILanguage["interactive.unsupported_platform", os]} {name}", TokenType.UNKNOWN) }, name);
                else
                    return (ScriptVisualizer.TokenizeScript($"@{macro.Name.PadRight(name_length)} : {to_debug_string(macro.GetValue(CallFrame))}"), name);

            }).AppendToList(suggestions);
        }

        if (suggest_all || curr_token?.Type is TokenType.Variable)
        {
            Variable[] vars = [.. Variables.LocalVariables, .. Interpreter.VariableResolver.GlobalVariables];
            int name_length = vars.Select(v => v.Name.Length).Append(0).Max();

            vars.Select(variable =>
            {
                string name = '$' + variable.Name.PadRight(name_length);
                string type = variable.Value.Type.ToString().PadRight(9);
                string value = to_debug_string(variable.Value);
                IEnumerable<ScriptToken> tokens = new[] {
                    ScriptToken.FromString(name, TokenType.Variable),
                    ScriptToken.FromString(" : ", TokenType.Operator),
                    ScriptToken.FromString(type, TokenType.Identifier),
                    ScriptToken.FromString(" = ", TokenType.Operator),
                };

                if (variable.IsConst)
                    tokens = tokens.Append(ScriptToken.FromString("CONST ", TokenType.Keyword));

                return (tokens.Concat(ScriptVisualizer.TokenizeScript(value)).ToArray(), name);
            }).AppendToList(suggestions);
        }

        if (suggest_all || curr_token?.Type is TokenType.Keyword or TokenType.Identifier or TokenType.FunctionCall)
        {
            add_suggestions(ScriptFunction.RESERVED_NAMES.Except(DO_NOT_SUGGEST), TokenType.Keyword);

            ScriptFunction[] functions = Interpreter.ScriptScanner.CachedFunctions.Where(f => !string.IsNullOrWhiteSpace(f.Name)).ToArray();
            int name_length = functions.Max(f => f.Name.Length);

            functions.Select(function =>
            {
                bool supported = function.Metadata.SupportsPlatfrom(os);
                bool deprecated = function.Metadata.IsDeprecated;   
                List<ScriptToken> tokens = [
                    ScriptToken.FromString(function.Name, TokenType.FunctionCall),
                    ScriptToken.FromString(new(' ', name_length - function.Name.Length + 1), TokenType.Whitespace),
                    ScriptToken.FromString(": ", TokenType.Symbol),
                ];

                if (function is NativeFunction native)
                {
                    tokens.AddRange(ScriptVisualizer.TokenizeScript([
                        $"({Interpreter.CurrentUILanguage["interactive.argument_count", function.ParameterCount.MinimumCount, function.ParameterCount.MaximumCount]}) "
                    ]));
                    tokens.Add(ScriptToken.FromString(function is NETFrameworkFunction ? "[.NET]" : Interpreter.CurrentUILanguage["interactive.native"], TokenType.Comment));
                }
                else if (function is AU3Function au3)
                {
                    StringBuilder sb = new();

                    if (au3.IsCached)
                        sb.Append("CACHED ");

                    if (au3.IsVolatile)
                        sb.Append("VOLATILE ");

                    sb.Append('(');

                    foreach ((AST.PARAMETER_DECLARATION param, int i) in au3.Parameters.WithIndex())
                    {
                        if (i > 0)
                            sb.Append(", ");

                        if (i >= function.ParameterCount.MinimumCount)
                            sb.Append('[');

                        sb.Append(param);

                        if (i >= function.ParameterCount.MinimumCount)
                            sb.Append(']');
                    }

                    sb.Append(')');

                    tokens.AddRange(ScriptVisualizer.TokenizeScript(sb.ToString()));
                }

                if (deprecated)
                {
                    tokens.Add(ScriptToken.FromString(" ", TokenType.Whitespace));
                    tokens.Add(ScriptToken.FromString(Interpreter.CurrentUILanguage["interactive.deprecated"], TokenType.UNKNOWN));
                }
                else if (!supported)
                {
                    tokens.Add(ScriptToken.FromString(" ", TokenType.Whitespace));
                    tokens.Add(ScriptToken.FromString(Interpreter.CurrentUILanguage["interactive.unsupported_platform", os], TokenType.UNKNOWN));
                }

                return (tokens.ToArray(), function.Name);
            }).AppendToList(suggestions);
        }

        Suggestions.Clear();
        Suggestions.AddRange(from s in suggestions.DistinctBy(s => s.content)
                             where filter is null || s.content.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
                             orderby s.tokens[0].Type, s.content ascending
                             select s);
        CurrentSuggestionIndex = Math.Min(CurrentSuggestionIndex, Suggestions.Count - 1);
    }

    /// <summary>
    /// Submits the given <paramref name="message"/> to be printed to the history while formatting it as <see cref="TokenType.Comment"/> and <see cref="InteractiveShellStreamDirection.Output"/>.
    /// </summary>
    /// <param name="message">The message to be printed to the interactive shell.</param>
    public void SubmitPrint(string message) => History.Add((new[] { ScriptToken.FromString(message, TokenType.Comment) }, InteractiveShellStreamDirection.Output));
}

/// <summary>
/// An enumeration of known interactive shell stream directions.
/// </summary>
public enum InteractiveShellStreamDirection
{
    /// <summary>
    /// Represents the input stream, i.e. what the user enters into the interactive shell.
    /// </summary>
    Input,
    /// <summary>
    /// Represents the output stream, e.g. result values of code executions.
    /// </summary>
    Output,
    /// <summary>
    /// Represents the error stream, i.e. occurrences of exceptions and parsing errors.
    /// </summary>
    Error,
}

/* ┌─┬┐
╭╮│├┼┤
╰╯└┴┘
*      +--------------------------------------------+
*      | help text                                  |
*      +--------------------------------+-+---------+
*      |                                |^|         |
*      |     ^ ^ ^ ^ ^ ^ ^ ^ ^ ^ ^      | | thread  |
*      |      text moves upwards        | | monitor |
*      |                                | +---------+
*      | > input                        | |         |
*      | result                         | | variable|
*      | > input                        | | monitor |
*      | result                         |#|         |
*      | > input                        |#|         |
*      | result                         |#|         |
*      | > input                        |#|         |
*      | result                         |V|         |
*      +--------------------------------+-+         |
*      | > ~~~~~~~~~ I                    |         |
*      |             |--------------+     |         |
*      |             | autocomplete |     |         |
*      |             | suggestions  |     |         |
*      |             +--------------+     |         |
*      +----------------------------------+---------+
*/

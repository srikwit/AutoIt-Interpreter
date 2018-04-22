﻿using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System;

using Microsoft.FSharp.Collections;

using AutoItInterpreter.Preprocessed;
using AutoItInterpreter.PartialAST;
using AutoItExpressionParser;

/* ====================== GLOBAL VARIABLE TRANSFORMATION =======================

 AutoIt .--
        |
        | Global $a
        | Global Const $b = 1
        | Const $c = "42", $d = func()
        | Global $e[5] = [1, "2", False, 4.2, "0"]
        | $f = 88
        | Dim $g
        |
        '--
     C# .--
        |
        |   static class globals
        |   {
        |       static Variant __global_error = default;
        |
        |       static Variant _a;
        |       static Variant _b; // use 'readonly' ?
        |       static Variant _c;
        |       static Variant _d;
        |       static Variant[] _e;
        |       static Variant _f;
        |       static Variant _g;
        |
        |       static void init()
        |       {
        |           globals._a = default;
        |           globals._b = 1m;
        |           globals._c = "42";
        |
        |           __func(ref _d, ref __global_error);
        |
        |           globals._e = new Variant[5];
        |           globals._e[0] = 1m;
        |           globals._e[1] = "2";
        |           globals._e[2] = false;
        |           globals._e[3] = 4.2m;
        |           globals._e[4] = "0";
        |
        |           globals._f = 88m;
        |           globals._g = default;
        |       }
        |
        |       ...
        |   }
        |
        '--
*/
/* ============================ FUNCTION GENERATION ============================

 AutoIt .--
        |
        |   Func MyFunc($a, ByRef $b, Const $c, Const ByRef $d, $e = 315, $f = "42")
        |       $b = $a + $c 
        |       MsgBox($d)
        |       $b *= $e - $f
        |       Return $a
        |   EndFunc
        |
        |   $x = "99"
        |   $y = "abc"
        |   $z = 0.88
        |   $w = MyFunc(0, $x, 1, $y, $z)
        |
        '--
     C# .--
        |
        |   void __myfunc(Variant _a, ref Variant _b, Variant _c, ref Variant _d, Variant _e, Variant _f, ref Variant @return, ref Variant @error)
        |   {
        |       _b = _a + _c;
        |       Util.MsgBox(_d);
        |       _b *= _e - _f;
        |       @return = _a;
        |   }
        |
        |   Variant _x = "99";
        |   Variant _y = "abc";
        |   Variant _z = 0.88m;
        |   Variant _w = default;
        |
        |   __myfunc((Variant)0m, ref _x, (Variant)1m, ref _y, _z, (Variant)"42", ref _w, ref __global_error);
        |   // do some error checking 
        |
        '--
 */

namespace AutoItInterpreter
{
    using MULTI_EXPRESSIONS = FSharpList<ExpressionAST.MULTI_EXPRESSION>;
    using static ExpressionAST;
    using static ControlBlock;


    public delegate void ErrorReporter(string name, params object[] args);

    public sealed class Interpreter
    {
        private static Dictionary<ControlBlock, string> ClosingInstruction { get; } = new Dictionary<ControlBlock, string>
        {
            [__NONE__] = "EndFunc",
            [If] = "EndIf",
            [ElseIf] = "EndIf",
            [Else] = "EndIf",
            [Select] = "EndSelect",
            [Switch] = "EndSwitch",
            [For] = "Next",
            [While] = "WEnd",
            [Do] = "Until ...",
            [With] = "EndWith",
        };
        private InterpreterSettings Settings { get; }
        public InterpreterContext RootContext { get; }
        public bool UseVerboseOutput { get; }
        internal Language Language { get; }


        public Interpreter(string path, Language lang, InterpreterSettings settings, bool verbose)
        {
            RootContext = new InterpreterContext(path);
            Language = lang;
            Settings = settings;
            UseVerboseOutput = verbose;
            Settings.IncludeDirectories = Settings.IncludeDirectories.Select(x => x.Trim().Replace('\\', '/')).Distinct().ToArray();

            if (RootContext.Content is null)
                throw new FileNotFoundException(lang["errors.general.file_nopen"], path);
        }

        public void DoMagic()
        {
            InterpreterState state = InterpretScript(RootContext, Settings, Language, UseVerboseOutput);

            ParseExpressionAST(state, Settings);







            if (UseVerboseOutput)
            {
                DebugPrintUtil.DisplayCodeAndErrors(RootContext.SourcePath, state);
                DebugPrintUtil.DisplayPartialAST(state, Settings);
            }
            else
                foreach (InterpreterError err in state.Errors)
                    Console.WriteLine($"[{(err.IsFatal ? " ERROR " : "WARNING")}]  {err}");
        }

        private static InterpreterState InterpretScript(InterpreterContext context, InterpreterSettings settings, Language lang, bool verbose)
        {
            List<(string Line, int[] OriginalLineNumbers, FileInfo File)> lines = new List<(string, int[], FileInfo)>();
            PreInterpreterState pstate = new PreInterpreterState
            {
                Language = lang,
                CurrentContext = context,
                GlobalFunction = new FunctionScope(new DefinitionContext(context.SourcePath, -1))
            };
            int locindx = 0;

            lines.AddRange(FetchLines(pstate, context));

            while (locindx < lines.Count)
            {
                string Line = lines[locindx].Line;
                DefinitionContext defcntx = new DefinitionContext(
                    lines[locindx].File,
                    lines[locindx].OriginalLineNumbers[0],
                    lines[locindx].OriginalLineNumbers.Length > 1 ? (int?)lines[locindx].OriginalLineNumbers.Last() : null
                );
                void err(string name, params object[] args) => pstate.ReportKnownError(name, defcntx, args);

                if (Line.StartsWith('#'))
                {
                    string path = ProcessDirective(Line.Substring(1), pstate, settings, err);

                    try
                    {
                        FileInfo inclpath = path.Length > 0 ? new FileInfo(path) : default;

                        if (inclpath?.Exists ?? false)
                            using (StreamReader rd = inclpath.OpenText())
                            {
                                lines.RemoveAt(locindx);
                                lines.InsertRange(locindx, FetchLines(pstate, new InterpreterContext(inclpath)));

                                --locindx;
                            }
                    }
                    catch
                    {
                        err("errors.preproc.include_nfound", path);
                    }
                }
                else if (ProcessFunctionDeclaration(Line, defcntx, pstate, err))
                    (pstate.CurrentFunction is FunctionScope f ? f : pstate.GlobalFunction).Lines.Add((Line, defcntx));

                ++locindx;
            }

            if (verbose)
                DebugPrintUtil.DisplayPreState(pstate);

            Dictionary<string, FUNCTION> ppfuncdir = PreProcessFunctions(pstate);
            InterpreterState state = InterpreterState.Convert(pstate);

            foreach (string func in ppfuncdir.Keys)
                state.Functions[func] = ppfuncdir[func];

            return state;
        }

        private static (string Content, int[] OriginalLineNumbers, FileInfo File)[] FetchLines(PreInterpreterState state, InterpreterContext context)
        {
            string raw = context.Content;

            List<(string, int[])> lines = new List<(string, int[])>();
            List<int> lnmbrs = new List<int>();
            bool comment = false;
            string prev = "";
            int lcnt = 0;

            foreach (string line in raw.Replace("\r\n", "\n").Split('\n'))
            {
                string tline = line.Trim();

                if (tline.Match(@"^\#(comments\-start|cs)", out _))
                    comment = true;
                else if (tline.Match(@"^\#(comments\-end|ce)", out _))
                    comment = false;
                else if (!comment)
                {
                    if (tline.Match(@"\;[^\""]*$", out Match m))
                        tline = tline.Remove(m.Index).Trim();
                    else if (tline.Match(@"^([^\""\;]*\""[^\""]*\""[^\""\;]*)*(?<cmt>\;).*$", out m))
                        tline = tline.Remove(m.Groups["cmt"].Index).Trim();

                    if (tline.Match(@"\s+_\s*$", out m))
                    {
                        prev = $"{prev} {tline.Remove(m.Index).Trim()}";
                        lnmbrs.Add(lcnt);
                    }
                    else
                    {
                        lnmbrs.Add(lcnt);
                        lines.Add(($"{prev} {tline}".Trim(), lnmbrs.ToArray()));
                        lnmbrs.Clear();

                        prev = "";
                    }
                }

                ++lcnt;
            }

            if (comment)
                state.ReportKnownWarning("warnings.preproc.no_closing_comment", new DefinitionContext(context.SourcePath, lcnt));

            lcnt = 0;

            while (lcnt < lines.Count)
            {
                int[] lnr = lines[lcnt].Item2;

                if (lines[lcnt].Item1.Match(@"^if\s+(?<cond>.+)\s+then\s+(?<iaction>.+)\s+else\s+(?<eaction>)$", out Match m))
                {
                    lines.RemoveAt(lcnt);
                    lines.AddRange(new(string, int[])[]
                    {
                        ($"If ({m.Get("cond")}) Then", lnr),
                        (m.Get("iaction"), lnr),
                        ("Else", lnr),
                        (m.Get("eaction"), lnr),
                        ("EndIf", lnr)
                    });
                }
                else if (lines[lcnt].Item1.Match(@"^if\s+(?<cond>.+)\s+then\s+(?<then>.+)$", out m))
                {
                    lines.RemoveAt(lcnt);
                    lines.AddRange(new(string, int[])[]
                    {
                        ($"If ({m.Get("cond")}) Then", lnr),
                        (m.Get("then"), lnr),
                        ("EndIf", lnr)
                    });
                }

                ++lcnt;
            }

            return (from ln in lines
                    where ln.Item1.Length > 0
                    select (ln.Item1, ln.Item2, context.SourcePath)).ToArray();
        }

        private static Dictionary<string, FUNCTION> PreProcessFunctions(PreInterpreterState state)
        {
            Dictionary<string, FUNCTION> funcdir = new Dictionary<string, FUNCTION>
            {
                [PreInterpreterState.GLOBAL_FUNC_NAME] = PreProcessFunction(state.GlobalFunction, PreInterpreterState.GLOBAL_FUNC_NAME, true)
            };

            foreach (string name in state.Functions.Keys)
                if (name != PreInterpreterState.GLOBAL_FUNC_NAME)
                    funcdir[name] = PreProcessFunction(state.Functions[name], name, false);

            return funcdir;

            FUNCTION PreProcessFunction(FunctionScope func, string name, bool global)
            {
                Stack<(Entity Entity, ControlBlock CB)> eblocks = new Stack<(Entity, ControlBlock)>();
                var lines = func.Lines.ToArray();
                int locndx = 0;

                Entity curr = new FUNCTION(name, global, func) { DefinitionContext = func.Context };

                eblocks.Push((curr, __NONE__));

                while (locndx < lines.Length)
                {
                    DefinitionContext defctx = lines[locndx].Context;
                    void err(string msg, bool fatal, params object[] args)
                    {
                        int errnum = Language.GetErrorNumber(msg);

                        msg = state.Language[msg, args];

                        if (fatal)
                            state.ReportError(msg, defctx, errnum);
                        else
                            state.ReportWarning(msg, defctx, errnum);
                    }
                    void Conflicts(Action f, params ControlBlock[] cbs)
                    {
                        if (cbs.Contains(eblocks.Peek().CB))
                            err("errors.preproc.block_confl", true, string.Join("', '", cbs));
                        else
                            f();
                    }
                    void TryCloseBlock(ControlBlock ivb)
                    {
                        (Entity et, ControlBlock CB) = eblocks.Pop();

                        if (CB == __NONE__)
                        {
                            eblocks.Push((et, CB));

                            return;
                        }
                        else if ((ivb == IfElifElseBlock)
                              || (CB == ivb)
                              || ((CB == Case) && (ivb == Select || ivb == Switch)
                              || (ClosingInstruction.ContainsKey(CB) && ClosingInstruction.ContainsKey(ivb) && ClosingInstruction[CB] == ClosingInstruction[ivb])))
                        {
                            curr = eblocks.Peek().Entity;

                            return;
                        }
                        else
                            eblocks.Push((et, CB));

                        if (CB == __NONE__)
                            err("errors.preproc.block_invalid_close", true, ivb);
                        else
                            err("errors.preproc.block_conflicting_close", true, CB, ClosingInstruction[ivb]);
                    }
                    void ForceCloseBlock()
                    {
                        eblocks.Pop();

                        curr = eblocks.Peek().Entity;
                    }
                    int AnyParentCount(params ControlBlock[] cb) => eblocks.Count(x => cb.Contains(x.CB));
                    void Append(params Entity[] es)
                    {
                        foreach (Entity e in es)
                        {
                            e.DefinitionContext = defctx;
                            e.Parent = curr;

                            curr.Append(e);
                        }
                    }
                    T OpenBlock<T>(ControlBlock cb, T e) where T : Entity
                    {
                        e.Parent = curr;
                        e.DefinitionContext = defctx;

                        curr.Append(e);
                        curr = e;

                        eblocks.Push((e, cb));

                        return e;
                    }

                    string line = lines[locndx].Line;

                    line.Match(new(string, ControlBlock[], Action<Match>)[]
                    {
                        (@"^(?<optelse>else)?if\s+(?<cond>.+)\s+then$", new[] { Switch, Select }, m =>
                        {
                            string cond = m.Get("cond").Trim();

                            if (m.Get("optelse").Length > 0)
                            {
                                ControlBlock cb = eblocks.Peek().CB;

                                if (cb == If || cb == ElseIf)
                                {
                                    ForceCloseBlock();

                                    IF par = (IF)curr;
                                    ELSEIF_BLOCK b = OpenBlock(ElseIf, new ELSEIF_BLOCK(par, cond));

                                    par.AddElseIf(b);
                                }
                                else
                                    err("errors.preproc.misplaced_elseif", true);
                            }
                            else
                            {
                                IF b = OpenBlock(IfElifElseBlock, new IF(curr));
                                IF_BLOCK ib = OpenBlock(If, new IF_BLOCK(b, cond));

                                b.SetIf(ib);
                            }
                        }),
                        (@"^(else)?if\s+.+$", new[] { Switch, Select }, _ => err("errors.preproc.missing_then", true)),
                        ("^else$", new[] { Switch, Select }, _ =>
                        {
                            ControlBlock cb = eblocks.Peek().CB;

                            if (cb == If || cb == ElseIf)
                            {
                                ForceCloseBlock();

                                IF par = (IF)curr;
                                ELSE_BLOCK eb = OpenBlock(Else, new ELSE_BLOCK(par));

                                par.SetElse(eb);
                            }
                            else
                                err("errors.preproc.misplaced_else", true);
                        }),
                        ("^endif$", new[] { Switch, Select }, _ =>
                        {
                            TryCloseBlock(If);
                            TryCloseBlock(IfElifElseBlock);
                        }),
                        ("^select$", new[] { Switch, Select }, _ => OpenBlock(Select, new SELECT(curr))),
                        ("^endselect$", new[] { Switch }, _ =>
                        {
                            if (eblocks.Peek().CB == Case)
                                TryCloseBlock(Case);

                            TryCloseBlock(Select);
                        }),
                        (@"^switch\s+(?<cond>.+)$", new[] { Switch, Select }, m => OpenBlock(Switch, new SWITCH(curr, m.Get("cond")))),
                        ("^endswitch$", new[] { Select }, _ =>
                        {
                            if (eblocks.Peek().CB == Case)
                                TryCloseBlock(Case);

                            SWITCH sw = eblocks.Peek().Entity as SWITCH;

                            sw.Cases.AddRange(sw.RawLines.Select(x => x as SWITCH_CASE));

                            TryCloseBlock(Switch);
                        }),
                        (@"^case\s+(?<cond>.+)$", new ControlBlock[0], m =>
                        {
                            var b = eblocks.Peek();
                            string cond = m.Get("cond");

                            if (b.CB == Case)
                            {
                                TryCloseBlock(Case);

                                b = eblocks.Peek();
                            }

                            if (b.CB == Switch)
                                OpenBlock(Case, new SWITCH_CASE(null, cond));
                            else if (b.CB == Select)
                                OpenBlock(Case, new SELECT_CASE(null, cond));
                            else
                            {
                                err("errors.preproc.misplaced_case", true);

                                return;
                            }
                        }),
                        ("^continuecase$", new[] { Switch, Select }, _ =>
                        {
                            if (AnyParentCount(Switch, Select) > 1)
                                Append(new CONTINUECASE(curr));
                            else
                                err("errors.preproc.misplaced_continuecase", true);
                        }),
                        (@"^for\s+(?<var>\$[a-z_]\w*)\s*\=\s*(?<start>.+)\s+to\s+(?<stop>.+)(\s+step\s+(?<step>.+))$", new[] { Switch, Select }, m => OpenBlock(For, new FOR(curr, m.Get("var"), m.Get("start"), m.Get("stop"), m.Get("step")))),
                        (@"^for\s+(?<var>\$[a-z_]\w*)\s*\=\s*(?<start>.+)\s+to\s+(?<stop>.+)$", new[] { Switch, Select }, m => OpenBlock(For, new FOR(curr, m.Get("var"), m.Get("start"), m.Get("stop")))),
                        (@"^for\s+(?<var>\$[a-z_]\w*)\s+in\s+(?<range>.+)$", new[] { Switch, Select }, m => OpenBlock(For, new FOREACH(curr, m.Get("var"), m.Get("range")))),
                        ("^next$", new[] { Switch, Select }, _ => TryCloseBlock(For)),
                        (@"^while\s+(?<cond>.+)$", new[] { Switch, Select }, m => OpenBlock(While, new WHILE(curr, m.Get("cond")))),
                        (@"^exitloop(\s+(?<levels>\-?[0-9]+))?$", new[] { Switch, Select }, m =>
                        {
                            int cnt = AnyParentCount (For, Do, While);

                            if (cnt == 0)
                                err("errors.preproc.misplaced_exitloop", true);
                            else if (int.TryParse(m.Get("levels"), out int levels) && levels > 0)
                            {
                                if (levels > cnt)
                                {
                                    err("warnings.preproc.exit_level_truncated", false, levels, cnt);

                                    levels = cnt;
                                }

                                Append(new BREAK(curr, levels));
                            }
                            else
                                err("warnings.preproc.exit_level_invalid", false, m.Get("levels"));
                        }),
                        (@"^continueloop(\s+(?<levels>\-?[0-9]+))?$", new[] { Switch, Select }, m =>
                        {
                            int cnt = AnyParentCount (For, Do, While);

                            if (cnt == 0)
                                err("errors.preproc.misplaced_continueloop", true);
                            else if (int.TryParse(m.Get("levels"), out int levels) && levels > 0)
                            {
                                if (levels > cnt)
                                {
                                    err("warnings.preproc.continue_level_truncated", false, levels, cnt);

                                    levels = cnt;
                                }

                                Append(new CONTINUE(curr, levels));
                            }
                            else
                                err("warnings.preproc.continue_level_invalid", false, m.Get("levels"));
                        }),
                        ("^wend$", new[] { Switch, Select }, _ => TryCloseBlock(While)),
                        ("^do$", new[] { Switch, Select }, _ => OpenBlock(Do, new DO_UNTIL(null))),
                        (@"^until\s+(?<cond>.+)$", new[] { Switch, Select }, m =>
                        {
                            (curr as DO_UNTIL)?.SetCondition(m.Get("cond"));

                            TryCloseBlock(Do);
                        }),
                        (@"^with\s+(?<expr>.+)$", new[] { Switch, Select }, m => OpenBlock(With, new WITH(curr, m.Get("expr")))),
                        ("^endwith$", new[] { Switch, Select }, _ => TryCloseBlock(Do)),
                        (@"(?<modifier>(static|const)\s+(local|global|dim)?|(global|local|dim)\s+(const|static)?)\s+(?<expr>.+)", new[] { Switch, Select }, m =>
                        {
                            string[] modf = m.Get("modifier").ToLower().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            string expr = m.Get("expr");

                            if (modf.Contains("local") && global)
                                err("errors.preproc.invalid_local", true);
                            else if (modf.Contains("global") && !global)
                                err("errors.preproc.invalid_global", true);

                            Append(new DECLARATION(curr, expr, modf));
                        }),
                        (@"^return\s+(?<val>.+)$", new[] { Switch, Select }, m => Append(new RETURN(curr, m.Get("val")))),
                        (".*", new[] { Switch, Select }, _ => Append(new RAWLINE(curr, line))),
                    }.Select<(string, ControlBlock[], Action<Match>), (string, Action<Match>)>(x => (x.Item1, m => Conflicts(() => x.Item3(m), x.Item2))).ToArray());

                    ++locndx;
                }

                List<string> ci = new List<string>();
                ControlBlock pb;

                while ((pb = eblocks.Pop().CB) != __NONE__)
                    ci.Add(ClosingInstruction[pb == IfElifElseBlock ? If : pb]);

                if (ci.Count > 0)
                {
                    const string errpath = "errors.preproc.blocks_unclosed";

                    state.ReportError((global ? $"[{ name}]  " : "") + state.Language[errpath, string.Join("', '", ci)], new DefinitionContext(func.Context.FilePath, locndx), Language.GetErrorNumber(errpath));

                    while (curr.Parent is Entity e)
                        curr = e;
                }

                return (FUNCTION)curr;
            }
        }

        private static bool ProcessFunctionDeclaration(string Line, DefinitionContext defcntx, PreInterpreterState st, ErrorReporter err)
        {
            void __procfunc(string name, string[] par, string[] opar)
            {
                if (st.CurrentFunction is null)
                {
                    string lname = name.ToLower();

                    if (st.Functions.ContainsKey(lname))
                        err("errors.preproc.function_exists", name, st.Functions[lname].Context);
                    else
                    {
                        st.CurrentFunction = new FunctionScope(defcntx);
                        st.CurrentFunction.Parameters.AddRange(from p in par
                                                               let ndx = p.IndexOf('$')
                                                               let attr = p.Remove(ndx).Trim().ToLower()
                                                               select (
                                                                    p.Substring(ndx + 1).Trim().ToLower(),
                                                                    attr.Contains("byref"),
                                                                    attr.Contains("const"),
                                                                    null as string
                                                               ));
                        st.CurrentFunction.Parameters.AddRange(from p in opar
                                                               let ndx = p.IndexOf('=')
                                                               let nm = p.Remove(ndx).Trim().ToLower().TrimStart('$')
                                                               select (
                                                                    nm,
                                                                    false,
                                                                    false,
                                                                    p.Substring(ndx + 1).Trim()
                                                               ));

                        st.Functions[lname] = st.CurrentFunction;
                    }
                }
                else
                    err("errors.preproc.function_nesting");
            }

            if (Line.Match(@"^func\s+(?<name>[a-z_]\w*)\s*\(\s*(?<params>((const\s)?\s*(byref\s)?\s*\$[a-z]\w*\s*)(,\s*(const\s)?\s*(byref\s)?\s*\$[a-z]\w*\s*)*)?\s*(?<optparams>(,\s*\$[a-z]\w*\s*=\s*.+\s*)*)\s*\)$", out Match m))
                __procfunc(
                    m.Get("name"),
                    m.Get("params").Split(',').Select(s => s.Trim()).Where(Enumerable.Any).ToArray(),
                    m.Get("optparams").Split(',').Select(s => s.Trim()).Where(Enumerable.Any).ToArray()
                );
            else if (Line.Match(@"^func\s+(?<name>[a-z_]\w*)\s*\(\s*(?<optparams>(\$[a-z]\w*\s*=\s*.+\s*)(,\s*\$[a-z]\w*\s*=\s*.+\s*)*)?\s*\)$", out m))
                __procfunc(
                    m.Get("name"),
                    new string[0],
                    m.Get("optparams").Split(',').Select(s => s.Trim()).Where(Enumerable.Any).ToArray()
                );
            else if (Line.Match("^endfunc$", out m))
                if (st.CurrentFunction is null)
                    err("errors.preproc.unexpected_endfunc");
                else
                    st.CurrentFunction = null;
            else
                return true;

            return false;
        }

        private static void ProcessPrgamaCompileOption(string name, string value, CompileInfo ci, ErrorReporter err)
        {
            value = value.Trim('\'', '"', ' ', '\t', '\r', '\n', '\v');

            name.ToLower().Switch(new Dictionary<string, Action>
            {
                ["out"] = () => ci.FileName = value,
                ["icon"] = () => ci.IconPath = value,
                ["execlevel"] = () => ci.ExecLevel = (ExecutionLevel)Enum.Parse(typeof(ExecutionLevel), value, true),
                ["upx"] = () => ci.UPX = bool.Parse(value),
                ["autoitexecuteallowed"] = () => ci.AutoItExecuteAllowed = bool.Parse(value),
                ["console"] = () => ci.ConsoleMode = bool.Parse(value),
                ["compression"] = () => ci.Compression = byte.TryParse(value, out byte b) && (b % 2) == 1 && b < 10 ? b : throw null,
                ["compatibility"] = () => ci.Compatibility = (Compatibility)Enum.Parse(typeof(Compatibility), value, true),
                ["x64"] = () => ci.X64 = bool.Parse(value),
                ["inputboxres"] = () => ci.InputBoxRes = bool.Parse(value),
                ["comments"] = () => ci.AssemblyComment = value,
                ["companyname"] = () => ci.AssemblyCompanyName = value,
                ["filedescription"] = () => ci.AssemblyFileDescription = value,
                ["fileversion"] = () => ci.AssemblyFileVersion = Version.Parse(value.Contains(',') ? value.Remove(value.IndexOf(',')).Trim() : value),
                ["internalname"] = () => ci.AssemblyInternalName = value,
                ["legalcopyright"] = () => ci.AssemblyCopyright = value,
                ["legaltrademarks"] = () => ci.AssemblyTrademarks = value,
                ["originalfilename"] = () => { /* do nothing */ },
                ["productname"] = () => ci.AssemblyProductName = value,
                ["productversion"] = () => ci.AssemblyProductVersion = Version.Parse(value.Contains(',') ? value.Remove(value.IndexOf(',')).Trim() : value),
            },
            () => err("errors.preproc.directive_invalid", name));
        }

        private static string ProcessDirective(string line, PreInterpreterState st, InterpreterSettings settings, ErrorReporter err)
        {
            string inclpath = "";

            line.Match(
                ("^notrayicon$", _ => st.UseTrayIcon = false),
                ("^requireadmin$", _ => st.RequireAdmin = true),
                ("^include-once$", _ => st.IsIncludeOnce = true),
                (@"^include(\s|\b)\s*(\<(?<rel>.*)\>|\""(?<abs1>.*)\""|\'(?<abs2>.*)\')$", m => {
                    string path = m.Get("abs1");

                    if (path.Length == 0)
                        path = m.Get("abs2");

                    path = path.Replace('\\', '/');

                    FileInfo nfo = new FileInfo($"{st.CurrentContext.SourcePath.FullName}/../{path}");

                    if (path.Length > 0)
                        if (!nfo.Exists)
                            nfo = new FileInfo(path);
                        else
                            try
                            {
                                include();

                                return;
                            }
                            catch
                            {
                            }
                    else
                        path = m.Get("rel").Replace('\\', '/');

                    foreach (string dir in settings.IncludeDirectories)
                        try
                        {
                            string ipath = $"{dir}/{path}";

                            if ((nfo = new FileInfo(ipath)).Exists && !Directory.Exists(ipath))
                            {
                                include();

                                return;
                            }
                        }
                        catch
                        {
                        }

                    err("errors.preproc.include_nfound", path);

                    void include()
                    {
                        if (!st.IncludeOncePaths.Contains(nfo.FullName))
                            inclpath = nfo.FullName;

                        if (inclpath.Match(@"^#include\-once$", out _, RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase))
                            st.IncludeOncePaths.Add(nfo.FullName);
                    }
                }),
                (@"^onautoitstartregister\b\s*\""(?<func>.*)\""$", m => st.StartFunctions.Add(m.Groups["func"].ToString().Trim())),
                (@"^pragma\b\s*(?<opt>[a-z]\w*)\s*\((?<name>[a-z]\w*)\s*\,\s*(?<value>.+)\s*\)$", m =>
                {
                    string opt = m.Get("opt");
                    string name = m.Get("name");
                    string value = m.Get("value");

                    try
                    {
                        switch (opt.ToLower())
                        {
                            case "compile":
                                ProcessPrgamaCompileOption(name, value, st.CompileInfo, err);

                                break;
                            default:
                                err("errors.preproc.pragma_unsupported", opt);

                                break;
                        }
                    }
                    catch
                    {
                        err("errors.preproc.directive_invalid_value", value, name);
                    }
                })
            );

            return inclpath;
        }

        private static void ParseExpressionAST(InterpreterState state, InterpreterSettings settings)
        {
            const string globnm = PreInterpreterState.GLOBAL_FUNC_NAME;
            ExpressionParser exparser = new ExpressionParser(settings.UseOptimization);

            exparser.Initialize();

            foreach ((string name, FUNCTION func) in new[] { (globnm, state.Functions[globnm]) }.Concat(from kvp in state.Functions
                                                                                                        where kvp.Key != globnm
                                                                                                        select (kvp.Key, kvp.Value)))
                state.ASTFunctions[name] = ProcessWhileBlocks(process(func)[0]);

            dynamic process(Entity e)
            {
                void err(string path, params object[] args) => state.ReportKnownError(path, e.DefinitionContext, args);
                void warn(string path, params object[] args) => state.ReportKnownWarning(path, e.DefinitionContext, args);
                AST_STATEMENT[] proclines() => e.RawLines.SelectMany(rl => process(rl) as AST_STATEMENT[]).Where(l => l != null).ToArray();
                EXPRESSION opt(EXPRESSION expr) => expr is null ? null : Refactorings.ProcessExpression(expr);
                AST_CONDITIONAL_BLOCK proccond() => new AST_CONDITIONAL_BLOCK
                {
                    Condition = parseexpr((e as ConditionalEntity).RawCondition),
                    Statements = proclines(),
                    Context = e.DefinitionContext
                };
                EXPRESSION parseexpr(string expr)
                {
                    if (parsemexpr(expr) is MULTI_EXPRESSIONS m)
                        if (m.Length > 1)
                            err("errors.astproc.no_comma_allowed", expr);
                        else if (m[0].IsValueRange)
                            err("errors.astproc.no_range_allowed", expr);
                        else
                            return (m[0] as MULTI_EXPRESSION.SingleValue).Item;

                    return null;
                }
                MULTI_EXPRESSIONS parsemexpr(string expr)
                {
                    expr = expr.Trim();

                    try
                    {
                        return exparser.Parse(expr);
                    }
                    catch (Exception ex)
                    {
                        err("errors.astproc.parser_error", expr, ex.Message);

                        return null;
                    }
                }
                dynamic __inner()
                {
                    switch (e)
                    {
                        case FUNCTION i:
                            return new AST_FUNCTION
                            {
                                Statements = proclines(),
                                Context = i.DefinitionContext,
                                Name = i.Name,
                                // TODO : parse params etc.
                            };
                        case IF i:
                            return new AST_IF_STATEMENT
                            {
                                If = process(i.If),
                                ElseIf = i.ElseIfs.Select(x => process(x) as AST_CONDITIONAL_BLOCK).ToArray(),
                                OptionalElse = i.Else is ELSE_BLOCK eb && process(eb) is AST_STATEMENT[] el && el.Length > 0 ? el : null,
                                Context = e.DefinitionContext,
                            };
                        case IF_BLOCK _:
                        case ELSEIF_BLOCK _:
                            return proccond();
                        case ELSE_BLOCK i:
                            return proclines();
                        case WHILE i:
                            return (AST_WHILE_STATEMENT)proccond();
                        case DO_UNTIL i:
                            {
                                return new AST_WHILE_STATEMENT
                                {
                                    WhileBlock = new AST_CONDITIONAL_BLOCK
                                    {
                                        Condition = EXPRESSION.NewLiteral(LITERAL.True),
                                        Statements = new AST_STATEMENT[]
                                        {
                                            new AST_IF_STATEMENT
                                            {
                                                If = proccond(),
                                                OptionalElse = new AST_STATEMENT[]
                                                {
                                                    new AST_BREAK_STATEMENT { Level = 1 }
                                                }
                                            }
                                        }
                                    }
                                };
                            }
                        case SELECT i:
                            {
                                IEnumerable<AST_CONDITIONAL_BLOCK> cases = i.Cases.Select(x => (process(x) as AST_SELECT_CASE)?.CaseBlock);

                                if (cases.Any())
                                    return new AST_IF_STATEMENT
                                    {
                                        If = cases.First(),
                                        ElseIf = cases.Skip(1).ToArray()
                                    };
                                else
                                    break;
                            }
                        case SWITCH i:
                            {
                                AST_LOCAL_VARIABLE exprvar = new AST_LOCAL_VARIABLE
                                {
                                    Variable = VARIABLE.NewTemporary,
                                    InitExpression = parseexpr(i.Expression)
                                };
                                EXPRESSION exprvare = EXPRESSION.NewVariableExpression(VARIABLE_EXPRESSION.NewVariable(exprvar.Variable));
                                dynamic[] cases = i.Cases.Select(x => process(x)[0]).ToArray();
                                IEnumerable<AST_CONDITIONAL_BLOCK> condcases = cases.Select(x =>
                                {
                                    AST_CONDITIONAL_BLOCK cblock = new AST_CONDITIONAL_BLOCK
                                    {
                                        Context = x.Context,
                                        Statements = x.Statements,
                                    };

                                    switch (x)
                                    {
                                        case AST_SWITCH_CASE_EXPRESSION ce:
                                            IEnumerable<EXPRESSION> expr = ce.Expressions.Select(ex =>
                                            {
                                                if (ex is MULTI_EXPRESSION.SingleValue sv)
                                                    return EXPRESSION.NewBinaryExpression(OPERATOR_BINARY.EqualCaseSensitive, exprvare, sv.Item);
                                                else if (ex is MULTI_EXPRESSION.ValueRange vr)
                                                    return EXPRESSION.NewBinaryExpression(
                                                        OPERATOR_BINARY.And,
                                                        EXPRESSION.NewBinaryExpression(
                                                        OPERATOR_BINARY.GreaterEqual,
                                                            exprvare,
                                                            vr.Item1
                                                        ),
                                                        EXPRESSION.NewBinaryExpression(
                                                            OPERATOR_BINARY.LowerEqual,
                                                            exprvare,
                                                            vr.Item2
                                                        )
                                                    );
                                                else
                                                    return null;
                                            });

                                            if (expr.Any())
                                                cblock.Condition = expr.Aggregate((a, b) => EXPRESSION.NewBinaryExpression(OPERATOR_BINARY.Or, a, b));
                                            else
                                                cblock.Condition = EXPRESSION.NewLiteral(LITERAL.False);

                                            break;
                                        case AST_SWITCH_CASE_ELSE _:
                                            cblock.Condition = EXPRESSION.NewLiteral(LITERAL.True);

                                            break;
                                    }

                                    cblock.ExplicitLocalsVariables.AddRange(x.ExplicitLocalsVariables);

                                    return cblock;
                                });
                                AST_SCOPE scope = new AST_SCOPE();

                                if (condcases.Any())
                                    scope.Statements = new AST_STATEMENT[]
                                    {
                                        new AST_IF_STATEMENT
                                        {
                                            Context = i.DefinitionContext,
                                            If = condcases.First(),
                                            ElseIf = condcases.Skip(1).ToArray()
                                        }
                                    };

                                if (cases.Count(x => x is AST_SWITCH_CASE_ELSE) > 1)
                                    err("errors.astproc.multiple_switch_case_else");

                                scope.ExplicitLocalsVariables.Add(exprvar);

                                return scope;
                            }
                        case SELECT_CASE i:
                            return (AST_SELECT_CASE)new AST_CONDITIONAL_BLOCK
                            {
                                Condition = i.RawCondition.ToLower() == "else" ? EXPRESSION.NewLiteral(LITERAL.True) : parseexpr(i.RawCondition),
                                Statements = proclines(),
                                Context = i.DefinitionContext
                            };
                        case SWITCH_CASE i:
                            {
                                string expr = i.RawCondition;

                                if (expr.ToLower() == "else")
                                    return new AST_SWITCH_CASE_ELSE();
                                else
                                    return new AST_SWITCH_CASE_EXPRESSION
                                    {
                                        Statements = proclines(),
                                        Expressions = parsemexpr(expr)?.ToArray() ?? new MULTI_EXPRESSION[0]
                                    };
                            }
                        case RETURN i:
                            return i.Expression is null ? new AST_RETURN_STATEMENT() : new AST_RETURN_VALUE_STATEMENT
                            {
                                Expression = parseexpr(i.Expression)
                            };
                        case CONTINUECASE _:
                            warn("warnings.not_impl"); // TODO

                            return new AST_CONTINUECASE_STATEMENT();
                        case CONTINUE i:
                            return new AST_CONTINUE_STATEMENT
                            {
                                Level = (uint)i.Level
                            };
                        case BREAK i:
                            return new AST_BREAK_STATEMENT
                            {
                                Level = (uint)i.Level
                            };
                        case WITH i:
                            {
                                if (parseexpr(i.Expression) is EXPRESSION expr)
                                {
                                    if (!expr.IsVariableExpression)
                                        err("errors.astproc.obj_expression_required", i.Expression);

                                    warn("warnings.not_impl");

                                    return new AST_WITH_STATEMENT
                                    {
                                        WithExpression = expr,
                                        WithLines = null // TODO
                                    };
                                }
                                else
                                    break;
                            }
                        case FOR i:
                            {
                                DefinitionContext defctx = i.DefinitionContext;
                                EXPRESSION start = parseexpr(i.StartExpression);
                                EXPRESSION stop = parseexpr(i.StopExpression);
                                EXPRESSION step = i.OptStepExpression is string stepexpr ? parseexpr(stepexpr) : EXPRESSION.NewLiteral(LITERAL.NewNumber(1));
                                AST_LOCAL_VARIABLE cntvar = new AST_LOCAL_VARIABLE
                                {
                                    Variable = new VARIABLE(i.VariableExpression),
                                    InitExpression = start,
                                };
                                AST_LOCAL_VARIABLE upvar = new AST_LOCAL_VARIABLE
                                {
                                    Variable = VARIABLE.NewTemporary,
                                    InitExpression = opt(EXPRESSION.NewBinaryExpression(
                                        OPERATOR_BINARY.LowerEqual,
                                        start,
                                        stop
                                    ))
                                };
                                AST_SCOPE scope = new AST_SCOPE
                                {
                                    Context = defctx,
                                    Statements = new AST_STATEMENT[]
                                    {
                                        new AST_WHILE_STATEMENT
                                        {
                                            Context = defctx,
                                            WhileBlock = new AST_CONDITIONAL_BLOCK
                                            {
                                                Context = defctx,
                                                Condition = EXPRESSION.NewLiteral(LITERAL.True),
                                                Statements = new AST_STATEMENT[]
                                                {
                                                    new AST_IF_STATEMENT
                                                    {
                                                        Context = defctx,
                                                        If = new AST_CONDITIONAL_BLOCK
                                                        {
                                                            Context = defctx,
                                                            Condition = EXPRESSION.NewBinaryExpression(
                                                                OPERATOR_BINARY.Or,
                                                                EXPRESSION.NewBinaryExpression(
                                                                    OPERATOR_BINARY.And,
                                                                    EXPRESSION.NewVariableExpression(
                                                                        VARIABLE_EXPRESSION.NewVariable(upvar.Variable)
                                                                    ),
                                                                    EXPRESSION.NewBinaryExpression(
                                                                        OPERATOR_BINARY.Greater,
                                                                        EXPRESSION.NewVariableExpression(
                                                                            VARIABLE_EXPRESSION.NewVariable(cntvar.Variable)
                                                                        ),
                                                                        stop
                                                                    )
                                                                ),EXPRESSION.NewBinaryExpression(
                                                                    OPERATOR_BINARY.And,
                                                                    EXPRESSION.NewUnaryExpression(
                                                                        OPERATOR_UNARY.Not,
                                                                        EXPRESSION.NewVariableExpression(
                                                                            VARIABLE_EXPRESSION.NewVariable(upvar.Variable)
                                                                        )
                                                                    ),
                                                                    EXPRESSION.NewBinaryExpression(
                                                                        OPERATOR_BINARY.Lower,
                                                                        EXPRESSION.NewVariableExpression(
                                                                            VARIABLE_EXPRESSION.NewVariable(cntvar.Variable)
                                                                        ),
                                                                        start
                                                                    )
                                                                )
                                                            ),
                                                            Statements = new AST_STATEMENT[]
                                                            {
                                                                new AST_BREAK_STATEMENT { Level = 1 }
                                                            }
                                                        }
                                                    },
                                                }
                                                .Concat(proclines())
                                                .Concat(new AST_STATEMENT[]
                                                {
                                                    new AST_ASSIGNMENT_EXPRESSION_STATEMENT
                                                    {
                                                        Context = defctx,
                                                        Expression = ASSIGNMENT_EXPRESSION.NewAssignment(
                                                            OPERATOR_ASSIGNMENT.AssignAdd,
                                                            VARIABLE_EXPRESSION.NewVariable(cntvar.Variable),
                                                            EXPRESSION.NewLiteral(LITERAL.NewNumber(1))
                                                        )
                                                    }
                                                })
                                                .ToArray()
                                            }
                                        }
                                    }
                                };

                                scope.ExplicitLocalsVariables.Add(cntvar);
                                scope.ExplicitLocalsVariables.Add(upvar);

                                return scope;
                            }
                        case FOREACH i:
                            {
                                DefinitionContext defctx = i.DefinitionContext;
                                AST_LOCAL_VARIABLE elemvar = new AST_LOCAL_VARIABLE
                                {
                                    Variable = new VARIABLE(i.VariableExpression),
                                    InitExpression = EXPRESSION.NewLiteral(LITERAL.Null)
                                };

                                if (parseexpr(i.RangeExpression) is EXPRESSION collexpr)
                                {
                                    AST_LOCAL_VARIABLE collvar = new AST_LOCAL_VARIABLE
                                    {
                                        Variable = VARIABLE.NewTemporary,
                                        InitExpression = collexpr
                                    };
                                    AST_LOCAL_VARIABLE cntvar = new AST_LOCAL_VARIABLE
                                    {
                                        Variable = VARIABLE.NewTemporary,
                                        InitExpression = EXPRESSION.NewLiteral(LITERAL.NewNumber(0))
                                    };
                                    AST_SCOPE scope = new AST_SCOPE
                                    {
                                        Context = defctx,
                                        Statements = new AST_STATEMENT[]
                                        {
                                            new AST_ASSIGNMENT_EXPRESSION_STATEMENT
                                            {
                                                Context = defctx,
                                                Expression = ASSIGNMENT_EXPRESSION.NewAssignment(
                                                    OPERATOR_ASSIGNMENT.Assign,
                                                    VARIABLE_EXPRESSION.NewVariable(elemvar.Variable),
                                                    EXPRESSION.NewArrayIndex(
                                                        VARIABLE_EXPRESSION.NewVariable(collvar.Variable),
                                                        EXPRESSION.NewVariableExpression(
                                                            VARIABLE_EXPRESSION.NewVariable(cntvar.Variable)
                                                        )
                                                    )
                                                )
                                            },
                                        }
                                        .Concat(proclines())
                                        .Concat(new AST_STATEMENT[]
                                        {
                                            new AST_IF_STATEMENT
                                            {
                                                Context = defctx,
                                                If = new AST_CONDITIONAL_BLOCK
                                                {
                                                    Context = defctx,
                                                    Condition = EXPRESSION.NewBinaryExpression(
                                                        OPERATOR_BINARY.GreaterEqual,
                                                        EXPRESSION.NewVariableExpression(
                                                            VARIABLE_EXPRESSION.NewVariable(cntvar.Variable)
                                                        ),
                                                        EXPRESSION.NewFunctionCall(
                                                            new Tuple<string, FSharpList<EXPRESSION>>(
                                                                "ubound",
                                                                new FSharpList<EXPRESSION>(
                                                                    EXPRESSION.NewVariableExpression(
                                                                        VARIABLE_EXPRESSION.NewVariable(collvar.Variable)
                                                                    ),
                                                                    FSharpList<EXPRESSION>.Empty
                                                                )
                                                            )
                                                        )
                                                    ),
                                                    Statements = new AST_STATEMENT[]
                                                    {
                                                        new AST_BREAK_STATEMENT { Level = 1 }
                                                    }
                                                },
                                                OptionalElse = new AST_STATEMENT[]
                                                {
                                                    new AST_ASSIGNMENT_EXPRESSION_STATEMENT
                                                    {
                                                        Context = defctx,
                                                        Expression = ASSIGNMENT_EXPRESSION.NewAssignment(
                                                            OPERATOR_ASSIGNMENT.AssignAdd,
                                                            VARIABLE_EXPRESSION.NewVariable(cntvar.Variable),
                                                            EXPRESSION.NewLiteral(
                                                                LITERAL.NewNumber(1)
                                                            )
                                                        )
                                                    }
                                                }
                                            }
                                        })
                                        .ToArray()
                                    };

                                    scope.ExplicitLocalsVariables.Add(collvar);

                                    return scope;
                                }

                                break;
                            }
                        case RAWLINE i:
                            {
                                // TODO : with-statement rawline ?

                                if (parseexpr(i.RawContent?.Trim() ?? "") is EXPRESSION expr)
                                    if (expr.IsAssignmentExpression)
                                        return new AST_ASSIGNMENT_EXPRESSION_STATEMENT
                                        {
                                            Expression = (expr as EXPRESSION.AssignmentExpression)?.Item,
                                        };
                                    else
                                    {
                                        if (!expr.IsFunctionCall)
                                            warn("warnings.astproc.expression_result_discarded");

                                        return new AST_EXPRESSION_STATEMENT
                                        {
                                            Expression = expr,
                                        };
                                    }
                                else
                                    break;
                            }
                        case DECLARATION i:
                            warn("warnings.not_impl"); // TODO

                            break;
                        default:
                            err("errors.astproc.unknown_entity", e?.GetType()?.FullName ?? "<null>");

                            return null;
                    }

                    return null;
                }

                dynamic res = __inner();

                if (res is AST_STATEMENT s)
                    s.Context = e.DefinitionContext;

                return res is AST_CONDITIONAL_BLOCK cb ? (dynamic)cb : res is IEnumerable<AST_STATEMENT> @enum ? @enum.ToArray() : new AST_STATEMENT[] { res };
            }
        }

        private static AST_FUNCTION ProcessWhileBlocks(AST_FUNCTION func)
        {
            ReversedLabelStack ls_cont = new ReversedLabelStack();
            ReversedLabelStack ls_exit = new ReversedLabelStack();

            return process(func) as AST_FUNCTION;

            AST_STATEMENT process(AST_STATEMENT e)
            {
                if (e is null)
                    return null;

                T[] procas<T>(T[] instr) where T : AST_STATEMENT => instr?.Select(x => process(x) as T)?.ToArray();

                AST_STATEMENT __inner()
                {
                    switch (e)
                    {
                        case AST_CONTINUE_STATEMENT s:
                            return new AST_GOTO_STATEMENT { Label = ls_cont[s.Level] };
                        case AST_BREAK_STATEMENT s:
                            return new AST_GOTO_STATEMENT { Label = ls_exit[s.Level] };
                        case AST_WHILE_STATEMENT s:
                            ls_cont.Push(AST_LABEL.NewLabel);
                            ls_exit.Push(AST_LABEL.NewLabel);

                            AST_WHILE_STATEMENT w = new AST_WHILE_STATEMENT
                            {
                                WhileBlock = s.WhileBlock,
                                ContinueLabel = ls_cont[1],
                                ExitLabel = ls_exit[1],
                                Context = e.Context,
                            };
                            AST_SCOPE sc = new AST_SCOPE
                            {
                                Statements = new AST_STATEMENT[]
                                {
                                    w,
                                    ls_exit[1]
                                }
                            };

                            w.WhileBlock.Statements = new AST_STATEMENT[] { ls_cont[1] }.Concat(procas(w.WhileBlock.Statements)).ToArray();

                            ls_cont.Pop();
                            ls_exit.Pop();

                            return sc;
                        case AST_SCOPE s:
                            s.Statements = procas(s.Statements);

                            return s;
                        case AST_IF_STATEMENT s:
                            s.If = process(s.If) as AST_CONDITIONAL_BLOCK;
                            s.ElseIf = procas(s.ElseIf);
                            s.OptionalElse = procas(s.OptionalElse);

                            return s;
                        case AST_WITH_STATEMENT s:
                            s.WithLines = procas(s.WithLines);

                            return s;
                        case AST_SWITCH_STATEMENT s:
                            s.Cases = procas(s.Cases);

                            return s;
                        default:
                            return e;
                    }
                }
                AST_STATEMENT res = __inner();

                res.Context = e.Context;

                return res;
            }
        }
    }

    internal sealed class ReversedLabelStack
    {
        private readonly List<AST_LABEL> _stack = new List<AST_LABEL>();


        /// <summary>level 1 == top-most</summary>
        public AST_LABEL this[uint level] => level == 0 || level > _stack.Count ? null : _stack[_stack.Count - (int)level];

        public void Push(AST_LABEL lb) => _stack.Add(lb);

        public void Clear() => _stack.Clear();

        public AST_LABEL Pop()
        {
            if (_stack.Count > 0)
            {
                int index = _stack.Count - 1;
                AST_LABEL lb = _stack[index];

                _stack.RemoveAt(index);

                return lb;
            }
            else
                return null;
        }
    }

    public abstract class AbstractParserState
    {
        private protected List<InterpreterError> _errors;

        public InterpreterError[] Errors => _errors.ToArray();
        public CompileInfo CompileInfo { private protected set; get; }
        public Language Language { get; set; }
        public bool IsIncludeOnce { set; get; }
        public bool RequireAdmin { set; get; }
        public bool UseTrayIcon { set; get; }


        public AbstractParserState()
        {
            _errors = new List<InterpreterError>();
            CompileInfo = new CompileInfo();
            UseTrayIcon = true;
        }

        public void ReportError(string msg, DefinitionContext ctx, int num) => _errors.Add(new InterpreterError(msg, ctx, num));

        public void ReportWarning(string msg, DefinitionContext ctx, int num) => _errors.Add(new InterpreterError(msg, ctx, num, false));

        internal void ReportKnownError(string errname, DefinitionContext ctx, params object[] args) => ReportError(Language[errname, args], ctx, Language.GetErrorNumber(errname));

        internal void ReportKnownWarning(string errname, DefinitionContext ctx, params object[] args) => ReportWarning(Language[errname, args], ctx, Language.GetErrorNumber(errname));
    }

    public sealed class InterpreterState
        : AbstractParserState
    {
        public Dictionary<string, FUNCTION> Functions { get; }
        public Dictionary<string, AST_FUNCTION> ASTFunctions { get; }
        public List<string> StartFunctions { get; }


        public InterpreterState()
        {
            Functions = new Dictionary<string, FUNCTION>();
            ASTFunctions = new Dictionary<string, AST_FUNCTION>();
            StartFunctions = new List<string>();
        }

        public static InterpreterState Convert(PreInterpreterState ps)
        {
            InterpreterState s = new InterpreterState
            {
                IsIncludeOnce = ps.IsIncludeOnce,
                RequireAdmin = ps.RequireAdmin,
                UseTrayIcon = ps.UseTrayIcon,
                CompileInfo = ps.CompileInfo,
                Language = ps.Language,
            };
            s.StartFunctions.AddRange(ps.StartFunctions);
            s._errors.AddRange(ps.Errors);

            return s;
        }

        public string GetFunctionSignature(string funcname) => $"func {funcname}({string.Join(", ", Functions[funcname].Parameters.Select(p => $"{(p.Const ? "const " : "")}{(p.ByRef ? "ref " : "")}${p.Name}{(p.RawInitExpression is string s ? $" = {s}" : "")}"))})";
    }

    public sealed class PreInterpreterState
        : AbstractParserState
    {
        internal const string GLOBAL_FUNC_NAME = "__global<>";

        public InterpreterContext CurrentContext { set; get; }
        public Dictionary<string, FunctionScope> Functions { get; }
        public FunctionScope CurrentFunction { set; get; }
        public List<string> IncludeOncePaths { get; }
        public List<string> StartFunctions { get; }

        public FunctionScope GlobalFunction
        {
            set => Functions[GLOBAL_FUNC_NAME] = value;
            get => Functions[GLOBAL_FUNC_NAME];
        }


        public PreInterpreterState()
        {
            Functions = new Dictionary<string, FunctionScope> { [GLOBAL_FUNC_NAME] = null };
            IncludeOncePaths = new List<string>();
            StartFunctions = new List<string>();
            UseTrayIcon = true;
        }

        public string GetFunctionSignature(string funcname) => $"func {funcname}({string.Join(", ", Functions[funcname].Parameters.Select(p => $"{(p.Constant ? "const " : "")}{(p.ByRef ? "ref " : "")}${p.Name}{(p.InitExpression is string s ? $" = {s}" : "")}"))})";
    }

    public sealed class FunctionScope
    {
        public List<(string Name, bool ByRef, bool Constant, string InitExpression)> Parameters { get; }
        public List<(string Line, DefinitionContext Context)> Lines { get; }
        public DefinitionContext Context { get; }


        public FunctionScope(DefinitionContext ctx)
        {
            Parameters = new List<(string, bool, bool, string)>();
            Lines = new List<(string, DefinitionContext)>();
            Context = ctx;
        }
    }

    public sealed class InterpreterContext
    {
        public FileInfo SourcePath { get; }
        public string Content { get; }


        public InterpreterContext(string path)
            : this(new FileInfo(path))
        {
        }

        public InterpreterContext(FileInfo path)
        {
            SourcePath = path;

            if (SourcePath.Exists)
                using (StreamReader rd = SourcePath.OpenText())
                    Content = rd.ReadToEnd();
        }
    }

    public sealed class CompileInfo
    {
        public string FileName { set; get; } = "AutoItApplication.exe";
        public string IconPath { set; get; }
        public ExecutionLevel ExecLevel { set; get; }
        public Compatibility Compatibility { set; get; }
        public bool AutoItExecuteAllowed { set; get; }
        public bool ConsoleMode { set; get; }
        public byte Compression { set; get; }
        public bool UPX { set; get; }
        public bool X64 { set; get; }
        public bool InputBoxRes { set; get; }
        public string AssemblyComment { set; get; }
        public string AssemblyCompanyName { set; get; }
        public string AssemblyFileDescription { set; get; }
        public Version AssemblyFileVersion { set; get; }
        public string AssemblyInternalName { set; get; }
        public string AssemblyCopyright { set; get; }
        public string AssemblyTrademarks { set; get; }
        public string AssemblyProductName { set; get; }
        public Version AssemblyProductVersion { set; get; }


        internal CompileInfo()
        {
        }
    }

    public sealed class InterpreterError
    {
        public DefinitionContext ErrorContext { get; }
        public string ErrorMessage { get; }
        public int ErrorNumber { get; }
        public bool IsFatal { get; }


        /// <summary>A new fatal error</summary>
        internal InterpreterError(string msg, DefinitionContext line, int number)
            : this(msg, line, number, true)
        {
        }

        internal InterpreterError(string msg, DefinitionContext line, int number, bool fatal)
        {
            IsFatal = fatal;
            ErrorMessage = msg;
            ErrorContext = line;
            ErrorNumber = number;
        }

        public void @throw() => throw (InvalidProgramException)this;

        public override string ToString() => $"(AU{ErrorNumber:D4})  {ErrorContext}: {ErrorMessage}";


        public static implicit operator InvalidProgramException(InterpreterError err) => new InvalidProgramException(err.ToString())
        {
            Source = err.ErrorContext.FilePath.FullName
        };
    }

    public struct DefinitionContext
    {
        public FileInfo FilePath { get; }
        public int StartLine { get; }
        public int? EndLine { get; }


        public DefinitionContext(FileInfo path, int line)
            : this(path, line, null)
        {
        }

        public DefinitionContext(FileInfo path, int start, int? end)
        {
            ++start;

            FilePath = path;
            StartLine = start;
            EndLine = end is int i && i > start ? (int?)(i + 1) : null;
        }

        public override string ToString() => $"[{FilePath.Name}] l. {StartLine}{(EndLine is int i ? $"-{i}" : "")}";
    }

    public enum ExecutionLevel
    {
        None,
        AsInvoker,
        HighestAvailable,
        RequireAdministrator
    }

    public enum Compatibility
    {
        vista,
        win7,
        win8,
        win81,
        win10
    }

    public enum ControlBlock
    {
        __NONE__,
        IfElifElseBlock,
        If,
        ElseIf,
        Else,
        Select,
        Switch,
        Case,
        For,
        While,
        Do,
        With,
    }
}

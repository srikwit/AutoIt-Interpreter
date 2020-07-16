﻿using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;

using Unknown6656.AutoIt3.Extensibility.Plugins.Internals;
using Unknown6656.AutoIt3.Runtime;
using Unknown6656.Common;

namespace Unknown6656.AutoIt3.Extensibility.Plugins.Debugging
{
    public sealed class DebuggingFunctionProvider
        : AbstractFunctionProvider
    {
        public override ProvidedNativeFunction[] ProvidedFunctions { get; }


        public DebuggingFunctionProvider(Interpreter interpreter)
            : base(interpreter) => ProvidedFunctions = new[]
            {
                ProvidedNativeFunction.Create(nameof(DebugVar), 1, DebugVar),
                ProvidedNativeFunction.Create(nameof(DebugCallFrame), 0, DebugCallFrame),
                ProvidedNativeFunction.Create(nameof(DebugThread), 0, DebugThread),
                ProvidedNativeFunction.Create(nameof(DebugAllVars), 0, DebugAllVars),
                ProvidedNativeFunction.Create(nameof(DebugAllCOM), 0, DebugAllCOM),
                ProvidedNativeFunction.Create(nameof(DebugAllVarsCompact), 0, DebugAllVarsCompact),
                ProvidedNativeFunction.Create(nameof(DebugCodeLines), 0, DebugCodeLines),
                ProvidedNativeFunction.Create(nameof(DebugAllThreads), 0, DebugAllThreads),
            };

        private IDictionary<string, object?> GetVariantInfo(Variant value)
        {
            string s = value.RawData?.ToString()?.Trim() ?? "";
            string ts = value.RawData?.GetType().ToString() ?? "<void>";

            IDictionary<string, object?> dic = new Dictionary<string, object?>
            {
                ["value"] = value.ToDebugString(Interpreter),
                ["type"] = value.Type,
                ["raw"] = s != ts ? $"\"{s}\" ({ts})" : ts
            };

            if (value.AssignedTo is Variable variable)
                dic["assignedTo"] = variable;

            if (value.ReferencedVariable is Variable @ref)
                dic["referenceTo"] = GetVariableInfo(@ref);

            return dic;
        }

        private IDictionary<string, object?> GetVariableInfo(Variable? variable) => new Dictionary<string, object?>
        {
            ["name"] = variable,
            ["constant"] = variable.IsConst,
            ["global"] = variable.IsGlobal,
            ["location"] = variable.DeclaredLocation,
            ["scope"] = variable.DeclaredScope,
            ["value"] = GetVariantInfo(variable.Value)
        };

        private IDictionary<string, object?> GetCallFrameInfo(CallFrame? frame)
        {
            IDictionary<string, object?> dic = new Dictionary<string, object?>();

            frame = frame?.CallerFrame;

            if (frame is { })
            {
                dic["type"] = frame.GetType().Name;
                dic["thread"] = frame.CurrentThread;
                dic["function"] = frame.CurrentFunction;
                dic["ret.value"] = frame.ReturnValue;
                dic["variables"] = frame.VariableResolver.LocalVariables.ToArray(GetVariableInfo);
                dic["arguments"] = frame.PassedArguments.ToArray(GetVariantInfo);

                if (frame is AU3CallFrame au3)
                {
                    dic["location"] = au3.CurrentLocation;
                    dic["line"] = $"\"{au3.CurrentLineContent}\"";
                }
            }

            return dic;
        }

        private IDictionary<string, object?> GetThreadInfo(AU3Thread thread) => new Dictionary<string, object?>
        {
            ["id"] = thread.ThreadID,
            ["disposed"] = thread.IsDisposed,
            ["isMain"] = thread.IsMainThread,
            ["running"] = thread.IsRunning,
            ["callstack"] = thread.CallStack.ToArray(GetCallFrameInfo)
        };

        private IDictionary<string, object?> GetAllVariables(Interpreter interpreter)
        {
            IDictionary<string, object?> dic = new Dictionary<string, object?>();
            List<VariableScope> scopes = new List<VariableScope> { interpreter.VariableResolver };
            int count;

            do
            {
                count = scopes.Count;

                foreach (VariableScope scope in from indexed in scopes.ToArray()
                                                from s in indexed.ChildScopes
                                                where !scopes.Contains(s)
                                                select s)
                    scopes.Add(scope);
            }
            while (count != scopes.Count);

            foreach (VariableScope scope in scopes)
                dic[scope.InternalName] = new Dictionary<string, object?>
                {
                    ["frame"] = scope.CallFrame,
                    ["function"] = scope.CallFrame?.CurrentFunction,
                    ["isGlobal"] = scope.IsGlobalScope,
                    ["parent"] = scope.Parent,
                    ["children"] = scope.ChildScopes.ToArray(c => c.InternalName),
                    ["variables"] = scope.LocalVariables.ToArray(GetVariableInfo),
                };

            return dic;
        }

        private string SerializeDictionary(IDictionary<string, object?> dic, string title)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine(title + ": {");

            void serialize(IDictionary<string, object?> dic, int level)
            {
                int w = dic.Keys.Select(k => k.Length).Append(0).Max();

                foreach (string key in dic.Keys)
                {
                    sb.Append($"{new string(' ', level * 4)}{(key + ':').PadRight(w + 1)} ");

                    switch (dic[key])
                    {
                        case IDictionary<string, object?> d:
                            sb.AppendLine();
                            serialize(d, level + 1);

                            break;
                        case Array { Length: 0 }:
                            sb.Append($"(0)");

                            break;
                        case Array arr:
                            sb.AppendLine($"({arr.Length})");

                            int index = 0;
                            int rad = 1 + (int)Math.Log10(arr.Length);

                            foreach (object? elem in arr)
                            {
                                sb.Append($"{new string(' ', (level + 1) * 4)}[{index.ToString().PadLeft(rad, '0')}]: ");

                                if (elem is IDictionary<string, object?> d)
                                {
                                    sb.AppendLine();
                                    serialize(d, level + 2);
                                }
                                else
                                    sb.Append(elem?.ToString());

                                ++index;
                            }

                            break;
                        case object obj:
                            sb.Append(obj);

                            break;
                    }

                    if (!sb.ToString().EndsWith(Environment.NewLine))
                        sb.AppendLine();
                }
            }

            serialize(dic, 1);

            return sb.AppendLine("}")
                     .ToString();
        }

        private FunctionReturnValue SerializePrint(CallFrame frame, IDictionary<string, object?> dic, object? title)
        {
            frame.Print(SerializeDictionary(dic, title is string s ? s : title?.ToString() ?? ""));

            return FunctionReturnValue.Success(Variant.Zero);
        }

        private string GenerateTable((string header, bool align_right, string?[] cells)[] columns, int max_width, Predicate<int>? select = null)
        {
            StringBuilder sb = new StringBuilder();
            string?[,] data = new string?[columns.Length, columns.Max(col => col.cells.Length)];
            int[] widths = columns.ToArray(col => col.header.Length);

            for (int i = 0; i < widths.Length; i++)
            {
                string?[] cells = columns[i].cells;

                for (int j = 0; j < cells.Length; ++j)
                {
                    data[i, j] = cells[j];
                    widths[i] = Math.Max(widths[i], cells[j]?.Length ?? 0);
                }
            }

            max_width -= 1 + widths.Length;

            int r = 0;

            while (true)
            {
                int c_width = widths.Sum();
                int diff = c_width - max_width;

                if (diff > 0 || r >= widths.Length)
                {
                    (int w, int i) = widths.WithIndex().OrderByDescending(Generics.fst).FirstOrDefault();

                    widths[i] = Math.Max(w - diff, w / 2) + 3;
                    ++r;
                }
                else
                    break;
            }

            sb.AppendLine($"{data.GetLength(1)} rows:")
              .Append('┌');

            for (int i = 0, l = widths.Length; i < l; i++)
                sb.Append(new string('─', widths[i]))
                  .Append(i < l - 1 ? '┬' : '┐');

            sb.AppendLine()
              .Append('│');

            for (int i = 0; i < widths.Length; i++)
                if (columns[i].header.Length > widths[i])
                    sb.Append(columns[i].header[..(widths[i] - 3)])
                      .Append("...");
                else
                    sb.Append(columns[i].align_right ? columns[i].header.PadLeft(widths[i]) : columns[i].header.PadRight(widths[i]))
                      .Append('│');

            sb.AppendLine().Append('├');

            for (int i = 0, l = widths.Length; i < l; i++)
                sb.Append(new string('─', widths[i]))
                  .Append(i < l - 1 ? '┼' : '┤');

            sb.AppendLine();

            for (int j = 0, l = data.GetLength(1); j < l; ++j)
            {
                bool sel = select?.Invoke(j) ?? false;

                if (sel)
                    sb.Append("\x1b[7m");

                for (int i = 0; i < widths.Length; i++)
                {
                    sb.Append('│');

                    string val = data[i, j] ?? "";

                    if (val.Length > widths[i])
                        sb.Append(val[..(widths[i] - 3)])
                          .Append("...");
                    else
                        sb.Append(columns[i].align_right ? val.PadLeft(widths[i]) : val.PadRight(widths[i]));
                }

                sb.Append('│');

                if (sel)
                    sb.Append("\x1b[27m");

                sb.AppendLine();
            }

            sb.Append('└');

            for (int i = 0, l = widths.Length; i < l; i++)
                sb.Append(new string('─', widths[i]))
                  .Append(i < l - 1 ? '┴' : '┘');

            return sb.AppendLine()
                     .ToString();
        }

        public FunctionReturnValue DebugVar(CallFrame frame, Variant[] args) => SerializePrint(frame, GetVariableInfo(args[0].AssignedTo), args[0].AssignedTo);

        public FunctionReturnValue DebugCallFrame(CallFrame frame, Variant[] args) => SerializePrint(frame, GetCallFrameInfo(frame), "Call Frame");

        public FunctionReturnValue DebugThread(CallFrame frame, Variant[] _) => SerializePrint(frame, GetThreadInfo(frame.CurrentThread), frame.CurrentThread);

        public FunctionReturnValue DebugAllVars(CallFrame frame, Variant[] _) => SerializePrint(frame, GetAllVariables(frame.Interpreter), frame.Interpreter);

        public FunctionReturnValue DebugAllVarsCompact(CallFrame frame, Variant[] _)
        {
            List<VariableScope> scopes = new List<VariableScope> { frame.Interpreter.VariableResolver };
            int count;

            do
            {
                count = scopes.Count;

                foreach (VariableScope scope in from indexed in scopes.ToArray()
                                                from s in indexed.ChildScopes
                                                where !scopes.Contains(s)
                                                select s)
                    scopes.Add(scope);
            }
            while (count != scopes.Count);

            object? netobj = null;
            StringBuilder sb = new StringBuilder();
            IEnumerable<(string, string, string, string)> iterators = from kvp in InternalsFunctionProvider._iterators
                                                                      let index = kvp.Value.index
                                                                      let tuple = kvp.Value.index < kvp.Value.collection.Length ? kvp.Value.collection[kvp.Value.index] : default
                                                                      select (
                                                                          $"/iter/{kvp.Key}",
                                                                          Autoit3.ASM.Name,
                                                                          "Iterator",
                                                                          $"Index:{index}, Length:{kvp.Value.collection.Length}, Key:{tuple.key.ToDebugString(Interpreter)}, Value:{tuple.value.ToDebugString(Interpreter)}"
                                                                      );
            IEnumerable<(string, string, string, string)> global_objs = from id in frame.Interpreter.GlobalObjectStorage.HandlesInUse
                                                                        where frame.Interpreter.GlobalObjectStorage.TryGet(id, out netobj)
                                                                        select (
                                                                            $"/objs/{id:x8}",
                                                                            Autoit3.ASM.Name,
                                                                            ".NET Object",
                                                                            netobj?.ToString() ?? "<null>"
                                                                        );
            (string name, string loc, string type, string value)[] variables = (from scope in scopes
                                                                                from variable in scope.LocalVariables
                                                                                let name = scope.InternalName + '$' + variable.Name
                                                                                orderby name ascending
                                                                                select (
                                                                                    name,
                                                                                    variable.DeclaredLocation.ToString(),
                                                                                    variable.Value.Type.ToString(),
                                                                                    variable.Value.ToDebugString(Interpreter)
                                                                                )).Concat(iterators)
                                                                                  .Concat(global_objs)
                                                                                  .ToArray();
            Array.Sort(variables, (x, y) =>
            {
                string[] pathx = x.name.Split('/');
                string[] pathy = y.name.Split('/');

                for (int i = 0, l = Math.Min(pathx.Length, pathy.Length); i < l; ++i)
                {
                    bool varx = pathx[i].StartsWith("$");
                    int cmp = varx ^ pathy[i].StartsWith("$") ? varx ? -1 : 1 : pathx[i].CompareTo(pathy[i]);

                    if (cmp != 0)
                        return cmp;
                }

                return string.Compare(x.name, y.name);
            });

            string table = GenerateTable(variables.Select(row => new string?[] { row.name, row.loc, row.type, row.value })
                                                  .Transpose()
                                                  .Zip(new[] {
                                                      ("Name", false),
                                                      ("Location", false),
                                                      ("Type", false),
                                                      ("Value", true),
                                                  })
                                                  .ToArray(t => (t.Second.Item1, t.Second.Item2, t.First)), Math.Min(Console.BufferWidth, Console.WindowWidth));

            frame.Print(table);

            return Variant.Zero;
        }

        public FunctionReturnValue DebugAllCOM(CallFrame frame, Variant[] _)
        {
            if (frame.Interpreter.COMConnector?.GetAllCOMObjectInfos() is { } objects)
            {
                var values = objects.Select(t => new string?[]
                {
                    $"/com/{t.id:x8}",
                    t.type,
                    t.clsid,
                    t.value.ToDebugString(frame.Interpreter),
                }).Transpose().Zip(new []
                {
                    ("Object", false),
                    ("Type", false),
                    ("CLSID", false),
                    ("Value", true),
                }).ToArray(t => (t.Second.Item1, t.Second.Item2, t.First));

                frame.Print(GenerateTable(values, Math.Min(Console.BufferWidth, Console.WindowWidth)));
            }

            return Variant.Zero;
        }

        public FunctionReturnValue DebugCodeLines(CallFrame frame, Variant[] _)
        {
            if (frame.CurrentThread.CallStack.OfType<AU3CallFrame>().FirstOrDefault() is AU3CallFrame au3frame)
            {
                StringBuilder sb = new StringBuilder();
                (SourceLocation loc, string txt)[] lines = au3frame.CurrentLineCache;
                int eip = au3frame.CurrentInstructionPointer;

                string table = GenerateTable(new[]
                {
                    ("Line", true, Enumerable.Range(0, lines.Length).ToArray(i => i.ToString())),
                    ("Location", false, lines.ToArray(t => t.loc.ToString())),
                    ("Content", false, lines.ToArray(Generics.snd)),
                }, Math.Min(Console.BufferWidth, Console.WindowWidth), i => i == eip);

                frame.Print(table);
            }

            return Variant.Zero;
        }

        public FunctionReturnValue DebugAllThreads(CallFrame frame, Variant[] _)
        {
            // TODO

            return Variant.Zero;
        }
    }
}

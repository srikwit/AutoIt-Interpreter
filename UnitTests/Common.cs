using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Linq;
using System;

using AutoItExpressionParser;

namespace UnitTests
{
    using static ExpressionAST;
    using static Console;


    public static class UnitTestRunner
    {
        internal static void AddTime(ref long target, Stopwatch sw)
        {
            sw.Stop();
            target += sw.ElapsedTicks;
            sw.Restart();
        }

        internal static void Print(string text, ConsoleColor color)
        {
            ForegroundColor = color;
            Write(text);
        }

        internal static void PrintLine(string text, ConsoleColor color) => Print(text + '\n', color);

        internal static void PrintHeader(string text, int width)
        {
            int rw = width - text.Length - 2;
            string ps = new string('=', rw / 2);

            WriteLine($"{ps} {text} {ps}{(rw % 2 == 0 ? "" : "=")}");
        }

        internal static void PrintColorDescription(ConsoleColor col, string desc)
        {
            Print("       ### ", col);
            PrintLine(desc, ConsoleColor.White);
        }

        internal static void PrintGraph(int padding, int width, string descr, params (double, ConsoleColor)[] values)
        {
            double sum = (from v in values select v.Item1).Sum();

            width -= 2;
            values = (from v in values select (v.Item1 / sum * width, v.Item2)).ToArray();

            double max = (from v in values select v.Item1).Max();
            int rem = width - (from v in values select (int)v.Item1).Sum();
            (double, ConsoleColor) elem = (from v in values where v.Item1 == max select v).First();
            int ndx = Array.IndexOf(values, elem);

            // this is by value not by reference!
            elem = values[ndx];
            elem.Item1 += rem;
            values[ndx] = elem;

            Print($"{new string(' ', padding)}[", ConsoleColor.White);

            foreach ((double, ConsoleColor) v in values)
                Print(new string('#', (int)v.Item1), v.Item2);

            PrintLine($"] {descr ?? ""}", ConsoleColor.White);
        }

        public static int Main(string[] argv)
        {
            #region REFLECTION + INVOCATION

            ForegroundColor = ConsoleColor.White;
            
            List<(string Name, int Passed, int Failed, int Skipped, long TimeCtor, long TimeInit, long TimeMethod)> partial_results = new List<(string, int, int, int, long, long, long)>();
            int passed = 0, failed = 0, skipped = 0;
            Stopwatch sw = new Stopwatch();
            long swc, swi, swm;

            foreach (Type t in from t in typeof(TestCommons).Assembly.GetTypes()
                               let attr = t.GetCustomAttributes<TestClassAttribute>(true).FirstOrDefault()
                               where attr != null
                               orderby t.Name ascending
                               select t)
            {
                sw.Restart();
                swc = swi = swm = 0;

                dynamic container = Activator.CreateInstance(t);
                MethodInfo init = t.GetMethod("Test_Init");
                int tp = 0, tf = 0, ts = 0, pleft = 0;

                WriteLine($"Testing class '{t.FullName}'");

                AddTime(ref swc, sw);

                foreach (MethodInfo nfo in t.GetMethods().OrderBy(_ => _.Name))
                    if (nfo.GetCustomAttributes<TestMethodAttribute>().FirstOrDefault() != null)
                    {
                        Write("\t[");
                        pleft = CursorLeft;
                        Write($"    ] Testing '{t.FullName}.{nfo.Name}'");

                        try
                        {
                            if (nfo.GetCustomAttributes<SkipAttribute>().FirstOrDefault() != null)
                                TestCommons.Skip();

                            init.Invoke(container, new object[0]);

                            AddTime(ref swi, sw);

                            nfo.Invoke(container, new object[0]);

                            ForegroundColor = ConsoleColor.Green;

                            CursorLeft = pleft;

                            WriteLine("PASS");
                            ForegroundColor = ConsoleColor.White;

                            ++passed;
                            ++tp;
                        }
                        catch (Exception ex)
                        when ((ex is SkippedException) || (ex?.InnerException is SkippedException))
                        {
                            ++skipped;
                            ++ts;

                            ForegroundColor = ConsoleColor.Yellow;
                            CursorLeft = pleft;

                            WriteLine("SKIP");

                            ForegroundColor = ConsoleColor.White;
                        }
                        catch (Exception ex)
                        {
                            ++failed;
                            ++tf;

                            ForegroundColor = ConsoleColor.Red;
                            CursorLeft = pleft;

                            WriteLine("FAIL");

                            while (ex != null)
                            {
                                WriteLine($"\t\t  {ex.Message}\n{string.Join("\n", ex.StackTrace.Split('\n').Select(x => $"\t\t{x}"))}");

                                ex = ex.InnerException;
                            }

                            ForegroundColor = ConsoleColor.White;
                        }

                        AddTime(ref swm, sw);
                    }

                AddTime(ref swc, sw);

                partial_results.Add((t.FullName, tp, ts, tf, swc, swi, swm));
            }

            #endregion
            #region PRINT RESULTS

            const int wdh = 110;
            int total = passed + failed + skipped;
            double time = (from r in partial_results select r.TimeCtor + r.TimeInit + r.TimeMethod).Sum();
            double pr = passed / (double)total;
            double sr = skipped / (double)total;
            double tr;
            const int i_wdh = wdh - 35;

            WriteLine();
            PrintHeader("TEST RESULTS", wdh);

            PrintGraph(0, wdh, "", (pr, ConsoleColor.Green),
                                   (sr, ConsoleColor.Yellow),
                                   (1 - pr - sr, ConsoleColor.Red));
            Print($@"
    MODULES: {partial_results.Count}
    TOTAL:   {passed + failed + skipped}
    PASSED:  {passed} ({pr * 100:F3} %)
    SKIPPED: {skipped} ({sr * 100:F3} %)
    FAILED:  {failed} ({(1 - pr - sr) * 100:F3} %)
    TIME:    {time * 1000d / Stopwatch.Frequency:F3} ms
    DETAILS:", ConsoleColor.White);

            foreach (var res in partial_results)
            {
                double mtime = res.TimeCtor + res.TimeInit + res.TimeMethod;
                double tot = res.Passed + res.Failed + res.Skipped;

                pr = res.Passed / tot;
                sr = res.Failed / tot;
                tr = mtime / time;

                WriteLine($@"
        MODULE:  {res.Name}
        PASSED:  {res.Passed} ({pr * 100:F3} %)
        SKIPPED: {res.Failed} ({sr * 100:F3} %)
        FAILED:  {res.Skipped} ({(1 - pr - sr) * 100:F3} %)
        TIME:    {mtime * 1000d / Stopwatch.Frequency:F3} ms ({tr * 100d:F3} %)");
                PrintGraph(8, i_wdh, "TIME/TOTAL", (tr, ConsoleColor.Magenta),
                                                   (1 - tr, ConsoleColor.DarkMagenta));
                PrintGraph(8, i_wdh, "TIME DISTR", (res.TimeCtor, ConsoleColor.DarkBlue),
                                                   (res.TimeInit, ConsoleColor.Blue),
                                                   (res.TimeMethod, ConsoleColor.Cyan));
                PrintGraph(8, i_wdh, "PASS/SKIP/FAIL", (res.Passed, ConsoleColor.Green),
                                                       (res.Failed, ConsoleColor.Yellow),
                                                       (res.Skipped, ConsoleColor.Red));
            }

            WriteLine("\n    GRAPH COLORS:");
            PrintColorDescription(ConsoleColor.Green, "Passed test methods");
            PrintColorDescription(ConsoleColor.Yellow, "Skipped test methods");
            PrintColorDescription(ConsoleColor.Red, "Failed test methods");
            PrintColorDescription(ConsoleColor.Magenta, "Time used for testing (relative to the total time)");
            PrintColorDescription(ConsoleColor.DarkBlue, "Time used for the module's static and instance constructors/destructors (.cctor, .ctor and .dtor)");
            PrintColorDescription(ConsoleColor.Blue, "Time used for the test initialization and cleanup method (@before and @after)");
            PrintColorDescription(ConsoleColor.Cyan, "Time used for the test method (@test)");
            WriteLine();
            //PrintHeader("DETAILED TEST METHOD RESULTS", wdh);
            //WriteLine();
            WriteLine(new string('=', wdh));

            if (Debugger.IsAttached)
            {
                WriteLine("\nPress any key to exit ....");
                ReadKey(true);
            }

            return failed; // NO FAILED TEST --> EXITCODE = 0

            #endregion
        }
    }

    public abstract class TestCommons
    {
        private static readonly ExpressionParser _parser = new ExpressionParser(true);


        static TestCommons()
        {
            _parser.Initialize();

        }

        [TestInitialize]
        public virtual void Test_Init()
        {
        }

        public static void Skip() => throw new SkippedException();

        public static MULTI_EXPRESSION[] PaseMultiexpressions(string s) => _parser.Parse(s).ToArray();

        public static EXPRESSION ParseExpression(string s) => (PaseMultiexpressions(s)[0] as MULTI_EXPRESSION.SingleValue)?.Item;

        public static EXPRESSION ProcessExpression(EXPRESSION expr) => Refactorings.ProcessExpression(expr);

        public static string ToString(EXPRESSION expr) => ExpressionAST.Print(expr);

        public static bool AreEqual(EXPRESSION e1, EXPRESSION e2) => e1 is EXPRESSION x && e2 is EXPRESSION y && (x == y || x.Equals(y));

        public static void AssertValidExpression(string s) => Assert.IsNotNull(ParseExpression(s));

        public static void AssertInvalidExpression(string s)
        {
            try
            {
                ParseExpression(s);

                Assert.Fail();
            }
            catch
            {
            }
        }

        public static void AssertEqualExpressions(string e1, string e2) => Assert.IsTrue(AreEqual(ParseExpression(e1), ParseExpression(e2)));

        public static void AssertEqualProcessedExpressions(string e1, string e2) => Assert.IsTrue(AreEqual(ProcessExpression(ParseExpression(e1)), ProcessExpression(ParseExpression(e2))));
    }

    public sealed class SkippedException
        : Exception
    {
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SkipAttribute
        : Attribute
    {
    }
}
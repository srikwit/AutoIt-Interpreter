﻿using System.Collections.Concurrent;
using System.Linq;
using System;

using Unknown6656.AutoIt3.CLI;
using Unknown6656.Common;

namespace Unknown6656.AutoIt3.Runtime
{
    /// <summary>
    /// Represents an AutoIt execution thread. The thread consists of multiple call frames which in term represent function invocations.
    /// </summary>
    public sealed class AU3Thread
        : IDisposable
        , IEquatable<AU3Thread>
    {
        private static volatile int _tid = 0;
        private readonly ConcurrentStack<CallFrame> _callstack = new ConcurrentStack<CallFrame>();
        private volatile bool _running = false;
        private int? _override_exitcode = null;


        /// <summary>
        /// The <see cref="Runtime.Interpreter"/> on which the current thread has been created.
        /// </summary>
        public Interpreter Interpreter { get; }

        /// <summary>
        /// Indicates whether the current thread is actively running.
        /// </summary>
        public bool IsRunning => _running;

        /// <summary>
        /// The top-most <see cref="CallFrame"/>, representing the top-most function invocation of this thread.
        /// </summary>
        public CallFrame? CurrentFrame => _callstack.TryPeek(out CallFrame? lp) ? lp : null;

        /// <summary>
        /// The <see cref="SourceLocation"/> of the currently executed statement or line.
        /// </summary>
        public SourceLocation? CurrentLocation => CallStack.OfType<AU3CallFrame>().FirstOrDefault()?.CurrentLocation ?? SourceLocation.Unknown;

        /// <summary>
        /// The currently executed function.
        /// </summary>
        public ScriptFunction? CurrentFunction => CurrentFrame?.CurrentFunction;

        public VariableScope CurrentVariableResolver => CurrentFrame?.VariableResolver ?? Interpreter.VariableResolver;

        /// <summary>
        /// The current call stack (function invocation stack) of this thread. The first item of the collection represents the top-most thread and holds the same value as <see cref="CurrentFrame"/>.
        /// </summary>
        public CallFrame[] CallStack => CurrentFrame.Propagate(frame => (frame?.CallerFrame, frame is { })).ToArrayWhere(frame => frame is { })!;

        /// <summary>
        /// Indicates whether the current thread has been disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Indicates whether the current thread is the <see cref="Interpreter.MainThread"/> of the associated <see cref="Runtime.Interpreter"/>.
        /// </summary>
        public bool IsMainThread => ReferenceEquals(this, Interpreter.MainThread);

        /// <summary>
        /// The unique ID of this thread.
        /// </summary>
        public int ThreadID { get; }


        internal AU3Thread(Interpreter interpreter)
        {
            ThreadID = ++_tid;
            Interpreter = interpreter;
            Interpreter.AddThread(this);

            MainProgram.PrintfDebugMessage("debug.au3thread.created", this);
        }

        /// <summary>
        /// Starts the current thread by invoking the given <paramref name="ScriptFunction"/> with the given arguments.
        /// <para/>
        /// This function is blocking and returns only after the given function has been invoked.
        /// </summary>
        /// <param name="function">The function to be invoked.</param>
        /// <param name="args">The arguments to be passed to the function.</param>
        /// <returns>The functions return value or execution error.</returns>
        public FunctionReturnValue Start(ScriptFunction function, Variant[] args) => Interpreter.Telemetry.Measure(TelemetryCategory.ThreadRun, delegate
        {
            if (_running)
                return InterpreterError.WellKnown(CurrentLocation, "error.thread_already_running", ThreadID);
            else
                _running = true;

            FunctionReturnValue result = Call(function, args);

            Stop();

            if (_override_exitcode is int code)
                return Variant.FromNumber(code);

            return result;
        });

        /// <summary>
        /// <b>[UNSAFE!]</b>
        /// Invokes the given <paramref name="ScriptFunction"/> with the given arguments. A call to this function is considered to be unsafe, as any non-concurrent call may result into undefined behavior.
        /// Use <see cref="Start"/> instead.
        /// <para/>
        /// This function is blocking and returns only after the given function has been invoked.
        /// </summary>
        /// <param name="function">The function to be invoked.</param>
        /// <param name="args">The arguments to be passed to the function.</param>
        /// <returns>The functions return value or execution error.</returns>
        public FunctionReturnValue Call(ScriptFunction function, Variant[] args)
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(AU3Thread));

            CallFrame? old = CurrentFrame;
            using CallFrame frame = function switch
            {
                AU3Function f => new AU3CallFrame(this, old, f, args),
                NativeFunction f => new NativeCallFrame(this, old, f, args),
                _ => throw new ArgumentException($"A function of the type '{function}' cannot be handled by the current thread '{this}'.", nameof(function)),
            };

            _callstack.Push(frame);

            FunctionReturnValue result = frame.Execute(args);

            while (!ReferenceEquals(CurrentFrame, old))
                ExitCall();

            return result;
        }

        /// <summary>
        /// Stops the current thread execution. This will dispose the current thread.
        /// </summary>
        public void Stop()
        {
            _running = false;

            Dispose();
        }

        /// <summary>
        /// Stops the current thread execution with the given exit code. This will dispose the current thread.
        /// </summary>
        /// <param name="exitcode">Exit code, with which the current thread will return.</param>
        public void Stop(int exitcode)
        {
            Stop();

            _override_exitcode = exitcode;
        }

        /// <summary>
        /// [UNSAFE!!!!]
        /// </summary>
        /// <param name="is_running"></param>
        internal void UnsafeSetIsRunning(bool is_running) => _running = is_running;

        internal SourceLocation? ExitCall()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(AU3Thread));

            _callstack.TryPop(out CallFrame? frame);
            frame?.Dispose();

            return CurrentLocation;
        }

        internal AU3CallFrame PushAnonymousCallFrame()
        {
            AU3CallFrame frame = new AU3CallFrame(this, CurrentFrame, Interpreter.ScriptScanner.AnonymousFunction, Array.Empty<Variant>());

            _callstack.Push(frame);

            return frame;
        }

        /// <inheritdoc/>
        public override string ToString() => $"0x{ThreadID:x4}{(IsMainThread ? " (main)" : "")} @ {CurrentLocation}";

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Interpreter, ThreadID);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is AU3Thread thread && Equals(thread);

        /// <inheritdoc/>
        public bool Equals(AU3Thread? other) => Interpreter == other?.Interpreter && ThreadID == other?.ThreadID;

        /// <inheritdoc/>
        public void Dispose()
        {
            _running = false;

            if (IsDisposed)
                return;
            else
                IsDisposed = true;

            Interpreter.RemoveThread(this);

            while (_callstack.TryPop(out CallFrame? frame))
                frame?.Dispose();

            MainProgram.PrintfDebugMessage("debug.au3thread.disposed", this);
        }
    }
}

using System.Collections.Concurrent;
using TreeSitter;

namespace Waymark.Languages.Python;

/// <summary>The tree-sitter native library or the Python grammar could not be loaded on this computer.</summary>
internal sealed class GrammarUnavailableException(string message, Exception inner) : Exception(message, inner)
{
}

/// <summary>A parser on loan from <see cref="PythonGrammar"/>; disposing it gives the parser back.</summary>
internal readonly struct ParserLease(Parser parser) : IDisposable
{
    public Parser Parser { get; } = parser;

    public void Dispose() => PythonGrammar.Return(Parser);
}

/// <summary>
/// Loads the Python grammar once. A parser is never used by two threads at the same time: a file borrows one with <see cref="RentParser"/>
/// and gives it back, so the native parser is created a few times and not once per file.
/// </summary>
internal static class PythonGrammar
{
    // The bindings name a grammar by its language name ("JavaScript" in their own sample); the lower-case spelling is a fallback.
    private static readonly string[] Names = ["Python", "python"];

    private static readonly Lazy<Language> Instance = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentQueue<Parser> Idle = new();
    private static readonly int MaxIdle = Math.Max(4, Environment.ProcessorCount * 2);
    private static int _idleCount;

    public static Parser CreateParser()
    {
        try
        {
            return new Parser(Instance.Value);
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            throw new GrammarUnavailableException("The tree-sitter parser could not be created.", ex);
        }
    }

    public static ParserLease RentParser()
    {
        if (Idle.TryDequeue(out var parser))
        {
            Interlocked.Decrement(ref _idleCount);
            return new ParserLease(parser);
        }

        return new ParserLease(CreateParser());
    }

    internal static void Return(Parser parser)
    {
        try
        {
            parser.Reset();
        }
        catch (Exception)
        {
            // A parser that cannot be reset is not trusted with the next file.
            parser.Dispose();
            return;
        }

        if (Interlocked.Increment(ref _idleCount) > MaxIdle)
        {
            Interlocked.Decrement(ref _idleCount);
            parser.Dispose();
            return;
        }

        Idle.Enqueue(parser);
    }

    private static Language Load()
    {
        Exception? last = null;
        foreach (var name in Names)
        {
            try
            {
                return new Language(name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                last = ex;
            }
        }

        throw new GrammarUnavailableException("The tree-sitter Python grammar could not be loaded.", last ?? new InvalidOperationException("No grammar name was tried."));
    }

    private static bool IsNativeFailure(Exception ex) =>
        ex is DllNotFoundException or BadImageFormatException or TypeInitializationException or EntryPointNotFoundException;
}
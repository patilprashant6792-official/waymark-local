using System.Text;
using Microsoft.ML.Tokenizers;
using Waymark.Core.Audit;
using Waymark.Core.Tools;

namespace Waymark.Host.Mcp;

/// <summary>
/// Counts tokens with OpenAI's o200k_base encoding (the one GPT-4o and newer models use), through Microsoft.ML.Tokenizers.
/// The vocabulary is loaded once, on first use, and shared. If it cannot be loaded or a text cannot be counted, the count falls back
/// to the size-based estimate and the problem is logged, because a tool call must never fail over a statistic.
/// </summary>
public sealed class OpenAiContextMeter(ILogger<OpenAiContextMeter> logger) : IContextMeter
{
    public const string EncodingName = "o200k_base";

    private readonly Lazy<Tokenizer?> _tokenizer = new(() => Load(logger), LazyThreadSafetyMode.ExecutionAndPublication);

    public int CountTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var tokenizer = _tokenizer.Value;
        if (tokenizer is null)
        {
            return Estimate(text);
        }

        try
        {
            return tokenizer.CountTokens(text, true, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "A tool result could not be tokenized, so its token count is estimated from its size");
            return Estimate(text);
        }
    }

    private static int Estimate(string text) =>
        (int)Math.Min(int.MaxValue, TokenEstimate.FromBytes(Encoding.UTF8.GetByteCount(text)));

    private static Tokenizer? Load(ILogger logger)
    {
        try
        {
            return TiktokenTokenizer.CreateForEncoding(EncodingName, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The OpenAI tokenizer ({Encoding}) could not be loaded, so token counts are estimated from size", EncodingName);
            return null;
        }
    }
}

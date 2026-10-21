namespace Waymark.Core.Tools;

/// <summary>Measures how much room text takes in the AI's context, in tokens.</summary>
public interface IContextMeter
{
    /// <summary>The number of tokens in the text; 0 for null or empty text. Never throws for text that cannot be tokenized.</summary>
    int CountTokens(string? text);
}

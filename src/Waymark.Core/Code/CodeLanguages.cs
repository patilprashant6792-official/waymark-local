namespace Waymark.Core.Code;

/// <summary>The words each language is shown with, so renderers do not repeat the same switch.</summary>
public static class CodeLanguages
{
    /// <summary>The info string of a markdown code fence.</summary>
    public static string Fence(CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => "csharp",
        CodeLanguage.Python => "python",
        CodeLanguage.TypeScript => "typescript",
        CodeLanguage.JavaScript => "javascript",
        _ => "text",
    };

    public static string DisplayName(CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => "C#",
        CodeLanguage.Python => "Python",
        CodeLanguage.TypeScript => "TypeScript",
        CodeLanguage.JavaScript => "JavaScript",
        _ => language.ToString(),
    };

    /// <summary>What the dotted container of a type is called: a namespace in C#, a module in Python.</summary>
    public static string ContainerWord(CodeLanguage language) => language == CodeLanguage.CSharp ? "Namespace" : "Module";

    /// <summary>What the import lines of a file are called in the outline.</summary>
    public static string ImportsTitle(CodeLanguage language) => language == CodeLanguage.CSharp ? "Using Directives" : "Imports";

    /// <summary>Decorators and attributes are written differently: <c>[Route("x")]</c> in C#, <c>@app.get("x")</c> in Python.</summary>
    public static string FormatAttribute(CodeLanguage language, string attribute) =>
        language == CodeLanguage.CSharp ? $"[{attribute}]" : $"@{attribute}";
}

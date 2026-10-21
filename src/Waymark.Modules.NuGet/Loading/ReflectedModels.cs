namespace Waymark.Modules.NuGet.Loading;

public enum ReflectedTypeKind
{
    Class,
    Interface,
    Enum,
    Struct,
    Delegate
}

public sealed record ReflectedParameter(string Name, string ParameterType);

public sealed record ReflectedMethod(
    string Name,
    string ReturnType,
    IReadOnlyList<ReflectedParameter> Parameters,
    string Visibility,
    bool IsStatic);

public sealed record ReflectedProperty(
    string Name,
    string PropertyType,
    string Visibility,
    bool CanRead,
    bool CanWrite,
    bool IsStatic);

public sealed record ReflectedField(string Name, string FieldType, string Visibility, bool IsStatic, bool IsReadOnly);

public sealed record ReflectedEvent(string Name, string EventType, string Visibility, bool IsStatic);

public sealed record ReflectedConstructor(IReadOnlyList<ReflectedParameter> Parameters, string Visibility);

/// <summary>One public type with its public members. Nested types are named Outer.Inner.</summary>
public sealed record ReflectedType(
    string Namespace,
    string TypeName,
    ReflectedTypeKind Kind,
    IReadOnlyList<ReflectedMethod> Methods,
    IReadOnlyList<ReflectedProperty> Properties,
    IReadOnlyList<ReflectedField> Fields,
    IReadOnlyList<ReflectedEvent> Events,
    IReadOnlyList<ReflectedConstructor> Constructors);

/// <summary>The raw XML documentation file that sits next to one of the package's assemblies.</summary>
public sealed record DocumentationFile(string FileName, string Xml);

/// <summary>Everything the loader learns from one package: the reflected types and the raw documentation files.</summary>
public sealed record ReflectedPackage(
    string ResolvedVersion,
    IReadOnlyList<ReflectedType> Types,
    IReadOnlyList<DocumentationFile> Documentation);

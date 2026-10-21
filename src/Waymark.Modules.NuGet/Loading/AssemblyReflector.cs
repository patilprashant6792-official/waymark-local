using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>Lists the public types and members of one assembly without loading it into the process (MetadataLoadContext).</summary>
internal sealed partial class AssemblyReflector(ILogger<AssemblyReflector> logger)
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly HashSet<string> ExcludedMethods = new(StringComparer.Ordinal)
    {
        "GetHashCode", "ToString", "Equals", "GetType", "Finalize", "MemberwiseClone"
    };

    /// <param name="assemblyPath">The assembly to read.</param>
    /// <param name="resolverAssemblies">Sibling and dependency assemblies used to resolve types referenced by signatures.</param>
    /// <exception cref="MissingSiblingAssembliesException">A referenced assembly could not be found among the resolver assemblies.</exception>
    public IReadOnlyList<ReflectedType> Reflect(string assemblyPath, IReadOnlyList<string> resolverAssemblies, CancellationToken cancellationToken)
    {
        if (!File.Exists(assemblyPath))
        {
            logger.LogWarning("Assembly file not found: {AssemblyPath}", assemblyPath);
            return [];
        }

        // The assembly itself and its real dependencies win over the generic runtime paths when names collide. The resolver
        // throws when two paths share a file name, so duplicates are removed by file name, not by full path.
        var paths = new List<string>(resolverAssemblies.Count + FrameworkAssemblyPaths.All.Count + 1) { assemblyPath };
        paths.AddRange(resolverAssemblies);
        paths.AddRange(FrameworkAssemblyPaths.All);
        var distinct = paths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();

        var result = new List<ReflectedType>();
        try
        {
            using var context = new MetadataLoadContext(new PathAssemblyResolver(distinct));
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            foreach (var type in assembly.GetTypes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((!type.IsPublic && !type.IsNestedPublic) || string.IsNullOrEmpty(type.Namespace))
                {
                    continue;
                }

                result.Add(BuildType(type));
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            // GetTypes fails for the whole assembly as soon as one referenced type is unresolved, so name what is missing.
            var missing = ex.LoaderExceptions
                .OfType<FileNotFoundException>()
                .Select(MissingAssemblyName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            logger.LogError(ex, "Type load failure in {AssemblyPath}; missing: {Missing}", assemblyPath, string.Join(", ", missing));
            if (missing.Count > 0)
            {
                throw new MissingSiblingAssembliesException(Path.GetFileNameWithoutExtension(assemblyPath), null, missing);
            }

            throw new InvalidOperationException($"Failed to load types from assembly '{Path.GetFileName(assemblyPath)}'.", ex);
        }
        catch (FileNotFoundException ex)
        {
            var name = MissingAssemblyName(ex) ?? "unknown (see the server log)";
            logger.LogError(ex, "Missing dependency while loading {AssemblyPath}: {Missing}", assemblyPath, name);
            throw new MissingSiblingAssembliesException(Path.GetFileNameWithoutExtension(assemblyPath), null, [name]);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidOperationException($"Invalid assembly format: {Path.GetFileName(assemblyPath)}", ex);
        }

        return result;
    }

    private ReflectedType BuildType(Type type)
    {
        var ns = type.Namespace!;
        var name = type.IsNested
            ? $"{TypeNameFormatter.Simplify(type.DeclaringType!.Name)}.{TypeNameFormatter.Simplify(type.Name)}"
            : TypeNameFormatter.Simplify(type.Name);

        IReadOnlyList<ReflectedMethod> methods = [];
        IReadOnlyList<ReflectedProperty> properties = [];
        IReadOnlyList<ReflectedField> fields = [];
        IReadOnlyList<ReflectedEvent> events = [];
        IReadOnlyList<ReflectedConstructor> constructors = [];

        try
        {
            methods = type.GetMethods(DeclaredMembers)
                .Where(method => !ExcludedMethods.Contains(method.Name) && !method.IsSpecialName)
                .Select(method => new ReflectedMethod(
                    method.Name, SafeTypeName(method.ReturnType), Parameters(method), Visibility(method), method.IsStatic))
                .ToList();

            properties = CollectProperties(type);

            fields = type.GetFields(DeclaredMembers)
                .Select(field => new ReflectedField(
                    field.Name, SafeTypeName(field.FieldType), FieldVisibility(field), field.IsStatic, field.IsInitOnly))
                .ToList();

            events = type.GetEvents(DeclaredMembers)
                .Select(item => new ReflectedEvent(
                    item.Name,
                    item.EventHandlerType is null ? string.Empty : SafeTypeName(item.EventHandlerType),
                    Visibility(item.AddMethod ?? item.RemoveMethod),
                    (item.AddMethod?.IsStatic ?? item.RemoveMethod?.IsStatic) ?? false))
                .ToList();

            constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(constructor => new ReflectedConstructor(Parameters(constructor), Visibility(constructor)))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read all members of {TypeName}", type.FullName);
        }

        return new ReflectedType(ns, name, KindOf(type), methods, properties, fields, events, constructors);
    }

    /// <summary>
    /// Walks the base-type chain so inherited properties are listed (generated SDK models keep theirs on a base class).
    /// The most derived declaration wins. A base type that cannot be resolved ends the walk without losing the properties found so far.
    /// </summary>
    private List<ReflectedProperty> CollectProperties(Type type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReflectedProperty>();

        for (var current = type; current is not null; current = SafeBaseType(current))
        {
            try
            {
                foreach (var property in current.GetProperties(DeclaredMembers))
                {
                    try
                    {
                        if (!seen.Add(property.Name))
                        {
                            continue;
                        }

                        // Init-only setters carry a modifier the context cannot resolve, so CanRead is unreliable for records:
                        // a property with any accessor counts as readable.
                        var getter = property.GetMethod;
                        var setter = property.SetMethod;
                        if (getter is null && setter is null)
                        {
                            continue;
                        }

                        result.Add(new ReflectedProperty(
                            property.Name,
                            SafeTypeName(property.PropertyType),
                            Visibility(getter ?? setter),
                            true,
                            property.CanWrite || setter is not null,
                            (getter?.IsStatic ?? setter?.IsStatic) ?? false));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Skipping property {Type}.{Property}: accessor unresolvable", current.Name, property.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Stopping the property scan of {Type} at {Base}", type.Name, current.Name);
                break;
            }
        }

        return result;
    }

    private static List<ReflectedParameter> Parameters(MethodBase method) =>
        method.GetParameters().Select(parameter => new ReflectedParameter(parameter.Name ?? string.Empty, SafeTypeName(parameter.ParameterType))).ToList();

    private static ReflectedTypeKind KindOf(Type type)
    {
        if (type.IsEnum)
        {
            return ReflectedTypeKind.Enum;
        }

        if (type.IsInterface)
        {
            return ReflectedTypeKind.Interface;
        }

        if (type.IsValueType)
        {
            return ReflectedTypeKind.Struct;
        }

        return SafeBaseType(type)?.FullName == "System.MulticastDelegate" ? ReflectedTypeKind.Delegate : ReflectedTypeKind.Class;
    }

    private static Type? SafeBaseType(Type type)
    {
        try
        {
            return type.BaseType;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>FullName is null for open generics and throws for unresolved references; the short name is the fallback.</summary>
    private static string SafeTypeName(Type type)
    {
        try
        {
            return type.FullName ?? type.Name;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
        {
            return type.Name;
        }
    }

    private static string Visibility(MethodBase? method)
    {
        if (method is null)
        {
            return "private";
        }

        if (method.IsPublic)
        {
            return "public";
        }

        if (method.IsFamilyOrAssembly)
        {
            return "protected internal";
        }

        if (method.IsFamily)
        {
            return "protected";
        }

        return method.IsAssembly ? "internal" : "private";
    }

    private static string FieldVisibility(FieldInfo field)
    {
        if (field.IsPublic)
        {
            return "public";
        }

        if (field.IsFamilyOrAssembly)
        {
            return "protected internal";
        }

        if (field.IsFamily)
        {
            return "protected";
        }

        return field.IsAssembly ? "internal" : "private";
    }

    /// <summary>The simple name of the assembly a FileNotFoundException is about; it is in FileName or, failing that, quoted in the message.</summary>
    private static string? MissingAssemblyName(FileNotFoundException ex)
    {
        var candidate = ex.FileName;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var quoted = QuotedName().Match(ex.Message);
            candidate = quoted.Success ? quoted.Groups[1].Value : null;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            return new AssemblyName(candidate).Name ?? candidate;
        }
        catch (Exception ex2) when (ex2 is ArgumentException or FileLoadException)
        {
            var simple = Path.GetFileNameWithoutExtension(candidate);
            return string.IsNullOrWhiteSpace(simple) ? candidate : simple;
        }
    }

    [GeneratedRegex("'([^']+)'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedName();
}

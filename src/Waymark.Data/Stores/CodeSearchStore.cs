using System.Globalization;
using System.Text;
using Dapper;
using Waymark.Core.Code;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class CodeSearchStore(ISqliteConnectionFactory connections) : ICodeSearchStore
{
    private const int MaxNames = 500;

    private const string TypeColumns =
        "SELECT t.name AS Name, t.full_name AS FullName, t.kind AS Kind, t.namespace AS Namespace, t.base_list AS BaseList, " +
        "f.rel_path AS RelativePath, t.start_line AS Line FROM code_types t JOIN code_files f ON f.id = t.file_id ";

    private const string TypeSelect =
        "SELECT f.workspace_id AS WorkspaceId, f.rel_path AS RelativePath, 1 AS IsType, t.kind AS Kind, t.name AS Name, " +
        "NULL AS Owner, NULL AS Signature, t.modifiers AS Modifiers, t.attributes AS Attributes, t.base_list AS BaseList, t.start_line AS Line " +
        "FROM code_types t JOIN code_files f ON f.id = t.file_id ";

    private const string MemberSelect =
        "SELECT f.workspace_id AS WorkspaceId, f.rel_path AS RelativePath, 0 AS IsType, m.kind AS Kind, m.name AS Name, " +
        "o.name AS Owner, m.signature AS Signature, m.modifiers AS Modifiers, m.attributes AS Attributes, NULL AS BaseList, m.start_line AS Line " +
        "FROM code_members m JOIN code_files f ON f.id = m.file_id LEFT JOIN code_types o ON o.id = m.type_id ";

    public async Task<CodeSymbolResult> FindSymbolsAsync(CodeSymbolQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Tokens.Count == 0)
        {
            throw new ArgumentException("A search needs at least one word.", nameof(query));
        }

        if (query.WorkspaceIds.Count == 0)
        {
            return new CodeSymbolResult([], false);
        }

        var workspaces = string.Join(',', query.WorkspaceIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var typeKinds = query.TypeKinds.Select(k => EnumText.ToDb(k)).ToList();
        var memberKinds = query.MemberKinds.Select(k => EnumText.ToDb(k)).ToList();

        await using var connection = await connections.OpenAsync(ct);
        var rows = new List<CodeSymbolRow>();
        var capped = false;

        var typeSql = BuildSql(TypeSelect, "t", "f", workspaces, typeKinds, query, query.IncludeBaseList, out var typeParameters);
        if (typeSql is not null)
        {
            var found = (await connection.QueryAsync<Row>(new CommandDefinition(typeSql, typeParameters, cancellationToken: ct))).ToList();
            capped |= found.Count > query.Limit;
            rows.AddRange(found.Take(query.Limit).Select(ToRow));
        }

        var memberSql = BuildSql(MemberSelect, "m", "f", workspaces, memberKinds, query, false, out var memberParameters);
        if (memberSql is not null)
        {
            var found = (await connection.QueryAsync<Row>(new CommandDefinition(memberSql, memberParameters, cancellationToken: ct))).ToList();
            capped |= found.Count > query.Limit;
            rows.AddRange(found.Take(query.Limit).Select(ToRow));
        }

        return new CodeSymbolResult(rows, capped);
    }

    public async Task<CodeCallSiteResult> FindCallSitesAsync(long workspaceId, string calleeName, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calleeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await connections.OpenAsync(ct);
        var found = (await connection.QueryAsync<CallRow>(new CommandDefinition(
            "SELECT f.rel_path AS RelativePath, c.caller_type AS CallerType, c.caller_member AS CallerMember, c.receiver AS Receiver, c.line AS Line, " +
            "c.receiver_type AS ReceiverType, c.receiver_guessed AS ReceiverGuessed " +
            "FROM code_call_sites c JOIN code_files f ON f.id = c.file_id " +
            "WHERE f.workspace_id = @workspaceId AND c.callee_name = @calleeName COLLATE BINARY " +
            "ORDER BY f.rel_path, c.line, c.id LIMIT @fetch",
            new { workspaceId, calleeName, fetch = limit + 1 },
            cancellationToken: ct))).ToList();

        return new CodeCallSiteResult(
            found.Take(limit).Select(r => new CodeCallSiteRow(r.RelativePath, r.CallerType, r.CallerMember, r.Receiver, (int)r.Line, r.ReceiverType, r.ReceiverGuessed != 0)).ToList(),
            found.Count > limit);
    }

    public async Task<CodeTypeRelations> FindTypeRelationsAsync(long workspaceId, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await connections.OpenAsync(ct);
        var found = (await connection.QueryAsync<TypeRow>(new CommandDefinition(
            TypeColumns + "WHERE f.workspace_id = @workspaceId AND t.base_list IS NOT NULL ORDER BY f.rel_path, t.start_line LIMIT @fetch",
            new { workspaceId, fetch = limit + 1 },
            cancellationToken: ct))).ToList();

        return new CodeTypeRelations(found.Take(limit).Select(ToTypeRow).ToList(), found.Count > limit);
    }

    public async Task<IReadOnlyList<CodeTypeRow>> FindTypesByNameAsync(long workspaceId, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(names.Count, MaxNames);
        if (names.Count == 0)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(ct);
        var found = await connection.QueryAsync<TypeRow>(new CommandDefinition(
            TypeColumns + "WHERE f.workspace_id = @workspaceId AND t.name COLLATE NOCASE IN @names ORDER BY f.rel_path, t.start_line",
            new { workspaceId, names = names.ToArray() },
            cancellationToken: ct));
        return found.Select(ToTypeRow).ToList();
    }

    public async Task<IReadOnlyList<CodeMemberLocation>> FindMembersByNameAsync(long workspaceId, string name, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await connections.OpenAsync(ct);
        var found = await connection.QueryAsync<MemberRow>(new CommandDefinition(
            "SELECT o.full_name AS OwnerFullName, m.kind AS Kind, m.signature AS Signature, f.rel_path AS RelativePath, m.start_line AS Line " +
            "FROM code_members m JOIN code_files f ON f.id = m.file_id LEFT JOIN code_types o ON o.id = m.type_id " +
            "WHERE f.workspace_id = @workspaceId AND m.name COLLATE NOCASE = @name AND m.name = @name " +
            "ORDER BY f.rel_path, m.start_line LIMIT @limit",
            new { workspaceId, name, limit },
            cancellationToken: ct));
        return found.Select(r => new CodeMemberLocation(r.OwnerFullName, r.Kind, r.Signature, r.RelativePath, (int)r.Line)).ToList();
    }

    public async Task<IReadOnlySet<string>> FindDeclaredValueNamesAsync(long workspaceId, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(names.Count, MaxNames);
        if (names.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await using var connection = await connections.OpenAsync(ct);
        var found = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT m.name FROM code_members m JOIN code_files f ON f.id = m.file_id " +
            "WHERE f.workspace_id = @workspaceId AND m.kind IN ('field', 'property', 'event', 'variable') AND m.name COLLATE NOCASE IN @names",
            new { workspaceId, names = names.ToArray() },
            cancellationToken: ct));
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        return found.Where(wanted.Contains).ToHashSet(StringComparer.Ordinal);
    }

    private static CodeTypeRow ToTypeRow(TypeRow row) =>
        new(row.Name, row.FullName, row.Kind, row.Namespace, row.BaseList, row.RelativePath, (int)row.Line);

    private static string? BuildSql(
        string select,
        string alias,
        string fileAlias,
        string workspaces,
        List<string> kinds,
        CodeSymbolQuery query,
        bool includeBaseList,
        out DynamicParameters parameters)
    {
        parameters = new DynamicParameters();
        var conditions = new List<string>();

        if (kinds.Count > 0)
        {
            var kindList = string.Join(',', kinds.Select(k => "'" + k + "'"));
            conditions.Add($"({alias}.kind IN ({kindList}) AND {NamePredicate($"{alias}.name", query, parameters)})");
        }

        if (query.IncludeAttributes)
        {
            conditions.Add($"({alias}.attributes IS NOT NULL AND {SubstringPredicate($"{alias}.attributes", "a", query, parameters)})");
        }

        if (includeBaseList)
        {
            conditions.Add($"({alias}.base_list IS NOT NULL AND {SubstringPredicate($"{alias}.base_list", "b", query, parameters)})");
        }

        if (conditions.Count == 0)
        {
            return null;
        }

        parameters.Add("fetch", query.Limit + 1);
        return $"{select}WHERE {fileAlias}.workspace_id IN ({workspaces}) AND ({string.Join(" OR ", conditions)}) " +
               $"ORDER BY length({alias}.name), {alias}.name, {fileAlias}.rel_path LIMIT @fetch";
    }

    private static string NamePredicate(string column, CodeSymbolQuery query, DynamicParameters parameters)
    {
        if (query.Tokens.Count > 1)
        {
            return SubstringPredicate(column, "n", query, parameters);
        }

        var pattern = query.CaseSensitive
            ? "*" + string.Join('*', query.Tokens[0].Select(EscapeGlob)) + "*"
            : "%" + string.Join('%', query.Tokens[0].Select(EscapeLike)) + "%";
        parameters.Add("n0", pattern);
        return Compare(column, "@n0", query.CaseSensitive);
    }

    private static string SubstringPredicate(string column, string prefix, CodeSymbolQuery query, DynamicParameters parameters)
    {
        var parts = new List<string>(query.Tokens.Count);
        for (var i = 0; i < query.Tokens.Count; i++)
        {
            var name = prefix + i.ToString(CultureInfo.InvariantCulture);
            parameters.Add(name, query.CaseSensitive
                ? "*" + Escape(query.Tokens[i], EscapeGlob) + "*"
                : "%" + Escape(query.Tokens[i], EscapeLike) + "%");
            parts.Add(Compare(column, "@" + name, query.CaseSensitive));
        }

        return "(" + string.Join(" AND ", parts) + ")";
    }

    private static string Compare(string column, string parameter, bool caseSensitive) =>
        caseSensitive ? $"{column} GLOB {parameter}" : $"{column} LIKE {parameter} ESCAPE '\\'";

    private static string Escape(string token, Func<char, string> escape)
    {
        var text = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            text.Append(escape(c));
        }

        return text.ToString();
    }

    private static string EscapeLike(char c) => c is '%' or '_' or '\\' ? "\\" + c : c.ToString();

    private static string EscapeGlob(char c) => c is '*' or '?' or '[' ? "[" + c + "]" : c.ToString();

    private static CodeSymbolRow ToRow(Row row) =>
        new(row.WorkspaceId, row.RelativePath, row.IsType != 0, row.Kind, row.Name, row.Owner, row.Signature, row.Modifiers, row.Attributes, row.BaseList, (int)row.Line);

    private sealed class Row
    {
        public long WorkspaceId { get; set; }

        public string RelativePath { get; set; } = string.Empty;

        public long IsType { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Owner { get; set; }

        public string? Signature { get; set; }

        public string? Modifiers { get; set; }

        public string? Attributes { get; set; }

        public string? BaseList { get; set; }

        public long Line { get; set; }
    }

    private sealed class CallRow
    {
        public string RelativePath { get; set; } = string.Empty;

        public string? CallerType { get; set; }

        public string? CallerMember { get; set; }

        public string? Receiver { get; set; }

        public string? ReceiverType { get; set; }

        public long ReceiverGuessed { get; set; }

        public long Line { get; set; }
    }

    private sealed class TypeRow
    {
        public string Name { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string? Namespace { get; set; }

        public string? BaseList { get; set; }

        public string RelativePath { get; set; } = string.Empty;

        public long Line { get; set; }
    }

    private sealed class MemberRow
    {
        public string? OwnerFullName { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Signature { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public long Line { get; set; }
    }
}

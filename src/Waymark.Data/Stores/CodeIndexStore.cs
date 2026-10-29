using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Code;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class CodeIndexStore(ISqliteConnectionFactory connections, IWriteQueue writes) : ICodeIndexStore
{
    private const char UsingSeparator = '\n';

    public async Task ReplaceFileAsync(long workspaceId, CodeFileIndex file, DateTimeOffset indexedUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        Validate(file);
        var result = await ApplyBatchAsync(workspaceId, [file], [], indexedUtc, ct);
        if (result.Failed.Count > 0)
        {
            throw new InvalidOperationException($"The code index could not store '{result.Failed[0].RelativePath}': {result.Failed[0].Message}");
        }
    }

    public Task<CodeBatchResult> ApplyBatchAsync(
        long workspaceId,
        IReadOnlyList<CodeFileIndex> replace,
        IReadOnlyList<string> remove,
        DateTimeOffset indexedUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(replace);
        ArgumentNullException.ThrowIfNull(remove);
        if (replace.Count == 0 && remove.Count == 0)
        {
            return Task.FromResult(new CodeBatchResult(0, 0, []));
        }

        return writes.ExecuteAsync<CodeBatchResult>(
            (connection, tx, k) =>
            {
                using var writer = new CodeIndexBatchWriter(connection, tx);
                return Task.FromResult(writer.Apply(workspaceId, replace, remove, indexedUtc, k));
            },
            ct);
    }

    public Task<bool> RemoveFileAsync(long workspaceId, string relativePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return writes.ExecuteAsync(
            (connection, tx, k) => DeleteFileAsync(connection, tx, workspaceId, relativePath, k),
            ct);
    }

    public Task<int> ClearWorkspaceAsync(long workspaceId, CancellationToken ct) =>
        writes.ExecuteAsync(
            (connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM code_files WHERE workspace_id = @workspaceId",
                new { workspaceId },
                tx,
                cancellationToken: k)),
            ct);

    public async Task<IReadOnlyList<CodeFileStamp>> ListStampsAsync(long workspaceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<StampRow>(new CommandDefinition(
            "SELECT rel_path, hash, size, mtime_utc FROM code_files WHERE workspace_id = @workspaceId ORDER BY rel_path",
            new { workspaceId },
            cancellationToken: ct));
        return rows.Select(ToStamp).ToList();
    }

    public async Task<IReadOnlyList<CodeFileStamp>> ListStampsUnderAsync(long workspaceId, string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var trimmed = folder.Trim('/');
        await using var connection = await connections.OpenAsync(ct);

        // Every path below the folder sorts between "folder/" and "folder0" ('0' is the character after '/'), so the unique index on
        // (workspace_id, rel_path) answers this as a range, with the same case folding the column uses.
        var rows = await connection.QueryAsync<StampRow>(new CommandDefinition(
            "SELECT rel_path, hash, size, mtime_utc FROM code_files WHERE workspace_id = @workspaceId AND rel_path >= @low AND rel_path < @high ORDER BY rel_path",
            new { workspaceId, low = trimmed + "/", high = trimmed + "0" },
            cancellationToken: ct));
        return rows.Select(ToStamp).ToList();
    }

    public async Task<IReadOnlyDictionary<string, CodeFileStamp>> GetStampsAsync(long workspaceId, IReadOnlyCollection<string> relativePaths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var stamps = new Dictionary<string, CodeFileStamp>(relativePaths.Count, StringComparer.OrdinalIgnoreCase);
        if (relativePaths.Count == 0)
        {
            return stamps;
        }

        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rel_path, hash, size, mtime_utc FROM code_files WHERE workspace_id = $workspaceId AND rel_path = $path";
        var workspace = command.CreateParameter();
        workspace.ParameterName = "$workspaceId";
        workspace.Value = workspaceId;
        command.Parameters.Add(workspace);
        var path = command.CreateParameter();
        path.ParameterName = "$path";
        command.Parameters.Add(path);

        foreach (var relativePath in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            path.Value = relativePath;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                stamps[relativePath] = new CodeFileStamp(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Timestamp.Parse(reader.GetString(3)));
            }
        }

        return stamps;
    }

    public async Task<CodeFileIndex?> GetFileAsync(long workspaceId, string relativePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        await using var connection = await connections.OpenAsync(ct);

        var file = await connection.QuerySingleOrDefaultAsync<FileRow>(new CommandDefinition(
            "SELECT id, rel_path, hash, size, mtime_utc, line_count, classification, language, usings FROM code_files " +
            "WHERE workspace_id = @workspaceId AND rel_path = @relativePath",
            new { workspaceId, relativePath },
            cancellationToken: ct));
        if (file is null)
        {
            return null;
        }

        var typeRows = (await connection.QueryAsync<TypeRow>(new CommandDefinition(
            "SELECT id, kind, name, full_name, namespace, base_list, modifiers, attributes, doc_summary, start_line, end_line " +
            "FROM code_types WHERE file_id = @id ORDER BY ordinal",
            new { id = file.Id },
            cancellationToken: ct))).ToList();

        var memberRows = await connection.QueryAsync<MemberRow>(new CommandDefinition(
            "SELECT type_id, kind, name, signature, return_type, modifiers, attributes, doc_summary, start_line, end_line " +
            "FROM code_members WHERE file_id = @id ORDER BY id",
            new { id = file.Id },
            cancellationToken: ct));

        var callRows = await connection.QueryAsync<CallSiteRow>(new CommandDefinition(
            "SELECT caller_type, caller_member, callee_name, receiver, line, receiver_type, receiver_guessed FROM code_call_sites WHERE file_id = @id ORDER BY id",
            new { id = file.Id },
            cancellationToken: ct));

        var indexByTypeId = new Dictionary<long, int>(typeRows.Count);
        for (var i = 0; i < typeRows.Count; i++)
        {
            indexByTypeId[typeRows[i].Id] = i;
        }

        return new CodeFileIndex(
            ToStamp(new StampRow { RelPath = file.RelPath, Hash = file.Hash, Size = file.Size, MtimeUtc = file.MtimeUtc }),
            (int)file.LineCount,
            file.Classification,
            file.Usings.Length == 0 ? [] : file.Usings.Split(UsingSeparator),
            typeRows.Select(ToType).ToList(),
            memberRows.Select(m => ToMember(m, indexByTypeId)).ToList(),
            callRows.Select(ToCallSite).ToList(),
            EnumText.FromDb<CodeLanguage>(file.Language));
    }

    public async Task<CodeIndexStats> GetStatsAsync(long workspaceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleAsync<StatsRow>(new CommandDefinition(
            "SELECT " +
            "(SELECT COUNT(*) FROM code_files WHERE workspace_id = @workspaceId) AS files, " +
            "(SELECT COUNT(*) FROM code_types t JOIN code_files f ON f.id = t.file_id WHERE f.workspace_id = @workspaceId) AS types, " +
            "(SELECT COUNT(*) FROM code_members m JOIN code_files f ON f.id = m.file_id WHERE f.workspace_id = @workspaceId) AS members, " +
            "(SELECT COUNT(*) FROM code_call_sites c JOIN code_files f ON f.id = c.file_id WHERE f.workspace_id = @workspaceId) AS call_sites",
            new { workspaceId },
            cancellationToken: ct));
        var perLanguage = await connection.QueryAsync<LanguageCountRow>(new CommandDefinition(
            "SELECT language, COUNT(*) AS files FROM code_files WHERE workspace_id = @workspaceId GROUP BY language",
            new { workspaceId },
            cancellationToken: ct));
        var byLanguage = perLanguage.ToDictionary(r => EnumText.FromDb<CodeLanguage>(r.Language), r => (int)r.Files);
        return new CodeIndexStats((int)row.Files, (int)row.Types, (int)row.Members, (int)row.CallSites, byLanguage);
    }

    internal static void Validate(CodeFileIndex file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Stamp.RelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Stamp.Hash);
        ArgumentNullException.ThrowIfNull(file.Classification);
        foreach (var member in file.Members)
        {
            if (member.TypeIndex is { } index && (index < 0 || index >= file.Types.Count))
            {
                throw new ArgumentException($"Member '{member.Name}' points at type index {index}, but the file has {file.Types.Count} types.", nameof(file));
            }
        }

        if (file.Usings.Any(u => u.Contains(UsingSeparator, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A using directive cannot contain a line break.", nameof(file));
        }
    }

    private static async Task<bool> DeleteFileAsync(SqliteConnection connection, SqliteTransaction tx, long workspaceId, string relativePath, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM code_files WHERE workspace_id = @workspaceId AND rel_path = @relativePath",
            new { workspaceId, relativePath },
            tx,
            cancellationToken: ct)) > 0;

    private static CodeFileStamp ToStamp(StampRow row) =>
        new(row.RelPath, row.Hash, row.Size, Timestamp.Parse(row.MtimeUtc));

    private static CodeTypeEntry ToType(TypeRow row) =>
        new(
            EnumText.FromDb<CodeTypeKind>(row.Kind),
            row.Name,
            row.FullName,
            row.Namespace,
            row.BaseList,
            row.Modifiers,
            row.Attributes,
            row.DocSummary,
            (int)row.StartLine,
            (int)row.EndLine);

    private static CodeMemberEntry ToMember(MemberRow row, Dictionary<long, int> indexByTypeId) =>
        new(
            row.TypeId is { } typeId ? indexByTypeId[typeId] : null,
            EnumText.FromDb<CodeMemberKind>(row.Kind),
            row.Name,
            row.Signature,
            row.ReturnType,
            row.Modifiers,
            row.Attributes,
            row.DocSummary,
            (int)row.StartLine,
            (int)row.EndLine);

    private static CodeCallSiteEntry ToCallSite(CallSiteRow row) =>
        new(row.CallerType, row.CallerMember, row.CalleeName, row.Receiver, (int)row.Line, row.ReceiverType, row.ReceiverGuessed != 0);

    private sealed class StampRow
    {
        public string RelPath { get; set; } = string.Empty;

        public string Hash { get; set; } = string.Empty;

        public long Size { get; set; }

        public string MtimeUtc { get; set; } = string.Empty;
    }

    private sealed class FileRow
    {
        public long Id { get; set; }

        public string RelPath { get; set; } = string.Empty;

        public string Hash { get; set; } = string.Empty;

        public long Size { get; set; }

        public string MtimeUtc { get; set; } = string.Empty;

        public long LineCount { get; set; }

        public string Classification { get; set; } = string.Empty;

        public string Language { get; set; } = "csharp";

        public string Usings { get; set; } = string.Empty;
    }

    private sealed class TypeRow
    {
        public long Id { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string? Namespace { get; set; }

        public string? BaseList { get; set; }

        public string? Modifiers { get; set; }

        public string? Attributes { get; set; }

        public string? DocSummary { get; set; }

        public long StartLine { get; set; }

        public long EndLine { get; set; }
    }

    private sealed class MemberRow
    {
        public long? TypeId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Signature { get; set; } = string.Empty;

        public string? ReturnType { get; set; }

        public string? Modifiers { get; set; }

        public string? Attributes { get; set; }

        public string? DocSummary { get; set; }

        public long StartLine { get; set; }

        public long EndLine { get; set; }
    }

    private sealed class CallSiteRow
    {
        public string? CallerType { get; set; }

        public string? CallerMember { get; set; }

        public string CalleeName { get; set; } = string.Empty;

        public string? Receiver { get; set; }

        public string? ReceiverType { get; set; }

        public long ReceiverGuessed { get; set; }

        public long Line { get; set; }
    }

    private sealed class StatsRow
    {
        public long Files { get; set; }

        public long Types { get; set; }

        public long Members { get; set; }

        public long CallSites { get; set; }
    }

    private sealed class LanguageCountRow
    {
        public string Language { get; set; } = string.Empty;

        public long Files { get; set; }
    }
}

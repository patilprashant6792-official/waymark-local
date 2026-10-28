using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Code;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class CodeIndexStore(ISqliteConnectionFactory connections, IWriteQueue writes) : ICodeIndexStore
{
    private const char UsingSeparator = '\n';

    public Task ReplaceFileAsync(long workspaceId, CodeFileIndex file, DateTimeOffset indexedUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        Validate(file);
        return writes.RunAsync(async (connection, tx, k) =>
        {
            await DeleteFileAsync(connection, tx, workspaceId, file.Stamp.RelativePath, k);

            var fileId = await connection.QuerySingleAsync<long>(new CommandDefinition(
                "INSERT INTO code_files (workspace_id, rel_path, hash, size, mtime_utc, line_count, classification, language, usings, indexed_utc) " +
                "VALUES (@workspaceId, @path, @hash, @size, @mtime, @lines, @classification, @language, @usings, @indexed) RETURNING id",
                new
                {
                    workspaceId,
                    path = file.Stamp.RelativePath,
                    hash = file.Stamp.Hash,
                    size = file.Stamp.Size,
                    mtime = Timestamp.Format(file.Stamp.ModifiedUtc),
                    lines = file.LineCount,
                    classification = file.Classification,
                    language = EnumText.ToDb(file.Language),
                    usings = string.Join(UsingSeparator, file.Usings),
                    indexed = Timestamp.Format(indexedUtc)
                },
                tx,
                cancellationToken: k));

            var typeIds = new long[file.Types.Count];
            for (var ordinal = 0; ordinal < file.Types.Count; ordinal++)
            {
                typeIds[ordinal] = await InsertTypeAsync(connection, tx, fileId, ordinal, file.Types[ordinal], file.Stamp.RelativePath, k);
            }

            foreach (var member in file.Members)
            {
                var typeIndex = member.TypeIndex;
                await InsertMemberAsync(connection, tx, fileId, typeIndex is null ? null : typeIds[typeIndex.Value], member, file, k);
            }

            foreach (var site in file.CallSites)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO code_call_sites (file_id, caller_type, caller_member, callee_name, receiver, line, receiver_type, receiver_guessed) " +
                    "VALUES (@fileId, @callerType, @callerMember, @calleeName, @receiver, @line, @receiverType, @receiverGuessed)",
                    new
                    {
                        fileId,
                        callerType = site.CallerType,
                        callerMember = site.CallerMember,
                        calleeName = site.CalleeName,
                        receiver = site.Receiver,
                        line = site.Line,
                        receiverType = site.ReceiverType,
                        receiverGuessed = site.ReceiverTypeGuessed ? 1 : 0
                    },
                    tx,
                    cancellationToken: k));
            }
        }, ct);
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

    private static void Validate(CodeFileIndex file)
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

    private static async Task<long> InsertTypeAsync(
        SqliteConnection connection, SqliteTransaction tx, long fileId, int ordinal, CodeTypeEntry type, string path, CancellationToken ct)
    {
        var typeId = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "INSERT INTO code_types (file_id, ordinal, kind, name, full_name, namespace, base_list, modifiers, attributes, doc_summary, start_line, end_line) " +
            "VALUES (@fileId, @ordinal, @kind, @name, @fullName, @namespace, @baseList, @modifiers, @attributes, @doc, @startLine, @endLine) RETURNING id",
            new
            {
                fileId,
                ordinal,
                kind = EnumText.ToDb(type.Kind),
                name = type.Name,
                fullName = type.FullName,
                @namespace = type.Namespace,
                baseList = type.BaseList,
                modifiers = type.Modifiers,
                attributes = type.Attributes,
                doc = type.DocSummary,
                startLine = type.StartLine,
                endLine = type.EndLine
            },
            tx,
            cancellationToken: ct));

        await InsertSearchRowAsync(connection, tx, typeId * 2, type.Name, type.FullName, type.DocSummary, path, fileId, EnumText.ToDb(type.Kind), ct);
        return typeId;
    }

    private static async Task InsertMemberAsync(
        SqliteConnection connection, SqliteTransaction tx, long fileId, long? typeId, CodeMemberEntry member, CodeFileIndex file, CancellationToken ct)
    {
        var memberId = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "INSERT INTO code_members (file_id, type_id, kind, name, signature, return_type, modifiers, attributes, doc_summary, start_line, end_line) " +
            "VALUES (@fileId, @typeId, @kind, @name, @signature, @returnType, @modifiers, @attributes, @doc, @startLine, @endLine) RETURNING id",
            new
            {
                fileId,
                typeId,
                kind = EnumText.ToDb(member.Kind),
                name = member.Name,
                signature = member.Signature,
                returnType = member.ReturnType,
                modifiers = member.Modifiers,
                attributes = member.Attributes,
                doc = member.DocSummary,
                startLine = member.StartLine,
                endLine = member.EndLine
            },
            tx,
            cancellationToken: ct));

        var owner = member.TypeIndex is { } index ? file.Types[index].FullName : null;
        var fullName = owner is null ? member.Name : owner + "." + member.Name;
        await InsertSearchRowAsync(connection, tx, memberId * 2 + 1, member.Name, fullName, member.DocSummary, file.Stamp.RelativePath, fileId, EnumText.ToDb(member.Kind), ct);
    }

    private static Task InsertSearchRowAsync(
        SqliteConnection connection, SqliteTransaction tx, long rowId, string name, string fullName, string? doc, string path, long fileId, string kind, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO code_fts (rowid, name, full_name, doc, path, file_id, kind) VALUES (@rowId, @name, @fullName, @doc, @path, @fileId, @kind)",
            new { rowId, name, fullName, doc = doc ?? string.Empty, path, fileId, kind },
            tx,
            cancellationToken: ct));

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

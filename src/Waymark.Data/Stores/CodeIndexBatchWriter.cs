using System.Globalization;
using Microsoft.Data.Sqlite;
using Waymark.Core.Code;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

/// <summary>
/// Writes code index files inside one open transaction. Every statement is created once and only re-bound per row, so SQLite compiles it once
/// per batch instead of once per row. Each file is written under its own savepoint: a file that fails is rolled back alone and reported,
/// and the rest of the batch still commits. The calls are synchronous on purpose: SQLite does its I/O synchronously anyway, and an async
/// state machine per row only adds cost.
/// </summary>
internal sealed class CodeIndexBatchWriter : IDisposable
{
    private const char UsingSeparator = '\n';

    private readonly SqliteCommand _savepoint;
    private readonly SqliteCommand _release;
    private readonly SqliteCommand _rollbackTo;
    private readonly Statement _deleteFile;
    private readonly Statement _insertFile;
    private readonly Statement _insertType;
    private readonly Statement _insertMember;
    private readonly Statement _insertCallSite;
    private readonly Statement _insertSearch;

    public CodeIndexBatchWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        _savepoint = Command(connection, transaction, "SAVEPOINT code_file");
        _release = Command(connection, transaction, "RELEASE code_file");
        _rollbackTo = Command(connection, transaction, "ROLLBACK TO code_file");

        _deleteFile = new Statement(
            connection,
            transaction,
            "DELETE FROM code_files WHERE workspace_id = $workspaceId AND rel_path = $path",
            "$workspaceId",
            "$path");
        _insertFile = new Statement(
            connection,
            transaction,
            "INSERT INTO code_files (workspace_id, rel_path, hash, size, mtime_utc, line_count, classification, language, usings, indexed_utc) " +
            "VALUES ($workspaceId, $path, $hash, $size, $mtime, $lines, $classification, $language, $usings, $indexed) RETURNING id",
            "$workspaceId",
            "$path",
            "$hash",
            "$size",
            "$mtime",
            "$lines",
            "$classification",
            "$language",
            "$usings",
            "$indexed");
        _insertType = new Statement(
            connection,
            transaction,
            "INSERT INTO code_types (file_id, ordinal, kind, name, full_name, namespace, base_list, modifiers, attributes, doc_summary, start_line, end_line) " +
            "VALUES ($fileId, $ordinal, $kind, $name, $fullName, $namespace, $baseList, $modifiers, $attributes, $doc, $startLine, $endLine) RETURNING id",
            "$fileId",
            "$ordinal",
            "$kind",
            "$name",
            "$fullName",
            "$namespace",
            "$baseList",
            "$modifiers",
            "$attributes",
            "$doc",
            "$startLine",
            "$endLine");
        _insertMember = new Statement(
            connection,
            transaction,
            "INSERT INTO code_members (file_id, type_id, kind, name, signature, return_type, modifiers, attributes, doc_summary, start_line, end_line) " +
            "VALUES ($fileId, $typeId, $kind, $name, $signature, $returnType, $modifiers, $attributes, $doc, $startLine, $endLine) RETURNING id",
            "$fileId",
            "$typeId",
            "$kind",
            "$name",
            "$signature",
            "$returnType",
            "$modifiers",
            "$attributes",
            "$doc",
            "$startLine",
            "$endLine");
        _insertCallSite = new Statement(
            connection,
            transaction,
            "INSERT INTO code_call_sites (file_id, caller_type, caller_member, callee_name, receiver, line, receiver_type, receiver_guessed) " +
            "VALUES ($fileId, $callerType, $callerMember, $calleeName, $receiver, $line, $receiverType, $receiverGuessed)",
            "$fileId",
            "$callerType",
            "$callerMember",
            "$calleeName",
            "$receiver",
            "$line",
            "$receiverType",
            "$receiverGuessed");
        _insertSearch = new Statement(
            connection,
            transaction,
            "INSERT INTO code_fts (rowid, name, full_name, doc, path, file_id, kind) VALUES ($rowId, $name, $fullName, $doc, $path, $fileId, $kind)",
            "$rowId",
            "$name",
            "$fullName",
            "$doc",
            "$path",
            "$fileId",
            "$kind");
    }

    /// <summary>Removes the paths, then stores the files. A file that fails is rolled back on its own and listed in the result.</summary>
    public CodeBatchResult Apply(
        long workspaceId,
        IReadOnlyList<CodeFileIndex> replace,
        IReadOnlyList<string> remove,
        DateTimeOffset indexedUtc,
        CancellationToken ct)
    {
        var indexed = Timestamp.Format(indexedUtc);
        var failed = new List<CodeBatchFailure>();
        var removed = 0;
        var replaced = 0;

        foreach (var path in remove)
        {
            ct.ThrowIfCancellationRequested();
            if (DeleteFile(workspaceId, path))
            {
                removed++;
            }
        }

        foreach (var file in replace)
        {
            ct.ThrowIfCancellationRequested();
            var path = file.Stamp.RelativePath;
            try
            {
                CodeIndexStore.Validate(file);
            }
            catch (ArgumentException ex)
            {
                failed.Add(new CodeBatchFailure(path ?? string.Empty, ex.Message));
                continue;
            }

            _savepoint.ExecuteNonQuery();
            try
            {
                WriteFile(workspaceId, file, indexed);
                _release.ExecuteNonQuery();
                replaced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _rollbackTo.ExecuteNonQuery();
                _release.ExecuteNonQuery();
                failed.Add(new CodeBatchFailure(path, ex.Message));
            }
        }

        return new CodeBatchResult(replaced, removed, failed);
    }

    public void Dispose()
    {
        _savepoint.Dispose();
        _release.Dispose();
        _rollbackTo.Dispose();
        _deleteFile.Dispose();
        _insertFile.Dispose();
        _insertType.Dispose();
        _insertMember.Dispose();
        _insertCallSite.Dispose();
        _insertSearch.Dispose();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private bool DeleteFile(long workspaceId, string relativePath)
    {
        _deleteFile.Bind(0, workspaceId);
        _deleteFile.Bind(1, relativePath);
        return _deleteFile.Run() > 0;
    }

    private void WriteFile(long workspaceId, CodeFileIndex file, string indexed)
    {
        var path = file.Stamp.RelativePath;
        DeleteFile(workspaceId, path);

        _insertFile.Bind(0, workspaceId);
        _insertFile.Bind(1, path);
        _insertFile.Bind(2, file.Stamp.Hash);
        _insertFile.Bind(3, file.Stamp.Size);
        _insertFile.Bind(4, Timestamp.Format(file.Stamp.ModifiedUtc));
        _insertFile.Bind(5, file.LineCount);
        _insertFile.Bind(6, file.Classification);
        _insertFile.Bind(7, EnumText.ToDb(file.Language));
        _insertFile.Bind(8, string.Join(UsingSeparator, file.Usings));
        _insertFile.Bind(9, indexed);
        var fileId = _insertFile.Insert();

        var typeIds = new long[file.Types.Count];
        for (var ordinal = 0; ordinal < file.Types.Count; ordinal++)
        {
            typeIds[ordinal] = InsertType(fileId, ordinal, file.Types[ordinal], path);
        }

        foreach (var member in file.Members)
        {
            InsertMember(fileId, member.TypeIndex is null ? null : typeIds[member.TypeIndex.Value], member, file, path);
        }

        foreach (var site in file.CallSites)
        {
            _insertCallSite.Bind(0, fileId);
            _insertCallSite.Bind(1, site.CallerType);
            _insertCallSite.Bind(2, site.CallerMember);
            _insertCallSite.Bind(3, site.CalleeName);
            _insertCallSite.Bind(4, site.Receiver);
            _insertCallSite.Bind(5, site.Line);
            _insertCallSite.Bind(6, site.ReceiverType);
            _insertCallSite.Bind(7, site.ReceiverTypeGuessed ? 1 : 0);
            _insertCallSite.Run();
        }
    }

    private long InsertType(long fileId, int ordinal, CodeTypeEntry type, string path)
    {
        var kind = EnumText.ToDb(type.Kind);
        _insertType.Bind(0, fileId);
        _insertType.Bind(1, ordinal);
        _insertType.Bind(2, kind);
        _insertType.Bind(3, type.Name);
        _insertType.Bind(4, type.FullName);
        _insertType.Bind(5, type.Namespace);
        _insertType.Bind(6, type.BaseList);
        _insertType.Bind(7, type.Modifiers);
        _insertType.Bind(8, type.Attributes);
        _insertType.Bind(9, type.DocSummary);
        _insertType.Bind(10, type.StartLine);
        _insertType.Bind(11, type.EndLine);
        var typeId = _insertType.Insert();

        InsertSearchRow(typeId * 2, type.Name, type.FullName, type.DocSummary, path, fileId, kind);
        return typeId;
    }

    private void InsertMember(long fileId, long? typeId, CodeMemberEntry member, CodeFileIndex file, string path)
    {
        var kind = EnumText.ToDb(member.Kind);
        _insertMember.Bind(0, fileId);
        _insertMember.Bind(1, typeId);
        _insertMember.Bind(2, kind);
        _insertMember.Bind(3, member.Name);
        _insertMember.Bind(4, member.Signature);
        _insertMember.Bind(5, member.ReturnType);
        _insertMember.Bind(6, member.Modifiers);
        _insertMember.Bind(7, member.Attributes);
        _insertMember.Bind(8, member.DocSummary);
        _insertMember.Bind(9, member.StartLine);
        _insertMember.Bind(10, member.EndLine);
        var memberId = _insertMember.Insert();

        var owner = member.TypeIndex is { } index ? file.Types[index].FullName : null;
        var fullName = owner is null ? member.Name : owner + "." + member.Name;
        InsertSearchRow(memberId * 2 + 1, member.Name, fullName, member.DocSummary, path, fileId, kind);
    }

    private void InsertSearchRow(long rowId, string name, string fullName, string? doc, string path, long fileId, string kind)
    {
        _insertSearch.Bind(0, rowId);
        _insertSearch.Bind(1, name);
        _insertSearch.Bind(2, fullName);
        _insertSearch.Bind(3, doc ?? string.Empty);
        _insertSearch.Bind(4, path);
        _insertSearch.Bind(5, fileId);
        _insertSearch.Bind(6, kind);
        _insertSearch.Run();
    }

    /// <summary>One SQL statement with its parameters created once; the caller binds values by position and runs it again.</summary>
    private sealed class Statement : IDisposable
    {
        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _parameters;

        public Statement(SqliteConnection connection, SqliteTransaction transaction, string sql, params string[] names)
        {
            _command = Command(connection, transaction, sql);
            _parameters = new SqliteParameter[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var parameter = _command.CreateParameter();
                parameter.ParameterName = names[i];
                _command.Parameters.Add(parameter);
                _parameters[i] = parameter;
            }
        }

        public void Bind(int index, object? value) => _parameters[index].Value = value ?? DBNull.Value;

        public int Run() => _command.ExecuteNonQuery();

        /// <summary>Runs an INSERT ... RETURNING id and gives the id.</summary>
        public long Insert() => Convert.ToInt64(_command.ExecuteScalar(), CultureInfo.InvariantCulture);

        public void Dispose() => _command.Dispose();
    }
}
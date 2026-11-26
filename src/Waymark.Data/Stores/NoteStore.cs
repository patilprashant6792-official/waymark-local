using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Memory;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

/// <summary>
/// The notes table and its full-text index. Every write runs on the write queue in one transaction that also keeps note_fts in step
/// (only active notes have a row). The writer rules are checked on the row read in that transaction and again in the UPDATE's WHERE clause.
/// </summary>
public sealed class NoteStore(ISqliteConnectionFactory connections, IWriteQueue writes) : INoteStore
{
    private const int MaxPageSize = 200;
    private const int MaxSearchHits = 200;
    private const int MaxIdsPerQuery = 500;

    private const string Columns =
        "n.id, n.workspace_id, n.map_id, m.name AS flow_name, n.step_spec, n.about, n.detached_from, n.kind, n.title, n.body, n.content_hash, n.author_kind, n.author_id, " +
        "n.code_hash, n.flow_version, n.confirmed_utc, n.replaces_id, n.superseded_utc, n.retired_utc, n.retired_by, n.pinned, n.created_utc, n.updated_utc";

    private const string From = "FROM notes n LEFT JOIN feature_maps m ON m.id = n.map_id";
    private const string Active = "n.retired_utc IS NULL AND n.superseded_utc IS NULL";

    // Notes anchored in one file: a workspace note about it (or a member of it), or a step note whose step is in it (a member or a range of lines).
    private const string InPath =
        "(n.about = @path OR n.about LIKE @member ESCAPE '!' OR n.step_spec = @path OR n.step_spec LIKE @member ESCAPE '!' OR n.step_spec LIKE @range ESCAPE '!')";

    // Notes anchored on exactly one member ('path:Type.Member'): about it, or a step note on it.
    private const string OnMember = "(n.about = @path OR n.step_spec = @path)";

    private static string OrderBy(MemoryOrder order) =>
        order == MemoryOrder.Used ? "n.last_used_utc DESC NULLS LAST, n.id DESC" : "n.updated_utc DESC, n.id DESC";

    public Task<Note> AddAsync(NewNote note, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(note.Body);
        var title = note.Title ?? NoteRules.DeriveTitle(note.Body);
        ArgumentException.ThrowIfNullOrWhiteSpace(title, nameof(note));
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync(async (connection, tx, k) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO notes (workspace_id, map_id, step_spec, about, kind, title, body, content_hash, author_kind, author_id, code_hash, flow_version, " +
                "confirmed_utc, pinned, created_utc, updated_utc) " +
                "VALUES (@WorkspaceId, @MapId, @StepSpec, @About, @Kind, @Title, @Body, @Hash, @AuthorKind, @AuthorId, @CodeHash, @FlowVersion, @Stamp, 0, @Stamp, @Stamp)",
                new
                {
                    note.WorkspaceId,
                    note.MapId,
                    note.StepSpec,
                    note.About,
                    Kind = NoteKinds.ToText(note.Kind),
                    Title = title,
                    note.Body,
                    Hash = NoteRules.Hash(note.Body),
                    AuthorKind = NoteAuthorKinds.ToText(note.Author.Kind),
                    AuthorId = note.Author.Id,
                    note.CodeHash,
                    note.FlowVersion,
                    Stamp = stamp,
                },
                tx,
                cancellationToken: k));
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: k));
            var saved = await ReadAsync(connection, tx, note.WorkspaceId, id, k) ?? throw new InvalidOperationException("The note just stored could not be read back.");
            await WriteFtsAsync(connection, tx, saved, k);
            return saved;
        }, ct);
    }

    public Task<NoteWriteResult> ReviseAsync(
        long workspaceId,
        long id,
        NoteAuthor author,
        string title,
        string body,
        NoteKind kind,
        string? codeHash,
        int? flowVersion,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync(async (connection, tx, k) =>
        {
            var current = await ReadAsync(connection, tx, workspaceId, id, k);
            if (Refusal(current, current is not null && NoteRules.CanRevise(author, current)) is { } refused)
            {
                return refused;
            }

            // An agent never changes the owner's note: the rule is in the WHERE clause too, so a mistake above cannot let it through.
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE notes SET superseded_utc = @stamp, updated_utc = @stamp, pinned = 0 " +
                "WHERE id = @id AND workspace_id = @workspaceId AND retired_utc IS NULL AND superseded_utc IS NULL AND (@human = 1 OR author_kind <> 'human')",
                new { id, workspaceId, stamp, human = author.IsHuman ? 1 : 0 },
                tx,
                cancellationToken: k));
            if (changed == 0)
            {
                return new NoteWriteResult(NoteWrite.NotActive, current);
            }

            await DeleteFtsAsync(connection, tx, id, k);
            // The revision is the editor's (an owner's edit of an agent's note makes it the owner's); it keeps when the note was last used.
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO notes (workspace_id, map_id, step_spec, about, detached_from, kind, title, body, content_hash, author_kind, author_id, code_hash, flow_version, " +
                "confirmed_utc, replaces_id, pinned, created_utc, updated_utc, last_used_utc) " +
                "SELECT workspace_id, map_id, step_spec, about, detached_from, @kind, @title, @body, @hash, @authorKind, @authorId, @codeHash, @flowVersion, " +
                "@stamp, id, @pinned, @stamp, @stamp, last_used_utc FROM notes WHERE id = @id",
                new
                {
                    id,
                    kind = NoteKinds.ToText(kind),
                    title,
                    body,
                    hash = NoteRules.Hash(body),
                    authorKind = NoteAuthorKinds.ToText(author.Kind),
                    authorId = author.Id,
                    codeHash,
                    flowVersion,
                    stamp,
                    pinned = current!.Pinned ? 1 : 0,
                },
                tx,
                cancellationToken: k));
            var newId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: k));
            var saved = await ReadAsync(connection, tx, workspaceId, newId, k) ?? throw new InvalidOperationException("The revised note could not be read back.");
            await WriteFtsAsync(connection, tx, saved, k);
            return new NoteWriteResult(NoteWrite.Saved, saved);
        }, ct);
    }

    public Task<NoteWriteResult> RetireAsync(long workspaceId, long id, NoteAuthor author, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(author);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync(async (connection, tx, k) =>
        {
            var current = await ReadAsync(connection, tx, workspaceId, id, k);
            if (Refusal(current, current is not null && NoteRules.CanRetire(author, current)) is { } refused)
            {
                return refused;
            }

            var changed = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE notes SET retired_utc = @stamp, retired_by = @by, updated_utc = @stamp, pinned = 0 " +
                "WHERE id = @id AND workspace_id = @workspaceId AND retired_utc IS NULL AND superseded_utc IS NULL " +
                "AND (@human = 1 OR author_kind <> 'human')",
                new { id, workspaceId, stamp, by = RetiredBy(author), human = author.IsHuman ? 1 : 0 },
                tx,
                cancellationToken: k));
            if (changed == 0)
            {
                return new NoteWriteResult(NoteWrite.NotActive, current);
            }

            await DeleteFtsAsync(connection, tx, id, k);
            return new NoteWriteResult(NoteWrite.Saved, await ReadAsync(connection, tx, workspaceId, id, k));
        }, ct);
    }

    public Task<NoteWriteResult> ConfirmAsync(long workspaceId, long id, NoteAuthor author, string? codeHash, int? flowVersion, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(author);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync(async (connection, tx, k) =>
        {
            var current = await ReadAsync(connection, tx, workspaceId, id, k);
            if (Refusal(current, current is not null && NoteRules.CanConfirm(author, current)) is { } refused)
            {
                return refused;
            }

            var changed = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE notes SET code_hash = @codeHash, flow_version = @flowVersion, confirmed_utc = @stamp, updated_utc = @stamp " +
                "WHERE id = @id AND workspace_id = @workspaceId AND retired_utc IS NULL AND superseded_utc IS NULL " +
                "AND (@human = 1 OR author_kind <> 'human')",
                new { id, workspaceId, stamp, codeHash, flowVersion, human = author.IsHuman ? 1 : 0 },
                tx,
                cancellationToken: k));
            return changed == 0
                ? new NoteWriteResult(NoteWrite.NotActive, current)
                : new NoteWriteResult(NoteWrite.Saved, await ReadAsync(connection, tx, workspaceId, id, k));
        }, ct);
    }

    public Task<NoteWriteResult> SetPinnedAsync(long workspaceId, long id, bool pinned, DateTimeOffset now, CancellationToken ct)
    {
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync(async (connection, tx, k) =>
        {
            var current = await ReadAsync(connection, tx, workspaceId, id, k);
            if (Refusal(current, true) is { } refused)
            {
                return refused;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE notes SET pinned = @pinned, updated_utc = @stamp WHERE id = @id AND workspace_id = @workspaceId AND retired_utc IS NULL AND superseded_utc IS NULL",
                new { id, workspaceId, stamp, pinned = pinned ? 1 : 0 },
                tx,
                cancellationToken: k));
            return new NoteWriteResult(NoteWrite.Saved, await ReadAsync(connection, tx, workspaceId, id, k));
        }, ct);
    }

    public async Task<IReadOnlyList<Note>> GetAsync(long workspaceId, IReadOnlyList<long> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct().Take(MaxIdsPerQuery).ToArray();
        if (wanted.Length == 0)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(ct);
        var rows = (await connection.QueryAsync<NoteRow>(new CommandDefinition(
            $"SELECT {Columns} {From} WHERE n.workspace_id = @workspaceId AND n.id IN @ids",
            new { workspaceId, ids = wanted },
            cancellationToken: ct))).ToDictionary(row => row.Id);
        return [.. wanted.Where(rows.ContainsKey).Select(id => rows[id].ToNote())];
    }

    public async Task<IReadOnlyList<Note>> ListForFlowsAsync(long workspaceId, IReadOnlyList<long> mapIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mapIds);
        if (mapIds.Count == 0)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            $"SELECT {Columns} {From} WHERE n.workspace_id = @workspaceId AND n.map_id IN @mapIds AND {Active} ORDER BY n.created_utc DESC, n.id DESC",
            new { workspaceId, mapIds = mapIds.Distinct().Take(MaxIdsPerQuery).ToArray() },
            cancellationToken: ct));
        return [.. rows.Select(row => row.ToNote())];
    }

    public async Task<NotePage> ListWorkspaceAsync(long workspaceId, NoteFilter filter, int skip, int take, CancellationToken ct, MemoryOrder order = MemoryOrder.Changed)
    {
        var where = filter switch
        {
            NoteFilter.Pinned => $"{Active} AND n.pinned = 1",
            NoteFilter.FromModels => $"{Active} AND n.author_kind <> 'human'",
            NoteFilter.Detached => $"{Active} AND n.map_id IS NULL AND n.detached_from IS NOT NULL",
            NoteFilter.Retired => "n.retired_utc IS NOT NULL",
            _ => Active,
        };
        var parameters = new { workspaceId, skip = Math.Max(0, skip), take = Math.Clamp(take, 1, MaxPageSize) };
        await using var connection = await connections.OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM notes n WHERE n.workspace_id = @workspaceId AND {where}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            $"SELECT {Columns} {From} WHERE n.workspace_id = @workspaceId AND {where} ORDER BY {OrderBy(order)} LIMIT @take OFFSET @skip",
            parameters,
            cancellationToken: ct));
        return new NotePage([.. rows.Select(row => row.ToNote())], (int)total);
    }

    public async Task<IReadOnlyList<Note>> HistoryAsync(long workspaceId, long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            "WITH RECURSIVE " +
            "older(id, replaces_id) AS (SELECT id, replaces_id FROM notes WHERE id = @id AND workspace_id = @workspaceId " +
            "  UNION SELECT n.id, n.replaces_id FROM notes n JOIN older o ON n.id = o.replaces_id), " +
            "newer(id) AS (SELECT id FROM notes WHERE id = @id AND workspace_id = @workspaceId " +
            "  UNION SELECT n.id FROM notes n JOIN newer w ON n.replaces_id = w.id) " +
            $"SELECT {Columns} {From} WHERE n.workspace_id = @workspaceId AND n.id IN (SELECT id FROM older UNION SELECT id FROM newer) " +
            "ORDER BY n.created_utc, n.id",
            new { workspaceId, id },
            cancellationToken: ct));
        return [.. rows.Select(row => row.ToNote())];
    }

    public async Task<IReadOnlyList<NoteWordHit>> SearchWordsAsync(long workspaceId, string ftsQuery, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ftsQuery))
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<HitRow>(new CommandDefinition(
            "SELECT note_fts.rowid AS note_id, -bm25(note_fts, 4.0, 1.0) AS score " +
            "FROM note_fts JOIN notes n ON n.id = note_fts.rowid " +
            $"WHERE note_fts MATCH @ftsQuery AND n.workspace_id = @workspaceId AND {Active} " +
            "ORDER BY bm25(note_fts, 4.0, 1.0) LIMIT @take",
            new { workspaceId, ftsQuery, take = Math.Clamp(take, 1, MaxSearchHits) },
            cancellationToken: ct));
        return [.. rows.Select(row => new NoteWordHit(row.NoteId, row.Score))];
    }

    public async Task<IReadOnlyList<Note>> ListPinnedAsync(long workspaceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            $"SELECT {Columns} {From} WHERE n.workspace_id = @workspaceId AND n.pinned = 1 AND {Active} ORDER BY n.created_utc, n.id",
            new { workspaceId },
            cancellationToken: ct));
        return [.. rows.Select(row => row.ToNote())];
    }

    public async Task<IReadOnlyList<long>> ListPinnedWorkspaceIdsAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<long>(new CommandDefinition(
            $"SELECT DISTINCT n.workspace_id FROM notes n WHERE n.pinned = 1 AND {Active}",
            cancellationToken: ct));
        return [.. rows];
    }
    public async Task<IReadOnlyList<Note>> ListActiveAsync(long workspaceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            $"SELECT {Columns} {From} WHERE n.workspace_id = @workspaceId AND {Active} ORDER BY n.id",
            new { workspaceId },
            cancellationToken: ct));
        return [.. rows.Select(row => row.ToNote())];
    }

    public async Task<int> CountActiveAsync(long workspaceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM notes n WHERE n.workspace_id = @workspaceId AND {Active}",
            new { workspaceId },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> FindActiveByPathAsync(long workspaceId, string path, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var escaped = EscapeLike(path);
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<long>(new CommandDefinition(
            $"SELECT n.id FROM notes n WHERE n.workspace_id = @workspaceId AND {Active} AND {InPath} " +
            "ORDER BY n.id DESC LIMIT @limit",
            new { workspaceId, path, member = escaped + ":%", range = escaped + "#%", limit = Math.Clamp(limit, 1, MaxPageSize) },
            cancellationToken: ct));
        return [.. rows];
    }

    public async Task<NotePage> PageForFlowsAsync(long workspaceId, IReadOnlyList<long> mapIds, int skip, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mapIds);
        if (mapIds.Count == 0)
        {
            return new NotePage([], 0);
        }

        var parameters = new { workspaceId, mapIds = mapIds.Distinct().Take(MaxIdsPerQuery).ToArray(), skip = Math.Max(0, skip), take = Math.Clamp(take, 1, MaxPageSize) };
        await using var connection = await connections.OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM notes n WHERE n.workspace_id = @workspaceId AND n.map_id IN @mapIds AND {Active}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            $"SELECT {Columns} {From} WHERE n.workspace_id = @workspaceId AND n.map_id IN @mapIds AND {Active} ORDER BY {OrderBy(MemoryOrder.Changed)} LIMIT @take OFFSET @skip",
            parameters,
            cancellationToken: ct));
        return new NotePage([.. rows.Select(row => row.ToNote())], (int)total);
    }

    public async Task<NotePage> PageByPathAsync(long workspaceId, string path, int skip, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var escaped = EscapeLike(path);
        var where = path.Contains(':', StringComparison.Ordinal) ? OnMember : InPath;
        var parameters = new { workspaceId, path, member = escaped + ":%", range = escaped + "#%", skip = Math.Max(0, skip), take = Math.Clamp(take, 1, MaxPageSize) };
        await using var connection = await connections.OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM notes n WHERE n.workspace_id = @workspaceId AND {Active} AND {where}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            $"SELECT {Columns} {From} WHERE n.workspace_id = @workspaceId AND {Active} AND {where} ORDER BY {OrderBy(MemoryOrder.Changed)} LIMIT @take OFFSET @skip",
            parameters,
            cancellationToken: ct));
        return new NotePage([.. rows.Select(row => row.ToNote())], (int)total);
    }

    public Task<int> RebaseStepAsync(long workspaceId, long mapId, string oldSpec, string newSpec, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSpec);
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
        {
            var ids = (await connection.QueryAsync<long>(new CommandDefinition(
                $"SELECT n.id FROM notes n WHERE n.workspace_id = @workspaceId AND n.map_id = @mapId AND n.step_spec = @oldSpec AND {Active}",
                new { workspaceId, mapId, oldSpec },
                tx,
                cancellationToken: k))).ToList();
            if (ids.Count == 0)
            {
                return 0;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE notes SET step_spec = @newSpec WHERE workspace_id = @workspaceId AND id IN @ids",
                new { workspaceId, ids, newSpec },
                tx,
                cancellationToken: k));
            foreach (var id in ids)
            {
                if (await ReadAsync(connection, tx, workspaceId, id, k) is { } moved)
                {
                    await WriteFtsAsync(connection, tx, moved, k);
                }
            }

            return ids.Count;
        }, ct);
    }

    public Task<int> RebaseFlowVersionAsync(long workspaceId, long mapId, int fromVersion, int toVersion, CancellationToken ct) =>
        writes.ExecuteAsync<int>((connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE notes SET flow_version = @toVersion WHERE workspace_id = @workspaceId AND map_id = @mapId AND step_spec IS NULL AND flow_version = @fromVersion " +
            $"AND id IN (SELECT n.id FROM notes n WHERE n.workspace_id = @workspaceId AND n.map_id = @mapId AND {Active})",
            new { workspaceId, mapId, fromVersion, toVersion },
            tx,
            cancellationToken: k)), ct);
    // Who retired a note, as the row keeps it: the owner, or an agent with the client it came through.
    private static string RetiredBy(NoteAuthor author) => author.IsHuman ? "owner" : $"agent ({author.Id})";

    private static NoteWriteResult? Refusal(Note? current, bool allowed) =>
        current is null ? new NoteWriteResult(NoteWrite.NotFound, null)
        : !current.IsActive ? new NoteWriteResult(NoteWrite.NotActive, current)
        : !allowed ? new NoteWriteResult(NoteWrite.NotYours, current)
        : null;

    private static async Task<Note?> ReadAsync(SqliteConnection connection, SqliteTransaction tx, long workspaceId, long id, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<NoteRow>(new CommandDefinition(
            $"SELECT {Columns} {From} WHERE n.id = @id AND n.workspace_id = @workspaceId",
            new { id, workspaceId },
            tx,
            cancellationToken: ct));
        return row?.ToNote();
    }

    // The title is indexed with the body (weight 4), so a word of either finds the note.
    private static async Task WriteFtsAsync(SqliteConnection connection, SqliteTransaction tx, Note note, CancellationToken ct)
    {
        await DeleteFtsAsync(connection, tx, note.Id, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO note_fts (rowid, body, anchor) VALUES (@id, @body, @anchor)",
            new { id = note.Id, body = (note.Title + " " + note.Body).TrimStart(), anchor = AnchorText(note) },
            tx,
            cancellationToken: ct));
    }

    private static Task DeleteFtsAsync(SqliteConnection connection, SqliteTransaction tx, long id, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("DELETE FROM note_fts WHERE rowid = @id", new { id }, tx, cancellationToken: ct));

    /// <summary>The words that place a note: its flow's name with dashes as spaces, its step as Type.Member, what it is about, and its kind.</summary>
    internal static string AnchorText(Note note) => AnchorText(note.FlowName ?? note.DetachedFrom, note.StepSpec, note.About, note.Kind);

    internal static string AnchorText(string? flow, string? stepSpec, string? about, NoteKind kind)
    {
        var text = new StringBuilder();
        if (flow is not null)
        {
            text.Append(flow.Replace('-', ' ')).Append(' ');
        }

        if (stepSpec is not null)
        {
            text.Append(NoteEmbedText.Label(stepSpec).Replace('.', ' ')).Append(' ');
        }

        if (about is not null)
        {
            text.Append(about.Replace('/', ' ').Replace(':', ' ').Replace('.', ' ')).Append(' ');
        }

        return text.Append(NoteKinds.ToText(kind)).ToString();
    }

    private static string EscapeLike(string text) =>
        text.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal).Replace("_", "!_", StringComparison.Ordinal);

    private sealed class NoteRow
    {
        public long Id { get; set; }

        public long WorkspaceId { get; set; }

        public long? MapId { get; set; }

        public string? FlowName { get; set; }

        public string? StepSpec { get; set; }

        public string? About { get; set; }

        public string? DetachedFrom { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public string AuthorKind { get; set; } = string.Empty;

        public string AuthorId { get; set; } = string.Empty;

        public string? CodeHash { get; set; }

        public long? FlowVersion { get; set; }

        public string? ConfirmedUtc { get; set; }

        public long? ReplacesId { get; set; }

        public string? SupersededUtc { get; set; }

        public string? RetiredUtc { get; set; }

        public string? RetiredBy { get; set; }

        public long Pinned { get; set; }

        public string CreatedUtc { get; set; } = string.Empty;

        public string UpdatedUtc { get; set; } = string.Empty;

        public Note ToNote() => new(
            Id,
            WorkspaceId,
            MapId,
            MapId is null ? null : FlowName,
            StepSpec,
            About,
            DetachedFrom,
            NoteKinds.FromStored(Kind),
            Title,
            Body,
            ContentHash,
            new NoteAuthor(NoteAuthorKinds.FromStored(AuthorKind), AuthorId),
            CodeHash,
            FlowVersion is null ? null : (int)FlowVersion.Value,
            Timestamp.ParseNullable(ConfirmedUtc),
            ReplacesId,
            Timestamp.ParseNullable(SupersededUtc),
            Timestamp.ParseNullable(RetiredUtc),
            RetiredBy,
            Pinned != 0,
            Timestamp.Parse(CreatedUtc),
            Timestamp.Parse(UpdatedUtc));
    }

    private sealed class HitRow
    {
        public long NoteId { get; set; }

        public double Score { get; set; }
    }
}

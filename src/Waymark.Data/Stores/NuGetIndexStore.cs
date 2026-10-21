using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.NuGetIndex;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class NuGetIndexStore(ISqliteConnectionFactory connections, IWriteQueue writes) : INuGetIndexStore
{
    private const int MaxErrorChars = 2000;
    private const int MaxSidsPerCall = 100;

    // bm25 column weights, in the column order of nuget_fts: name, full_name, namespace, summary, tokens.
    private const string Bm25 = "bm25(nuget_fts, 10.0, 6.0, 2.0, 1.0, 3.0)";

    private const string PackageColumns =
        "id, package_id, version, tfm, state, schema_version, symbol_count, type_count, started_utc, indexed_utc, error";

    private static readonly (string Section, string Column, string Alias)[] SectionColumns =
    [
        (NuGetSections.Sig, "d_sig", "Sig"),
        (NuGetSections.Methods, "d_methods", "Methods"),
        (NuGetSections.Props, "d_props", "Props"),
        (NuGetSections.Data, "d_data", "Data"),
        (NuGetSections.Doc, "d_doc", "Doc"),
        (NuGetSections.Rel, "related", "Rel")
    ];

    public Task<long?> TryBeginBuildAsync(NuGetPackageKey key, int schemaVersion, DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct)
    {
        ValidateKey(key);
        return writes.ExecuteAsync<long?>(async (connection, tx, k) =>
        {
            var existing = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
                "SELECT id, state, started_utc FROM nuget_packages WHERE package_id = @PackageId AND version = @Version AND tfm = @Tfm",
                KeyParameters(key),
                tx,
                cancellationToken: k));

            if (existing is { State: "building" } && Timestamp.Parse(existing.StartedUtc) > now - staleAfter)
            {
                return null;
            }

            if (existing is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM nuget_packages WHERE id = @id",
                    new { id = existing.Id },
                    tx,
                    cancellationToken: k));
            }

            return await connection.QuerySingleAsync<long>(new CommandDefinition(
                "INSERT INTO nuget_packages (package_id, version, tfm, state, schema_version, started_utc) " +
                "VALUES (@PackageId, @Version, @Tfm, 'building', @schemaVersion, @started) RETURNING id",
                new { key.PackageId, key.Version, Tfm = key.TargetFramework, schemaVersion, started = Timestamp.Format(now) },
                tx,
                cancellationToken: k));
        }, ct);
    }

    public Task AddSymbolsAsync(long packageRowId, IReadOnlyList<NuGetSymbolEntry> symbols, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        foreach (var symbol in symbols)
        {
            ValidateSymbol(symbol);
        }

        return writes.RunAsync(async (connection, tx, k) =>
        {
            await RequireBuildingAsync(connection, tx, packageRowId, k);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO nuget_symbols (package_row_id, sid, kind, name, full_name, namespace, declaring_type, declaration, card, prominence, tokens, summary, related, " +
                "d_sig, d_methods, d_props, d_data, d_doc) VALUES (@packageRowId, @Sid, @kind, @Name, @FullName, @Namespace, @DeclaringType, @Declaration, @Card, @Prominence, " +
                "@Tokens, @Summary, @Related, @SignatureSection, @MethodsSection, @PropertiesSection, @DataSection, @DocSection)",
                symbols.Select(s => new
                {
                    packageRowId,
                    s.Sid,
                    kind = EnumText.ToDb(s.Kind),
                    s.Name,
                    s.FullName,
                    s.Namespace,
                    s.DeclaringType,
                    s.Declaration,
                    s.Card,
                    s.Prominence,
                    s.Tokens,
                    s.Summary,
                    s.Related,
                    s.SignatureSection,
                    s.MethodsSection,
                    s.PropertiesSection,
                    s.DataSection,
                    s.DocSection
                }).ToList(),
                tx,
                cancellationToken: k));
        }, ct);
    }

    public Task CompleteBuildAsync(long packageRowId, IReadOnlyDictionary<string, int> typesPerNamespace, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typesPerNamespace);
        foreach (var (name, count) in typesPerNamespace)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
        }

        return writes.RunAsync(async (connection, tx, k) =>
        {
            await RequireBuildingAsync(connection, tx, packageRowId, k);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO nuget_namespaces (package_row_id, namespace, type_count) VALUES (@packageRowId, @name, @count)",
                typesPerNamespace.Select(p => new { packageRowId, name = p.Key, count = p.Value }).ToList(),
                tx,
                cancellationToken: k));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE nuget_packages SET state = 'ready', error = NULL, indexed_utc = @indexed, " +
                "symbol_count = (SELECT COUNT(*) FROM nuget_symbols WHERE package_row_id = @packageRowId), " +
                "type_count = (SELECT COUNT(*) FROM nuget_symbols WHERE package_row_id = @packageRowId AND kind <> 'methodgroup') " +
                "WHERE id = @packageRowId",
                new { packageRowId, indexed = Timestamp.Format(now) },
                tx,
                cancellationToken: k));
        }, ct);
    }

    public Task<bool> FailBuildAsync(long packageRowId, string error, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var text = error.Length > MaxErrorChars ? error[..MaxErrorChars] : error;
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
        {
            var state = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT state FROM nuget_packages WHERE id = @packageRowId",
                new { packageRowId },
                tx,
                cancellationToken: k));
            if (state != "building")
            {
                return false;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM nuget_symbols WHERE package_row_id = @packageRowId",
                new { packageRowId },
                tx,
                cancellationToken: k));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE nuget_packages SET state = 'failed', error = @text, symbol_count = 0, type_count = 0 WHERE id = @packageRowId",
                new { packageRowId, text },
                tx,
                cancellationToken: k));
            return true;
        }, ct);
    }

    public async Task<NuGetPackageIndex?> GetPackageAsync(NuGetPackageKey key, CancellationToken ct)
    {
        ValidateKey(key);
        await using var connection = await connections.OpenAsync(ct);
        return await ReadPackageAsync(connection, key, ct);
    }

    public async Task<NuGetPackageOutline?> GetOutlineAsync(NuGetPackageKey key, CancellationToken ct)
    {
        ValidateKey(key);
        await using var connection = await connections.OpenAsync(ct);
        var package = await ReadPackageAsync(connection, key, ct);
        if (package is null)
        {
            return null;
        }

        var rows = await connection.QueryAsync<NamespaceRow>(new CommandDefinition(
            "SELECT namespace, type_count FROM nuget_namespaces WHERE package_row_id = @id ORDER BY type_count DESC, namespace",
            new { id = package.Id },
            cancellationToken: ct));
        return new NuGetPackageOutline(package, rows.Select(r => new NuGetNamespaceCount(r.Namespace, (int)r.TypeCount)).ToList());
    }

    public async Task<NuGetSearchResult> SearchAsync(NuGetSymbolQuery query, IReadOnlyCollection<string> sections, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        var wanted = ResolveSections(sections);
        var words = query.Tokens.Select(Sanitize).Where(w => w.Length > 0).ToList();
        if (query.Tokens.Count > 0 && words.Count == 0)
        {
            throw new ArgumentException("A search word needs at least one letter or digit.", nameof(query));
        }

        var parameters = new DynamicParameters();
        parameters.Add("packageRowId", query.PackageRowId);
        parameters.Add("limit", query.Limit);
        var filters = new StringBuilder("s.package_row_id = @packageRowId");
        if (query.Kind is { } kind)
        {
            filters.Append(" AND s.kind = @kind");
            parameters.Add("kind", EnumText.ToDb(kind));
        }

        if (!string.IsNullOrWhiteSpace(query.Namespace))
        {
            filters.Append(" AND s.namespace = @namespace");
            parameters.Add("namespace", query.Namespace.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.DeclaringType))
        {
            filters.Append(" AND s.declaring_type = @declaringType");
            parameters.Add("declaringType", query.DeclaringType.Trim());
        }

        string select;
        string count;
        if (words.Count == 0)
        {
            select = $"SELECT {Projection(wanted)}, 0.0 AS Relevance FROM nuget_symbols s WHERE {filters} ORDER BY s.prominence DESC, s.full_name LIMIT @limit";
            count = $"SELECT COUNT(*) FROM nuget_symbols s WHERE {filters}";
        }
        else
        {
            parameters.Add("match", string.Join(' ', words.Select(w => "\"" + w + "\"*")));
            const string from = "FROM nuget_fts JOIN nuget_symbols s ON s.id = nuget_fts.rowid WHERE nuget_fts MATCH @match AND ";
            select = $"SELECT {Projection(wanted)}, -{Bm25} AS Relevance {from}{filters} ORDER BY {Bm25}, s.prominence DESC, s.full_name LIMIT @limit";
            count = $"SELECT COUNT(*) {from}{filters}";
        }

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<HitRow>(new CommandDefinition(select, parameters, cancellationToken: ct));
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(count, parameters, cancellationToken: ct));
        return new NuGetSearchResult(rows.Select(r => ToHit(r, wanted)).ToList(), (int)total);
    }

    public async Task<IReadOnlyList<NuGetSymbolHit>> GetBySidsAsync(
        long packageRowId, IReadOnlyCollection<string> sids, IReadOnlyCollection<string> sections, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sids);
        var wanted = ResolveSections(sections);
        var distinct = sids.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(distinct.Length, MaxSidsPerCall);
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<HitRow>(new CommandDefinition(
            $"SELECT {Projection(wanted)}, 0.0 AS Relevance FROM nuget_symbols s WHERE s.package_row_id = @packageRowId AND s.sid IN @distinct",
            new { packageRowId, distinct },
            cancellationToken: ct));
        var bySid = rows.ToDictionary(r => r.Sid, StringComparer.Ordinal);
        return distinct.Where(bySid.ContainsKey).Select(sid => ToHit(bySid[sid], wanted)).ToList();
    }

    public async Task<IReadOnlyList<NuGetSymbolHit>> ResolveByNameAsync(
        long packageRowId, string identifier, int limit, IReadOnlyCollection<string> sections, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var wanted = ResolveSections(sections);
        var name = identifier.Trim();

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<HitRow>(new CommandDefinition(
            $"SELECT {Projection(wanted)}, 0.0 AS Relevance FROM nuget_symbols s WHERE s.package_row_id = @packageRowId " +
            "AND (s.full_name = @name OR s.name = @name OR s.full_name LIKE @suffix ESCAPE '\\') " +
            "ORDER BY CASE WHEN s.full_name = @name THEN 0 WHEN s.name = @name THEN 1 ELSE 2 END, s.prominence DESC, s.full_name LIMIT @limit",
            new { packageRowId, name, suffix = "%." + EscapeLike(name), limit },
            cancellationToken: ct));
        return rows.Select(r => ToHit(r, wanted)).ToList();
    }

    public Task<bool> DropPackageAsync(NuGetPackageKey key, CancellationToken ct)
    {
        ValidateKey(key);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM nuget_packages WHERE package_id = @PackageId AND version = @Version AND tfm = @Tfm",
                KeyParameters(key),
                tx,
                cancellationToken: k)) > 0,
            ct);
    }

    private static async Task RequireBuildingAsync(SqliteConnection connection, SqliteTransaction tx, long packageRowId, CancellationToken ct)
    {
        var state = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT state FROM nuget_packages WHERE id = @packageRowId",
            new { packageRowId },
            tx,
            cancellationToken: ct));
        if (state != "building")
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"NuGet package row {packageRowId} is not being built (state: {state ?? "missing"}); the build was dropped or taken over."));
        }
    }

    private static async Task<NuGetPackageIndex?> ReadPackageAsync(SqliteConnection connection, NuGetPackageKey key, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<PackageRow>(new CommandDefinition(
            "SELECT " + PackageColumns + " FROM nuget_packages WHERE package_id = @PackageId AND version = @Version AND tfm = @Tfm",
            KeyParameters(key),
            cancellationToken: ct));
        return row is null ? null : ToPackage(row);
    }

    private static object KeyParameters(NuGetPackageKey key) => new { key.PackageId, key.Version, Tfm = key.TargetFramework };

    private static void ValidateKey(NuGetPackageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.PackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.TargetFramework);
    }

    private static void ValidateSymbol(NuGetSymbolEntry symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol.Sid);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol.FullName);
        ArgumentNullException.ThrowIfNull(symbol.Namespace);
        if (!double.IsFinite(symbol.Prominence))
        {
            throw new ArgumentException($"Symbol '{symbol.FullName}' has a prominence that is not a finite number.", nameof(symbol));
        }

        if ((symbol.Kind == NuGetSymbolKind.MethodGroup) != (symbol.DeclaringType is not null))
        {
            throw new ArgumentException($"Symbol '{symbol.FullName}': only a method group has a declaring type, and it must have one.", nameof(symbol));
        }
    }

    private static List<string> ResolveSections(IReadOnlyCollection<string> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var wanted = new List<string>(sections.Count);
        foreach (var section in sections)
        {
            var name = (section ?? string.Empty).Trim().ToLowerInvariant();
            if (!SectionColumns.Any(c => c.Section == name))
            {
                throw new ArgumentException($"Unknown symbol section '{section}'.", nameof(sections));
            }

            if (!wanted.Contains(name))
            {
                wanted.Add(name);
            }
        }

        return wanted;
    }

    private static string Projection(List<string> wanted)
    {
        var text = new StringBuilder("s.sid AS Sid, s.kind AS Kind, s.full_name AS FullName, s.namespace AS Namespace, s.card AS Card, s.prominence AS Prominence");
        foreach (var (section, column, alias) in SectionColumns)
        {
            text.Append(wanted.Contains(section) ? $", s.{column} AS {alias}" : $", NULL AS {alias}");
        }

        return text.ToString();
    }

    private static string Sanitize(string word) => new(word.Where(char.IsLetterOrDigit).ToArray());

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static NuGetSymbolHit ToHit(HitRow row, List<string> wanted)
    {
        var sections = new Dictionary<string, string>(wanted.Count, StringComparer.Ordinal);
        foreach (var section in wanted)
        {
            sections[section] = SectionValue(row, section) ?? string.Empty;
        }

        return new NuGetSymbolHit(
            row.Sid,
            EnumText.FromDb<NuGetSymbolKind>(row.Kind),
            row.FullName,
            row.Namespace,
            row.Card,
            row.Prominence,
            row.Relevance,
            sections);
    }

    private static string? SectionValue(HitRow row, string section) => section switch
    {
        NuGetSections.Sig => row.Sig,
        NuGetSections.Methods => row.Methods,
        NuGetSections.Props => row.Props,
        NuGetSections.Data => row.Data,
        NuGetSections.Doc => row.Doc,
        NuGetSections.Rel => row.Rel,
        _ => null
    };

    private static NuGetPackageIndex ToPackage(PackageRow row) =>
        new(
            row.Id,
            new NuGetPackageKey(row.PackageId, row.Version, row.Tfm),
            EnumText.FromDb<NuGetIndexState>(row.State),
            (int)row.SchemaVersion,
            (int)row.SymbolCount,
            (int)row.TypeCount,
            Timestamp.Parse(row.StartedUtc),
            Timestamp.ParseNullable(row.IndexedUtc),
            row.Error);

    private sealed class StateRow
    {
        public long Id { get; set; }

        public string State { get; set; } = string.Empty;

        public string StartedUtc { get; set; } = string.Empty;
    }

    private sealed class PackageRow
    {
        public long Id { get; set; }

        public string PackageId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Tfm { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public long SchemaVersion { get; set; }

        public long SymbolCount { get; set; }

        public long TypeCount { get; set; }

        public string StartedUtc { get; set; } = string.Empty;

        public string? IndexedUtc { get; set; }

        public string? Error { get; set; }
    }

    private sealed class NamespaceRow
    {
        public string Namespace { get; set; } = string.Empty;

        public long TypeCount { get; set; }
    }

    private sealed class HitRow
    {
        public string Sid { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string Namespace { get; set; } = string.Empty;

        public string Card { get; set; } = string.Empty;

        public double Prominence { get; set; }

        public double Relevance { get; set; }

        public string? Sig { get; set; }

        public string? Methods { get; set; }

        public string? Props { get; set; }

        public string? Data { get; set; }

        public string? Doc { get; set; }

        public string? Rel { get; set; }
    }
}

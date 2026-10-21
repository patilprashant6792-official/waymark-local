using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Approvals;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class ApprovalStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IApprovalStore
{
    private const string Columns =
        "id, pairing_code, client_id, client_name_claimed, redirect_uri, redirect_host, state, code_challenge, scope, resource, " +
        "requested_utc, expires_utc, status, decided_utc, decided_by, decision_reason, code_issued_utc";

    // A request is removable when it is finished. An approved request is kept while its app is still connected.
    private const string RemovableRequests =
        "SELECT a.id FROM approvals a WHERE a.status IN ('denied', 'expired') " +
        "OR (a.status = 'pending' AND a.expires_utc <= @now) " +
        "OR (a.status = 'approved' AND NOT EXISTS (SELECT 1 FROM oauth_clients c WHERE c.client_id = a.client_id AND c.revoked_utc IS NULL))";

    public Task<Approval> CreateAsync(NewApproval request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PollHash);

        return writes.ExecuteAsync<Approval>(async (connection, tx, k) =>
        {
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO approvals (poll_hash, pairing_code, client_id, client_name_claimed, redirect_uri, redirect_host, state, code_challenge, scope, resource, requested_utc, expires_utc, status) " +
                "VALUES (@pollHash, @pairingCode, @clientId, @clientName, @redirectUri, @redirectHost, @state, @codeChallenge, @scope, @resource, @requested, @expires, 'pending') RETURNING id",
                new
                {
                    pollHash = request.PollHash,
                    pairingCode = request.PairingCode,
                    clientId = request.ClientId,
                    clientName = request.ClientNameClaimed,
                    redirectUri = request.RedirectUri,
                    redirectHost = request.RedirectHost,
                    state = request.State,
                    codeChallenge = request.CodeChallenge,
                    scope = request.Scope,
                    resource = request.Resource,
                    requested = Timestamp.Format(request.RequestedUtc),
                    expires = Timestamp.Format(request.ExpiresUtc)
                },
                tx,
                cancellationToken: k));

            return new Approval(
                id,
                request.PairingCode,
                request.ClientId,
                request.ClientNameClaimed,
                request.RedirectUri,
                request.RedirectHost,
                request.State,
                request.CodeChallenge,
                request.Scope,
                request.Resource,
                Timestamp.Parse(Timestamp.Format(request.RequestedUtc)),
                Timestamp.Parse(Timestamp.Format(request.ExpiresUtc)),
                ApprovalStatus.Pending,
                null,
                null,
                null,
                null);
        }, ct);
    }

    public async Task<Approval?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ApprovalRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM approvals WHERE id = @id",
            new { id },
            cancellationToken: ct));
        return row is null ? null : ToApproval(row);
    }

    public async Task<Approval?> GetByPollHashAsync(byte[] pollHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pollHash);
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ApprovalRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM approvals WHERE poll_hash = @pollHash",
            new { pollHash },
            cancellationToken: ct));
        return row is null ? null : ToApproval(row);
    }

    public async Task<IReadOnlyList<Approval>> ListPendingAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<ApprovalRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM approvals WHERE status = 'pending' AND expires_utc > @now ORDER BY id",
            new { now = Timestamp.Format(now) },
            cancellationToken: ct));
        return rows.Select(ToApproval).ToList();
    }

    public async Task<IReadOnlyList<Approval>> ListRecentAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<ApprovalRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM approvals ORDER BY id DESC LIMIT @limit",
            new { limit },
            cancellationToken: ct));
        return rows.Select(ToApproval).ToList();
    }

    public Task<bool> DecideAsync(long id, bool approve, string decidedBy, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        var stamp = Timestamp.Format(now);

        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
        {
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE approvals SET status = @status, decided_utc = @now, decided_by = @by, decision_reason = @reason " +
                "WHERE id = @id AND status = 'pending' AND expires_utc > @now",
                new { status = approve ? "approved" : "denied", now = stamp, by = decidedBy, reason, id },
                tx,
                cancellationToken: k));
            return changed == 1;
        }, ct);
    }

    public Task<CodeMintResult> TryMintCodeAsync(byte[] pollHash, byte[] codeHash, DateTimeOffset codeExpiresUtc, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pollHash);
        ArgumentNullException.ThrowIfNull(codeHash);
        var stamp = Timestamp.Format(now);
        var current = Timestamp.Parse(stamp);
        var codeExpires = Timestamp.Format(codeExpiresUtc);

        return writes.ExecuteAsync<CodeMintResult>(async (connection, tx, k) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<ApprovalRow>(new CommandDefinition(
                "SELECT " + Columns + " FROM approvals WHERE poll_hash = @pollHash",
                new { pollHash },
                tx,
                cancellationToken: k));
            if (row is null)
            {
                return new CodeMintResult(CodeMintStatus.NotFound, null);
            }

            var approval = ToApproval(row);
            if (approval.Status == ApprovalStatus.Pending)
            {
                if (approval.ExpiresUtc > current)
                {
                    return new CodeMintResult(CodeMintStatus.Pending, approval);
                }

                await ExpireAsync(connection, tx, approval.Id, stamp, "timeout", k);
                return new CodeMintResult(CodeMintStatus.Expired, approval with { Status = ApprovalStatus.Expired });
            }

            if (approval.Status == ApprovalStatus.Denied)
            {
                return new CodeMintResult(CodeMintStatus.Denied, approval);
            }

            if (approval.Status == ApprovalStatus.Expired)
            {
                return new CodeMintResult(CodeMintStatus.Expired, approval);
            }

            if (approval.CodeIssuedUtc is not null)
            {
                return new CodeMintResult(CodeMintStatus.AlreadyIssued, approval);
            }

            // An approval given just before the deadline must still be collectable by the polling browser.
            var decided = approval.DecidedUtc ?? approval.ExpiresUtc;
            var collectBy = decided + TimeSpan.FromSeconds(30) > approval.ExpiresUtc
                ? decided + TimeSpan.FromSeconds(30)
                : approval.ExpiresUtc;
            if (collectBy <= current)
            {
                return new CodeMintResult(CodeMintStatus.Expired, approval);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE approvals SET code_issued_utc = @now WHERE id = @id AND code_issued_utc IS NULL",
                new { now = stamp, id = approval.Id },
                tx,
                cancellationToken: k));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO auth_codes (code_hash, approval_id, client_id, redirect_uri, code_challenge, scope, resource, expires_utc) " +
                "VALUES (@codeHash, @approvalId, @clientId, @redirectUri, @codeChallenge, @scope, @resource, @expires)",
                new
                {
                    codeHash,
                    approvalId = approval.Id,
                    clientId = approval.ClientId,
                    redirectUri = approval.RedirectUri,
                    codeChallenge = approval.CodeChallenge,
                    scope = approval.Scope,
                    resource = approval.Resource,
                    expires = codeExpires
                },
                tx,
                cancellationToken: k));

            return new CodeMintResult(CodeMintStatus.Issued, approval with { CodeIssuedUtc = current });
        }, ct);
    }

    public Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken ct)
    {
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE approvals SET status = 'expired', decided_utc = @now, decision_reason = 'timeout' WHERE status = 'pending' AND expires_utc <= @now",
                new { now = stamp },
                tx,
                cancellationToken: k)),
            ct);
    }

    public Task<int> ExpireAllPendingAsync(DateTimeOffset now, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE approvals SET status = 'expired', decided_utc = @now, decision_reason = @reason WHERE status = 'pending'",
                new { now = stamp, reason },
                tx,
                cancellationToken: k)),
            ct);
    }

    public Task<int> ClearFinishedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var parameters = new { now = Timestamp.Format(now) };
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM auth_codes WHERE approval_id IN (" + RemovableRequests + ")",
                parameters,
                tx,
                cancellationToken: k));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE tokens SET approval_id = NULL WHERE approval_id IN (" + RemovableRequests + ")",
                parameters,
                tx,
                cancellationToken: k));
            return await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM approvals WHERE id IN (" + RemovableRequests + ")",
                parameters,
                tx,
                cancellationToken: k));
        }, ct);
    }

    private static Task ExpireAsync(SqliteConnection connection, SqliteTransaction tx, long id, string stamp, string reason, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE approvals SET status = 'expired', decided_utc = @now, decision_reason = @reason WHERE id = @id AND status = 'pending'",
            new { now = stamp, reason, id },
            tx,
            cancellationToken: ct));

    private static Approval ToApproval(ApprovalRow row) =>
        new(
            row.Id,
            row.PairingCode,
            row.ClientId,
            row.ClientNameClaimed,
            row.RedirectUri,
            row.RedirectHost,
            row.State,
            row.CodeChallenge,
            row.Scope,
            row.Resource,
            Timestamp.Parse(row.RequestedUtc),
            Timestamp.Parse(row.ExpiresUtc),
            EnumText.FromDb<ApprovalStatus>(row.Status),
            Timestamp.ParseNullable(row.DecidedUtc),
            row.DecidedBy,
            row.DecisionReason,
            Timestamp.ParseNullable(row.CodeIssuedUtc));

    private sealed class ApprovalRow
    {
        public long Id { get; set; }

        public string PairingCode { get; set; } = string.Empty;

        public string ClientId { get; set; } = string.Empty;

        public string ClientNameClaimed { get; set; } = string.Empty;

        public string RedirectUri { get; set; } = string.Empty;

        public string RedirectHost { get; set; } = string.Empty;

        public string? State { get; set; }

        public string CodeChallenge { get; set; } = string.Empty;

        public string Scope { get; set; } = string.Empty;

        public string Resource { get; set; } = string.Empty;

        public string RequestedUtc { get; set; } = string.Empty;

        public string ExpiresUtc { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? DecidedUtc { get; set; }

        public string? DecidedBy { get; set; }

        public string? DecisionReason { get; set; }

        public string? CodeIssuedUtc { get; set; }
    }
}

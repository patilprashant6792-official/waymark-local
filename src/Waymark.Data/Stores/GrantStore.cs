using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Auth;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class GrantStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IGrantStore
{
    private const string TokenColumns = "kind, family_id, client_id, approval_id, scope, resource, issued_utc, expires_utc, used_utc, revoked_utc";
    private const string CodeColumns = "approval_id, client_id, redirect_uri, code_challenge, scope, resource, expires_utc, consumed_utc";
    private const string AccessKind = "access";
    private const string RefreshKind = "refresh";

    public Task<CodeConsumeResult> ConsumeCodeAsync(byte[] codeHash, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codeHash);
        return writes.ExecuteAsync<CodeConsumeResult>(async (connection, tx, k) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<CodeRow>(new CommandDefinition(
                "SELECT " + CodeColumns + " FROM auth_codes WHERE code_hash = @codeHash",
                new { codeHash },
                tx,
                cancellationToken: k));
            if (row is null)
            {
                return new CodeConsumeResult(CodeConsumeStatus.NotFound, null);
            }

            var code = ToCode(row);
            if (code.ConsumedUtc is not null)
            {
                return new CodeConsumeResult(CodeConsumeStatus.AlreadyConsumed, code);
            }

            if (code.ExpiresUtc <= now)
            {
                return new CodeConsumeResult(CodeConsumeStatus.Expired, code);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE auth_codes SET consumed_utc = @now WHERE code_hash = @codeHash AND consumed_utc IS NULL",
                new { now = Timestamp.Format(now), codeHash },
                tx,
                cancellationToken: k));
            return new CodeConsumeResult(CodeConsumeStatus.Consumed, code with { ConsumedUtc = now });
        }, ct);
    }

    public Task IssuePairAsync(NewToken access, NewToken refresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(refresh);
        return writes.RunAsync(async (connection, tx, k) =>
        {
            await InsertAsync(connection, tx, access, k);
            await InsertAsync(connection, tx, refresh, k);
        }, ct);
    }

    public async Task<StoredToken?> FindActiveAccessAsync(byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hash);
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<TokenRow>(new CommandDefinition(
            "SELECT " + TokenColumns + " FROM tokens WHERE token_hash = @hash AND kind = 'access' AND revoked_utc IS NULL AND expires_utc > @now",
            new { hash, now = Timestamp.Format(now) },
            cancellationToken: ct));
        return row is null ? null : ToToken(row);
    }

    public Task<RotationResult> RotateRefreshAsync(RotationRequest request, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<RotationResult>(async (connection, tx, k) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<TokenRow>(new CommandDefinition(
                "SELECT " + TokenColumns + " FROM tokens WHERE token_hash = @hash AND kind = 'refresh'",
                new { hash = request.PresentedRefreshHash },
                tx,
                cancellationToken: k));
            if (row is null || row.ClientId != request.ClientId)
            {
                return new RotationResult(RotationStatus.NotFound, null);
            }

            var presented = ToToken(row);
            if (presented.RevokedUtc is not null)
            {
                return new RotationResult(RotationStatus.Revoked, presented);
            }

            if (presented.UsedUtc is not null)
            {
                // A refresh token that was already exchanged is being replayed: assume it leaked and end the whole family.
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE tokens SET revoked_utc = @now WHERE family_id = @familyId AND revoked_utc IS NULL",
                    new { now = stamp, familyId = presented.FamilyId },
                    tx,
                    cancellationToken: k));
                return new RotationResult(RotationStatus.ReuseDetected, presented);
            }

            if (presented.ExpiresUtc <= now)
            {
                return new RotationResult(RotationStatus.Expired, presented);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE tokens SET used_utc = @now WHERE token_hash = @hash AND used_utc IS NULL",
                new { now = stamp, hash = request.PresentedRefreshHash },
                tx,
                cancellationToken: k));

            await InsertAsync(connection, tx, Next(request.NewAccessHash, TokenKind.Access, presented, now, request.AccessExpiresUtc), k);
            await InsertAsync(connection, tx, Next(request.NewRefreshHash, TokenKind.Refresh, presented, now, request.RefreshExpiresUtc), k);
            return new RotationResult(RotationStatus.Rotated, presented);
        }, ct);
    }

    public Task<int> RevokeFamilyAsync(string familyId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyId);
        return RevokeWhereAsync("family_id = @value", familyId, now, ct);
    }

    public Task<int> RevokeByApprovalAsync(long approvalId, DateTimeOffset now, CancellationToken ct) =>
        RevokeWhereAsync("approval_id = @value", approvalId, now, ct);

    public Task<bool> RevokeTokenAsync(byte[] hash, string clientId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
        {
            var found = await connection.QuerySingleOrDefaultAsync<TokenRow>(new CommandDefinition(
                "SELECT " + TokenColumns + " FROM tokens WHERE token_hash = @hash AND client_id = @clientId",
                new { hash, clientId },
                tx,
                cancellationToken: k));
            if (found is null)
            {
                return false;
            }

            // Revoking a refresh token ends the family it belongs to; an access token is revoked on its own.
            if (found.Kind == RefreshKind)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE tokens SET revoked_utc = @now WHERE family_id = @familyId AND revoked_utc IS NULL",
                    new { now = stamp, familyId = found.FamilyId },
                    tx,
                    cancellationToken: k));
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE tokens SET revoked_utc = @now WHERE token_hash = @hash AND revoked_utc IS NULL",
                    new { now = stamp, hash },
                    tx,
                    cancellationToken: k));
            }

            return true;
        }, ct);
    }

    public Task<int> PruneExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct)
    {
        var cutoff = Timestamp.Format(expiredBefore);
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
        {
            var tokens = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tokens WHERE expires_utc < @cutoff",
                new { cutoff },
                tx,
                cancellationToken: k));
            var codes = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM auth_codes WHERE expires_utc < @cutoff",
                new { cutoff },
                tx,
                cancellationToken: k));
            return tokens + codes;
        }, ct);
    }

    private Task<int> RevokeWhereAsync(string condition, object value, DateTimeOffset now, CancellationToken ct)
    {
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE tokens SET revoked_utc = @now WHERE " + condition + " AND revoked_utc IS NULL",
                new { now = stamp, value },
                tx,
                cancellationToken: k)),
            ct);
    }

    private static NewToken Next(byte[] hash, TokenKind kind, StoredToken from, DateTimeOffset issued, DateTimeOffset expires) =>
        new(hash, kind, from.FamilyId, from.ClientId, from.ApprovalId, from.Scope, from.Resource, issued, expires);

    private static Task InsertAsync(SqliteConnection connection, SqliteTransaction tx, NewToken token, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tokens (token_hash, kind, family_id, client_id, approval_id, scope, resource, issued_utc, expires_utc) " +
            "VALUES (@hash, @kind, @familyId, @clientId, @approvalId, @scope, @resource, @issued, @expires)",
            new
            {
                hash = token.Hash,
                kind = token.Kind == TokenKind.Access ? AccessKind : RefreshKind,
                familyId = token.FamilyId,
                clientId = token.ClientId,
                approvalId = token.ApprovalId,
                scope = token.Scope,
                resource = token.Resource,
                issued = Timestamp.Format(token.IssuedUtc),
                expires = Timestamp.Format(token.ExpiresUtc)
            },
            tx,
            cancellationToken: ct));

    private static StoredToken ToToken(TokenRow row) =>
        new(
            row.Kind == AccessKind ? TokenKind.Access : TokenKind.Refresh,
            row.FamilyId,
            row.ClientId,
            row.ApprovalId,
            row.Scope,
            row.Resource,
            Timestamp.Parse(row.IssuedUtc),
            Timestamp.Parse(row.ExpiresUtc),
            Timestamp.ParseNullable(row.UsedUtc),
            Timestamp.ParseNullable(row.RevokedUtc));

    private static StoredAuthCode ToCode(CodeRow row) =>
        new(
            row.ApprovalId,
            row.ClientId,
            row.RedirectUri,
            row.CodeChallenge,
            row.Scope,
            row.Resource,
            Timestamp.Parse(row.ExpiresUtc),
            Timestamp.ParseNullable(row.ConsumedUtc));

    private sealed class TokenRow
    {
        public string Kind { get; set; } = string.Empty;

        public string FamilyId { get; set; } = string.Empty;

        public string ClientId { get; set; } = string.Empty;

        public long? ApprovalId { get; set; }

        public string Scope { get; set; } = string.Empty;

        public string Resource { get; set; } = string.Empty;

        public string IssuedUtc { get; set; } = string.Empty;

        public string ExpiresUtc { get; set; } = string.Empty;

        public string? UsedUtc { get; set; }

        public string? RevokedUtc { get; set; }
    }

    private sealed class CodeRow
    {
        public long ApprovalId { get; set; }

        public string ClientId { get; set; } = string.Empty;

        public string RedirectUri { get; set; } = string.Empty;

        public string CodeChallenge { get; set; } = string.Empty;

        public string Scope { get; set; } = string.Empty;

        public string Resource { get; set; } = string.Empty;

        public string ExpiresUtc { get; set; } = string.Empty;

        public string? ConsumedUtc { get; set; }
    }
}

using System.Text.Json;
using Dapper;
using Waymark.Core.Auth;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class OAuthClientStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IOAuthClientStore
{
    private const string Columns = "client_id, client_name, redirect_uris_json, registered_utc, last_used_utc, revoked_utc";

    // Disconnected clients only, and never one with a request that is still waiting for an answer.
    private const string RemovableClients =
        "SELECT c.client_id FROM oauth_clients c WHERE c.revoked_utc IS NOT NULL " +
        "AND NOT EXISTS (SELECT 1 FROM approvals a WHERE a.client_id = c.client_id AND a.status = 'pending' AND a.expires_utc > @now)";

    public Task AddAsync(OAuthClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientId);
        if (client.RedirectUris.Count == 0)
        {
            throw new ArgumentException("At least one redirect URI is required.", nameof(client));
        }

        var urisJson = JsonSerializer.Serialize(client.RedirectUris);
        return writes.RunAsync(async (connection, tx, k) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO oauth_clients (" + Columns + ") VALUES (@id, @name, @uris, @registered, @lastUsed, @revoked)",
                new
                {
                    id = client.ClientId,
                    name = client.ClientName,
                    uris = urisJson,
                    registered = Timestamp.Format(client.RegisteredUtc),
                    lastUsed = Timestamp.FormatNullable(client.LastUsedUtc),
                    revoked = Timestamp.FormatNullable(client.RevokedUtc)
                },
                tx,
                cancellationToken: k));
        }, ct);
    }

    public async Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ClientRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM oauth_clients WHERE client_id = @clientId",
            new { clientId },
            cancellationToken: ct));
        return row is null ? null : ToClient(row);
    }

    public async Task<IReadOnlyList<OAuthClient>> ListAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<ClientRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM oauth_clients ORDER BY registered_utc DESC, client_id",
            cancellationToken: ct));
        return rows.Select(ToClient).ToList();
    }

    public Task TouchAsync(string clientId, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return writes.RunAsync(async (connection, tx, k) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE oauth_clients SET last_used_utc = @at WHERE client_id = @clientId",
                new { at = Timestamp.Format(at), clientId },
                tx,
                cancellationToken: k));
        }, ct);
    }

    public Task<bool> RevokeAsync(string clientId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
        {
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE oauth_clients SET revoked_utc = @now WHERE client_id = @clientId AND revoked_utc IS NULL",
                new { now = stamp, clientId },
                tx,
                cancellationToken: k));
            if (changed == 0)
            {
                return false;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE tokens SET revoked_utc = @now WHERE client_id = @clientId AND revoked_utc IS NULL",
                new { now = stamp, clientId },
                tx,
                cancellationToken: k));
            return true;
        }, ct);
    }

    public Task<int> PruneUnusedAsync(DateTimeOffset registeredBefore, CancellationToken ct) =>
        writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM oauth_clients WHERE registered_utc < @before AND last_used_utc IS NULL " +
                "AND NOT EXISTS (SELECT 1 FROM approvals a WHERE a.client_id = oauth_clients.client_id)",
                new { before = Timestamp.Format(registeredBefore) },
                tx,
                cancellationToken: k)),
            ct);

    public Task<int> ClearDisconnectedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var parameters = new { now = Timestamp.Format(now) };
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM auth_codes WHERE client_id IN (" + RemovableClients + ") " +
                "OR approval_id IN (SELECT id FROM approvals WHERE client_id IN (" + RemovableClients + "))",
                parameters,
                tx,
                cancellationToken: k));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tokens WHERE client_id IN (" + RemovableClients + ") " +
                "OR approval_id IN (SELECT id FROM approvals WHERE client_id IN (" + RemovableClients + "))",
                parameters,
                tx,
                cancellationToken: k));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM approvals WHERE client_id IN (" + RemovableClients + ")",
                parameters,
                tx,
                cancellationToken: k));
            return await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM oauth_clients WHERE client_id IN (" + RemovableClients + ")",
                parameters,
                tx,
                cancellationToken: k));
        }, ct);
    }

    private static OAuthClient ToClient(ClientRow row) =>
        new(
            row.ClientId,
            row.ClientName,
            JsonSerializer.Deserialize<List<string>>(row.RedirectUrisJson) ?? [],
            Timestamp.Parse(row.RegisteredUtc),
            Timestamp.ParseNullable(row.LastUsedUtc),
            Timestamp.ParseNullable(row.RevokedUtc));

    private sealed class ClientRow
    {
        public string ClientId { get; set; } = string.Empty;

        public string ClientName { get; set; } = string.Empty;

        public string RedirectUrisJson { get; set; } = "[]";

        public string RegisteredUtc { get; set; } = string.Empty;

        public string? LastUsedUtc { get; set; }

        public string? RevokedUtc { get; set; }
    }
}

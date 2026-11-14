using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Waymark.Core.Vault;

namespace Waymark.Host.Vault.MasterKey;

/// <summary>
/// Keeps the Data Protection key ring in the database instead of loose files. Data Protection calls this synchronously, sometimes from a
/// Blazor component that holds a synchronization context, so the store is called on the thread pool to keep a blocking wait from deadlocking.
/// </summary>
public sealed class SqliteXmlRepository(IDataProtectionKeyStore store, TimeProvider time) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var stored = Task.Run(() => store.ListAsync(CancellationToken.None)).GetAwaiter().GetResult();
        return stored.Select(xml => XElement.Parse(xml)).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        var xml = element.ToString(SaveOptions.DisableFormatting);
        Task.Run(() => store.AddAsync(friendlyName ?? string.Empty, xml, time.GetUtcNow(), CancellationToken.None)).GetAwaiter().GetResult();
    }
}

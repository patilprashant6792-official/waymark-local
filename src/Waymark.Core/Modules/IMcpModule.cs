using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Waymark.Core.Modules;

public interface IMcpModule
{
    string Name { get; }
    void AddServices(IServiceCollection services, IConfiguration configuration);
    void AddTools(IMcpServerBuilder mcp);
}

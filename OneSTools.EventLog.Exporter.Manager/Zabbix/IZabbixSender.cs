using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OneSTools.EventLog.Exporter.Manager.Zabbix
{
    public interface IZabbixSender
    {
        Task SendStatusAsync(string dataBaseName, bool success, CancellationToken cancellationToken = default);
        Task SendDiscoveryAsync(IReadOnlyCollection<string> dataBaseNames, CancellationToken cancellationToken = default);
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OneSTools.EventLog.Exporter.Manager.Zabbix
{
    public class ZabbixSender : IZabbixSender
    {
        private const string DiscoveryKey = "EventLog.ExportStatus.Discovery";

        private readonly HttpClient _httpClient;
        private readonly ZabbixOptions _options;
        private readonly ILogger<ZabbixSender> _logger;
        private readonly ConcurrentDictionary<string, DateTime> _lastSentAt = new();

        public ZabbixSender(HttpClient httpClient, IOptions<ZabbixOptions> options, ILogger<ZabbixSender> logger = null)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task SendStatusAsync(string dataBaseName, bool success, CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
                return;

            var interval = TimeSpan.FromMinutes(_options.StatusSendIntervalMinutes);
            var now = DateTime.UtcNow;

            if (_lastSentAt.TryGetValue(dataBaseName, out var lastSentAt) && now - lastSentAt < interval)
                return;

            _lastSentAt[dataBaseName] = now;

            var key = $"ExportStatus[{dataBaseName}]";
            var value = success ? "OK" : "FAIL";
            var url = $"https://{_options.Server}/zabbix_sender/index.php" +
                      $"?server={Uri.EscapeDataString(_options.ItemsHost)}" +
                      $"&key={Uri.EscapeDataString(key)}" +
                      $"&value={Uri.EscapeDataString(value)}";

            try
            {
                using var response = await _httpClient.PostAsync(url, null, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.LogWarning(ex, $"Failed to send Zabbix export status for \"{dataBaseName}\"");
            }
        }

        public async Task SendDiscoveryAsync(IReadOnlyCollection<string> dataBaseNames, CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
                return;

            var discoveryData = new
            {
                data = dataBaseNames.Select(name => new Dictionary<string, string> { ["{#IBNAME}"] = name })
            };

            var json = JsonSerializer.Serialize(discoveryData);

            var url = $"https://{_options.Server}/zabbix_sender/index.php";

            var form = new Dictionary<string, string>
            {
                ["server"] = _options.ItemsHost,
                ["key"] = DiscoveryKey,
                ["value"] = json
            };

            using var content = new FormUrlEncodedContent(form);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
            {
                CharSet = "utf-8"
            };

            try
            {
                using var response = await _httpClient.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.LogWarning(ex, "Failed to send Zabbix LLD discovery");
            }
        }
    }
}

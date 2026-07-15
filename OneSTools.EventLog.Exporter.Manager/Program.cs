using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneSTools.EventLog.Exporter.Core;
using OneSTools.EventLog.Exporter.Manager.Zabbix;

namespace OneSTools.EventLog.Exporter.Manager
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .UseSystemd()
                .ConfigureLogging((hostingContext, logging) =>
                {
                    var logPath = Path.Combine(hostingContext.HostingEnvironment.ContentRootPath, "log.txt");
                    logging.AddFile(logPath);
                    logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));

                    // IHttpClientFactory логирует каждый запрос к Zabbix на уровне Information —
                    // при "Default": "Debug" в Logging это забивает лог, поэтому по умолчанию отключено
                    if (!hostingContext.Configuration.GetValue("Zabbix:VerboseHttpLogging", false))
                    {
                        logging.AddFilter("System.Net.Http.HttpClient.IZabbixSender.LogicalHandler", LogLevel.Warning);
                        logging.AddFilter("System.Net.Http.HttpClient.IZabbixSender.ClientHandler", LogLevel.Warning);
                    }
                })
                .ConfigureServices((hostingContext, services) =>
                {
                    services.Configure<ZabbixOptions>(hostingContext.Configuration.GetSection("Zabbix"));

                    // Сервер Zabbix отдаёт самоподписанный сертификат — проверка отключена только для этого клиента
                    services.AddHttpClient<IZabbixSender, ZabbixSender>()
                        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                        });

                    services.AddHostedService<ExportersManager>();
                });
        }
    }
}
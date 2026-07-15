namespace OneSTools.EventLog.Exporter.Manager.Zabbix
{
    public class ZabbixOptions
    {
        public bool Enabled { get; set; }
        public string Server { get; set; } = "";
        public string ItemsHost { get; set; } = "";
        public int StatusSendIntervalMinutes { get; set; } = 5;
        public bool VerboseHttpLogging { get; set; }
    }
}

using DashCall.Collector.Net;
using DashCall.Collector.Sources;
using Microsoft.Extensions.Configuration;

var cfg = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables("DASHCALL_")
    .Build();

var tenantId = cfg["Tenant:Id"]!;
var hubUri = new Uri(cfg["Hub:Url"]!);
var token = cfg["Hub:Token"]!;

// Pasta das gravações na VPS (Módulo 6). Default = padrão do Issabel/Asterisk.
var recordingsDir = cfg["Recordings:Dir"] ?? "/var/spool/asterisk/monitor";

ICallCenterSource source = cfg["Source"] switch
{
    // Poller read-only do MariaDB (sem AMI). ConnString vem do appsettings/env/secret.
    "mariadb" => new MariaDbCallCenterSource(cfg["Db:ConnString"]!, tenantId, recordingsDir, 2000),
    _ => new FakeCallCenterSource(tenantId, intervalMs: 1000)
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"[collector] tenant={tenantId} -> {hubUri}");
// A mesma instância é fonte de snapshots (ICallCenterSource) E de relatórios (IReportSource).
await new HubConnection(hubUri, token)
    .RunAsync(source.StreamAsync(cts.Token), (IReportSource)source, cts.Token);

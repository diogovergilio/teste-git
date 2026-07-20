using DashCall.Collector.Eccp;
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

// Controle do agente via ECCP (Módulo 7 — ESCRITA). Só habilita se a credencial ECCP estiver
// configurada; sem ela, login/pausa ficam indisponíveis e o resto do coletor segue read-only.
var eccpUser = cfg["Eccp:User"];
var eccpPass = cfg["Eccp:Password"];
var eccpHost = cfg["Eccp:Host"] ?? "127.0.0.1";
EccpAgentSource? eccp = eccpUser is not null && eccpPass is not null
    ? new EccpAgentSource(cfg["Db:ConnString"]!, eccpHost, eccpUser, eccpPass)
    : null;

ICallCenterSource source = cfg["Source"] switch
{
    // Poller read-only do MariaDB (sem AMI). ConnString vem do appsettings/env/secret.
    "mariadb" => new MariaDbCallCenterSource(cfg["Db:ConnString"]!, tenantId, recordingsDir, eccp, 2000),
    _ => new FakeCallCenterSource(tenantId, intervalMs: 1000)
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"[collector] tenant={tenantId} -> {hubUri}");
// A mesma instância é fonte de snapshots (ICallCenterSource) E de relatórios (IReportSource).
await new HubConnection(hubUri, token)
    .RunAsync(source.StreamAsync(cts.Token), (IReportSource)source, cts.Token);

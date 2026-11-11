using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Trasgo
{
    /// <summary>Network diagnosis for "tengo lag / no tengo internet" and reminders ("avísame en 10 minutos").</summary>
    public static partial class Tools
    {
        static void AddNet(List<Tool> l)
        {
            l.Add(new Tool
            {
                Name = "network_check",
                Icon = "⇅",
                ReadOnly = true,
                Description = "Diagnóstico de red en ~2 s para 'tengo lag', 'no tengo internet', 'me va lento internet', 'cómo va mi ping': adaptador (cable/WiFi y velocidad), señal WiFi, " +
                              "ping/jitter/pérdida al router, a Cloudflare y a Google, y resolución DNS. Mucho más rápido que hacerlo con PowerShell.",
                Schema = Schema(null),
                Summary = a => "diagnóstico de red",
                Run = (a, host) => ToolResult.Ok(NetworkCheck()),
            });

            l.Add(new Tool
            {
                Name = "reminder",
                Icon = "⏰",
                Description = "Programa un aviso: dentro de X minutos Trasgo aparece encima de lo que estés haciendo con un sonidito y el mensaje. Para 'avísame en 10 minutos', 'recuérdame sacar la pizza en 15', 'en media hora dime que pare de jugar'.",
                Schema = Schema(new[] { "minutes", "text" }, "minutes", Json.Obj("type", "number", "description", "Minutos desde ahora (admite decimales)"), "text", S("Mensaje del aviso, corto y en segunda persona")),
                Summary = a => "aviso en " + Json.Str(a, "minutes") + " min",
                Run = (a, host) =>
                {
                    double min = Json.Dbl(a, 0, "minutes");
                    if (min <= 0 || min > 24 * 60) return ToolResult.Err("Minutos fuera de rango (0-1440).");
                    var resp = Ipc.Request(Json.Obj("op", "remind", "minutes", min, "text", A(a, "text", "¡Aviso!")), 3000);
                    if (resp == null || !Json.Bool(resp, false, "ok")) return ToolResult.Err("Trasgo no está corriendo en segundo plano, no puedo programar el aviso.");
                    return ToolResult.Ok("Aviso programado para las " + DateTime.Now.AddMinutes(min).ToString("HH:mm") + ": " + A(a, "text", ""));
                },
            });
        }

        sealed class PingStats { public string Target; public int Sent, Ok; public List<long> Rtts = new List<long>(); }

        static PingStats PingMany(string host, int count)
        {
            var st = new PingStats { Target = host, Sent = count };
            using (var p = new Ping())
                for (int i = 0; i < count; i++)
                {
                    try { var r = p.Send(host, 900); if (r.Status == IPStatus.Success) { st.Ok++; st.Rtts.Add(r.RoundtripTime); } } catch { }
                    if (i < count - 1) System.Threading.Thread.Sleep(80);
                }
            return st;
        }

        static string Fmt(PingStats s, string label)
        {
            if (s.Ok == 0) return label + ": SIN RESPUESTA (" + s.Sent + " pings perdidos)";
            double avg = s.Rtts.Average();
            double jitter = s.Rtts.Count > 1 ? s.Rtts.Zip(s.Rtts.Skip(1), (x, y) => Math.Abs(x - y)).Average() : 0;
            int loss = (s.Sent - s.Ok) * 100 / s.Sent;
            return label + ": " + avg.ToString("0", CultureInfo.InvariantCulture) + " ms (mín " + s.Rtts.Min() + ", máx " + s.Rtts.Max() + "), jitter " + jitter.ToString("0", CultureInfo.InvariantCulture) + " ms, pérdida " + loss + "%";
        }

        public static string NetworkCheck()
        {
            var sb = new StringBuilder();
            string gateway = null;
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .FirstOrDefault();
            if (nic == null) return "No hay ningún adaptador de red conectado (cable desenchufado o WiFi apagado).";
            var gw = nic.GetIPProperties().GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gw != null) gateway = gw.Address.ToString();
            bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            sb.AppendLine("Adaptador: " + nic.Description + " (" + (wifi ? "WiFi" : "cable/Ethernet") + ", " + (nic.Speed / 1000000) + " Mb/s)");

            var tWifi = wifi ? Task.Run(() => Util.Run("netsh", "wlan show interfaces", 4000).Stdout) : Task.FromResult<string>(null);
            var tGw = gateway != null ? Task.Run(() => PingMany(gateway, 10)) : Task.FromResult<PingStats>(null);
            var tCf = Task.Run(() => PingMany("1.1.1.1", 10));
            var tGg = Task.Run(() => PingMany("8.8.8.8", 10));
            var dnsServers = string.Join(", ", nic.GetIPProperties().DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()));
            var tDns = Task.Run(() =>
            {
                // a name nobody has cached measures the real DNS server, not Windows' cache
                var name = "t" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".example.com";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { Dns.GetHostAddresses(name); } catch { }
                long ms = sw.ElapsedMilliseconds;
                return "DNS (" + dnsServers + "): " + ms + " ms por consulta nueva" + (ms > 1500 ? " ← LENTO: las webs y los lobbies tardan más en conectar; prueba 1.1.1.1 o 8.8.8.8" : ms > 400 ? " (algo lento)" : " (bien)");
            });
            Task.WaitAll(new Task[] { tWifi, tGw, tCf, tGg }, 4000);
            tDns.Wait(3000);

            if (tWifi.Result != null && Regex.IsMatch(tWifi.Result, "ubicaci|location", RegexOptions.IgnoreCase) && !Regex.IsMatch(tWifi.Result, @"(Señal|Signal)\s*:", RegexOptions.IgnoreCase))
                sb.AppendLine("WiFi: Windows no deja leer la señal sin permiso de ubicación (Configuración → Privacidad → Ubicación).");
            else if (tWifi.Result != null)
            {
                var sig = Regex.Match(tWifi.Result, @"(Señal|Signal)\s*:\s*(\d+)%", RegexOptions.IgnoreCase);
                var ssid = Regex.Match(tWifi.Result, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                var band = Regex.Match(tWifi.Result, @"(Banda|Band)\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                sb.AppendLine("WiFi: " + (ssid.Success ? ssid.Groups[1].Value.Trim() : "?") + (sig.Success ? ", señal " + sig.Groups[2].Value + "%" + (int.Parse(sig.Groups[2].Value) < 60 ? " (FLOJA: mejor cable o acercarse al router)" : "") : "") + (band.Success ? ", " + band.Groups[2].Value.Trim() : ""));
            }
            if (tGw.Result != null) sb.AppendLine(Fmt(tGw.Result, "Router (" + gateway + ")"));
            sb.AppendLine(Fmt(tCf.Result, "Cloudflare 1.1.1.1"));
            sb.AppendLine(Fmt(tGg.Result, "Google 8.8.8.8"));
            sb.AppendLine(tDns.IsCompleted ? tDns.Result : "DNS (" + dnsServers + "): más de 3 s sin respuesta ← MUY LENTO (las webs y lobbies tardan en conectar): prueba 1.1.1.1 o 8.8.8.8");
            return sb.ToString();
        }
    }
}

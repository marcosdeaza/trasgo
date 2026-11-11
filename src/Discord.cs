using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace Trasgo
{
    /// <summary>Reads Discord messages from the Windows notification store (wpndatabase.db), read-only.
    /// No Discord token, no self-bot, nothing against Discord's ToS: it only sees what Windows already showed you.</summary>
    public static class DiscordInbox
    {
        public sealed class Msg { public DateTime When; public string Title, Body, App; }

        public static string DbPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications\wpndatabase.db"); } }

        public static List<Msg> Read(int limit, int minutes, string appFilter = "discord")
        {
            var result = new List<Msg>();
            if (!File.Exists(DbPath)) throw new Exception("No encuentro la base de notificaciones de Windows.");
            IntPtr db;
            var path = Encoding.UTF8.GetBytes("file:" + DbPath.Replace('\\', '/') + "?mode=ro\0");
            int rc = Native.sqlite3_open_v2(path, out db, Native.SQLITE_OPEN_READONLY | Native.SQLITE_OPEN_URI, IntPtr.Zero);
            if (rc != 0) { Native.sqlite3_close(db); throw new Exception("No puedo abrir la base de notificaciones (sqlite " + rc + ")."); }
            try
            {
                var sql = Encoding.UTF8.GetBytes(
                    "SELECT n.Payload, n.ArrivalTime, h.PrimaryId FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                    "WHERE n.Type = 'toast' AND h.PrimaryId LIKE '%" + appFilter.Replace("'", "") + "%' ORDER BY n.ArrivalTime DESC LIMIT ?1\0");
                IntPtr st;
                rc = Native.sqlite3_prepare_v2(db, sql, -1, out st, IntPtr.Zero);
                if (rc != 0) throw new Exception("sqlite prepare " + rc + ": " + Marshal.PtrToStringAnsi(Native.sqlite3_errmsg(db)));
                try
                {
                    Native.sqlite3_bind_int(st, 1, Math.Max(1, Math.Min(200, limit * 3)));
                    var since = DateTime.Now.AddMinutes(-Math.Max(1, minutes));
                    while (Native.sqlite3_step(st) == Native.SQLITE_ROW)
                    {
                        int n = Native.sqlite3_column_bytes(st, 0);
                        var blob = Native.sqlite3_column_blob(st, 0);
                        if (blob == IntPtr.Zero || n <= 0) continue;
                        var bytes = new byte[n]; Marshal.Copy(blob, bytes, 0, n);
                        long ft = Native.sqlite3_column_int64(st, 1);
                        var app = Marshal.PtrToStringAnsi(Native.sqlite3_column_text(st, 2)) ?? "";
                        DateTime when;
                        try { when = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { when = DateTime.MinValue; }
                        if (when < since) continue;
                        var texts = ToastTexts(Encoding.UTF8.GetString(bytes));
                        if (texts.Count == 0) continue;
                        result.Add(new Msg { When = when, App = app, Title = texts[0], Body = string.Join(" ", texts.Skip(1)) });
                        if (result.Count >= limit) break;
                    }
                }
                finally { Native.sqlite3_finalize(st); }
            }
            finally { Native.sqlite3_close(db); }
            return result;
        }

        static List<string> ToastTexts(string xml)
        {
            var list = new List<string>();
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(xml.Trim('\0', ' ', '\r', '\n', '﻿'));
                foreach (XmlNode t in doc.GetElementsByTagName("text"))
                    if (!string.IsNullOrWhiteSpace(t.InnerText)) list.Add(t.InnerText.Trim());
            }
            catch { }
            return list;
        }
    }

    public static partial class Tools
    {
        static void AddDiscord(List<Tool> l)
        {
            l.Add(new Tool
            {
                Name = "discord_messages",
                Icon = "✉",
                Description = "Lee los mensajes de Discord que le han llegado al usuario (a partir de las notificaciones de Windows: DMs, menciones, grupos). " +
                              "Solo ve lo que Discord notificó; si sale vacío puede que tenga las notificaciones de Discord desactivadas o en No molestar.",
                Schema = Schema(null, "limit", I("Máximo de mensajes (por defecto 10)"), "minutes", I("Antigüedad máxima en minutos (por defecto 720)"), "from", S("Filtrar por remitente/canal (opcional)")),
                Summary = a => "mensajes de Discord" + (A(a, "from") != null ? " de " + A(a, "from") : ""),
                Run = (a, host) =>
                {
                    var msgs = DiscordInbox.Read(N(a, "limit", 10), N(a, "minutes", 720));
                    var from = A(a, "from");
                    if (from != null) msgs = msgs.Where(m => (m.Title + " " + m.Body).IndexOf(from, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (msgs.Count == 0)
                    {
                        bool running = Process.GetProcessesByName("Discord").Length > 0;
                        string note = "No hay notificaciones de Discord guardadas en Windows" + (from != null ? " de " + from : "") +
                                      " (si las notificaciones de Windows para Discord están desactivadas, Trasgo no puede leerlas por esa vía).";
                        if (!running) return ToolResult.Ok(note + " Además, Discord no está abierto.");
                        // fallback: look at the Discord window itself (title shows the open DM/channel; sidebar shows unread badges)
                        var cap = CaptureWindow("Discord");
                        if (!cap.IsError) { cap.Text = note + " Te paso una captura de la ventana de Discord para que la leas: " + cap.Text; return cap; }
                        return ToolResult.Ok(note + " " + cap.Text);
                    }
                    var sb = new StringBuilder();
                    foreach (var m in msgs.OrderBy(x => x.When))
                        sb.AppendLine("[" + m.When.ToString("HH:mm") + (m.When.Date != DateTime.Today ? " " + m.When.ToString("dd/MM") : "") + "] " + m.Title + ": " + m.Body);
                    return ToolResult.Ok(Util.Truncate(sb.ToString(), 6000));
                },
            });

            l.Add(new Tool
            {
                Name = "app_notifications",
                Icon = "✉",
                Description = "Lee notificaciones recientes de Windows de cualquier app (WhatsApp, Telegram, Steam, Outlook…). 'app' filtra por el identificador de la app.",
                Schema = Schema(null, "app", S("Parte del nombre de la app, p. ej. 'whatsapp', 'telegram', 'steam' (vacío = todas)"), "limit", I("Máximo (por defecto 10)"), "minutes", I("Antigüedad máxima en minutos (por defecto 720)")),
                Summary = a => "notificaciones " + A(a, "app", "todas"),
                Run = (a, host) =>
                {
                    var msgs = DiscordInbox.Read(N(a, "limit", 10), N(a, "minutes", 720), A(a, "app", ""));
                    if (msgs.Count == 0) return ToolResult.Ok("No hay notificaciones recientes" + (A(a, "app") != null ? " de " + A(a, "app") : "") + ".");
                    var sb = new StringBuilder();
                    foreach (var m in msgs.OrderBy(x => x.When))
                    {
                        var app = m.App; int bang = app.LastIndexOf('!'); if (bang >= 0) app = app.Substring(bang + 1);
                        sb.AppendLine("[" + m.When.ToString("HH:mm") + "] (" + Util.OneLine(app, 30) + ") " + m.Title + ": " + m.Body);
                    }
                    return ToolResult.Ok(Util.Truncate(sb.ToString(), 6000));
                },
            });
        }
    }
}

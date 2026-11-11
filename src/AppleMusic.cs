using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Trasgo
{
    /// <summary>Apple Music on Windows, the light way: the app (or iTunes) already reports what's playing in its
    /// window title, media keys work through Windows' media session, and per-app volume goes through the mixer.
    /// Experimental — no official API for a native Windows app, so no search/play of specific songs.</summary>
    public static class AppleMusic
    {
        static readonly string[] ProcNames = { "AppleMusic", "iTunes" };

        public static Process Find()
        {
            foreach (var name in ProcNames)
                foreach (var p in Process.GetProcessesByName(name))
                    try { if (!string.IsNullOrEmpty(p.MainWindowTitle)) return p; } catch { }
            return null;
        }

        /// <summary>"Sonando: X" / "Apple Music abierto, en pausa" / null si no está abierto.</summary>
        public static string Status()
        {
            using (var p = Find())
            {
                if (p == null) return "Apple Music no está abierto.";
                var t = p.MainWindowTitle ?? "";
                // The store app titles the window "Apple Music"; while playing it shows "Canción — Artista".
                if (t.StartsWith("Apple Music") || t.StartsWith("iTunes")) return "Apple Music abierto, en pausa o sin canción.";
                return "Sonando (título de la ventana): " + t;
            }
        }

        public static string Control(string action)
        {
            byte vk;
            switch (action)
            {
                case "play": case "pause": vk = 0xB3; break;
                case "next": vk = 0xB0; break;
                case "previous": vk = 0xB1; break;
                case "restart": vk = 0xB2; break;
                default: throw new Exception("Acción desconocida: " + action);
            }
            if (Find() == null) throw new Exception("Apple Music no está abierto. Ábrelo y dímelo otra vez.");
            Native.TapKey(vk);
            Thread.Sleep(700);
            var t = Status();
            return "Tecla multimedia enviada (" + action + ")." + (t.StartsWith("Sonando") ? " Ahora: " + t.Substring("Sonando (título de la ventana): ".Length) : "");
        }
    }

    public static partial class Tools
    {
        static void AddAppleMusic(List<Tool> l)
        {
            l.Add(new Tool
            {
                Name = "am_status",
                Icon = "♫",
                Description = "Qué está sonando en Apple Music (o iTunes) en este PC, según el título de su ventana. Experimental.",
                Schema = Schema(null),
                Summary = a => "¿qué suena en Apple Music?",
                Run = (a, host) => ToolResult.Ok(AppleMusic.Status()),
            });

            l.Add(new Tool
            {
                Name = "am_control",
                Icon = "♫",
                Description = "Controla Apple Music (o iTunes) con teclas multimedia: play, pause, next, previous, restart. " +
                              "Ojo: si Spotify está sonando a la vez, la tecla multimedia puede irse a él. El volumen por app es 'app_volume' con app=applemusic.",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "play", "pause", "next", "previous", "restart")),
                Summary = a => "Apple Music " + A(a, "action", "?"),
                Run = (a, host) => ToolResult.Ok(AppleMusic.Control(A(a, "action", "play"))),
            });

            l.Add(new Tool
            {
                Name = "am_volume",
                Icon = "♫",
                Description = "Volumen de Apple Music en el mezclador de Windows, sin tocar ninguna ventana: set (value 0-100), up/down (value = paso), mute, unmute.",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "set", "up", "down", "mute", "unmute"), "value", I("Volumen o paso")),
                Summary = a => "Apple Music volumen " + A(a, "action", "?") + (a.ContainsKey("value") ? " " + Json.Str(a, "value") : ""),
                Run = (a, host) => ToolResult.Ok(AudioControl.AppVolume("applemusic", A(a, "action", "set"), a.ContainsKey("value") ? (int?)N(a, "value", 50) : null)),
            });
        }
    }
}

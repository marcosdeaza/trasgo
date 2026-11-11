using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Trasgo
{
    /// <summary>User customisation, all plain files in %APPDATA%\Trasgo:
    ///   tools\*.ps1        → each script with a "# trasgo: description" header becomes a tool
    ///   instrucciones.md   → extra instructions/personality appended to the system prompt</summary>
    public static class Custom
    {
        public static string ToolsDir { get { return Path.Combine(Config.AppDir, "tools"); } }
        public static string InstructionsPath { get { return Path.Combine(Config.AppDir, "instrucciones.md"); } }

        public static string Instructions()
        {
            try
            {
                if (!File.Exists(InstructionsPath)) return null;
                var t = File.ReadAllText(InstructionsPath, Encoding.UTF8);
                // drop the commented template lines
                t = string.Join("\n", t.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("<!--") && !l.TrimStart().StartsWith("//")));
                t = t.Trim();
                return t.Length == 0 ? null : (t.Length > 6000 ? t.Substring(0, 6000) : t);
            }
            catch { return null; }
        }

        public const string InstructionsTemplate =
@"// Escribe aquí cosas que Trasgo debe saber o cómo quieres que se comporte.
// Las líneas que empiezan por // se ignoran. Se aplica en la siguiente pregunta.
// Ejemplos:
// - Llámame Marcos y háblame como un colega de Madrid.
// - Mi juego principal es Fortnite; también juego a Valorant y al CS2.
// - Mis colegas de Discord: Dani y Pablo.
// - Cuando diga ""modo gaming"", usa la herramienta custom_modo_gaming.
// - Si pido música para entrenar, mi playlist es ""Gym"".
";

        public const string ExampleTool =
@"# trasgo: Modo gaming. Cierra lo que sobra antes de jugar (navegadores y launchers que no se usan) y pone el plan de energía de alto rendimiento.
# param keep: Programas que NO hay que cerrar, separados por comas (opcional)
param([string]$keep = '')

$cerrar = 'chrome','msedge','firefox','opera','EpicWebHelper','OneDrive','Teams','ms-teams','Cortana','PhoneExperienceHost'
$keepList = $keep.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
$cerrados = @()
foreach ($n in $cerrar) {
  if ($keepList -contains $n) { continue }
  $p = Get-Process -Name $n -ErrorAction SilentlyContinue
  if ($p) { $p | Stop-Process -Force -ErrorAction SilentlyContinue; $cerrados += $n }
}
powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 2>$null
if ($cerrados) { ""Cerrados: $($cerrados -join ', '). Plan de energía: alto rendimiento."" } else { ""No había nada que cerrar. Plan de energía: alto rendimiento."" }
";

        /// <summary>Create the folders and example files the first time (never overwrites).</summary>
        public static void EnsureScaffold()
        {
            try
            {
                Directory.CreateDirectory(ToolsDir);
                if (!File.Exists(InstructionsPath)) File.WriteAllText(InstructionsPath, InstructionsTemplate, new UTF8Encoding(true));
                var ex = Path.Combine(ToolsDir, "modo_gaming.ps1.ejemplo");
                if (!File.Exists(ex)) File.WriteAllText(ex, ExampleTool, new UTF8Encoding(true));
            }
            catch (Exception e) { Log.Write("custom scaffold: " + e.Message); }
        }

        static readonly Regex Header = new Regex(@"^\s*#\s*trasgo\s*:\s*(.+)$", RegexOptions.IgnoreCase);
        static readonly Regex ParamLine = new Regex(@"^\s*#\s*param\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.+)$", RegexOptions.IgnoreCase);

        public static List<Tool> LoadTools()
        {
            var list = new List<Tool>();
            if (!Directory.Exists(ToolsDir)) return list;
            foreach (var file in Directory.GetFiles(ToolsDir, "*.ps1").OrderBy(f => f))
            {
                try
                {
                    var lines = File.ReadAllLines(file, Encoding.UTF8).Take(40).ToList();
                    string desc = null; bool readOnly = false;
                    var pars = new List<KeyValuePair<string, string>>();
                    foreach (var l in lines)
                    {
                        var m = Header.Match(l); if (m.Success && desc == null) { desc = m.Groups[1].Value.Trim(); continue; }
                        var p = ParamLine.Match(l); if (p.Success) { pars.Add(new KeyValuePair<string, string>(p.Groups[1].Value, p.Groups[2].Value.Trim())); continue; }
                        if (Regex.IsMatch(l, @"^\s*#\s*readonly\s*$", RegexOptions.IgnoreCase)) readOnly = true;
                    }
                    if (desc == null) continue; // not a Trasgo tool
                    var name = "custom_" + Regex.Replace(Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
                    var props = new Dictionary<string, object>();
                    foreach (var kv in pars) props[kv.Key] = Json.Obj("type", "string", "description", kv.Value);
                    var path = file;
                    list.Add(new Tool
                    {
                        Name = name,
                        Icon = "★",
                        ReadOnly = readOnly,
                        Description = "[Herramienta personalizada del usuario] " + desc,
                        Schema = Json.Obj("type", "object", "properties", props),
                        Summary = a => name.Substring(7).Replace('_', ' ') + (a.Count > 0 ? " " + Util.OneLine(string.Join(", ", a.Values.Select(v => Convert.ToString(v))), 50) : ""),
                        Run = (a, host) =>
                        {
                            var sb = new StringBuilder("& '" + path.Replace("'", "''") + "'");
                            foreach (var kv in a)
                            {
                                if (!pars.Any(p => p.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                                sb.Append(" -" + kv.Key + " '" + Convert.ToString(kv.Value).Replace("'", "''") + "'");
                            }
                            var r = Tools.RunPowerShell(sb.ToString(), 120000);
                            var outp = (r.Stdout.Trim() + (r.Stderr.Trim().Length > 0 ? "\n[stderr] " + r.Stderr.Trim() : "")).Trim();
                            return new ToolResult { Text = Util.Truncate(outp.Length > 0 ? outp : "hecho (exit " + r.ExitCode + ")", 6000), IsError = r.ExitCode != 0 && r.Stdout.Trim().Length == 0 };
                        },
                    });
                }
                catch (Exception ex) { Log.Write("custom tool " + file + ": " + ex.Message); }
            }
            return list;
        }
    }
}

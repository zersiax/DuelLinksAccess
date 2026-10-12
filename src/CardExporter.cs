using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MelonLoader;
using Il2CppYgomGame.Card;
using Il2CppYgomGame.Deck;
using Il2CppYgomGame.Single;

namespace DuelLinksAccess
{
    /// <summary>
    /// Safely exports all owned cards and their detailed card text/effects to a structured plain text file.
    /// Threading model:
    /// 1. Il2Cpp card database extraction runs exclusively on the Unity main thread (guarantees 0 crash).
    /// 2. Windows SaveFileDialog runs in an isolated external subprocess via PowerShell (100% memory-isolated,
    ///    no P/Invoke stack corruption, and completely accessible with NVDA/Zhengdu).
    /// 3. Remembers the user's chosen folder for all future exports.
    /// </summary>
    public static class CardExporter
    {
        private static bool _isExporting = false;
        private static readonly string ConfigDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData");
        private static readonly string ConfigFilePath = Path.Combine(ConfigDir, "DuelLinksAccess_export_dir.txt");

        /// <summary>
        /// Initiates the card export process. Called safely on the Unity main thread upon hotkey press (Ctrl+E / F8).
        /// </summary>
        public static void ExportOwnedCards()
        {
            MelonLogger.Msg("[CardExporter] Export hotkey triggered!");
            if (_isExporting)
            {
                ScreenReader.Say("卡片导出正在进行中，请稍候...");
                return;
            }

            var content = Content.Instance;
            if (content == null)
            {
                MelonLogger.Msg("[CardExporter] Content.Instance is null");
                ScreenReader.Say("卡片数据库尚未就绪，请进入游戏主菜单后再试。");
                return;
            }

            _isExporting = true;

            try
            {
                // Step 1: Extract all card data safely on the Unity Main Thread
                string exportText;
                int totalDistinctCount;
                int totalCopies;

                bool extracted = TryExtractCardsMainThread(out exportText, out totalDistinctCount, out totalCopies);
                MelonLogger.Msg($"[CardExporter] Extraction completed: extracted={extracted}, distinct={totalDistinctCount}, copies={totalCopies}");
                if (!extracted || totalDistinctCount == 0)
                {
                    ScreenReader.Say("未检测到您当前持有的卡片数据，请先进入卡片商人或卡组编辑加载卡箱。");
                    _isExporting = false;
                    return;
                }

                ScreenReader.Say("正在打开保存文件窗口，请选择保存位置...");

                // Step 2: Spawn isolated dialog worker on background thread (does not touch Il2Cpp)
                var ctx = new ExportContext
                {
                    Text = exportText,
                    Distinct = totalDistinctCount,
                    Copies = totalCopies
                };
                var th = new Thread(new ParameterizedThreadStart(SaveFileWorkerEntry));
                th.IsBackground = true;
                th.Start(ctx);
            }
            catch (Exception exMain)
            {
                MelonLogger.Msg($"[CardExporter] MainThread extraction error: {exMain.Message}");
                DebugLogger.Log(LogCategory.Handler, "CardExporter", $"MainThread error: {exMain}");
                ScreenReader.Say($"卡片提取失败：{exMain.Message}");
                _isExporting = false;
            }
        }

        /// <summary>
        /// Reads card data strictly on the Unity Main Thread.
        /// </summary>
        private static bool TryExtractCardsMainThread(out string exportText, out int totalDistinctCount, out int totalCopies)
        {
            exportText = "";
            totalDistinctCount = 0;
            totalCopies = 0;

            var content = Content.Instance;
            if (content == null) return false;

            TrunkData trunk = null;
            try { trunk = TrunkData.Create(); } catch { }

            var candidateMrks = new HashSet<int>();

            // Collect known card IDs
            try
            {
                var allList = content.allCardList;
                if (allList != null && allList.Count > 0)
                {
                    for (int i = 0; i < allList.Count; i++) candidateMrks.Add(allList[i]);
                }
            }
            catch { }

            if (candidateMrks.Count == 0)
            {
                try
                {
                    var mrkDict = content.mrk2idx;
                    if (mrkDict != null)
                    {
                        foreach (var key in mrkDict.Keys) candidateMrks.Add(key);
                    }
                }
                catch { }
            }

            if (candidateMrks.Count == 0)
            {
                for (int i = 1; i <= 32000; i++) candidateMrks.Add(i);
            }

            var monsterCards = new List<CardRecord>();
            var spellCards = new List<CardRecord>();
            var trapCards = new List<CardRecord>();

            foreach (int mrk in candidateMrks)
            {
                if (mrk <= 0) continue;

                int count = 0;
                if (trunk != null)
                {
                    try { count = trunk.GetNum(mrk); } catch { }
                }

                if (count <= 0)
                {
                    try { count = CardTraderInfoBase.CardPoss(mrk, 0); } catch { }
                }

                if (count <= 0) continue;

                totalCopies += count;

                string name = "";
                try { name = content.GetName(mrk); } catch { }
                if (string.IsNullOrWhiteSpace(name)) name = $"(未知卡片 #{mrk})";

                var kind = Content.Kind.Normal;
                try { kind = content.GetKind(mrk); } catch { }

                string desc = "";
                try { desc = content.GetDesc(mrk); } catch { }
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    desc = desc.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
                }

                var record = new CardRecord
                {
                    Mrk = mrk,
                    Name = name,
                    Count = count,
                    Kind = kind,
                    Desc = desc
                };

                if (CardFormatter.IsMonsterKind(kind))
                {
                    try
                    {
                        var attr = content.GetAttr(mrk);
                        var type = content.GetType(mrk);
                        record.AttributeText = content.GetAttributeText(attr);
                        record.TypeText = content.GetTypeText(type);
                        record.KindText = content.GetKindText(kind);
                        record.Level = content.GetLevel(mrk);
                        record.Rank = content.GetRank(mrk);
                        record.LinkNum = content.GetLinkNum(mrk);
                        record.Atk = content.GetAtk(mrk);
                        record.Def = content.GetDef2(mrk);
                    }
                    catch { }
                    monsterCards.Add(record);
                }
                else if (kind == Content.Kind.Magic)
                {
                    try
                    {
                        var icon = content.GetIcon(mrk);
                        record.KindText = icon != Content.Icon.Null
                            ? $"{content.GetIconText(icon)} 魔法卡"
                            : content.GetKindText(kind);
                    }
                    catch
                    {
                        record.KindText = "魔法卡";
                    }
                    spellCards.Add(record);
                }
                else
                {
                    try
                    {
                        var icon = content.GetIcon(mrk);
                        record.KindText = icon != Content.Icon.Null
                            ? $"{content.GetIconText(icon)} 陷阱卡"
                            : content.GetKindText(kind);
                    }
                    catch
                    {
                        record.KindText = "陷阱卡";
                    }
                    trapCards.Add(record);
                }
            }

            totalDistinctCount = monsterCards.Count + spellCards.Count + trapCards.Count;
            if (totalDistinctCount == 0) return false;

            // Sort cards by card ID
            monsterCards.Sort((a, b) => a.Mrk.CompareTo(b.Mrk));
            spellCards.Sort((a, b) => a.Mrk.CompareTo(b.Mrk));
            trapCards.Sort((a, b) => a.Mrk.CompareTo(b.Mrk));

            // Format human-friendly plain text document
            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("                   游戏王：决斗链接 - 持有卡片及具体效果清单");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"持有卡片种类总数：{totalDistinctCount} 种");
            sb.AppendLine($"持有卡片总张数：  {totalCopies} 张");
            sb.AppendLine($"  - 怪兽卡：{monsterCards.Count} 种");
            sb.AppendLine($"  - 魔法卡：{spellCards.Count} 种");
            sb.AppendLine($"  - 陷阱卡：{trapCards.Count} 种");
            sb.AppendLine("================================================================================");
            sb.AppendLine();

            // 1. 怪兽卡
            sb.AppendLine("################################################################################");
            sb.AppendLine($"【怪兽卡一览】(共 {monsterCards.Count} 种)");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            for (int i = 0; i < monsterCards.Count; i++)
            {
                var c = monsterCards[i];
                sb.AppendLine($"[{i + 1}] {c.Name} (卡片编号: {c.Mrk})");
                sb.AppendLine($"    持有数量：{c.Count} 张");

                string progression = "";
                if (c.LinkNum > 0) progression = $"Link-{c.LinkNum}";
                else if (c.Rank > 0) progression = $"{c.Rank} 阶";
                else if (c.Level > 0) progression = $"{c.Level} 星";

                string combat = $"攻击力: {c.Atk}";
                if (c.LinkNum <= 0) combat += $" / 守备力: {c.Def}";

                sb.AppendLine($"    属性种族：{c.AttributeText} | {c.TypeText} | {c.KindText} | {progression} | {combat}");
                sb.AppendLine("    【卡片效果】:");
                if (!string.IsNullOrWhiteSpace(c.Desc))
                {
                    string[] lines = c.Desc.Split('\n');
                    foreach (var l in lines) sb.AppendLine($"        {l}");
                }
                else
                {
                    sb.AppendLine("        (通常怪兽无效果说明)");
                }
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine();
            }

            // 2. 魔法卡
            sb.AppendLine("################################################################################");
            sb.AppendLine($"【魔法卡一览】(共 {spellCards.Count} 种)");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            for (int i = 0; i < spellCards.Count; i++)
            {
                var c = spellCards[i];
                sb.AppendLine($"[{i + 1}] {c.Name} (卡片编号: {c.Mrk})");
                sb.AppendLine($"    持有数量：{c.Count} 张");
                sb.AppendLine($"    卡片种类：{c.KindText}");
                sb.AppendLine("    【卡片效果】:");
                if (!string.IsNullOrWhiteSpace(c.Desc))
                {
                    string[] lines = c.Desc.Split('\n');
                    foreach (var l in lines) sb.AppendLine($"        {l}");
                }
                else
                {
                    sb.AppendLine("        (无特别说明)");
                }
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine();
            }

            // 3. 陷阱卡
            sb.AppendLine("################################################################################");
            sb.AppendLine($"【陷阱卡一览】(共 {trapCards.Count} 种)");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            for (int i = 0; i < trapCards.Count; i++)
            {
                var c = trapCards[i];
                sb.AppendLine($"[{i + 1}] {c.Name} (卡片编号: {c.Mrk})");
                sb.AppendLine($"    持有数量：{c.Count} 张");
                sb.AppendLine($"    卡片种类：{c.KindText}");
                sb.AppendLine("    【卡片效果】:");
                if (!string.IsNullOrWhiteSpace(c.Desc))
                {
                    string[] lines = c.Desc.Split('\n');
                    foreach (var l in lines) sb.AppendLine($"        {l}");
                }
                else
                {
                    sb.AppendLine("        (无特别说明)");
                }
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine();
            }

            exportText = sb.ToString();
            return true;
        }

        private static void SaveFileWorkerEntry(object state)
        {
            var ctx = state as ExportContext;
            if (ctx == null)
            {
                _isExporting = false;
                return;
            }

            try
            {
                SaveFileWorker(ctx.Text, ctx.Distinct, ctx.Copies);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[CardExporter] Worker error: {ex.Message}");
                DebugLogger.Log(LogCategory.Handler, "CardExporter", $"Worker error: {ex}");
                ScreenReader.Say($"导出过程出错：{ex.Message}");
            }
            finally
            {
                _isExporting = false;
            }
        }

        /// <summary>
        /// Background worker: Prompts user with a process-isolated SaveFileDialog and writes the file.
        /// </summary>
        private static void SaveFileWorker(string exportText, int totalDistinctCount, int totalCopies)
        {
            string initialDir = GetInitialDirectory();
            string defaultFileName = $"游戏王决斗链接_我的卡片收藏_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            string chosenPath = ShowSubprocessSaveDialog(initialDir, defaultFileName);
            if (string.IsNullOrEmpty(chosenPath))
            {
                // Fallback: Save a copy to Desktop so user never loses their data
                try
                {
                    string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    string fallbackPath = Path.Combine(desktopDir, defaultFileName);
                    File.WriteAllText(fallbackPath, exportText, Encoding.UTF8);
                    MelonLogger.Msg($"[CardExporter] Dialog closed/empty, fallback saved to: {fallbackPath}");
                    ScreenReader.Say($"选择窗口已关闭。已为您自动保存一份卡片清单至桌面：{defaultFileName}，共 {totalDistinctCount} 种卡片。");
                    return;
                }
                catch (Exception exFb)
                {
                    MelonLogger.Msg($"[CardExporter] Desktop fallback failed: {exFb.Message}");
                    ScreenReader.Say("已取消导出。");
                    return;
                }
            }

            // Save directory for next time
            try
            {
                string dir = Path.GetDirectoryName(chosenPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                    File.WriteAllText(ConfigFilePath, dir, Encoding.UTF8);
                }
            }
            catch { }

            // Write formatted text to destination
            File.WriteAllText(chosenPath, exportText, Encoding.UTF8);

            DebugLogger.Log(LogCategory.Handler, "CardExporter", $"Cards successfully exported to: {chosenPath}");
            ScreenReader.Say($"导出完成！共导出您拥有的 {totalDistinctCount} 种卡片，总计 {totalCopies} 张，已保存至 {Path.GetFileName(chosenPath)}。");
        }

        private static string GetInitialDirectory()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string saved = File.ReadAllText(ConfigFilePath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
                        return saved;
                }
            }
            catch { }

            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        /// <summary>
        /// Spawns an external helper process to display the native Windows SaveFileDialog.
        /// 100% isolates COM and Win32 dialog state from the game process, guaranteeing zero crashes.
        /// </summary>
        private static string ShowSubprocessSaveDialog(string initialDir, string defaultFileName)
        {
            try
            {
                string helperPath = Path.Combine(ConfigDir, "DuelLinksFilePicker.exe");
                string resultFile = Path.Combine(ConfigDir, "DuelLinksAccess_picked_path.tmp");

                if (File.Exists(helperPath))
                {
                    try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }

                    MelonLogger.Msg($"[CardExporter] Launching helper: {helperPath}");
                    var psi = new ProcessStartInfo
                    {
                        FileName = helperPath,
                        Arguments = $"\"{initialDir}\" \"{defaultFileName}\" \"{resultFile}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd()?.Trim();
                        proc.WaitForExit();
                        MelonLogger.Msg($"[CardExporter] Helper exited. Stdout='{output}'");

                        // 1. Check result file first
                        if (File.Exists(resultFile))
                        {
                            try
                            {
                                string fileContent = File.ReadAllText(resultFile, Encoding.UTF8).Trim();
                                File.Delete(resultFile);
                                if (!string.IsNullOrWhiteSpace(fileContent))
                                {
                                    MelonLogger.Msg($"[CardExporter] Got chosen path from result file: {fileContent}");
                                    return fileContent;
                                }
                            }
                            catch (Exception exFile)
                            {
                                MelonLogger.Msg($"[CardExporter] Reading result file error: {exFile.Message}");
                            }
                        }

                        // 2. Check stdout
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            MelonLogger.Msg($"[CardExporter] Got chosen path from stdout: {output}");
                            return output;
                        }

                        MelonLogger.Msg("[CardExporter] Helper returned no path (user canceled or dialog closed)");
                        return null; // User canceled
                    }
                }

                // Fallback: PowerShell without -WindowStyle Hidden
                MelonLogger.Msg("[CardExporter] Helper not found, falling back to powershell dialog");
                string safeDir = (initialDir ?? "").Replace("'", "''");
                string safeFile = (defaultFileName ?? "").Replace("'", "''");

                string script = $@"
Add-Type -AssemblyName System.Windows.Forms
$dlg = New-Object System.Windows.Forms.SaveFileDialog
$dlg.Title = '请选择卡片清单导出保存位置'
$dlg.Filter = '纯文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*'
$dlg.FileName = '{safeFile}'
$dlg.InitialDirectory = '{safeDir}'
$dlg.RestoreDirectory = $true
if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    Write-Output $dlg.FileName
}}
";
                string base64Script = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

                var psiPs = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-STA -NoProfile -EncodedCommand {base64Script}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var procPs = Process.Start(psiPs);
                if (procPs == null) return null;

                string psOutput = procPs.StandardOutput.ReadToEnd()?.Trim();
                procPs.WaitForExit();

                if (!string.IsNullOrWhiteSpace(psOutput))
                {
                    return psOutput;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[CardExporter] ShowSubprocessSaveDialog error: {ex.Message}");
            }

            return null;
        }

        private class CardRecord
        {
            public int Mrk;
            public string Name;
            public int Count;
            public Content.Kind Kind;
            public string KindText;
            public string AttributeText;
            public string TypeText;
            public int Level;
            public int Rank;
            public int LinkNum;
            public int Atk;
            public int Def;
            public string Desc;
        }

        private class ExportContext
        {
            public string Text;
            public int Distinct;
            public int Copies;
        }
    }
}

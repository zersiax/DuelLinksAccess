import os
import sys
import io

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

mods_dir = r"F:\SteamLibrary\steamapps\common\Yu-Gi-Oh! Duel Links\Mods"

deleted = 0
failed = 0

if os.path.exists(mods_dir):
    for f in os.listdir(mods_dir):
        if f.endswith(".old") or f.endswith(".bak") or f.endswith(".old_locked"):
            fp = os.path.join(mods_dir, f)
            try:
                os.remove(fp)
                print(f"[已删除备份文件]: {fp}")
                deleted += 1
            except Exception as ex:
                print(f"[等待游戏关闭后删除]: {fp} (原因: {ex})")
                failed += 1

if deleted > 0 and failed == 0:
    print(f"所有备份文件清理完成！共删除 {deleted} 个旧版本文件。")
elif failed > 0:
    print(f"\n提示：检测到游戏当前正在运行中，Windows 系统锁定了旧 DLL 句柄。退出游戏后再次运行本脚本即可彻底删除。")
else:
    print("未发现任何旧版本备份文件，目录非常干净！")

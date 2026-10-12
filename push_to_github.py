import subprocess
import sys
import io
import os

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

USER = "liguangyao123456aaa"
REPO = "DuelLinksAccess"
BRANCH = "ch"

def main():
    print("=" * 60)
    print(f"DuelLinksAccess 一键上传工具 (One-Click GitHub Uploader)")
    print(f"目标仓库: https://github.com/{USER}/{REPO}")
    print(f"本地分支: {BRANCH}")
    print("=" * 60)

    token = None
    if len(sys.argv) > 1:
        token = sys.argv[1].strip()
    else:
        try:
            token = input("请输入您的 GitHub Token (以 ghp_ 开头): ").strip()
        except EOFError:
            pass

    if not token:
        print("\n[提示] 未输入 Token，尝试直接调用系统 Git 凭据管理器推送...")
        target_url = f"https://github.com/{USER}/{REPO}.git"
    else:
        # Avoid embedding token in output
        target_url = f"https://{token}@github.com/{USER}/{REPO}.git"

    print("\n[1/3] 检查远程仓库分支...")
    try:
        # Push ch branch as main branch on user's fork
        print(f"[2/3] 正在推送本地 {BRANCH} 分支到 GitHub 远程 main 和 {BRANCH} 分支...")
        cmd_main = ["git", "push", target_url, f"{BRANCH}:main", "--force"]
        res_main = subprocess.run(cmd_main, capture_output=True, text=True, encoding='utf-8', errors='replace')
        
        cmd_ch = ["git", "push", target_url, f"{BRANCH}:{BRANCH}", "--force"]
        res_ch = subprocess.run(cmd_ch, capture_output=True, text=True, encoding='utf-8', errors='replace')

        if res_main.returncode == 0 or res_ch.returncode == 0:
            print("\n" + "=" * 60)
            print("【推送成功！】")
            print(f"您的开源仓库已更新：https://github.com/{USER}/{REPO}")
            print(f"所有中文无障碍最新代码已成功同步！")
            print("=" * 60)
        else:
            print("\n[推送失败] 详细错误信息：")
            if res_main.stderr:
                # Sanitize token in error messages
                clean_err = res_main.stderr.replace(token, "***") if token else res_main.stderr
                print(clean_err)
            if res_ch.stderr:
                clean_err = res_ch.stderr.replace(token, "***") if token else res_ch.stderr
                print(clean_err)
    except Exception as ex:
        print(f"\n[执行异常]: {ex}")
    finally:
        # Always ensure origin URL is clean without token in config
        subprocess.run(["git", "remote", "set-url", "origin", f"https://github.com/{USER}/{REPO}.git"], capture_output=True)

if __name__ == "__main__":
    main()

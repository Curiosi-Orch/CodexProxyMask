@echo off
chcp 65001 >nul
echo ================================================
echo  删除 Codex 沙箱遗留的防火墙规则(修复回环阻断)
echo ================================================
echo.

netsh advfirewall firewall delete rule name="codex_sandbox_offline_block_loopback_tcp"
netsh advfirewall firewall delete rule name="codex_sandbox_offline_block_loopback_udp"
netsh advfirewall firewall delete rule name="codex_sandbox_offline_block_outbound"

echo.
echo 删除完成。若上方每条都显示"确定。"或"找不到"均属正常。
echo 规则删除后立即生效,无需重启。
echo 请刷新浏览器或重启 MonoCloud 客户端验证外网是否恢复。
echo.
echo 如果浏览器仍打不开:关闭系统代理再重新打开
echo (Windows 设置 - 网络 - 代理 中切换,或重启 MonoCloud 客户端)。
echo.
pause

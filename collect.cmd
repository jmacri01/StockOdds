@echo off
REM Daily snapshot collector for the 0-DTE research line. None of these feeds backfill:
REM a session that is not collected is gone. Schedule shortly after the US close.
REM   schtasks /Create /TN StockOddsCollect /TR "C:\git\StockOdds\.claude\worktrees\lt-transition-bias\collect.cmd" /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 16:20
cd /d "%~dp0"
dotnet run --project StockOdds\StockOdds.csproj -c Debug -- collect >> data\collect.log 2>&1

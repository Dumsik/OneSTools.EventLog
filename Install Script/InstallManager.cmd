::Скрипт для автоматизации установки службы экспортера журнала регистрации 1с в кликхаус
::https://kaiten.corp.grandtrade.world/p/d/9a707abf-bcbc-4c7b-be86-74513bf3ac0d
::Автор Кирилкин Д.А.
@echo off
SETLOCAL ENABLEEXTENSIONS 
SETLOCAL ENABLEDELAYEDEXPANSION
SET SERVICE_NAME=OnesEventLogExportersManager
SET SERVICE_DESCR=Экспортер журналов регистрации 1С:Предприятия в ClickHouse
SET SCRIPT_FOLDER=%~dp0
SET DISLPAY_HELP=1

IF /I "%1"=="/INSTALL" SET DISLPAY_HELP=0 & CALL :INSTALL
IF /I "%1"=="/UNINSTALL" SET DISLPAY_HELP=0 & CALL :UNINSTALL


IF %DISLPAY_HELP%==1 CALL :HELP 

GOTO :EOF


:INSTALL

echo Install service "%SERVICE_NAME%" ...

set SrvBinPath=\"%SCRIPT_FOLDER%EventLogExportersManager.exe\"

sc create "%SERVICE_NAME%" binPath= "%SrvBinPath%" start= auto obj= "NT AUTHORITY\LocalService" password= "" displayname= "%SERVICE_DESCR%" depend= Dnscache/Tcpip
IF %ERRORLEVEL% LEQ 1 sc failure "%SERVICE_NAME%" reset=0 actions=restart/60000

GOTO :EOF

:UNINSTALL

echo Uninstall service "%SERVICE_NAME%" ...
sc stop "%SERVICE_NAME%"
sc delete "%SERVICE_NAME%"

GOTO :EOF

:HELP

echo Script for OneSTools.EventLog.Exporter service setup and configure
echo Usage:
echo /INSTALL
echo /UNINSTALL 

GOTO :EOF
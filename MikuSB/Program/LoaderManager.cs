using Microsoft.AspNetCore.Components;
using MikuSB.Data;
using MikuSB.Database;
using MikuSB.GameServer.Command;
using MikuSB.GameServer.Server;
using MikuSB.GameServer.Server.CallGS;
using MikuSB.GameServer.Server.Packet;
using MikuSB.Internationalization;
using MikuSB.MikuSB.Tool;
using MikuSB.MikuSB.Update;
using MikuSB.Proto;
using MikuSB.SdkServer;
using MikuSB.TcpSharp;
using MikuSB.Util;
using MikuSB.Util.Security;
using System.Reflection;

namespace MikuSB.MikuSB.Program;

public class LoaderManager : MikuSB
{
    public static void InitConfig()
    {
        // Initialize log
        var logDir = ConfigManager.Config.Path.LogPath;
        var logFile = new FileInfo(Path.Combine(logDir, "Server.log"));
        logFile.Directory?.Create();

        if (logFile.Exists)
        {
            // Read start time from first log line, fall back to file creation time
            DateTime logStartTime;
            try
            {
                var firstLine = File.ReadLines(logFile.FullName).FirstOrDefault() ?? "";
                // Format: [HH:mm:ss] ...
                var timeStr = firstLine.Length >= 10 ? firstLine[1..9] : "";
                var dateStr = logFile.CreationTime.ToString("yyyy-MM-dd");
                logStartTime = DateTime.TryParse($"{dateStr} {timeStr}", out var parsed)
                    ? parsed
                    : logFile.CreationTime;
            }
            catch
            {
                logStartTime = logFile.CreationTime;
            }

            var backupName = $"Server-backup-{logStartTime:yyyy.MM.dd-HH.mm.ss}.log";
            var backupFile = new FileInfo(Path.Combine(logDir, backupName));
            logFile.MoveTo(backupFile.FullName, overwrite: true);
        }

        Logger.SetLogFile(new FileInfo(Path.Combine(logDir, "Server.log")));

        // Init all directories
        try
        {
            ConfigManager.InitDirectories();
        }
        catch (Exception e)
        {
            Logger.Error(I18NManager.Translate("Server.ServerInfo.FailedToLoadItem", I18NManager.Translate("Word.Config")), e);
            throw;
        }

        // Starting the server
        Logger.Info(I18NManager.Translate("Server.ServerInfo.StartingServer"));
        Logger.Info($"Build version: {BuildVersion.Current}");

        // Load the config
        Logger.Info(I18NManager.Translate("Server.ServerInfo.LoadingItem", I18NManager.Translate("Word.Config")));
        try
        {
            ConfigManager.LoadConfig();
        }
        catch (Exception e)
        {
            Logger.Error(
                I18NManager.Translate("Server.ServerInfo.FailedToLoadItem", I18NManager.Translate("Word.Config")), e);
            throw;
        }

        // Load the language
        Logger.Info(I18NManager.Translate("Server.ServerInfo.LoadingItem", I18NManager.Translate("Word.Language")));
        try
        {
            I18NManager.LoadLanguage();
        }
        catch (Exception e)
        {
            Logger.Error(
                I18NManager.Translate("Server.ServerInfo.FailedToLoadItem", I18NManager.Translate("Word.Language")), e);
            throw;
        }
    }

    public static async Task InitDatabase()
    {
        // Propagate failures instead of waiting forever on a flag from a faulted task.
        await Task.Run(DatabaseHelper.Initialize);
    }

    public static async Task InitSdkServer()
    {
        await SdkServer.SdkServer.Start([]);
        Logger.Info(I18NManager.Translate("Server.ServerInfo.ServerRunning", I18NManager.Translate("Word.Dispatch"),
            ConfigManager.Config.HttpServer.GetDisplayAddress()));

        //KcpListener.BaseConnection = typeof(Connection);
        //KcpListener.StartListener();
        SocketListener.StartListener<Connection>();

        await Task.CompletedTask;
    }

    public static void InitPacket()
    {
        // get opcode from CmdIds
        var opcodes = typeof(CmdIds).GetFields().Where(x => x.FieldType == typeof(int)).ToList();
        foreach (var opcode in opcodes)
        {
            var name = opcode.Name;
            var value = (int)opcode.GetValue(null)!;
            SocketConnection.LogMap.TryAdd(value, name);
        }

        HandlerManager.Init();
        CallGSRouter.Init();
    }

    public static async Task InitResource()
    {
        // Init custom files
        Logger.Info(I18NManager.Translate("Server.ServerInfo.GeneratingItem", I18NManager.Translate("Word.CustomData")));
        try
        {
            await AssemblyGenerater.LoadCustomData(Assembly.GetExecutingAssembly());
        }
        catch (Exception e)
        {
            Logger.Error(
                I18NManager.Translate("Server.ServerInfo.FailedToLoadItem", I18NManager.Translate("Word.CustomData")), e);
            throw;
        }

        // Load the game data
        try
        {
            await UpdateService.EnsureResourcesPresentAsync();
            Logger.Info(I18NManager.Translate("Server.ServerInfo.LoadingItem", I18NManager.Translate("Word.GameData")));
            ResourceManager.LoadGameData();
        }
        catch (Exception e)
        {
            Logger.Error(
                I18NManager.Translate("Server.ServerInfo.FailedToLoadItem", I18NManager.Translate("Word.GameData")), e);
            throw;
        }
    }

    public static void InitCommandServices()
    {
        // Register the command handlers
        try
        {
            CommandManager.RegisterCommands();
        }
        catch (Exception e)
        {
            Logger.Error(
                I18NManager.Translate("Server.ServerInfo.FailedToInitializeItem",
                    I18NManager.Translate("Word.Command")), e);
            throw;
        }
        IConsole.OnConsoleExcuteCommand += CommandExecutor.ConsoleExcuteCommand;
        CommandExecutor.OnRunCommand += (sender, e) => { _ = CommandManager.HandleCommand(e, sender); };
        InGameConsoleBridge.ExecuteCommandAsync = InGameConsoleCommandService.ExecuteAsync;

    }

    public static Task InitCommand(CancellationToken exitToken) => IConsole.ListenConsole(exitToken);
}

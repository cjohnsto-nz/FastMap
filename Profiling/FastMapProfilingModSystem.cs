using FastMap.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace FastMap.Profiling;

public sealed class FastMapProfilingModSystem : ModSystem
{
    private FastMapConfig config = new();

    public override double ExecuteOrder() => 0.05;

    public override void Start(ICoreAPI api)
    {
        config = FastMapConfig.Load(api);
        FastMapHarmonyPatches.Install(api.Logger);
        if (config.EnableProfiling || config.AutoStartProfilingOnStartup)
        {
            FastMapProfileRecorder.Start(api, config);
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        RegisterCommands(api);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        RegisterCommands(api);
    }

    public override void Dispose()
    {
        FastMapProfileRecorder.DisposeAll();
    }

    private void RegisterCommands(ICoreAPI api)
    {
        api.ChatCommands.Create("fastmapprofile")
            .WithDescription("FastMap profiling tools")
            .RequiresPrivilege(api.Side == EnumAppSide.Server ? Privilege.controlserver : Privilege.chat)
            .BeginSubCommand("start")
                .WithDescription("Start writing FastMap profiling CSV output for this side")
                .HandleWith(_ =>
                {
                    string path = FastMapProfileRecorder.Start(api, config);
                    return TextCommandResult.Success("FastMap profiling started. Output: " + path);
                })
            .EndSubCommand()
            .BeginSubCommand("stop")
                .WithDescription("Stop FastMap profiling for this side")
                .HandleWith(_ => TextCommandResult.Success(FastMapProfileRecorder.Stop(api.Side)))
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Show FastMap profiling output status for this side")
                .HandleWith(_ => TextCommandResult.Success(FastMapProfileRecorder.Status(api.Side)))
            .EndSubCommand()
            .BeginSubCommand("flush")
                .WithDescription("Flush pending FastMap profiling rows to disk")
                .HandleWith(_ => TextCommandResult.Success(FastMapProfileRecorder.Flush(api.Side)))
            .EndSubCommand();
    }
}

#if FASTMAPPROFILING
using FastMap.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace FastMap.Profiling;

public sealed class FastMapProfilingModSystem : ModSystem
{
    private FastMapConfig config = new();

    public override double ExecuteOrder() => 0.05;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void Start(ICoreAPI api)
    {
        config = FastMapConfig.Load(api);
        if (config.EnableProfiling || config.AutoStartProfilingOnStartup)
        {
            StartProfiling(api);
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        RegisterCommands(api);
    }

    public override void Dispose()
    {
        FastMapProfileRecorder.DisposeAll();
    }

    private string StartProfiling(ICoreAPI api)
    {
        FastMapHarmonyPatches.InstallClient(api.Logger);
        return FastMapProfileRecorder.Start(api, config);
    }

    private void RegisterCommands(ICoreAPI api)
    {
        api.ChatCommands.Create("fastmapprofile")
            .WithDescription("FastMap profiling tools")
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("start")
                .WithDescription("Start writing FastMap profiling CSV output for this side")
                .HandleWith(_ =>
                {
                    string path = StartProfiling(api);
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
#endif

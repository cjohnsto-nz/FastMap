using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace FastMap.Profiling;

internal static class FastMapHarmonyPatches
{
    private const string HarmonyId = "chrisunfocused.fastmap.profiling";
    private static readonly object Sync = new();
    private static bool installed;

    public static void Install(ILogger logger)
    {
        lock (Sync)
        {
            if (installed)
            {
                return;
            }

            Harmony harmony = new(HarmonyId);
            Patch(logger, harmony, "Vintagestory.Server.ServerMain", "LoadChunkColumn", typeof(ServerMainLoadChunkColumnPatch), parameterCount: 3);
            Patch(logger, harmony, "Vintagestory.Server.ServerMain", "LoadChunkColumnFast", typeof(ServerMainLoadChunkColumnFastPatch), parameterCount: 3);
            Patch(logger, harmony, "Vintagestory.Server.ServerSystemSupplyChunks", "loadOrGenerateChunkColumn_OnChunkThread", typeof(ServerSupplyStepPatch));
            Patch(logger, harmony, "Vintagestory.Server.ServerSystemSupplyChunks", "TryLoadChunkColumn", typeof(ServerTryLoadColumnPatch));
            Patch(logger, harmony, "Vintagestory.Server.ServerSystemSupplyChunks", "GenerateNewChunkColumn", typeof(ServerGenerateNewColumnPatch));
            Patch(logger, harmony, "Vintagestory.Server.ServerSystemSupplyChunks", "PopulateChunk", typeof(ServerPopulateChunkPatch));
            Patch(logger, harmony, "Vintagestory.Server.ServerSystemSupplyChunks", "mainThreadLoadChunkColumn", typeof(ServerMainThreadLoadColumnPatch));
            Patch(logger, harmony, "Vintagestory.Server.ServerSystemSendChunks", "collectChunk", typeof(ServerCollectChunkPatch));
            Patch(logger, harmony, "Vintagestory.Client.NoObf.ClientWorldMap", "LoadChunkFromPacket", typeof(ClientLoadChunkPacketPatch));
            installed = true;
        }
    }

    private static void Patch(ILogger logger, Harmony harmony, string typeName, string methodName, Type patchType, int? parameterCount = null)
    {
        Type? targetType = AccessTools.TypeByName(typeName);
        if (targetType == null)
        {
            return;
        }

        MethodInfo? targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == methodName && (parameterCount == null || method.GetParameters().Length == parameterCount.Value));
        if (targetMethod == null)
        {
            logger.Warning("[FastMap] Profiling patch target not found: {0}.{1}", typeName, methodName);
            return;
        }

        try
        {
            HarmonyMethod? prefix = PatchMethod(patchType, "Prefix");
            HarmonyMethod? postfix = PatchMethod(patchType, "Postfix");
            harmony.Patch(targetMethod, prefix, postfix);
            logger.Notification("[FastMap] Profiling patch installed: {0}.{1}", typeName, methodName);
        }
        catch (Exception ex)
        {
            logger.Error("[FastMap] Profiling patch failed for {0}.{1}: {2}", typeName, methodName, ex);
        }
    }

    private static HarmonyMethod? PatchMethod(Type patchType, string name)
    {
        MethodInfo? method = patchType.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return method == null ? null : new HarmonyMethod(method);
    }

    private static ChunkProfileInfo ChunkRequestInfo(object? chunkRequest)
    {
        if (chunkRequest == null)
        {
            return default;
        }

        return new ChunkProfileInfo(
            IntMember(chunkRequest, "chunkX"),
            IntMember(chunkRequest, "dimension"),
            IntMember(chunkRequest, "chunkZ"),
            PassDetail(chunkRequest));
    }

    private static ChunkProfileInfo PacketChunkInfo(object? packet)
    {
        if (packet == null)
        {
            return default;
        }

        return new ChunkProfileInfo(
            IntMember(packet, "X"),
            IntMember(packet, "Y"),
            IntMember(packet, "Z"),
            string.Empty,
            PacketBytes(packet));
    }

    private static int IntMember(object target, string name)
    {
        Type type = target.GetType();
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.GetValue(target) is int fieldValue)
        {
            return fieldValue;
        }

        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(target) is int propertyValue)
        {
            return propertyValue;
        }

        return 0;
    }

    private static string PassDetail(object chunkRequest)
    {
        int pass = IntMember(chunkRequest, "CurrentIncompletePass_AsInt");
        return pass == 0 ? string.Empty : "pass=" + pass;
    }

    private static long PacketBytes(object packet)
    {
        long total = 0;
        total += ByteArrayLength(packet, "Blocks");
        total += ByteArrayLength(packet, "Light");
        total += ByteArrayLength(packet, "LightSat");
        total += ByteArrayLength(packet, "Liquids");
        return total;
    }

    private static long ByteArrayLength(object target, string name)
    {
        Type type = target.GetType();
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(target) is byte[] bytes ? bytes.LongLength : 0;
    }

    private readonly record struct ChunkProfileInfo(int X, int Y, int Z, string Detail = "", long Bytes = 0);

    private static class ServerMainLoadChunkColumnFastPatch
    {
        public static void Prefix(object[] __args)
        {
            int x = __args.Length > 0 && __args[0] is int ix ? ix : 0;
            int z = __args.Length > 1 && __args[1] is int iz ? iz : 0;
            FastMapProfileRecorder.RecordServer("server_enqueue_fast_column", x, 0, z);
        }
    }

    private static class ServerMainLoadChunkColumnPatch
    {
        public static void Prefix(object[] __args)
        {
            int x = __args.Length > 0 && __args[0] is int ix ? ix : 0;
            int z = __args.Length > 1 && __args[1] is int iz ? iz : 0;
            bool keepLoaded = __args.Length > 2 && __args[2] is bool value && value;
            FastMapProfileRecorder.RecordServer("server_enqueue_slow_column", x, 0, z, detail: keepLoaded ? "keepLoaded" : string.Empty);
        }
    }

    private static class ServerSupplyStepPatch
    {
        public static void Prefix(object[] __args, out long __state)
        {
            __state = FastMapProfileRecorder.Timestamp();
        }

        public static void Postfix(object[] __args, long __state)
        {
            ChunkProfileInfo info = ChunkRequestInfo(__args.Length > 0 ? __args[0] : null);
            FastMapProfileRecorder.RecordServer("server_supply_column_step", info.X, info.Y, info.Z, FastMapProfileRecorder.ElapsedMilliseconds(__state), detail: info.Detail);
        }
    }

    private static class ServerTryLoadColumnPatch
    {
        public static void Prefix(out long __state)
        {
            __state = FastMapProfileRecorder.Timestamp();
        }

        public static void Postfix(object[] __args, object? __result, long __state)
        {
            ChunkProfileInfo info = ChunkRequestInfo(__args.Length > 0 ? __args[0] : null);
            bool loaded = __result is Array loadedChunks && loadedChunks.Length > 0;
            string detail = string.IsNullOrEmpty(info.Detail) ? "loaded=" + loaded : info.Detail + ";loaded=" + loaded;
            FastMapProfileRecorder.RecordServer("server_try_load_column", info.X, info.Y, info.Z, FastMapProfileRecorder.ElapsedMilliseconds(__state), detail: detail);
        }
    }

    private static class ServerGenerateNewColumnPatch
    {
        public static void Prefix(out long __state)
        {
            __state = FastMapProfileRecorder.Timestamp();
        }

        public static void Postfix(object[] __args, long __state)
        {
            ChunkProfileInfo info = ChunkRequestInfo(__args.Length > 1 ? __args[1] : null);
            FastMapProfileRecorder.RecordServer("server_generate_empty_column", info.X, info.Y, info.Z, FastMapProfileRecorder.ElapsedMilliseconds(__state), detail: info.Detail);
        }
    }

    private static class ServerPopulateChunkPatch
    {
        public static void Prefix(object[] __args, out TimedChunkProfileInfo __state)
        {
            __state = new TimedChunkProfileInfo(ChunkRequestInfo(__args.Length > 0 ? __args[0] : null), FastMapProfileRecorder.Timestamp());
        }

        public static void Postfix(TimedChunkProfileInfo __state)
        {
            FastMapProfileRecorder.RecordServer(
                "server_populate_worldgen_pass",
                __state.Info.X,
                __state.Info.Y,
                __state.Info.Z,
                FastMapProfileRecorder.ElapsedMilliseconds(__state.StartTimestamp),
                detail: __state.Info.Detail);
        }
    }

    private static class ServerMainThreadLoadColumnPatch
    {
        public static void Prefix(out long __state)
        {
            __state = FastMapProfileRecorder.Timestamp();
        }

        public static void Postfix(object[] __args, long __state)
        {
            ChunkProfileInfo info = ChunkRequestInfo(__args.Length > 0 ? __args[0] : null);
            FastMapProfileRecorder.RecordServer("server_mainthread_load_column", info.X, info.Y, info.Z, FastMapProfileRecorder.ElapsedMilliseconds(__state), detail: info.Detail);
        }
    }

    private static class ServerCollectChunkPatch
    {
        public static void Prefix(out long __state)
        {
            __state = FastMapProfileRecorder.Timestamp();
        }

        public static void Postfix(object __result, long __state)
        {
            ChunkProfileInfo info = PacketChunkInfo(__result);
            FastMapProfileRecorder.RecordServer("server_chunk_to_packet", info.X, info.Y, info.Z, FastMapProfileRecorder.ElapsedMilliseconds(__state), info.Bytes, info.Detail);
        }
    }

    private static class ClientLoadChunkPacketPatch
    {
        public static void Prefix(out long __state)
        {
            __state = FastMapProfileRecorder.Timestamp();
        }

        public static void Postfix(object[] __args, long __state)
        {
            ChunkProfileInfo info = PacketChunkInfo(__args.Length > 0 ? __args[0] : null);
            FastMapProfileRecorder.RecordClient("client_load_chunk_packet", info.X, info.Y, info.Z, FastMapProfileRecorder.ElapsedMilliseconds(__state), info.Bytes);
        }
    }

    private readonly record struct TimedChunkProfileInfo(ChunkProfileInfo Info, long StartTimestamp);
}

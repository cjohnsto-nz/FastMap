#if FASTMAPPROFILING
using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace FastMap.Profiling;

internal static class FastMapWorldgenDelegateProfiler
{
    public static ChunkColumnGenerationDelegate Wrap(ChunkColumnGenerationDelegate handler, EnumWorldGenPass pass, string worldType)
    {
        string handlerName = HandlerName(handler);
        string detail = "worldType=" + worldType + ";pass=" + (int)pass + ":" + pass + ";handler=" + handlerName;

        return request =>
        {
            if (!FastMapProfileRecorder.ServerEnabled)
            {
                handler(request);
                return;
            }

            long start = FastMapProfileRecorder.Timestamp();
            try
            {
                handler(request);
            }
            finally
            {
                FastMapProfileRecorder.RecordServer(
                    "server_worldgen_delegate",
                    request.ChunkX,
                    0,
                    request.ChunkZ,
                    FastMapProfileRecorder.ElapsedMilliseconds(start),
                    detail: detail,
                    category: "worldgen_delegate");
            }
        };
    }

    private static string HandlerName(Delegate handler)
    {
        Type? declaringType = handler.Method.DeclaringType;
        string typeName = declaringType?.FullName ?? "<unknown>";
        return typeName + "." + handler.Method.Name;
    }
}
#endif

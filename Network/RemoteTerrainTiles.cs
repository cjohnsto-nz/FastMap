using System;
using System.Collections.Generic;
using System.Linq;

namespace FastMap.Network;

internal sealed class RemoteTerrainTiles : IDisposable
{
    private readonly object sync=new();
    private readonly Dictionary<(int,int,int,int), Tile> tiles=new();
    private readonly Queue<TerrainTileRequest> outgoing=new();
    private readonly Func<long> clock;
    private int nextId;
    private bool available;
    private long sent,acknowledged,completed,wireBytes;
    internal const long MemoryLimit=32L*1024*1024;
    public string Diagnostics { get { lock(sync) return $"sent={sent} acked={acknowledged} completed={completed} outgoing={outgoing.Count} pending={tiles.Values.Count(t=>!t.Complete)} ready={tiles.Values.Count(t=>t.Ready)} bytes={PoolBytes} wireBytes={wireBytes}"; } }
    public long PoolBytes { get { lock(sync) return tiles.Values.Sum(t=>(long)(t.Data?.Length??0)+256); } }
    public RemoteTerrainTiles(Func<long> clock)=>this.clock=clock;
    public void SetAvailable(bool value) { lock(sync) { available=value; if(!value){tiles.Clear();outgoing.Clear();} } }
    public int[] Get(int x,int z,int step,int style)
    {
        lock(sync)
        {
            if(!available) throw new InvalidOperationException("Server terrain tiles unavailable");
            var key=(x,z,step,style);
            if(tiles.TryGetValue(key,out Tile? tile))
            {
                tile.Used=clock();
                if(tile.Error!=null){tiles.Remove(key);throw new InvalidOperationException(tile.Error);}
                if(tile.Ready)
                {
                    int pixels=tile.Request.Width*tile.Request.Width;
                    if(!MakeRoom(pixels*8L,tile)) throw new TerrainSamplesPendingException();
                    try
                    {
                        var result=new EncodedTerrainTile(tile.Data!,tile.Compressed).Decode(pixels);
                        tile.Consumed=true;
                        return result;
                    }
                    catch(Exception ex) when(ex is System.IO.InvalidDataException || ex is ArgumentException)
                    {Cancel(tile,"Invalid terrain tile pixels");throw new InvalidOperationException(tile.Error,ex);}
                }
                throw new TerrainSamplesPendingException();
            }
            var request=new TerrainTileRequest {Id=++nextId,PageX=x,PageZ=z,Step=step,Style=style};
            if(!request.IsValid(int.MaxValue,int.MaxValue)) throw new ArgumentOutOfRangeException(nameof(step));
            // Queued pages reserve metadata, not a pixel buffer or a generation slot.
            while(tiles.Count>=2048)
            {
                var oldest=tiles.Where(t=>t.Value.Complete).OrderBy(t=>t.Value.Used).FirstOrDefault();
                if(oldest.Value==null) throw new TerrainSamplesPendingException();
                tiles.Remove(oldest.Key);
            }
            if(!MakeRoom(256,null))throw new TerrainSamplesPendingException();
            tiles.Add(key,new Tile(request,clock())); outgoing.Enqueue(request);
            throw new TerrainSamplesPendingException();
        }
    }
    public void Pump(Action<TerrainTileRequest> send)
    {
        lock(sync)
        {
            if(!available)return;
            foreach(var tile in tiles.Values.Where(t=>!t.Complete))
                if(clock()-tile.Used>15000 || clock()-tile.Progress>60000) Cancel(tile,"Terrain tile timed out");
            for(int i=0;i<64&&outgoing.TryDequeue(out var request);i++)
            {send(request);if(!request.Cancel)sent++;}
        }
    }
    private void Cancel(Tile tile,string error)
    {tile.Error=error;tile.Data=null;outgoing.Enqueue(new TerrainTileRequest{Id=tile.Request.Id,Cancel=true});}
    private bool MakeRoom(long bytes,Tile? keep)
    {
        while(PoolBytes+bytes>MemoryLimit)
        {
            var oldest=tiles.Where(t=>t.Value!=keep&&t.Value.Complete).OrderByDescending(t=>t.Value.Consumed).ThenBy(t=>t.Value.Used).FirstOrDefault();
            if(oldest.Value==null)return false;
            tiles.Remove(oldest.Key);
        }
        return true;
    }
    public void Receive(TerrainTileBatch batch)
    {
        lock(sync)
        {
            var tile=tiles.Values.FirstOrDefault(t=>t.Request.Id==batch.Id);
            if(tile==null || tile.Complete)return;
            if(batch.Queued)
            {
                if(batch.Data.Length!=0||batch.Offset!=0||batch.TotalBytes!=0||batch.NextUpdateMilliseconds<1||batch.NextUpdateMilliseconds>30000)
                {Cancel(tile,"Invalid terrain tile acknowledgement");return;}
                tile.Progress=clock();acknowledged++;return;
            }
            if(batch.CacheMiss)
            {
                if(!tile.Request.CachedOnly || tile.Received!=0 || batch.Data.Length!=0)
                {Cancel(tile,"Invalid terrain tile cache miss");return;}
                var key=(tile.Request.PageX,tile.Request.PageZ,tile.Request.Step,tile.Request.Style);
                tiles.Remove(key);
                return;
            }
            if(!string.IsNullOrEmpty(batch.Error)){Cancel(tile,batch.Error);return;}
            int rawBytes=tile.Request.Width*tile.Request.Width*4;
            if(batch.TotalBytes<1 || batch.TotalBytes>rawBytes || batch.Data.Length<1 || batch.Data.Length>EncodedTerrainTile.FragmentBytes
                || batch.Offset!=tile.Received || batch.Data.Length>batch.TotalBytes-tile.Received
                || (tile.Data!=null && (tile.Data.Length!=batch.TotalBytes || tile.Compressed!=batch.Compressed)))
            {Cancel(tile,"Invalid terrain tile fragment");return;}
            if(tile.Data==null)
            {
                if(!MakeRoom(batch.TotalBytes,tile)){Cancel(tile,"Terrain tile memory budget exceeded");return;}
                tile.Data=new byte[batch.TotalBytes];
            }
            tile.Compressed=batch.Compressed;
            batch.Data.CopyTo(tile.Data,tile.Received);tile.Received+=batch.Data.Length;tile.Progress=clock();
            wireBytes+=batch.Data.Length;
            if(tile.Received==tile.Data.Length)
            {
                // Keep the wire representation until the map worker consumes it.
                // Expanding a cached burst here evicts tiles before they can render.
                tile.Ready=true;completed++;
            }
        }
    }
    public void Dispose()=>SetAvailable(false);
    private sealed class Tile
    {
        public readonly TerrainTileRequest Request;
        public long Used,Progress;
        public byte[]? Data;
        public bool Ready,Consumed;
        public string? Error;
        public int Received;
        public bool Compressed;
        public bool Complete=>Error!=null || Ready;
        public Tile(TerrainTileRequest request,long now){Request=request;Used=Progress=now;}
    }
}

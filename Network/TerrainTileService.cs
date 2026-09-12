using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastMap.Map;

namespace FastMap.Network;

// Server-owned pixel cache. Prewarming has no subscriber and therefore sends no network data.
internal sealed class TerrainTileService : IDisposable
{
    private readonly Dictionary<(int,int,int), Entry> cache=new();
    private readonly List<Subscription> subscriptions=new();
    private readonly List<TerrainTileRequest> targets=new();
    private readonly Func<string,bool> allowed;
    private readonly Func<int,int,FastMapTerrainSamplerColumn> sample;
    private readonly Func<TerrainTileRequest,FastMapTerrainSamplerColumn[],int,int[]> render;
    private readonly Action<string,TerrainTileBatch> send;
    private readonly Action<string>? log;
    private readonly int sizeX,sizeZ,transferLimit;
    private readonly long limit;
    private readonly Func<long> clock;
    private readonly BlockingCollection<Build>? queue;
    private readonly Thread? thread;
    private Build? work;
    private long serial;
    private int cursor;
    private volatile int delay;
    private bool paused,disposed;
    public long Samples {get;private set;}
    public long WireBytes {get;private set;}
    public long CacheHits {get;private set;}
    public long Prewarmed {get;private set;}
    public long PoolBytes=>cache.Values.Sum(e=>e.Bytes)+(work?.Reservation??0);
    public int CachedPages=>cache.Count;
    public int PendingRequests=>subscriptions.Count;
    public int PendingPrewarm=>targets.Count(t=>!cache.ContainsKey(Key(t)));
    public bool IsWorking=>work!=null;
    public bool Background=>thread!=null;
    public TerrainTileService(int sizeX,int sizeZ,Func<string,bool> allowed,
        Func<int,int,FastMapTerrainSamplerColumn> sample,Func<TerrainTileRequest,FastMapTerrainSamplerColumn[],int,int[]> render,
        Action<string,TerrainTileBatch> send,int cacheMegabytes=64,int transferKilobytes=256,bool background=true,Action<string>? log=null,Func<long>? clock=null)
    {
        this.sizeX=sizeX;this.sizeZ=sizeZ;this.allowed=allowed;this.sample=sample;this.render=render;this.send=send;this.log=log;
        this.clock=clock??(()=>Environment.TickCount64);
        limit=Math.Clamp(cacheMegabytes,1,256)*1024L*1024;transferLimit=Math.Clamp(transferKilobytes,32,1024)*1024;
        if(background)
        {
            queue=new BlockingCollection<Build>(1);
            thread=new Thread(()=>{foreach(var build in queue.GetConsumingEnumerable())Run(build);})
                {IsBackground=true,Name="FastMap terrain tiles"};
            thread.Start();
        }
    }
    private static (int,int,int) Key(TerrainTileRequest r)=>(r.PageX,r.PageZ,r.Step);
    public void Request(string owner,TerrainTileRequest request)
    {
        if(disposed)return;
        if(request.Cancel){Remove(owner,request.Id);return;}
        if(!allowed(owner)||!request.IsValid(sizeX,sizeZ)){Fail(owner,request.Id,"Terrain tile request denied or invalid");return;}
        if(subscriptions.Any(s=>s.Owner==owner&&s.Request.Id==request.Id))return;
        bool hit=cache.TryGetValue(Key(request),out var entry);
        // A cache lookup must never enqueue sampling or wait behind a cold request.
        if(request.CachedOnly&&!hit){send(owner,new TerrainTileBatch{Id=request.Id,CacheMiss=true});return;}
        if(subscriptions.Count>=4096||subscriptions.Count(s=>s.Owner==owner)>=2048)
        {Fail(owner,request.Id,"Terrain tile server busy");return;}
        if(hit){CacheHits++;entry!.Used=++serial;entry.Demanded=true;}
        var subscription=new Subscription(owner,request,hit);
        subscriptions.Add(subscription);
        Acknowledge(subscription);
    }
    private void Acknowledge(Subscription sub)
    {
        sub.LastAcknowledgement=clock();
        send(sub.Owner,new TerrainTileBatch{Id=sub.Request.Id,Queued=true,NextUpdateMilliseconds=5000});
    }
    public void Remove(string owner,int? id=null)
    {subscriptions.RemoveAll(s=>s.Owner==owner&&(!id.HasValue||s.Request.Id==id));CancelUnneeded();}
    public void SetPrewarmTargets(IEnumerable<TerrainSamplingRequest> grids)
    {
        if(disposed)return;
        targets.Clear();var seen=new HashSet<(int,int,int)>();
        foreach(var g in grids)
        {
            var r=new TerrainTileRequest{Id=1,PageX=(g.X+g.Step)/1024,PageZ=(g.Z+g.Step)/1024,Step=g.Step};
            if(!r.IsValid(sizeX,sizeZ)||!seen.Add(Key(r)))continue;
            if(targets.Count>=1024)break;
            // Targets only reserve coordinates. MakeRoom bounds actual compressed
            // tiles and the active build, and stops prewarming when the pool fills.
            targets.Add(r);
        }
        CancelUnneeded();
    }
    public void UpdateLoad(double cpu,long interval)
    {paused=cpu>=70||interval>100;delay=cpu>=75?20:interval>100?10:0;}
    private void CancelUnneeded()
    {
        if(work!=null && !subscriptions.Any(s=>Key(s.Request)==Key(work.Request))&&!targets.Any(t=>Key(t)==Key(work.Request)))
            work.Cancel.Cancel();
    }
    private bool MakeRoom(long needed,bool demand)
    {
        while(cache.Count>=2048||PoolBytes+needed>limit)
        {
            var old=cache.Where(e=>!subscriptions.Any(s=>Key(s.Request)==e.Key)
                && (demand||(!e.Value.Demanded&&!targets.Any(t=>Key(t)==e.Key))))
                .OrderBy(e=>e.Value.Used).FirstOrDefault();
            if(old.Value==null)return false;
            cache.Remove(old.Key);
        }
        return true;
    }
    public void Tick(double budgetMilliseconds,int maxSamples)
    {
        if(disposed)return;
        var timer=Stopwatch.StartNew();
        foreach(var sub in subscriptions.Where(s=>!allowed(s.Owner)).ToArray())
        {Fail(sub.Owner,sub.Request.Id,"Terrain tile permission revoked");subscriptions.Remove(sub);}
        foreach(var sub in subscriptions)
            if(sub.Offset==0&&clock()-sub.LastAcknowledgement>=5000)Acknowledge(sub);
        CancelUnneeded();
        if(work!=null&&work.Completion.Task.IsCompleted)
        {
            var done=work;work=null;
            try
            {
                var result=done.Completion.Task.GetAwaiter().GetResult();Samples+=done.Offset;
                bool wanted=subscriptions.Any(s=>Key(s.Request)==Key(done.Request));
                cache[Key(done.Request)]=new Entry(result,++serial,wanted);
                if(done.Prewarm)Prewarmed++;
                log?.Invoke($"tile built page={done.Request.PageX},{done.Request.PageZ} step={done.Request.Step} prewarm={done.Prewarm} samples={done.Offset} elapsedMs={done.Timer.ElapsedMilliseconds} bytes={result.Sum(t=>t.Data.Length)} subscribers={subscriptions.Count(s=>Key(s.Request)==Key(done.Request))}");
            }
            catch(OperationCanceledException){}
            catch(Exception ex)
            {
                foreach(var sub in subscriptions.Where(s=>Key(s.Request)==Key(done.Request)).ToArray())
                {Fail(sub.Owner,sub.Request.Id,"Terrain tile generation failed");subscriptions.Remove(sub);}
                targets.RemoveAll(t=>Key(t)==Key(done.Request));
                log?.Invoke($"tile generation failed page={done.Request.PageX},{done.Request.PageZ}: {ex}");
            }
            done.Cancel.Dispose();
        }
        int bytes=0,packets=0,idle=0;
        while(subscriptions.Count>0&&packets<32&&timer.Elapsed.TotalMilliseconds<Math.Clamp(budgetMilliseconds,1,20))
        {
            cursor%=subscriptions.Count;var sub=subscriptions[cursor];
            if(!cache.TryGetValue(Key(sub.Request),out var entry)){if(++idle>=subscriptions.Count)break;cursor++;continue;}
            var tile=entry.Tiles[sub.Request.Style];
            int count=Math.Min(EncodedTerrainTile.FragmentBytes,tile.Data.Length-sub.Offset);
            if(bytes+count>transferLimit)break;
            send(sub.Owner,new TerrainTileBatch{Id=sub.Request.Id,Offset=sub.Offset,TotalBytes=tile.Data.Length,
                Compressed=tile.Compressed,Data=tile.Data.AsSpan(sub.Offset,count).ToArray()});
            idle=0;sub.Offset+=count;bytes+=count;packets++;WireBytes+=count;entry.Used=++serial;entry.Demanded=true;
            if(sub.Offset==tile.Data.Length)
            {
                log?.Invoke($"tile sent page={sub.Request.PageX},{sub.Request.PageZ} style={sub.Request.Style} cache={sub.Hit} elapsedMs={sub.Timer.ElapsedMilliseconds} wireBytes={sub.Offset}");
                subscriptions.RemoveAt(cursor);
            }
            // Finish a tile before interleaving another large payload: queued requests only hold metadata.
        }
        if(work==null)
        {
            var request=subscriptions.FirstOrDefault(s=>!cache.ContainsKey(Key(s.Request)))?.Request;
            bool prewarm=request==null;
            request??=paused?null:targets.FirstOrDefault(t=>!cache.ContainsKey(Key(t)));
            if(request!=null)
            {
                long needed=(long)(request.Width+1)*(request.Width+1)*32+(long)request.Width*request.Width*24+131072;
                if(MakeRoom(needed,!prewarm))
                {
                    work=new Build(request,needed,prewarm);
                    if(queue!=null)queue.Add(work);
                }
                else if(!prewarm)
                {
                    var blocked=subscriptions.First(s=>Key(s.Request)==Key(request));
                    Fail(blocked.Owner,blocked.Request.Id,"Terrain tile cache busy");subscriptions.Remove(blocked);
                }
            }
        }
        if(work!=null && queue==null)
        {
            try {Advance(work,Math.Clamp(maxSamples,1,65536),timer,Math.Clamp(budgetMilliseconds,1,20));}
            catch(OperationCanceledException){work.Completion.TrySetCanceled();}
            catch(Exception ex){work.Completion.TrySetException(ex);}
        }
    }
    private void Run(Build build)
    {
        try {Advance(build,int.MaxValue,Stopwatch.StartNew(),double.PositiveInfinity);}
        catch(OperationCanceledException){build.Completion.TrySetCanceled();}
        catch(Exception ex){build.Completion.TrySetException(ex);}
    }
    private void Advance(Build build,int maxSamples,Stopwatch timer,double budget)
    {
        var r=build.Request;int width=r.Width+1;
        while(build.Offset<build.Grid.Length && maxSamples-->0 && timer.Elapsed.TotalMilliseconds<budget)
        {
            build.Cancel.Token.ThrowIfCancellationRequested();
            int i=build.Offset;
            int x=Math.Clamp(r.PageX*1024-r.Step+i%width*r.Step,0,sizeX-1);
            int z=Math.Clamp(r.PageZ*1024-r.Step+i/width*r.Step,0,sizeZ-1);
            build.Grid[build.Offset++]=sample(x,z);
            if(queue!=null && (build.Offset&1023)==0 && delay>0 && build.Cancel.Token.WaitHandle.WaitOne(delay))
                build.Cancel.Token.ThrowIfCancellationRequested();
        }
        if(build.Offset==build.Grid.Length)
        {
            var tiles=new EncodedTerrainTile[3];
            for(int style=0;style<3;style++)
            {build.Cancel.Token.ThrowIfCancellationRequested();tiles[style]=EncodedTerrainTile.Encode(render(r,build.Grid,style));}
            build.Completion.TrySetResult(tiles);
        }
    }
    private void Fail(string owner,int id,string error)=>send(owner,new TerrainTileBatch{Id=id,Error=error});
    public void Dispose()
    {
        if(disposed)return;disposed=true;work?.Cancel.Cancel();queue?.CompleteAdding();thread?.Join();
        if(work?.Completion.Task.IsFaulted==true)_=work.Completion.Task.Exception;
        work?.Cancel.Dispose();work=null;queue?.Dispose();cache.Clear();subscriptions.Clear();targets.Clear();
    }
    private sealed class Entry
    {
        public readonly EncodedTerrainTile[] Tiles;public long Used;public bool Demanded;
        public long Bytes=>Tiles.Sum(t=>(long)t.Data.Length+128)+256;
        public Entry(EncodedTerrainTile[] tiles,long used,bool demanded){Tiles=tiles;Used=used;Demanded=demanded;}
    }
    private sealed class Subscription
    {
        public readonly string Owner;public readonly TerrainTileRequest Request;public readonly bool Hit;
        public readonly Stopwatch Timer=Stopwatch.StartNew();public int Offset;public long LastAcknowledgement;
        public Subscription(string owner,TerrainTileRequest request,bool hit){Owner=owner;Request=request;Hit=hit;}
    }
    private sealed class Build
    {
        public readonly TerrainTileRequest Request;public readonly long Reservation;public readonly bool Prewarm;
        public readonly FastMapTerrainSamplerColumn[] Grid;public int Offset;
        public readonly Stopwatch Timer=Stopwatch.StartNew();public readonly CancellationTokenSource Cancel=new();
        public readonly TaskCompletionSource<EncodedTerrainTile[]> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Build(TerrainTileRequest r,long reservation,bool prewarm)
        {Request=r;Reservation=reservation;Prewarm=prewarm;Grid=new FastMapTerrainSamplerColumn[(r.Width+1)*(r.Width+1)];}
    }
}

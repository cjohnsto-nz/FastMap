using FastMap.Map;
using FastMap.Network;
using ProtoBuf;

int assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
void Pending(Action action) { try { action(); throw new Exception("Expected pending samples."); } catch (TerrainSamplesPendingException) { assertions++; } }
void Failed(Action action) { try { action(); throw new Exception("Expected failed samples."); } catch (InvalidOperationException) { assertions++; } }
TerrainSamplingRequest Req(int id, int x = 100, int width = 4, int height = 4, int step = 4) =>
    new() { Id = id, X = x, Z = 200, Width = width, Height = height, Step = step };
void Drain(TerrainSamplingService server)
{
    for (int i = 0; i < 10000 && server.PendingRequests > 0; i++) server.Tick(20, 65536);
    Check(server.PendingRequests == 0, "All requests drained");
}

Check(Req(1).IsValid(10000,10000), "Valid grid");
Check(!Req(1,int.MaxValue).IsValid(10000,10000), "Coordinate overflow rejected");
Check(!Req(1,-100).IsValid(10000,10000), "Negative coordinate rejected");
Check(!Req(1,width:1025,step:32).IsValid(10000,10000), "Excessive grid span rejected");
Check(!Req(1,step:0).IsValid(10000,10000), "Invalid step rejected");

long now=0;
using var client = new RemoteTerrainSampler(()=>now);
client.SetAvailable(true);
var replies=new List<(string Owner,TerrainSamplingBatch Batch)>();
bool allowed=true;
int samples=0;
var server=new TerrainSamplingService(10000,10000,_=>allowed,
    (x,z)=> { samples++; return new FastMapTerrainSamplerColumn(x+z,.25f,.75f,0xabcdef,.5f,.1f); },
    (owner,batch)=>{
        var copy=Serializer.DeepClone(batch);
        replies.Add((owner,copy));
        Check(copy.Data.Length<=TerrainSamplingCodec.MaxBatchColumns*TerrainSamplingCodec.ColumnBytes,"Bounded wire packet");
        if(owner=="client") client.Receive(copy);
    });
void Pump()=>client.Pump(r=>server.Request("client",Serializer.DeepClone(r)));
Pending(()=>client.SampleGrid(96,196,257,257,4));
Pending(()=>client.SampleGrid(96,196,257,257,4));
Pump();
Check(samples==0,"Request handler never samples");
for(int i=0;i<10000 && server.PendingRequests>0;i++)
{
    int before=samples;
    server.Tick(20,4096);
    Check(samples-before<=4096,"Global sample cap");
}
Check(server.PendingRequests==0,"Full grid transfer completes");
var result=client.SampleGrid(96,196,257,257,4);
Check(result.Length==66049 && result[0].Height==292 && result[^1].Height==2340,"Full grid and border coordinates");
Check(result[0].HasClimate && result[0].Rainfall==.25f && result[0].Temperature==.75f
    && result[0].ClimateColor==0xabcdef && result[0].ForestDensity==.5f && result[0].ShrubDensity==.1f,"Lossless climate roundtrip");
Check(server.WireBytes<server.RawBytes/2,"Representative samples compress by more than 2x");
int beforeCache=samples;
server.Request("second-client",new TerrainSamplingRequest{Id=2,X=96,Z=196,Width=257,Height=257,Step=4});
Drain(server);
Check(samples==beforeCache && server.CacheHits==1,"Shared warm pool replays without sampling");
Check(server.CachedGrids==1,"Completed grid retained");
Check(replies.Where(r=>r.Owner=="second-client").Sum(r=>r.Batch.Count)==66049,"New client receives whole cached grid");

allowed=false;
server.Request("denied",Req(3));
Check(replies[^1].Batch.Error.Length>0 && samples==beforeCache,"Denied cached/new requests never sample");
allowed=true;
server.Request("revoked",Req(4));
allowed=false;
server.Tick(20,100);
Check(samples==beforeCache && replies[^1].Batch.Error.Length>0,"Permission revoked before sampling");
allowed=true;

// Same in-flight grid is sampled once, even when a later subscriber joins after a block exists.
var sharedReplies=new List<(string Owner,TerrainSamplingBatch Batch)>();
int sharedSamples=0;
var shared=new TerrainSamplingService(10000,10000,_=>true,
    (x,z)=>{sharedSamples++;return new FastMapTerrainSamplerColumn(x);},
    (o,b)=>sharedReplies.Add((o,b)));
shared.Request("a",Req(10,width:65,height:65));
shared.Tick(20,1024);
shared.Request("b",Req(11,width:65,height:65));
Check(shared.SharedRequests==1,"Late subscriber joins existing producer");
shared.Remove("a");
Drain(shared);
Check(sharedSamples==4225,"Cancellation of one subscriber preserves shared work");
Check(sharedReplies.Where(r=>r.Owner=="b").Sum(r=>r.Batch.Count)==4225,"Late joiner receives prefix too");
shared.Request("abandon",Req(12,x:400,width:65,height:65));
shared.Tick(20,128);
shared.Remove("abandon");
long abandonedSamples=shared.Samples;
Drain(shared);
Check(shared.Samples==abandonedSamples,"Unsubscribed work is discarded");

// Rotate producers within a tick, even when one grid is much larger.
var seen=new List<int>();
var fair=new TerrainSamplingService(10000,10000,_=>true,(x,z)=>{seen.Add(x);return new FastMapTerrainSamplerColumn(x);},(_,_)=>{});
fair.Request("a",Req(20,x:100,width:65,height:65));
fair.Request("b",Req(21,x:2000,width:65,height:65));
fair.Tick(20,256);
Check(seen.Any(x=>x<1000)&&seen.Any(x=>x>=2000),"Both producers get sampling time");
fair.Remove("a");fair.Remove("b");

// Subscriber bounds still hold when every request targets one shared grid.
for(int i=0;i<5;i++) server.Request("flood",Req(30+i,x:400));
Check(replies[^1].Batch.Id==34 && replies[^1].Batch.Error.Length>0,"Four subscriptions per player");
server.Remove("flood");
for(int i=0;i<8;i++) for(int j=0;j<4;j++) server.Request("owner"+i,Req(50+j,x:400));
server.Request("overflow",Req(60,x:400));
Check(replies[^1].Batch.Id==60 && replies[^1].Batch.Error.Length>0,"Global subscriber bound");
for(int i=0;i<8;i++) server.Remove("owner"+i);

// Cache eviction under a tiny memory budget, using deterministic high-entropy columns.
uint random=12345;
uint Next(){random^=random<<13;random^=random>>17;random^=random<<5;return random;}
var small=new TerrainSamplingService(10000,10000,_=>true,(x,z)=>
    new FastMapTerrainSamplerColumn((int)Next(),Next()/4294967296f,Next()/4294967296f,(int)Next(),Next()/4294967296f,Next()/4294967296f),(_,_)=>{},cacheMegabytes:1);
for(int i=0;i<4;i++){small.Request("a",Req(70+i,x:100+i*1100,width:129,height:129));Drain(small);Check(small.PoolBytes<=1024*1024,"Pool remains within memory bound");}
long beforeEviction=small.Samples;
small.Request("a",Req(80,x:100,width:129,height:129));Drain(small);
Check(small.Samples>beforeEviction,"Least recently used grid was evicted");
long beforeReplay=small.Samples;
small.Request("a",Req(81,x:100,width:129,height:129));Drain(small);
Check(small.Samples==beforeReplay,"Most recent grid is retained");

// Malformed compressed blocks cannot allocate beyond the block limit or become map pixels.
foreach(var corrupt in new[]{
    new TerrainSamplingBatch{Count=int.MaxValue,Compressed=true,Data=new byte[]{0}},
    new TerrainSamplingBatch{Count=1,Compressed=true,Data=new byte[]{255}},
    new TerrainSamplingBatch{Count=1,Data=new byte[24]},
    new TerrainSamplingBatch{Count=1,Offset=1,Data=new byte[25]}})
{
    using var bad=new RemoteTerrainSampler(()=>now);bad.SetAvailable(true);
    var sent=new List<TerrainSamplingRequest>();
    Pending(()=>bad.SampleGrid(0,0,1,1,1));bad.Pump(sent.Add);
    corrupt.Id=sent[0].Id;bad.Receive(Serializer.DeepClone(corrupt));
    Failed(()=>bad.SampleGrid(0,0,1,1,1));
}
var edgeReplies=new List<TerrainSamplingBatch>();
var edgeServer=new TerrainSamplingService(10000,10000,_=>true,(x,z)=>new FastMapTerrainSamplerColumn(x+z),(_,b)=>edgeReplies.Add(b));
edgeServer.Request("edge",new TerrainSamplingRequest{Id=90,X=-4,Z=-4,Width=2,Height=2,Step=4});Drain(edgeServer);
using(var reader=new BinaryReader(new MemoryStream(TerrainSamplingCodec.Decode(edgeReplies[0]))))
    Check(TerrainSamplingCodec.Read(reader).Height==0,"World-edge coordinates clamped");

// Progressing streams survive timeout; abandoned viewport requests cancel.
using var waiting=new RemoteTerrainSampler(()=>now);waiting.SetAvailable(true);
var waitingSent=new List<TerrainSamplingRequest>();
for(int i=0;i<5;i++)Pending(()=>waiting.SampleGrid(i,0,1,1,1));
waiting.Pump(waitingSent.Add);Check(waitingSent.Count==4,"Client pending bound");
now=31000;waiting.Pump(waitingSent.Add);
Check(waitingSent.Count(r=>r.Cancel)==4,"Stale requests cancelled");
waiting.SetAvailable(false);
Failed(()=>waiting.SampleGrid(0,0,1,1,1));

// Time and adaptive CPU/callback controls.
int slowSamples=0;
var slow=new TerrainSamplingService(10000,10000,_=>true,(x,z)=>{slowSamples++;Thread.Sleep(5);return new FastMapTerrainSamplerColumn(1);},(_,_)=>{});
slow.Request("s",Req(100));slow.Tick(1,65536);
Check(slowSamples<=1,"Slow sample cannot trigger another sample past deadline");
var adaptive=new AdaptiveSamplingBudget();
double grown=0;
for(int i=0;i<40;i++)grown=adaptive.Update(67,20,2,15,true);
Check(grown==15,"Spare capacity ramps to configured ceiling");
Check(adaptive.Update(100,20,2,15,true)<grown,"Slow callback reduces budget");
double reduced=adaptive.Update(50,95,2,15,true);
Check(reduced<grown,"High process CPU reduces budget");
Check(adaptive.Update(50,0,2,15,false)==2,"Fixed-budget mode preserved");
Console.WriteLine($"PASS {assertions} assertions; full-grid raw={66049*25} bytes, compressed={replies.Where(r=>r.Owner=="client").Sum(r=>r.Batch.Data.Length)} bytes.");

// The real producer can block without blocking permission checks or network delivery.
int mainThread = Environment.CurrentManagedThreadId;
var workerThreads = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
using var entered = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
int backgroundSamples = 0;
var backgroundReplies = new List<(string Owner, TerrainSamplingBatch Batch)>();
using var background = new TerrainSamplingService(10000, 10000,
    _ => { Check(Environment.CurrentManagedThreadId == mainThread, "Permissions stay on main thread"); return true; },
    (x,z) => {
        workerThreads.TryAdd(Environment.CurrentManagedThreadId, true);
        entered.Set();
        if (!release.Wait(5000)) throw new Exception("Test worker release timed out");
        Interlocked.Increment(ref backgroundSamples);
        return new FastMapTerrainSamplerColumn(x+z,.25f,.75f,0xabcdef,.5f,.1f);
    },
    (o,b) => { Check(Environment.CurrentManagedThreadId == mainThread, "Network stays on main thread"); backgroundReplies.Add((o,Serializer.DeepClone(b))); },
    backgroundSampling:true);
background.Request("a", Req(200, width:257, height:257));
background.Tick(2, 1);
Check(entered.Wait(5000), "Background producer started");
background.Request("b", Req(201, width:257, height:257));
background.Tick(2, 1);
Check(background.PendingRequests == 2 && backgroundSamples == 0, "Main tick returns while sampling is blocked");
background.Remove("a");
release.Set();
void DrainWorker(TerrainSamplingService service)
{
    var timer=System.Diagnostics.Stopwatch.StartNew();
    while(service.PendingRequests>0 && timer.ElapsedMilliseconds<10000) { service.Tick(20,1); Thread.Sleep(1); }
    Check(service.PendingRequests==0,"Background requests finish");
}
DrainWorker(background);
Check(workerThreads.Count==1 && !workerThreads.ContainsKey(mainThread), "One persistent sampler thread");
Check(backgroundSamples==66049 && background.SharedRequests==1, "Shared background grid sampled exactly once");
Check(backgroundReplies.Where(r=>r.Owner=="b").Sum(r=>r.Batch.Count)==66049,"Background blocks complete full resolution grid");
background.Request("c", Req(202,width:257,height:257));
DrainWorker(background);
Check(backgroundSamples==66049 && background.CacheHits==1,"Background warm cache avoids sampling");

// Cancellation retains the memory reservation until the worker stops; immediate rejoin is safe.
entered.Reset(); release.Reset();
background.Request("cancel", Req(203,x:2000,width:257,height:257));
background.Tick(20,1);
Check(entered.Wait(5000),"Cancellation worker started");
long reserved=background.PoolBytes;
background.Remove("cancel");
Check(background.PoolBytes==reserved,"Active cancellation retains memory reservation");
background.Request("rejoin", Req(204,x:2000,width:257,height:257));
release.Set();
DrainWorker(background);
Check(backgroundReplies.Where(r=>r.Owner=="rejoin").Sum(r=>r.Batch.Count)==66049,"Cancelled producer can be rejoined without missing prefix");

using var broken = new TerrainSamplingService(10000,10000,_=>true,
    (_,_)=>throw new InvalidOperationException("Sampler failed"), (_,b)=>Check(b.Error.Length>0,"Worker failure reaches client"),backgroundSampling:true);
broken.Request("failure",Req(205));DrainWorker(broken);
Check(broken.PoolBytes==0,"Failed worker frees reservation");

int shutdownSamples=0;
using var shutdownEntered = new ManualResetEventSlim();
var shutdown = new TerrainSamplingService(10000,10000,_=>true,
    (_,_)=>{Interlocked.Increment(ref shutdownSamples);shutdownEntered.Set();Thread.Sleep(1);return new FastMapTerrainSamplerColumn(1);},
    (_,_)=>throw new Exception("Shutdown must not send"),backgroundSampling:true);
shutdown.Request("shutdown",Req(206,width:257,height:257));shutdown.Tick(20,1);
Check(shutdownEntered.Wait(5000),"Shutdown worker started");
shutdown.Dispose();int stopped=shutdownSamples;Thread.Sleep(20);
Check(stopped==shutdownSamples && stopped<66049 && shutdown.PoolBytes==0,"Shutdown joins and cancels worker before sampler disposal");
shutdown.Dispose();
Console.WriteLine($"PASS {assertions} assertions including background isolation, sharing, cancellation, failure and shutdown.");

// Server prewarming operates with no clients. Only the requested pixel variant is transmitted.
var tilePlan=TerrainPrewarmPlanner.Around(new[]{(X:2050,Z:2050),(X:2050,Z:2050)},1,4,10000,10000).ToArray();
Check(tilePlan.Length==9 && tilePlan[0].X==2044 && tilePlan[0].Z==2044,"Aligned, deduplicated nearest-first prewarm plan");
Check(TerrainPrewarmPlanner.Around(new[]{(X:0,Z:0)},1,4,10000,10000).Count()==4,"Prewarm clips world edges");
using var tileClient=new RemoteTerrainTiles(()=>now);tileClient.SetAvailable(true);
int tileSamples=0,tileRenders=0,tilePackets=0;
bool tileAllowed=true;
using var tilesServer=new TerrainTileService(10000,10000,_=>tileAllowed,
    (x,z)=>{Interlocked.Increment(ref tileSamples);return new FastMapTerrainSamplerColumn(x+z);},
    (request,grid,style)=>{
        Interlocked.Increment(ref tileRenders);
        Check(grid[0].Height==request.PageX*1024+request.PageZ*1024-request.Step*2,"Tile uses shading border");
        Check(grid[^1].Height==(request.PageX+request.PageZ+2)*1024-request.Step*2,"Tile includes final column");
        return Enumerable.Repeat(unchecked((int)0xff112233)+style,request.Width*request.Width).ToArray();
    },(owner,batch)=>{tilePackets++;Check(Environment.CurrentManagedThreadId==mainThread,"Tile network stays on main thread");tileClient.Receive(Serializer.DeepClone(batch));});
void DrainTiles(TerrainTileService service,bool prewarm=false)
{
    var timer=System.Diagnostics.Stopwatch.StartNew();
    while((service.PendingRequests>0||service.IsWorking||(prewarm&&service.PendingPrewarm>0))&&timer.ElapsedMilliseconds<10000)
    {service.Tick(20,65536);Thread.Sleep(1);}
    Check(service.PendingRequests==0&&!service.IsWorking&&(!prewarm||service.PendingPrewarm==0),"Tile work drained");
}
tilesServer.SetPrewarmTargets(tilePlan.Take(1));
tilesServer.UpdateLoad(90,67);tilesServer.Tick(20,65536);
Check(!tilesServer.IsWorking,"Speculative work pauses under CPU load");
tilesServer.UpdateLoad(10,67);DrainTiles(tilesServer,true);
Check(tileSamples==66049 && tileRenders==3 && tilePackets==0 && tilesServer.Prewarmed==1,"No-player prewarming samples once, renders three styles, sends nothing");
Check(tilesServer.PoolBytes<10000,"Pixel cache is tiny for uniform tiles");
Pending(()=>tileClient.Get(2,2,4,0));tileClient.Pump(r=>tilesServer.Request("tiles",Serializer.DeepClone(r)));DrainTiles(tilesServer);
Check(tileClient.Get(2,2,4,0).All(p=>p==unchecked((int)0xff112233)),"Pixels survive serialized tile transport");
Check(tileSamples==66049 && tilesServer.CacheHits==1 && tilesServer.WireBytes<2048,"Warm map fetch sends only compressed pixels without sampling");
Pending(()=>tileClient.Get(2,2,4,2));tileClient.Pump(r=>tilesServer.Request("tiles",r));DrainTiles(tilesServer);
Check(tileClient.Get(2,2,4,2)[0]==unchecked((int)0xff112235) && tileSamples==66049,"Palette switch reuses pre-rendered variant");
tileAllowed=false;int packetsBefore=tilePackets;
tilesServer.Request("denied",new TerrainTileRequest{Id=500,PageX=2,PageZ=2,Step=4});
Check(tilePackets==packetsBefore+1 && tileSamples==66049,"Permissions also gate cached tiles");
tileAllowed=true;

// Once background work finishes its current page, a live request precedes the next prewarm page.
using var tileGate=new ManualResetEventSlim();using var tileEntered=new ManualResetEventSlim();
var buildOrder=new System.Collections.Concurrent.ConcurrentQueue<int>();
using var priorityTiles=new TerrainTileService(10000,10000,_=>true,
    (x,z)=>{tileEntered.Set();if(!tileGate.Wait(5000))throw new Exception("Tile gate timeout");return new FastMapTerrainSamplerColumn(x);},
    (r,g,style)=>{if(style==0)buildOrder.Enqueue(r.PageX);return new int[r.Width*r.Width];},(_,_)=>{});
priorityTiles.SetPrewarmTargets(new[]{Req(600,x:1020,width:257,height:257),Req(601,x:2044,width:257,height:257)});
priorityTiles.Tick(20,65536);Check(tileEntered.Wait(5000),"Prewarm worker started");
priorityTiles.Request("live",new TerrainTileRequest{Id=602,PageX=6,PageZ=1,Step=4});
priorityTiles.Tick(20,65536);Check(priorityTiles.PendingRequests==1,"Live request accepted during background generation");
tileGate.Set();DrainTiles(priorityTiles,true);
Check(buildOrder.ToArray().SequenceEqual(new[]{1,6,2}),"Live request runs before next prewarm page");

// Incompressible pixels exercise fragmentation and raw fallback, including packed season metadata.
int[] noisePixels=Enumerable.Range(0,256*256).Select(_=>unchecked((int)Next())).ToArray();
var encodedNoise=EncodedTerrainTile.Encode(noisePixels);
Check(encodedNoise.Decode(noisePixels.Length).SequenceEqual(noisePixels),"Pixel codec preserves every bit");
using var fragmented=new RemoteTerrainTiles(()=>now);fragmented.SetAvailable(true);
TerrainTileRequest? fragmentRequest=null;
Pending(()=>fragmented.Get(1,1,4,2));fragmented.Pump(r=>fragmentRequest=r);
for(int offset=0;offset<encodedNoise.Data.Length;offset+=EncodedTerrainTile.FragmentBytes)
    fragmented.Receive(Serializer.DeepClone(new TerrainTileBatch{Id=fragmentRequest!.Id,Offset=offset,TotalBytes=encodedNoise.Data.Length,
        Compressed=encodedNoise.Compressed,Data=encodedNoise.Data.Skip(offset).Take(EncodedTerrainTile.FragmentBytes).ToArray()}));
Check(fragmented.Get(1,1,4,2).SequenceEqual(noisePixels),"Fragmented pixels reassemble without loss");
foreach(var badTile in new[]{new TerrainTileBatch{TotalBytes=int.MaxValue,Data=new byte[]{0}},
    new TerrainTileBatch{Offset=1,TotalBytes=1,Data=new byte[]{0}},new TerrainTileBatch{TotalBytes=1,Data=new byte[]{255},Compressed=true}})
{
    using var invalidTile=new RemoteTerrainTiles(()=>now);invalidTile.SetAvailable(true);
    Pending(()=>invalidTile.Get(1,1,4,0));invalidTile.Pump(r=>badTile.Id=r.Id);invalidTile.Receive(badTile);
    Failed(()=>invalidTile.Get(1,1,4,0));
}
tilesServer.SetPrewarmTargets(Array.Empty<TerrainSamplingRequest>());
tilesServer.Tick(20,65536);Check(!tilesServer.IsWorking,"Disabling server prewarm stops speculative work");
Console.WriteLine($"PASS {assertions} assertions including no-player prewarming, pixel-only transport, palette reuse, priority and invalid fragments.");

// Bound the complete pixel variants plus the active render reservation, including eviction.
using var boundedTiles=new TerrainTileService(100000,100000,_=>true,
    (x,z)=>new FastMapTerrainSamplerColumn(x+z),
    (r,g,style)=>Enumerable.Range(0,r.Width*r.Width).Select(_=>unchecked((int)Next())).ToArray(),
    (_,b)=>Check(b.Error.Length==0,"Bounded tile requests succeed"),cacheMegabytes:1,background:false);
for(int i=0;i<24;i++)
{
    boundedTiles.Request("bounded",new TerrainTileRequest{Id=700+i,PageX=i,PageZ=1,Step=16});
    boundedTiles.Tick(20,1);
    Check(boundedTiles.PoolBytes<=1024*1024,"Active tile reservation stays within budget");
    DrainTiles(boundedTiles);
    Check(boundedTiles.PoolBytes<=1024*1024,"All cached pixel variants stay within budget");
}
long boundedSamples=boundedTiles.Samples;
boundedTiles.Request("bounded",new TerrainTileRequest{Id=730,PageX=0,PageZ=1,Step=16});DrainTiles(boundedTiles);
Check(boundedTiles.Samples>boundedSamples,"Old pixel variants are evicted under memory pressure");
boundedSamples=boundedTiles.Samples;
boundedTiles.Request("bounded",new TerrainTileRequest{Id=731,PageX=0,PageZ=1,Step=16,Style=2});DrainTiles(boundedTiles);
Check(boundedTiles.Samples==boundedSamples,"Recent pixel variants survive eviction");

// Disconnecting one viewer preserves a shared build; the last viewer cancels it safely.
using var cancelTileEntered=new ManualResetEventSlim();using var cancelTileRelease=new ManualResetEventSlim();
var cancelTileReplies=new List<(string Owner,TerrainTileBatch Batch)>();
using var cancelTiles=new TerrainTileService(10000,10000,_=>true,
    (x,z)=>{cancelTileEntered.Set();if(!cancelTileRelease.Wait(5000))throw new Exception("Tile cancellation timeout");return new FastMapTerrainSamplerColumn(x);},
    (r,g,style)=>new int[r.Width*r.Width],(o,b)=>cancelTileReplies.Add((o,b)));
cancelTiles.Request("first",new TerrainTileRequest{Id=740,PageX=1,PageZ=1});cancelTiles.Tick(20,1);
Check(cancelTileEntered.Wait(5000),"Shared tile worker starts");
cancelTiles.Request("second",new TerrainTileRequest{Id=741,PageX=1,PageZ=1,Style=2});
cancelTiles.Remove("first");cancelTileRelease.Set();DrainTiles(cancelTiles);
Check(cancelTiles.Samples==66049 && cancelTileReplies.Where(r=>!r.Batch.Queued).All(r=>r.Owner=="second"),"Disconnect preserves another viewer's tile and samples once");
cancelTileEntered.Reset();cancelTileRelease.Reset();
cancelTiles.Request("abandoned",new TerrainTileRequest{Id=742,PageX=2,PageZ=1});cancelTiles.Tick(20,1);
Check(cancelTileEntered.Wait(5000),"Abandoned tile worker starts");
long tileReservation=cancelTiles.PoolBytes;
cancelTiles.Remove("abandoned");
Check(cancelTiles.PoolBytes==tileReservation,"Cancelled tile retains reservation until worker stops");
cancelTiles.Request("rejoined",new TerrainTileRequest{Id=743,PageX=2,PageZ=1});
cancelTileRelease.Set();DrainTiles(cancelTiles);
Check(cancelTileReplies.Any(r=>r.Owner=="rejoined" && r.Batch.Data.Length>0),"Cancelled tile can be requested again safely");
Check(cancelTileReplies.Where(r=>!r.Batch.Queued).All(r=>r.Owner!="abandoned"),"Cancelled viewer gets no tile data");

int tileShutdownSamples=0;
using var tileShutdownEntered=new ManualResetEventSlim();
var tileShutdown=new TerrainTileService(10000,10000,_=>true,
    (_,_)=>{Interlocked.Increment(ref tileShutdownSamples);tileShutdownEntered.Set();Thread.Sleep(1);return new FastMapTerrainSamplerColumn(1);},
    (r,g,style)=>throw new Exception("Cancelled shutdown must not render"),(_,b)=>{if(!b.Queued)throw new Exception("Shutdown must not send tiles");});
tileShutdown.Request("shutdown",new TerrainTileRequest{Id=750,PageX=1,PageZ=1});tileShutdown.Tick(20,1);
Check(tileShutdownEntered.Wait(5000),"Tile shutdown worker starts");
tileShutdown.Dispose();int tileStopped=tileShutdownSamples;Thread.Sleep(20);
Check(tileShutdownSamples==tileStopped && tileStopped<66049 && tileShutdown.PoolBytes==0,"Tile shutdown joins worker and frees cache");
tileShutdown.Dispose();
Console.WriteLine($"PASS {assertions} assertions including tile memory bounds, eviction, shared cancellation/rejoin and shutdown.");

// Submit the whole view. Acknowledged cold requests never prevent cached delivery.
using var fastClient=new RemoteTerrainTiles(()=>now);fastClient.SetAvailable(true);
using var coldEntered=new ManualResetEventSlim();using var coldRelease=new ManualResetEventSlim();
bool blockCold=false;int queueAcknowledgements=0;
using var fastServer=new TerrainTileService(1000000,1000000,_=>true,
    (x,z)=>{if(blockCold){coldEntered.Set();if(!coldRelease.Wait(5000))throw new Exception("Cold queue timeout");}return new FastMapTerrainSamplerColumn(x);},
    (r,g,style)=>Enumerable.Repeat(r.PageX,r.Width*r.Width).ToArray(),
    (_,b)=>{if(b.Queued)queueAcknowledgements++;fastClient.Receive(Serializer.DeepClone(b));},clock:()=>now);
fastServer.SetPrewarmTargets(TerrainPrewarmPlanner.Around(new[]{(X:2050,Z:2050)},1,4,100000,100000));DrainTiles(fastServer,true);
fastServer.SetPrewarmTargets(Array.Empty<TerrainSamplingRequest>());
long beforeBlocked=fastServer.Samples;blockCold=true;
for(int i=0;i<100;i++)Pending(()=>fastClient.Get(20+i,20,32,0));
fastClient.Pump(r=>fastServer.Request("fast",Serializer.DeepClone(r)));
Check(fastServer.PendingRequests==64,"Outgoing request bursts are bounded");
fastClient.Pump(r=>fastServer.Request("fast",Serializer.DeepClone(r)));
Check(fastServer.PendingRequests==100&&queueAcknowledgements==100,"Every missing page is accepted and immediately acknowledged");
Check(fastClient.PoolBytes<1024*1024,"Queued requests only reserve metadata");
fastServer.Tick(20,1);Check(coldEntered.Wait(5000),"Generation deliberately blocked");
foreach(var g in TerrainPrewarmPlanner.Around(new[]{(X:2050,Z:2050)},1,4,100000,100000))
    Pending(()=>fastClient.Get((g.X+4)/1024,(g.Z+4)/1024,4,0));
fastClient.Pump(r=>fastServer.Request("fast",Serializer.DeepClone(r)));
fastServer.Tick(20,1);
Check(fastServer.PendingRequests==100 && fastServer.Samples==beforeBlocked,"Cached tiles all sent while 100 cold requests remain blocked");
Check(fastClient.Get(2,2,4,0)[0]==2 && fastClient.Get(3,3,4,0)[0]==3,"Ready central and outer tiles reach client during cold sampling");
// Queue updates keep long generation waits alive without re-requesting or resampling.
int unexpectedRequests=0;
for(int interval=0;interval<15;interval++)
{
    now+=5001;
    for(int i=0;i<100;i++)Pending(()=>fastClient.Get(20+i,20,32,0));
    fastServer.Tick(20,1);
    fastClient.Pump(_=>unexpectedRequests++);
}
Check(unexpectedRequests==0&&queueAcknowledgements>=1600,"Server callbacks keep a long queue alive without client network polling");
coldRelease.Set();DrainTiles(fastServer);
Check(fastClient.Get(119,20,32,0)[0]==119,"Server pushes the last queued tile without needing a new request");
Check(fastClient.PoolBytes<=RemoteTerrainTiles.MemoryLimit,"Client tile memory stays bounded");
Console.WriteLine($"PASS {assertions} assertions including full-view acknowledgements, cached delivery during queued work and callback liveness.");

// A cached viewport can arrive faster than the renderer consumes it. Keep its
// compressed pages, instead of expanding 300 tiles beyond the 32 MiB budget.
using var burstClient=new RemoteTerrainTiles(()=>now);burstClient.SetAvailable(true);
var burstRequests=new List<TerrainTileRequest>();
for(int i=0;i<300;i++)Pending(()=>burstClient.Get(i,1,4,0));
for(int i=0;i<5;i++)burstClient.Pump(r=>burstRequests.Add(r));
foreach(var r in burstRequests)
{
    var tile=EncodedTerrainTile.Encode(Enumerable.Repeat(r.PageX,r.Width*r.Width).ToArray());
    burstClient.Receive(new TerrainTileBatch{Id=r.Id,TotalBytes=tile.Data.Length,Data=tile.Data,Compressed=tile.Compressed});
}
Check(burstClient.PoolBytes<1024*1024,"Whole cached burst remains compressed while waiting for rendering");
for(int i=0;i<300;i++)Check(burstClient.Get(i,1,4,0)[0]==i,"Burst page retained until the renderer consumes it");
burstClient.Pump(_=>throw new Exception("Cached burst must not require refetches"));
Console.WriteLine($"PASS {assertions} assertions including compressed cached viewport bursts without refetching.");

// An unaudited sampler stays on the tick thread, but a slow renderer must not.
using var renderEntered=new ManualResetEventSlim();
using var renderRelease=new ManualResetEventSlim();
int foregroundSamples=0,foregroundRenders=0;
var foregroundReplies=new List<TerrainTileBatch>();
using var foregroundTiles=new TerrainTileService(10000,10000,_=>true,
    (_,_)=>{if(Environment.CurrentManagedThreadId!=mainThread)throw new Exception("Unsafe sampler ran on worker");foregroundSamples++;return new FastMapTerrainSamplerColumn(42);},
    (r,g,style)=>
    {
        if(Environment.CurrentManagedThreadId==mainThread)throw new Exception("Renderer ran on tick thread");
        Interlocked.Increment(ref foregroundRenders);renderEntered.Set();
        if(!renderRelease.Wait(5000))throw new Exception("Render gate timeout");
        return Enumerable.Repeat(style+42,r.Width*r.Width).ToArray();
    },(_,b)=>foregroundReplies.Add(b),background:false);
foregroundTiles.Request("foreground",new TerrainTileRequest{Id=800,PageX=1,PageZ=1,Step=32});
var foregroundTimer=System.Diagnostics.Stopwatch.StartNew();
while(!renderEntered.IsSet&&foregroundTimer.ElapsedMilliseconds<1000)foregroundTiles.Tick(2,256);
Check(renderEntered.Wait(1000),"Tick returns while slow rendering is blocked on a worker");
Check(foregroundSamples==1089&&!foregroundTiles.Background,"Main-thread-only sampler completes exactly one grid");
long renderingReservation=foregroundTiles.PoolBytes;
for(int i=0;i<10;i++)foregroundTiles.Tick(2,256);
Check(foregroundSamples==1089&&foregroundRenders==1&&foregroundTiles.PoolBytes==renderingReservation,"Pending render retains reservation without sampling or dispatching again");
renderRelease.Set();DrainTiles(foregroundTiles);
Check(foregroundRenders==3&&foregroundReplies.Any(b=>b.Data.Length>0)&&foregroundReplies.All(b=>b.Error.Length==0),"Worker renders and compresses three variants before tick-thread delivery");
renderEntered.Reset();renderRelease.Reset();
foregroundTiles.Request("cancel-render",new TerrainTileRequest{Id=801,PageX=2,PageZ=1,Step=32});
foregroundTimer.Restart();
while(!renderEntered.IsSet&&foregroundTimer.ElapsedMilliseconds<1000)foregroundTiles.Tick(2,256);
Check(renderEntered.Wait(1000),"Second main-thread sample reaches rendering worker");
renderingReservation=foregroundTiles.PoolBytes;
foregroundTiles.Remove("cancel-render");
Check(foregroundTiles.PoolBytes==renderingReservation,"Cancelling an active render retains its memory until worker completion");
renderRelease.Set();DrainTiles(foregroundTiles);
Check(!foregroundReplies.Any(b=>b.Id==801&&b.Data.Length>0),"Cancelled render cannot send a late tile");

// Integrated single-player servers must not duplicate the client's Background Pregen.
var prewarmConfig=new FastMap.Config.FastMapServerConfig{EnableTerrainSampling=true,EnableServerPrewarm=true};
int hostingSamples=0,hostingPackets=0;
using var hostingTiles=new TerrainTileService(10000,10000,_=>true,
    (_,_)=>{Interlocked.Increment(ref hostingSamples);return new FastMapTerrainSamplerColumn(42);},
    (r,g,s)=>new int[r.Width*r.Width],(_,_)=>hostingPackets++);
void SetHostingTargets(bool dedicated)=>hostingTiles.SetPrewarmTargets(prewarmConfig.ShouldPrewarm(dedicated)?tilePlan.Take(1):Array.Empty<TerrainSamplingRequest>());
SetHostingTargets(false);hostingTiles.Tick(2,256);
Check(hostingSamples==0&&!hostingTiles.IsWorking&&hostingTiles.PendingPrewarm==0,"Enabled server settings still produce no automatic single-player work");
prewarmConfig.EnableTerrainSampling=false;SetHostingTargets(true);hostingTiles.Tick(2,256);
Check(hostingSamples==0&&!hostingTiles.IsWorking,"Master opt-out suppresses dedicated prewarming");
prewarmConfig.EnableTerrainSampling=true;prewarmConfig.EnableServerPrewarm=false;SetHostingTargets(true);hostingTiles.Tick(2,256);
Check(hostingSamples==0&&!hostingTiles.IsWorking,"Prewarm opt-out suppresses dedicated speculative work");
prewarmConfig.EnableServerPrewarm=true;SetHostingTargets(true);DrainTiles(hostingTiles,true);
Check(hostingSamples==66049&&hostingPackets==0,"Dedicated hosting still prewarms one target without players or packets");

// Server settings and effective palettes define a stable disk-cache namespace.
var renderIdentity=new TerrainRenderingIdentity(0,-1,250,256,110,1,2,3,"palette-a","1.3.0");
string originalFingerprint=renderIdentity.Fingerprint;
var changedIdentities=new[]
{
    renderIdentity with {HeightOffset=1}, renderIdentity with {WaterLevelOffset=0},
    renderIdentity with {SnowStartHeight=251}, renderIdentity with {MapHeight=320},
    renderIdentity with {SeaLevel=111}, renderIdentity with {LandColor=4},
    renderIdentity with {WaterColor=5}, renderIdentity with {WaterEdgeColor=6},
    renderIdentity with {PaletteFingerprint="palette-b"},renderIdentity with {SamplerVersion="1.4.0"}
};
Check(originalFingerprint==(renderIdentity with {}).Fingerprint,"Identical settings reuse the cache across server restarts");
foreach(var changedIdentity in changedIdentities)
{
    var status=Serializer.DeepClone(new TerrainSamplingStatus{Available=true,RenderingFingerprint=changedIdentity.Fingerprint});
    Check(status.CanUseTiles&&status.RenderingFingerprint==changedIdentity.Fingerprint,"Server rendering identity survives negotiation");
    Check(TerrainRenderingIdentity.CacheSuffix(status.RenderingFingerprint)!=TerrainRenderingIdentity.CacheSuffix(originalFingerprint),"Changed server rendering input selects a different cache namespace");
}
Check(!new TerrainSamplingStatus{Available=true}.CanUseTiles,"Missing identity cannot expose legacy cached tiles");
Check(!new TerrainSamplingStatus{Version=6,Available=true,RenderingFingerprint=originalFingerprint}.CanUseTiles,"Previous protocol cannot bypass rendering identity negotiation");
Check(!new TerrainSamplingStatus{Available=true,RenderingFingerprint="../../other-cache"}.CanUseTiles,"Untrusted identity cannot become a filesystem path");
Check(TerrainRenderingIdentity.CacheSuffix(null)!=TerrainRenderingIdentity.CacheSuffix(originalFingerprint),"Pre-handshake cache is isolated until the server identity arrives");
// Vanilla captures a List enumerator before the handshake changes the live list.
// Reproduce the reported failure, then exercise every production publication path.
var mutatedLayers = new List<string> { "waypoints", "old-terrain", "overlay" };
var invalidatedEnumerator = mutatedLayers.GetEnumerator();
Check(invalidatedEnumerator.MoveNext(), "Vanilla enumeration starts before handshake");
mutatedLayers[1] = "new-terrain";
Failed(() => invalidatedEnumerator.MoveNext());
foreach (string operation in new[] { "replace", "mapper-insert", "overlay-remove", "overlay-append" })
{
    var oldLayers = new List<string> { "waypoints", "old-terrain", "overlay" };
    var inFlight = oldLayers.GetEnumerator();
    Check(inFlight.MoveNext(), "Worker captures the old layer list");
    var published = operation switch
    {
        "replace" => MapLayerList.Replace(oldLayers, 1, "new-terrain"),
        "mapper-insert" => MapLayerList.Insert(oldLayers, 2, "fastmap"),
        "overlay-remove" => MapLayerList.RemoveAt(oldLayers, 2),
        _ => MapLayerList.Insert(oldLayers, oldLayers.Count, "new-overlay")
    };
    Check(!ReferenceEquals(published, oldLayers), "Publish an independent layer list");
    var remaining = new List<string>();
    while (inFlight.MoveNext()) remaining.Add(inFlight.Current);
    Check(remaining.SequenceEqual(new[] { "old-terrain", "overlay" }), "In-flight enumeration finishes without mutation or skipped layers");
    string[] expected = operation switch
    {
        "replace" => new[] { "waypoints", "new-terrain", "overlay" },
        "mapper-insert" => new[] { "waypoints", "old-terrain", "fastmap", "overlay" },
        "overlay-remove" => new[] { "waypoints", "old-terrain" },
        _ => new[] { "waypoints", "old-terrain", "overlay", "new-overlay" }
    };
    Check(published.SequenceEqual(expected), "Next worker iteration sees the complete replacement in order");
}

// Hold both an off-thread tick and a page task open during retirement. Cleanup
// must await both, and a task dispatched earlier but not yet started must be denied.
var retiringLifetime = new MapLayerWorkerLifetime();
Check(retiringLifetime.TryEnter(), "Existing map tick admitted");
Check(retiringLifetime.TryEnter(), "Existing page task admitted concurrently");
int resourcesDisposed = 0;
var retirement = Task.Run(() =>
{
    retiringLifetime.StopAndWait();
    Interlocked.Exchange(ref resourcesDisposed, 1);
});
bool retirementStarted = SpinWait.SpinUntil(() =>
{
    if (!retiringLifetime.TryEnter()) return true;
    retiringLifetime.Exit();
    return false;
}, 5000);
try
{
    Check(retirementStarted, "Retirement closes work admission before cleanup");
    Check(Volatile.Read(ref resourcesDisposed) == 0, "Resources survive active tick and task");
    Check(!retiringLifetime.TryEnter(), "Late task and stale-list tick cannot enter retired layer");
}
finally { retiringLifetime.Exit(); }
try
{
    Check(Volatile.Read(ref resourcesDisposed) == 0, "One finished worker cannot free resources still used by another");
}
finally { retiringLifetime.Exit(); }
Check(retirement.Wait(5000) && resourcesDisposed == 1, "Resources released after all admitted work exits");
retiringLifetime.StopAndWait();
Check(!retiringLifetime.TryEnter(), "Repeated retirement stays closed without waiting");
var nextLifetime = new MapLayerWorkerLifetime();
Check(nextLifetime.TryEnter(), "Replacement layer has an independent active lifetime");
nextLifetime.Exit();
nextLifetime.StopAndWait();

Console.WriteLine($"PASS {assertions} assertions including immutable layer publication and worker retirement.");

// Durable tile caches survive service recreation without sampling or rendering.
string diskTestRoot = Path.Combine(Path.GetTempPath(), "fastmap-disk-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(diskTestRoot);
try
{
    var defaults = new FastMap.Config.FastMapServerConfig();
    Check(defaults.PersistTileCache && defaults.TileDiskCacheMegabytes == 1024 && !defaults.ShouldPersist(true),
        "Persistence defaults on but master Pregen opt-out prevents disk use");
    defaults.EnableTerrainSampling = true;
    Check(defaults.ShouldPersist(true) && !defaults.ShouldPersist(false), "Only enabled dedicated hosts persist server tiles");
    defaults.PersistTileCache = false;
    Check(!defaults.ShouldPersist(true), "Persistence can be explicitly disabled");

    var identity = new TerrainTileCacheIdentity("world-a", 123, 10000, 10000, "settings-a", "mods-a", originalFingerprint);
    Check(identity.Fingerprint == (identity with {}).Fingerprint, "World cache identity is stable across restarts");
    foreach (var changed in new[] { identity with { WorldId="world-b" }, identity with { Seed=124 },
        identity with { SizeX=20000 }, identity with { SizeZ=20000 }, identity with { WorldConfiguration="settings-b" },
        identity with { ModVersions="mods-b" }, identity with { Rendering=changedIdentities[0].Fingerprint }, identity with { Revision=1 } })
        Check(changed.Fingerprint != identity.Fingerprint, "Changed world or renderer cannot reuse stale pixels");

    string persistentRoot = Path.Combine(diskTestRoot, "restart");
    var savedRequest = new TerrainTileRequest { Id=1, PageX=2, PageZ=2, Step=4 };
    int savedSamples=0, savedRenders=0;
    int[] DiskPixels(TerrainTileRequest r, int style) => Enumerable.Repeat(unchecked((int)0xff345678)+style, r.Width*r.Width).ToArray();
    using (var first = new TerrainTileService(10000,10000,_=>true,
        (x,z)=>{savedSamples++;return new FastMapTerrainSamplerColumn(42);},
        (r,g,s)=>{savedRenders++;return DiskPixels(r,s);},(_,_)=>{},
        diskCache:new TerrainTileDiskCache(persistentRoot,identity.Fingerprint)))
    {
        first.SetPrewarmTargets(tilePlan.Take(1));
        DrainTiles(first,true);
        Check(savedSamples==66049 && savedRenders==3 && first.DiskCachedPages==1, "Completed prewarm is persisted immediately in all palettes");
    }
    bool diskPermission=true;
    using (var restoredClient=new RemoteTerrainTiles(()=>now))
    using (var restored = new TerrainTileService(10000,10000,_=>diskPermission,
        (_,_)=>throw new Exception("Restored tile resampled"),(_,_,_)=>throw new Exception("Restored tile rerendered"),
        (_,batch)=>restoredClient.Receive(Serializer.DeepClone(batch)),background:false,
        diskCache:new TerrainTileDiskCache(persistentRoot,identity.Fingerprint)))
    {
        restored.SetPrewarmTargets(tilePlan.Take(1));
        DrainTiles(restored,true);
        Check(restored.CachedPages==0 && restored.DiskCachedPages==1 && restored.Samples==0,
            "Restart indexes compressed files without eagerly filling RAM or regenerating prewarm");
        restoredClient.SetAvailable(true);
        for(int style=0;style<3;style++)
        {
            int selected=style;
            Pending(()=>restoredClient.Get(2,2,4,selected));
            restoredClient.Pump(r=>restored.Request("reader",r));
            DrainTiles(restored);
            Check(restoredClient.Get(2,2,4,style).SequenceEqual(DiskPixels(savedRequest,style)), "Every persisted palette survives restart and network transfer");
        }
        Check(restored.DiskHits==1 && restored.Samples==0, "One disk read serves every palette without sampling");
        long sentBefore=restored.WireBytes;
        diskPermission=false;
        restored.Request("denied",new TerrainTileRequest {Id=44,PageX=2,PageZ=2,Step=4});
        restored.Tick(20,65536);
        Check(restored.WireBytes==sentBefore, "Persisted tiles do not bypass permission checks");
    }

    // Disk reads must bypass the busy sampling/rendering worker.
    using(var coldStarted=new ManualResetEventSlim())
    using(var releaseCold=new ManualResetEventSlim())
    {
        var received=new List<TerrainTileBatch>();
        using var duringCold=new TerrainTileService(10000,10000,_=>true,
            (_,_)=>{coldStarted.Set();if(!releaseCold.Wait(5000))throw new Exception("Cold sampling timeout");return new FastMapTerrainSamplerColumn(42);},
            (r,g,s)=>DiskPixels(r,s),(_,b)=>received.Add(b),diskCache:new TerrainTileDiskCache(persistentRoot,identity.Fingerprint));
        DrainTiles(duringCold);
        duringCold.Request("reader",new TerrainTileRequest {Id=91,PageX=6,PageZ=6,Step=32});
        duringCold.Tick(20,65536);
        try
        {
            Check(coldStarted.Wait(5000), "Cold generation deliberately held open");
            duringCold.Request("reader",new TerrainTileRequest {Id=92,PageX=2,PageZ=2,Step=4,CachedOnly=true});
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(!received.Any(b=>b.Id==92 && b.Data.Length>0) && timer.ElapsedMilliseconds<3000)
            {duringCold.Tick(20,65536);Thread.Sleep(1);}
            Check(received.Any(b=>b.Id==92 && b.Data.Length>0) && duringCold.DiskHits==1,
                "Persisted cached-only delivery completes while cold sampling is blocked");
        }
        finally { releaseCold.Set(); }
        DrainTiles(duringCold);
    }

    string tileFile=Path.Combine(persistentRoot,identity.Fingerprint,"2_2_4.fmt");
    foreach(string corruption in new[]{"checksum","truncated","oversized-length","missing"})
    {
        using var disk=new TerrainTileDiskCache(persistentRoot,identity.Fingerprint);
        Check(SpinWait.SpinUntil(()=>disk.Ready,5000), "Disk index initializes off the caller thread");
        byte[] good=File.ReadAllBytes(tileFile);
        if(corruption=="missing") File.Delete(tileFile);
        else
        {
            byte[] damaged=good.ToArray();
            if(corruption=="checksum") damaged[^1]^=1;
            if(corruption=="truncated") damaged=damaged[..20];
            if(corruption=="oversized-length") System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(17),int.MaxValue);
            File.WriteAllBytes(tileFile,damaged);
        }
        int regenerated=0;
        using(var recovery=new TerrainTileService(10000,10000,_=>true,
            (_,_)=>{regenerated++;return new FastMapTerrainSamplerColumn(42);},(r,g,s)=>DiskPixels(r,s),(_,_)=>{},diskCache:disk))
        {
            recovery.Request("repair",savedRequest);
            DrainTiles(recovery);
            Check(regenerated==66049 && recovery.DiskHits==0 && recovery.DiskCachedPages>=1,
                "Missing/corrupt/oversized persisted file regenerates without failing the request");
        }
    }

    string boundedRoot=Path.Combine(diskTestRoot,"bounded");
    using(var bounded=new TerrainTileDiskCache(boundedRoot,identity.Fingerprint,megabytes:1))
    {
        Check(SpinWait.SpinUntil(()=>bounded.Ready,5000), "Bounded cache initialized");
        var noise=new Random(17);
        var payloads=Enumerable.Range(0,3).Select(_=>{var data=new byte[256*256*4];noise.NextBytes(data);return new EncodedTerrainTile(data,false);}).ToArray();
        bounded.Store(savedRequest,payloads);
        var second=new TerrainTileRequest{Id=2,PageX=3,PageZ=2,Step=4};
        bounded.Store(second,payloads,prewarm:true);
        Check(bounded.Size(savedRequest)>0 && bounded.Size(second)==0 && bounded.Bytes<=1024*1024,
            "Speculative disk writes stop at capacity without endlessly evicting prewarm targets");
        bounded.Store(second,payloads);
        Check(bounded.Size(savedRequest)==0 && bounded.Size(second)>0 && bounded.CachedPages==1 && bounded.Bytes<=1024*1024,
            "Demanded tiles evict older disk files within the size budget");
        Check(bounded.TryRead(second,out var rawRead) && rawRead.Wait(5000) && rawRead.Result![2].Data.SequenceEqual(payloads[2].Data),
            "Incompressible raw tiles persist without losing pixel bits");
    }
    using(var changedCache=new TerrainTileDiskCache(boundedRoot,(identity with{Rendering="new-renderer"}).Fingerprint,megabytes:1))
    {
        Check(SpinWait.SpinUntil(()=>changedCache.Ready,5000) && changedCache.CachedPages==0,
            "Changed rendering namespace cannot load prior settings");
        changedCache.Store(savedRequest,Enumerable.Range(0,3).Select(s=>EncodedTerrainTile.Encode(DiskPixels(savedRequest,s))).ToArray());
        Check(changedCache.Bytes<=1024*1024, "Obsolete namespaces share the same disk budget");
    }
    string unavailable=Path.Combine(diskTestRoot,"not-a-directory");
    File.WriteAllText(unavailable,"blocked");
    int warnings=0;
    using(var fallback=new TerrainTileService(10000,10000,_=>true,(_,_)=>new FastMapTerrainSamplerColumn(42),
        (r,g,s)=>DiskPixels(r,s),(_,_)=>{},diskCache:new TerrainTileDiskCache(unavailable,identity.Fingerprint,log:_=>warnings++)))
    {
        fallback.Request("reader",new TerrainTileRequest{Id=5,PageX=2,PageZ=2,Step=32});
        DrainTiles(fallback);
        Check(fallback.Samples>0 && fallback.DiskCachedPages==0 && warnings==1,
            "Unavailable disk falls back to memory and generation with one diagnostic");
    }
    string burstRoot=Path.Combine(diskTestRoot,"burst");
    using(var seedCache=new TerrainTileDiskCache(burstRoot,identity.Fingerprint))
    {
        Check(SpinWait.SpinUntil(()=>seedCache.Ready,5000), "Burst fixture initialized");
        for(int i=0;i<40;i++)
        {
            var request=new TerrainTileRequest{Id=i+1,PageX=i%8,PageZ=i/8,Step=32};
            seedCache.Store(request,Enumerable.Range(0,3).Select(s=>new EncodedTerrainTile(new byte[32*32*4],false)).ToArray());
        }
    }
    int diskBurstCompleted=0;
    using(var burst=new TerrainTileService(10000,10000,_=>true,
        (_,_)=>throw new Exception("Disk burst regenerated"),(_,_,_)=>throw new Exception("Disk burst rerendered"),
        (_,b)=>{if(b.Data.Length>0 && b.Offset+b.Data.Length==b.TotalBytes)diskBurstCompleted++;},cacheMegabytes:1,
        diskCache:new TerrainTileDiskCache(burstRoot,identity.Fingerprint)))
    {
        // Queue even before the startup scan has completed.
        for(int i=0;i<40;i++) burst.Request("burst",new TerrainTileRequest{Id=i+1,PageX=i%8,PageZ=i/8,Step=32,CachedOnly=true});
        var timer=System.Diagnostics.Stopwatch.StartNew();
        long maximumPool=0;
        while(burst.IsWorking || burst.PendingRequests>0)
        {
            burst.Tick(20,65536);
            maximumPool=Math.Max(maximumPool,burst.PoolBytes);
            if(timer.ElapsedMilliseconds>5000)throw new Exception("Disk burst timeout");
            Thread.Sleep(1);
        }
        Check(diskBurstCompleted==40 && burst.DiskHits==40 && burst.Samples==0, "Entire persisted burst is served after restart without cold generation");
        Check(maximumPool<=1024*1024, "Concurrent disk-read reservations and ready tiles stay within the RAM budget");
    }
    using(var shrunken=new TerrainTileDiskCache(burstRoot,identity.Fingerprint,megabytes:1))
    {
        Check(SpinWait.SpinUntil(()=>shrunken.Ready,5000), "Cache reopened with explicit disk limit");
        Check(shrunken.Bytes==Directory.EnumerateFiles(burstRoot,"*.fmt",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length),
            "Disk accounting includes files restored from a previous process");
    }
}
finally { Directory.Delete(diskTestRoot,recursive:true); }
Console.WriteLine($"PASS {assertions} assertions including persistent tile reuse, corruption recovery, independent disk reads and bounded storage.");

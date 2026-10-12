using System;
using System.Collections.Generic;
using UnityEngine;

// Minimal deterministic world for the production spot allocator and local passing controller.
// This tests their decisions; it does not simulate Unity physics or the navmesh baker.
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public float sqrMagnitude => x*x+y*y+z*z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude > 0 ? this / magnitude : zero;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a, float b) => new(a.x*b,a.y*b,a.z*b);
        public static Vector3 operator /(Vector3 a, float b) => a*(1/b);
        public static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp((a.x*b.x+a.y*b.y+a.z*b.z)/(a.magnitude*b.magnitude),-1,1))*Mathf.Rad2Deg;
    }
    public static class Mathf
    {
        public const float Rad2Deg = 180/MathF.PI, Deg2Rad = MathF.PI/180;
        public static float Clamp(float x, float a, float b) => Math.Clamp(x,a,b);
        public static float Sqrt(float x) => MathF.Sqrt(x);
        public static float DeltaAngle(float a, float b) => (b-a+540)%360-180;
        public static float MoveTowardsAngle(float a,float b,float d) => a+Math.Clamp(DeltaAngle(a,b),-d,d);
    }
    public static class Time { public static float time; }
}

namespace FruitLib
{
    public class FruitBody
    {
        public int Id;
        public Vector3 Position;
        public Vector3 Facing = new(0,0,1);
        public bool HasPosition = true, Alive = true, Upright = true;
    }
    public static class FruitBodies { public static List<FruitBody> All = new(); }
    public enum FruitPlaceKind { Building, Room, Area }
    public class FruitPlace
    {
        public FruitPlaceKind Kind;
        public Vector3 Size = new(10,3,10);
        public bool Contains(Vector3 p, float margin = 0) => Math.Abs(p.x) < Size.x/2+margin && Math.Abs(p.z) < Size.z/2+margin;
    }
    public class FruitDoor { public Vector3 Position; }
    public static class FruitPlaces
    {
        public static FruitPlace Place;
        public static List<FruitDoor> Doors = new();
        public static FruitPlace Where(Vector3 p) => Place;
    }
    public enum FruitPathStatus { Complete }
    public enum FruitMoveResult { Arrived }
    public class FruitPath
    {
        public float Length;
        public FruitPathStatus Status;
        public Vector3 Start, End;
        public Vector3 PointAt(float d) => Start+(End-Start).normalized*d;
    }
    public static class FruitLog { public static void Info(string s) {} }
    public static class FruitNav
    {
        public static bool Ready = true;
        public static Func<Vector3, bool> Allowed = p => true;
        public static Func<Vector3, Vector3, bool> Clear = (a,b) => true;
        public static float SampleY;
        public static float Flat(Vector3 p) => MathF.Sqrt(p.x*p.x+p.z*p.z);
        public static bool Sample(Vector3 p, float distance, out Vector3 q) { q=p; q.y=SampleY; return Ready && Allowed(q); }
        public static bool Under(Vector3 p, float distance, out Vector3 q) => Sample(p,distance,out q);
        public static float Room(Vector3 p) => Allowed(p) ? 2 : 0;
        public static bool StraightClear(Vector3 a,Vector3 b) => Ready && Clear(a,b);
        public static bool FindPath(Vector3 a,Vector3 b,FruitPath p,float snap,bool measure,bool shape)
        { p.Start=a; p.End=b; p.Length=Flat(b-a); p.Status=FruitPathStatus.Complete; return Ready; }
    }
    public static partial class FruitBodyControl
    {
        private sealed class Stats { public int Reslots; }
        private sealed class Runtime
        {
            public float CreatureYRotation, PuppeteerTargetYRot;
            public void UpdateTargetYRot(float a) { PuppeteerTargetYRot=a; }
        }
        private sealed class Order
        {
            public FruitBody Body, Target;
            public Vector3 Want, Stand, Dest, Slot, Dir, PassBeside, PassBeyond, ComVel;
            public float WantStop, SpreadRadius, ReslotAt, StopDistance, PathAt, SlotAt, SlotAngle,
                HoldDistance, PassUntil, PassCheckAt, ProgressLeft, ProgressAt, TrafficSince;
            public bool HasDest, Slotted, Paused, Requested, Pivot, Stance, Strikes, Keep, Watching, ComInit;
            public bool Run, Tight, TrafficBlocked;
            public int Reslots, PassStage;
            public FruitPlace SpreadPlace;
            public Stats Stats;
            public Runtime Runtime = new();
            public FruitMoveResult Result;
        }
        private const float ProgressWindow = 2, TightRoom = .25f, TurnRate = 150;
        private static readonly Dictionary<int, Order> _orders = new();
        private static bool Upright(FruitBody b) => b.Upright;
        private static bool Smooth(Order o) => true;
        private static bool IsGoto(Order o) => o.HasDest || o.Target != null;
        private static Vector3 LiveCentre(FruitBody b) => b.Position;
        private static float FlatSpeed(Order o) => FruitNav.Flat(o.ComVel);
        private static void Face(FruitBody b, Vector3 d,float t) {}

        private static int checks;
        private static void Check(bool ok,string name) { if(!ok) throw new Exception(name); checks++; }
        private static FruitBody Body(float x,float z,float y=1)
        { var b=new FruitBody { Id=FruitBodies.All.Count+1,Position=new(x,y,z) }; FruitBodies.All.Add(b); return b; }
        private static void Reset()
        { FruitBodies.All.Clear(); _orders.Clear(); FruitPlaces.Doors.Clear(); FruitPlaces.Place=null;
          FruitNav.Ready=true; FruitNav.Allowed=p=>true; FruitNav.Clear=(a,b)=>true; FruitNav.SampleY=0; }
        private static Order Walking(FruitBody b,Vector3 dest)
        { var o=new Order { Body=b,HasDest=true,Dest=dest,Stand=dest,StopDistance=.8f,Requested=true,ComInit=true };
          _orders[b.Id]=o; return o; }

        public static void Main()
        {
            Reset();
            var claims=new List<Vector3>();
            for(int i=0;i<10;i++)
            {
                var b=Body(0,-10-i*1.5f);
                var o=Walking(b,new(0,0,0)); Claim(o,o.Dest); claims.Add(o.Stand);
                for(int j=0;j<i;j++) Check(FruitNav.Flat(claims[j]-o.Stand)>=PersonalSpace+SpotStop-.001f,"shared destination spacing");
            }
            float minX=0,maxX=0;
            foreach(var p in claims) { minX=Math.Min(minX,p.x); maxX=Math.Max(maxX,p.x); Check(FruitNav.Flat(p)<=SpreadRadius,"compact gathering"); }
            Check(maxX-minX>2,"arrivals spread laterally instead of forming one line");

            Reset();
            var walker=Body(0,-5); var stander=Body(0,-1);
            Check(ArrivalObstruction(walker,walker.Position,new(0,0,0))>ArrivalObstruction(walker,walker.Position,new(1.4f,0,0)),"prefer an unobstructed final approach");
            FruitPlaces.Doors.Add(new FruitDoor { Position=new(0,0,0) });
            Check(FreeSpot(walker,new(0,0,0),out var spot) && FruitNav.Flat(spot)>=1,"keep door clear");
            FruitNav.Clear=(a,b)=>false;
            Check(!FreeSpot(walker,new(0,0,0),out spot),"no gathering through walls");
            FruitNav.Ready=false;
            Check(!FreeSpot(walker,new(0,0,0),out spot),"no invented slots without navmesh");

            Reset();
            walker=Body(0,0); stander=Body(0,1.6f);
            var order=Walking(walker,new(0,0,6));
            Check(PassCrowd(order,null,walker.Position,new(0,0,1),order.Dest,0,out var dir),"route around settled person");
            Check(dir.x>0,"deterministic right hand pass");
            var original=order.PassBeside;
            Check(PassCrowd(order,null,new(.7f,1,.5f),new(-.8f,0,.6f),order.Dest,.1f,out dir) && dir.x>0,"off-path pursuit cannot pull passer back into queue");
            Check(PassCrowd(order,null,original,new(0,0,1),order.Dest,.2f,out dir) && order.PassStage==2 && dir.z>0,"continue beyond blocker before rejoining");
            Check(!PassCrowd(order,null,order.PassBeyond,new(0,0,1),order.Dest,.3f,out dir) && order.PassStage==0,"release passing route once beyond");

            order.PassCheckAt=0; FruitNav.Allowed=p=>p.x<=0;
            Check(PassCrowd(order,null,walker.Position,new(0,0,1),order.Dest,1,out dir) && dir.x<0,"use opposite side when preferred side is a wall");
            order.PassStage=0; order.PassCheckAt=0; FruitNav.Allowed=p=>Math.Abs(p.x)<.5f;
            Check(!PassCrowd(order,null,walker.Position,new(0,0,1),order.Dest,2,out dir),"do not pass through narrow corridor walls");
            FruitNav.Allowed=p=>true; FruitNav.Clear=(a,b)=>false; order.PassCheckAt=0;
            Check(!PassCrowd(order,null,walker.Position,new(0,0,1),order.Dest,3,out dir),"free endpoints do not authorize crossing a wall");
            FruitNav.Clear=(a,b)=>true; FruitNav.SampleY=3; order.PassCheckAt=0;
            Check(!PassCrowd(order,null,walker.Position,new(0,0,1),order.Dest,4,out dir),"do not pass on another floor");
            FruitNav.SampleY=0; order.PassCheckAt=0;
            Check(!PassCrowd(order,stander,walker.Position,new(0,0,1),order.Dest,5,out dir),"do not bypass the person being approached");
            var traffic=Walking(stander,new(0,0,8)); traffic.ComVel=new(0,0,1); order.PassCheckAt=0;
            Check(!PassCrowd(order,null,walker.Position,new(0,0,1),order.Dest,6,out dir),"moving traffic keeps ordinary avoidance");
            traffic.ComVel=Vector3.zero; order.PassCheckAt=0;
            Check(PassCrowd(order,null,walker.Position,new(0,0,1),order.Dest,7,out dir),"a stalled walker can be passed");

            Reset();
            walker=Body(0,0); stander=Body(0,1.6f); stander.Upright=false;
            order=Walking(walker,new(0,0,6));
            Check(PassCrowd(order,null,walker.Position,new(0,0,1),order.Dest,0,out dir) && Math.Abs(order.PassBeside.x)>=LyingSpace+.39f,"fallen bodies get a wider berth");
            var flank=Body(.65f,.525f);
            Check(!PassLegClear(walker,walker.Position,order.PassBeside),"free endpoint cannot route through a second person");
            flank.Position=new(0,4,1);
            Check(PassLegClear(walker,walker.Position,order.PassBeside),"people on other storeys do not block a pass");
            Reset();
            walker=Body(0,0); var incoming=Body(0,2.4f);
            order=Walking(walker,new(0,0,8)); order.ComVel=new(0,0,1.5f);
            var opposing=Walking(incoming,new(0,0,-8)); opposing.ComVel=new(0,0,-1.5f);
            dir=Traffic(order,null,walker.Position,new(0,0,1),0);
            Check(!order.TrafficBlocked && dir.x>0.3f,"head-on traffic steers early to its right");
            var reverse=Traffic(opposing,null,incoming.Position,new(0,0,-1),0);
            Check(!opposing.TrafficBlocked && reverse.x<-.3f,"opposing walker prefers its own right");
            FruitNav.Clear=(a,b)=>false;
            dir=Traffic(order,null,walker.Position,new(0,0,1),1);
            Check(order.TrafficBlocked,"stop rather than steer through furniture");
            Traffic(order,null,walker.Position,new(0,0,1),2);
            Check(order.TrafficSince==1,"waiting timer persists for bounded recovery grace");
            incoming.Position=new(0,1,6);
            Traffic(order,null,walker.Position,new(0,0,1),3);
            Check(!order.TrafficBlocked,"resume after incoming traffic clears");
            incoming.Position=new(0,1,2.4f); order.Tight=true; FruitNav.Clear=(a,b)=>true;
            Traffic(order,null,walker.Position,new(0,0,1),4);
            Check(order.TrafficBlocked,"no lateral detour inside narrow passage");

            Reset();
            walker=Body(0,0); var crossing=Body(1.5f,1.5f);
            order=Walking(walker,new(0,0,8));
            var crossOrder=Walking(crossing,new(-8,0,1.5f)); crossOrder.ComVel=new(-1.5f,0,0);
            Check(!TrafficClear(walker,null,walker.Position,new(0,0,1),1.5f),"predict crossing collision before body enters current lane");
            crossOrder.ComVel=new(1.5f,0,0);
            Check(TrafficClear(walker,null,walker.Position,new(0,0,1),1.5f),"receding traffic does not cause a stop");
            crossing.Position=new(0,4,1);
            Check(TrafficClear(walker,null,walker.Position,new(0,0,1),1.5f),"prediction excludes other floors");
            crossing.Position=new(0,1,.5f); crossOrder.ComVel=Vector3.zero;
            Check(TrafficClear(walker,null,walker.Position,new(0,0,-1),1.5f),"already touching bodies may retreat out of contact");
            Check(!TrafficClear(walker,null,walker.Position,new(0,0,1),1.5f),"close contact must not keep pushing forward");
            Check(TrafficClear(walker,crossing,walker.Position,new(0,0,1),1.5f),"approach target keeps intended stop-distance behavior");
            crossing.Position=walker.Position;
            Check(TrafficClear(walker,null,walker.Position,new(-1,0,0),1.5f),"coincident centres have a deterministic escape direction");
            Console.WriteLine($"Crowd regression checks passed: {checks}");
        }
    }
}
